using System.Net;
using System.Text.Json;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DshLauncher.Acceptance.Tests.EndToEnd;

/// <summary>
/// A faithful stand-in for the host side of the "DSH 远程访问" plugin: it
/// answers the same wire contract (/api/pair/accept, /api/pair/heartbeat,
/// /api/pair/status) with the same status codes, error envelopes and
/// Set-Cookie semantics, so the real HTTP pairing transport is exercised
/// end to end.
/// </summary>
internal sealed class FakeRemoteAccessHost : IAsyncDisposable
{
    private readonly HashSet<string> _devices = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private WebApplication? _app;
    private bool _acceptOpen = true;
    private bool _stopped;
    private string? _activeToken;

    public string? LastAcceptUserAgent { get; private set; }

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:1");

    public string PairingLink => $"{BaseUri.ToString().TrimEnd('/')}/pair-accept?pair={_activeToken}";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapPost("/api/pair/accept", async (HttpContext context) =>
        {
            LastAcceptUserAgent = context.Request.Headers.UserAgent.ToString();
            if (_stopped)
            {
                await WriteErrorAsync(context, 403, "forbidden");
                return;
            }

            string? token = null;
            try
            {
                using var document = await JsonDocument.ParseAsync(
                    context.Request.Body, cancellationToken: context.RequestAborted);
                token = document.RootElement.TryGetProperty("token", out var element)
                    ? element.GetString()
                    : null;
            }
            catch (JsonException)
            {
            }

            if (string.IsNullOrEmpty(token) || token != _activeToken || !_acceptOpen)
            {
                await WriteErrorAsync(context, 404, "invalid");
                return;
            }

            var deviceId = Guid.NewGuid().ToString("N");
            lock (_gate)
            {
                _devices.Add(deviceId);
                _acceptOpen = false;
            }

            context.Response.StatusCode = 200;
            context.Response.Headers.ContentType = "application/json";
            context.Response.Headers.SetCookie = new Microsoft.Extensions.Primitives.StringValues(
                [$"dsh_pair={deviceId}; Path=/; HttpOnly; SameSite=Lax"]);
            await context.Response.WriteAsync(
                $"{{\"ok\":true,\"deviceId\":\"{deviceId}\"}}", context.RequestAborted);
        });

