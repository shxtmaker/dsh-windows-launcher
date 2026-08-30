using DshLauncher.Core;
using Xunit;

namespace DshLauncher.Core.Tests;

[Trait("triggerTags", "VFY-02,VFY-03")]
public sealed class TargetManagerTests
{
    [Fact]
    public async Task InspectCandidateNormalizesPrivateAddressAndDefaultPort()
    {
        using var rig = new TargetManagerTestRig();

        var result = await rig.Manager.ExecuteAsync(
            new InspectCandidate("192.168.10.8", string.Empty),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var inspected = Assert.IsType<CandidateInspected>(result.Outcome);
        Assert.Equal("192.168.10.8", inspected.Candidate.Endpoint.Ipv4);
        Assert.Equal(3080, inspected.Candidate.Endpoint.Port);
        Assert.Equal("http://192.168.10.8:3080", inspected.Candidate.Endpoint.Origin);
        Assert.Empty(result.Snapshot.Targets);
    }

    [Theory]
    [InlineData("192.168.001.8", null, TargetErrorCode.InvalidIpv4)]
    [InlineData("10.00.0.1", null, TargetErrorCode.InvalidIpv4)]
    [InlineData("172.32.0.1", null, TargetErrorCode.InvalidIpv4)]
    [InlineData("127.0.0.1", null, TargetErrorCode.InvalidIpv4)]
    [InlineData("8.8.8.8", null, TargetErrorCode.InvalidIpv4)]
    [InlineData("http://192.168.1.8", null, TargetErrorCode.InvalidIpv4)]
    [InlineData("192.168.1.8", "0", TargetErrorCode.InvalidPort)]
    [InlineData("192.168.1.8", "65536", TargetErrorCode.InvalidPort)]
    [InlineData("192.168.1.8", " 3080", TargetErrorCode.InvalidPort)]
    [InlineData("192.168.1.8", "+3080", TargetErrorCode.InvalidPort)]
    public async Task InspectCandidateRejectsAmbiguousOrOutOfPolicyEndpoint(
        string ipv4,
        string? port,
        TargetErrorCode expectedCode)
    {
        using var rig = new TargetManagerTestRig();

        var result = await ExecuteAsync(rig, new InspectCandidate(ipv4, port));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Equal(0, rig.Probe.InvocationCount);
    }

    [Fact]
    public async Task ConfirmCandidateRequiresEveryCurrentTrustConfirmation()
    {
        using var rig = new TargetManagerTestRig();
        var inspected = TargetManagerTestRig.AssertOutcome<CandidateInspected>(
            await ExecuteAsync(rig, new InspectCandidate("10.2.3.4")));

        var incomplete = await ExecuteAsync(
            rig,
            new ConfirmCandidate(
                inspected.Candidate.CandidateId,
                "lab",
                new TrustConfirmation(1, true, false, true)));
        var outdated = await ExecuteAsync(
            rig,
            new ConfirmCandidate(
                inspected.Candidate.CandidateId,
                "lab",
                new TrustConfirmation(2, true, true, true)));

        Assert.Equal(TargetErrorCode.TrustConfirmationRequired, incomplete.Error?.Code);
        Assert.Equal(TargetErrorCode.TrustPolicyOutdated, outdated.Error?.Code);
        Assert.Empty((await GetSnapshotAsync(rig)).Targets);
    }

    [Fact]
    public async Task ConfirmCandidateRevalidatesNetworkAndProbeBeforeCatalogCommit()
    {
        using var rig = new TargetManagerTestRig();
        var inspected = TargetManagerTestRig.AssertOutcome<CandidateInspected>(
            await ExecuteAsync(rig, new InspectCandidate("10.2.3.4")));
        rig.Network.Category = NetworkCategory.Public;

        var confirmed = await ExecuteAsync(
            rig,
            new ConfirmCandidate(
                inspected.Candidate.CandidateId,
                "lab",
                TargetManagerTestRig.CompleteTrust));

        Assert.False(confirmed.Succeeded);
        Assert.Equal(TargetErrorCode.NetworkNotPrivate, confirmed.Error?.Code);
        Assert.Equal(2, rig.Network.InvocationCount);
        Assert.Equal(0, rig.Storage.CatalogWriteCount);
        Assert.Empty(confirmed.Snapshot.Targets);
    }

    [Fact]
    public async Task CatalogDeduplicatesEndpointsAndPreservesIdentityDefaultAndNames()
    {
        using var rig = new TargetManagerTestRig();
        var candidateOne = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var targetOne = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var candidateTwo = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var targetTwo = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var duplicateCandidate = Guid.Parse("10000000-0000-0000-0000-000000000003");
        rig.Ids.Enqueue(candidateOne, targetOne, candidateTwo, targetTwo, duplicateCandidate);

        var first = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(displayName: "  主机 A  "));
        var second = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(port: "3180"));
        var duplicate = TargetManagerTestRig.AssertOutcome<TargetConfirmed>(
            await rig.AddTargetAsync(displayName: "不得静默改名"));

