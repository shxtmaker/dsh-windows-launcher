using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace DshLauncher.Core;

public sealed class TargetManager : ITargetManager, IDisposable
{
    public const int CurrentCatalogSchemaVersion = 1;
    public const int CurrentTrustPolicyVersion = 1;
    public const int DefaultPort = 3080;

    private readonly TargetManagerPorts _ports;
    private readonly IPairingDiagnosticSink _pairingDiagnostics;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _catalogWriteLock = new(1, 1);
    private readonly ConcurrentDictionary<TargetId, SemaphoreSlim> _targetLocks = new();
    private readonly ConcurrentDictionary<CandidateId, CandidateView> _candidates = new();
    private readonly ConcurrentDictionary<TargetId, TargetSessionState> _sessionStates = new();
    private CatalogState _catalog = CatalogState.Unavailable;
    private bool _initialized;
    private bool _disposed;

    public TargetManager(TargetManagerPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(ports.Storage);
        ArgumentNullException.ThrowIfNull(ports.Clock);
        ArgumentNullException.ThrowIfNull(ports.IdGenerator);
        ArgumentNullException.ThrowIfNull(ports.Network);
        ArgumentNullException.ThrowIfNull(ports.Probe);
        ArgumentNullException.ThrowIfNull(ports.Sessions);
        ArgumentNullException.ThrowIfNull(ports.Runtime);
        ArgumentNullException.ThrowIfNull(ports.Clipboard);
        _ports = ports;
        _pairingDiagnostics = ports.PairingDiagnostics ?? NullPairingDiagnosticSink.Instance;
    }

