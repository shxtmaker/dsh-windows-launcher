using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 来源优先与截图所有者握手的平台中立测试（Linux 真实执行）：
/// 文件列表优先于位图、纯文本保留原行为、无模式不猜、确定限额码。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class ClipboardSourcePreferenceTests
{
    private static readonly AttachmentLimits Limits = new();

    [Fact]
    public void FileListWinsOverBitmap()
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.FileListProbe(fileCount: 2, bitmap: true),
            ScreenshotOwnerMode.Bridge,
            Limits);

        Assert.Equal(ClipboardSourceKind.FileListAndBitmap, decision.Kind);
        Assert.Equal(ClipboardImportRoute.NativePaste, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.FileListPreferredCode, decision.Code);
        Assert.Equal(2, decision.FileCount);
        Assert.True(decision.ImportsAttachments);
    }

    [Fact]
    public void FileListBeatsPlainTextToo()
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.FileListProbe(fileCount: 1, text: true),
            ScreenshotOwnerMode.NativePaste,
            Limits);

        Assert.Equal(ClipboardImportRoute.NativePaste, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.FileListOnlyCode, decision.Code);
    }

    [Fact]
    public void PlainTextKeepsTheOriginalBehaviour()
    {
        var decision = ClipboardSourcePolicy.Decide(NativePasteFixture.TextProbe(), ScreenshotOwnerMode.NativePaste, Limits);

        Assert.Equal(ClipboardSourceKind.TextOnly, decision.Kind);
        Assert.Equal(ClipboardImportRoute.OriginalTextBehaviour, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.TextOriginalCode, decision.Code);
        Assert.False(decision.ImportsAttachments);
    }

    [Theory]
    [InlineData(ScreenshotOwnerMode.NativePaste, ClipboardImportRoute.NativePaste, ClipboardSourcePolicy.BitmapNativePasteCode)]
    [InlineData(ScreenshotOwnerMode.Bridge, ClipboardImportRoute.Bridge, ClipboardSourcePolicy.BitmapBridgeCode)]
    public void BitmapFollowsTheHandshakeMode(
        ScreenshotOwnerMode mode,
        ClipboardImportRoute expectedRoute,
        string expectedCode)
    {
        var decision = ClipboardSourcePolicy.Decide(NativePasteFixture.BitmapProbe(), mode, Limits);

        Assert.Equal(ClipboardSourceKind.Bitmap, decision.Kind);
        Assert.Equal(expectedRoute, decision.Route);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void BitmapWithoutAHandshakeIsRefusedInsteadOfGuessed()
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.BitmapProbe(),
            ScreenshotOwnerMode.Undecided,
            Limits);

        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.ModeUndecidedCode, decision.Code);
        Assert.False(decision.ImportsAttachments);
    }

    [Fact]
    public void AnEmptyClipboardIsRefusedDeterministically()
    {
        var decision = ClipboardSourcePolicy.Decide(ClipboardProbe.Empty, ScreenshotOwnerMode.NativePaste, Limits);

        Assert.Equal(ClipboardSourceKind.Empty, decision.Kind);
        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.EmptyCode, decision.Code);
    }

    [Fact]
    public void FormatsWeDoNotUnderstandAreRefusedDeterministically()
    {
        var probe = new ClipboardProbe(7, 3, HasText: false, HasFileList: false, HasBitmap: false, 0, 0, 0, 0, 0);

        var decision = ClipboardSourcePolicy.Decide(probe, ScreenshotOwnerMode.NativePaste, Limits);

        Assert.Equal(ClipboardSourceKind.Unsupported, decision.Kind);
        Assert.Equal(ClipboardSourcePolicy.UnsupportedCode, decision.Code);
    }

    [Fact]
    public void AFileListWithoutFilesIsRefused()
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.FileListProbe(fileCount: 0),
            ScreenshotOwnerMode.NativePaste,
            Limits);

        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.FileListEmptyCode, decision.Code);
    }

    [Fact]
    public void AFileListBeyondTheBatchLimitReusesTheFrozenCode()
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.FileListProbe(fileCount: Limits.MaxFilesPerBatch + 1),
            ScreenshotOwnerMode.NativePaste,
            Limits);

        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal("limit-batch-files", decision.Code);
        Assert.Contains("limit-batch-files", AttachmentProtocol.ErrorCodes, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    [InlineData(70_000, 10)]
    public void AbsurdBitmapDimensionsAreRefusedBeforeAnyAllocation(int width, int height)
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.BitmapProbe(width, height),
            ScreenshotOwnerMode.NativePaste,
            Limits);

        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal(ScreenshotPngPolicy.DimensionsInvalidCode, decision.Code);
    }

    [Fact]
    public void ABitmapOverTheFrozenPixelCapIsRefusedWithTheFrozenCode()
    {
        // 4000 万像素上限：8001 × 5000 = 40 005 000 超过一格。
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.BitmapProbe(8_001, 5_000),
            ScreenshotOwnerMode.NativePaste,
            Limits);

        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal("limit-screenshot-pixels", decision.Code);
        Assert.Contains("limit-screenshot-pixels", AttachmentProtocol.ErrorCodes, StringComparer.Ordinal);
    }

    [Fact]
    public void ThePixelCapBoundaryItselfIsAccepted()
    {
        var decision = ClipboardSourcePolicy.Decide(
            NativePasteFixture.BitmapProbe(8_000, 5_000),
            ScreenshotOwnerMode.NativePaste,
            Limits);

        Assert.Equal(ClipboardImportRoute.NativePaste, decision.Route);
    }

    [Fact]
    public void DropWithoutAFileListIsRefused()
    {
        var decision = ClipboardSourcePolicy.DecideDrop(hasFileList: false);

        Assert.Equal(ClipboardImportRoute.None, decision.Route);
        Assert.Equal(ClipboardSourcePolicy.UnsupportedCode, decision.Code);
    }

    [Fact]
    public void DropWithAFileListGoesNative()
    {
        var decision = ClipboardSourcePolicy.DecideDrop(hasFileList: true);

        Assert.Equal(ClipboardImportRoute.NativePaste, decision.Route);
        Assert.Equal(ClipboardSourceKind.FileList, decision.Kind);
    }

    [Theory]
    [InlineData(false, false, false, ScreenshotOwnerMode.Bridge, ScreenshotOwnerHandshake.NoFeatureCode)]
    [InlineData(true, true, false, ScreenshotOwnerMode.Bridge, ScreenshotOwnerHandshake.NativeUnavailableCode)]
    [InlineData(true, true, true, ScreenshotOwnerMode.NativePaste, ScreenshotOwnerHandshake.NativeRequestedCode)]
    [InlineData(true, false, true, ScreenshotOwnerMode.Bridge, ScreenshotOwnerHandshake.BridgePreferredCode)]
    public void HandshakeChoosesTheScreenshotOwnerFromVerifiedFacts(
        bool advertises,
        bool prefersNative,
        bool nativeAvailable,
        ScreenshotOwnerMode expectedMode,
        string expectedCode)
    {
        var decision = ScreenshotOwnerHandshake.Decide(new ScreenshotOwnerFacts(advertises, prefersNative, nativeAvailable));

        Assert.Equal(expectedMode, decision.Mode);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void TheOwnerRegistryIsBoundToThePageEpoch()
    {
        var registry = new ScreenshotOwnerRegistry();
        var recorded = registry.Record(
            NativePasteFixture.TargetA,
            new RemotePageEpoch(3),
            new ScreenshotOwnerFacts(true, true, true));

        Assert.Equal(ScreenshotOwnerMode.NativePaste, recorded.Mode);
        Assert.Equal(ScreenshotOwnerMode.NativePaste, registry.ModeFor(NativePasteFixture.TargetA, new RemotePageEpoch(3)));
        // 导航推进代际后必须重新握手：旧结论不得沿用。
        Assert.Equal(ScreenshotOwnerMode.Undecided, registry.ModeFor(NativePasteFixture.TargetA, new RemotePageEpoch(4)));
        Assert.Equal(ScreenshotOwnerMode.Undecided, registry.ModeFor(NativePasteFixture.TargetB, new RemotePageEpoch(3)));
        Assert.True(registry.Forget(NativePasteFixture.TargetA));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ComposerContextIsAlsoEpochBoundAndDefaultsToNoSession()
    {
        var store = new AttachmentComposerContextStore();
        Assert.Null(store.Current(NativePasteFixture.TargetA).SessionId);

        Assert.True(store.Record(
            NativePasteFixture.TargetA,
            new RemotePageEpoch(1),
            NativePasteFixture.Context(sessionId: "session-9"),
            pageAdmitted: true));
        Assert.Equal("session-9", store.Current(NativePasteFixture.TargetA, new RemotePageEpoch(1)).SessionId);
        Assert.Null(store.Current(NativePasteFixture.TargetA, new RemotePageEpoch(2)).SessionId);

        // 未准入的声明一律不记录：页面不能靠"自称有会话"越过闸门。
        Assert.False(store.Record(
            NativePasteFixture.TargetB,
            new RemotePageEpoch(1),
            NativePasteFixture.Context(sessionId: "session-forged"),
            pageAdmitted: false));
        Assert.Null(store.Current(NativePasteFixture.TargetB).SessionId);
    }
}
