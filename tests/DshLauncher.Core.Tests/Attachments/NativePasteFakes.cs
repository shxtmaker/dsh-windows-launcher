using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 测试用的假剪贴板端口：只提供事实与结果，不做任何平台调用。
/// 生产读取端口在 Windows 半区（<c>WindowsClipboardPasteSource</c>），本轮不执行。
/// </summary>
internal sealed class FakeClipboardPort : INativeClipboardPastePort
{
    /// <summary>默认事实：一个含 1 个文件的文件列表（最常见的成功路径）。</summary>
    public ClipboardProbeResult ProbeResult { get; set; } = new()
    {
        Ok = true,
        Probe = NativePasteFixture.FileListProbe(fileCount: 1),
    };

    public ClipboardReadResult ReadResult { get; set; } = new()
    {
        Ok = true,
        Kind = ClipboardSourceKind.FileList,
        CaptureIds = ["capture-1"],
        FileCount = 1,
        ReadOffSta = true,
    };

    /// <summary>可选的自定义读取行为（例如挂住不返回、抛异常）。</summary>
    public Func<ClipboardProbe, ClipboardSourceDecision, CancellationToken, Task<ClipboardReadResult>>? ReadHandler { get; set; }

    public int ProbeCount { get; private set; }

    public int ReadCount { get; private set; }

    public ClipboardSourceDecision? LastDecision { get; private set; }

    public ClipboardProbeResult Probe()
    {
        ProbeCount += 1;
        return ProbeResult;
    }

    public Task<ClipboardReadResult> ReadAsync(
        ClipboardProbe probe,
        ClipboardSourceDecision decision,
        CancellationToken cancellationToken)
    {
        ReadCount += 1;
        LastDecision = decision;
        return ReadHandler is null
            ? Task.FromResult(ReadResult)
            : ReadHandler(probe, decision, cancellationToken);
    }
}

/// <summary>可控时钟：只推进显式给定的时间，避免测试依赖真实等待。</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;

    /// <summary>当前毫秒时间戳（与编排器使用的换算一致）。</summary>
    public long NowMs => _now.ToUnixTimeMilliseconds();
}

/// <summary>D16 事实构造器：手势证据、导入上下文与剪贴板探测。</summary>
internal static class NativePasteFixture
{
    public static readonly Guid TargetA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid TargetB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public const string FocusA = "window-1:page-a";

    public const string FocusB = "window-1:page-b";

    public static NativeGestureEvidence Evidence(
        string gestureId = "gesture-1",
        NativeGestureKind kind = NativeGestureKind.PasteHotkey,
        Guid? targetId = null,
        long epoch = 1,
        string focusToken = FocusA,
        bool windowVisible = true,
        bool windowActive = true,
        bool focusInsidePage = true,
        bool activeTabMatches = true,
        long observedAtMs = 0) =>
        new(
            gestureId,
            kind,
            targetId ?? TargetA,
            new RemotePageEpoch(epoch),
            focusToken,
            new NativeWindowFocusState(windowVisible, windowActive, focusInsidePage, activeTabMatches),
            observedAtMs);

    public static ImportContextFacts Context(
        string? sessionId = "session-1",
        bool editable = true,
        bool locked = false,
        bool subagent = false,
        bool admitted = true,
        string admissionCode = RemotePageSessionCodes.WorkAccepted) =>
        new(sessionId, editable, locked, subagent, admitted, admissionCode);

    public static ClipboardProbe FileListProbe(int fileCount = 3, bool text = false, bool bitmap = false) =>
        new(
            SequenceNumber: 42,
            FormatCount: 2,
            HasText: text,
            HasFileList: true,
            HasBitmap: bitmap,
            FileCount: fileCount,
            BitmapWidth: bitmap ? 1920 : 0,
            BitmapHeight: bitmap ? 1080 : 0,
            BitmapBitsPerPixel: bitmap ? 32 : 0,
            BitmapPayloadBytes: bitmap ? 1920L * 1080 * 4 : 0);

    public static ClipboardProbe BitmapProbe(
        int width = 1920,
        int height = 1080,
        bool text = false,
        int bitsPerPixel = 32) =>
        new(
            SequenceNumber: 43,
            FormatCount: 1,
            HasText: text,
            HasFileList: false,
            HasBitmap: true,
            FileCount: 0,
            BitmapWidth: width,
            BitmapHeight: height,
            BitmapBitsPerPixel: bitsPerPixel,
            BitmapPayloadBytes: (long)width * height * 4);

    public static ClipboardProbe TextProbe() =>
        new(SequenceNumber: 44, FormatCount: 1, HasText: true, HasFileList: false, HasBitmap: false, 0, 0, 0, 0, 0);

    /// <summary>组装一套完整的生产编排器（生产状态机 + 假端口 + 假时钟）。</summary>
    public static NativePasteOrchestrator Orchestrator(
        FakeClipboardPort port,
        out NativePasteConsumerLedger ledger,
        out ScreenshotOwnerRegistry owners,
        out FakeTimeProvider clock,
        AttachmentLimits? limits = null,
        int readTimeoutMs = NativePasteOrchestrator.ReadTimeoutMs)
    {
        ledger = new NativePasteConsumerLedger();
        owners = new ScreenshotOwnerRegistry();
        clock = new FakeTimeProvider();
        return new NativePasteOrchestrator(
            port,
            new NativeGestureAuthorizer(),
            ledger,
            owners,
            limits,
            clock,
            readTimeoutMs);
    }
}
