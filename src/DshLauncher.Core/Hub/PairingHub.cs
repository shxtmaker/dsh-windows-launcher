using DshLauncher.Core.Pairing;

namespace DshLauncher.Core.Hub;

/// <summary>Tunables for the central pairing hub.</summary>
public sealed record PairingHubOptions
{
    /// <summary>Keep-alive heartbeat cadence. The host marks a device offline
    /// after 25 s without a gated request, so the interval must stay well
    /// below that; heartbeats also keep the 30-day idle sweep away.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for keep-alive retry backoff.</summary>
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Hard cap on managed targets.</summary>
    public int MaximumTargets { get; init; } = 32;

    /// <summary>Replaces the keep-alive wait between heartbeats. Hosting and
    /// tests inject an immediate or virtualized wait; production leaves this
    /// unset and uses real Task.Delay.</summary>
    public Func<TimeSpan, CancellationToken, Task>? DelayFactory { get; init; }

    public PairingHubOptions Normalized()
    {
        var interval = HeartbeatInterval < MinimumInterval ? MinimumInterval
            : HeartbeatInterval > MaximumInterval ? MaximumInterval
            : HeartbeatInterval;
        var backoff = MaximumBackoff < interval ? interval * MaximumBackoffMultiplier : MaximumBackoff;
        return this with { HeartbeatInterval = interval, MaximumBackoff = backoff };
    }

    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(2);

    internal const int MaximumBackoffMultiplier = 30;
}

/// <summary>Machine-readable hub failures for the web API surface.</summary>
public enum HubErrorCode
{
    InvalidPairingLink,
    InvalidBaseUrl,
    TargetLimitReached,
    TargetNotFound,
    TargetNotPaired,
    PairingInvalidToken,
    PairingTokenUsed,
    PairingForbidden,
    PairingRateLimited,
    PairingUnreachable,
    PairingFailed,
    StorageFailure,
    Cancelled,
}

public sealed record HubError(HubErrorCode Code, bool Retryable);

/// <summary>Result of a hub operation. Target is null when no target record
/// exists or changed (for example a malformed link); Url carries a derived
/// resource URL when the operation produces one.</summary>
public sealed record HubOperationResult(
    TargetSnapshot? Target,
    HubError? Error,
    string? Url = null,
    RemoteStatusProbeOutcome? Probe = null);

/// <summary>
/// The central pairing hub. It owns every pairing the Windows side holds
/// with harness remote access endpoints: strict pairing-link handling, one
/// device credential per target, per-target keep-alive heartbeats with
/// backoff, and persistence of the target directory. State changes are
/// announced through <see cref="Changed"/>; the event fires on a worker
/// thread and never while the hub's internal gate is held.
/// </summary>
public sealed class PairingHub : IAsyncDisposable
{
    public PairingHub(
        PairingHubOptions options,
        ITargetStore store,
        IPairingTransport transport,
        IClock clock,
        IIdGenerator idGenerator)
    {
        _options = options.Normalized();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
    }

    /// <summary>Fired after every visible state change with a fresh snapshot.</summary>
    public event Action<HubSnapshot>? Changed;

    public TimeSpan HeartbeatInterval => _options.HeartbeatInterval;

    public bool IsStarted { get; private set; }

    /// <summary>Loads the persisted target directory and starts keep-alive for paired targets.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStarted)
            {
                return;
            }

