using System.Net;
using DshLauncher.Core.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DshLauncher.Acceptance.Tests.EndToEnd;

[Trait("triggerTags", "VFY-02,VFY-04")]
public sealed class PublicPairingTransportTests
{
    [Fact]
    public async Task PairingCredentialsStayIsolatedWhenTargetsShareAHost()
    {
        await using var first = await StartHostAsync(async context =>
        {
            context.Response.Headers.SetCookie = "dsh_pair=first-device; Path=/; HttpOnly; SameSite=Lax";
            await context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "first-device" });
        });
        await using var second = await StartHostAsync(async context =>
        {
            Assert.Equal("dsh_pair=second-device", context.Request.Headers.Cookie.ToString());
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });
        using var transport = new HttpPairingTransport(new PairingTransportOptions());
        var accepted = await transport.AcceptPairingAsync(Endpoint(first), "first-token", TestContext.Current.CancellationToken);
        Assert.Equal(PairingAcceptStatus.Paired, accepted.Status);

        var heartbeat = await transport.SendHeartbeatAsync(
            Endpoint(second), new DeviceCredential("dsh_pair", "second-device"), TestContext.Current.CancellationToken);

        Assert.Equal(HeartbeatStatus.Alive, heartbeat.Status);
    }

    [Fact]
    public async Task AcceptUsesTheCookieMatchingTheReturnedDeviceBehindAProxy()
    {
        await using var host = await StartHostAsync(async context =>
        {
            if (context.Request.Path == PairingProtocol.AcceptPath)
            {
                context.Response.Headers.SetCookie = new Microsoft.Extensions.Primitives.StringValues(
                    ["dsh_pair=stale-device; Path=/", "custom_pair=current-device; Path=/; HttpOnly; Secure"]);
                await context.Response.WriteAsJsonAsync(new { ok = true, deviceId = "current-device" });
                return;
            }

            Assert.Equal("custom_pair=current-device", context.Request.Headers.Cookie.ToString());
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });
        using var transport = new HttpPairingTransport(new PairingTransportOptions());
        var accepted = await transport.AcceptPairingAsync(Endpoint(host), "token", TestContext.Current.CancellationToken);
        Assert.Equal(PairingAcceptStatus.Paired, accepted.Status);
        Assert.Equal("custom_pair", accepted.Credential!.CookieName);

        var heartbeat = await transport.SendHeartbeatAsync(Endpoint(host), accepted.Credential, TestContext.Current.CancellationToken);

        Assert.Equal(HeartbeatStatus.Alive, heartbeat.Status);
    }

    [Fact]
    public async Task RelayErrorsAreNotReportedAsAReachableHarness()
    {
        foreach (var status in new[] { 200, 403, 502, 503, 530 })
        {
            await using var host = await StartHostAsync(async context =>
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new { ok = false, code = "instance-offline" });
            });
            using var transport = new HttpPairingTransport(new PairingTransportOptions());

            var probe = await transport.ProbeStatusAsync(Endpoint(host), null, TestContext.Current.CancellationToken);

            Assert.Equal(RemoteStatusProbeKind.Failed, probe.Kind);
            Assert.Null(probe.Paired);
            Assert.Equal(status, probe.StatusCode);
        }
    }

    [Fact]
    public async Task AnOfflineRelayDoesNotRevokePairingAndRecoversWithTheSameCredential()
    {
        var offline = true;
        await using var host = await StartHostAsync(async context =>
        {
            Assert.Equal("dsh_pair=persistent-device", context.Request.Headers.Cookie.ToString());
            if (offline)
            {
                context.Response.StatusCode = 503;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<html><body>Instance offline</body></html>");
                return;
            }

            await context.Response.WriteAsJsonAsync(new { ok = true });
        });
        using var transport = new HttpPairingTransport(new PairingTransportOptions());
        var credential = new DeviceCredential("dsh_pair", "persistent-device");
        var failed = await transport.SendHeartbeatAsync(Endpoint(host), credential, TestContext.Current.CancellationToken);
        Assert.Equal(HeartbeatStatus.Failed, failed.Status);
        Assert.Equal(503, failed.StatusCode);

        offline = false;
        var recovered = await transport.SendHeartbeatAsync(Endpoint(host), credential, TestContext.Current.CancellationToken);

        Assert.Equal(HeartbeatStatus.Alive, recovered.Status);
    }

    private static HarnessEndpoint Endpoint(WebApplication app) => new(new Uri(app.Urls.Single()));

    private static async Task<WebApplication> StartHostAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
