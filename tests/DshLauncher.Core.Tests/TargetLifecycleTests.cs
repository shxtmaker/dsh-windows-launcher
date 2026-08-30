using DshLauncher.Core;
using Xunit;

namespace DshLauncher.Core.Tests;

[Trait("triggerTags", "VFY-02,VFY-04,VFY-05")]
public sealed class TargetLifecycleTests
{
    [Fact]
    public async Task ForgetUsesTwoCheckpointsAndMakesTheOnlyRemainingTargetDefault()
    {
        using var rig = new TargetManagerTestRig();
        var first = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var second = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(port: "3180"));
        var snapshot = await GetSnapshotAsync(rig);
        rig.Events.Expect(
            "tombstone-write",
            "runtime-close",
            "runtime-delete",
            "runtime-exists",
            "session-delete",
            "session-read",
            "catalog-write",
            "catalog-write",
            "tombstone-clear");

        var result = await ExecuteAsync(rig, new Forget(first.TargetId, snapshot.Revision));

        Assert.True(result.Succeeded);
        Assert.Equal(second.TargetId, result.Snapshot.DefaultTargetId);
        Assert.Equal(second.TargetId, Assert.Single(result.Snapshot.Targets).TargetId);
        rig.Events.AssertComplete();
    }

    [Fact]
    public async Task ForgetDefaultAmongMultipleTargetsRequiresAnExplicitValidSuccessor()
    {
        using var rig = new TargetManagerTestRig();
        var first = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var second = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(port: "3180"));
        _ = await rig.AddTargetAsync(ipv4: "192.168.10.9");
        var snapshot = await GetSnapshotAsync(rig);

        var missingSuccessor = await ExecuteAsync(
            rig,
            new Forget(first.TargetId, snapshot.Revision));
        var forgotten = await ExecuteAsync(
            rig,
            new Forget(first.TargetId, snapshot.Revision, second.TargetId));

        Assert.Equal(TargetErrorCode.SuccessorRequired, missingSuccessor.Error?.Code);
        Assert.True(forgotten.Succeeded);
        Assert.Equal(second.TargetId, forgotten.Snapshot.DefaultTargetId);
        Assert.Equal(2, forgotten.Snapshot.Targets.Count);
    }

    [Fact]
    public async Task ForgetRejectsAConfirmationFromAnOlderSnapshotBeforeDeletingAnything()
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var staleRevision = (await GetSnapshotAsync(rig)).Revision;
        _ = await ExecuteAsync(rig, new Rename(target.TargetId, "new display name"));

        var result = await ExecuteAsync(rig, new Forget(target.TargetId, staleRevision));

        Assert.Equal(TargetErrorCode.SnapshotStale, result.Error?.Code);
        Assert.True(result.Error?.Retryable);
        Assert.Single(result.Snapshot.Targets);
    }

    [Fact]
    public async Task StartupTombstoneFinishesDeletionBeforePublishingRecoveredSnapshot()
    {
        var forgottenId = new TargetId(Guid.Parse("40000000-0000-0000-0000-000000000001"));
        var successorId = new TargetId(Guid.Parse("40000000-0000-0000-0000-000000000002"));
        var document = new TargetCatalogDocument(
            1,
            forgottenId,
            new[]
            {
                Stored(forgottenId, "192.168.2.1", 3080),
                Stored(successorId, "192.168.2.2", 3080),
            });
        using var rig = new TargetManagerTestRig(
            new TargetCatalogReadResult(CatalogReadStatus.Loaded, document));
        rig.Storage.SeedTombstone(new ForgetTombstone(forgottenId, successorId));
        rig.Events.Expect(
            "catalog-read",
            "tombstones-read",
            "runtime-close",
            "runtime-delete",
            "runtime-exists",
            "session-delete",
            "session-read",
            "catalog-write",
            "catalog-write",
            "tombstone-clear",
            "session-read");

        var snapshot = await GetSnapshotAsync(rig);

        Assert.Equal(successorId, snapshot.DefaultTargetId);
        Assert.Equal(successorId, Assert.Single(snapshot.Targets).TargetId);
        rig.Events.AssertComplete();
    }

    [Fact]
    public async Task StartupTombstoneForAlreadyRemovedTargetSealsBothCatalogCopiesBeforeClearing()
    {
        var retainedId = new TargetId(Guid.Parse("40000000-0000-0000-0000-000000000010"));
        var removedId = new TargetId(Guid.Parse("40000000-0000-0000-0000-000000000011"));
        var document = new TargetCatalogDocument(
            1,
            retainedId,
            new[] { Stored(retainedId, "192.168.2.10", 3080) });
        using var rig = new TargetManagerTestRig(
            new TargetCatalogReadResult(CatalogReadStatus.Loaded, document));
        rig.Storage.SeedTombstone(new ForgetTombstone(removedId, null));
        rig.Events.Expect(
            "catalog-read",
            "tombstones-read",
            "catalog-write",
            "catalog-write",
            "tombstone-clear",
            "session-read");

        var snapshot = await GetSnapshotAsync(rig);

        Assert.Equal(retainedId, Assert.Single(snapshot.Targets).TargetId);
        rig.Events.AssertComplete();
    }

    [Theory]
    [InlineData(RuntimeOpenStatus.Unauthorized, true, TargetErrorCode.RuntimeFailure)]
    [InlineData(RuntimeOpenStatus.OriginMismatch, true, TargetErrorCode.RuntimeFailure)]
    [InlineData(RuntimeOpenStatus.Forbidden, false, TargetErrorCode.SessionForbidden)]
    [InlineData(RuntimeOpenStatus.Unreachable, false, TargetErrorCode.SessionUnavailable)]
    [InlineData(RuntimeOpenStatus.TemporaryFailure, false, TargetErrorCode.SessionUnavailable)]
    public async Task PrepareOpenAppliesPerTargetSessionInvalidationMatrix(
        RuntimeOpenStatus runtimeStatus,
        bool shouldInvalidate,
        TargetErrorCode failureCode)
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        _ = await PairTargetAsync(rig, target.TargetId, "192.168.10.8", 3080);
        rig.Runtime.OpenStatus = runtimeStatus;

        var result = await ExecuteAsync(rig, new PrepareOpen(target.TargetId));

        if (shouldInvalidate)
        {
            Assert.True(result.Succeeded);
            var prepared = Assert.IsType<OpenPrepared>(result.Outcome);
            Assert.Equal(OpenDisposition.PairingRequired, prepared.Disposition);
            Assert.Equal(SessionMetadataStatus.Missing, rig.Sessions.GetStatus(target.TargetId));
            Assert.Equal(TargetSessionState.PendingPairing, Assert.Single(result.Snapshot.Targets).SessionState);
        }
        else
        {
            Assert.Equal(failureCode, result.Error?.Code);
            Assert.Equal(SessionMetadataStatus.Committed, rig.Sessions.GetStatus(target.TargetId));
            Assert.Equal(TargetSessionState.Paired, Assert.Single(result.Snapshot.Targets).SessionState);
        }
    }

    [Theory]
    [InlineData(TargetProbeClassification.DefaultPortOccupied, true, TargetErrorCode.DefaultPortOccupied)]
    [InlineData(TargetProbeClassification.WrongService, true, TargetErrorCode.WrongService)]
    [InlineData(TargetProbeClassification.LegacyUnauthenticatedHarness, true, TargetErrorCode.LegacyUnauthenticatedHarness)]
    [InlineData(TargetProbeClassification.UnsupportedHarness, true, TargetErrorCode.UnsupportedHarness)]
    [InlineData(TargetProbeClassification.Forbidden, false, TargetErrorCode.ProbeForbidden)]
    [InlineData(TargetProbeClassification.Unreachable, false, TargetErrorCode.ProbeUnreachable)]
    [InlineData(TargetProbeClassification.TimedOut, false, TargetErrorCode.ProbeTimedOut)]
    [InlineData(TargetProbeClassification.HarnessNotListening, false, TargetErrorCode.HarnessNotRunning)]
    public async Task PrepareOpenInvalidatesOnlyDefinitiveIdentityFailures(
        TargetProbeClassification classification,
        bool shouldInvalidate,
        TargetErrorCode expectedCode)
    {
        using var rig = new TargetManagerTestRig();
        var target = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        _ = await PairTargetAsync(rig, target.TargetId, "192.168.10.8", 3080);
        rig.Probe.Classification = classification;

        var result = await ExecuteAsync(rig, new PrepareOpen(target.TargetId));

        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Equal(
            shouldInvalidate ? SessionMetadataStatus.Missing : SessionMetadataStatus.Committed,
            rig.Sessions.GetStatus(target.TargetId));
        Assert.Equal(
            shouldInvalidate ? TargetSessionState.PendingPairing : TargetSessionState.Paired,
            Assert.Single(result.Snapshot.Targets).SessionState);
    }

    [Fact]
    public async Task UnknownSessionSchemaClearsOnlyThatTargetAndReturnsToPairing()
    {
        using var rig = new TargetManagerTestRig();
        var first = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var second = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(port: "3180"));
        _ = await PairTargetAsync(rig, first.TargetId, "192.168.10.8", 3080);
        _ = await PairTargetAsync(rig, second.TargetId, "192.168.10.8", 3180);
        rig.Sessions.SetStatus(first.TargetId, SessionMetadataStatus.UnknownSchema);

        var result = await ExecuteAsync(rig, new PrepareOpen(first.TargetId));

        Assert.True(result.Succeeded);
        Assert.Equal(
            OpenDisposition.PairingRequired,
            Assert.IsType<OpenPrepared>(result.Outcome).Disposition);
        Assert.Equal(SessionMetadataStatus.Missing, rig.Sessions.GetStatus(first.TargetId));
        Assert.Equal(SessionMetadataStatus.Committed, rig.Sessions.GetStatus(second.TargetId));
        Assert.Equal(
            TargetSessionState.Paired,
            result.Snapshot.Targets.Single(item => item.TargetId == second.TargetId).SessionState);
    }

    [Fact]
    public async Task ExplicitAuthenticationInvalidationDeletesAndVerifiesOnlyThatSession()
    {
        using var rig = new TargetManagerTestRig();
        var first = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync());
        var second = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(port: "3180"));
        _ = await PairTargetAsync(rig, first.TargetId, "192.168.10.8", 3080);
        _ = await PairTargetAsync(rig, second.TargetId, "192.168.10.8", 3180);
        rig.Events.Expect(
            "runtime-close",
            "runtime-delete",
            "runtime-exists",
            "session-delete",
            "session-read");

        var result = await ExecuteAsync(
            rig,
            new InvalidateSession(first.TargetId));

        Assert.True(result.Succeeded);
        Assert.Equal(
            first.TargetId,
            Assert.IsType<SessionInvalidated>(result.Outcome).TargetId);
        Assert.Equal(
            TargetSessionState.PendingPairing,
            result.Snapshot.Targets.Single(
                target => target.TargetId == first.TargetId).SessionState);
        Assert.Equal(
            TargetSessionState.Paired,
            result.Snapshot.Targets.Single(
                target => target.TargetId == second.TargetId).SessionState);
        Assert.Equal(
            SessionMetadataStatus.Missing,
            rig.Sessions.GetStatus(first.TargetId));
        Assert.Equal(
            SessionMetadataStatus.Committed,
            rig.Sessions.GetStatus(second.TargetId));
        rig.Events.AssertComplete();
    }

    [Fact]
    public async Task SameTargetCommandsSerializeWhileDifferentTargetsCanPairInParallel()
    {
        using var rig = new TargetManagerTestRig();
        var first = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(await rig.AddTargetAsync());
        var second = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(port: "3180"));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Runtime.PairHandler = async (targetId, _, cancellationToken) =>
        {
            if (targetId == first.TargetId)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondEntered.TrySetResult();
            }

            return new PairingHandshake(303, true, true);
        };
        using var firstLink = Link("192.168.10.8", 3080);
        using var secondLink = Link("192.168.10.8", 3180);
        var firstPair = ExecuteAsync(rig, new Pair(first.TargetId, firstLink)).AsTask();
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var sameTargetOpen = ExecuteAsync(rig, new PrepareOpen(first.TargetId)).AsTask();
        var secondPair = ExecuteAsync(rig, new Pair(second.TargetId, secondLink)).AsTask();
        await secondEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(sameTargetOpen.IsCompleted);
        Assert.True((await secondPair).Succeeded);
        releaseFirst.TrySetResult();
        Assert.True((await firstPair).Succeeded);
        Assert.True((await sameTargetOpen).Succeeded);
    }

    private static StoredTarget Stored(TargetId targetId, string ipv4, int port) => new(
        targetId,
        ipv4,
        port,
        null,
        1,
        DateTimeOffset.UnixEpoch);

    private static async ValueTask<TargetCommandResult> PairTargetAsync(
        TargetManagerTestRig rig,
        TargetId targetId,
        string ipv4,
        int port)
    {
        using var link = Link(ipv4, port);
        return await ExecuteAsync(rig, new Pair(targetId, link));
    }

    private static PairingLinkSecret Link(string ipv4, int port) => PairingLinkSecret.TakeOwnership(
        $"http://{ipv4}:{port}/?token=test-secret".ToCharArray());

    private static ValueTask<TargetCommandResult> ExecuteAsync(
        TargetManagerTestRig rig,
        TargetCommand command) =>
        rig.Manager.ExecuteAsync(command, TestContext.Current.CancellationToken);

    private static ValueTask<TargetManagerSnapshot> GetSnapshotAsync(TargetManagerTestRig rig) =>
        rig.Manager.GetSnapshotAsync(TestContext.Current.CancellationToken);
}
