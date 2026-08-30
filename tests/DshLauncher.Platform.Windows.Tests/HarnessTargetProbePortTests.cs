using DshLauncher.Core;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-04")]
public sealed class HarnessTargetProbePortTests
{
    [Fact]
    public void LockedV1FingerprintAcceptsOnlyTheTwoExactUnauthorizedBodies()
    {
        var fingerprint = HarnessProbeFingerprint.V1;
        var observation = new HarnessHttpProbeObservation(
            ProbeExchangeObservation.Http(
                fingerprint.ApiStatusCode,
                fingerprint.ApiBodyLength,
                fingerprint.ApiBodySha256),
            ProbeExchangeObservation.Http(
                fingerprint.RootStatusCode,
                fingerprint.RootBodyLength,
                fingerprint.RootBodySha256));

        Assert.Equal(
            TargetProbeClassification.SupportedAuthenticatedHarness,
            HarnessTargetProbePort.Classify(observation, fingerprint));

        var changedBody = observation with
        {
            Root = observation.Root with { BodySha256 = new string('0', 64) },
        };
        Assert.Equal(
            TargetProbeClassification.UnsupportedHarness,
            HarnessTargetProbePort.Classify(changedBody, fingerprint));
    }

    [Theory]
    [InlineData(200, TargetProbeClassification.WrongService)]
    [InlineData(302, TargetProbeClassification.WrongService)]
    [InlineData(403, TargetProbeClassification.Forbidden)]
    [InlineData(404, TargetProbeClassification.WrongService)]
    [InlineData(500, TargetProbeClassification.UnsupportedHarness)]
    public void HttpStatusMatrixIsFailClosed(
        int status,
        TargetProbeClassification expected)
    {
        var exchange = ProbeExchangeObservation.Http(status, 0, Sha256OfEmpty);
        var observation = new HarnessHttpProbeObservation(exchange, exchange);

        Assert.Equal(
            expected,
            HarnessTargetProbePort.Classify(observation, HarnessProbeFingerprint.V1));
    }

    [Fact]
    public void TimeoutTransportAndNonHttpRemainDistinct()
    {
        var root = ProbeExchangeObservation.Http(401, 12, Sha256OfEmpty);

        Assert.Equal(
            TargetProbeClassification.TimedOut,
            HarnessTargetProbePort.Classify(
                new HarnessHttpProbeObservation(ProbeExchangeObservation.TimedOut(), root),
                HarnessProbeFingerprint.V1));
        Assert.Equal(
            TargetProbeClassification.HarnessNotListening,
            HarnessTargetProbePort.Classify(
                new HarnessHttpProbeObservation(
                    ProbeExchangeObservation.FailedTransport(ProbeTransportFailure.ConnectionRefused),
                    root),
                HarnessProbeFingerprint.V1));
        Assert.Equal(
            TargetProbeClassification.WrongService,
            HarnessTargetProbePort.Classify(
                new HarnessHttpProbeObservation(
                    ProbeExchangeObservation.FailedTransport(ProbeTransportFailure.NonHttpOrTruncated),
                    root),
                HarnessProbeFingerprint.V1));
    }

    [Fact]
    public void FrozenLegacyUnauthenticatedFingerprintIsDistinctFromArbitraryHttp200()
    {
        var legacy = HarnessProbeFingerprint.LegacyUnauthenticatedV011Rc2;
        var observation = new HarnessHttpProbeObservation(
            ProbeExchangeObservation.Http(
                legacy.ApiStatusCode,
                legacy.ApiBodyLength,
                legacy.ApiBodySha256),
            ProbeExchangeObservation.Http(
                legacy.RootStatusCode,
                legacy.RootBodyLength,
                legacy.RootBodySha256));

        Assert.Equal(
            TargetProbeClassification.LegacyUnauthenticatedHarness,
            HarnessTargetProbePort.Classify(
                observation,
                HarnessProbeFingerprint.V1,
                TargetManager.DefaultPort));

        var arbitraryHttp200 = observation with
        {
            Api = ProbeExchangeObservation.Http(200, 0, Sha256OfEmpty),
            Root = ProbeExchangeObservation.Http(200, 0, Sha256OfEmpty),
        };
        Assert.Equal(
            TargetProbeClassification.WrongService,
            HarnessTargetProbePort.Classify(
                arbitraryHttp200,
                HarnessProbeFingerprint.V1,
                port: 3180));
    }

    [Theory]
    [InlineData(3080, TargetProbeClassification.DefaultPortOccupied)]
    [InlineData(3180, TargetProbeClassification.WrongService)]
    public void WrongServiceOnTheDefaultPortHasItsOwnClassification(
        int port,
        TargetProbeClassification expected)
    {
        var exchange = ProbeExchangeObservation.Http(200, 0, Sha256OfEmpty);

        Assert.Equal(
            expected,
            HarnessTargetProbePort.Classify(
                new HarnessHttpProbeObservation(exchange, exchange),
                HarnessProbeFingerprint.V1,
                port));
    }

    private const string Sha256OfEmpty =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
}
