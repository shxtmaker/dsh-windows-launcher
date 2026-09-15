using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 失败分类与导入闸门的平台中立测试（Linux 真实执行）：
/// 剪贴板 busy/畸形/不支持、锁定/子代理/无会话都是<b>确定且可恢复</b>的结果，
/// 未登记的码 fail-closed，唯一消费者不变量一律不可重试。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class NativePasteFailureClassificationTests
{
    [Theory]
    [InlineData(NativePasteOrchestrator.ClipboardBusyCode, NativePasteRetry.RetrySameGesture)]
    [InlineData(NativePasteOrchestrator.ClipboardOpenFailedCode, NativePasteRetry.RetrySameGesture)]
    [InlineData(NativePasteOrchestrator.ClipboardChangedCode, NativePasteRetry.RetrySameGesture)]
    [InlineData(NativePasteOrchestrator.ClipboardTimeoutCode, NativePasteRetry.RetryAfterUserAction)]
    [InlineData(NativePasteOrchestrator.ClipboardMalformedCode, NativePasteRetry.RetryAfterUserAction)]
    [InlineData(ClipboardSourcePolicy.EmptyCode, NativePasteRetry.RetryAfterUserAction)]
    [InlineData(ClipboardSourcePolicy.FileListEmptyCode, NativePasteRetry.RetryAfterUserAction)]
    [InlineData(ClipboardSourcePolicy.UnsupportedCode, NativePasteRetry.RetryAfterUserAction)]
    public void ClipboardTroubleIsRecoverableWithADeterminateNextStep(string code, NativePasteRetry expectedRetry)
    {
        var decision = NativePasteFailurePolicy.Classify(code);

        Assert.True(decision.Recoverable);
        Assert.Equal(expectedRetry, decision.Retry);
        Assert.True(decision.OffersRetry);
        Assert.Equal(code, decision.Code);
    }

    [Fact]
    public void NoSessionIsRecoverableButNeverCreatesASessionImplicitly()
    {
        var decision = NativePasteFailurePolicy.Classify("no-session");

        Assert.True(decision.Recoverable);
        Assert.Equal(NativePasteRetry.RetryAfterUserAction, decision.Retry);
        Assert.Contains("不自动创建", decision.Detail, StringComparison.Ordinal);

        var gate = AttachmentImportGate.Evaluate(NativePasteFixture.Context(sessionId: null));
        Assert.False(gate.Allowed);
        Assert.Equal("no-session", gate.Code);
    }

    [Theory]
    [InlineData(AttachmentImportGate.ComposerLockedCode)]
    [InlineData(AttachmentImportGate.SubagentPromptCode)]
    [InlineData(AttachmentImportGate.ComposerNotEditableCode)]
    public void LockedSubagentAndReadOnlyStatesAreDeterminateAndRecoverable(string code)
    {
        var decision = NativePasteFailurePolicy.Classify(code);

        Assert.True(decision.Recoverable);
        Assert.Equal(NativePasteRetry.RetryAfterUserAction, decision.Retry);
    }

    [Theory]
    [InlineData(AttachmentStagingCodes.Directory)]
    [InlineData(AttachmentStagingCodes.UncPath)]
    [InlineData(AttachmentStagingCodes.DevicePath)]
    [InlineData(AttachmentStagingCodes.ReparsePoint)]
    [InlineData(AttachmentStagingCodes.CloudPlaceholder)]
    [InlineData(AttachmentStagingCodes.OfflineFile)]
    [InlineData(AttachmentStagingCodes.NetworkShare)]
    [InlineData(AttachmentStagingCodes.PathTraversal)]
    public void D14CandidateRefusalsAreReusedAndStayRecoverableByChangingTheSource(string code)
    {
        var decision = NativePasteFailurePolicy.Classify(code);

        Assert.True(decision.Recoverable);
        Assert.Equal(NativePasteRetry.RetryAfterUserAction, decision.Retry);
    }

    [Theory]
    [InlineData(AttachmentStagingCodes.SourceLocked)]
    [InlineData(AttachmentStagingCodes.SourceUnavailable)]
    [InlineData(AttachmentStagingCodes.SourceChanged)]
    [InlineData(AttachmentStagingCodes.InsufficientFreeSpace)]
    public void StagingTroubleIsRecoverable(string code)
    {
        Assert.True(NativePasteFailurePolicy.Classify(code).Recoverable);
    }

    [Theory]
    [InlineData(NativePasteConsumerLedger.AlreadyElectedCode)]
    [InlineData(NativePasteConsumerLedger.NotElectedCode)]
    [InlineData(NativePasteConsumerLedger.UnknownGestureCode)]
    [InlineData(NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode)]
    [InlineData(NativePasteConsumerLedger.NativeAbandonedCode)]
    public void DuplicateImportInvariantsAreNeverRetryable(string code)
    {
        var decision = NativePasteFailurePolicy.Classify(code);

        Assert.False(decision.Recoverable);
        Assert.Equal(NativePasteRetry.None, decision.Retry);
        Assert.False(decision.OffersRetry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something-nobody-registered")]
    public void UnknownCodesFailClosed(string code)
    {
        var decision = NativePasteFailurePolicy.Classify(code);

        Assert.False(decision.Recoverable);
        Assert.Equal(NativePasteRetry.None, decision.Retry);
        Assert.False(string.IsNullOrWhiteSpace(decision.Detail));
    }

    [Fact]
    public void GestureRefusalsAskForANewGestureInsteadOfARetry()
    {
        foreach (var code in new[]
                 {
                     NativePasteCodes.AuthorizationExpired,
                     NativePasteCodes.AuthorizationReplayed,
                     NativePasteCodes.AuthorizationEpochMismatch,
                     NativePasteCodes.AuthorizationFocusMismatch,
                     NativePasteCodes.GestureTabNotActive,
                     NativePasteCodes.GestureWindowHidden,
                 })
        {
            var decision = NativePasteFailurePolicy.Classify(code);
            Assert.True(decision.Recoverable, code);
            Assert.Equal(NativePasteRetry.RetryAfterUserAction, decision.Retry);
        }
    }

    [Fact]
    public void EveryCodeTheOrchestratorCanEmitHasItsOwnDeterminateClassification()
    {
        var codes = new[]
        {
            NativePasteOrchestrator.ClipboardBusyCode,
            NativePasteOrchestrator.ClipboardOpenFailedCode,
            NativePasteOrchestrator.ClipboardChangedCode,
            NativePasteOrchestrator.ClipboardMalformedCode,
            NativePasteOrchestrator.ClipboardTimeoutCode,
            ClipboardSourcePolicy.EmptyCode,
            ClipboardSourcePolicy.FileListEmptyCode,
            ClipboardSourcePolicy.UnsupportedCode,
            ClipboardSourcePolicy.ModeUndecidedCode,
            ScreenshotPngPolicy.DimensionsInvalidCode,
            ScreenshotPngPolicy.PixelFormatUnsupportedCode,
            ScreenshotPngPolicy.EncodeFailedCode,
            "limit-batch-files",
            "limit-file-bytes",
            "limit-screenshot-pixels",
            "no-session",
        };

        foreach (var code in codes)
        {
            var decision = NativePasteFailurePolicy.Classify(code);
            Assert.Equal(code, decision.Code);
            Assert.False(string.IsNullOrWhiteSpace(decision.Detail));
        }
    }

    [Fact]
    public void TheGateChecksSessionBeforeEverythingElse()
    {
        var facts = new ImportContextFacts(
            SessionId: null,
            ComposerEditable: false,
            ComposerLocked: true,
            SubagentActive: true,
            PageAdmitted: false,
            PageAdmissionCode: RemotePageSessionCodes.SessionClosed);

        var decision = AttachmentImportGate.Evaluate(facts);

        Assert.False(decision.Allowed);
        Assert.Equal("no-session", decision.Code);
    }

    [Fact]
    public void TheGateReportsSubagentLockAndReadOnlyInAFixedOrder()
    {
        Assert.Equal(
            AttachmentImportGate.SubagentPromptCode,
            AttachmentImportGate.Evaluate(NativePasteFixture.Context(subagent: true, locked: true, editable: false)).Code);
        Assert.Equal(
            AttachmentImportGate.ComposerLockedCode,
            AttachmentImportGate.Evaluate(NativePasteFixture.Context(locked: true, editable: false)).Code);
        Assert.Equal(
            AttachmentImportGate.ComposerNotEditableCode,
            AttachmentImportGate.Evaluate(NativePasteFixture.Context(editable: false)).Code);
    }

    [Fact]
    public void TheGatePropagatesThePageAdmissionCode()
    {
        var decision = AttachmentImportGate.Evaluate(
            NativePasteFixture.Context(admitted: false, admissionCode: RemotePageSessionCodes.WorkPageSuspended));

        Assert.False(decision.Allowed);
        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, decision.Code);
    }

    [Fact]
    public void TheGateAllowsOnlyWhenEveryFactHolds()
    {
        var decision = AttachmentImportGate.Evaluate(NativePasteFixture.Context());

        Assert.True(decision.Allowed);
        Assert.Equal(AttachmentImportGate.AllowedCode, decision.Code);
    }

    [Fact]
    public void PageLifecycleRefusalsFromD15AreClassifiedAsRecoverable()
    {
        foreach (var code in new[]
                 {
                     RemotePageSessionCodes.WorkPageSuspended,
                     RemotePageSessionCodes.WorkCapabilityAbsent,
                     RemotePageSessionCodes.SessionClosed,
                 })
        {
            Assert.True(NativePasteFailurePolicy.Classify(code).Recoverable, code);
        }
    }
}