        Assert.Equal(new TargetId(targetOne), first.TargetId);
        Assert.Equal(new TargetId(targetTwo), second.TargetId);
        Assert.Equal(first.TargetId, duplicate.TargetId);
        Assert.True(duplicate.WasExisting);

        var snapshot = await GetSnapshotAsync(rig);
        Assert.Equal(2, snapshot.Targets.Count);
        Assert.Equal(first.TargetId, snapshot.DefaultTargetId);
        Assert.Equal("主机 A", snapshot.Targets.Single(item => item.TargetId == first.TargetId).DisplayName);
        Assert.Equal(
            "192.168.10.8:3180",
            snapshot.Targets.Single(item => item.TargetId == second.TargetId).EffectiveDisplayName);

        var revisionBeforeRename = snapshot.Revision;
        var renamed = await ExecuteAsync(rig, new Rename(first.TargetId, "重命名后"));
        var changedDefault = await ExecuteAsync(rig, new SetDefault(second.TargetId));

        Assert.True(renamed.Succeeded);
        Assert.True(changedDefault.Succeeded);
        Assert.Equal(first.TargetId, changedDefault.Snapshot.Targets.Single(
            item => item.DisplayName == "重命名后").TargetId);
        Assert.Equal(second.TargetId, changedDefault.Snapshot.DefaultTargetId);
        Assert.True(changedDefault.Snapshot.Revision > revisionBeforeRename);
    }

    [Fact]
    public async Task InvalidCatalogInvariantsStopWritesAndAutomaticWork()
    {
        var duplicateEndpoint = new TargetCatalogDocument(
            1,
            new TargetId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
            new[]
            {
                new StoredTarget(
                    new TargetId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
                    "192.168.1.2",
                    3080,
                    null,
                    1,
                    DateTimeOffset.UnixEpoch),
                new StoredTarget(
                    new TargetId(Guid.Parse("30000000-0000-0000-0000-000000000002")),
                    "192.168.1.2",
                    3080,
                    "duplicate",
                    1,
                    DateTimeOffset.UnixEpoch),
            });
        using var rig = new TargetManagerTestRig(
            new TargetCatalogReadResult(CatalogReadStatus.Loaded, duplicateEndpoint));

        var snapshot = await GetSnapshotAsync(rig);
        var command = await ExecuteAsync(rig, new InspectCandidate("192.168.1.3"));

        Assert.Equal(CatalogAvailability.Unrecoverable, snapshot.Availability);
        Assert.Equal(TargetErrorCode.CatalogCorrupt, command.Error?.Code);
        Assert.Equal(0, rig.Network.InvocationCount);
        Assert.Equal(0, rig.Storage.CatalogWriteCount);
    }

    [Fact]
    public async Task UnknownCatalogSchemaBlocksGloballyWithoutTouchingOriginalState()
    {
        using var rig = new TargetManagerTestRig(
            new TargetCatalogReadResult(CatalogReadStatus.UnknownSchema, FoundSchemaVersion: 99));

        var snapshot = await GetSnapshotAsync(rig);
        var command = await ExecuteAsync(rig, new InspectCandidate("192.168.1.3"));

        Assert.Equal(CatalogAvailability.UnknownSchema, snapshot.Availability);
        Assert.Equal(99, snapshot.SchemaVersion);
        Assert.Equal(TargetErrorCode.CatalogUnknownSchema, command.Error?.Code);
        Assert.Equal(TargetErrorScope.Catalog, command.Error?.Scope);
        Assert.Equal(0, rig.Network.InvocationCount);
        Assert.Equal(0, rig.Storage.CatalogWriteCount);
    }

    private static ValueTask<TargetCommandResult> ExecuteAsync(
        TargetManagerTestRig rig,
        TargetCommand command) =>
        rig.Manager.ExecuteAsync(command, TestContext.Current.CancellationToken);

    private static ValueTask<TargetManagerSnapshot> GetSnapshotAsync(TargetManagerTestRig rig) =>
        rig.Manager.GetSnapshotAsync(TestContext.Current.CancellationToken);
}
