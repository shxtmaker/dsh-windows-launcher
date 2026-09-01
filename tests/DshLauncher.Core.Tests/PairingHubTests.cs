using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using Xunit;

namespace DshLauncher.Core.Tests;

[Trait("triggerTags", "VFY-02")]
public sealed class PairingHubAcceptTests
{
    [Fact]
    public async Task AddFromPairingLinkAcceptsPersistsAndStartsKeepAlive()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);

        var target = await rig.AddPairedTargetAsync();

        Assert.Equal(PairingState.Paired, target.Pairing);
        Assert.Equal("工作台", target.DisplayName);
        Assert.Equal("http://192.168.10.8:3080", target.BaseUrl);
        Assert.True(target.HasCredential);
        Assert.NotNull(target.PairedAtUtc);
        Assert.Equal("secret-token", Assert.Single(rig.Transport.AcceptCalls));
        Assert.Equal(2, rig.Store.SaveCount);
        await rig.WaitUntilAsync(_ => rig.Transport.HeartbeatCredentials.Count >= 1);
        Assert.Equal(PairingProtocol.DefaultCookieName, rig.Transport.HeartbeatCredentials[0].CookieName);
        Assert.Equal("device-1", rig.Transport.HeartbeatCredentials[0].DeviceId);
    }

    [Fact]
    public async Task AddUsesAnExistingTargetWithTheSameEndpointInsteadOfDuplicating()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        rig.Transport.AcceptOutcomes.Enqueue(PairedOutcome("device-1"));
        await rig.Hub.AddFromPairingLinkAsync(
            HubTestRig.PairingLinkText, "first", TestContext.Current.CancellationToken);

        rig.Transport.AcceptOutcomes.Enqueue(PairedOutcome("device-1"));
        var result = await rig.Hub.AddFromPairingLinkAsync(
            "http://192.168.10.8:3080/pair-accept?pair=second-token", null, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        var snapshot = await rig.Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var target = Assert.Single(snapshot.Targets);
        Assert.Equal("first", target.DisplayName);
        Assert.Equal(2, rig.Transport.AcceptCalls.Count);
        Assert.Equal(["secret-token", "second-token"], rig.Transport.AcceptCalls);
    }

    private static PairingAcceptOutcome PairedOutcome(string deviceId) => new(
        PairingAcceptStatus.Paired,
        new DeviceCredential(PairingProtocol.DefaultCookieName, deviceId),
        200);

    [Fact]
    public async Task AcceptFailuresKeepTheTargetAwaitingPairingWithAMachineReadableError()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        rig.Transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(PairingAcceptStatus.TokenUsed, null, 409));

        var result = await rig.Hub.AddFromPairingLinkAsync(
            HubTestRig.PairingLinkText, null, TestContext.Current.CancellationToken);

        var target = HubTestRig.SingleTargetOf(result);
        Assert.Equal(PairingState.AwaitingPairing, target.Pairing);
        Assert.False(target.HasCredential);
        Assert.NotNull(result.Error);
        Assert.Equal(HubErrorCode.PairingTokenUsed, result.Error.Code);
        Assert.False(result.Error.Retryable);
        Assert.Equal("TokenUsed", target.LastFailure);
        Assert.Equal(1, rig.Store.SaveCount);
    }

    [Fact]
    public async Task UnreachableHostsProduceARetryablePairingError()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        rig.Transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(PairingAcceptStatus.Unreachable, null, null));

        var result = await rig.Hub.AddFromPairingLinkAsync(
            HubTestRig.PairingLinkText, null, TestContext.Current.CancellationToken);

        Assert.Equal(HubErrorCode.PairingUnreachable, result.Error?.Code);
        Assert.True(result.Error?.Retryable);
    }

    [Fact]
    public async Task MalformedLinksAreRejectedWithoutTouchingTransportOrStore()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);

        var result = await rig.Hub.AddFromPairingLinkAsync(
            "http://192.168.10.8:3080/pair-accept?pair=", null, TestContext.Current.CancellationToken);

        Assert.Null(result.Target);
        Assert.Equal(HubErrorCode.InvalidPairingLink, result.Error?.Code);
        Assert.Empty(rig.Transport.AcceptCalls);
        Assert.Equal(0, rig.Store.SaveCount);
    }

    [Fact]
    public async Task AttachPairingRejectsALinkPointingAtADifferentEndpoint()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();

        var result = await rig.Hub.AttachPairingAsync(
            target.TargetId,
            "http://192.168.10.9:3080/pair-accept?pair=other",
            TestContext.Current.CancellationToken);

        Assert.Equal(HubErrorCode.InvalidPairingLink, result.Error?.Code);
        Assert.Single(rig.Transport.AcceptCalls);
    }

    [Fact]
    public async Task AttachPairingReplacesTheCredentialOnTheSameEndpoint()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();
        rig.Transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired,
            new DeviceCredential("custom_pair", "device-2"),
            200));

        var result = await rig.Hub.AttachPairingAsync(
            target.TargetId,
            "http://192.168.10.8:3080/pair-accept?pair=fresh",
            TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(PairingState.Paired, HubTestRig.SingleTargetOf(result).Pairing);
        Assert.Equal("fresh", rig.Transport.AcceptCalls.Last());
        await rig.WaitUntilAsync(_ =>
            rig.Transport.HeartbeatCredentials.Count >= 2 &&
            rig.Transport.HeartbeatCredentials[^1].DeviceId == "device-2" &&
            rig.Transport.HeartbeatCredentials[^1].CookieName == "custom_pair");
    }

    [Fact]
    public async Task EndpointOnlyAddProbesTheRemoteAccessPluginPresence()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);

        var result = await rig.Hub.AddEndpointAsync(
            "http://192.168.10.8:3080", null, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.NotNull(result.Probe);
        Assert.Equal(RemoteStatusProbeKind.Reached, result.Probe.Kind);
        Assert.False(result.Probe.Paired);
        var target = HubTestRig.SingleTargetOf(result);
        Assert.Equal(PairingState.AwaitingPairing, target.Pairing);
        Assert.Equal(1, rig.Store.SaveCount);
    }

    [Fact]
    public async Task TargetLimitIsEnforced()
    {
        using var rig = new HubTestRig();
        var hub = new PairingHub(
            new PairingHubOptions { MaximumTargets = 1, DelayFactory = (_, cancellationToken) => Task.Delay(5, cancellationToken) },
            rig.Store,
            rig.Transport,
            rig.Clock,
            new HubTestRig.SequentialIdGenerator());
        await hub.StartAsync(TestContext.Current.CancellationToken);
        rig.Transport.AcceptOutcomes.Enqueue(new PairingAcceptOutcome(
            PairingAcceptStatus.Paired, new DeviceCredential("dsh_pair", "d1"), 200));
        await hub.AddFromPairingLinkAsync(HubTestRig.PairingLinkText, null, TestContext.Current.CancellationToken);

        var result = await hub.AddEndpointAsync(
            "http://192.168.20.1:3080", null, TestContext.Current.CancellationToken);

        Assert.Equal(HubErrorCode.TargetLimitReached, result.Error?.Code);
    }
}

