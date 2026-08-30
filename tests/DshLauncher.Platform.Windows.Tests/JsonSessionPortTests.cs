using DshLauncher.Core;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-03,VFY-05")]
public sealed class JsonSessionPortTests : IAsyncLifetime
{
    private static readonly TargetId TargetId = new(
        Guid.Parse("3bb07775-580d-4972-9bbb-a1faaea53cbd"));

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "DshLauncherJsonSessionPortTests",
        Guid.NewGuid().ToString("N"));

    private ApplicationDataStore? _store;
    private JsonSessionPort? _sessions;
    private TargetEndpoint? _endpoint;

    private ApplicationDataStore Store => _store ??
        throw new InvalidOperationException("The test store has not been initialized.");

    private JsonSessionPort Sessions => _sessions ??
        throw new InvalidOperationException("The session port has not been initialized.");

    private TargetEndpoint Endpoint => _endpoint ??
        throw new InvalidOperationException("The target endpoint has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        _store = new ApplicationDataStore(new ApplicationDataLayout(_testRoot));
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        _sessions = new JsonSessionPort(_store);
        _endpoint = await JsonEndpointFactory.CreateAsync(
            "192.168.50.20",
            "3080",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _store?.Dispose();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PairingMarkerTransitionsToCommittedSessionAndDeleteReturnsToMissing()
    {
        var missing = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);
        await Sessions.MarkPairingInProgressAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);
        var pairing = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionMetadataStatus.Missing, missing.Status);
        Assert.Equal(SessionMetadataStatus.PairingInProgress, pairing.Status);

        var committedAt = new DateTimeOffset(2026, 8, 30, 2, 3, 4, TimeSpan.Zero);
        await Sessions.CommitAsync(
            TargetId,
            Endpoint,
            committedAt,
            TestContext.Current.CancellationToken);
        var committed = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);
        var targetRoot = Store.Layout.GetTargetRoot(TargetId.Value);

        Assert.Equal(SessionMetadataStatus.Committed, committed.Status);
        Assert.True(File.Exists(Path.Combine(targetRoot, "session.json")));
        Assert.False(File.Exists(Path.Combine(targetRoot, "pairing-in-progress.json")));

        await Sessions.DeleteAsync(TargetId, TestContext.Current.CancellationToken);
        var deleted = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);
        Assert.Equal(SessionMetadataStatus.Missing, deleted.Status);
    }

    [Fact]
    public async Task PairingStateDeletionDoesNotDependOnDeletingTheUdf()
    {
        await Sessions.MarkPairingInProgressAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);
        var udfPath = Store.Layout.GetUdfPath(TargetId.Value);
        var retainedPath = Path.Combine(udfPath, "retained-browser-state");
        await File.WriteAllTextAsync(
            retainedPath,
            "uncommitted",
            TestContext.Current.CancellationToken);

        await Sessions.DeletePairingStateAsync(
            TargetId,
            TestContext.Current.CancellationToken);
        var result = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionMetadataStatus.Missing, result.Status);
        Assert.True(File.Exists(retainedPath));
        Assert.False(File.Exists(Path.Combine(
            Store.Layout.GetTargetRoot(TargetId.Value),
            "pairing-in-progress.json")));
    }

    [Fact]
    public async Task CommittedSessionBoundToAnotherAuthorityIsOriginMismatch()
    {
        await Sessions.CommitAsync(
            TargetId,
            Endpoint,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var otherEndpoint = await JsonEndpointFactory.CreateAsync(
            "192.168.50.20",
            "3180",
            TestContext.Current.CancellationToken);

        var result = await Sessions.ReadAsync(
            TargetId,
            otherEndpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionMetadataStatus.OriginMismatch, result.Status);
    }

    [Fact]
    public async Task UnknownSessionSchemaIsClassifiedWithoutAcceptingItsContents()
    {
        await Store.PrepareTargetDataAsync(
            TargetId.Value,
            TestContext.Current.CancellationToken);
        await WriteSessionDocumentAsync(
            "session.json",
            $$"""
              {
                "schemaVersion": 99,
                "targetId": "{{TargetId.Value:D}}",
                "origin": "{{Endpoint.Origin}}",
                "state": "committed",
                "committedAtUtc": "2026-08-30T02:03:04+00:00"
              }
              """);

        var result = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionMetadataStatus.UnknownSchema, result.Status);
    }

    [Fact]
    public async Task UnknownPairingMarkerTakesPrecedenceOverACommittedSession()
    {
        await Sessions.CommitAsync(
            TargetId,
            Endpoint,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        await WriteSessionDocumentAsync(
            "pairing-in-progress.json",
            $$"""
              {
                "schemaVersion": 2,
                "targetId": "{{TargetId.Value:D}}",
                "origin": "{{Endpoint.Origin}}",
                "state": "pairing",
                "committedAtUtc": null
              }
              """);

        var result = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionMetadataStatus.UnknownSchema, result.Status);
    }

    [Fact]
    public async Task DuplicateSessionPropertiesAreCorrupt()
    {
        await Store.PrepareTargetDataAsync(
            TargetId.Value,
            TestContext.Current.CancellationToken);
        await WriteSessionDocumentAsync(
            "session.json",
            $$"""
              {
                "schemaVersion": 1,
                "schemaVersion": 1,
                "targetId": "{{TargetId.Value:D}}",
                "origin": "{{Endpoint.Origin}}",
                "state": "committed",
                "committedAtUtc": "2026-08-30T02:03:04+00:00"
              }
              """);

        var result = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionMetadataStatus.Corrupt, result.Status);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":1,\"targetId\":\"00000000-0000-0000-0000-000000000000\",\"origin\":\"http://192.168.50.20:3080\",\"state\":\"committed\",\"committedAtUtc\":null}")]
    public async Task MalformedOrInternallyInvalidSessionDocumentIsCorrupt(string json)
    {
        await Store.PrepareTargetDataAsync(
            TargetId.Value,
            TestContext.Current.CancellationToken);
        await WriteSessionDocumentAsync("session.json", json);

        var result = await Sessions.ReadAsync(
            TargetId,
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            result.Status,
            new[] { SessionMetadataStatus.Corrupt, SessionMetadataStatus.OriginMismatch });
    }

    private Task WriteSessionDocumentAsync(string fileName, string json) =>
        File.WriteAllTextAsync(
            Path.Combine(Store.Layout.GetTargetRoot(TargetId.Value), fileName),
            json,
            TestContext.Current.CancellationToken);
}

