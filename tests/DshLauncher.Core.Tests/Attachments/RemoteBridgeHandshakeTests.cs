using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D17 握手接通（Linux 真实执行）：<c>hello</c> / <c>capabilities</c> / <c>context</c> 的真实解析，
/// 路由到 D16 的真实消费者（截图所有者登记表 + composer 上下文台账），
/// 并证明 D16 登记的缺口已闭合——位图粘贴在握手前后有<b>不同且确定</b>的路由。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemoteBridgeHandshakeTests
{
    [Fact]
    public void HelloRecordsThePeerBuildWithoutBindingAnIdentity()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();
        rig.Composition.StartHandshake();

        var result = rig.DeliverPageFrame("hello", new JsonObject { ["clientBuild"] = "addon/0.1.0" });

        Assert.True(result.Accepted);
        Assert.True(result.Handshake);
        Assert.Equal("addon/0.1.0", rig.Composition.PeerBuild);
        Assert.Null(rig.Composition.Identity);
        Assert.Equal(RemoteBridgePhase.Handshaking, rig.Composition.Phase);
    }

    [Fact]
    public void CapabilitiesWithoutTheScreenshotFeatureDecideTheBridgeAndEffLowToThePage()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        var result = rig.DeliverPageFrame("capabilities", RemoteBridgeRig.CapabilitiesFields(screenshotFeature: false));

        Assert.True(result.Accepted);
        Assert.Equal(ScreenshotOwnerMode.Bridge, rig.Composition.ScreenshotMode);
        Assert.Equal(ScreenshotOwnerHandshake.NoFeatureCode, rig.Composition.ScreenshotDecision.Code);
        Assert.False(rig.Sink.ScreenshotCalls[0].PluginAdvertisesScreenshot);
    }

    [Fact]
    public void CapabilitiesWithTheScreenshotFeatureDecideNativePaste()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        rig.DeliverPageFrame("capabilities", RemoteBridgeRig.CapabilitiesFields(screenshotFeature: true));

        Assert.Equal(ScreenshotOwnerMode.NativePaste, rig.Composition.ScreenshotMode);
        Assert.Equal(ScreenshotOwnerHandshake.NativeRequestedCode, rig.Composition.ScreenshotDecision.Code);
        Assert.True(rig.Sink.ScreenshotCalls[0].PluginAdvertisesScreenshot);
        Assert.True(rig.Sink.ScreenshotCalls[0].PluginPrefersNativePaste);
    }

    [Fact]
    public void CapabilitiesStayOnTheBridgeWhenTheHostHasNoNativeCapture()
    {
        using var rig = new RemoteBridgeRig(nativeCaptureAvailable: false);
        rig.LoadDocument();

        rig.DeliverPageFrame("capabilities", RemoteBridgeRig.CapabilitiesFields(screenshotFeature: true));

        Assert.Equal(ScreenshotOwnerMode.Bridge, rig.Composition.ScreenshotMode);
        Assert.Equal(ScreenshotOwnerHandshake.NativeUnavailableCode, rig.Composition.ScreenshotDecision.Code);
    }

    [Fact]
    public void TheScreenshotDecisionLandsInTheRealOwnerRegistryKeyedByPageEpoch()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        rig.DeliverPageFrame("capabilities", RemoteBridgeRig.CapabilitiesFields(screenshotFeature: true));

        // 真实消费者：D16 的截图所有者登记表（原生路由在每次手势时就读它）。
        Assert.Equal(ScreenshotOwnerMode.NativePaste, rig.Sink.Owners.ModeFor(rig.TargetId, rig.Page.Epoch));
        Assert.Equal(ScreenshotOwnerMode.Undecided, rig.Sink.Owners.ModeFor(NativePasteFixture.TargetB, rig.Page.Epoch));
        // 代际不符即回到"未决"：旧页面的握手不会给新页面带来模式。
        Assert.Equal(
            ScreenshotOwnerMode.Undecided,
            rig.Sink.Owners.ModeFor(rig.TargetId, rig.Page.Epoch.Next()));
    }

    [Fact]
    public void ContextBindsTheIdentityAndLandsInTheRealComposerContextStore()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();
        rig.DeliverPageFrame("capabilities", RemoteBridgeRig.CapabilitiesFields());

        var result = rig.DeliverPageFrame("context", RemoteBridgeRig.ContextFields(sessionId: "session-42", composerEpoch: 7));

        Assert.True(result.Accepted);
        Assert.Equal(RemoteBridgePhase.Ready, rig.Composition.Phase);
        var identity = rig.Composition.Identity;
        Assert.NotNull(identity);
        Assert.Equal("session-42", identity!.SessionId);
        Assert.Equal("page-target-1", identity.TargetId);
        Assert.Equal(7, identity.ComposerEpoch);
        Assert.Equal("scope-1", identity.ComposerScope);

        // 真实消费者：D16 的 composer 上下文台账（导入闸门读它）。
        var facts = rig.Sink.Contexts.Current(rig.TargetId, rig.Page.Epoch);
        Assert.Equal("session-42", facts.SessionId);
        Assert.True(facts.ComposerEditable);
        Assert.True(facts.PageAdmitted);
        Assert.Equal(AttachmentImportGate.AllowedCode, AttachmentImportGate.Evaluate(facts).Code);
        // 宿主回执 context：页面据此绑定同一身份。
        var echo = AttachmentCodec.Decode(rig.Transport.SentFrames[^1]);
        Assert.True(echo.Ok);
        Assert.Equal(WireMessageKind.Context, echo.Message!.Kind);
        Assert.Equal("session-42", echo.Message.SessionId);
    }

    [Fact]
    public void ContextWithoutCapabilitiesIsRefusedAndBindsNothing()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        var result = rig.DeliverPageFrame("context", RemoteBridgeRig.ContextFields());

        Assert.False(result.Accepted);
        Assert.Equal(RemoteBridgeCodes.HandshakeIncomplete, result.Code);
        Assert.Null(rig.Composition.Identity);
        Assert.Empty(rig.Sink.ContextCalls);
        Assert.Equal(RemoteBridgePhase.Detached, rig.Composition.Phase);
    }

    [Fact]
    public void ASecondContextWithADifferentIdentityIsRefusedAndDoesNotRebind()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake(sessionId: "session-1");

        var result = rig.DeliverPageFrame("context", RemoteBridgeRig.ContextFields(sessionId: "session-2", composerEpoch: 9));

        Assert.False(result.Accepted);
        Assert.Equal(RemoteBridgeCodes.SecondContext, result.Code);
        Assert.Equal("session-1", rig.Composition.Identity!.SessionId);
        Assert.Single(rig.Sink.ContextCalls);
    }

    [Fact]
    public void ADuplicateContextIsIdempotentAndDoesNotResendTheHostEcho()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        var sentBefore = rig.Transport.SentFrames.Count;

        var result = rig.DeliverPageFrame("context", RemoteBridgeRig.ContextFields());

        Assert.True(result.Accepted);
        Assert.Equal(sentBefore, rig.Transport.SentFrames.Count);
        Assert.Single(rig.Sink.ContextCalls);
    }

    [Fact]
    public void PeerLimitsClampTheEffectiveLimits()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        rig.DeliverPageFrame(
            "capabilities",
            RemoteBridgeRig.CapabilitiesFields(maxFileBytes: 1024));

        Assert.Equal(1024, rig.Composition.EffectiveLimits.MaxFileBytes);
        Assert.Equal(AttachmentProtocol.MaxFilesPerBatch, rig.Composition.EffectiveLimits.MaxFilesPerBatch);
        Assert.Equal(1024, rig.Composition.Capabilities!.DeclaredLimits.MaxFileBytes);
    }

    [Fact]
    public void AContextFromAStalePageGenerationNeverBindsANewIdentity()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        var secondGenerationContext = RemoteBridgeRig.ContextFields(sessionId: "session-2");

        rig.Page.BeginNavigation(RemoteBridgeRig.PageUrl, RemoteNavigationKind.MainDocument);
        rig.Composition.OnPageAdvanced();
        var result = rig.Deliver(rig.Envelope(secondGenerationContext.ToJsonString(), epoch: 1));

        Assert.False(result.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale, result.Code);
        Assert.Null(rig.Composition.Identity);
    }

    [Fact]
    public void ABitmapPasteBeforeTheHandshakeIsRefusedWithConsumerModeUndecided()
    {
        using var rig = new RemoteBridgeRig();
        var orchestrator = BitmapOrchestrator(rig, out var ledger);

        var plan = orchestrator.Plan(
            NativePasteFixture.Evidence(gestureId: "g-before", targetId: rig.TargetId, epoch: rig.Page.Epoch.Value),
            NativePasteFixture.Context());

        // D16 登记的缺口：握手到达之前一律确定拒绝，绝不猜。
        Assert.NotNull(plan.Immediate);
        Assert.False(plan.Immediate!.Accepted);
        Assert.Equal(ClipboardSourcePolicy.ModeUndecidedCode, plan.Immediate.Code);
        Assert.Equal(NativePasteConsumer.None, plan.Immediate.Consumer);
        Assert.Equal(0, ledger.Accounting("g-before")!.Value.TotalImports);
    }

    [Fact]
    public void ABitmapPasteAfterABridgeHandshakeGoesToThePageWithoutReadingTheClipboard()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake(screenshotFeature: false);
        var orchestrator = BitmapOrchestrator(rig, out _);

        var plan = orchestrator.Plan(
            NativePasteFixture.Evidence(gestureId: "g-bridge", targetId: rig.TargetId, epoch: rig.Page.Epoch.Value),
            NativePasteFixture.Context());

        Assert.NotNull(plan.Immediate);
        Assert.True(plan.Immediate!.Accepted);
        Assert.Equal(ClipboardImportRoute.Bridge, plan.Immediate.Route);
        Assert.Equal(NativePasteOrchestrator.DelegatedToBridgeCode, plan.Immediate.Code);
        Assert.True(plan.Immediate.KeepsBrowserDefault);
        Assert.Equal(ClipboardSourcePolicy.BitmapBridgeCode, plan.Decision.Code);
    }

    [Fact]
    public void ABitmapPasteAfterANativeHandshakeIsOwnedByTheHost()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake(screenshotFeature: true);
        var orchestrator = BitmapOrchestrator(rig, out _);

        var plan = orchestrator.Plan(
            NativePasteFixture.Evidence(gestureId: "g-native", targetId: rig.TargetId, epoch: rig.Page.Epoch.Value),
            NativePasteFixture.Context());

        Assert.Null(plan.Immediate);
        Assert.True(plan.NeedsRead);
        Assert.True(plan.SuppressesBrowserDefault);
        Assert.Equal(ClipboardImportRoute.NativePaste, plan.Decision.Route);
        Assert.Equal(ClipboardSourcePolicy.BitmapNativePasteCode, plan.Decision.Code);
    }

    /// <summary>用<b>真实</b> D16 编排器 + 桥接进来的截图所有者台账驱动一次位图手势。</summary>
    private static NativePasteOrchestrator BitmapOrchestrator(RemoteBridgeRig rig, out NativePasteConsumerLedger ledger)
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.BitmapProbe() },
        };
        ledger = new NativePasteConsumerLedger();
        return new NativePasteOrchestrator(
            port,
            new NativeGestureAuthorizer(),
            ledger,
            rig.Sink.Owners,
            new AttachmentLimits(),
            new FakeTimeProvider());
    }
}
