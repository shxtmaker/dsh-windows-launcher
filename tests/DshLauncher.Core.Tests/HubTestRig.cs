using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using Xunit;

namespace DshLauncher.Core.Tests;

/// <summary>Shared fakes and helpers for hub behavior tests.</summary>
internal sealed class HubTestRig : IDisposable
{
    public HubTestRig()
    {
        Clock = new ManualClock();
        Transport = new FakePairingTransport();
        Store = new InMemoryTargetStore();
        Changed = new List<HubSnapshot>();
        Hub = new PairingHub(
            new PairingHubOptions
            {
                HeartbeatInterval = TimeSpan.FromSeconds(10),
                DelayFactory = (_, cancellationToken) => Task.Delay(5, cancellationToken),
            },
            Store,
            Transport,
            Clock,
            new SequentialIdGenerator());
        Hub.Changed += snapshot =>
        {
            lock (Changed)
            {
                Changed.Add(snapshot);
            }
        };
    }

    public ManualClock Clock { get; }

    public FakePairingTransport Transport { get; }

    public PairingHub Hub { get; }

    public InMemoryTargetStore Store { get; }

    public List<HubSnapshot> Changed { get; }

    public const string PairingLinkText = "http://192.168.10.8:3080/pair-accept?pair=secret-token";

    public async Task<TargetSnapshot> AddPairedTargetAsync()
    {
        Transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired,
            new DeviceCredential(PairingProtocol.DefaultCookieName, "device-1"),
            200));
        var result = await Hub.AddFromPairingLinkAsync(PairingLinkText, "工作台", TestContext.Current.CancellationToken);
        Assert.Null(result.Error);
        Assert.NotNull(result.Target);
        return result.Target!;
    }

    public async Task<TargetSnapshot> WaitUntilAsync(Func<HubSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
            if (predicate(snapshot))
            {
                return Assert.Single(snapshot.Targets);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The hub did not reach the expected state in time.");
    }

    /// <summary>Waits for a (possibly transient) state that appeared in the
    /// change stream, so fast transitions are never missed by polling.</summary>
    public async Task<HubSnapshot> WaitUntilChangedAsync(Func<HubSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            HubSnapshot? match;
            lock (Changed)
            {
                match = Changed.FirstOrDefault(predicate);
            }

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The hub never announced the expected state change.");
    }

    public void Dispose() => Hub.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public static TargetSnapshot SingleTargetOf(HubOperationResult result) =>
        result.Target ?? throw new InvalidOperationException("The operation produced no target.");

    public sealed class ManualClock : IClock
    {
        private DateTimeOffset _utcNow = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    public sealed class SequentialIdGenerator : IIdGenerator
    {
        private int _next;

        public Guid NewId() => new(10000000 + _next++, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    public sealed class InMemoryTargetStore : ITargetStore
    {
        private StoredHubDocument? _document;

        public int SaveCount { get; private set; }

        public HubStorageReadResult NextLoad { get; set; } = new(HubStorageStatus.Missing);

        public ValueTask<HubStorageReadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_document is null ? NextLoad : new HubStorageReadResult(HubStorageStatus.Loaded, _document));

        public ValueTask SaveAsync(StoredHubDocument document, CancellationToken cancellationToken = default)
        {
            _document = document;
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class FakePairingTransport : IPairingTransport
    {
        public Queue<PairingAcceptOutcome> AcceptOutcomes { get; } = new();

        public Queue<HeartbeatOutcome> HeartbeatOutcomes { get; } = new();

        public RemoteStatusProbeOutcome? ProbeOutcome { get; set; }

        public List<string> AcceptCalls { get; } = [];

        public List<DeviceCredential> HeartbeatCredentials { get; } = [];

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
            CancellationToken cancellationToken = default)
        {
            HeartbeatCredentials.Add(credential);
            return ValueTask.FromResult(HeartbeatOutcomes.Count > 0
                ? HeartbeatOutcomes.Dequeue()
                : HeartbeatOutcome.Alive());
        }

        public ValueTask<RemoteStatusProbeOutcome> ProbeStatusAsync(
            HarnessEndpoint endpoint,
            DeviceCredential? credential,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(ProbeOutcome ??
                new RemoteStatusProbeOutcome(RemoteStatusProbeKind.Reached, false, "idle", true, 200));
        }
    }
}
