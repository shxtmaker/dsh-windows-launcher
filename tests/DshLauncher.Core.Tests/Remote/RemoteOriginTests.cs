using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Remote;

/// <summary>
/// D15 来源规范化与比较规则。全部在 Linux 真实执行；本类里每一条反例都对应一种
/// 可能被"看起来像我们主机"的地址骗过的实现。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemoteOriginTests
{
    private static readonly RemoteOrigin OurHost = Required("https://our.host/");

    [Theory]
    [InlineData("https://our.host/", "https://our.host")]
    [InlineData("https://our.host", "https://our.host")]
    [InlineData("HTTPS://OUR.HOST:443/", "https://our.host")]
    [InlineData("http://our.host:80", "http://our.host")]
    [InlineData("http://our.host./", "http://our.host")]
    [InlineData("http://OUR.HOST.:8080", "http://our.host:8080")]
    [InlineData("http://our.host.:8080/deep/path?x=1#frag", "http://our.host:8080")]
    [InlineData("http://our.host:8080", "http://our.host:8080")]
    [InlineData("http://our.host:443/", "http://our.host:443")]
    [InlineData("https://our.host:80/", "https://our.host:80")]
    [InlineData("http://127.0.0.1:51234/x", "http://127.0.0.1:51234")]
    [InlineData("http://[::1]:80/", "http://[::1]")]
    [InlineData("http://[0:0:0:0:0:0:0:1]/", "http://[::1]")]
    [InlineData("http://[2001:DB8::1]:8443/x", "http://[2001:db8::1]:8443")]
    [InlineData("https://[2001:db8::1]/", "https://[2001:db8::1]")]
    public void NormalizationHandlesCaseDefaultPortsTrailingDotsAndIpv6Literals(string url, string expected)
    {
        var parsed = RemoteOrigin.Parse(url);

        Assert.True(parsed.Ok);
        Assert.Equal(RemoteOriginCodes.Valid, parsed.Code);
        Assert.Equal(expected, parsed.Origin!.Normalized);
    }

    [Theory]
    [InlineData("http://our.host/", "http://OUR.HOST:80/")]
    [InlineData("https://our.host/", "HTTPS://our.host.:443/")]
    [InlineData("http://[::1]/", "http://[0:0:0:0:0:0:0:1]:80/")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080/deep")]
    public void EquivalentFormsCompareAsTheSameOrigin(string left, string right) =>
        Assert.True(Required(left).Matches(Required(right)));

    [Theory]
    [InlineData("http://our.host/", "https://our.host/")]
    [InlineData("https://our.host/", "https://our.host:8443/")]
    [InlineData("http://our.host/", "http://our.host:8080/")]
    [InlineData("http://our.host/", "http://sub.our.host/")]
    [InlineData("http://our.host/", "http://our.host.evil.example/")]
    [InlineData("http://our.host/", "http://evil-our.host/")]
    [InlineData("http://our.host/", "http://our.hostx/")]
    [InlineData("http://our.host/", "http://evil.example/")]
    [InlineData("http://our.host/", "http://notour.host/")]
    public void DifferentSchemePortOrHostIsNotOurOrigin(string ours, string other) =>
        Assert.False(Required(ours).Matches(Required(other)));

    [Theory]
    [InlineData("http://evil.example@our.host/")]
    [InlineData("https://evil.example@our.host/")]
    [InlineData("https://user:secret@our.host/deep?x=1")]
    [InlineData("http://our.host@evil.example/")]
    public void UserInfoUrlsAreRejectedInsteadOfBeingTreatedAsOurHost(string url)
    {
        var parsed = RemoteOrigin.Parse(url);

        Assert.False(parsed.Ok);
        Assert.Equal(RemoteOriginCodes.UserInfoRejected, parsed.Code);
        Assert.Null(parsed.Origin);
        Assert.False(OurHost.Matches(parsed.Origin));
    }

    [Theory]
    [InlineData("http://our.host.evil.example/")]
    [InlineData("http://our.host.evil.example:80/")]
    [InlineData("http://our.host.evil.example./")]
    [InlineData("http://evil.example/?next=http://our.host/")]
    [InlineData("http://evil.example/#http://our.host/")]
    [InlineData("http://evil-our.host/")]
    [InlineData("http://our.hostx/")]
    [InlineData("http://xour.host/")]
    [InlineData("http://our.host:8080/")]
    [InlineData("http://sub.our.host/")]
    [InlineData("http://[::1]/")]
    public void LookalikeAddressesNeverMatchOurHost(string url)
    {
        var parsed = RemoteOrigin.Parse(url);

        Assert.True(parsed.Ok, url);
        Assert.False(OurHost.Matches(parsed.Origin), url);
    }

    [Theory]
    [InlineData("", RemoteOriginCodes.Invalid)]
    [InlineData("   ", RemoteOriginCodes.Invalid)]
    [InlineData("not a url", RemoteOriginCodes.Invalid)]
    [InlineData("our.host", RemoteOriginCodes.Invalid)]
    [InlineData("//our.host/x", RemoteOriginCodes.UnsupportedScheme)]
    [InlineData("http:///path", RemoteOriginCodes.Invalid)]
    [InlineData("about:blank", RemoteOriginCodes.UnsupportedScheme)]
    [InlineData("javascript:alert(1)", RemoteOriginCodes.UnsupportedScheme)]
    [InlineData("file://our.host/c$/x", RemoteOriginCodes.UnsupportedScheme)]
    [InlineData("data:text/html,<b>x</b>", RemoteOriginCodes.UnsupportedScheme)]
    [InlineData("http://our.host%2Eevil.example/", RemoteOriginCodes.Invalid)]
    [InlineData("http://[::1].evil.example/", RemoteOriginCodes.Invalid)]
    [InlineData("http://[::1]evil.example/", RemoteOriginCodes.Invalid)]
    [InlineData("http://[::1", RemoteOriginCodes.Invalid)]
    [InlineData("http://[not-an-ip]/", RemoteOriginCodes.Invalid)]
    [InlineData("http://our.host\\@evil.example/", RemoteOriginCodes.Invalid)]
    public void MalformedOrNonHttpUrlsAreRejectedWithDeterminateCodes(string url, string expectedCode)
    {
        var parsed = RemoteOrigin.Parse(url);

        Assert.False(parsed.Ok);
        Assert.Equal(expectedCode, parsed.Code);
        Assert.Null(parsed.Origin);
    }

    [Fact]
    public void HiddenJunkAfterAnIpv6LiteralIsRejectedInsteadOfBeingSilentlyDropped()
    {
        // .NET 的 Uri 会把 "http://[::1].evil.example/" 的 Host 解析成 "[::1]"（把后缀丢掉），
        // 若直接采用 Uri.Host，这个地址就会被当成我们的 ::1 页面。规范化必须对照原始 authority 复核。
        var parsed = RemoteOrigin.Parse("http://[::1].evil.example/");

        Assert.False(parsed.Ok);
        Assert.Equal(RemoteOriginCodes.Invalid, parsed.Code);
        Assert.False(OurHost.Matches(parsed.Origin));
    }

    [Fact]
    public void ParsingNeverThrowsForHostileInput()
    {
        string[] hostile =
        [
            "http://", "http://:", "http://@", "http://[]/", "http://[::1]:/", "http://[::1]:abc/",
            "http://our.host:99999999/", "http://our.host:abc/", "HTTP://", "\0",
        ];

        foreach (var url in hostile)
        {
            var parsed = RemoteOrigin.Parse(url);
            Assert.False(parsed.Ok, url);
            Assert.False(string.IsNullOrWhiteSpace(parsed.Code), url);
        }
    }

    private static RemoteOrigin Required(string url)
    {
        var parsed = RemoteOrigin.Parse(url);
        Assert.True(parsed.Ok, url);
        return parsed.Origin!;
    }
}
