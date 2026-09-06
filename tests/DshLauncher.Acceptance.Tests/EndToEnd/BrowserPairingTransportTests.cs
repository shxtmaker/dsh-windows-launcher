using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows.Threading;
using DshLauncher.Core.Pairing;
using DshLauncher.Desktop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DshLauncher.Acceptance.Tests.EndToEnd;

[Trait("triggerTags", "VFY-02,VFY-04")]
public sealed class BrowserPairingTransportTests
{
    [Fact]
    public Task BrowserPairingPreservesPostAndIsolatesCookiesAcrossPorts() => OnDispatcherAsync(async dispatcher =>
    {
        await using var first = await StartHostAsync(async context =>
        {
            if (context.Request.Path == PairingProtocol.AcceptPath)
            {
                Assert.Equal("POST", context.Request.Method);
                using var body = await JsonDocument.ParseAsync(context.Request.Body);
                Assert.Equal("test-token", body.RootElement.GetProperty("token").GetString());
                Assert.Equal("", context.Request.Headers.Cookie.ToString());
                context.Response.Headers.SetCookie = "custom_pair=first-device; Path=/; HttpOnly";
                await context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "first-device" });
            }
            else if (context.Request.Path == PairingProtocol.StatusPath)
            {
                Assert.Equal("", context.Request.Headers.Cookie.ToString());
                await context.Response.WriteAsJsonAsync(new { ok = true, paired = false });
            }
            else
            {
                Assert.Equal("custom_pair=first-device", context.Request.Headers.Cookie.ToString());
                await context.Response.WriteAsJsonAsync(new { ok = true });
            }
        });
        await using var second = await StartHostAsync(async context =>
        {
            Assert.Equal("", context.Request.Headers.Cookie.ToString());
            await context.Response.WriteAsJsonAsync(new { ok = true, paired = false });
        });
        using var transport = CreateTransport(dispatcher);
        var paired = await transport.AcceptPairingAsync(Endpoint(first), "test-token", TestContext.Current.CancellationToken);
        Assert.Equal(PairingAcceptStatus.Paired, paired.Status);
        Assert.Equal("custom_pair", paired.Credential!.CookieName);
        Assert.Equal(RemoteStatusProbeKind.Reached,
            (await transport.ProbeStatusAsync(Endpoint(first), null, TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(RemoteStatusProbeKind.Reached,
            (await transport.ProbeStatusAsync(Endpoint(second), null, TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(HeartbeatStatus.Alive,
            (await transport.SendHeartbeatAsync(Endpoint(first), paired.Credential, TestContext.Current.CancellationToken)).Status);
    });

    [Fact]
    public Task BrowserSameHostRedirectPreservesPairingMethodAndBody() => OnDispatcherAsync(async dispatcher =>
    {
        var accepted = 0;
        await using var host = await StartHostAsync(async context =>
        {
            if (context.Request.Path == PairingProtocol.AcceptPath)
            {
                context.Response.Redirect("/accept-final", permanent: false, preserveMethod: true);
                return;
            }
            Assert.Equal("POST", context.Request.Method);
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.Equal("redirect-token", body.RootElement.GetProperty("token").GetString());
            Interlocked.Increment(ref accepted);
            await context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "redirect-device" });
        });
        using var transport = CreateTransport(dispatcher);
        Assert.Equal(PairingAcceptStatus.Paired,
            (await transport.AcceptPairingAsync(Endpoint(host), "redirect-token", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, accepted);
    });

    [Fact]
    public Task BrowserRejectsCrossHostRedirectBeforeSendingCredentials() => OnDispatcherAsync(async dispatcher =>
    {
        var leaked = 0;
        await using var destination = await StartHostAsync(context =>
        {
            Interlocked.Increment(ref leaked);
            return context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "unexpected" });
        });
        var otherHost = destination.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        await using var source = await StartHostAsync(context =>
        {
            context.Response.Redirect(otherHost + "/accept", permanent: false, preserveMethod: true);
            return Task.CompletedTask;
        });
        using var transport = CreateTransport(dispatcher);
        Assert.Equal(PairingAcceptStatus.Failed,
            (await transport.AcceptPairingAsync(Endpoint(source), "private-token", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, leaked);
    });

    [Fact]
    public Task BrowserCancellationDoesNotPoisonTheNextRequest() => OnDispatcherAsync(async dispatcher =>
    {
        var hang = true;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartHostAsync(async context =>
        {
            if (hang)
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new { ok = true, paired = false });
            }
        });
        using var transport = CreateTransport(dispatcher);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = transport.ProbeStatusAsync(Endpoint(host), null, cancellation.Token).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        hang = false;
        Assert.Equal(RemoteStatusProbeKind.Reached,
            (await transport.ProbeStatusAsync(Endpoint(host), null, TestContext.Current.CancellationToken)).Kind);
    });

    [Fact]
    public Task BrowserRejectsAnUntrustedCertificateBeforeSendingTheToken() => OnDispatcherAsync(async dispatcher =>
    {
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        builder.Logging.ClearProviders();
        await using var host = builder.Build();
        var requests = 0;
        host.Run(context =>
        {
            Interlocked.Increment(ref requests);
            return context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "unexpected" });
        });
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var transport = CreateTransport(dispatcher);
        Assert.Equal(PairingAcceptStatus.Unreachable,
            (await transport.AcceptPairingAsync(Endpoint(host), "private-token", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, requests);
    });

    [Fact]
    public Task BrowserSlowTargetDoesNotBlockAnotherTarget() => OnDispatcherAsync(async dispatcher =>
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await StartHostAsync(async context =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await using var fast = await StartHostAsync(context => context.Response.WriteAsJsonAsync(new { ok = true, paired = false }));
        using var transport = CreateTransport(dispatcher);
        // Measure isolation between requests, not a cold browser-profile startup
        // while other acceptance tests are also launching processes.
        Assert.Equal(RemoteStatusProbeKind.Reached,
            (await transport.ProbeStatusAsync(Endpoint(fast), null, TestContext.Current.CancellationToken)).Kind);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = transport.ProbeStatusAsync(Endpoint(slow), null, cancellation.Token).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var result = await transport.ProbeStatusAsync(Endpoint(fast), null, TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RemoteStatusProbeKind.Reached, result.Kind);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    });
    [Fact]
    public Task BrowserRequestsSatisfyThePluginOriginFence() => OnDispatcherAsync(async dispatcher =>
    {
        await using var host = await StartHostAsync(async context =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
                (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                 originUri.Authority != context.Request.Host.Value)))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { ok = false, code = "forbidden" });
                return;
            }
            await context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "fence-device" });
        });
        using var transport = CreateTransport(dispatcher);
        var paired = await transport.AcceptPairingAsync(Endpoint(host), "test-token", TestContext.Current.CancellationToken);
        Assert.Equal(PairingAcceptStatus.Paired, paired.Status);
        Assert.Equal(HeartbeatStatus.Alive,
            (await transport.SendHeartbeatAsync(Endpoint(host), paired.Credential!, TestContext.Current.CancellationToken)).Status);
    });
    private static HttpPairingTransport CreateTransport(Dispatcher dispatcher) =>
        new(new PairingTransportOptions(), new WebViewHttpMessageHandler(dispatcher));

    private static HarnessEndpoint Endpoint(WebApplication host) => new(new Uri(host.Urls.Single()));

    private static async Task<WebApplication> StartHostAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        var host = builder.Build();
        host.Run(handler);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static Task OnDispatcherAsync(Func<Dispatcher, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(dispatcher); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
