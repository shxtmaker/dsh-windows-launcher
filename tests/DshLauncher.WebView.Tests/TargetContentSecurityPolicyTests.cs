using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06,third-party-ui")]
public sealed class TargetContentSecurityPolicyTests
{
    [Fact]
    public void SameOriginRoutesRemainInsideTheTargetContentHost()
    {
        var decision = CreateBasePolicy().EvaluateNavigation(
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
        var decision = CreateBasePolicy().EvaluateNavigation(
            new Uri(destination),
            isUserInitiated: false);

        Assert.Equal(TargetNavigationDisposition.Block, decision.Disposition);
    }

    [Theory]
    [InlineData("http://192.168.10.20:3080/assets/app.js", TargetContentResourceKind.Script, "GET", true)]
    [InlineData("http://192.168.10.20:3080/api/session", TargetContentResourceKind.Fetch, "POST", true)]
    [InlineData("ws://192.168.10.20:3080/socket", TargetContentResourceKind.Websocket, "GET", false)]
    [InlineData("data:image/png;base64,AA==", TargetContentResourceKind.Image, "GET", true)]
    [InlineData("data:text/javascript,alert(1)", TargetContentResourceKind.Script, "GET", false)]
    [InlineData("blob:http://192.168.10.20:3080/id", TargetContentResourceKind.Image, "GET", true)]
    [InlineData("blob:http://192.168.10.20:3080/id", TargetContentResourceKind.Media, "GET", true)]
    [InlineData("blob:http://192.168.10.20:3080/id", TargetContentResourceKind.Fetch, "GET", true)]
    [InlineData("blob:http://192.168.10.20:3080/id", TargetContentResourceKind.Script, "GET", false)]
    [InlineData("blob:https://example.com/id", TargetContentResourceKind.Media, "GET", false)]
    [InlineData("file:///C:/Windows/win.ini", TargetContentResourceKind.Other, "GET", false)]
    public void BaseSnapshotEnforcesOnlyContractCapabilities(
        string resource,
        TargetContentResourceKind resourceKind,
        string method,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreateBasePolicy().AllowsResource(
                new Uri(resource),
                resourceKind,
                method));
    }

