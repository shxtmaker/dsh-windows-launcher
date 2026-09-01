namespace DshLauncher.Core.Pairing;

/// <summary>
/// Wire facts of the "DSH 远程访问" (dsh-remote-web-ui) pairing contract the
/// hub depends on: exact paths, the default device cookie name and the
/// host-side presence window that keep-alive heartbeats must stay under.
/// </summary>
public static class PairingProtocol
{
    /// <summary>The top-level QR entry page the pairing link points at.</summary>
    public const string AcceptPagePath = "/pair-accept";

    /// <summary>POST: exchange a one-time pairing token for a device id.</summary>
    public const string AcceptPath = "/api/pair/accept";

    /// <summary>POST: refresh device presence; requires the device cookie.</summary>
    public const string HeartbeatPath = "/api/pair/heartbeat";

    /// <summary>GET: pairing status as seen by the host panel.</summary>
    public const string StatusPath = "/api/pair/status";

    /// <summary>The cookieless device credential header used by paired clients.</summary>
    public const string DeviceCredentialHeader = "x-dsh-remote-device";

    /// <summary>Default device cookie name (host config may override it; the
    /// accept response's Set-Cookie always wins).</summary>
    public const string DefaultCookieName = "dsh_pair";

    /// <summary>Host-side offline window (offlineAfterMs default): a device
    /// whose last heartbeat is older than this shows as offline.</summary>
    public static readonly TimeSpan HostOnlineWindow = TimeSpan.FromSeconds(25);
}

/// <summary>Identifies one harness web endpoint that runs the remote access plugin.</summary>
public sealed record HarnessEndpoint(Uri BaseUri)
{
    public override string ToString() => BaseUri.ToString().TrimEnd('/');
}

/// <summary>
/// The paired device credential: the cookie name captured from the accept
/// response plus the device id it carries. Heartbeats ride the cookie; the
/// remote UI URL reuses the device id.
/// </summary>
public sealed record DeviceCredential(string CookieName, string DeviceId);

/// <summary>Outcome of one pairing accept attempt.</summary>
public enum PairingAcceptStatus
{
    Paired,
    InvalidToken,
    TokenUsed,
    Forbidden,
    RateLimited,
    Unreachable,
    Failed,
}

public sealed record PairingAcceptOutcome(
    PairingAcceptStatus Status,
    DeviceCredential? Credential,
    int? StatusCode)
{
    public bool Retryable => Status is PairingAcceptStatus.Unreachable or PairingAcceptStatus.RateLimited or PairingAcceptStatus.Failed;
}

/// <summary>Outcome of one keep-alive heartbeat.</summary>
public enum HeartbeatStatus
{
    Alive,
    Unpaired,
    Unreachable,
    Failed,
}

public sealed record HeartbeatOutcome(HeartbeatStatus Status, int? StatusCode)
{
    public static HeartbeatOutcome Alive(int? statusCode = 200) => new(HeartbeatStatus.Alive, statusCode);

    public static HeartbeatOutcome Unpaired(int? statusCode = 401) => new(HeartbeatStatus.Unpaired, statusCode);

    public static HeartbeatOutcome Unreachable() => new(HeartbeatStatus.Unreachable, null);

    public static HeartbeatOutcome Failed(int? statusCode) => new(HeartbeatStatus.Failed, statusCode);
}

/// <summary>Outcome of one host status probe (GET /api/pair/status).</summary>
public enum RemoteStatusProbeKind
{
    Reached,
    Unreachable,
    Failed,
}

public sealed record RemoteStatusProbeOutcome(
    RemoteStatusProbeKind Kind,
    bool? Paired,
    string? Phase,
    bool? LanAvailable,
    int? StatusCode);

/// <summary>
/// The transport port the hub uses to speak the remote access pairing
/// protocol. Implemented over HTTP in Core; tests replace it with fakes.
/// </summary>
public interface IPairingTransport
{
    ValueTask<PairingAcceptOutcome> AcceptPairingAsync(
        HarnessEndpoint endpoint,
        string token,
        CancellationToken cancellationToken = default);

    ValueTask<HeartbeatOutcome> SendHeartbeatAsync(
        HarnessEndpoint endpoint,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteStatusProbeOutcome> ProbeStatusAsync(
        HarnessEndpoint endpoint,
        DeviceCredential? credential,
        CancellationToken cancellationToken = default);
}
