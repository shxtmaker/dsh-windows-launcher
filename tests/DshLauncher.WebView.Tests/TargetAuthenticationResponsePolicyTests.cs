using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-05,VFY-06")]
public sealed class TargetAuthenticationResponsePolicyTests
{
    private static readonly TargetContentBinding Binding = new(
        Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa"),
        new Uri("http://192.168.10.20:3080/"),
        @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

    [Theory]
    [InlineData("http://192.168.10.20:3080/", 401, true)]
    [InlineData("http://192.168.10.20:3080/", 403, false)]
    [InlineData("http://192.168.10.20:3080/", 503, false)]
    [InlineData("http://192.168.10.20:3080/session", 401, false)]
    [InlineData("http://192.168.10.21:3080/", 401, false)]
    public void OnlyAnExactRoot401InvalidatesAuthentication(
        string source,
        int statusCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            TargetAuthenticationResponsePolicy.IsInvalidRootNavigation(
                Binding,
                source,
                statusCode));
    }

    [Theory]
    [InlineData("http://192.168.10.20:3080/api", 401, true)]
    [InlineData("http://192.168.10.20:3080/api/remote.mux", 401, true)]
    [InlineData("http://192.168.10.20:3080/api", 403, false)]
    [InlineData("http://192.168.10.20:3080/api", 503, false)]
    [InlineData("http://192.168.10.20:3080/apiary", 401, false)]
    [InlineData("http://192.168.10.20:3080/api?secret=ignored", 401, false)]
    [InlineData("http://192.168.10.21:3080/api", 401, false)]
    public void OnlyAnExactApi401InvalidatesAuthentication(
        string requestUri,
        int statusCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            TargetAuthenticationResponsePolicy.IsInvalidApiResponse(
                Binding,
                requestUri,
                statusCode));
    }
}
