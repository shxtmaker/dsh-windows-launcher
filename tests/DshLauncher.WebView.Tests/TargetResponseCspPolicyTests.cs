using System.Text.Json;
using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class TargetResponseCspPolicyTests
{
    private static readonly TargetContentBinding Binding = new(
        Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa"),
        new Uri("http://192.168.10.20:3080/"),
        @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

    [Fact]
    public void PolicyLimitsFetchAndWebSocketConnectionsToTheExactAuthority()
    {
        var policy = TargetResponseCspPolicy.CreatePolicy(Binding);

        Assert.Contains(
            "connect-src http://192.168.10.20:3080 ws://192.168.10.20:3080",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.10.21", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("wss:", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void SameOriginResponseIsFulfilledWithOneEnforcingCspHeader()
    {
        const string paused = """
            {
              "requestId": "interception-1",
              "request": { "url": "http://192.168.10.20:3080/assets/app.js" },
              "responseStatusCode": 200,
              "responseStatusText": "OK",
              "responseHeaders": [
                { "name": "Content-Type", "value": "application/javascript" },
                { "name": "Content-Security-Policy", "value": "connect-src *" }
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
            header.Value == "application/javascript");
        var csp = Assert.Single(headers, header =>
            string.Equals(
                header.Name,
                "Content-Security-Policy",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TargetResponseCspPolicy.CreatePolicy(Binding), csp.Value);
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
              "request": { "url": "http://192.168.10.20:3080/" }
            }
            """;

        var command = TargetResponseCspPolicy.CreatePausedResponseCommand(
            paused,
            Binding);

        Assert.NotNull(command);
        Assert.Equal("Fetch.failRequest", command.Method);
    }

    [Fact]
    public void FetchIsEnabledOnlyAtTheResponseStage()
    {
        using var parameters = JsonDocument.Parse(
            TargetResponseCspPolicy.CreateEnableParameters());
        var pattern = Assert.Single(
            parameters.RootElement.GetProperty("patterns").EnumerateArray());

        Assert.Equal("*", pattern.GetProperty("urlPattern").GetString());
        Assert.Equal("Response", pattern.GetProperty("requestStage").GetString());
        Assert.False(parameters.RootElement
            .GetProperty("handleAuthRequests")
            .GetBoolean());
    }
}
