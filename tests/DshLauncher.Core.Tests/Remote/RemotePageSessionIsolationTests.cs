using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Remote;

/// <summary>
/// D15 跨目标隔离：每个目标页面一个通道、一份代际、一份能力；
/// 一个目标的授权、取消或关闭都不得泄漏到另一个目标的页面。全部在 Linux 真实执行。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemotePageSessionIsolationTests
{
    private const string FirstPage = "https://first.host/";
    private const string SecondPage = "https://second.host/";

    [Fact]
    public void EveryPageSessionGetsItsOwnChannelId()
    {
        var channels = Enumerable.Range(0, 16).Select(_ => RemotePageChannelId.New()).ToArray();

        Assert.Equal(channels.Length, channels.Distinct(StringComparer.Ordinal).Count());
        Assert.All(channels, channel => Assert.StartsWith("page-", channel, StringComparison.Ordinal));
    }

    [Fact]
    public void BothSessionsCanShareTheSameOriginAndStillStayIndependent()
    {
        using var first = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var second = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);

        LoadTrusted(first, FirstPage);

        Assert.NotEqual(first.ChannelId, second.ChannelId);
        Assert.True(first.Capability is not null);
        Assert.Null(second.Capability);
        Assert.Equal(RemotePagePhase.AwaitingDocument, second.Phase);
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, second.AdmitNewWork().Code);
    }

    [Fact]
    public void AMessageAddressedToAnotherPagesChannelIsRefused()
    {
        using var first = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var second = new RemotePageSessionState(RemotePageChannelId.New(), SecondPage);
        LoadTrusted(first, FirstPage);
        LoadTrusted(second, SecondPage);

        var crossing = second.AdmitMessage(
            new RemotePageInboundMessage(first.ChannelId, first.Epoch, "chunk"));

        Assert.False(crossing.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageChannelMismatch, crossing.Code);
        Assert.True(first.AdmitMessage(new RemotePageInboundMessage(first.ChannelId, first.Epoch, "chunk")).Accepted);
        Assert.True(second.AdmitMessage(new RemotePageInboundMessage(second.ChannelId, second.Epoch, "chunk")).Accepted);
    }

    [Fact]
    public void ACapabilityTokenFromOnePageCannotBeReplayedOnAnother()
    {
        using var first = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var second = new RemotePageSessionState(RemotePageChannelId.New(), SecondPage);
        LoadTrusted(first, FirstPage);
        var stolen = first.Capability!.Value;
        var secondEpochBefore = second.Epoch;

        var replay = second.AdmitMessage(new RemotePageInboundMessage(stolen.ChannelId, stolen.Epoch, "file-begin"));

        Assert.False(replay.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageChannelMismatch, replay.Code);
        Assert.Null(second.Capability);
        Assert.Equal(secondEpochBefore, second.Epoch);
    }

    [Fact]
    public void EpochsAdvanceIndependentlyPerPage()
    {
        using var first = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var second = new RemotePageSessionState(RemotePageChannelId.New(), SecondPage);
        LoadTrusted(first, FirstPage);
        LoadTrusted(second, SecondPage);

        for (var navigation = 0; navigation < 4; navigation++)
        {
            LoadTrusted(first, FirstPage);
        }

        Assert.Equal(6, first.Epoch.Value);
        Assert.Equal(2, second.Epoch.Value);
        Assert.Equal(RemotePageEpoch.Initial.Next(), second.Epoch);
        Assert.True(second.AdmitMessage(new RemotePageInboundMessage(second.ChannelId, second.Epoch, "chunk")).Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale,
            second.AdmitMessage(new RemotePageInboundMessage(second.ChannelId, first.Epoch, "chunk")).Code);
    }

    [Fact]
    public void SuspendingOnePageDoesNotSuspendAnother()
    {
        using var first = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var second = new RemotePageSessionState(RemotePageChannelId.New(), SecondPage);
        LoadTrusted(first, FirstPage);
        LoadTrusted(second, SecondPage);

        Assert.True(first.SuspendForNewWork(RemotePageSuspensionReason.TabSwitched));

        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, first.AdmitNewWork().Code);
        Assert.Equal(RemotePagePhase.Active, second.Phase);
        Assert.Equal(RemotePageSessionCodes.WorkAccepted, second.AdmitNewWork().Code);
    }

    [Fact]
    public void ClosingOnePageCancelsOnlyItsOwnWork()
    {
        using var first = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var second = new RemotePageSessionState(RemotePageChannelId.New(), SecondPage);
        LoadTrusted(first, FirstPage);
        LoadTrusted(second, SecondPage);
        var firstToken = first.InFlightToken;
        var secondToken = second.InFlightToken;
        var secondCancelled = false;
        using var registration = secondToken.Register(() => secondCancelled = true);

        var cancellation = first.Close(RemotePageCancellationTrigger.TargetRemoved);

        Assert.True(cancellation.InFlightCancelled);
        Assert.True(firstToken.IsCancellationRequested);
        Assert.False(secondCancelled);
        Assert.False(secondToken.IsCancellationRequested);
        Assert.Equal(RemotePagePhase.Active, second.Phase);
        Assert.True(second.AdmitNewWork().Accepted);
        Assert.True(second.AdmitMessage(new RemotePageInboundMessage(second.ChannelId, second.Epoch, "chunk")).Accepted);
        Assert.Null(first.Capability);
    }

    [Fact]
    public void RevokingOneTargetsCredentialDoesNotAffectTheOtherTargetsCapability()
    {
        using var revoked = new RemotePageSessionState(RemotePageChannelId.New(), FirstPage);
        using var surviving = new RemotePageSessionState(RemotePageChannelId.New(), SecondPage);
        LoadTrusted(revoked, FirstPage);
        LoadTrusted(surviving, SecondPage);

        var cancellation = revoked.Close(RemotePageCancellationTrigger.CredentialRevoked);

        Assert.Equal(RemotePageSessionCodes.CredentialRevoked, cancellation.Reason);
        Assert.Equal(RemotePagePhase.Closed, revoked.Phase);
        Assert.Equal(RemotePageSessionCodes.SessionClosed,
            revoked.AdmitMessage(new RemotePageInboundMessage(revoked.ChannelId, revoked.Epoch, "chunk")).Code);
        Assert.Equal(RemotePagePhase.Active, surviving.Phase);
        Assert.NotNull(surviving.Capability);
        Assert.Equal(RemotePageSessionCodes.WorkAccepted, surviving.AdmitNewWork().Code);
    }

    private static void LoadTrusted(RemotePageSessionState session, string url)
    {
        Assert.True(session.BeginNavigation(url, RemoteNavigationKind.MainDocument).Trusted, url);
        Assert.True(session.CompleteMainDocumentLoad(url).Granted, url);
    }
}
