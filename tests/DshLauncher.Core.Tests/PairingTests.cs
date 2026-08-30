using DshLauncher.Core;
using Xunit;

namespace DshLauncher.Core.Tests;

[Trait("triggerTags", "VFY-05")]
public sealed class PairingTests
{
    [Fact]
    public async Task PairAcceptsOneExactLanUrlCommitsThreeStageHandshakeAndClearsSecrets()
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var ownedCharacters = "  dsh web: http://192.168.10.8:3080/?token=abc%2Fdef  ".ToCharArray();
        using var link = PairingLinkSecret.TakeOwnership(ownedCharacters);
        rig.Runtime.PairHandler = (_, token, _) => ValueTask.FromResult(
            token.Span.SequenceEqual("abc%2Fdef")
                ? new PairingHandshake(303, true, true)
                : new PairingHandshake(400, false, false));
        rig.Events.Expect(
            "network",
            "probe",
            "session-read",
            "session-marker",
            "runtime-reset",
            "runtime-pair",
            "session-commit",
            "clipboard");

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.True(result.Succeeded);
        Assert.IsType<TargetPaired>(result.Outcome);
        Assert.Equal(TargetSessionState.Paired, Assert.Single(result.Snapshot.Targets).SessionState);
        Assert.True(link.IsCleared);
        Assert.All(ownedCharacters, character => Assert.Equal('\0', character));
        rig.Events.AssertComplete();
    }

    [Theory]
    [InlineData("token-only", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("https://192.168.10.8:3080/?token=x", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://127.0.0.1:3080/?token=x", TargetErrorCode.PairingLinkWrongTarget)]
    [InlineData("http://192.168.10.9:3080/?token=x", TargetErrorCode.PairingLinkWrongTarget)]
    [InlineData("http://192.168.10.8:3180/?token=x", TargetErrorCode.PairingLinkWrongTarget)]
    [InlineData("http://user@192.168.10.8:3080/?token=x", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/path?token=x", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?token=", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?Token=x", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?token=x&extra=1", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?token=x&token=y", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?token=x#fragment", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?token=x http://192.168.10.8:3080/?token=y", TargetErrorCode.PairingLinkInvalid)]
    [InlineData("http://192.168.10.8:3080/?token=x?extra", TargetErrorCode.PairingLinkInvalid)]
    public async Task PairRejectsAnythingExceptOneStrictAuthorityBoundUrl(
        string input,
        TargetErrorCode expectedCode)
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var characters = input.ToCharArray();
        using var link = PairingLinkSecret.TakeOwnership(characters);

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Equal(0, rig.Runtime.PairInvocationCount);
        Assert.True(link.IsCleared);
        Assert.All(characters, character => Assert.Equal('\0', character));
        Assert.Equal(1, rig.Clipboard.InvocationCount);
    }

    [Theory]
    [InlineData(302, true, true, TargetErrorCode.PairingRejected)]
    [InlineData(303, false, true, TargetErrorCode.PairingIncomplete)]
    [InlineData(303, true, false, TargetErrorCode.PairingIncomplete)]
    public async Task PairCommitsOnlyAfterAllThreeHandshakeChecks(
        int tokenStatus,
        bool rootLoaded,
        bool apiAvailable,
        TargetErrorCode expectedCode)
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        rig.Runtime.Handshake = new PairingHandshake(tokenStatus, rootLoaded, apiAvailable);
        using var link = LinkForTarget();

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Equal(SessionMetadataStatus.Missing, rig.Sessions.GetStatus(target.TargetId));
        Assert.Equal(TargetSessionState.PendingPairing, Assert.Single(result.Snapshot.Targets).SessionState);
    }

    [Fact]
    public async Task BoundaryExceptionReturnsStableMetadataWithoutExposingExceptionText()
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        rig.Runtime.PairHandler = static (_, _, _) =>
            throw new InvalidOperationException(
                "http://192.168.10.8:3080/?token=must-never-cross-the-seam");
        using var link = LinkForTarget();

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.False(result.Succeeded);
        Assert.Equal(TargetErrorCode.RuntimeFailure, result.Error?.Code);
        Assert.Equal(TargetErrorScope.Command, result.Error?.Scope);
        Assert.True(result.Error?.Retryable);
        Assert.DoesNotContain(
            typeof(TargetError).GetProperties(),
            property => property.PropertyType == typeof(string));
        Assert.True(link.IsCleared);
    }

    [Fact]
    public async Task PairReportsCleanupFailureWhenRuntimeRollbackCannotFinish()
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        rig.Runtime.DeleteHandler = (_, _) => ValueTask.FromException(
            new InvalidOperationException("scripted rollback failure"));
        rig.Runtime.PairHandler = static (_, _, _) =>
            throw new InvalidOperationException("scripted pairing failure");
        using var link = LinkForTarget();

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.False(result.Succeeded);
        Assert.Equal(TargetErrorCode.SessionCleanupFailed, result.Error?.Code);
        Assert.Equal(
            SessionMetadataStatus.Missing,
            rig.Sessions.GetStatus(target.TargetId));
        Assert.Equal(1, rig.Sessions.PairingStateDeleteCount);
        Assert.Equal(
            TargetSessionState.PendingPairing,
            Assert.Single(result.Snapshot.Targets).SessionState);
    }

    [Fact]
    public async Task PairingRollbackDeletesBrowserDataWithoutPreparingAnotherUdf()
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        rig.Runtime.Handshake = new PairingHandshake(303, true, false);
        using var link = LinkForTarget();

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.Equal(TargetErrorCode.PairingIncomplete, result.Error?.Code);
        Assert.Equal(1, rig.Runtime.ResetInvocationCount);
        Assert.Equal(1, rig.Runtime.DeleteInvocationCount);
        Assert.Equal(
            SessionMetadataStatus.Missing,
            rig.Sessions.GetStatus(target.TargetId));
    }

    [Fact]
    public async Task DiagnosticSinkFailureNeverChangesPairingOutcomeOrSecretCleanup()
    {
        using var rig = new TargetManagerTestRig(
            pairingDiagnostics: new ThrowingPairingDiagnosticSink());
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var characters = "http://192.168.10.8:3080/?token=test-secret".ToCharArray();
        using var link = PairingLinkSecret.TakeOwnership(characters);

        var result = await ExecuteAsync(rig, new Pair(target.TargetId, link));

        Assert.True(result.Succeeded);
        Assert.True(link.IsCleared);
        Assert.All(characters, character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void PairingDiagnosticEventRejectsInvalidHttpStatus(int statusCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PairingDiagnosticEvent(
                PairingDiagnosticStage.TokenNavigation,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.Navigation,
                statusCode));
    }

    private static PairingLinkSecret LinkForTarget() => PairingLinkSecret.TakeOwnership(
        "http://192.168.10.8:3080/?token=test-secret".ToCharArray());

    private static ValueTask<TargetCommandResult> ExecuteAsync(
        TargetManagerTestRig rig,
        TargetCommand command) =>
        rig.Manager.ExecuteAsync(command, TestContext.Current.CancellationToken);

    private sealed class ThrowingPairingDiagnosticSink : IPairingDiagnosticSink
    {
        public ValueTask ReportAsync(
            PairingDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("scripted diagnostic failure"));
    }
}
