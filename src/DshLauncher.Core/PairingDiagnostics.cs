namespace DshLauncher.Core;

public enum PairingDiagnosticStage
{
    PairingTransaction,
    MetadataMarkerWrite,
    UncommittedRuntimeReset,
    BrowserDataPreparation,
    BrowserSessionOpen,
    BrowserEnvironmentCreate,
    BrowserControllerInitialize,
    TokenNavigation,
    BrowsingHistoryCleanup,
    ResponseCspBoundary,
    CleanRootNavigation,
    AuthenticatedApiNavigation,
    BrowserSessionRelease,
    MetadataCommit,
    RollbackRuntimeDelete,
    RollbackMetadataDelete,
}

public enum PairingDiagnosticOutcome
{
    Started,
    Succeeded,
    Rejected,
    Incomplete,
    Failed,
    Cancelled,
}

public enum PairingFailureCategory
{
    Timeout,
    Cancelled,
    BrowserInitialization,
    Navigation,
    HistoryCleanup,
    OriginMismatch,
    BrowserProcessRelease,
    BrowserData,
    SessionMetadata,
    Unexpected,
}

public sealed record PairingDiagnosticEvent
{
    public PairingDiagnosticEvent(
        PairingDiagnosticStage stage,
        PairingDiagnosticOutcome outcome,
        PairingFailureCategory? failureCategory = null,
        int? httpStatusCode = null)
    {
        if (httpStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode));
        }

        Stage = stage;
        Outcome = outcome;
        FailureCategory = failureCategory;
        HttpStatusCode = httpStatusCode;
    }

    public PairingDiagnosticStage Stage { get; }

    public PairingDiagnosticOutcome Outcome { get; }

    public PairingFailureCategory? FailureCategory { get; }

    public int? HttpStatusCode { get; }
}

public interface IPairingDiagnosticSink
{
    ValueTask ReportAsync(
        PairingDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken);
}

public sealed class NullPairingDiagnosticSink : IPairingDiagnosticSink
{
    public static NullPairingDiagnosticSink Instance { get; } = new();

    private NullPairingDiagnosticSink()
    {
    }

    public ValueTask ReportAsync(
        PairingDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        return ValueTask.CompletedTask;
    }
}

public static class PairingDiagnosticSinkExtensions
{
    public static async ValueTask ReportSafelyAsync(
        this IPairingDiagnosticSink diagnosticSink,
        PairingDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        try
        {
            await diagnosticSink.ReportAsync(diagnosticEvent, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are fail-open and must never change pairing behavior.
        }
    }
}
