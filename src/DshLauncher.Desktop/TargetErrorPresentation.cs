using DshLauncher.Core;
using DshLauncher.Desktop.Resources;
using DshLauncher.Desktop.ViewModels;

namespace DshLauncher.Desktop;

internal static class TargetErrorPresentation
{
    public static LauncherPresentationException CreateException(TargetError? error) =>
        new(Map(error?.Code ?? TargetErrorCode.RuntimeFailure));

    private static string Map(TargetErrorCode code) => code switch
    {
        TargetErrorCode.InvalidIpv4 => Strings.InvalidIpv4,
        TargetErrorCode.InvalidPort => Strings.InvalidPort,
        TargetErrorCode.NetworkNotPrivate => Strings.PublicNetworkBlocked,
        TargetErrorCode.HarnessNotRunning => Strings.HarnessNotRunning,
        TargetErrorCode.DefaultPortOccupied => Strings.DefaultPortOccupied,
        TargetErrorCode.WrongService => Strings.WrongService,
        TargetErrorCode.LegacyUnauthenticatedHarness => Strings.LegacyUnauthenticatedHarness,
        TargetErrorCode.UnsupportedHarness => Strings.UnsupportedHarness,
        TargetErrorCode.ProbeForbidden => Strings.ProbeForbidden,
        TargetErrorCode.ProbeUnreachable => Strings.ProbeUnreachable,
        TargetErrorCode.ProbeTimedOut => Strings.ProbeTimedOut,
        TargetErrorCode.SessionForbidden => Strings.SessionForbidden,
        TargetErrorCode.SessionUnavailable => Strings.TargetUnavailable,
        TargetErrorCode.PairingLinkInvalid => Strings.PairingLinkInvalid,
        TargetErrorCode.PairingLinkWrongTarget => Strings.PairingLinkWrongTarget,
        TargetErrorCode.PairingRejected => Strings.PairingRejected,
        TargetErrorCode.PairingIncomplete => Strings.PairingIncomplete,
        TargetErrorCode.CatalogUnknownSchema => Strings.CatalogUnknownSchema,
        TargetErrorCode.CatalogCorrupt or TargetErrorCode.CatalogUnavailable => Strings.CatalogUnavailable,
        TargetErrorCode.SessionCleanupFailed => Strings.SessionCleanupFailed,
        TargetErrorCode.SuccessorRequired => Strings.SuccessorRequired,
        TargetErrorCode.SnapshotStale => Strings.SnapshotStale,
        _ => Strings.UnexpectedError,
    };
}
