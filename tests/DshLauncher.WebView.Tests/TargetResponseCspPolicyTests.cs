using System.Text.Json;
using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06,third-party-ui")]
public sealed class TargetResponseCspPolicyTests
{
    private static readonly TargetContentBinding Binding = new(
        Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa"),
        new Uri("http://192.168.10.20:3080/"),
        @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

    [Fact]
    public void PolicyLimitsConnectionsAndFramesToDshWebDependencies()
    {
        var policy = TargetResponseCspPolicy.CreatePolicy(Binding);

        Assert.Contains(
            "connect-src 'self' ws: https://dsh-market.com",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://dsh-market.com",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "frame-src 'self' blob: https://dsh-market.com https://challenges.cloudflare.com",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "script-src 'self' 'unsafe-inline' blob:",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "worker-src 'self' blob:",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "script-src 'self' 'unsafe-inline' blob: https:",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.10.21", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.10.20", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("wss:", policy, StringComparison.Ordinal);
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

        var command = TargetResponseCspPolicy.CreatePausedResponseCommand(
            paused,
            Binding);

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
        Assert.Contains(headers, header =>
            header.Name == "Content-Type" &&
            header.Value == "text/html");
        var cspHeaders = headers.Where(header =>
            string.Equals(
                header.Name,
                "Content-Security-Policy",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(2, cspHeaders.Length);
        Assert.Contains(cspHeaders, header =>
            header.Value == "sandbox allow-scripts; object-src 'none'");
        Assert.Contains(cspHeaders, header =>
            header.Value == TargetResponseCspPolicy.CreatePolicy(Binding));
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
            Binding);

        Assert.NotNull(command);
        Assert.Equal("Fetch.failRequest", command.Method);
        Assert.Contains("BlockedByClient", command.ParametersJson, StringComparison.Ordinal);
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
            Binding);

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
