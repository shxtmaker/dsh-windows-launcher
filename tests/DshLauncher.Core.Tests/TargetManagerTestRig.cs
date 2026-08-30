using System.Collections.Concurrent;
using DshLauncher.Core;
using Xunit;

namespace DshLauncher.Core.Tests;

internal sealed class TargetManagerTestRig : IDisposable
{
    public TargetManagerTestRig(
        TargetCatalogReadResult? initialCatalog = null,
        IPairingDiagnosticSink? pairingDiagnostics = null)
    {
        Events = new BoundaryScript();
        Storage = new MemoryTargetStorage(Events, initialCatalog);
        Clock = new TestClock();
        Ids = new TestIdGenerator();
        Network = new ScriptedNetworkPort(Events);
        Probe = new ScriptedProbePort(Events);
        Sessions = new MemorySessionPort(Events);
        Runtime = new ScriptedRuntimePort(Events);
        Clipboard = new ScriptedClipboardPort(Events);
        Manager = new TargetManager(
            new TargetManagerPorts(
                Storage,
                Clock,
                Ids,
                Network,
                Probe,
                Sessions,
                Runtime,
                Clipboard,
                pairingDiagnostics));
    }

    public TargetManager Manager { get; }

    public BoundaryScript Events { get; }

    public MemoryTargetStorage Storage { get; }

    public TestClock Clock { get; }

    public TestIdGenerator Ids { get; }

    public ScriptedNetworkPort Network { get; }

    public ScriptedProbePort Probe { get; }

    public MemorySessionPort Sessions { get; }

    public ScriptedRuntimePort Runtime { get; }

    public ScriptedClipboardPort Clipboard { get; }

    public static TrustConfirmation CompleteTrust { get; } = new(1, true, true, true);

    public async ValueTask<TargetCommandResult> AddTargetAsync(
        string ipv4 = "192.168.10.8",
        string? port = null,
        string? displayName = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inspected = await Manager.ExecuteAsync(
            new InspectCandidate(ipv4, port),
            cancellationToken);
        var candidate = AssertOutcome<CandidateInspected>(inspected).Candidate;
        return await Manager.ExecuteAsync(
            new ConfirmCandidate(candidate.CandidateId, displayName, CompleteTrust),
            cancellationToken);
    }

    public static TOutcome AssertOutcome<TOutcome>(TargetCommandResult result)
        where TOutcome : TargetCommandOutcome
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Expected success but received {result.Error?.Code}.");
        }

        return result.Outcome as TOutcome ??
            throw new InvalidOperationException($"Expected outcome {typeof(TOutcome).Name}.");
    }

    public void Dispose() => Manager.Dispose();
}

internal sealed class BoundaryScript
{
    private readonly object _gate = new();
    private readonly Queue<string> _expected = new();

    public bool Enabled { get; private set; }

    public void Expect(params string[] steps)
    {
        lock (_gate)
        {
            _expected.Clear();
            foreach (var step in steps)
            {
                _expected.Enqueue(step);
            }

            Enabled = true;
        }
    }

    public void Observe(string step)
    {
        lock (_gate)
        {
            if (!Enabled)
            {
                return;
            }

            if (_expected.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected boundary step: {step}.");
            }

            var expected = _expected.Dequeue();
            if (!string.Equals(expected, step, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected boundary step {expected}, received {step}.");
            }
        }
    }

    public void AssertComplete()
    {
        lock (_gate)
        {
            if (_expected.Count != 0)
            {
                throw new InvalidOperationException($"Boundary script has {_expected.Count} unobserved steps.");
            }
        }
    }
}

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
}

internal sealed class TestIdGenerator : IIdGenerator
{
    private readonly ConcurrentQueue<Guid> _ids = new();

    public void Enqueue(params Guid[] ids)
    {
        foreach (var id in ids)
        {
            _ids.Enqueue(id);
        }
    }

    public Guid NewId() => _ids.TryDequeue(out var id) ? id : Guid.NewGuid();
}

internal sealed class MemoryTargetStorage : ITargetStorage
{
    private readonly BoundaryScript _events;
    private readonly ConcurrentDictionary<TargetId, ForgetTombstone> _tombstones = new();
    private TargetCatalogReadResult _catalog;

    public MemoryTargetStorage(BoundaryScript events, TargetCatalogReadResult? catalog)
    {
        _events = events;
        _catalog = catalog ?? new TargetCatalogReadResult(CatalogReadStatus.Missing);
    }

    public int CatalogWriteCount { get; private set; }

    public void SeedTombstone(ForgetTombstone tombstone) =>
        _tombstones[tombstone.TargetId] = tombstone;

