namespace DshLauncher.WebView;

public sealed class TargetContentRecoveryPolicy
{
    private readonly int _rendererReloadAttempt;
    private readonly int _rendererRecreateAttempt;
    private readonly int _browserRecreateAttempt;

    public TargetContentRecoveryPolicy(
        int rendererReloadAttempt = 1,
        int rendererRecreateAttempt = 2,
        int browserRecreateAttempt = 1)
    {
        _rendererReloadAttempt = rendererReloadAttempt;
        _rendererRecreateAttempt = rendererRecreateAttempt;
        _browserRecreateAttempt = browserRecreateAttempt;
    }

    public static TargetContentRecoveryPolicy Default { get; } = new();

    public TargetRecoveryAction Decide(
        TargetProcessFailureKind failureKind,
        int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailures),
                "A recovery decision requires at least one failure.");
        }

        return failureKind switch
        {
            TargetProcessFailureKind.Renderer or
            TargetProcessFailureKind.Unresponsive => consecutiveFailures switch
            {
                var attempt when attempt == _rendererReloadAttempt =>
                    TargetRecoveryAction.Reload,
                var attempt when attempt == _rendererRecreateAttempt =>
                    TargetRecoveryAction.RecreateWebView,
                _ => TargetRecoveryAction.Fail,
            },
            TargetProcessFailureKind.Browser =>
                consecutiveFailures == _browserRecreateAttempt
                ? TargetRecoveryAction.RecreateEnvironment
                : TargetRecoveryAction.Fail,
            _ => TargetRecoveryAction.Fail,
        };
    }
}

public enum TargetProcessFailureKind
{
    Renderer,
    Browser,
    Unresponsive,
    Other
}

public enum TargetRecoveryAction
{
    Reload,
    RecreateWebView,
    RecreateEnvironment,
    Fail
}
