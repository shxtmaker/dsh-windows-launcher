using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 唯一消费者账本的平台中立测试（Linux 真实执行）：选举、一次性占用、
/// 原生失败/超时后禁止桥补导入，以及"同一次动作导入次数恒 ≤ 1"的可断言形式。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class NativePasteConsumerElectionTests
{
    [Fact]
    public void ANativeGestureIsConsumedExactlyOnce()
    {
        var ledger = new NativePasteConsumerLedger();

        var elected = ledger.Elect("g1", ClipboardImportRoute.NativePaste);
        var claimed = ledger.Claim("g1", NativePasteConsumer.NativePaste);

        Assert.True(elected.Accepted);
        Assert.Equal(NativePasteConsumerLedger.ElectedNativeCode, elected.Code);
        Assert.True(claimed.Accepted);
        var accounting = ledger.Accounting("g1")!.Value;
        Assert.Equal(NativePasteConsumer.NativePaste, accounting.Elected);
        Assert.Equal(NativePasteConsumer.NativePaste, accounting.Claimed);
        Assert.Equal(1, accounting.NativeImports);
        Assert.Equal(0, accounting.BridgeImports);
        Assert.Equal(1, accounting.TotalImports);
        Assert.True(accounting.ExactlyOneConsumer);
    }

    [Fact]
    public void ASecondClaimFromAnyConsumerIsRefused()
    {
        var ledger = new NativePasteConsumerLedger();
        ledger.Elect("g1", ClipboardImportRoute.NativePaste);
        Assert.True(ledger.Claim("g1", NativePasteConsumer.NativePaste).Accepted);

        var duplicateNative = ledger.Claim("g1", NativePasteConsumer.NativePaste);
        var bridgeLate = ledger.Claim("g1", NativePasteConsumer.Bridge);

        Assert.False(duplicateNative.Accepted);
        Assert.Equal(NativePasteConsumerLedger.AlreadyElectedCode, duplicateNative.Code);
        Assert.False(bridgeLate.Accepted);
        Assert.Equal(NativePasteConsumerLedger.AlreadyElectedCode, bridgeLate.Code);
        Assert.Equal(2, ledger.RefusedDuplicateClaims);
        var accounting = ledger.Accounting("g1")!.Value;
        Assert.Equal(1, accounting.TotalImports);
        Assert.True(accounting.ExactlyOneConsumer);
    }

    [Fact]
    public void AConsumerThatWasNotElectedCannotClaim()
    {
        var ledger = new NativePasteConsumerLedger();
        ledger.Elect("g1", ClipboardImportRoute.NativePaste);

        var decision = ledger.Claim("g1", NativePasteConsumer.Bridge);

        Assert.False(decision.Accepted);
        Assert.Equal(NativePasteConsumerLedger.NotElectedCode, decision.Code);
        Assert.Equal(0, ledger.Accounting("g1")!.Value.TotalImports);
    }

    [Fact]
    public void ClaimingAGestureThatWasNeverElectedIsRefused()
    {
        var ledger = new NativePasteConsumerLedger();

        var decision = ledger.Claim("g-unknown", NativePasteConsumer.NativePaste);

        Assert.False(decision.Accepted);
        Assert.Equal(NativePasteConsumerLedger.UnknownGestureCode, decision.Code);
    }

    [Fact]
    public void PureTextAndRefusalsGetNoConsumerAtAll()
    {
        var ledger = new NativePasteConsumerLedger();

        var text = ledger.Elect("g-text", ClipboardImportRoute.OriginalTextBehaviour);
        var none = ledger.Elect("g-none", ClipboardImportRoute.None);

        Assert.False(text.Accepted);
        Assert.False(none.Accepted);
        Assert.Equal(NativePasteConsumerLedger.NotElectedCode, text.Code);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void AfterANativeFailureTheBridgeCannotImportTheSameGesture()
    {
        var ledger = new NativePasteConsumerLedger();
        ledger.Elect("g1", ClipboardImportRoute.NativePaste);

        var abandoned = ledger.Abandon("g1", NativePasteOrchestrator.ClipboardBusyCode, "剪贴板被占用");
        var bridge = ledger.Claim("g1", NativePasteConsumer.Bridge);
        var native = ledger.Claim("g1", NativePasteConsumer.NativePaste);

        Assert.True(abandoned.Accepted);
        Assert.False(bridge.Accepted);
        Assert.Equal(NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode, bridge.Code);
        Assert.False(native.Accepted);
        Assert.Equal(NativePasteConsumerLedger.NativeAbandonedCode, native.Code);
        Assert.Equal(1, ledger.RefusedBridgeAfterNativeTimeout);
        var accounting = ledger.Accounting("g1")!.Value;
        Assert.True(accounting.BridgeMustNotImport);
        Assert.Equal(0, accounting.TotalImports);
        Assert.True(accounting.ExactlyOneConsumer);
    }

    [Fact]
    public void AbandoningAGestureThatNeverReachedTheLedgerStillBlocksTheBridge()
    {
        var ledger = new NativePasteConsumerLedger();

        var abandoned = ledger.Abandon("g-probe-failed", NativePasteOrchestrator.ClipboardBusyCode, "探测失败");
        var bridge = ledger.Claim("g-probe-failed", NativePasteConsumer.Bridge);

        Assert.True(abandoned.Accepted);
        Assert.False(bridge.Accepted);
        Assert.Equal(NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode, bridge.Code);
    }

    [Fact]
    public void AbandoningAnAlreadyConsumedGestureIsRefused()
    {
        var ledger = new NativePasteConsumerLedger();
        ledger.Elect("g1", ClipboardImportRoute.NativePaste);
        ledger.Claim("g1", NativePasteConsumer.NativePaste);

        var abandoned = ledger.Abandon("g1", "clipboard-busy", "太晚了");

        Assert.False(abandoned.Accepted);
        Assert.Equal(NativePasteConsumerLedger.AlreadyElectedCode, abandoned.Code);
    }

    [Fact]
    public void ABridgeGestureCanBeClaimedOnceByThePage()
    {
        var ledger = new NativePasteConsumerLedger();
        ledger.Elect("g1", ClipboardImportRoute.Bridge);

        var first = ledger.Claim("g1", NativePasteConsumer.Bridge);
        var second = ledger.Claim("g1", NativePasteConsumer.Bridge);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        var accounting = ledger.Accounting("g1")!.Value;
        Assert.Equal(1, accounting.BridgeImports);
        Assert.True(accounting.ExactlyOneConsumer);
    }

    [Fact]
    public void TheLedgerIsBounded()
    {
        var ledger = new NativePasteConsumerLedger();
        for (var index = 0; index < NativePasteConsumerLedger.MaxTrackedGestures + 5; index++)
        {
            ledger.Elect($"gesture-{index}", ClipboardImportRoute.NativePaste);
        }

        Assert.Equal(NativePasteConsumerLedger.MaxTrackedGestures, ledger.Count);
        Assert.Null(ledger.Accounting("gesture-0"));
        Assert.NotNull(ledger.Accounting($"gesture-{NativePasteConsumerLedger.MaxTrackedGestures + 4}"));
    }

    [Fact]
    public void ForgetDropsTheAccountingWithoutReopeningTheGesture()
    {
        var ledger = new NativePasteConsumerLedger();
        ledger.Elect("g1", ClipboardImportRoute.NativePaste);
        ledger.Claim("g1", NativePasteConsumer.NativePaste);

        Assert.True(ledger.Forget("g1"));
        Assert.Null(ledger.Accounting("g1"));
        Assert.False(ledger.Claim("g1", NativePasteConsumer.NativePaste).Accepted);
    }
}
