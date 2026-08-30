using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class TargetContentSecurityPolicyTests
{
    private static readonly Guid TargetId =
        Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");

    [Fact]
    public void SameOriginRoutesRemainInsideTheTargetContentHost()
    {
        var policy = CreatePolicy();

        var decision = policy.EvaluateNavigation(
            new Uri("http://192.168.10.20:3080/session/42?view=chat"),
            isUserInitiated: false);

        Assert.Equal(TargetNavigationDisposition.AllowInTarget, decision.Disposition);
        Assert.Equal("192.168.10.20:3080", decision.DisplayTarget);
    }

    [Theory]
    [InlineData("http://192.168.10.20:3180/")]
    [InlineData("http://192.168.10.21:3080/")]
    [InlineData("https://192.168.10.20:3080/")]
    public void ExactOriginChangesNeverNavigateInsideTheTargetContentHost(
        string destination)
    {
        var policy = CreatePolicy();

        var decision = policy.EvaluateNavigation(
            new Uri(destination),
            isUserInitiated: false);

        Assert.Equal(TargetNavigationDisposition.Block, decision.Disposition);
    }

    [Theory]
    [InlineData("http://192.168.10.20:3080/assets/app.js", true)]
    [InlineData("http://192.168.10.20:3080/api/session", true)]
    [InlineData("http://192.168.10.20:3180/assets/app.js", false)]
    [InlineData("http://192.168.10.21:3080/assets/app.js", false)]
    [InlineData("https://192.168.10.20:3080/assets/app.js", false)]
    [InlineData("https://cdn.example.com/app.js", false)]
    [InlineData("data:text/plain,blocked", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void SubresourcesAreConfinedToTheExactBoundOrigin(
        string resource,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreatePolicy().AllowsResource(new Uri(resource)));
    }

    [Theory]
    [InlineData("https://example.com/docs", TargetExternalNavigationKind.HttpOrHttps)]
    [InlineData("mailto:operator@example.com", TargetExternalNavigationKind.Mailto)]
    public void UserInitiatedExternalDestinationsRequireExplicitConfirmation(
        string destination,
        TargetExternalNavigationKind expectedKind)
    {
        var policy = CreatePolicy();

        var decision = policy.EvaluateNavigation(
            new Uri(destination),
            isUserInitiated: true);

        Assert.Equal(
            TargetNavigationDisposition.RequireExternalConfirmation,
            decision.Disposition);
        Assert.Equal(expectedKind, decision.ExternalKind);
    }

    [Theory]
    [InlineData("https://example.com/docs")]
    [InlineData("mailto:operator@example.com")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/plain,hello")]
    [InlineData("javascript:alert(1)")]
    [InlineData("custom-protocol:payload")]
    public void NonUserInitiatedOrUnsupportedDestinationsAreBlocked(
        string destination)
    {
        var policy = CreatePolicy();

        var decision = policy.EvaluateNavigation(
            new Uri(destination),
            isUserInitiated: false);

        Assert.Equal(TargetNavigationDisposition.Block, decision.Disposition);
    }

    [Fact]
    public void PublishedRuntimePolicyDeniesEveryNativeCapability()
    {
        var runtime = CreatePolicy().RuntimePolicy;

        Assert.False(runtime.AllowDownloads);
        Assert.False(runtime.AllowPermissions);
        Assert.False(runtime.AllowCertificateErrors);
        Assert.False(runtime.AllowDevTools);
        Assert.False(runtime.AllowDefaultContextMenus);
        Assert.False(runtime.AllowHostObjects);
        Assert.False(runtime.AllowWebMessages);
        Assert.False(runtime.AllowBrowserNavigationShortcuts);
        Assert.True(runtime.AllowPageEditing);
        Assert.True(runtime.AllowFindInPage);
        Assert.True(runtime.AllowZoom);
    }

    [Fact]
    public void TargetBindingRequiresAStableAbsoluteUserDataFolder()
    {
        Assert.Throws<ArgumentException>(() => new TargetContentBinding(
            TargetId,
            new Uri("http://192.168.10.20:3080/"),
            "relative\\udf"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:3080/")]
    [InlineData("http://8.8.8.8:3080/")]
    [InlineData("http://169.254.1.2:3080/")]
    public void TargetBindingRejectsOriginsOutsideRfc1918(string origin)
    {
        Assert.Throws<ArgumentException>(() => new TargetContentBinding(
            TargetId,
            new Uri(origin),
            @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf"));
    }

    private static TargetContentSecurityPolicy CreatePolicy()
    {
        var binding = new TargetContentBinding(
            TargetId,
            new Uri("http://192.168.10.20:3080/"),
            @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

        return new TargetContentSecurityPolicy(binding);
    }
}
