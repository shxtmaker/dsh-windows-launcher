namespace DshLauncher.Core;

public interface ITargetManager
{
    ValueTask<TargetManagerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    ValueTask<TargetCommandResult> ExecuteAsync(
        TargetCommand command,
        CancellationToken cancellationToken = default);
}

public abstract record TargetCommand;

public sealed record InspectCandidate(string Ipv4, string? Port = null) : TargetCommand;

public sealed record ConfirmCandidate(
    CandidateId CandidateId,
    string? DisplayName,
    TrustConfirmation Trust) : TargetCommand;

public sealed record TrustConfirmation(
    int PolicyVersion,
    bool TrustedLanConfirmed,
    bool LinuxFirewallConfirmed,
    bool PrivilegedHarnessConfirmed);

public sealed record Rename(TargetId TargetId, string? DisplayName) : TargetCommand;

public sealed record SetDefault(TargetId TargetId) : TargetCommand;

public sealed record Pair(TargetId TargetId, PairingLinkSecret Link) : TargetCommand;

public sealed record PrepareOpen(TargetId TargetId) : TargetCommand;

public sealed record InvalidateSession(TargetId TargetId) : TargetCommand;

public sealed record Forget(
    TargetId TargetId,
    long ExpectedRevision,
    TargetId? SuccessorTargetId = null) : TargetCommand;

public readonly record struct TargetId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CandidateId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record TargetEndpoint
{
    internal TargetEndpoint(string ipv4, int port)
    {
        Ipv4 = ipv4;
        Port = port;
        Origin = $"http://{ipv4}:{port}";
        Authority = $"{ipv4}:{port}";
    }

    public string Ipv4 { get; }

    public int Port { get; }

    public string Origin { get; }

    public string Authority { get; }
}

public enum CatalogAvailability
{
    Ready,
    UnknownSchema,
    Unrecoverable,
    Unavailable,
}

public enum TargetSessionState
{
    PendingPairing,
    Pairing,
    Paired,
}

public sealed record CandidateView(CandidateId CandidateId, TargetEndpoint Endpoint);

public sealed record TargetView(
    TargetId TargetId,
    TargetEndpoint Endpoint,
    string? DisplayName,
    string EffectiveDisplayName,
    bool IsDefault,
    int TrustPolicyVersion,
    DateTimeOffset TrustedAtUtc,
    TargetSessionState SessionState);

public sealed record TargetManagerSnapshot(
    CatalogAvailability Availability,
    int? SchemaVersion,
    long Revision,
    IReadOnlyList<TargetView> Targets,
    TargetId? DefaultTargetId,
    TargetError? Problem)
{
    internal static TargetManagerSnapshot Uninitialized { get; } = new(
        CatalogAvailability.Unavailable,
        null,
        0,
        Array.Empty<TargetView>(),
        null,
        new TargetError(TargetErrorCode.CatalogUnavailable, TargetErrorScope.Catalog, true));
}

public abstract record TargetCommandOutcome;

public sealed record CandidateInspected(CandidateView Candidate) : TargetCommandOutcome;

public sealed record TargetConfirmed(TargetId TargetId, bool WasExisting) : TargetCommandOutcome;

public sealed record TargetRenamed(TargetId TargetId) : TargetCommandOutcome;

public sealed record DefaultTargetChanged(TargetId TargetId) : TargetCommandOutcome;

public sealed record TargetPaired(TargetId TargetId) : TargetCommandOutcome;

public enum OpenDisposition
{
    Ready,
    PairingRequired,
}

public sealed record OpenPrepared(TargetId TargetId, OpenDisposition Disposition) : TargetCommandOutcome;

public sealed record SessionInvalidated(TargetId TargetId) : TargetCommandOutcome;

public sealed record TargetForgotten(TargetId TargetId) : TargetCommandOutcome;

public sealed record TargetCommandResult(
    bool Succeeded,
    TargetManagerSnapshot Snapshot,
    TargetCommandOutcome? Outcome,
    TargetError? Error)
{
    internal static TargetCommandResult Success(TargetManagerSnapshot snapshot, TargetCommandOutcome outcome) =>
        new(true, snapshot, outcome, null);

    internal static TargetCommandResult Failure(TargetManagerSnapshot snapshot, TargetError error) =>
        new(false, snapshot, null, error);
}

public enum TargetErrorCode
{
    InvalidIpv4,
    InvalidPort,
    NetworkNotPrivate,
    CandidateNotFound,
    TrustConfirmationRequired,
    TrustPolicyOutdated,
    TargetNotFound,
    SuccessorRequired,
    InvalidSuccessor,
    SnapshotStale,
    ProbeForbidden,
    ProbeUnreachable,
    ProbeTimedOut,
    HarnessNotRunning,
    DefaultPortOccupied,
    WrongService,
    LegacyUnauthenticatedHarness,
    UnsupportedHarness,
    PairingLinkInvalid,
    PairingLinkWrongTarget,
    AlreadyPaired,
    PairingRejected,
    PairingIncomplete,
    SessionForbidden,
    SessionUnavailable,
    SessionCleanupFailed,
    CatalogUnknownSchema,
    CatalogCorrupt,
    CatalogUnavailable,
    StorageFailure,
    RuntimeFailure,
    CommandUnsupported,
    Cancelled,
}

public enum TargetErrorScope
{
    Command,
    Catalog,
    Candidate,
    Target,
    Network,
    Probe,
    Pairing,
    Session,
}

public sealed record TargetError(TargetErrorCode Code, TargetErrorScope Scope, bool Retryable);