            var targets = await LoadTargetsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var stored in targets)
            {
                var record = new TargetRecord(stored);
                _targets[record.Stored.TargetId] = record;
            }

            foreach (var record in _targets.Values)
            {
                StartKeepAlive(record);
            }

            IsStarted = true;
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
    }

    /// <summary>Adds a target from a pasted pairing link and pairs immediately.</summary>
    public async ValueTask<HubOperationResult> AddFromPairingLinkAsync(
        string pairingLink,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PairingLink link;
        try
        {
            link = PairingLink.Parse(pairingLink);
        }
        catch (PairingLinkFormatException)
        {
            return new HubOperationResult(null, new HubError(HubErrorCode.InvalidPairingLink, false));
        }

        var (record, created) = await ResolveEndpointTargetAsync(link.BaseUri, displayName, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new HubOperationResult(null, new HubError(HubErrorCode.TargetLimitReached, false));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            record.Pairing = PairingState.Pairing;
            record.LastFailure = null;
            record.LastStatusCode = null;
            if (created)
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return await AcceptPairingAsync(record, link.Token, created, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-pairs an existing target with a fresh pairing link.</summary>
    public async ValueTask<HubOperationResult> AttachPairingAsync(
        Guid targetId,
        string pairingLink,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PairingLink link;
        try
        {
            link = PairingLink.Parse(pairingLink);
        }
        catch (PairingLinkFormatException)
        {
            return new HubOperationResult(null, new HubError(HubErrorCode.InvalidPairingLink, false));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TargetRecord? record;
        try
        {
            if (!_targets.TryGetValue(targetId, out record))
            {
                return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotFound, false));
            }

            if (!link.BaseUri.Equals(record.Endpoint.BaseUri))
            {
                return new HubOperationResult(
                    SnapshotOf(record),
                    new HubError(HubErrorCode.InvalidPairingLink, false));
            }

            record.Pairing = PairingState.Pairing;
            record.LastFailure = null;
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return await AcceptPairingAsync(record, link.Token, created: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds an endpoint-only target (no pairing link yet) and probes
    /// the remote access plugin's presence.</summary>
    public async ValueTask<HubOperationResult> AddEndpointAsync(
        string baseUrl,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri;
        try
        {
            baseUri = PairingLink.ParseBaseUri(baseUrl);
        }
        catch (PairingLinkFormatException)
        {
            return new HubOperationResult(null, new HubError(HubErrorCode.InvalidBaseUrl, false));
        }

        var (record, created) = await ResolveEndpointTargetAsync(baseUri, displayName, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new HubOperationResult(null, new HubError(HubErrorCode.TargetLimitReached, false));
        }

        if (created)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            await PublishChangedAsync().ConfigureAwait(false);
        }

        var probe = await _transport.ProbeStatusAsync(record.Endpoint, null, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            record.LastStatusCode = probe.StatusCode;
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return new HubOperationResult(SnapshotOf(record), null, Probe: probe);
    }

    public async ValueTask<HubOperationResult> RenameAsync(
        Guid targetId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TargetSnapshot snapshot;
        try
        {
            if (!_targets.TryGetValue(targetId, out var record))
            {
                return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotFound, false));
            }

            record.Stored = record.Stored with { DisplayName = NormalizeDisplayName(displayName) };
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            snapshot = SnapshotOf(record);
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return new HubOperationResult(snapshot, null);
    }

    public async ValueTask<HubOperationResult> SetKeepAliveAsync(
        Guid targetId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TargetSnapshot snapshot;
        try
        {
            if (!_targets.TryGetValue(targetId, out var record))
            {
                return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotFound, false));
            }

            record.Stored = record.Stored with { KeepAliveEnabled = enabled };
            if (enabled)
            {
                StartKeepAlive(record);
            }
            else
            {
                StopKeepAlive(record);
            }

            await PersistAsync(cancellationToken).ConfigureAwait(false);
            snapshot = SnapshotOf(record);
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return new HubOperationResult(snapshot, null);
    }

    /// <summary>Runs one keep-alive heartbeat immediately (outside the loop cadence).</summary>
    public async ValueTask<HubOperationResult> HeartbeatNowAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TargetRecord? record;
        DeviceCredential? credential;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.TryGetValue(targetId, out var candidate))
            {
                return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotFound, false));
            }

            record = candidate;
            credential = record.Stored.HasCredential
                ? new DeviceCredential(record.Stored.CookieName!, record.Stored.DeviceId!)
                : null;
        }
        finally
        {
            _gate.Release();
        }

        if (credential is null)
        {
            return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotPaired, false));
        }

        var outcome = await _transport.SendHeartbeatAsync(record.Endpoint, credential, cancellationToken)
            .ConfigureAwait(false);
        var revoked = await ApplyHeartbeatAsync(record, outcome, cancellationToken).ConfigureAwait(false);
        if (revoked)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StopKeepAlive(record);
            }
            finally
            {
                _gate.Release();
            }
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return new HubOperationResult(
            SnapshotOf(record),
            outcome.Status == HeartbeatStatus.Unpaired
                ? new HubError(HubErrorCode.TargetNotPaired, false)
                : null);
    }

    public async ValueTask<HubOperationResult> RemoveAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.Remove(targetId, out var record))
            {
                return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotFound, false));
            }

            StopKeepAlive(record);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return new HubOperationResult(null, null);
    }

    /// <summary>The cookieless remote UI URL for a paired target, for the
    /// web page's "open remote interface" action.</summary>
    public async ValueTask<HubOperationResult> GetRemoteUiUrlAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_targets.TryGetValue(targetId, out var record))
            {
                return new HubOperationResult(null, new HubError(HubErrorCode.TargetNotFound, false));
            }

            if (!record.Stored.HasCredential)
            {
                return new HubOperationResult(SnapshotOf(record), new HubError(HubErrorCode.TargetNotPaired, false));
            }

            return new HubOperationResult(
                SnapshotOf(record),
                null,
                Url: PairingLink.RemoteUiUrl(record.Endpoint.BaseUri, record.Stored.DeviceId!));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HubSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Task> loops;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            loops = _targets.Values
                .Select(record => record.LoopTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToList();
            foreach (var record in _targets.Values)
            {
                StopKeepAlive(record);
            }
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private readonly PairingHubOptions _options;
    private readonly ITargetStore _store;
    private readonly IPairingTransport _transport;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, TargetRecord> _targets = new();
    private bool _disposed;

    private const int MaximumDisplayNameCharacters = 256;

    /// <summary>Loads and validates the persisted directory. A corrupt
    /// document starts empty (the store keeps the previous bytes as backup);
    /// a newer schema refuses to boot.</summary>
    private async ValueTask<List<StoredTarget>> LoadTargetsAsync(CancellationToken cancellationToken)
    {
        HubStorageReadResult read;
        try
        {
            read = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HubStorageException or IOException or UnauthorizedAccessException)
        {
            throw new HubStorageException("The target store could not be read.", exception);
        }

        if (read is not { Status: HubStorageStatus.Loaded, Document: { } document })
        {
            return [];
        }

        if (document.SchemaVersion > StoredHubDocument.CurrentSchemaVersion)
        {
            throw new HubStorageException($"Unknown hub document schema {document.SchemaVersion}.");
        }

        var targets = new List<StoredTarget>();
        foreach (var target in document.Targets)
        {
            if (IsValid(target))
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    private static bool IsValid(StoredTarget target)
    {
        return target.TargetId != Guid.Empty &&
            Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            target.DisplayName is null or { Length: <= MaximumDisplayNameCharacters } &&
            (target.CookieName is null) == (target.DeviceId is null) &&
            target.HasCredential == (target.PairedAtUtc is not null);
    }

    /// <summary>Shared accept round trip behind add/attach: performs the
    /// transport call outside the gate, then commits or rolls back.</summary>
    private async ValueTask<HubOperationResult> AcceptPairingAsync(
        TargetRecord record,
        string token,
        bool created,
        CancellationToken cancellationToken)
    {
        var outcome = await _transport.AcceptPairingAsync(record.Endpoint, token, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status == PairingAcceptStatus.Paired && outcome.Credential is { } credential)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TargetSnapshot snapshot;
            try
            {
                record.Stored = record.Stored with
                {
                    CookieName = credential.CookieName,
                    DeviceId = credential.DeviceId,
                    PairedAtUtc = _clock.UtcNow,
                };
                record.Pairing = PairingState.Paired;
                record.Connectivity = ConnectivityState.Unknown;
                record.ConsecutiveFailures = 0;
                record.LastFailure = null;
                record.LastStatusCode = outcome.StatusCode;
                await PersistAsync(cancellationToken).ConfigureAwait(false);
                StartKeepAlive(record);
                snapshot = SnapshotOf(record);
            }
            finally
            {
                _gate.Release();
            }

            await PublishChangedAsync().ConfigureAwait(false);
            return new HubOperationResult(snapshot, null);
        }

        var error = outcome.Status switch
        {
            PairingAcceptStatus.InvalidToken => new HubError(HubErrorCode.PairingInvalidToken, false),
            PairingAcceptStatus.TokenUsed => new HubError(HubErrorCode.PairingTokenUsed, false),
            PairingAcceptStatus.Forbidden => new HubError(HubErrorCode.PairingForbidden, false),
            PairingAcceptStatus.RateLimited => new HubError(HubErrorCode.PairingRateLimited, true),
            PairingAcceptStatus.Unreachable => new HubError(HubErrorCode.PairingUnreachable, true),
            _ => new HubError(HubErrorCode.PairingFailed, true),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TargetSnapshot failureSnapshot;
        try
        {
            record.Pairing = record.Stored.HasCredential ? PairingState.Paired : PairingState.AwaitingPairing;
            record.LastFailure = outcome.Status.ToString();
            record.LastStatusCode = outcome.StatusCode;
            failureSnapshot = SnapshotOf(record);
        }
        finally
        {
            _gate.Release();
        }

        await PublishChangedAsync().ConfigureAwait(false);
        return new HubOperationResult(failureSnapshot, error);
    }

    /// <summary>Finds a target by base URL or creates one within the limit.
    /// The gate is held only around the map update; persistence happens in
    /// the caller once the accept flow completes.</summary>
    private async ValueTask<(TargetRecord? Record, bool Created)> ResolveEndpointTargetAsync(
        Uri baseUri,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var canonical = baseUri.ToString().TrimEnd('/');
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _targets.Values.FirstOrDefault(record =>
                record.Stored.BaseUrl.Equals(canonical, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return (existing, false);
            }

            if (_targets.Count >= _options.MaximumTargets)
            {
                return (null, false);
            }

            var stored = new StoredTarget(
                _idGenerator.NewId(),
                canonical,
                NormalizeDisplayName(displayName),
                null,
                null,
                null,
                KeepAliveEnabled: true);
            var record = new TargetRecord(stored);
            _targets[stored.TargetId] = record;
            return (record, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartKeepAlive(TargetRecord record)
    {
        if (_disposed ||
            record.LoopCts is not null ||
            !record.Stored.KeepAliveEnabled ||
            !record.Stored.HasCredential ||
            record.Pairing != PairingState.Paired)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => RunKeepAliveLoopAsync(record, cts), CancellationToken.None);
        record.LoopCts = cts;
        record.LoopTask = loop;
    }

    private static void StopKeepAlive(TargetRecord record)
    {
        record.LoopCts?.Cancel();
        record.LoopCts = null;
    }

    private async Task RunKeepAliveLoopAsync(TargetRecord record, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var credential = record.Stored.HasCredential
                    ? new DeviceCredential(record.Stored.CookieName!, record.Stored.DeviceId!)
                    : null;
                if (credential is null)
                {
                    return;
                }

                var outcome = await _transport.SendHeartbeatAsync(record.Endpoint, credential, token)
                    .ConfigureAwait(false);
                if (await ApplyHeartbeatAsync(record, outcome, token).ConfigureAwait(false))
                {
                    // The host revoked the device: the credential is dead and
                    // heartbeating it again would only produce 401 noise.
                    await PublishChangedAsync().ConfigureAwait(false);
                    return;
                }

                await PublishChangedAsync().ConfigureAwait(false);

                var delay = NextHeartbeatDelay(record);
                if (_options.DelayFactory is { } delayFactory)
                {
                    await delayFactory(delay, token).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A crashed loop must not tear the hub down: surface the target
            // as offline and let a manual heartbeat or re-pair recover it.
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                record.Connectivity = ConnectivityState.Offline;
                record.ConsecutiveFailures++;
                record.LastFailure = HeartbeatStatus.Failed.ToString();
            }
            finally
            {
                _gate.Release();
            }

            await PublishChangedAsync().ConfigureAwait(false);
        }
        finally
        {
            if (record.LoopCts == cts)
            {
                record.LoopCts = null;
            }
        }
    }

    /// <summary>Applies one heartbeat outcome to the target state. Returns
    /// true when the keep-alive loop must stop (host revoked the device).</summary>
    private async ValueTask<bool> ApplyHeartbeatAsync(
        TargetRecord record,
        HeartbeatOutcome outcome,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            record.LastHeartbeatUtc = _clock.UtcNow;
            record.LastStatusCode = outcome.StatusCode;
            switch (outcome.Status)
            {
                case HeartbeatStatus.Alive:
                    record.Connectivity = ConnectivityState.Online;
                    record.ConsecutiveFailures = 0;
                    record.LastFailure = null;
                    return false;
                case HeartbeatStatus.Unpaired:
                    record.Pairing = PairingState.Revoked;
                    record.Connectivity = ConnectivityState.Unknown;
                    record.LastFailure = outcome.Status.ToString();
                    return true;
                default:
                    record.Connectivity = ConnectivityState.Offline;
                    record.ConsecutiveFailures++;
                    record.LastFailure = outcome.Status.ToString();
                    return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeSpan NextHeartbeatDelay(TargetRecord record)
    {
        if (record.ConsecutiveFailures <= 0)
        {
            return _options.HeartbeatInterval;
        }

        var multiplier = Math.Min(Math.Pow(2, record.ConsecutiveFailures), PairingHubOptions.MaximumBackoffMultiplier);
        var delay = TimeSpan.FromMilliseconds(_options.HeartbeatInterval.TotalMilliseconds * multiplier);
        return delay > _options.MaximumBackoff ? _options.MaximumBackoff : delay;
    }

    private async ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        var document = new StoredHubDocument(
            StoredHubDocument.CurrentSchemaVersion,
            _targets.Values.Select(record => record.Stored).ToArray());
        try
        {
            await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HubStorageException or IOException or UnauthorizedAccessException)
        {
            throw new HubStorageException("The target store could not be written.", exception);
        }
    }

    private HubSnapshot BuildSnapshot() => new(
        _clock.UtcNow,
        _options.HeartbeatInterval,
        IsStarted,
        _targets.Values
            .OrderBy(record => record.Stored.BaseUrl, StringComparer.OrdinalIgnoreCase)
            .Select(SnapshotOf)
            .ToArray());

    private async ValueTask PublishChangedAsync()
    {
        HubSnapshot snapshot;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            snapshot = BuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(snapshot);
    }

    private static TargetSnapshot SnapshotOf(TargetRecord record) => new(
        record.Stored.TargetId,
        record.Stored.BaseUrl,
        record.Stored.DisplayName,
        record.Pairing,
        record.Connectivity,
        record.Stored.KeepAliveEnabled,
        record.Stored.HasCredential,
        record.Stored.PairedAtUtc,
        record.LastHeartbeatUtc,
        record.ConsecutiveFailures,
        record.LastFailure,
        record.LastStatusCode);

    private static string? NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var trimmed = displayName.Trim();
        return trimmed.Length == 0 ? null : trimmed[..Math.Min(trimmed.Length, MaximumDisplayNameCharacters)];
    }

    private sealed class TargetRecord
    {
        public TargetRecord(StoredTarget stored)
        {
            Stored = stored;
            Endpoint = new HarnessEndpoint(new Uri(stored.BaseUrl, UriKind.Absolute));
            Pairing = stored.HasCredential ? PairingState.Paired : PairingState.AwaitingPairing;
        }

        public StoredTarget Stored { get; set; }

        public HarnessEndpoint Endpoint { get; }

        public PairingState Pairing { get; set; }

        public ConnectivityState Connectivity { get; set; }

        public DateTimeOffset? LastHeartbeatUtc { get; set; }

        public int ConsecutiveFailures { get; set; }

        public string? LastFailure { get; set; }

        public int? LastStatusCode { get; set; }

        public CancellationTokenSource? LoopCts { get; set; }

        public Task? LoopTask { get; set; }
    }
}
