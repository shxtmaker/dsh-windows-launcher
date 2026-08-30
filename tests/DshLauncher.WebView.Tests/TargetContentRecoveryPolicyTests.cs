using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class TargetContentRecoveryPolicyTests
{
    [Theory]
    [InlineData(1, TargetRecoveryAction.Reload)]
    [InlineData(2, TargetRecoveryAction.RecreateWebView)]
    [InlineData(3, TargetRecoveryAction.Fail)]
    public void RendererRecoveryIsFinite(
        int consecutiveFailures,
        TargetRecoveryAction expected)
    {
        var action = TargetContentRecoveryPolicy.Default.Decide(
            TargetProcessFailureKind.Renderer,
            consecutiveFailures);

        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(1, TargetRecoveryAction.RecreateEnvironment)]
    [InlineData(2, TargetRecoveryAction.Fail)]
    public void BrowserRecoveryReleasesAndRecreatesTheEnvironmentOnce(
        int consecutiveFailures,
        TargetRecoveryAction expected)
    {
        var action = TargetContentRecoveryPolicy.Default.Decide(
            TargetProcessFailureKind.Browser,
            consecutiveFailures);

        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(1, TargetRecoveryAction.Reload)]
    [InlineData(2, TargetRecoveryAction.RecreateWebView)]
    [InlineData(3, TargetRecoveryAction.Fail)]
    public void UnresponsiveRecoveryNeverIntroducesBusinessReplay(
        int consecutiveFailures,
        TargetRecoveryAction expected)
    {
        var action = TargetContentRecoveryPolicy.Default.Decide(
            TargetProcessFailureKind.Unresponsive,
            consecutiveFailures);

        Assert.Equal(expected, action);
        Assert.Contains(
            action,
            new[]
            {
                TargetRecoveryAction.Reload,
                TargetRecoveryAction.RecreateWebView,
                TargetRecoveryAction.Fail,
            });
    }
}
