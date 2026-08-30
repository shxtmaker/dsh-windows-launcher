namespace DshLauncher.Core;

public sealed record TargetManagerPorts(
    ITargetStorage Storage,
    IClock Clock,
    IIdGenerator IdGenerator,
    INetworkPort Network,
    ITargetProbePort Probe,
    ISessionPort Sessions,
    ITargetRuntimePort Runtime,
    IClipboardPort Clipboard,
    IPairingDiagnosticSink? PairingDiagnostics = null);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    Guid NewId();
}

public enum CatalogReadStatus
{
    Missing,
    Loaded,
    RecoveredFromBackup,
    UnknownSchema,
    Corrupt,
}

public sealed record StoredTarget(
    TargetId TargetId,
    string Ipv4,
    int Port,
    string? DisplayName,
    int TrustPolicyVersion,
    DateTimeOffset TrustedAtUtc);

public sealed record TargetCatalogDocument(
    int SchemaVersion,
    TargetId? DefaultTargetId,
    IReadOnlyList<StoredTarget> Targets);

public sealed record TargetCatalogReadResult(
    CatalogReadStatus Status,
    TargetCatalogDocument? Document = null,
    int? FoundSchemaVersion = null);

public sealed record ForgetTombstone(TargetId TargetId, TargetId? SuccessorTargetId);

public interface ITargetStorage
{
    ValueTask<TargetCatalogReadResult> ReadCatalogAsync(CancellationToken cancellationToken);

    ValueTask WriteCatalogAsync(TargetCatalogDocument document, CancellationToken cancellationToken);

    ValueTask<IReadOnlyCollection<ForgetTombstone>> ReadForgetTombstonesAsync(
        CancellationToken cancellationToken);

    ValueTask WriteForgetTombstoneAsync(
        ForgetTombstone tombstone,
        CancellationToken cancellationToken);

    ValueTask ClearForgetTombstoneAsync(TargetId targetId, CancellationToken cancellationToken);
}

public enum NetworkCategory
{
    Private,
    Public,
    Unknown,
}

public interface INetworkPort
{
    ValueTask<NetworkCategory> GetCurrentCategoryAsync(CancellationToken cancellationToken);
}

public enum TargetProbeClassification
{
    SupportedAuthenticatedHarness,
    Forbidden,
    Unreachable,
    TimedOut,
    HarnessNotListening,
    DefaultPortOccupied,
    WrongService,
    LegacyUnauthenticatedHarness,
    UnsupportedHarness,
}

public sealed record TargetProbeResult(TargetProbeClassification Classification);

public interface ITargetProbePort
{
    ValueTask<TargetProbeResult> ProbeAsync(
        TargetEndpoint endpoint,
        CancellationToken cancellationToken);
}

public enum SessionMetadataStatus
{
    Missing,
    PairingInProgress,
    Committed,
    Corrupt,
    UnknownSchema,
    OriginMismatch,
}

public sealed record SessionMetadataReadResult(SessionMetadataStatus Status);

public interface ISessionPort
{
    ValueTask<SessionMetadataReadResult> ReadAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken);

    ValueTask MarkPairingInProgressAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken);

    ValueTask CommitAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken);

    ValueTask DeletePairingStateAsync(
        TargetId targetId,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(TargetId targetId, CancellationToken cancellationToken);
}

public sealed record PairingHandshake(
    int TokenRequestStatusCode,
    bool RootPageLoaded,
    bool AuthenticatedApiAvailable);

public enum RuntimeOpenStatus
{
    Ready,
    Unauthorized,
    Forbidden,
    Unreachable,
    TemporaryFailure,
    OriginMismatch,
}

public interface ITargetRuntimePort
{
    ValueTask ResetUncommittedAsync(TargetId targetId, CancellationToken cancellationToken);

    ValueTask<PairingHandshake> PairAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        ReadOnlyMemory<char> encodedToken,
        CancellationToken cancellationToken);

    ValueTask<RuntimeOpenStatus> PrepareOpenAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken);

    ValueTask CloseAsync(TargetId targetId, CancellationToken cancellationToken);

    ValueTask DeleteSessionDataAsync(TargetId targetId, CancellationToken cancellationToken);

    ValueTask<bool> SessionDataExistsAsync(TargetId targetId, CancellationToken cancellationToken);
}

public interface IClipboardPort
{
    ValueTask ClearIfUnchangedAsync(
        ReadOnlyMemory<char> pastedText,
        CancellationToken cancellationToken);
}