[Trait("triggerTags", "VFY-03")]
public sealed class PairingHubKeepAliveTests
{
    [Fact]
    public async Task KeepAliveHeartbeatsUntilTheHostRevokesTheDevice()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        await rig.AddPairedTargetAsync();

        await rig.WaitUntilAsync(snapshot =>
            snapshot.Targets.Single().Connectivity == ConnectivityState.Online);

        rig.Transport.HeartbeatOutcomes.Enqueue(HeartbeatOutcome.Unpaired(401));

        var revoked = await rig.WaitUntilAsync(snapshot =>
            snapshot.Targets.Single().Pairing == PairingState.Revoked);
        Assert.Equal(ConnectivityState.Unknown, revoked.Connectivity);
        Assert.True(rig.Transport.HeartbeatCredentials.Count >= 2);
        await rig.Hub.DisposeAsync();
        var beatsAfterRevoke = rig.Transport.HeartbeatCredentials.Count;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(beatsAfterRevoke, rig.Transport.HeartbeatCredentials.Count);
    }

    [Fact]
    public async Task NetworkFailuresMarkTheTargetOfflineAndKeepRetrying()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        await rig.AddPairedTargetAsync();
        await rig.WaitUntilAsync(snapshot =>
            snapshot.Targets.Single().Connectivity == ConnectivityState.Online);

        rig.Transport.HeartbeatOutcomes.Enqueue(HeartbeatOutcome.Unreachable());
        rig.Transport.HeartbeatOutcomes.Enqueue(HeartbeatOutcome.Unreachable());
        await rig.WaitUntilChangedAsync(snapshot =>
            snapshot.Targets.Any(target => target.ConsecutiveFailures >= 2));

        rig.Transport.HeartbeatOutcomes.Enqueue(HeartbeatOutcome.Alive(200));
        await rig.WaitUntilAsync(snapshot =>
            snapshot.Targets.Single().Connectivity == ConnectivityState.Online &&
            snapshot.Targets.Single().ConsecutiveFailures == 0);
    }

    [Fact]
    public async Task DisabledKeepAliveStopsHeartbeatingUntilReEnabled()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();
        await rig.WaitUntilAsync(snapshot =>
            snapshot.Targets.Single().Connectivity == ConnectivityState.Online);

        var disabled = await rig.Hub.SetKeepAliveAsync(target.TargetId, false, TestContext.Current.CancellationToken);
        Assert.False(HubTestRig.SingleTargetOf(disabled).KeepAliveEnabled);
        var beatsWhenDisabled = rig.Transport.HeartbeatCredentials.Count;
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.True(rig.Transport.HeartbeatCredentials.Count <= beatsWhenDisabled + 1);

        await rig.Hub.SetKeepAliveAsync(target.TargetId, true, TestContext.Current.CancellationToken);
        await rig.WaitUntilAsync(snapshot => rig.Transport.HeartbeatCredentials.Count > beatsWhenDisabled + 1);
        await rig.Hub.DisposeAsync();
    }

    [Fact]
    public async Task ManualHeartbeatReportsRevocationAsATargetNotPairedError()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();
        await rig.Hub.SetKeepAliveAsync(target.TargetId, false, TestContext.Current.CancellationToken);
        var beats = rig.Transport.HeartbeatCredentials.Count;
        rig.Transport.HeartbeatOutcomes.Enqueue(HeartbeatOutcome.Unpaired(401));

        var result = await rig.Hub.HeartbeatNowAsync(target.TargetId, TestContext.Current.CancellationToken);

        Assert.Equal(beats + 1, rig.Transport.HeartbeatCredentials.Count);
        Assert.Equal(HubErrorCode.TargetNotPaired, result.Error?.Code);
        var after = await rig.Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingState.Revoked, Assert.Single(after.Targets).Pairing);
    }

    [Fact]
    public async Task ManualHeartbeatWithoutCredentialIsRefused()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var added = await rig.Hub.AddEndpointAsync(
            "http://192.168.10.8:3080", null, TestContext.Current.CancellationToken);
        var target = HubTestRig.SingleTargetOf(added);

        var result = await rig.Hub.HeartbeatNowAsync(target.TargetId, TestContext.Current.CancellationToken);

        Assert.Equal(HubErrorCode.TargetNotPaired, result.Error?.Code);
        Assert.Empty(rig.Transport.HeartbeatCredentials);
    }

    [Fact]
    public async Task RemoteUiUrlIsOnlyAvailableForPairedTargets()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();

        var result = await rig.Hub.GetRemoteUiUrlAsync(target.TargetId, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal("http://192.168.10.8:3080/pair-app?device=device-1", result.Url);
    }

    [Fact]
    public async Task RemoveCancelsTheLoopAndForgetsThePersistedTarget()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();

        var result = await rig.Hub.RemoveAsync(target.TargetId, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        var snapshot = await rig.Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Targets);
        Assert.Empty((await rig.Store.LoadAsync(TestContext.Current.CancellationToken)).Document!.Targets);
    }

    [Fact]
    public async Task RenamePersistsAndPublishes()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();

        var result = await rig.Hub.RenameAsync(target.TargetId, "新名字", TestContext.Current.CancellationToken);

        Assert.Equal("新名字", HubTestRig.SingleTargetOf(result).DisplayName);
        Assert.Equal("新名字", (await rig.Store.LoadAsync(TestContext.Current.CancellationToken)).Document!
            .Targets.Single().DisplayName);
    }

    [Fact]
    public async Task RestartRestoresPairedTargetsFromTheStoreAndResumesKeepAlive()
    {
        using var rig = new HubTestRig();
        await rig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await rig.AddPairedTargetAsync();
        await rig.Hub.DisposeAsync();

        var revived = new PairingHub(
            new PairingHubOptions { DelayFactory = (_, cancellationToken) => Task.Delay(5, cancellationToken) },
            rig.Store,
            rig.Transport,
            rig.Clock,
            new HubTestRig.SequentialIdGenerator());
        await revived.StartAsync(TestContext.Current.CancellationToken);
        var snapshot = await revived.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(snapshot.Targets);
        Assert.Equal(target.TargetId, restored.TargetId);
        Assert.Equal(PairingState.Paired, restored.Pairing);
        Assert.True(restored.HasCredential);
        await revived.DisposeAsync();
    }
}
