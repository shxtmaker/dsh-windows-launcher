using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.WebUi;
using Xunit;

namespace DshLauncher.WebUi.Tests;

[Trait("triggerTags", "VFY-07")]
public sealed class HubApiIntegrationTests : IAsyncDisposable
{
    private readonly FakePairingTransport _transport = new();
    private readonly InMemoryTargetStore _store = new();
    private readonly PairingHub _hub;
    private readonly HubWebServer _server;
    private readonly HttpClient _client;

    public HubApiIntegrationTests()
    {
        _hub = new PairingHub(
            new PairingHubOptions { DelayFactory = (_, cancellationToken) => Task.Delay(5, cancellationToken) },
            _store,
            _transport,
            new FixedClock(),
            new SequentialIdGenerator());
        _server = new HubWebServer(_hub, new HubWebServerOptions { Port = 0 });
        _server.StartAsync().AsTask().GetAwaiter().GetResult();
        _hub.StartAsync().AsTask().GetAwaiter().GetResult();
        _client = new HttpClient { BaseAddress = new Uri(_server.DashboardUrl) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _hub.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task DashboardIsServedWithTheManagementTitle()
    {
        var response = await _client.GetAsync("/", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("DSH 集中配对管理", html);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task AddingATargetThroughTheApiPairsItAndShowsUpInTheSnapshot()
    {
        _transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired,
            new DeviceCredential("dsh_pair", "device-1"),
            200));

        var response = await _client.PostAsJsonAsync(
            "/api/targets",
            new { pairingLink = "http://192.168.10.8:3080/pair-accept?pair=secret", displayName = "工作台" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var snapshot = await GetSnapshotAsync();
        var target = Assert.Single(snapshot.GetProperty("targets").EnumerateArray());
        Assert.Equal("Paired", target.GetProperty("pairing").GetString());
        Assert.Equal("工作台", target.GetProperty("displayName").GetString());
        Assert.True(target.GetProperty("hasCredential").GetBoolean());
    }

    [Fact]
    public async Task InvalidPairingLinksReturnAMachineReadableBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/targets",
            new { pairingLink = "http://192.168.10.8:3080/", displayName = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("InvalidPairingLink", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task HostSideRevocationSurfacesAsARetryableUnpairedConflict()
    {
        _transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired, new DeviceCredential("dsh_pair", "device-1"), 200));
        var targetId = await AddTargetAsync();
        await _hub.SetKeepAliveAsync(targetId, false, TestContext.Current.CancellationToken);
        _transport.HeartbeatOutcomes.Enqueue(HeartbeatOutcome.Unpaired(401));

        var response = await _client.PostAsync(
            $"/api/targets/{targetId}/heartbeat", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("TargetNotPaired", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RemoteUiUrlsAreOnlyIssuedForPairedTargets()
    {
        var unpairedId = await AddEndpointOnlyTargetAsync();

        var denied = await _client.GetAsync(
            $"/api/targets/{unpairedId}/remote-url", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);

        _transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired, new DeviceCredential("dsh_pair", "device-1"), 200));
        var pairedId = await AddTargetAsync();
        var allowed = await _client.GetAsync(
            $"/api/targets/{pairedId}/remote-url", TestContext.Current.CancellationToken);

        allowed.EnsureSuccessStatusCode();
        var body = await allowed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("/pair-app?device=", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task RenameKeepAliveAndRemoveWorkOverTheApi()
    {
        var targetId = await AddTargetAsync();

        var renamed = await _client.PostAsJsonAsync(
            $"/api/targets/{targetId}/rename",
            new { displayName = "新名字" },
            TestContext.Current.CancellationToken);
        renamed.EnsureSuccessStatusCode();

        var paused = await _client.PostAsJsonAsync(
            $"/api/targets/{targetId}/keep-alive",
            new { enabled = false },
            TestContext.Current.CancellationToken);
        paused.EnsureSuccessStatusCode();

        var removed = await _client.DeleteAsync($"/api/targets/{targetId}", TestContext.Current.CancellationToken);
        removed.EnsureSuccessStatusCode();

        var snapshot = await GetSnapshotAsync();
        Assert.Empty(snapshot.GetProperty("targets").EnumerateArray());
    }

    [Fact]
    public async Task UnknownTargetsAreNotFound()
    {
        var response = await _client.DeleteAsync(
            $"/api/targets/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EventsStreamPushesTheInitialSnapshot()
    {
        using var stream = await _client.GetAsync(
            "/api/events",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        stream.EnsureSuccessStatusCode();

        await using var content = await stream.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[16 * 1024];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var text = new StringBuilder();
        while (DateTime.UtcNow < deadline && !text.ToString().Contains("data:", StringComparison.Ordinal))
        {
            var read = await content.ReadAsync(buffer, TestContext.Current.CancellationToken);
            if (read > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            else
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        Assert.Contains("\"targets\":", text.ToString());
        Assert.Contains("\"heartbeatInterval\":", text.ToString());
    }

    private async Task<Guid> AddTargetAsync()
    {
        _transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired,
            new DeviceCredential("dsh_pair", "device-" + _transport.AcceptCalls.Count),
            200));
        var response = await _client.PostAsJsonAsync(
            "/api/targets",
            new { pairingLink = "http://192.168.10.8:3080/pair-accept?pair=secret", displayName = (string?)null },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        return Guid.Parse(body.GetProperty("targetId").GetString()!);
    }

    private async Task<Guid> AddEndpointOnlyTargetAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/targets",
            new { pairingLink = (string?)null, baseUrl = "http://192.168.10.8:3080", displayName = (string?)null },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        return Guid.Parse(body.GetProperty("targetId").GetString()!);
    }

    private async Task<JsonElement> GetSnapshotAsync()
    {
        var response = await _client.GetAsync("/api/hub", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private int _next;

        public Guid NewId() => new(20000000 + _next++, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class InMemoryTargetStore : ITargetStore
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

    private sealed class FakePairingTransport : IPairingTransport
    {
        public Queue<PairingAcceptOutcome> AcceptOutcomes { get; } = new();

        public Queue<HeartbeatOutcome> HeartbeatOutcomes { get; } = new();

        public List<string> AcceptCalls { get; } = [];

        public ValueTask<PairingAcceptOutcome> AcceptPairingAsync(
            HarnessEndpoint endpoint,
            string token,
            CancellationToken cancellationToken = default)
        {
            AcceptCalls.Add(token);
            return ValueTask.FromResult(AcceptOutcomes.Count > 0
                ? AcceptOutcomes.Dequeue()
                : new PairingAcceptOutcome(PairingAcceptStatus.Failed, null, null));
        }

        public ValueTask<HeartbeatOutcome> SendHeartbeatAsync(
            HarnessEndpoint endpoint,
            DeviceCredential credential,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(HeartbeatOutcomes.Count > 0
                ? HeartbeatOutcomes.Dequeue()
                : HeartbeatOutcome.Alive());

        public ValueTask<RemoteStatusProbeOutcome> ProbeStatusAsync(
            HarnessEndpoint endpoint,
            DeviceCredential? credential,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RemoteStatusProbeOutcome(
                RemoteStatusProbeKind.Reached, false, "idle", true, 200));
    }
}
