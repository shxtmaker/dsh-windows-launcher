using System.Net;
using System.Net.Http;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-04")]
public sealed class HarnessHttpProbeAdapterTests
{
    [Fact]
    public async Task ProbeUsesTwoCredentialFreeGetRequestsAndReturnsOnlyDigests()
    {
        var handler = new RecordingHandler(request =>
        {
            var content = request.RequestUri!.AbsolutePath == "/api"
                ? "api"
                : "root";
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(content),
            };
        });
        await using var adapter = new HarnessHttpProbeAdapter(
            handler,
            new HarnessHttpProbeOptions(
                TimeSpan.FromSeconds(1),
                maxResponseBytes: 128));

        var result = await adapter.ProbeAsync(
            new Uri("http://192.168.1.20:3080/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Collection(
            handler.Requests,
            request => AssertRequestIsCredentialFree(request, "/api"),
            request => AssertRequestIsCredentialFree(request, "/"));
        Assert.Equal(ProbeExchangeKind.HttpResponse, result.Api.Kind);
        Assert.Equal(401, result.Api.StatusCode);
        Assert.Equal(
            "14c2529eb4498c5d1ffd6915d05bf58a91bdda796af59f41d480d11c099d0479",
            result.Api.BodySha256);
        Assert.Equal(
            "4813494d137e1631bba301d5acab6e7bb7aa74ce1185d456565ef51d737677b2",
            result.Root.BodySha256);
    }

    [Fact]
    public async Task RedirectResponsesAreObservedAndNeverFollowed()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://203.0.113.10/token");
            return response;
        });
        await using var adapter = new HarnessHttpProbeAdapter(
            handler,
            new HarnessHttpProbeOptions(
                TimeSpan.FromSeconds(1),
                maxResponseBytes: 128));

        var result = await adapter.ProbeAsync(
            new Uri("http://192.168.1.20:3080/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(302, result.Api.StatusCode);
        Assert.Equal(302, result.Root.StatusCode);
        Assert.Null(result.Api.RedirectLocation);
        Assert.Null(result.Root.RedirectLocation);
    }

    [Fact]
    public async Task ResponseLargerThanTheConfiguredLimitIsRejectedWithoutReturningBytes()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new ByteArrayContent(new byte[33]),
            });
        await using var adapter = new HarnessHttpProbeAdapter(
            handler,
            new HarnessHttpProbeOptions(
                TimeSpan.FromSeconds(1),
                maxResponseBytes: 32));

        var result = await adapter.ProbeAsync(
            new Uri("http://192.168.1.20:3080/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProbeExchangeKind.ResponseTooLarge, result.Api.Kind);
        Assert.Null(result.Api.BodySha256);
        Assert.Equal(ProbeExchangeKind.ResponseTooLarge, result.Root.Kind);
        Assert.Null(result.Root.BodySha256);
    }

    [Fact]
    public async Task PerRequestTimeoutProducesAClassificationInsteadOfAnException()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        await using var adapter = new HarnessHttpProbeAdapter(
            handler,
            new HarnessHttpProbeOptions(
                TimeSpan.FromMilliseconds(20),
                maxResponseBytes: 128));

        var result = await adapter.ProbeAsync(
            new Uri("http://192.168.1.20:3080/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProbeExchangeKind.TimedOut, result.Api.Kind);
        Assert.Equal(ProbeExchangeKind.TimedOut, result.Root.Kind);
    }

    [Fact]
    public void ProductionHandlerDisablesRedirectsCredentialsCookiesAndProxying()
    {
        var options = new HarnessHttpProbeOptions(
            TimeSpan.FromSeconds(2),
            maxResponseBytes: 4096);

        using var handler = HarnessHttpProbeAdapter.CreateSecureHandler(options);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    private static void AssertRequestIsCredentialFree(
        HttpRequestMessage request,
        string path)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Origin"));
        Assert.False(request.Headers.Contains("Referer"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public RecordingHandler(Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _respond(request, cancellationToken);
        }
    }
}
