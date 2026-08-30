using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DshLauncher.Platform.Windows;

public sealed class HarnessHttpProbeOptions
{
    public HarnessHttpProbeOptions(
        TimeSpan requestTimeout,
        int maxResponseBytes)
    {
        if (requestTimeout <= TimeSpan.Zero ||
            requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (maxResponseBytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        }

        RequestTimeout = requestTimeout;
        MaxResponseBytes = maxResponseBytes;
    }

    public TimeSpan RequestTimeout { get; }

    public int MaxResponseBytes { get; }
}

public sealed class HarnessHttpProbeAdapter : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly HarnessHttpProbeOptions _options;
    private int _disposed;

    public HarnessHttpProbeAdapter(
        HttpMessageHandler handler,
        HarnessHttpProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static HarnessHttpProbeAdapter CreateDefault(
        HarnessHttpProbeOptions options)
    {
        return new HarnessHttpProbeAdapter(
            CreateSecureHandler(options),
            options);
    }

    public static SocketsHttpHandler CreateSecureHandler(
        HarnessHttpProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            UseProxy = false,
            Proxy = null,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.RequestTimeout,
        };
    }

    public async ValueTask<HarnessHttpProbeObservation> ProbeAsync(
        Uri origin,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateOrigin(origin);

        var api = await ObserveAsync(origin, "/api", cancellationToken)
            .ConfigureAwait(false);
        var root = await ObserveAsync(origin, "/", cancellationToken)
            .ConfigureAwait(false);

        return new HarnessHttpProbeObservation(api, root);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<ProbeExchangeObservation> ObserveAsync(
        Uri origin,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(origin, path));
        request.Headers.ConnectionClose = true;

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.Content.Headers.ContentLength is > 0 &&
                response.Content.Headers.ContentLength > _options.MaxResponseBytes)
            {
                return ProbeExchangeObservation.ResponseTooLarge(
                    (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                timeout.Token).ConfigureAwait(false);
            var body = await ReadBoundedAsync(stream, timeout.Token)
                .ConfigureAwait(false);

            if (body.TooLarge)
            {
                return ProbeExchangeObservation.ResponseTooLarge(
                    (int)response.StatusCode);
            }

            return ProbeExchangeObservation.Http(
                (int)response.StatusCode,
                body.Length,
                body.Sha256!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeExchangeObservation.TimedOut();
        }
        catch (HttpRequestException exception)
        {
            return ProbeExchangeObservation.FailedTransport(
                ClassifyTransportFailure(exception));
        }
        catch (IOException)
        {
            return ProbeExchangeObservation.FailedTransport(
                ProbeTransportFailure.NonHttpOrTruncated);
        }
    }

    private async ValueTask<BoundedBody> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.MaxResponseBytes + 1);
        try
        {
            var total = 0;
            while (total <= _options.MaxResponseBytes)
            {
                var remaining = (_options.MaxResponseBytes + 1) - total;
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, remaining),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    var digest = SHA256.HashData(buffer.AsSpan(0, total));
                    return new BoundedBody(
                        TooLarge: false,
                        total,
                        Convert.ToHexStringLower(digest));
                }

                total += read;
                if (total > _options.MaxResponseBytes)
                {
                    return new BoundedBody(
                        TooLarge: true,
                        Length: total,
                        Sha256: null);
                }
            }

            return new BoundedBody(
                TooLarge: true,
                Length: total,
                Sha256: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ProbeTransportFailure ClassifyTransportFailure(
        HttpRequestException exception)
    {
        if (exception.InnerException is SocketException socketException)
        {
            return socketException.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => ProbeTransportFailure.ConnectionRefused,
                SocketError.HostUnreachable or
                SocketError.NetworkUnreachable => ProbeTransportFailure.Unreachable,
                _ => ProbeTransportFailure.Other,
            };
        }

        return ProbeTransportFailure.Other;
    }

    private static void ValidateOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !IPAddress.TryParse(origin.Host, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !IsRfc1918(address))
        {
            throw new ArgumentException(
                "Harness origin must be a clean RFC1918 HTTP IPv4 origin.",
                nameof(origin));
        }
    }

    private static bool IsRfc1918(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private sealed record BoundedBody(
        bool TooLarge,
        int Length,
        string? Sha256);
}

public sealed record HarnessHttpProbeObservation(
    ProbeExchangeObservation Api,
    ProbeExchangeObservation Root);

public sealed record ProbeExchangeObservation(
    ProbeExchangeKind Kind,
    int? StatusCode,
    int BodyLength,
    string? BodySha256,
    ProbeTransportFailure? TransportFailure,
    string? RedirectLocation)
{
    public static ProbeExchangeObservation Http(
        int statusCode,
        int bodyLength,
        string sha256) => new(
            ProbeExchangeKind.HttpResponse,
            statusCode,
            bodyLength,
            sha256,
            null,
            null);

    public static ProbeExchangeObservation ResponseTooLarge(int statusCode) => new(
        ProbeExchangeKind.ResponseTooLarge,
        statusCode,
        0,
        null,
        null,
        null);

    public static ProbeExchangeObservation TimedOut() => new(
        ProbeExchangeKind.TimedOut,
        null,
        0,
        null,
        null,
        null);

    public static ProbeExchangeObservation FailedTransport(
        ProbeTransportFailure failure) => new(
            ProbeExchangeKind.TransportFailure,
            null,
            0,
            null,
            failure,
            null);
}

public enum ProbeExchangeKind
{
    HttpResponse,
    ResponseTooLarge,
    TimedOut,
    TransportFailure
}

public enum ProbeTransportFailure
{
    ConnectionRefused,
    Unreachable,
    NonHttpOrTruncated,
    Other
}