    [Theory]
    [InlineData("https://assets.example/theme/main.css", TargetContentResourceKind.Stylesheet, "GET", true)]
    [InlineData("https://assets.example/theme/icon.png", TargetContentResourceKind.Image, "HEAD", true)]
    [InlineData("https://assets.example/theme", TargetContentResourceKind.Image, "GET", false)]
    [InlineData("https://assets.example/theme2/icon.png", TargetContentResourceKind.Image, "GET", false)]
    [InlineData("https://assets.example/theme/%252e%252e/secret", TargetContentResourceKind.Image, "GET", false)]
    [InlineData("https://assets.example/theme/app.js", TargetContentResourceKind.Script, "GET", false)]
    [InlineData("https://assets.example/theme/icon.png", TargetContentResourceKind.Image, "POST", false)]
    [InlineData("https://api.example/v1/items", TargetContentResourceKind.Fetch, "POST", true)]
    [InlineData("https://api.example/v1/items", TargetContentResourceKind.XmlHttpRequest, "OPTIONS", true)]
    [InlineData("https://api.example/v1/items?cursor=1&mode=full", TargetContentResourceKind.Fetch, "GET", true)]
    [InlineData("https://api.example/v1/items?unknown=1", TargetContentResourceKind.Fetch, "GET", false)]
    [InlineData("https://api.example/v1/items?cursor=1&cursor=2", TargetContentResourceKind.Fetch, "GET", false)]
    [InlineData("https://api.example/v1/items?cursor", TargetContentResourceKind.Fetch, "GET", false)]
    [InlineData("https://api.example/v1/items?%63ursor=1", TargetContentResourceKind.Fetch, "GET", false)]
    [InlineData("https://api.example/v1/items#fragment", TargetContentResourceKind.Fetch, "GET", false)]
    [InlineData("https://api.example/v2/items", TargetContentResourceKind.Fetch, "GET", false)]
    [InlineData("https://assets.example/theme/icon.png?size=2", TargetContentResourceKind.Image, "GET", false)]
    [InlineData("https://frame.example/challenge", TargetContentResourceKind.Document, "GET", true)]
    public void ExtendedSnapshotEnforcesExactOriginPathMethodAndResourceKind(
        string resource,
        TargetContentResourceKind resourceKind,
        string method,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreateExtendedPolicy().AllowsResource(
                new Uri(resource),
                resourceKind,
                method));
    }

    [Theory]
    [InlineData("ws://192.168.10.20:3080/remote/api/remote.mux", "GET", true)]
    [InlineData("ws://192.168.10.20:3080/remote/api/remote.mux?device=abc", "GET", true)]
    [InlineData("ws://192.168.10.20:3080/remote/sidebar/ws/terminal", "GET", true)]
    [InlineData("ws://192.168.10.20:3080/remote/sidebar/ws/agent-terminals", "GET", true)]
    [InlineData("ws://192.168.10.20:3080/remote/api/dsh-ssh/terminal", "GET", true)]
    [InlineData("ws://192.168.10.20:3080/remote/api/remote.mux/child", "GET", false)]
    [InlineData("ws://192.168.10.20:3080/remote/other", "GET", false)]
    [InlineData("ws://192.168.10.20:3180/remote/api/remote.mux", "GET", false)]
    [InlineData("ws://192.168.10.21:3080/remote/api/remote.mux", "GET", false)]
    [InlineData("wss://192.168.10.20:3080/remote/api/remote.mux", "GET", false)]
    [InlineData("ws://192.168.10.20:3080/remote/api/remote.mux?token=x", "GET", false)]
    [InlineData("ws://192.168.10.20:3080/remote/api/remote.mux?device=a&device=b", "GET", false)]
    [InlineData("ws://192.168.10.20:3080/remote/api/remote.mux", "POST", false)]
    public void RemoteUiSnapshotAllowsOnlyReviewedTargetWebSocketPaths(
        string resource,
        string method,
        bool expected)
    {
        var policy = new TargetContentSecurityPolicy(
            WebViewCompatibilityFixture.CreateBinding(),
            WebViewCompatibilityFixture.CreateRemoteUiResolution().Snapshot);

        Assert.Equal(
            expected,
            policy.AllowsResource(
                new Uri(resource),
                TargetContentResourceKind.Websocket,
                method));
    }

    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("about:srcdoc", false)]
    [InlineData("http://192.168.10.20:3080/frame", true)]
    [InlineData("https://frame.example/challenge", true)]
    [InlineData("https://frame.example/challenge/child", false)]
    [InlineData("https://example.com/", false)]
    public void FramesConsumeOnlyTheActivatedSnapshot(
        string destination,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreateExtendedPolicy().AllowsFrameNavigation(new Uri(destination)));
    }

    [Theory]
    [InlineData("http://192.168.10.20:3080/", "https://frame.example/challenge", true)]
    [InlineData("https://outer.example/", "https://frame.example/challenge", false)]
    [InlineData("https://outer.example/", "http://192.168.10.20:3080/frame", false)]
    [InlineData("https://outer.example/", "about:blank", false)]
    public void FrameGrantsRequireTheActualCommittedParentOrigin(
        string parent,
        string destination,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreateExtendedPolicy().AllowsFrameNavigation(
                new Uri(destination),
                new Uri(parent)));
    }

    [Theory]
    [InlineData("https://example.com/docs", TargetExternalNavigationKind.HttpOrHttps)]
    [InlineData("mailto:operator@example.com", TargetExternalNavigationKind.Mailto)]
    public void UserInitiatedExternalDestinationsRequireExplicitConfirmation(
        string destination,
        TargetExternalNavigationKind expectedKind)
    {
        var decision = CreateBasePolicy().EvaluateNavigation(
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
        var decision = CreateBasePolicy().EvaluateNavigation(
            new Uri(destination),
            isUserInitiated: false);

        Assert.Equal(TargetNavigationDisposition.Block, decision.Disposition);
    }

    [Fact]
    public void RuntimePolicyComesFromTheBaseCapabilitySnapshot()
    {
        var runtime = CreateBasePolicy().RuntimePolicy;

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
            WebViewCompatibilityFixture.TargetId,
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
            WebViewCompatibilityFixture.TargetId,
            new Uri(origin),
            @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf"));
    }

    private static TargetContentSecurityPolicy CreateBasePolicy() => new(
        WebViewCompatibilityFixture.CreateBinding(),
        WebViewCompatibilityFixture.CreateBaseSnapshot());

    private static TargetContentSecurityPolicy CreateExtendedPolicy() => new(
        WebViewCompatibilityFixture.CreateBinding(),
        WebViewCompatibilityFixture.CreateExtendedResolution().Snapshot);
}