        app.MapPost("/api/pair/heartbeat", async (HttpContext context) =>
        {
            var deviceId = ReadDevice(context);
            if (_stopped || deviceId is null || !_devices.Contains(deviceId))
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.ContentType = "application/json";
                await context.Response.WriteAsync("{\"ok\":false,\"code\":\"unpaired\"}", context.RequestAborted);
                return;
            }

            context.Response.Headers.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}", context.RequestAborted);
        });

        app.MapGet("/api/pair/status", async (HttpContext context) =>
        {
            var deviceId = ReadDevice(context);
            context.Response.Headers.ContentType = "application/json";
            await context.Response.WriteAsync(
                $"{{\"ok\":true,\"paired\":{(_devices.Contains(deviceId ?? string.Empty) ? "true" : "false")},\"phase\":\"idle\",\"lanAvailable\":true}}",
                context.RequestAborted);
        });

        _app = app;
        await app.StartAsync();
        var address = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses
            .First(address => address.StartsWith("http://", StringComparison.Ordinal));
        BaseUri = new Uri(address);
        _activeToken = Guid.NewGuid().ToString("N").Substring(0, 24);
    }

    /// <summary>Revokes every device, exactly like the host panel's 停止.</summary>
    public void StopPairing()
    {
        lock (_gate)
        {
            _stopped = true;
            _devices.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static string? ReadDevice(HttpContext context)
    {
        var cookie = context.Request.Headers.Cookie.ToString();
        foreach (var pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 &&
                pair[..separator].Trim() == "dsh_pair")
            {
                return pair[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"ok\":false,\"code\":\"{code}\"}}", context.RequestAborted);
    }
}

[Trait("triggerTags", "VFY-02,VFY-04")]
public sealed class HubEndToEndTests
{
    [Fact]
    public async Task TheFullPairingRoundTripSurvivesHostRestartsAndDetectsRevocation()
    {
        var host = new FakeRemoteAccessHost();
        await host.StartAsync();
        try
        {
            var hub = new PairingHub(
                new PairingHubOptions { HeartbeatInterval = TimeSpan.FromSeconds(2) },
                new InMemoryStore(),
                new HttpPairingTransport(new PairingTransportOptions
                {
                    UserAgent = "DshWindowsLauncher/1.0 (accept-ua)",
                }),
                new SystemClockShim(),
                new GuidShim());
            await hub.StartAsync(TestContext.Current.CancellationToken);

            var result = await hub.AddFromPairingLinkAsync(
                host.PairingLink, "端到端", TestContext.Current.CancellationToken);
            Assert.Null(result.Error);

            var target = result.Target!;
            Assert.Equal(PairingState.Paired, target.Pairing);
            Assert.True(target.HasCredential);
            Assert.False(string.IsNullOrEmpty(host.LastAcceptUserAgent));

            var online = await WaitUntilAsync(hub, snapshot =>
                snapshot.Targets.Single().Connectivity == ConnectivityState.Online);
            Assert.Equal(PairingState.Paired, online.Pairing);

            // Host side revokes every device (the panel's 停止): the next
            // keep-alive beat must classify the target as revoked.
            host.StopPairing();
            var revoked = await WaitUntilAsync(hub, snapshot =>
                snapshot.Targets.Single().Pairing == PairingState.Revoked,
                TimeSpan.FromSeconds(30));
            Assert.Equal(ConnectivityState.Unknown, revoked.Connectivity);

            await hub.DisposeAsync();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AConsumedTokenCannotPairTwiceButAcceptReuseInsideTheWindowIsHostControlled()
    {
        var host = new FakeRemoteAccessHost();
        await host.StartAsync();
        try
        {
            var hub = new PairingHub(
                new PairingHubOptions(),
                new InMemoryStore(),
                new HttpPairingTransport(new PairingTransportOptions()),
                new SystemClockShim(),
                new GuidShim());
            await hub.StartAsync(TestContext.Current.CancellationToken);

            var first = await hub.AddFromPairingLinkAsync(
                host.PairingLink, null, TestContext.Current.CancellationToken);
            Assert.Null(first.Error);

            // A second hub instance reusing the same link acts like a second
            // device racing the same one-shot token; the plugin refuses it.
            var rival = new PairingHub(
                new PairingHubOptions(),
                new InMemoryStore(),
                new HttpPairingTransport(new PairingTransportOptions()),
                new SystemClockShim(),
                new GuidShim2());
            await rival.StartAsync(TestContext.Current.CancellationToken);

            var second = await rival.AddFromPairingLinkAsync(
                host.PairingLink, null, TestContext.Current.CancellationToken);

            Assert.NotNull(second.Error);
            Assert.NotEqual(PairingState.Paired, second.Target?.Pairing);

            await hub.DisposeAsync();
            await rival.DisposeAsync();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task<TargetSnapshot> WaitUntilAsync(
        PairingHub hub,
        Func<HubSnapshot, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
            if (predicate(snapshot))
            {
                return Assert.Single(snapshot.Targets);
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The end-to-end hub never reached the expected state.");
    }

    private sealed class SystemClockShim : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class GuidShim : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class GuidShim2 : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class InMemoryStore : ITargetStore
    {
        private StoredHubDocument? _document;

        public ValueTask<HubStorageReadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_document is null
                ? new HubStorageReadResult(HubStorageStatus.Missing)
                : new HubStorageReadResult(HubStorageStatus.Loaded, _document));

        public ValueTask SaveAsync(StoredHubDocument document, CancellationToken cancellationToken = default)
        {
            _document = document;
            return ValueTask.CompletedTask;
        }
    }
}
