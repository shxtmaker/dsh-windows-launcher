using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 生产编排器（<see cref="NativePasteOrchestrator"/>）的 Linux 可执行测试：
/// 用假剪贴板端口驱动<b>真实生产状态机</b>，逐条验证手势授权 → 导入闸门 → 探测 →
/// 来源优先 → 唯一消费者 → 读取/占用的完整链路，以及"无会话不读剪贴板""原生失败后桥不得补导入"。
/// 真实 Win32 剪贴板读取属 WindowsPending。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class NativePasteOrchestratorTests
{
    [Fact]
    public async Task AnInactiveWindowNeverReachesTheClipboard()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(windowActive: false),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(NativePasteCodes.GestureWindowInactive, outcome.Code);
        Assert.Equal(0, port.ProbeCount);
        Assert.Equal(0, ledger.Count);
        Assert.True(outcome.KeepsBrowserDefault);
    }

    [Fact]
    public async Task NoSessionRefusesWithoutTouchingTheClipboardOrCreatingOne()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(sessionId: null),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal("no-session", outcome.Code);
        Assert.True(outcome.Recoverable);
        Assert.Equal(NativePasteRetry.RetryAfterUserAction, outcome.Retry);
        Assert.Equal(0, port.ProbeCount);
        Assert.Equal(0, port.ReadCount);
        // 墓碑：即使没有会话，同一次动作也不能被桥补导入。
        Assert.Equal(
            NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode,
            ledger.Claim("gesture-1", NativePasteConsumer.Bridge).Code);
    }

    [Fact]
    public async Task ALockedComposerRefusesWithoutReadingTheClipboard()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out _, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(locked: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(AttachmentImportGate.ComposerLockedCode, outcome.Code);
        Assert.True(outcome.Recoverable);
        Assert.Equal(0, port.ProbeCount);
    }

    [Fact]
    public async Task ASubagentPromptRefusesWithoutReadingTheClipboard()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out _, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(subagent: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(AttachmentImportGate.SubagentPromptCode, outcome.Code);
        Assert.Equal(0, port.ProbeCount);
    }

    [Fact]
    public async Task APageThatRefusesNewWorkPropagatesItsOwnCode()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out _, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(admitted: false, admissionCode: RemotePageSessionCodes.WorkPageSuspended),
            TestContext.Current.CancellationToken);

        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, outcome.Code);
        Assert.True(outcome.Recoverable);
        Assert.Equal(0, port.ProbeCount);
    }

    [Fact]
    public async Task ABusyClipboardIsARecoverableResultThatTheBridgeCannotPickUp()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult
            {
                Ok = false,
                Code = NativePasteOrchestrator.ClipboardBusyCode,
                Detail = "剪贴板被其他进程占用",
                OpenAttempts = 3,
            },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(NativePasteOrchestrator.ClipboardBusyCode, outcome.Code);
        Assert.True(outcome.Recoverable);
        Assert.Equal(NativePasteRetry.RetrySameGesture, outcome.Retry);
        Assert.Equal(0, port.ReadCount);
        var bridge = ledger.Claim("gesture-1", NativePasteConsumer.Bridge);
        Assert.False(bridge.Accepted);
        Assert.Equal(NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode, bridge.Code);
    }

    [Fact]
    public async Task PlainTextIsLeftToTheBrowserWithoutAnyAttachmentWork()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.TextProbe() },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(NativePasteOrchestrator.TextPassThroughCode, outcome.Code);
        Assert.Equal(ClipboardImportRoute.OriginalTextBehaviour, outcome.Route);
        Assert.True(outcome.KeepsBrowserDefault);
        Assert.Equal(0, port.ReadCount);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public async Task AFileListGestureImportsExactlyOnceAndOffTheSta()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.FileListProbe(fileCount: 2, bitmap: true) },
            ReadResult = new ClipboardReadResult
            {
                Ok = true,
                Kind = ClipboardSourceKind.FileListAndBitmap,
                CaptureIds = ["capture-1", "capture-2"],
                FileCount = 2,
                ReadOffSta = true,
            },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Accepted);
        Assert.Equal(NativePasteOrchestrator.ImportedCode, outcome.Code);
        Assert.Equal(ClipboardImportRoute.NativePaste, outcome.Route);
        Assert.Equal(NativePasteConsumer.NativePaste, outcome.Consumer);
        Assert.Equal(["capture-1", "capture-2"], outcome.CaptureIds);
        Assert.Equal(2, outcome.FileCount);
        Assert.Equal(ClipboardSourcePolicy.FileListPreferredCode, port.LastDecision!.Code);
        Assert.True(outcome.ReadOffSta);
        var accounting = ledger.Accounting("gesture-1")!.Value;
        Assert.Equal(1, accounting.TotalImports);
        Assert.True(accounting.ExactlyOneConsumer);
    }

    [Fact]
    public async Task ASecondPushOfTheSamePlanIsRefusedAsAReplay()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out _, out _, out _);
        var evidence = NativePasteFixture.Evidence();
        var plan = orchestrator.Plan(evidence, NativePasteFixture.Context());

        var first = await orchestrator.ExecuteAsync(plan, port, TestContext.Current.CancellationToken);
        var replay = await orchestrator.ExecuteAsync(plan, port, TestContext.Current.CancellationToken);

        Assert.True(first.Accepted);
        Assert.False(replay.Accepted);
        Assert.Equal(NativePasteCodes.AuthorizationAbsent, replay.Code);
        // 重放不是"重试同一个手势"，而是要用户重新做一次手势。
        Assert.True(replay.Recoverable);
        Assert.Equal(NativePasteRetry.RetryAfterUserAction, replay.Retry);
        Assert.Equal(1, port.ReadCount);
    }

    [Fact]
    public async Task AReadFailureIsRecoverableAndBlocksALateBridgeImport()
    {
        var port = new FakeClipboardPort
        {
            ReadResult = new ClipboardReadResult
            {
                Ok = false,
                Code = NativePasteOrchestrator.ClipboardChangedCode,
                Detail = "剪贴板在探测之后变化",
                Kind = ClipboardSourceKind.FileList,
                ReadOffSta = true,
            },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(NativePasteOrchestrator.ClipboardChangedCode, outcome.Code);
        Assert.True(outcome.Recoverable);
        var accounting = ledger.Accounting("gesture-1")!.Value;
        Assert.True(accounting.BridgeMustNotImport);
        Assert.Equal(0, accounting.TotalImports);
        Assert.False(ledger.Claim("gesture-1", NativePasteConsumer.Bridge).Accepted);
    }

    [Fact]
    public async Task AStalledNativeReadTimesOutAndBlocksTheBridge()
    {
        var port = new FakeClipboardPort
        {
            ReadHandler = static async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new ClipboardReadResult { Ok = false };
            },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _, readTimeoutMs: 30);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(NativePasteOrchestrator.ClipboardTimeoutCode, outcome.Code);
        Assert.True(outcome.Recoverable);
        Assert.Equal(NativePasteRetry.RetryAfterUserAction, outcome.Retry);
        Assert.Equal(1, port.ReadCount);
        var bridge = ledger.Claim("gesture-1", NativePasteConsumer.Bridge);
        Assert.False(bridge.Accepted);
        Assert.Equal(NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode, bridge.Code);
        Assert.Equal(1, ledger.RefusedBridgeAfterNativeTimeout);
    }

    [Fact]
    public async Task AnExpiredGestureAuthorizationIsRefusedBeforeTheRead()
    {
        var port = new FakeClipboardPort();
        var ledger = new NativePasteConsumerLedger();
        var owners = new ScreenshotOwnerRegistry();
        var clock = new FakeTimeProvider();
        var orchestrator = new NativePasteOrchestrator(
            port,
            new NativeGestureAuthorizer(),
            ledger,
            owners,
            limits: null,
            timeProvider: clock);
        var plan = orchestrator.Plan(NativePasteFixture.Evidence(), NativePasteFixture.Context());

        clock.Advance(TimeSpan.FromMilliseconds(NativeGestureAuthorizer.DefaultLifetimeMs + 1));
        var outcome = await orchestrator.ExecuteAsync(plan, port, TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(NativePasteCodes.AuthorizationExpired, outcome.Code);
        Assert.Equal(0, port.ReadCount);
        Assert.False(ledger.Claim("gesture-1", NativePasteConsumer.Bridge).Accepted);
    }

    [Fact]
    public async Task BitmapInNativeModeIsReadAndCappedByTheFrozenPixelLimit()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.BitmapProbe(100, 50) },
            ReadResult = new ClipboardReadResult
            {
                Ok = true,
                Kind = ClipboardSourceKind.Bitmap,
                CaptureIds = ["capture-shot"],
                FileCount = 1,
                BitmapWidth = 100,
                BitmapHeight = 50,
                PixelCount = 5_000,
                EncodedBytes = 512,
                ReadOffSta = true,
            },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out var owners, out _);
        owners.Record(NativePasteFixture.TargetA, new RemotePageEpoch(1), new ScreenshotOwnerFacts(true, true, true));

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Accepted);
        Assert.Equal(ClipboardImportRoute.NativePaste, outcome.Route);
        Assert.Equal(ClipboardSourcePolicy.BitmapNativePasteCode, port.LastDecision!.Code);
        Assert.Equal(5_000, outcome.PixelCount);
        Assert.Equal(512, outcome.EncodedBytes);
        Assert.True(outcome.ReadOffSta);
        Assert.Equal(1, ledger.Accounting("gesture-1")!.Value.TotalImports);
    }

    [Fact]
    public async Task BitmapInBridgeModeNeverTouchesTheClipboardContent()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.BitmapProbe() },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out var owners, out _);
        owners.Record(NativePasteFixture.TargetA, new RemotePageEpoch(1), new ScreenshotOwnerFacts(true, false, true));

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Accepted);
        Assert.Equal(ClipboardImportRoute.Bridge, outcome.Route);
        Assert.Equal(NativePasteConsumer.Bridge, outcome.Consumer);
        Assert.True(outcome.KeepsBrowserDefault);
        Assert.Equal(0, port.ReadCount);
        var accounting = ledger.Accounting("gesture-1")!.Value;
        Assert.Equal(1, accounting.BridgeImports);
        Assert.True(accounting.ExactlyOneConsumer);
    }

    [Fact]
    public async Task BitmapWithoutAHandshakeIsRefusedAndTheClipboardIsNotRead()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.BitmapProbe() },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var outcome = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Accepted);
        Assert.Equal(ClipboardSourcePolicy.ModeUndecidedCode, outcome.Code);
        Assert.Equal(0, port.ReadCount);
        Assert.False(ledger.Claim("gesture-1", NativePasteConsumer.Bridge).Accepted);
    }

    [Fact]
    public async Task TheScreenshotOwnerModeOnlyAffectsLaterGestures()
    {
        var port = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.BitmapProbe() },
        };
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out var owners, out _);
        owners.Record(NativePasteFixture.TargetA, new RemotePageEpoch(1), new ScreenshotOwnerFacts(true, true, true));

        var first = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(gestureId: "gesture-native"),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);
        owners.Record(NativePasteFixture.TargetA, new RemotePageEpoch(1), new ScreenshotOwnerFacts(true, false, true));
        var second = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(gestureId: "gesture-bridge"),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ClipboardImportRoute.NativePaste, first.Route);
        Assert.Equal(ClipboardImportRoute.Bridge, second.Route);
        Assert.Equal(ClipboardImportRoute.NativePaste, ledger.Accounting("gesture-native")!.Value.Route);
        Assert.Equal(1, port.ReadCount);
    }

    [Fact]
    public async Task ADropGestureUsesTheDropSourceAndCountsAsOneConsumer()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);
        var evidence = NativePasteFixture.Evidence(kind: NativeGestureKind.DragDrop);

        var plan = orchestrator.PlanDrop(evidence, NativePasteFixture.Context(), hasFileList: true);
        Assert.True(plan.NeedsRead);
        var outcome = await orchestrator.ExecuteAsync(plan, port, TestContext.Current.CancellationToken);

        Assert.True(outcome.Accepted);
        Assert.Equal(NativeGestureKind.DragDrop, outcome.GestureKind);
        Assert.Equal(0, port.ProbeCount);   // 拖放不经过剪贴板
        Assert.Equal(1, ledger.Accounting("gesture-1")!.Value.TotalImports);
    }

    [Fact]
    public async Task ADropWithoutAFileListIsRefusedWithoutAnyRead()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var plan = orchestrator.PlanDrop(
            NativePasteFixture.Evidence(kind: NativeGestureKind.DragDrop),
            NativePasteFixture.Context(),
            hasFileList: false);

        Assert.NotNull(plan.Immediate);
        Assert.Equal(ClipboardSourcePolicy.UnsupportedCode, plan.Immediate!.Code);
        Assert.False(plan.NeedsRead);
        Assert.Equal(0, port.ProbeCount);
        Assert.False(ledger.Claim("gesture-1", NativePasteConsumer.Bridge).Accepted);
    }

    [Fact]
    public async Task EachGestureGetsItsOwnAuthorizationSoASecondPasteWorks()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out var ledger, out _, out _);

        var first = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(gestureId: "gesture-a"),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);
        var second = await orchestrator.HandleAsync(
            NativePasteFixture.Evidence(gestureId: "gesture-b"),
            NativePasteFixture.Context(),
            TestContext.Current.CancellationToken);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(2, port.ReadCount);
        Assert.Equal(1, ledger.Accounting("gesture-a")!.Value.TotalImports);
        Assert.Equal(1, ledger.Accounting("gesture-b")!.Value.TotalImports);
    }

    [Fact]
    public async Task ThePlanSuppressesTheBrowserDefaultOnlyForTheNativeRoute()
    {
        var port = new FakeClipboardPort();
        var orchestrator = NativePasteFixture.Orchestrator(port, out _, out var owners, out _);

        var nativePlan = orchestrator.Plan(NativePasteFixture.Evidence(gestureId: "g-native"), NativePasteFixture.Context());
        Assert.True(nativePlan.SuppressesBrowserDefault);

        var textPort = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.TextProbe() },
        };
        var textOrchestrator = NativePasteFixture.Orchestrator(textPort, out _, out _, out _);
        var textPlan = textOrchestrator.Plan(
            NativePasteFixture.Evidence(gestureId: "g-text"),
            NativePasteFixture.Context());
        Assert.False(textPlan.SuppressesBrowserDefault);
        Assert.NotNull(textPlan.Immediate);

        owners.Record(NativePasteFixture.TargetA, new RemotePageEpoch(1), new ScreenshotOwnerFacts(true, false, true));
        var bridgePort = new FakeClipboardPort
        {
            ProbeResult = new ClipboardProbeResult { Ok = true, Probe = NativePasteFixture.BitmapProbe() },
        };
        var bridgeOrchestrator = NativePasteFixture.Orchestrator(bridgePort, out _, out var bridgeOwners, out _);
        bridgeOwners.Record(NativePasteFixture.TargetA, new RemotePageEpoch(1), new ScreenshotOwnerFacts(true, false, true));
        var bridgePlan = bridgeOrchestrator.Plan(
            NativePasteFixture.Evidence(gestureId: "g-bridge"),
            NativePasteFixture.Context());
        Assert.False(bridgePlan.SuppressesBrowserDefault);
        Assert.Equal(ClipboardImportRoute.Bridge, bridgePlan.Immediate!.Route);
    }
}