    public async ValueTask<TargetManagerSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return CreateSnapshot();
    }

    public async ValueTask<TargetCommandResult> ExecuteAsync(
        TargetCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            return command switch
            {
                InspectCandidate inspect => await InspectCandidateAsync(inspect, cancellationToken)
                    .ConfigureAwait(false),
                ConfirmCandidate confirm => await ConfirmCandidateAsync(confirm, cancellationToken)
                    .ConfigureAwait(false),
                Rename rename => await RenameAsync(rename, cancellationToken).ConfigureAwait(false),
                SetDefault setDefault => await SetDefaultAsync(setDefault, cancellationToken)
                    .ConfigureAwait(false),
                Pair pair => await PairAsync(pair, cancellationToken).ConfigureAwait(false),
                PrepareOpen prepareOpen => await PrepareOpenAsync(prepareOpen, cancellationToken)
                    .ConfigureAwait(false),
                InvalidateSession invalidateSession =>
                    await InvalidateSessionCommandAsync(
                        invalidateSession,
                        cancellationToken).ConfigureAwait(false),
                Forget forget => await ForgetAsync(forget, cancellationToken).ConfigureAwait(false),
                _ => Failure(TargetErrorCode.CommandUnsupported),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(TargetErrorCode.Cancelled);
        }
        catch
        {
            return Failure(TargetErrorCode.RuntimeFailure);
        }
        finally
        {
            if (command is Pair pair)
            {
                await ClearPairingInputAsync(pair.Link).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initializationLock.Dispose();
        _catalogWriteLock.Dispose();
        foreach (var targetLock in _targetLocks.Values)
        {
            targetLock.Dispose();
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                var read = await _ports.Storage.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
                _catalog = CreateCatalogState(read);

                if (_catalog.Availability == CatalogAvailability.Ready)
                {
                    await RecoverIncompleteOperationsAsync(cancellationToken).ConfigureAwait(false);
                    await LoadSessionStatesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _catalog = CatalogState.Unavailable;
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static CatalogState CreateCatalogState(TargetCatalogReadResult read)
    {
        return read.Status switch
        {
            CatalogReadStatus.Missing => CatalogState.Ready(Array.Empty<StoredTarget>(), null, 1),
            CatalogReadStatus.UnknownSchema => CatalogState.UnknownSchema(read.FoundSchemaVersion),
            CatalogReadStatus.Corrupt => CatalogState.Unrecoverable,
            CatalogReadStatus.Loaded or CatalogReadStatus.RecoveredFromBackup =>
                CreateValidatedCatalogState(read.Document),
            _ => CatalogState.Unavailable,
        };
    }

    private static CatalogState CreateValidatedCatalogState(TargetCatalogDocument? document)
    {
        if (document is null)
        {
            return CatalogState.Unrecoverable;
        }

        if (document.SchemaVersion > CurrentCatalogSchemaVersion)
        {
            return CatalogState.UnknownSchema(document.SchemaVersion);
        }

        if (document.SchemaVersion != CurrentCatalogSchemaVersion ||
            !TryValidateCatalog(document.Targets, document.DefaultTargetId))
        {
            return CatalogState.Unrecoverable;
        }

        return CatalogState.Ready(document.Targets.ToArray(), document.DefaultTargetId, 1);
    }

    private async ValueTask LoadSessionStatesAsync(CancellationToken cancellationToken)
    {
        foreach (var target in _catalog.Targets)
        {
            var endpoint = CreateStoredEndpoint(target);
            try
            {
                var session = await _ports.Sessions.ReadAsync(target.TargetId, endpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (session.Status == SessionMetadataStatus.PairingInProgress)
                {
                    await RollbackPairingAsync(target, cancellationToken).ConfigureAwait(false);
                    _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
                }
                else
                {
                    _sessionStates[target.TargetId] = session.Status == SessionMetadataStatus.Committed
                        ? TargetSessionState.Paired
                        : TargetSessionState.PendingPairing;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
            }
        }
    }

    private async ValueTask RecoverIncompleteOperationsAsync(CancellationToken cancellationToken)
    {
        var tombstones = await _ports.Storage.ReadForgetTombstonesAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var tombstone in tombstones)
        {
            var target = FindTarget(tombstone.TargetId);
            if (target is null)
            {
                var currentDocument = CreateDocument(
                    _catalog.Targets,
                    _catalog.DefaultTargetId);
                await _ports.Storage.WriteCatalogAsync(currentDocument, cancellationToken)
                    .ConfigureAwait(false);
                await _ports.Storage.WriteCatalogAsync(currentDocument, cancellationToken)
                    .ConfigureAwait(false);
                await _ports.Storage.ClearForgetTombstoneAsync(tombstone.TargetId, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var nextDefault = ResolveDefaultAfterForget(
                target.TargetId,
                tombstone.SuccessorTargetId,
                _catalog.Targets,
                _catalog.DefaultTargetId,
                out var error);
            if (error is not null)
            {
                _catalog = CatalogState.Unrecoverable;
                return;
            }

            await DeleteAndVerifySessionAsync(target, cancellationToken).ConfigureAwait(false);
            var remaining = _catalog.Targets.Where(item => item.TargetId != target.TargetId).ToArray();
            var document = CreateDocument(remaining, nextDefault);
            await _ports.Storage.WriteCatalogAsync(document, cancellationToken).ConfigureAwait(false);
            await _ports.Storage.WriteCatalogAsync(document, cancellationToken).ConfigureAwait(false);
            _catalog = CatalogState.Ready(remaining, nextDefault, _catalog.Revision + 1);
            _sessionStates.TryRemove(target.TargetId, out _);
            await _ports.Storage.ClearForgetTombstoneAsync(target.TargetId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<TargetCommandResult> InspectCandidateAsync(
        InspectCandidate command,
        CancellationToken cancellationToken)
    {
        if (TryGetCatalogBlock(out var blocked))
        {
            return blocked;
        }

        var endpointResult = TryCreateEndpoint(command.Ipv4, command.Port);
        if (endpointResult.Error is not null)
        {
            return Failure(endpointResult.Error.Code);
        }

        var networkError = await ValidateNetworkAndProbeAsync(endpointResult.Endpoint!, cancellationToken)
            .ConfigureAwait(false);
        if (networkError is not null)
        {
            return Failure(networkError.Code);
        }

        var candidateId = CreateUniqueCandidateId();
        var candidate = new CandidateView(candidateId, endpointResult.Endpoint!);
        _candidates[candidateId] = candidate;
        return Success(new CandidateInspected(candidate));
    }

    private async ValueTask<TargetCommandResult> ConfirmCandidateAsync(
        ConfirmCandidate command,
        CancellationToken cancellationToken)
    {
        if (TryGetCatalogBlock(out var blocked))
        {
            return blocked;
        }

        if (command.Trust.PolicyVersion != CurrentTrustPolicyVersion)
        {
            return Failure(TargetErrorCode.TrustPolicyOutdated);
        }

        if (!command.Trust.TrustedLanConfirmed ||
            !command.Trust.LinuxFirewallConfirmed ||
            !command.Trust.PrivilegedHarnessConfirmed)
        {
            return Failure(TargetErrorCode.TrustConfirmationRequired);
        }

        if (!_candidates.TryGetValue(command.CandidateId, out var candidate))
        {
            return Failure(TargetErrorCode.CandidateNotFound);
        }

        var networkError = await ValidateNetworkAndProbeAsync(candidate.Endpoint, cancellationToken)
            .ConfigureAwait(false);
        if (networkError is not null)
        {
            return Failure(networkError.Code);
        }

        await _catalogWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _catalog.Targets.FirstOrDefault(
                target => EndpointEquals(target, candidate.Endpoint));
            if (existing is not null)
            {
                _candidates.TryRemove(command.CandidateId, out _);
                return Success(new TargetConfirmed(existing.TargetId, true));
            }

            var targetId = CreateUniqueTargetId();
            var target = new StoredTarget(
                targetId,
                candidate.Endpoint.Ipv4,
                candidate.Endpoint.Port,
                NormalizeDisplayName(command.DisplayName),
                CurrentTrustPolicyVersion,
                _ports.Clock.UtcNow);
            var targets = _catalog.Targets.Append(target).ToArray();
            var defaultTargetId = _catalog.Targets.Count == 0 ? targetId : _catalog.DefaultTargetId;
            var document = CreateDocument(targets, defaultTargetId);
            await _ports.Storage.WriteCatalogAsync(document, cancellationToken).ConfigureAwait(false);
            _catalog = CatalogState.Ready(targets, defaultTargetId, _catalog.Revision + 1);
            _sessionStates[targetId] = TargetSessionState.PendingPairing;
            _candidates.TryRemove(command.CandidateId, out _);
            return Success(new TargetConfirmed(targetId, false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(TargetErrorCode.StorageFailure);
        }
        finally
        {
            _catalogWriteLock.Release();
        }
    }

    private async ValueTask<TargetCommandResult> RenameAsync(
        Rename command,
        CancellationToken cancellationToken)
    {
        return await WithTargetLockAsync(
            command.TargetId,
            async token =>
            {
                if (TryGetCatalogBlock(out var blocked))
                {
                    return blocked;
                }

                await _catalogWriteLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var target = FindTarget(command.TargetId);
                    if (target is null)
                    {
                        return Failure(TargetErrorCode.TargetNotFound);
                    }

                    var renamed = target with { DisplayName = NormalizeDisplayName(command.DisplayName) };
                    var targets = _catalog.Targets
                        .Select(item => item.TargetId == target.TargetId ? renamed : item)
                        .ToArray();
                    await _ports.Storage.WriteCatalogAsync(
                            CreateDocument(targets, _catalog.DefaultTargetId),
                            token)
                        .ConfigureAwait(false);
                    _catalog = CatalogState.Ready(
                        targets,
                        _catalog.DefaultTargetId,
                        _catalog.Revision + 1);
                    return Success(new TargetRenamed(target.TargetId));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return Failure(TargetErrorCode.StorageFailure);
                }
                finally
                {
                    _catalogWriteLock.Release();
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TargetCommandResult> SetDefaultAsync(
        SetDefault command,
        CancellationToken cancellationToken)
    {
        return await WithTargetLockAsync(
            command.TargetId,
            async token =>
            {
                if (TryGetCatalogBlock(out var blocked))
                {
                    return blocked;
                }

                await _catalogWriteLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (FindTarget(command.TargetId) is null)
                    {
                        return Failure(TargetErrorCode.TargetNotFound);
                    }

                    await _ports.Storage.WriteCatalogAsync(
                            CreateDocument(_catalog.Targets, command.TargetId),
                            token)
                        .ConfigureAwait(false);
                    _catalog = CatalogState.Ready(
                        _catalog.Targets,
                        command.TargetId,
                        _catalog.Revision + 1);
                    return Success(new DefaultTargetChanged(command.TargetId));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return Failure(TargetErrorCode.StorageFailure);
                }
                finally
                {
                    _catalogWriteLock.Release();
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TargetCommandResult> PairAsync(Pair command, CancellationToken cancellationToken)
    {
        return await WithTargetLockAsync(
            command.TargetId,
            async token =>
            {
                if (TryGetCatalogBlock(out var blocked))
                {
                    return blocked;
                }

                var target = FindTarget(command.TargetId);
                if (target is null)
                {
                    return Failure(TargetErrorCode.TargetNotFound);
                }

                var endpoint = CreateStoredEndpoint(target);
                var networkError = await ValidateNetworkAndProbeAsync(endpoint, token).ConfigureAwait(false);
                if (networkError is not null)
                {
                    return Failure(networkError.Code);
                }

                var parsed = PairingLinkParser.Parse(command.Link.DangerousGetMemory().Span, endpoint);
                if (parsed.ErrorCode is not null)
                {
                    return Failure(parsed.ErrorCode.Value);
                }

                var tokenCharacters = parsed.EncodedToken!;
                var transactionStarted = false;
                var activeStage = PairingDiagnosticStage.PairingTransaction;
                await ReportPairingAsync(
                    activeStage,
                    PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                try
                {
                    var existingSession = await _ports.Sessions.ReadAsync(target.TargetId, endpoint, token)
                        .ConfigureAwait(false);
                    if (existingSession.Status == SessionMetadataStatus.Committed)
                    {
                        await ReportPairingAsync(
                            PairingDiagnosticStage.PairingTransaction,
                            PairingDiagnosticOutcome.Rejected,
                            PairingFailureCategory.SessionMetadata).ConfigureAwait(false);
                        return Failure(TargetErrorCode.AlreadyPaired);
                    }

                    if (existingSession.Status != SessionMetadataStatus.Missing)
                    {
                        await InvalidateSessionAsync(target, token).ConfigureAwait(false);
                    }

                    activeStage = PairingDiagnosticStage.MetadataMarkerWrite;
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                    await _ports.Sessions.MarkPairingInProgressAsync(target.TargetId, endpoint, token)
                        .ConfigureAwait(false);
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
                    transactionStarted = true;
                    _sessionStates[target.TargetId] = TargetSessionState.Pairing;

                    activeStage = PairingDiagnosticStage.UncommittedRuntimeReset;
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                    await _ports.Runtime.ResetUncommittedAsync(target.TargetId, token).ConfigureAwait(false);
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);

                    activeStage = PairingDiagnosticStage.PairingTransaction;
                    var handshake = await _ports.Runtime.PairAsync(
                            target.TargetId,
                            endpoint,
                            tokenCharacters,
                            token)
                        .ConfigureAwait(false);
                    if (handshake.TokenRequestStatusCode != 303)
                    {
                        await RollbackPairingAsync(target, token).ConfigureAwait(false);
                        await ReportPairingAsync(
                            PairingDiagnosticStage.PairingTransaction,
                            PairingDiagnosticOutcome.Rejected,
                            PairingFailureCategory.Navigation,
                            NormalizeHttpStatusCode(handshake.TokenRequestStatusCode))
                            .ConfigureAwait(false);
                        return Failure(TargetErrorCode.PairingRejected);
                    }

                    if (!handshake.RootPageLoaded || !handshake.AuthenticatedApiAvailable)
                    {
                        await RollbackPairingAsync(target, token).ConfigureAwait(false);
                        await ReportPairingAsync(
                            PairingDiagnosticStage.PairingTransaction,
                            PairingDiagnosticOutcome.Incomplete,
                            PairingFailureCategory.Navigation).ConfigureAwait(false);
                        return Failure(TargetErrorCode.PairingIncomplete);
                    }

                    activeStage = PairingDiagnosticStage.MetadataCommit;
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                    await _ports.Sessions.CommitAsync(target.TargetId, endpoint, _ports.Clock.UtcNow, token)
                        .ConfigureAwait(false);
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
                    _sessionStates[target.TargetId] = TargetSessionState.Paired;
                    transactionStarted = false;
                    await ReportPairingAsync(
                        PairingDiagnosticStage.PairingTransaction,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
                    return Success(new TargetPaired(target.TargetId));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Cancelled,
                        PairingFailureCategory.Cancelled).ConfigureAwait(false);
                    if (transactionStarted &&
                        !await RollbackPairingBestEffortAsync(target).ConfigureAwait(false))
                    {
                        return Failure(TargetErrorCode.SessionCleanupFailed);
                    }

                    throw;
                }
                catch
                {
                    await ReportPairingAsync(
                        activeStage,
                        PairingDiagnosticOutcome.Failed,
                        ClassifyManagerPairingFailure(activeStage)).ConfigureAwait(false);
                    if (transactionStarted &&
                        !await RollbackPairingBestEffortAsync(target).ConfigureAwait(false))
                    {
                        return Failure(TargetErrorCode.SessionCleanupFailed);
                    }

                    return Failure(TargetErrorCode.RuntimeFailure);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(tokenCharacters.AsSpan()));
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TargetCommandResult> PrepareOpenAsync(
        PrepareOpen command,
        CancellationToken cancellationToken)
    {
        return await WithTargetLockAsync(
            command.TargetId,
            async token =>
            {
                if (TryGetCatalogBlock(out var blocked))
                {
                    return blocked;
                }

                var target = FindTarget(command.TargetId);
                if (target is null)
                {
                    return Failure(TargetErrorCode.TargetNotFound);
                }

                var endpoint = CreateStoredEndpoint(target);
                var network = await _ports.Network.GetCurrentCategoryAsync(token).ConfigureAwait(false);
                if (network != NetworkCategory.Private)
                {
                    return Failure(TargetErrorCode.NetworkNotPrivate);
                }

                var probe = await _ports.Probe.ProbeAsync(endpoint, token).ConfigureAwait(false);
                if (probe.Classification != TargetProbeClassification.SupportedAuthenticatedHarness)
                {
                    if (probe.Classification is TargetProbeClassification.DefaultPortOccupied or
                        TargetProbeClassification.WrongService or
                        TargetProbeClassification.LegacyUnauthenticatedHarness or
                        TargetProbeClassification.UnsupportedHarness)
                    {
                        if (!await InvalidateSessionSafelyAsync(target, token).ConfigureAwait(false))
                        {
                            return Failure(TargetErrorCode.SessionCleanupFailed);
                        }
                    }

                    return Failure(MapProbeError(probe.Classification).Code);
                }

                SessionMetadataReadResult session;
                try
                {
                    session = await _ports.Sessions.ReadAsync(target.TargetId, endpoint, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    return Failure(TargetErrorCode.RuntimeFailure);
                }

                if (session.Status is SessionMetadataStatus.Missing or SessionMetadataStatus.PairingInProgress)
                {
                    if (session.Status == SessionMetadataStatus.PairingInProgress &&
                        !await InvalidateSessionSafelyAsync(target, token).ConfigureAwait(false))
                    {
                        return Failure(TargetErrorCode.SessionCleanupFailed);
                    }

                    _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
                    return Success(new OpenPrepared(target.TargetId, OpenDisposition.PairingRequired));
                }

                if (session.Status is SessionMetadataStatus.Corrupt or
                    SessionMetadataStatus.UnknownSchema or
                    SessionMetadataStatus.OriginMismatch)
                {
                    if (!await InvalidateSessionSafelyAsync(target, token).ConfigureAwait(false))
                    {
                        return Failure(TargetErrorCode.SessionCleanupFailed);
                    }

                    return Success(new OpenPrepared(target.TargetId, OpenDisposition.PairingRequired));
                }

                RuntimeOpenStatus runtimeStatus;
                try
                {
                    runtimeStatus = await _ports.Runtime.PrepareOpenAsync(target.TargetId, endpoint, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    return Failure(TargetErrorCode.RuntimeFailure);
                }

                switch (runtimeStatus)
                {
                    case RuntimeOpenStatus.Ready:
                        _sessionStates[target.TargetId] = TargetSessionState.Paired;
                        return Success(new OpenPrepared(target.TargetId, OpenDisposition.Ready));
                    case RuntimeOpenStatus.Unauthorized:
                    case RuntimeOpenStatus.OriginMismatch:
                        if (!await InvalidateSessionSafelyAsync(target, token).ConfigureAwait(false))
                        {
                            return Failure(TargetErrorCode.SessionCleanupFailed);
                        }

                        return Success(new OpenPrepared(target.TargetId, OpenDisposition.PairingRequired));
                    case RuntimeOpenStatus.Forbidden:
                        return Failure(TargetErrorCode.SessionForbidden);
                    case RuntimeOpenStatus.Unreachable:
                    case RuntimeOpenStatus.TemporaryFailure:
                        return Failure(TargetErrorCode.SessionUnavailable);
                    default:
                        return Failure(TargetErrorCode.RuntimeFailure);
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TargetCommandResult> ForgetAsync(
        Forget command,
        CancellationToken cancellationToken)
    {
        return await WithTargetLockAsync(
            command.TargetId,
            async token =>
            {
                if (TryGetCatalogBlock(out var blocked))
                {
                    return blocked;
                }

                var target = FindTarget(command.TargetId);
                if (target is null)
                {
                    return Failure(TargetErrorCode.TargetNotFound);
                }

                if (command.ExpectedRevision != _catalog.Revision)
                {
                    return Failure(TargetErrorCode.SnapshotStale);
                }

                var nextDefault = ResolveDefaultAfterForget(
                    target.TargetId,
                    command.SuccessorTargetId,
                    _catalog.Targets,
                    _catalog.DefaultTargetId,
                    out var error);
                if (error is not null)
                {
                    return Failure(error.Code);
                }

                try
                {
                    await _ports.Storage.WriteForgetTombstoneAsync(
                            new ForgetTombstone(
                                target.TargetId,
                                _catalog.DefaultTargetId == target.TargetId ? nextDefault : null),
                            token)
                        .ConfigureAwait(false);
                    await DeleteAndVerifySessionAsync(target, token).ConfigureAwait(false);

                    await _catalogWriteLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (FindTarget(target.TargetId) is null)
                        {
                            return Failure(TargetErrorCode.TargetNotFound);
                        }

                        var committedDefault = ResolveDefaultAfterForget(
                            target.TargetId,
                            command.SuccessorTargetId,
                            _catalog.Targets,
                            _catalog.DefaultTargetId,
                            out var commitError);
                        if (commitError is not null)
                        {
                            await _ports.Storage.ClearForgetTombstoneAsync(target.TargetId, token)
                                .ConfigureAwait(false);
                            _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
                            return Failure(commitError.Code);
                        }

                        if (committedDefault != nextDefault)
                        {
                            await _ports.Storage.WriteForgetTombstoneAsync(
                                    new ForgetTombstone(
                                        target.TargetId,
                                        _catalog.DefaultTargetId == target.TargetId
                                            ? committedDefault
                                            : null),
                                    token)
                                .ConfigureAwait(false);
                        }

                        var remaining = _catalog.Targets
                            .Where(item => item.TargetId != target.TargetId)
                            .ToArray();
                        var document = CreateDocument(remaining, committedDefault);
                        await _ports.Storage.WriteCatalogAsync(document, token).ConfigureAwait(false);
                        await _ports.Storage.WriteCatalogAsync(document, token).ConfigureAwait(false);
                        _catalog = CatalogState.Ready(
                            remaining,
                            committedDefault,
                            _catalog.Revision + 1);
                        _sessionStates.TryRemove(target.TargetId, out _);
                    }
                    finally
                    {
                        _catalogWriteLock.Release();
                    }

                    try
                    {
                        await _ports.Storage.ClearForgetTombstoneAsync(target.TargetId, token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // A committed catalog plus the durable tombstone is safe and is reconciled on restart.
                    }

                    return Success(new TargetForgotten(target.TargetId));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return Failure(TargetErrorCode.SessionCleanupFailed);
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TargetCommandResult> InvalidateSessionCommandAsync(
        InvalidateSession command,
        CancellationToken cancellationToken)
    {
        return await WithTargetLockAsync(
            command.TargetId,
            async token =>
            {
                if (TryGetCatalogBlock(out var blocked))
                {
                    return blocked;
                }

                var target = FindTarget(command.TargetId);
                if (target is null)
                {
                    return Failure(TargetErrorCode.TargetNotFound);
                }

                try
                {
                    await InvalidateSessionAsync(target, token)
                        .ConfigureAwait(false);
                    return Success(new SessionInvalidated(target.TargetId));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return Failure(TargetErrorCode.SessionCleanupFailed);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TargetError?> ValidateNetworkAndProbeAsync(
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var network = await _ports.Network.GetCurrentCategoryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (network != NetworkCategory.Private)
        {
            return Error(TargetErrorCode.NetworkNotPrivate);
        }

        var probe = await _ports.Probe.ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return probe.Classification == TargetProbeClassification.SupportedAuthenticatedHarness
            ? null
            : MapProbeError(probe.Classification);
    }

    private static TargetError MapProbeError(TargetProbeClassification classification) => classification switch
    {
        TargetProbeClassification.Forbidden => Error(TargetErrorCode.ProbeForbidden),
        TargetProbeClassification.Unreachable => Error(TargetErrorCode.ProbeUnreachable),
        TargetProbeClassification.TimedOut => Error(TargetErrorCode.ProbeTimedOut),
        TargetProbeClassification.HarnessNotListening => Error(TargetErrorCode.HarnessNotRunning),
        TargetProbeClassification.DefaultPortOccupied => Error(TargetErrorCode.DefaultPortOccupied),
        TargetProbeClassification.WrongService => Error(TargetErrorCode.WrongService),
        TargetProbeClassification.LegacyUnauthenticatedHarness =>
            Error(TargetErrorCode.LegacyUnauthenticatedHarness),
        TargetProbeClassification.UnsupportedHarness => Error(TargetErrorCode.UnsupportedHarness),
        _ => Error(TargetErrorCode.RuntimeFailure),
    };

    private async ValueTask<TargetCommandResult> WithTargetLockAsync(
        TargetId targetId,
        Func<CancellationToken, ValueTask<TargetCommandResult>> action,
        CancellationToken cancellationToken)
    {
        var targetLock = _targetLocks.GetOrAdd(targetId, static _ => new SemaphoreSlim(1, 1));
        await targetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            targetLock.Release();
        }
    }

    private async ValueTask DeleteAndVerifySessionAsync(
        StoredTarget target,
        CancellationToken cancellationToken)
    {
        await _ports.Runtime.CloseAsync(target.TargetId, cancellationToken).ConfigureAwait(false);
        await _ports.Runtime.DeleteSessionDataAsync(target.TargetId, cancellationToken)
            .ConfigureAwait(false);
        if (await _ports.Runtime.SessionDataExistsAsync(target.TargetId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("Session data deletion could not be verified.");
        }

        await _ports.Sessions.DeleteAsync(target.TargetId, cancellationToken).ConfigureAwait(false);
        var session = await _ports.Sessions.ReadAsync(
                target.TargetId,
                CreateStoredEndpoint(target),
                cancellationToken)
            .ConfigureAwait(false);
        if (session.Status != SessionMetadataStatus.Missing)
        {
            throw new InvalidOperationException("Session metadata deletion could not be verified.");
        }
    }

    private async ValueTask InvalidateSessionAsync(
        StoredTarget target,
        CancellationToken cancellationToken)
    {
        await DeleteAndVerifySessionAsync(target, cancellationToken).ConfigureAwait(false);
        _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
    }

    private async ValueTask<bool> InvalidateSessionSafelyAsync(
        StoredTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            await InvalidateSessionAsync(target, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask RollbackPairingAsync(
        StoredTarget target,
        CancellationToken cancellationToken)
    {
        var cleanupFailed = false;
        await ReportPairingAsync(
            PairingDiagnosticStage.RollbackRuntimeDelete,
            PairingDiagnosticOutcome.Started).ConfigureAwait(false);
        try
        {
            await _ports.Runtime.DeleteSessionDataAsync(target.TargetId, cancellationToken)
                .ConfigureAwait(false);
            if (await _ports.Runtime.SessionDataExistsAsync(
                    target.TargetId,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The uncommitted browser data could not be removed.");
            }

            await ReportPairingAsync(
                PairingDiagnosticStage.RollbackRuntimeDelete,
                PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
        }
        catch
        {
            cleanupFailed = true;
            await ReportPairingAsync(
                PairingDiagnosticStage.RollbackRuntimeDelete,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.BrowserData).ConfigureAwait(false);
        }

        await ReportPairingAsync(
            PairingDiagnosticStage.RollbackMetadataDelete,
            PairingDiagnosticOutcome.Started).ConfigureAwait(false);
        try
        {
            await _ports.Sessions.DeletePairingStateAsync(target.TargetId, cancellationToken)
                .ConfigureAwait(false);
            await ReportPairingAsync(
                PairingDiagnosticStage.RollbackMetadataDelete,
                PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
        }
        catch
        {
            cleanupFailed = true;
            await ReportPairingAsync(
                PairingDiagnosticStage.RollbackMetadataDelete,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.SessionMetadata).ConfigureAwait(false);
        }

        _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
        if (cleanupFailed)
        {
            throw new InvalidOperationException(
                "The uncommitted pairing state could not be completely removed.");
        }
    }

    private async ValueTask<bool> RollbackPairingBestEffortAsync(StoredTarget target)
    {
        try
        {
            await RollbackPairingAsync(target, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _sessionStates[target.TargetId] = TargetSessionState.PendingPairing;
            return false;
        }
    }

    private ValueTask ReportPairingAsync(
        PairingDiagnosticStage stage,
        PairingDiagnosticOutcome outcome,
        PairingFailureCategory? failureCategory = null,
        int? httpStatusCode = null) =>
        _pairingDiagnostics.ReportSafelyAsync(
            new PairingDiagnosticEvent(
                stage,
                outcome,
                failureCategory,
                httpStatusCode));

    private static PairingFailureCategory ClassifyManagerPairingFailure(
        PairingDiagnosticStage stage) => stage switch
        {
            PairingDiagnosticStage.MetadataMarkerWrite or
                PairingDiagnosticStage.MetadataCommit =>
                PairingFailureCategory.SessionMetadata,
            PairingDiagnosticStage.UncommittedRuntimeReset =>
                PairingFailureCategory.BrowserData,
            _ => PairingFailureCategory.Unexpected,
        };

    private static int? NormalizeHttpStatusCode(int statusCode) =>
        statusCode is >= 100 and <= 599 ? statusCode : null;

    private async ValueTask ClearPairingInputAsync(PairingLinkSecret link)
    {
        if (link.IsCleared)
        {
            return;
        }

        try
        {
            await _ports.Clipboard.ClearIfUnchangedAsync(
                    link.DangerousGetMemory(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Clipboard cleanup is explicitly best effort; the in-process copy is still cleared below.
        }
        finally
        {
            link.Clear();
        }
    }

    private TargetManagerSnapshot CreateSnapshot()
    {
        var catalog = _catalog;
        var targets = catalog.Targets
            .Select(target =>
            {
                var endpoint = CreateStoredEndpoint(target);
                return new TargetView(
                    target.TargetId,
                    endpoint,
                    target.DisplayName,
                    target.DisplayName ?? endpoint.Authority,
                    catalog.DefaultTargetId == target.TargetId,
                    target.TrustPolicyVersion,
                    target.TrustedAtUtc,
                    _sessionStates.GetValueOrDefault(
                        target.TargetId,
                        TargetSessionState.PendingPairing));
            })
            .ToArray();

        return new TargetManagerSnapshot(
            catalog.Availability,
            catalog.SchemaVersion,
            catalog.Revision,
            Array.AsReadOnly(targets),
            catalog.DefaultTargetId,
            catalog.Problem);
    }

    private bool TryGetCatalogBlock(out TargetCommandResult result)
    {
        if (_catalog.Availability == CatalogAvailability.Ready)
        {
            result = null!;
            return false;
        }

        result = TargetCommandResult.Failure(
            CreateSnapshot(),
            _catalog.Problem ?? Error(TargetErrorCode.CatalogUnavailable));
        return true;
    }

    private TargetCommandResult Success(TargetCommandOutcome outcome) =>
        TargetCommandResult.Success(CreateSnapshot(), outcome);

    private TargetCommandResult Failure(TargetErrorCode code) =>
        TargetCommandResult.Failure(CreateSnapshot(), Error(code));

    private static TargetError Error(TargetErrorCode code) => new(
        code,
        GetErrorScope(code),
        IsRetryable(code));

    private static TargetErrorScope GetErrorScope(TargetErrorCode code) => code switch
    {
        TargetErrorCode.InvalidIpv4 or TargetErrorCode.InvalidPort or
            TargetErrorCode.CandidateNotFound or TargetErrorCode.TrustConfirmationRequired or
            TargetErrorCode.TrustPolicyOutdated => TargetErrorScope.Candidate,
        TargetErrorCode.NetworkNotPrivate => TargetErrorScope.Network,
        TargetErrorCode.ProbeForbidden or TargetErrorCode.ProbeUnreachable or
            TargetErrorCode.ProbeTimedOut or TargetErrorCode.HarnessNotRunning or
            TargetErrorCode.DefaultPortOccupied or TargetErrorCode.WrongService or
            TargetErrorCode.LegacyUnauthenticatedHarness or
            TargetErrorCode.UnsupportedHarness => TargetErrorScope.Probe,
        TargetErrorCode.PairingLinkInvalid or TargetErrorCode.PairingLinkWrongTarget or
            TargetErrorCode.AlreadyPaired or TargetErrorCode.PairingRejected or
            TargetErrorCode.PairingIncomplete => TargetErrorScope.Pairing,
        TargetErrorCode.SessionForbidden or TargetErrorCode.SessionUnavailable or
            TargetErrorCode.SessionCleanupFailed => TargetErrorScope.Session,
        TargetErrorCode.CatalogUnknownSchema or TargetErrorCode.CatalogCorrupt or
            TargetErrorCode.CatalogUnavailable or TargetErrorCode.StorageFailure =>
            TargetErrorScope.Catalog,
        TargetErrorCode.TargetNotFound or TargetErrorCode.SuccessorRequired or
            TargetErrorCode.InvalidSuccessor or TargetErrorCode.SnapshotStale =>
            TargetErrorScope.Target,
        _ => TargetErrorScope.Command,
    };

    private static bool IsRetryable(TargetErrorCode code) => code is
        TargetErrorCode.ProbeForbidden or
        TargetErrorCode.ProbeUnreachable or
        TargetErrorCode.ProbeTimedOut or
        TargetErrorCode.HarnessNotRunning or
        TargetErrorCode.SessionForbidden or
        TargetErrorCode.SessionUnavailable or
        TargetErrorCode.SessionCleanupFailed or
        TargetErrorCode.CatalogUnavailable or
        TargetErrorCode.StorageFailure or
        TargetErrorCode.RuntimeFailure or
        TargetErrorCode.Cancelled or
        TargetErrorCode.SnapshotStale;

    private StoredTarget? FindTarget(TargetId targetId) =>
        _catalog.Targets.FirstOrDefault(target => target.TargetId == targetId);

    private CandidateId CreateUniqueCandidateId()
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var id = new CandidateId(_ports.IdGenerator.NewId());
            if (id.Value != Guid.Empty && !_candidates.ContainsKey(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("The ID source did not produce a unique candidate ID.");
    }

    private TargetId CreateUniqueTargetId()
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var id = new TargetId(_ports.IdGenerator.NewId());
            if (id.Value != Guid.Empty && _catalog.Targets.All(target => target.TargetId != id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("The ID source did not produce a unique target ID.");
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        return displayName.Trim();
    }

    private static EndpointResult TryCreateEndpoint(string ipv4, string? portText)
    {
        if (!TryNormalizePrivateIpv4(ipv4, out var normalizedIpv4))
        {
            return new EndpointResult(null, Error(TargetErrorCode.InvalidIpv4));
        }

        var port = DefaultPort;
        if (!string.IsNullOrEmpty(portText))
        {
            if (!portText.All(static character => character is >= '0' and <= '9') ||
                !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port is < 1 or > 65535)
            {
                return new EndpointResult(null, Error(TargetErrorCode.InvalidPort));
            }
        }

        return new EndpointResult(new TargetEndpoint(normalizedIpv4, port), null);
    }

    private static bool TryNormalizePrivateIpv4(string ipv4, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(ipv4))
        {
            return false;
        }

        Span<int> octets = stackalloc int[4];
        var octetIndex = 0;
        var segmentStart = 0;
        for (var index = 0; index <= ipv4.Length; index++)
        {
            if (index != ipv4.Length && ipv4[index] != '.')
            {
                continue;
            }

            if (octetIndex >= octets.Length || index == segmentStart)
            {
                return false;
            }

            var segment = ipv4.AsSpan(segmentStart, index - segmentStart);
            if (segment.Length > 1 && segment[0] == '0')
            {
                return false;
            }

            var value = 0;
            foreach (var character in segment)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }

                value = (value * 10) + (character - '0');
                if (value > 255)
                {
                    return false;
                }
            }

            octets[octetIndex++] = value;
            segmentStart = index + 1;
        }

        if (octetIndex != 4)
        {
            return false;
        }

        var isPrivate = octets[0] == 10 ||
            (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
            (octets[0] == 192 && octets[1] == 168);
        if (!isPrivate)
        {
            return false;
        }

        normalized = string.Create(
            CultureInfo.InvariantCulture,
            $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}");
        return true;
    }

    private static bool TryValidateCatalog(
        IReadOnlyList<StoredTarget>? targets,
        TargetId? defaultTargetId)
    {
        if (targets is null)
        {
            return false;
        }

        if (targets.Count == 0)
        {
            return defaultTargetId is null;
        }

        if (defaultTargetId is null || defaultTargetId.Value.Value == Guid.Empty)
        {
            return false;
        }

        var ids = new HashSet<TargetId>();
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.TargetId.Value == Guid.Empty || !ids.Add(target.TargetId))
            {
                return false;
            }

            var endpointResult = TryCreateEndpoint(target.Ipv4, target.Port.ToString(CultureInfo.InvariantCulture));
            if (endpointResult.Endpoint is null ||
                endpointResult.Endpoint.Ipv4 != target.Ipv4 ||
                endpointResult.Endpoint.Port != target.Port ||
                !endpoints.Add(endpointResult.Endpoint.Origin) ||
                target.TrustPolicyVersion < 1 ||
                target.DisplayName is not null &&
                (string.IsNullOrWhiteSpace(target.DisplayName) || target.DisplayName != target.DisplayName.Trim()))
            {
                return false;
            }
        }

        return ids.Contains(defaultTargetId.Value);
    }

    private static TargetId? ResolveDefaultAfterForget(
        TargetId removedTargetId,
        TargetId? requestedSuccessor,
        IReadOnlyList<StoredTarget> targets,
        TargetId? currentDefault,
        out TargetError? error)
    {
        error = null;
        if (targets.All(target => target.TargetId != removedTargetId))
        {
            error = Error(TargetErrorCode.TargetNotFound);
            return currentDefault;
        }

        if (currentDefault != removedTargetId)
        {
            return currentDefault;
        }

        var remaining = targets.Where(target => target.TargetId != removedTargetId).ToArray();
        if (remaining.Length == 0)
        {
            if (requestedSuccessor is not null)
            {
                error = Error(TargetErrorCode.InvalidSuccessor);
            }

            return null;
        }

        if (remaining.Length == 1)
        {
            if (requestedSuccessor is not null && requestedSuccessor != remaining[0].TargetId)
            {
                error = Error(TargetErrorCode.InvalidSuccessor);
            }

            return remaining[0].TargetId;
        }

        if (requestedSuccessor is null)
        {
            error = Error(TargetErrorCode.SuccessorRequired);
            return currentDefault;
        }

        if (remaining.All(target => target.TargetId != requestedSuccessor.Value))
        {
            error = Error(TargetErrorCode.InvalidSuccessor);
            return currentDefault;
        }

        return requestedSuccessor;
    }

    private static TargetCatalogDocument CreateDocument(
        IReadOnlyList<StoredTarget> targets,
        TargetId? defaultTargetId)
    {
        if (!TryValidateCatalog(targets, defaultTargetId))
        {
            throw new InvalidOperationException("The catalog invariant is invalid.");
        }

        return new TargetCatalogDocument(CurrentCatalogSchemaVersion, defaultTargetId, targets.ToArray());
    }

    private static bool EndpointEquals(StoredTarget target, TargetEndpoint endpoint) =>
        target.Ipv4 == endpoint.Ipv4 && target.Port == endpoint.Port;

    private static TargetEndpoint CreateStoredEndpoint(StoredTarget target) =>
        new(target.Ipv4, target.Port);

    private sealed record CatalogState(
        CatalogAvailability Availability,
        int? SchemaVersion,
        long Revision,
        IReadOnlyList<StoredTarget> Targets,
        TargetId? DefaultTargetId,
        TargetError? Problem)
    {
        public static CatalogState Unavailable { get; } = new(
            CatalogAvailability.Unavailable,
            null,
            0,
            Array.Empty<StoredTarget>(),
            null,
            Error(TargetErrorCode.CatalogUnavailable));

        public static CatalogState Unrecoverable { get; } = new(
            CatalogAvailability.Unrecoverable,
            CurrentCatalogSchemaVersion,
            0,
            Array.Empty<StoredTarget>(),
            null,
            Error(TargetErrorCode.CatalogCorrupt));

        public static CatalogState UnknownSchema(int? schemaVersion) => new(
            CatalogAvailability.UnknownSchema,
            schemaVersion,
            0,
            Array.Empty<StoredTarget>(),
            null,
            Error(TargetErrorCode.CatalogUnknownSchema));

        public static CatalogState Ready(
            IReadOnlyList<StoredTarget> targets,
            TargetId? defaultTargetId,
            long revision) =>
            new(
                CatalogAvailability.Ready,
                CurrentCatalogSchemaVersion,
                revision,
                targets.ToArray(),
                defaultTargetId,
                null);
    }

    private sealed record EndpointResult(TargetEndpoint? Endpoint, TargetError? Error);
}

internal static class PairingLinkParser
{
    private const string Prefix = "dsh web:";
    private const string Scheme = "http://";
    private const string TokenName = "token=";

    public static PairingParseResult Parse(ReadOnlySpan<char> input, TargetEndpoint endpoint)
    {
        var value = Trim(input);
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = Trim(value[Prefix.Length..]);
        }

        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return PairingParseResult.Invalid;
        }

        value = value[Scheme.Length..];
        var slashIndex = value.IndexOf('/');
        if (slashIndex <= 0)
        {
            return PairingParseResult.Invalid;
        }

        var authority = value[..slashIndex];
        if (authority.Contains('@') || authority.Contains('[') || authority.Contains(']'))
        {
            return PairingParseResult.Invalid;
        }

        var pathAndQuery = value[slashIndex..];
        if (!pathAndQuery.StartsWith("/?", StringComparison.Ordinal) ||
            pathAndQuery.Contains('#') ||
            pathAndQuery[2..].Contains('?'))
        {
            return PairingParseResult.Invalid;
        }

        var query = pathAndQuery[2..];
        if (!query.StartsWith(TokenName, StringComparison.Ordinal) ||
            query.Length == TokenName.Length ||
            query.Contains('&') ||
            query.Contains(';'))
        {
            return PairingParseResult.Invalid;
        }

        var encodedToken = query[TokenName.Length..];
        if (!IsValidEncodedToken(encodedToken))
        {
            return PairingParseResult.Invalid;
        }

        if (!authority.Equals(endpoint.Authority, StringComparison.Ordinal))
        {
            return PairingParseResult.WrongTarget;
        }

        return new PairingParseResult(encodedToken.ToArray(), null);
    }

    private static bool IsValidEncodedToken(ReadOnlySpan<char> token)
    {
        for (var index = 0; index < token.Length; index++)
        {
            var character = token[index];
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }

            if (character == '%')
            {
                if (index + 2 >= token.Length ||
                    !IsHex(token[index + 1]) ||
                    !IsHex(token[index + 2]))
                {
                    return false;
                }

                index += 2;
            }
        }

        return true;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end >= start && char.IsWhiteSpace(value[end]))
        {
            end--;
        }

        return value[start..(end + 1)];
    }
}

internal sealed record PairingParseResult(char[]? EncodedToken, TargetErrorCode? ErrorCode)
{
    public static PairingParseResult Invalid { get; } = new(null, TargetErrorCode.PairingLinkInvalid);

    public static PairingParseResult WrongTarget { get; } = new(null, TargetErrorCode.PairingLinkWrongTarget);
}
