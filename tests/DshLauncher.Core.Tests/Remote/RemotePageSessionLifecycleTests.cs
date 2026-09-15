using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Remote;

/// <summary>
/// D15 页面会话状态机：代际推进、能力撤销、迟到消息拒绝、取消触发与挂起语义。
/// 全部在 Linux 真实执行；真实 WebView2/WPF 事件顺序另见 WindowsPending 清单。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemotePageSessionLifecycleTests
{
    private const string PageUrl = "https://our.host/";
    private const string NextUrl = "https://our.host/session/2";

    [Fact]
    public void ANewSessionHasNoCapabilityAndRefusesEveryNewOperation()
    {
        using var session = NewSession();

        Assert.Equal(RemotePagePhase.AwaitingDocument, session.Phase);
        Assert.Equal(RemotePageEpoch.Initial, session.Epoch);
        Assert.Null(session.Capability);
        Assert.Equal(RemotePageSessionCodes.CapabilityAbsent, session.CapabilityCode);
        Assert.Null(session.LastCancellation);
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, session.AdmitNewWork().Code);

        var message = session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, session.Epoch, "batch-begin"));
        Assert.False(message.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageCapabilityAbsent, message.Code);
    }

    [Fact]
    public void ATrustedMainDocumentGrantsCapabilityBoundToChannelEpochAndOrigin()
    {
        using var session = NewSession();

        var decision = LoadTrusted(session, PageUrl);

        Assert.True(decision.Granted);
        Assert.Equal(RemotePageSessionCodes.CapabilityGranted, decision.Code);
        Assert.Equal(RemotePagePhase.Active, session.Phase);
        Assert.Equal(session.ChannelId, decision.Capability!.Value.ChannelId);
        Assert.Equal(session.Epoch, decision.Capability.Value.Epoch);
        Assert.Equal("https://our.host", decision.Capability.Value.Origin.Normalized);
        Assert.True(session.AdmitNewWork().Accepted);

        var message = session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, session.Epoch, "file-end"));
        Assert.True(message.Accepted);
        Assert.Equal(RemotePageSessionCodes.Accepted, message.Code);
        Assert.Equal("file-end", message.Kind);
    }

    [Fact]
    public void ANavigationToAnotherOriginNeverGrantsCapability()
    {
        using var session = NewSession();

        var verdict = session.BeginNavigation("https://our.host.evil.example/", RemoteNavigationKind.MainDocument);
        var decision = session.CompleteMainDocumentLoad("https://our.host.evil.example/");

        Assert.False(verdict.Trusted);
        Assert.Equal(RemoteOriginCodes.External, verdict.Code);
        Assert.False(decision.Granted);
        Assert.Equal(RemoteOriginCodes.External, decision.Code);
        Assert.Null(session.Capability);
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, session.AdmitNewWork().Code);
    }

    [Fact]
    public void NavigationStartRevokesTheOldCapabilityAndCancelsInFlightWorkImmediately()
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        var previousEpoch = session.Epoch;
        var previousToken = session.InFlightToken;
        var cancellationObserved = false;
        using var registration = previousToken.Register(() => cancellationObserved = true);

        var verdict = session.BeginNavigation(NextUrl, RemoteNavigationKind.MainDocument);

        Assert.True(verdict.Trusted);
        Assert.True(cancellationObserved);
        Assert.True(previousToken.IsCancellationRequested);
        Assert.False(session.InFlightToken.IsCancellationRequested);
        Assert.Null(session.Capability);
        Assert.Equal(RemotePageSessionCodes.CapabilityRevokedNavigation, session.CapabilityCode);
        Assert.NotEqual(previousEpoch, session.Epoch);
        Assert.Equal(previousEpoch.Next(), session.Epoch);
        Assert.Equal(RemotePagePhase.AwaitingDocument, session.Phase);
        Assert.Equal(RemotePageCancellationTrigger.NavigationStarted, session.LastCancellation!.Value.Trigger);
        Assert.Equal(previousEpoch, session.LastCancellation.Value.Epoch);
        Assert.Equal(RemotePageSessionCodes.NavigationStarted, session.LastCancellation.Value.Reason);

        // 新文档还没加载完，所以现在既不能开新操作，也没有能力。
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, session.AdmitNewWork().Code);
    }

    [Fact]
    public void ALateMessageFromThePreviousPageIsRefusedWithEpochStale()
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        var staleEpoch = session.Epoch;
        var channel = session.ChannelId;

        session.BeginNavigation(NextUrl, RemoteNavigationKind.MainDocument);
        var late = session.AdmitMessage(new RemotePageInboundMessage(channel, staleEpoch, "chunk"));
        Assert.False(late.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale, late.Code);

        // 新文档验证完成后，旧代际的消息仍然作废。
        var decision = LoadTrusted(session, NextUrl);
        Assert.True(decision.Granted);
        Assert.NotEqual(staleEpoch, session.Epoch);
        var stillLate = session.AdmitMessage(new RemotePageInboundMessage(channel, staleEpoch, "chunk"));
        Assert.False(stillLate.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale, stillLate.Code);

        // 同一通道、当前代际的消息才被接受。
        Assert.True(session.AdmitMessage(new RemotePageInboundMessage(channel, session.Epoch, "chunk")).Accepted);
    }

    [Fact]
    public void MessagesFromAFutureEpochAreRefusedToo()
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);

        var future = session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, session.Epoch.Next(), "chunk"));

        Assert.False(future.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale, future.Code);
    }

    [Fact]
    public void AnExternalLinkRevokesCapabilityAndItsCompletionNeverRegrantsIt()
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        var previousEpoch = session.Epoch;
        var previousToken = session.InFlightToken;

        var verdict = session.BeginExternalLink("https://evil.example/");

        Assert.False(verdict.Trusted);
        Assert.Equal(RemoteOriginCodes.ExternalNavigation, verdict.Code);
        Assert.True(previousToken.IsCancellationRequested);
        Assert.Null(session.Capability);
        Assert.True(session.Epoch.Value > previousEpoch.Value);

        var completion = session.CompleteMainDocumentLoad("https://evil.example/");
        Assert.False(completion.Granted);
        Assert.Equal(RemoteOriginCodes.ExternalNavigation, completion.Code);
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, session.AdmitNewWork().Code);

        // 即使外部导航最终又落回我们的来源，也不在这次导航里继承能力。
        session.BeginExternalLink("https://our.host/popup");
        var backToOurOrigin = session.CompleteMainDocumentLoad("https://our.host/popup");
        Assert.False(backToOurOrigin.Granted);
        Assert.Equal(RemoteOriginCodes.ExternalNavigation, backToOurOrigin.Code);
        Assert.Null(session.Capability);

        // 之后一次正常的主文档导航才会重新授予。
        Assert.True(LoadTrusted(session, PageUrl).Granted);
    }

    [Fact]
    public void SubresourcesAndChildFramesNeitherGrantNorRevokeCapability()
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        var epoch = session.Epoch;
        var token = session.InFlightToken;
        var capability = session.Capability;
        var cancellationBefore = session.LastCancellation;

        foreach (var kind in new[] { RemoteNavigationKind.ChildFrame, RemoteNavigationKind.Subresource })
        {
            foreach (var url in new[] { "https://evil.example/frame", "https://our.host/widget" })
            {
                var verdict = session.BeginNavigation(url, kind);
                Assert.False(verdict.Trusted, url);
                Assert.False(verdict.MainDocument, url);
                Assert.Equal(RemoteOriginCodes.NotMainDocument, verdict.Code);
            }
        }

        Assert.Equal(epoch, session.Epoch);
        Assert.Equal(capability, session.Capability);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(cancellationBefore, session.LastCancellation);
        Assert.True(session.AdmitNewWork().Accepted);
        Assert.True(session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, epoch, "chunk")).Accepted);
    }

    [Theory]
    [InlineData(RemotePageCancellationTrigger.TargetRemoved, RemotePageSessionCodes.TargetRemoved)]
    [InlineData(RemotePageCancellationTrigger.CredentialRevoked, RemotePageSessionCodes.CredentialRevoked)]
    [InlineData(RemotePageCancellationTrigger.ApplicationExit, RemotePageSessionCodes.ApplicationExit)]
    public void ClosingTriggersCancelInFlightWorkAndRejectLateResults(
        RemotePageCancellationTrigger trigger,
        string expectedReason)
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        var epoch = session.Epoch;
        var channel = session.ChannelId;
        var token = session.InFlightToken;
        var cancellationObserved = false;
        using var registration = token.Register(() => cancellationObserved = true);

        var cancellation = session.Close(trigger);

        Assert.True(cancellationObserved);
        Assert.True(cancellation.InFlightCancelled);
        Assert.Equal(trigger, cancellation.Trigger);
        Assert.Equal(expectedReason, cancellation.Reason);
        Assert.Equal(epoch, cancellation.Epoch);
        Assert.Equal(RemotePagePhase.Closed, session.Phase);
        Assert.Null(session.Capability);
        Assert.Equal(RemotePageSessionCodes.CapabilityRevokedClosed, session.CapabilityCode);
        Assert.Equal(2, session.CancelledEpochCount);

        var late = session.AdmitMessage(new RemotePageInboundMessage(channel, epoch, "chunk"));
        Assert.False(late.Accepted);
        Assert.Equal(RemotePageSessionCodes.SessionClosed, late.Code);
        Assert.Equal(RemotePageSessionCodes.SessionClosed, session.AdmitNewWork().Code);
        Assert.False(session.BeginNavigation(PageUrl, RemoteNavigationKind.MainDocument).Trusted);

        // 重复关闭安全：不再取消一次，也返回同一事实。
        var again = session.Close(trigger);
        Assert.Equal(cancellation, again);
        Assert.Equal(2, session.CancelledEpochCount);
        Assert.True(session.InFlightToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(RemotePageSuspensionReason.WindowHidden)]
    [InlineData(RemotePageSuspensionReason.TabSwitched)]
    public void SuspensionRefusesNewWorkButKeepsInFlightWorkAlive(RemotePageSuspensionReason reason)
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        var epoch = session.Epoch;
        var channel = session.ChannelId;
        var token = session.InFlightToken;
        var cancellationBefore = session.LastCancellation;
        var cancellationObserved = false;
        using var registration = token.Register(() => cancellationObserved = true);

        Assert.True(session.SuspendForNewWork(reason));

        Assert.Equal(RemotePagePhase.Suspended, session.Phase);
        Assert.Equal(reason, session.SuspensionReason);
        var refusal = session.AdmitNewWork();
        Assert.False(refusal.Accepted);
        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, refusal.Code);

        // 方案：已授权任务继续绑定原草稿 —— 不取消、不提代际、在途回执仍被接受。
        Assert.False(cancellationObserved);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(epoch, session.Epoch);
        Assert.Equal(cancellationBefore, session.LastCancellation);
        Assert.True(session.AdmitMessage(new RemotePageInboundMessage(channel, epoch, "ack")).Accepted);

        Assert.True(session.ResumeNewWork());
        Assert.Equal(RemotePagePhase.Active, session.Phase);
        Assert.Null(session.SuspensionReason);
        Assert.True(session.AdmitNewWork().Accepted);
    }

    [Fact]
    public void SuspensionDuringTheFirstLoadDelaysNewWorkUntilResumed()
    {
        using var session = NewSession();

        Assert.True(session.SuspendForNewWork(RemotePageSuspensionReason.WindowHidden));
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, session.AdmitNewWork().Code);

        Assert.True(LoadTrusted(session, PageUrl).Granted);
        Assert.Equal(RemotePagePhase.Suspended, session.Phase);
        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, session.AdmitNewWork().Code);

        Assert.True(session.ResumeNewWork());
        Assert.Equal(RemotePagePhase.Active, session.Phase);
        Assert.True(session.AdmitNewWork().Accepted);
    }

    [Fact]
    public void ANavigationDuringSuspensionStillRevokesAndStaysSuspendedAfterTheNewDocumentLoads()
    {
        using var session = NewSession();
        LoadTrusted(session, PageUrl);
        session.SuspendForNewWork(RemotePageSuspensionReason.TabSwitched);
        var staleEpoch = session.Epoch;

        session.BeginNavigation(NextUrl, RemoteNavigationKind.MainDocument);

        Assert.Null(session.Capability);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale,
            session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, staleEpoch, "chunk")).Code);

        Assert.True(LoadTrusted(session, NextUrl).Granted);
        Assert.Equal(RemotePagePhase.Suspended, session.Phase);
        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, session.AdmitNewWork().Code);
        Assert.True(session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, session.Epoch, "chunk")).Accepted);
    }

    [Fact]
    public void AnUntrustedTargetAddressFailsClosedForEveryDecision()
    {
        using var session = new RemotePageSessionState(RemotePageChannelId.New(), "file:///C:/harness/index.html");

        Assert.Null(session.TrustedOrigin);
        Assert.Equal(RemoteOriginCodes.UnsupportedScheme, session.TrustedOriginCode);
        Assert.False(session.BeginNavigation(PageUrl, RemoteNavigationKind.MainDocument).Trusted);
        Assert.False(session.CompleteMainDocumentLoad(PageUrl).Granted);
        Assert.Equal(RemoteOriginCodes.Invalid, session.CompleteMainDocumentLoad(PageUrl).Code);
        Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, session.AdmitNewWork().Code);
        Assert.Equal(RemotePageSessionCodes.MessageCapabilityAbsent,
            session.AdmitMessage(new RemotePageInboundMessage(session.ChannelId, session.Epoch, "hello")).Code);
    }

    [Fact]
    public void DisposeClosesTheSessionAndCancelsWorkExactlyOnce()
    {
        var session = NewSession();
        LoadTrusted(session, PageUrl);
        var token = session.InFlightToken;

        session.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(RemotePagePhase.Closed, session.Phase);
        Assert.Equal(RemotePageCancellationTrigger.ApplicationExit, session.LastCancellation!.Value.Trigger);
        Assert.Equal(2, session.CancelledEpochCount);
        Assert.True(session.InFlightToken.IsCancellationRequested);

        session.Dispose();
        Assert.Equal(2, session.CancelledEpochCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyChannelIdIsRefused(string channelId) =>
        Assert.Throws<ArgumentException>(() => new RemotePageSessionState(channelId, PageUrl));

    private static RemotePageSessionState NewSession() =>
        new(RemotePageChannelId.New(), PageUrl);

    private static RemotePageCapabilityDecision LoadTrusted(RemotePageSessionState session, string url)
    {
        var verdict = session.BeginNavigation(url, RemoteNavigationKind.MainDocument);
        Assert.True(verdict.Trusted, url);
        return session.CompleteMainDocumentLoad(url);
    }
}
