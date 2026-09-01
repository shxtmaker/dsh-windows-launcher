using DshLauncher.Core.Pairing;
using Xunit;

namespace DshLauncher.Core.Tests;

[Trait("triggerTags", "VFY-05")]
public sealed class PairingLinkTests
{
    [Theory]
    [InlineData("http://192.168.10.8:3080/pair-accept?pair=abc123", "abc123", "http://192.168.10.8:3080/")]
    [InlineData("  http://192.168.10.8:3080/pair-accept?pair=abc123  ", "abc123", "http://192.168.10.8:3080/")]
    [InlineData("https://dsh-tunnel.example.com/pair-accept?pair=deadbeef", "deadbeef", "https://dsh-tunnel.example.com/")]
    [InlineData("http://host.lan/pair-accept?pair=token-with-dashes", "token-with-dashes", "http://host.lan/")]
    public void ParsesExactRemoteAccessPairingLinks(string input, string expectedToken, string expectedBase)
    {
        var link = PairingLink.Parse(input);

        Assert.Equal(expectedToken, link.Token);
        Assert.Equal(expectedBase, link.BaseUri.ToString());
    }

    [Fact]
    public void BaseUriDropsThePathAndKeepsTheExplicitPort()
    {
        var link = PairingLink.Parse("http://192.168.10.8:3080/pair-accept?pair=abc");

        Assert.Equal("http://192.168.10.8:3080/", link.BaseUri.ToString());
    }

    [Fact]
    public void DefaultPortsAreCanonicalizedAway()
    {
        var link = PairingLink.Parse("http://192.168.10.8:80/pair-accept?pair=abc");

        Assert.Equal("http://192.168.10.8/", link.BaseUri.ToString());
    }

    [Theory]
    [InlineData("token-only")]
    [InlineData("")]
    [InlineData("ftp://192.168.10.8:3080/pair-accept?pair=abc")]
    [InlineData("http://user:pass@192.168.10.8:3080/pair-accept?pair=abc")]
    [InlineData("http://192.168.10.8:3080/pair?pair=abc")]
    [InlineData("http://192.168.10.8:3080/pair-accept/extra?pair=abc")]
    [InlineData("http://192.168.10.8:3080/pair-accept?")]
    [InlineData("http://192.168.10.8:3080/pair-accept?pair=")]
    [InlineData("http://192.168.10.8:3080/pair-accept?Pair=abc")]
    [InlineData("http://192.168.10.8:3080/pair-accept?token=abc")]
    [InlineData("http://192.168.10.8:3080/pair-accept?pair=abc&extra=1")]
    [InlineData("http://192.168.10.8:3080/pair-accept?pair=abc#frag")]
    public void RejectsMalformedPairingLinks(string input)
    {
        Assert.Throws<PairingLinkFormatException>(() => PairingLink.Parse(input));
    }

    [Theory]
    [InlineData("http://192.168.10.8:3080", "http://192.168.10.8:3080/")]
    [InlineData("  https://tunnel.example.com/  ", "https://tunnel.example.com/")]
    [InlineData("http://192.168.10.8", "http://192.168.10.8/")]
    public void ParsesBareEndpointOrigins(string input, string expected)
    {
        var baseUri = PairingLink.ParseBaseUri(input);

        Assert.Equal(expected, baseUri.ToString());
    }

    [Theory]
    [InlineData("http://192.168.10.8:3080/api")]
    [InlineData("http://192.168.10.8:3080/?token=x")]
    [InlineData("not a url")]
    [InlineData("")]
    public void RejectsMalformedEndpointOrigins(string input)
    {
        Assert.Throws<PairingLinkFormatException>(() => PairingLink.ParseBaseUri(input));
    }

    [Fact]
    public void RemoteUiUrlUsesTheCookielessDeviceFlow()
    {
        var baseUri = new Uri("http://192.168.10.8:3080");

        var url = PairingLink.RemoteUiUrl(baseUri, "device-42");

        Assert.Equal("http://192.168.10.8:3080/pair-app?device=device-42", url);
    }

    [Fact]
    public void RemoteUiUrlEscapesTheDeviceId()
    {
        var url = PairingLink.RemoteUiUrl(new Uri("http://192.168.10.8:3080"), "a b/c");

        Assert.Equal("http://192.168.10.8:3080/pair-app?device=a%20b%2Fc", url);
    }
}