internal static class JsonEndpointFactory
{
    public static async ValueTask<TargetEndpoint> CreateAsync(
        string ipv4,
        string port,
        CancellationToken cancellationToken)
    {
        var support = new EndpointSupport();
        using var manager = new TargetManager(new TargetManagerPorts(
            new EmptyTargetStorage(),
            support,
            support,
            support,
            support,
            support,
            support,
            support));
        var result = await manager.ExecuteAsync(
            new InspectCandidate(ipv4, port),
            cancellationToken);
        return result.Outcome is CandidateInspected inspected
            ? inspected.Candidate.Endpoint
            : throw new InvalidOperationException("The endpoint fixture could not be created.");
    }

    private sealed class EmptyTargetStorage : ITargetStorage
    {
        public ValueTask<TargetCatalogReadResult> ReadCatalogAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TargetCatalogReadResult(CatalogReadStatus.Missing));

        public ValueTask WriteCatalogAsync(
            TargetCatalogDocument document,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<IReadOnlyCollection<ForgetTombstone>> ReadForgetTombstonesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyCollection<ForgetTombstone>>(
                Array.Empty<ForgetTombstone>());

        public ValueTask WriteForgetTombstoneAsync(
            ForgetTombstone tombstone,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask ClearForgetTombstoneAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class EndpointSupport :
        IClock,
        IIdGenerator,
        INetworkPort,
        ITargetProbePort,
        ISessionPort,
        ITargetRuntimePort,
        IClipboardPort
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public Guid NewId() => Guid.NewGuid();

        public ValueTask<NetworkCategory> GetCurrentCategoryAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(NetworkCategory.Private);

        public ValueTask<TargetProbeResult> ProbeAsync(
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new TargetProbeResult(TargetProbeClassification.SupportedAuthenticatedHarness));

        public ValueTask<SessionMetadataReadResult> ReadAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new SessionMetadataReadResult(SessionMetadataStatus.Missing));

        public ValueTask MarkPairingInProgressAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask CommitAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DeleteAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DeletePairingStateAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask ResetUncommittedAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<PairingHandshake> PairAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            ReadOnlyMemory<char> encodedToken,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<RuntimeOpenStatus> PrepareOpenAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask CloseAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DeleteSessionDataAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<bool> SessionDataExistsAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask ClearIfUnchangedAsync(
            ReadOnlyMemory<char> pastedText,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
