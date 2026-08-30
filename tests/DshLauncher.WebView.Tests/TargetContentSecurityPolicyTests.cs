using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06,third-party-ui")]
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
    [InlineData("http://192.168.10.20:3080/assets/app.js", TargetContentResourceKind.Script, true)]
    [InlineData("http://192.168.10.20:3080/api/session", TargetContentResourceKind.Fetch, true)]
    [InlineData("ws://192.168.10.20:3080/sidebar/ws/terminal", TargetContentResourceKind.Websocket, true)]
    [InlineData("ws://192.168.10.20:3180/sidebar/ws/terminal", TargetContentResourceKind.Websocket, false)]
    [InlineData("ws://192.168.10.21:3080/sidebar/ws/terminal", TargetContentResourceKind.Websocket, false)]
    [InlineData("wss://192.168.10.20:3080/sidebar/ws/terminal", TargetContentResourceKind.Websocket, false)]
    [InlineData("https://dsh-market.com/manifest/skins.json", TargetContentResourceKind.Fetch, true)]
    [InlineData("https://dsh-market.com/assets/skin.webp", TargetContentResourceKind.Image, true)]
    [InlineData("https://challenges.cloudflare.com/turnstile/v0/api.js", TargetContentResourceKind.Script, true)]
    [InlineData("https://challenges.cloudflare.com/cdn-cgi/challenge-platform/style.css", TargetContentResourceKind.Stylesheet, true)]
    [InlineData("data:image/svg+xml,%3Csvg%3E%3C/svg%3E", TargetContentResourceKind.Image, true)]
    [InlineData("blob:http://192.168.10.20:3080/0d5d28f2-8081-4203-a80f-25a8f51ebaf5", TargetContentResourceKind.Media, true)]
    [InlineData("ws://192.168.10.20:3080/sidebar/ws/terminal", TargetContentResourceKind.Fetch, false)]
    [InlineData("https://dsh-market.com/manifest/skins.json", TargetContentResourceKind.Script, false)]
    [InlineData("https://dsh-market.com/app.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://dsh-market.com/.webmcp/bridge.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://dsh-market.com/cdn-cgi/challenge-platform/scripts/jsd/main.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://dsh-market.com/mcp", TargetContentResourceKind.Fetch, false)]
    [InlineData("https://dsh-market.com/.webmcp/rpc/call", TargetContentResourceKind.Fetch, false)]
    [InlineData("https://challenges.cloudflare.com/arbitrary.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://challenges.cloudflare.com/arbitrary.css", TargetContentResourceKind.Stylesheet, false)]
    [InlineData("data:text/javascript,alert(1)", TargetContentResourceKind.Script, false)]
    [InlineData("http://192.168.10.20:3180/assets/app.js", TargetContentResourceKind.Script, false)]
    [InlineData("http://192.168.10.21:3080/assets/app.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://192.168.10.20:3080/assets/app.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://cdn.example.com/app.js", TargetContentResourceKind.Script, false)]
    [InlineData("https://qt.gtimg.cn/q=sh000001", TargetContentResourceKind.Script, false)]
    [InlineData("blob:https://example.com/0d5d28f2-8081-4203-a80f-25a8f51ebaf5", TargetContentResourceKind.Media, false)]
    [InlineData("file:///C:/Windows/win.ini", TargetContentResourceKind.Other, false)]
    public void SubresourcesUseTheDshWebCompatibilityAllowlist(
        string resource,
        TargetContentResourceKind resourceKind,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreatePolicy().AllowsResource(
                new Uri(resource),
                resourceKind));
    }

    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("about:srcdoc", false)]
    [InlineData("http://192.168.10.20:3080/api/skin-center/we/web/token/", true)]
    [InlineData("http://192.168.10.20:3080/api/skin-center/we/scene-runtime/token", true)]
    [InlineData("http://192.168.10.20:3080/sidebar/html/token", true)]
    [InlineData("blob:http://192.168.10.20:3080/0d5d28f2-8081-4203-a80f-25a8f51ebaf5", true)]
    [InlineData("http://192.168.10.20:3080/api/session", false)]
    [InlineData("https://dsh-market.com/api/turnstile/challenge", true)]
    [InlineData("https://dsh-market.com/preview.html", false)]
    [InlineData("https://challenges.cloudflare.com/turnstile/v0/", true)]
    [InlineData("https://challenges.cloudflare.com/cdn-cgi/challenge-platform/frame", true)]
    [InlineData("https://challenges.cloudflare.com/arbitrary", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("blob:https://example.com/0d5d28f2-8081-4203-a80f-25a8f51ebaf5", false)]
    public void FramesUseTheDshWebCompatibilityAllowlist(
        string destination,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreatePolicy().AllowsFrameNavigation(new Uri(destination)));
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
