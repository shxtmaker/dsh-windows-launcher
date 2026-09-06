using System.Net.Http.Json;
using System.Text.Json;

namespace DshLauncher.Core.Pairing;

/// <summary>
/// HTTP implementation of the remote access pairing protocol. One instance
/// owns one HttpClient; every call maps the plugin's exact responses onto
/// the outcome records so no exception ever crosses the hub seam.
/// </summary>
public sealed class HttpPairingTransport : IPairingTransport, IDisposable
{
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public HttpPairingTransport(PairingTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            // Public deployments commonly redirect HTTP to HTTPS. Follow
            // only redirects that stay on the same host and preserve the
            // original API method/body; cross-host redirects must never
            // receive a pairing token or device credential.
            AllowAutoRedirect = false,
            ConnectTimeout = options.ConnectTimeout,
        });
    }

    public async ValueTask<PairingAcceptOutcome> AcceptPairingAsync(
        HarnessEndpoint endpoint,
        string token,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                uri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, uri);
                    request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                    request.Headers.Accept.ParseAdd("application/json");
                    request.Content = JsonContent.Create(new AcceptPayload(token), options: RequestJson);
                    return request;
                },
                EndpointUri(endpoint, PairingProtocol.AcceptPath),
                cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<AcceptResponse>(ResponseJson, cancellationToken)
                    .ConfigureAwait(false);
                if (payload is not { Ok: true } || string.IsNullOrWhiteSpace(payload.DeviceId))
                {
                    return new PairingAcceptOutcome(PairingAcceptStatus.Failed, null, statusCode);
                }

                var cookieName = ExtractCookieName(response.Headers.TryGetValues("Set-Cookie", out var cookies)
                    ? cookies
                    : null, payload.DeviceId) ?? PairingProtocol.DefaultCookieName;
                return new PairingAcceptOutcome(
                    PairingAcceptStatus.Paired,
                    new DeviceCredential(cookieName, payload.DeviceId),
                    statusCode);
            }

            var code = await TryReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
            return code switch
            {
                "invalid" or "expired" or "stopped" => new PairingAcceptOutcome(PairingAcceptStatus.InvalidToken, null, statusCode),
                "used" => new PairingAcceptOutcome(PairingAcceptStatus.TokenUsed, null, statusCode),
                "forbidden" => new PairingAcceptOutcome(PairingAcceptStatus.Forbidden, null, statusCode),
                "rate-limited" => new PairingAcceptOutcome(PairingAcceptStatus.RateLimited, null, statusCode),
                _ => new PairingAcceptOutcome(PairingAcceptStatus.Failed, null, statusCode),
            };
        }
        catch (HttpRequestException)
        {
            return new PairingAcceptOutcome(PairingAcceptStatus.Unreachable, null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PairingAcceptOutcome(PairingAcceptStatus.Unreachable, null, null);
        }
        catch (JsonException)
        {
            return new PairingAcceptOutcome(PairingAcceptStatus.Failed, null, null);
        }
    }

    public async ValueTask<HeartbeatOutcome> SendHeartbeatAsync(
        HarnessEndpoint endpoint,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                uri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, uri);
                    request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                    request.Headers.Accept.ParseAdd("application/json");
                    request.Headers.TryAddWithoutValidation("Cookie", $"{credential.CookieName}={credential.DeviceId}");
                    request.Content = new StringContent("{}", System.Text.Encoding.UTF8, JsonMediaType);
                    return request;
                },
                EndpointUri(endpoint, PairingProtocol.HeartbeatPath),
                cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<OkResponse>(ResponseJson, cancellationToken)
                    .ConfigureAwait(false);
                return payload is { Ok: true }
                    ? HeartbeatOutcome.Alive(statusCode)
                    : HeartbeatOutcome.Failed(statusCode);
            }

            if (statusCode == 401)
            {
                return HeartbeatOutcome.Unpaired(statusCode);
            }

            return HeartbeatOutcome.Failed(statusCode);
        }
        catch (HttpRequestException)
        {
            return HeartbeatOutcome.Unreachable();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HeartbeatOutcome.Unreachable();
        }
        catch (JsonException)
        {
            return HeartbeatOutcome.Failed(null);
        }
    }

    public async ValueTask<RemoteStatusProbeOutcome> ProbeStatusAsync(
        HarnessEndpoint endpoint,
        DeviceCredential? credential,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                uri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (credential is not null)
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", $"{credential.CookieName}={credential.DeviceId}");
                    }

                    return request;
                },
                EndpointUri(endpoint, PairingProtocol.StatusPath),
                cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            var payload = await response.Content.ReadFromJsonAsync<StatusResponse>(ResponseJson, cancellationToken)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return new RemoteStatusProbeOutcome(RemoteStatusProbeKind.Failed, null, null, null, statusCode);
            }

            return new RemoteStatusProbeOutcome(
                RemoteStatusProbeKind.Reached,
                payload.Paired,
                payload.Phase,
                payload.LanAvailable,
                statusCode);
        }
        catch (HttpRequestException)
        {
            return new RemoteStatusProbeOutcome(RemoteStatusProbeKind.Unreachable, null, null, null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteStatusProbeOutcome(RemoteStatusProbeKind.Unreachable, null, null, null, null);
        }
        catch (JsonException)
        {
            return new RemoteStatusProbeOutcome(RemoteStatusProbeKind.Failed, null, null, null, null);
        }
    }

    public void Dispose() => _client.Dispose();

    private static Uri EndpointUri(HarnessEndpoint endpoint, string path) =>
        new($"{endpoint.BaseUri.ToString().TrimEnd('/')}{path}", UriKind.Absolute);

    private async Task<HttpResponseMessage> SendAsync(
        Func<Uri, HttpRequestMessage> requestFactory,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        var currentUri = requestUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = requestFactory(currentUri);
            var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) ||
                response.Headers.Location is not { } location ||
                redirectCount >= MaximumSameHostRedirects ||
                !TryGetSafeRedirectUri(currentUri, location, out var nextUri))
            {
                return response;
            }

            response.Dispose();
            currentUri = nextUri;
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode) =>
        statusCode is System.Net.HttpStatusCode.MovedPermanently or
            System.Net.HttpStatusCode.Found or
            System.Net.HttpStatusCode.SeeOther or
            System.Net.HttpStatusCode.TemporaryRedirect or
            System.Net.HttpStatusCode.PermanentRedirect;

    private static bool TryGetSafeRedirectUri(
        Uri currentUri,
        Uri location,
        out Uri redirectUri)
    {
        redirectUri = location.IsAbsoluteUri
            ? location
            : new Uri(currentUri, location);

        if (redirectUri.Scheme is not ("http" or "https") ||
            redirectUri.UserInfo.Length > 0 ||
            redirectUri.Fragment.Length > 0 ||
            !string.Equals(currentUri.IdnHost, redirectUri.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An already secure request must never be downgraded. An HTTP public
        // endpoint may upgrade to HTTPS on the same host.
        return currentUri.Scheme != "https" || redirectUri.Scheme == "https";
    }

    private static string? ExtractCookieName(IEnumerable<string>? setCookieHeaders, string deviceId)
    {
        if (setCookieHeaders is null)
        {
            return null;
        }

        foreach (var header in setCookieHeaders)
        {
            var pair = header.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (name.Length > 0 && (value == deviceId || name == PairingProtocol.DefaultCookieName))
            {
                return name;
            }
        }

        return null;
    }

    private static async ValueTask<string?> TryReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>(ResponseJson, cancellationToken)
                .ConfigureAwait(false);
            return payload?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string JsonMediaType = "application/json";
    private const int MaximumSameHostRedirects = 3;

    private readonly PairingTransportOptions _options;
    private readonly HttpClient _client;

    private sealed record AcceptPayload(string Token);

    private sealed record AcceptResponse(bool Ok, string? DeviceId);

    private sealed record OkResponse(bool Ok);

    private sealed record ErrorResponse(bool Ok, string? Code);

    private sealed record StatusResponse(
        bool Ok,
        bool Paired,
        string? Phase,
        bool? LanAvailable,
        bool? RequirePairingForLan);
}

/// <summary>Tunables for the HTTP pairing transport.</summary>
public sealed record PairingTransportOptions
{
    /// <summary>User-Agent advertised to the host; the panel shows it as the device name.</summary>
    public string UserAgent { get; init; } = "DshWindowsLauncher";

    /// <summary>Connect timeout per attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
