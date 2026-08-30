using System.Text.Json;
using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06,third-party-ui")]
public sealed class TargetResponseCspPolicyTests
{
    private static readonly TargetContentBinding Binding =
        WebViewCompatibilityFixture.CreateBinding();

    [Fact]
    public void BasePolicyContainsNoConditionalNetworkCapability()
    {
        var policy = TargetResponseCspPolicy.CreatePolicy(
            Binding,
            WebViewCompatibilityFixture.CreateBaseSnapshot());

        Assert.Contains("connect-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("img-src 'self' blob: data:;", policy, StringComparison.Ordinal);
        Assert.Contains("media-src 'self' blob:;", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("worker-src 'none';", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("ws:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("wss:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtendedPolicyContainsOnlyOriginsFromTheActivatedSnapshot()
    {
        var policy = TargetResponseCspPolicy.CreatePolicy(
            Binding,
            WebViewCompatibilityFixture.CreateExtendedResolution().Snapshot);

        Assert.Contains(
            "connect-src 'self' https://api.example:443;",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "style-src 'self' https://assets.example:443;",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "frame-src 'self' https://frame.example:443;",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "script-src 'self' https://",
            policy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransientPolicyDisablesPageScriptAndAllPassiveResources()
    {
        var policy = TargetResponseCspPolicy.CreateTransientPolicy(Binding);

        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("img-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void SameOriginDocumentPreservesServerCspAndAddsLauncherCsp()
    {
        const string paused = """
            {
              "requestId": "interception-1",
              "request": { "url": "http://192.168.10.20:3080/" },
              "resourceType": "Document",
              "responseStatusCode": 200,
              "responseStatusText": "OK",
              "responseHeaders": [
                { "name": "Content-Type", "value": "text/html" },
                { "name": "Content-Security-Policy", "value": "sandbox allow-scripts; object-src 'none'" }
              ]
            }
            """;
        var launcherCsp = TargetResponseCspPolicy.CreatePolicy(
            Binding,
            WebViewCompatibilityFixture.CreateBaseSnapshot());

        var command = TargetResponseCspPolicy.CreatePausedResponseCommand(
            paused,
            Binding,
            launcherCsp);

        Assert.NotNull(command);
        Assert.Equal("Fetch.fulfillRequest", command.Method);
        using var parameters = JsonDocument.Parse(command.ParametersJson);
        var root = parameters.RootElement;
        Assert.Equal("interception-1", root.GetProperty("requestId").GetString());
        Assert.Equal(200, root.GetProperty("responseCode").GetInt32());
        var headers = root.GetProperty("responseHeaders")
            .EnumerateArray()
            .Select(header => (
                Name: header.GetProperty("name").GetString(),
                Value: header.GetProperty("value").GetString()))
            .ToArray();
        var cspHeaders = headers.Where(header =>
            string.Equals(
                header.Name,
                "Content-Security-Policy",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(2, cspHeaders.Length);
        Assert.Contains(cspHeaders, header =>
            header.Value == "sandbox allow-scripts; object-src 'none'");
        Assert.Contains(cspHeaders, header => header.Value == launcherCsp);
    }

    [Theory]
    [InlineData("http://192.168.10.20:3180/")]
    [InlineData("http://192.168.10.21:3080/")]
    [InlineData("https://192.168.10.20:3080/")]
    [InlineData("https://example.com/")]
    public void CrossOriginPausedResponsesFailClosed(string url)
    {
        var paused = $$"""
            {
              "requestId": "interception-2",
              "request": { "url": "{{url}}" },
              "resourceType": "Document",
              "responseStatusCode": 200,
              "responseHeaders": []
            }
            """;

        var command = TargetResponseCspPolicy.CreatePausedResponseCommand(
            paused,
            Binding,
            TargetResponseCspPolicy.CreateTransientPolicy(Binding));

        Assert.NotNull(command);
        Assert.Equal("Fetch.failRequest", command.Method);
        Assert.Contains(
            "BlockedByClient",
            command.ParametersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedPausedResponseWithAnIdentityFailsClosed()
    {
        const string paused = """
            {
              "requestId": "interception-3",
              "request": { "url": "http://192.168.10.20:3080/" },
              "resourceType": "Document"
            }
            """;

        var command = TargetResponseCspPolicy.CreatePausedResponseCommand(
            paused,
            Binding,
            TargetResponseCspPolicy.CreateTransientPolicy(Binding));

        Assert.NotNull(command);
        Assert.Equal("Fetch.failRequest", command.Method);
    }

    [Fact]
    public void FetchInterceptsOnlyBoundDocumentsAtTheResponseStage()
    {
        using var parameters = JsonDocument.Parse(
            TargetResponseCspPolicy.CreateEnableParameters(Binding));
        var pattern = Assert.Single(
            parameters.RootElement.GetProperty("patterns").EnumerateArray());

        Assert.Equal(
            "http://192.168.10.20:3080/*",
            pattern.GetProperty("urlPattern").GetString());
        Assert.Equal("Document", pattern.GetProperty("resourceType").GetString());
        Assert.Equal("Response", pattern.GetProperty("requestStage").GetString());
        Assert.False(parameters.RootElement
            .GetProperty("handleAuthRequests")
            .GetBoolean());
    }
}
