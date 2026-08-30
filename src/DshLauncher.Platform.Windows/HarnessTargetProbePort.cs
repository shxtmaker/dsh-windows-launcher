using DshLauncher.Core;

namespace DshLauncher.Platform.Windows;

public sealed record HarnessProbeFingerprint(
    int ApiStatusCode,
    int ApiBodyLength,
    string ApiBodySha256,
    int RootStatusCode,
    int RootBodyLength,
    string RootBodySha256)
{
    public static HarnessProbeFingerprint V1 { get; } = new(
        ApiStatusCode: 401,
        ApiBodyLength: 12,
        ApiBodySha256: "e9d83f01c9aff03af6380e341aad90a5547d378ef54582383c6c9a35c53181af",
        RootStatusCode: 401,
        RootBodyLength: 68,
        RootBodySha256: "3aad6226baa021e747c19d2c55cbd7a5995d10f43c1d05b248af37836942acaf");

    public static HarnessProbeFingerprint LegacyUnauthenticatedV011Rc2 { get; } = new(
        ApiStatusCode: 404,
        ApiBodyLength: 9,
        ApiBodySha256: "907ba78b4545338d3539683e63ecb51cf51c10adc9dabd86e92bd52339f298b9",
        RootStatusCode: 200,
        RootBodyLength: 14556,
        RootBodySha256: "a1c9e8d395d34fc83466b19d652a36e94b4f643fafef57f840b226b0bfab74de");
}

public sealed class HarnessTargetProbePort : ITargetProbePort, IAsyncDisposable
{
    private readonly HarnessHttpProbeAdapter _adapter;
    private readonly HarnessProbeFingerprint _fingerprint;

    public HarnessTargetProbePort(
        HarnessHttpProbeAdapter adapter,
        HarnessProbeFingerprint fingerprint)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
    }

    public async ValueTask<TargetProbeResult> ProbeAsync(
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var observation = await _adapter.ProbeAsync(
            new Uri(endpoint.Origin + '/', UriKind.Absolute),
            cancellationToken).ConfigureAwait(false);
        return new TargetProbeResult(Classify(observation, _fingerprint, endpoint.Port));
    }

    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    public static TargetProbeClassification Classify(
        HarnessHttpProbeObservation observation,
        HarnessProbeFingerprint fingerprint) =>
        Classify(observation, fingerprint, port: 0);

    public static TargetProbeClassification Classify(
        HarnessHttpProbeObservation observation,
        HarnessProbeFingerprint fingerprint,
        int port)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var exchanges = new[] { observation.Api, observation.Root };
        if (exchanges.Any(exchange => exchange.Kind == ProbeExchangeKind.TimedOut))
        {
            return TargetProbeClassification.TimedOut;
        }

        if (exchanges.Any(exchange => exchange.Kind == ProbeExchangeKind.TransportFailure))
        {
            if (exchanges.Any(exchange =>
                    exchange.TransportFailure == ProbeTransportFailure.NonHttpOrTruncated))
            {
                return WrongServiceFor(port);
            }

            return exchanges.Any(exchange =>
                exchange.TransportFailure == ProbeTransportFailure.ConnectionRefused)
                ? TargetProbeClassification.HarnessNotListening
                : TargetProbeClassification.Unreachable;
        }

        if (exchanges.Any(exchange => exchange.StatusCode == 403))
        {
            return TargetProbeClassification.Forbidden;
        }

        if (Matches(observation.Api, fingerprint.ApiStatusCode, fingerprint.ApiBodyLength,
                fingerprint.ApiBodySha256) &&
            Matches(observation.Root, fingerprint.RootStatusCode, fingerprint.RootBodyLength,
                fingerprint.RootBodySha256))
        {
            return TargetProbeClassification.SupportedAuthenticatedHarness;
        }

        var legacy = HarnessProbeFingerprint.LegacyUnauthenticatedV011Rc2;
        if (Matches(observation.Api, legacy.ApiStatusCode, legacy.ApiBodyLength,
                legacy.ApiBodySha256) &&
            Matches(observation.Root, legacy.RootStatusCode, legacy.RootBodyLength,
                legacy.RootBodySha256))
        {
            return TargetProbeClassification.LegacyUnauthenticatedHarness;
        }

        if (exchanges.Any(IsClearlyWrongService))
        {
            return WrongServiceFor(port);
        }

        return TargetProbeClassification.UnsupportedHarness;
    }

    private static bool Matches(
        ProbeExchangeObservation exchange,
        int statusCode,
        int bodyLength,
        string bodySha256) =>
        exchange.Kind == ProbeExchangeKind.HttpResponse &&
        exchange.StatusCode == statusCode &&
        exchange.BodyLength == bodyLength &&
        string.Equals(exchange.BodySha256, bodySha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsClearlyWrongService(ProbeExchangeObservation exchange) =>
        exchange.Kind == ProbeExchangeKind.ResponseTooLarge ||
        exchange.StatusCode is >= 200 and <= 399 or 404;

    private static TargetProbeClassification WrongServiceFor(int port) =>
        port == TargetManager.DefaultPort
            ? TargetProbeClassification.DefaultPortOccupied
            : TargetProbeClassification.WrongService;
}
