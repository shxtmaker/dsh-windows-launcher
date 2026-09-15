using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Remote;

/// <summary>
/// D15 主文档来源规则：只有顶层文档能决定可信来源；子框架与子资源既不能授予也不能撤销能力；
/// 外部链接导航不继承文件能力。全部在 Linux 真实执行。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemotePageOriginPolicyTests
{
    private static readonly RemoteOrigin OurHost = Required("https://our.host/");

    [Theory]
    [InlineData("https://our.host/", "https://our.host")]
    [InlineData("https://our.host/session/42?x=1#frag", "https://our.host")]
    [InlineData("HTTPS://OUR.HOST.:443/deep", "https://our.host")]
    public void MainDocumentAtOurOriginIsTrusted(string url, string normalizedOrigin)
    {
        var verdict = RemotePageOriginPolicy.Evaluate(OurHost, url, RemoteNavigationKind.MainDocument);

        Assert.True(verdict.Trusted);
        Assert.True(verdict.MainDocument);
        Assert.Equal(RemoteOriginCodes.Trusted, verdict.Code);
        Assert.Equal(normalizedOrigin, verdict.Origin!.Normalized);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("https://our.host.evil.example/")]
    [InlineData("http://our.host/")]
    [InlineData("https://our.host:8443/")]
    [InlineData("https://sub.our.host/")]
    public void MainDocumentAtAnotherOriginIsExternal(string url)
    {
        var verdict = RemotePageOriginPolicy.Evaluate(OurHost, url, RemoteNavigationKind.MainDocument);

        Assert.False(verdict.Trusted);
        Assert.True(verdict.MainDocument);
        Assert.Equal(RemoteOriginCodes.External, verdict.Code);
        Assert.False(RemotePageOriginPolicy.IsTrustedMainDocument(OurHost, url));
    }

    [Fact]
    public void ARedirectFromOurOriginToAnotherOriginIsNotTrusted()
    {
        // 服务器重定向仍是一次主文档导航（WebView2 会带 IsRedirected=true 再发一次 NavigationStarting）。
        // 规则里没有"起点可信就继承"的捷径：判据只能是最终文档 URL。
        const string start = "https://our.host/login";
        const string destination = "https://our.host.evil.example/landing";

        Assert.True(RemotePageOriginPolicy.IsTrustedMainDocument(OurHost, start));
        var final = RemotePageOriginPolicy.Evaluate(OurHost, destination, RemoteNavigationKind.MainDocument);
        Assert.False(final.Trusted);
        Assert.Equal(RemoteOriginCodes.External, final.Code);
        Assert.False(RemotePageOriginPolicy.IsTrustedMainDocument(OurHost, destination));
    }

    [Theory]
    [InlineData(RemoteNavigationKind.ChildFrame)]
    [InlineData(RemoteNavigationKind.Subresource)]
    public void ChildFramesAndSubresourcesNeverGrantCapability(RemoteNavigationKind kind)
    {
        foreach (var url in new[] { "https://our.host/frame", "https://evil.example/frame", "https://our.host/x.js" })
        {
            var verdict = RemotePageOriginPolicy.Evaluate(OurHost, url, kind);

            Assert.False(verdict.Trusted, url);
            Assert.False(verdict.MainDocument, url);
            Assert.Equal(RemoteOriginCodes.NotMainDocument, verdict.Code);
            Assert.Null(verdict.Origin);
        }
    }

    [Theory]
    [InlineData("https://evil.example/popup")]
    [InlineData("https://our.host/popup")]
    [InlineData("http://our.host/popup")]
    public void ExternalLinksNeverInheritCapabilityEvenAtOurOwnOrigin(string url)
    {
        var verdict = RemotePageOriginPolicy.Evaluate(OurHost, url, RemoteNavigationKind.ExternalLink);

        Assert.False(verdict.Trusted, url);
        Assert.True(verdict.MainDocument, url);
        Assert.Equal(RemoteOriginCodes.ExternalNavigation, verdict.Code);
    }

    [Theory]
    [InlineData("http://evil.example@our.host/", RemoteOriginCodes.UserInfoRejected)]
    [InlineData("https://our.host%2Eevil.example/", RemoteOriginCodes.Invalid)]
    [InlineData("http://[::1].evil.example/", RemoteOriginCodes.Invalid)]
    [InlineData("file://our.host/x", RemoteOriginCodes.UnsupportedScheme)]
    [InlineData("about:blank", RemoteOriginCodes.UnsupportedScheme)]
    public void SpoofedMainDocumentUrlsAreRejectedWithDeterminateCodes(string url, string expectedCode)
    {
        var verdict = RemotePageOriginPolicy.Evaluate(OurHost, url, RemoteNavigationKind.MainDocument);

        Assert.False(verdict.Trusted);
        Assert.Equal(expectedCode, verdict.Code);
        Assert.False(RemotePageOriginPolicy.IsTrustedMainDocument(OurHost, url));
    }

    [Fact]
    public void AnUnparseableTrustedOriginFailsClosed()
    {
        var verdict = RemotePageOriginPolicy.Evaluate(null, "https://our.host/", RemoteNavigationKind.MainDocument);

        Assert.False(verdict.Trusted);
        Assert.Equal(RemoteOriginCodes.Invalid, verdict.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    public void MissingDocumentSourceIsNeverTrusted(string? documentSource)
    {
        var verdict = RemotePageOriginPolicy.Evaluate(OurHost, documentSource, RemoteNavigationKind.MainDocument);

        Assert.False(verdict.Trusted);
        Assert.False(RemotePageOriginPolicy.IsTrustedMainDocument(OurHost, documentSource));
    }

    private static RemoteOrigin Required(string url)
    {
        var parsed = RemoteOrigin.Parse(url);
        Assert.True(parsed.Ok, url);
        return parsed.Origin!;
    }
}
