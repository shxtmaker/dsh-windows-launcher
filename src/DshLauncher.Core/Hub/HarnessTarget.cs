using DshLauncher.Core.Pairing;

namespace DshLauncher.Core.Hub;

/// <summary>Pairing lifecycle of one target as the hub sees it.</summary>
public enum PairingState
{
    /// <summary>The endpoint is known but no device credential exists yet.</summary>
    AwaitingPairing,

    /// <summary>A pairing accept round trip is in flight.</summary>
    Pairing,

    /// <summary>The hub holds a device credential accepted by the host.</summary>
    Paired,

    /// <summary>The host no longer knows the device credential (revoked/stopped/expired).</summary>
    Revoked,
}

/// <summary>Runtime connectivity of one target, derived from keep-alive heartbeats.</summary>
public enum ConnectivityState
{
    Unknown,
    Online,
    Offline,
}

/// <summary>Persisted facts for one harness target. The device credential is
/// part of the persisted document: it is the hub's pairing identity on the
/// host and must survive restarts just like the host-side devices file.</summary>
public sealed record StoredTarget(
    Guid TargetId,
    string BaseUrl,
    string? DisplayName,
    string? CookieName,
    string? DeviceId,
    DateTimeOffset? PairedAtUtc,
    bool KeepAliveEnabled)
{
    public bool HasCredential => CookieName is not null && DeviceId is not null;
}

/// <summary>The persisted hub document: a versioned list of targets.</summary>
public sealed record StoredHubDocument(int SchemaVersion, IReadOnlyList<StoredTarget> Targets)
{
    public const int CurrentSchemaVersion = 1;

    public static StoredHubDocument Empty { get; } = new(CurrentSchemaVersion, Array.Empty<StoredTarget>());
}

/// <summary>
/// Runtime view of one target for the web page: pairing state plus live
/// keep-alive facts. The device credential itself never appears here; the
/// web API only exposes a derived remote UI URL on explicit request.
/// </summary>
public sealed record TargetSnapshot(
    Guid TargetId,
    string BaseUrl,
    string? DisplayName,
    PairingState Pairing,
    ConnectivityState Connectivity,
    bool KeepAliveEnabled,
    bool HasCredential,
    DateTimeOffset? PairedAtUtc,
    DateTimeOffset? LastHeartbeatUtc,
    int ConsecutiveFailures,
    string? LastFailure,
    int? LastStatusCode)
{
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? BaseUrl : DisplayName!;
}

/// <summary>Whole-hub view pushed to the web page.</summary>
public sealed record HubSnapshot(
    DateTimeOffset GeneratedAtUtc,
    TimeSpan HeartbeatInterval,
    bool KeepAliveRunning,
    IReadOnlyList<TargetSnapshot> Targets);
