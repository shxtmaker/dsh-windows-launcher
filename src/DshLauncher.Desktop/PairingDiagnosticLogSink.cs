using DshLauncher.Core;
using DshLauncher.Platform.Windows;

namespace DshLauncher.Desktop;

internal sealed class PairingDiagnosticLogSink : IPairingDiagnosticSink
{
    private readonly RollingDiagnosticLog _log;

    public PairingDiagnosticLogSink(RollingDiagnosticLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public ValueTask ReportAsync(
        PairingDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        return _log.WriteAsync(
            DiagnosticRecord.Create(
                DateTimeOffset.UtcNow,
                DiagnosticEventCode.PairingStateChanged,
                diagnosticEvent.HttpStatusCode,
                CreateClassification(diagnosticEvent),
                normalizedPrivateEndpoint: null,
                includePrivateEndpoint: false),
            cancellationToken);
    }

    internal static string CreateClassification(PairingDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var classification = $"pairing.{StageCode(diagnosticEvent.Stage)}.{OutcomeCode(diagnosticEvent.Outcome)}";
        return diagnosticEvent.FailureCategory is { } category
            ? $"{classification}.{CategoryCode(category)}"
            : classification;
    }

    private static string StageCode(PairingDiagnosticStage stage) => stage switch
    {
        PairingDiagnosticStage.PairingTransaction => "transaction",
        PairingDiagnosticStage.MetadataMarkerWrite => "metadata-marker-write",
        PairingDiagnosticStage.UncommittedRuntimeReset => "runtime-reset",
        PairingDiagnosticStage.BrowserDataPreparation => "browser-data-prepare",
        PairingDiagnosticStage.BrowserSessionOpen => "browser-session-open",
        PairingDiagnosticStage.BrowserEnvironmentCreate => "browser-environment-create",
        PairingDiagnosticStage.BrowserControllerInitialize => "browser-controller-initialize",
        PairingDiagnosticStage.TokenNavigation => "credential-navigation",
        PairingDiagnosticStage.BrowsingHistoryCleanup => "history-cleanup",
        PairingDiagnosticStage.ResponseCspBoundary => "response-csp-boundary",
        PairingDiagnosticStage.CleanRootNavigation => "clean-root-navigation",
        PairingDiagnosticStage.AuthenticatedApiNavigation => "authenticated-api-navigation",
        PairingDiagnosticStage.BrowserSessionRelease => "browser-session-release",
        PairingDiagnosticStage.MetadataCommit => "metadata-commit",
        PairingDiagnosticStage.RollbackRuntimeDelete => "rollback-runtime-delete",
        PairingDiagnosticStage.RollbackMetadataDelete => "rollback-metadata-delete",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static string OutcomeCode(PairingDiagnosticOutcome outcome) => outcome switch
    {
        PairingDiagnosticOutcome.Started => "started",
        PairingDiagnosticOutcome.Succeeded => "succeeded",
        PairingDiagnosticOutcome.Rejected => "rejected",
        PairingDiagnosticOutcome.Incomplete => "incomplete",
        PairingDiagnosticOutcome.Failed => "failed",
        PairingDiagnosticOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string CategoryCode(PairingFailureCategory category) => category switch
    {
        PairingFailureCategory.Timeout => "timeout",
        PairingFailureCategory.Cancelled => "cancelled",
        PairingFailureCategory.BrowserInitialization => "browser-initialization",
        PairingFailureCategory.Navigation => "navigation",
        PairingFailureCategory.HistoryCleanup => "history-cleanup",
        PairingFailureCategory.OriginMismatch => "origin-mismatch",
        PairingFailureCategory.BrowserProcessRelease => "browser-process-release",
        PairingFailureCategory.BrowserData => "browser-data",
        PairingFailureCategory.SessionMetadata => "session-metadata",
        PairingFailureCategory.Unexpected => "unexpected",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}