    public ValueTask<TargetCatalogReadResult> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("catalog-read");
        return ValueTask.FromResult(_catalog);
    }

    public ValueTask WriteCatalogAsync(
        TargetCatalogDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("catalog-write");
        CatalogWriteCount++;
        var copy = document with { Targets = document.Targets.ToArray() };
        _catalog = new TargetCatalogReadResult(CatalogReadStatus.Loaded, copy);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<ForgetTombstone>> ReadForgetTombstonesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("tombstones-read");
        IReadOnlyCollection<ForgetTombstone> result = _tombstones.Values.ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask WriteForgetTombstoneAsync(
        ForgetTombstone tombstone,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("tombstone-write");
        _tombstones[tombstone.TargetId] = tombstone;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearForgetTombstoneAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("tombstone-clear");
        _tombstones.TryRemove(targetId, out _);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScriptedNetworkPort : INetworkPort
{
    private readonly BoundaryScript _events;

    public ScriptedNetworkPort(BoundaryScript events)
    {
        _events = events;
    }

    public NetworkCategory Category { get; set; } = NetworkCategory.Private;

    public int InvocationCount { get; private set; }

    public ValueTask<NetworkCategory> GetCurrentCategoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("network");
        InvocationCount++;
        return ValueTask.FromResult(Category);
    }
}

internal sealed class ScriptedProbePort : ITargetProbePort
{
    private readonly BoundaryScript _events;

    public ScriptedProbePort(BoundaryScript events)
    {
        _events = events;
    }

    public TargetProbeClassification Classification { get; set; } =
        TargetProbeClassification.SupportedAuthenticatedHarness;

    public int InvocationCount { get; private set; }

    public Func<TargetEndpoint, CancellationToken, ValueTask<TargetProbeResult>>? Handler { get; set; }

    public ValueTask<TargetProbeResult> ProbeAsync(
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("probe");
        InvocationCount++;
        return Handler is null
            ? ValueTask.FromResult(new TargetProbeResult(Classification))
            : Handler(endpoint, cancellationToken);
    }
}

internal sealed class MemorySessionPort : ISessionPort
{
    private readonly BoundaryScript _events;
    private readonly ConcurrentDictionary<TargetId, SessionMetadataStatus> _statuses = new();

    public MemorySessionPort(BoundaryScript events)
    {
        _events = events;
    }

    public SessionMetadataStatus GetStatus(TargetId targetId) =>
        _statuses.GetValueOrDefault(targetId, SessionMetadataStatus.Missing);

    public Func<TargetId, CancellationToken, ValueTask>? DeleteHandler { get; set; }

    public int PairingStateDeleteCount { get; private set; }

    public void SetStatus(TargetId targetId, SessionMetadataStatus status) =>
        _statuses[targetId] = status;

    public ValueTask<SessionMetadataReadResult> ReadAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("session-read");
        return ValueTask.FromResult(new SessionMetadataReadResult(GetStatus(targetId)));
    }

    public ValueTask MarkPairingInProgressAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("session-marker");
        _statuses[targetId] = SessionMetadataStatus.PairingInProgress;
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("session-commit");
        _statuses[targetId] = SessionMetadataStatus.Committed;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeleteAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("session-delete");
        if (DeleteHandler is not null)
        {
            await DeleteHandler(targetId, cancellationToken);
        }

        _statuses[targetId] = SessionMetadataStatus.Missing;
    }

    public ValueTask DeletePairingStateAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("pairing-state-delete");
        PairingStateDeleteCount++;
        _statuses[targetId] = SessionMetadataStatus.Missing;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScriptedRuntimePort : ITargetRuntimePort
{
    private readonly BoundaryScript _events;
    private readonly ConcurrentDictionary<TargetId, bool> _sessionData = new();

    public ScriptedRuntimePort(BoundaryScript events)
    {
        _events = events;
    }

    public PairingHandshake Handshake { get; set; } = new(303, true, true);

    public RuntimeOpenStatus OpenStatus { get; set; } = RuntimeOpenStatus.Ready;

    public int PairInvocationCount { get; private set; }

    public int ResetInvocationCount { get; private set; }

    public int DeleteInvocationCount { get; private set; }

    public Func<TargetId, ReadOnlyMemory<char>, CancellationToken, ValueTask<PairingHandshake>>?
        PairHandler
    { get; set; }

    public Func<TargetId, CancellationToken, ValueTask>? ResetHandler { get; set; }

    public Func<TargetId, CancellationToken, ValueTask>? DeleteHandler { get; set; }

    public Func<TargetId, CancellationToken, ValueTask<RuntimeOpenStatus>>? OpenHandler { get; set; }

    public async ValueTask ResetUncommittedAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("runtime-reset");
        ResetInvocationCount++;
        if (ResetHandler is not null)
        {
            await ResetHandler(targetId, cancellationToken);
        }

        _sessionData[targetId] = false;
    }

    public ValueTask<PairingHandshake> PairAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        ReadOnlyMemory<char> encodedToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("runtime-pair");
        PairInvocationCount++;
        _sessionData[targetId] = true;
        return PairHandler is null
            ? ValueTask.FromResult(Handshake)
            : PairHandler(targetId, encodedToken, cancellationToken);
    }

    public ValueTask<RuntimeOpenStatus> PrepareOpenAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("runtime-open");
        return OpenHandler is null
            ? ValueTask.FromResult(OpenStatus)
            : OpenHandler(targetId, cancellationToken);
    }

    public ValueTask CloseAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("runtime-close");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeleteSessionDataAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("runtime-delete");
        DeleteInvocationCount++;
        if (DeleteHandler is not null)
        {
            await DeleteHandler(targetId, cancellationToken);
        }

        _sessionData[targetId] = false;
    }

    public ValueTask<bool> SessionDataExistsAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("runtime-exists");
        return ValueTask.FromResult(_sessionData.GetValueOrDefault(targetId));
    }
}

internal sealed class ScriptedClipboardPort : IClipboardPort
{
    private readonly BoundaryScript _events;

    public ScriptedClipboardPort(BoundaryScript events)
    {
        _events = events;
    }

    public int InvocationCount { get; private set; }

    public ValueTask ClearIfUnchangedAsync(
        ReadOnlyMemory<char> pastedText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Observe("clipboard");
        InvocationCount++;
        return ValueTask.CompletedTask;
    }
}
