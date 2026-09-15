using System.ComponentModel;
using System.Runtime.InteropServices;
using DshLauncher.Core.Attachments;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// D16 的真实 Windows 剪贴板端口（实现 <see cref="INativeClipboardPastePort"/>）。
///
/// <b>为什么不会阻塞 STA</b>：
/// <list type="bullet">
/// <item><see cref="Probe"/> 只做有界工作：<c>OpenClipboard</c> 最多
/// <see cref="WindowsClipboardNative.OpenAttempts"/> 次、每次间隔 <see cref="WindowsClipboardNative.OpenRetryDelayMs"/>
/// 毫秒（最坏十几毫秒），拿到的只有格式可用性、CF_HDROP 的<b>文件个数</b>与 DIB 的
/// <b>头 124 字节</b>；绝不复制像素，也不读路径字符串；</item>
/// <item><see cref="ReadAsync"/> 立刻把真正的工作切到线程池：重新打开剪贴板、复核
/// <c>GetClipboardSequenceNumber</c>、复制 DIB 像素、编码 PNG、按文件列表登记捕获票据。
/// 调用方（WPF 的 STA）在第一个 <c>await</c> 就回到消息循环；</item>
/// <item>打开失败/被占用给出 <c>clipboard-busy</c>，内容变化给出 <c>clipboard-changed</c>，
/// 数据畸形给出 <c>clipboard-malformed</c>，全部是<b>可恢复</b>结果而不是异常或挂起。</item>
/// </list>
///
/// <b>无任意路径读取入口</b>：唯一的路径来源是剪贴板当前的 <c>CF_HDROP</c> 内容；
/// 公开 API 不接受任何路径参数，票据登记仍在 <see cref="WindowsAttachmentStagingAdapter"/> 内部完成。
/// </summary>
public sealed unsafe class WindowsClipboardPasteSource : INativeClipboardPastePort
{
    /// <summary>剪贴板被其他进程占用（可恢复：可就地重试）。</summary>
    public const string ClipboardBusyCode = "clipboard-busy";

    /// <summary>打开剪贴板失败且不是占用（可恢复）。</summary>
    public const string ClipboardOpenFailedCode = "clipboard-open-failed";

    /// <summary>读取期间剪贴板内容变化（可恢复：重新复制）。</summary>
    public const string ClipboardChangedCode = "clipboard-changed";

    /// <summary>剪贴板数据畸形（可恢复：重新复制）。</summary>
    public const string ClipboardMalformedCode = "clipboard-malformed";

    /// <summary>截图默认显示名。</summary>
    public const string ScreenshotDisplayName = "screenshot.png";

    private const int ErrorAccessDenied = 5;

    private readonly WindowsAttachmentStagingAdapter _staging;
    private readonly AttachmentLimits _limits;

    public WindowsClipboardPasteSource(WindowsAttachmentStagingAdapter staging, AttachmentLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(staging);
        _staging = staging;
        _limits = limits ?? new AttachmentLimits();
    }

    /// <summary>有界探测：不复制任何大块数据，也不读路径字符串。</summary>
    public ClipboardProbeResult Probe()
    {
        if (!TryOpenClipboard(out var attempts, out var code, out var detail))
        {
            return new ClipboardProbeResult
            {
                Ok = false,
                Code = code,
                Detail = detail,
                OpenAttempts = attempts,
            };
        }

        try
        {
            var sequence = WindowsClipboardNative.GetClipboardSequenceNumber();
            var formats = WindowsClipboardNative.CountClipboardFormats();
            var hasText = WindowsClipboardNative.IsClipboardFormatAvailable(WindowsClipboardNative.CfUnicodeText)
                || WindowsClipboardNative.IsClipboardFormatAvailable(WindowsClipboardNative.CfText);
            var hasFileList = WindowsClipboardNative.IsClipboardFormatAvailable(WindowsClipboardNative.CfHDrop);
            var hasBitmap = WindowsClipboardNative.IsClipboardFormatAvailable(WindowsClipboardNative.CfDibV5)
                || WindowsClipboardNative.IsClipboardFormatAvailable(WindowsClipboardNative.CfDib)
                || WindowsClipboardNative.IsClipboardFormatAvailable(WindowsClipboardNative.CfBitmap);

            var fileCount = hasFileList ? CountDropFiles() : 0;
            var (bitmapWidth, bitmapHeight, bitsPerPixel, payloadBytes) = hasBitmap
                ? ReadBitmapHeader()
                : (0, 0, 0, 0L);

            return new ClipboardProbeResult
            {
                Ok = true,
                OpenAttempts = attempts,
                Probe = new ClipboardProbe(
                    sequence,
                    formats,
                    hasText,
                    hasFileList,
                    hasBitmap,
                    fileCount,
                    bitmapWidth,
                    bitmapHeight,
                    bitsPerPixel,
                    payloadBytes),
            };
        }
        finally
        {
            _ = WindowsClipboardNative.CloseClipboard();
        }
    }

    /// <summary>按判定读取内容；昂贵的部分在线程池线程上完成，STA 只负责发起。</summary>
    public Task<ClipboardReadResult> ReadAsync(
        ClipboardProbe probe,
        ClipboardSourceDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // 关键：真正的剪贴板读取（像素复制、路径字符串、PNG 编码）全部离开 STA。
        return decision.Kind switch
        {
            ClipboardSourceKind.FileList or ClipboardSourceKind.FileListAndBitmap =>
                Task.Run(() => ReadFileList(probe, decision, cancellationToken), cancellationToken),
            ClipboardSourceKind.Bitmap =>
                Task.Run(() => ReadBitmap(probe, decision, cancellationToken), cancellationToken),
            _ => Task.FromResult(new ClipboardReadResult
            {
                Ok = false,
                Code = ClipboardSourcePolicy.UnsupportedCode,
                Detail = $"判定为 {decision.Kind}：没有可读取的原生内容",
                Kind = decision.Kind,
                ReadOffSta = true,
            }),
        };
    }

    private ClipboardReadResult ReadFileList(
        ClipboardProbe probe,
        ClipboardSourceDecision decision,
        CancellationToken cancellationToken)
    {
        if (!TryOpenClipboard(out _, out var code, out var detail))
        {
            return Failed(code!, detail!, decision.Kind);
        }

        try
        {
            if (WindowsClipboardNative.GetClipboardSequenceNumber() != probe.SequenceNumber)
            {
                return Failed(ClipboardChangedCode, "剪贴板在探测之后变化：本次手势放弃，请重新粘贴", decision.Kind);
            }

            var handle = WindowsClipboardNative.GetClipboardData(WindowsClipboardNative.CfHDrop);
            if (handle == nint.Zero)
            {
                return Failed(ClipboardMalformedCode, "CF_HDROP 句柄不可用", decision.Kind);
            }

            var count = (int)WindowsClipboardNative.DragQueryFileW(handle, WindowsClipboardNative.DragQueryFileCount, null, 0);
            if (count <= 0)
            {
                return Failed(ClipboardSourcePolicy.FileListEmptyCode, "CF_HDROP 不含任何文件", decision.Kind);
            }

            if (count > _limits.MaxFilesPerBatch)
            {
                return Failed(
                    "limit-batch-files",
                    $"文件列表 {count} 项超过批内上限 {_limits.MaxFilesPerBatch}",
                    decision.Kind);
            }

            var captures = new List<string>(count);
            var gesture = NativePasteGesture.MintFromNativePaste();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ReadDropFile(handle, (uint)index);
                if (path is null)
                {
                    return Failed(ClipboardMalformedCode, $"CF_HDROP 第 {index} 项无法解析", decision.Kind);
                }

                try
                {
                    captures.Add(_staging.RegisterNativeCapture(gesture, path));
                }
                catch (AttachmentStagingException error)
                {
                    return Failed(error.Code, error.Message, decision.Kind);
                }
            }

            return new ClipboardReadResult
            {
                Ok = true,
                Kind = decision.Kind,
                CaptureIds = captures,
                FileCount = captures.Count,
                ReadOffSta = true,
            };
        }
        finally
        {
            _ = WindowsClipboardNative.CloseClipboard();
        }
    }

    private ClipboardReadResult ReadBitmap(
        ClipboardProbe probe,
        ClipboardSourceDecision decision,
        CancellationToken cancellationToken)
    {
        if (!TryOpenClipboard(out _, out var code, out var detail))
        {
            return Failed(code!, detail!, decision.Kind);
        }

        byte[]? dib = null;
        try
        {
            if (WindowsClipboardNative.GetClipboardSequenceNumber() != probe.SequenceNumber)
            {
                return Failed(ClipboardChangedCode, "剪贴板在探测之后变化：本次手势放弃，请重新粘贴", decision.Kind);
            }

            // 尺寸与像素上限在复制之前先判定：荒谬尺寸不会走到任何分配。
            var dimension = ScreenshotPngPolicy.EvaluateDimensions(
                probe.BitmapWidth,
                probe.BitmapHeight,
                _limits);
            if (!dimension.Allowed)
            {
                return Failed(dimension.Code!, dimension.Detail!, decision.Kind);
            }

            dib = CopyClipboardDib(probe, out var malformed);
            if (dib is null)
            {
                return Failed(ClipboardMalformedCode, malformed ?? "DIB 复制失败", decision.Kind);
            }
        }
        finally
        {
            _ = WindowsClipboardNative.CloseClipboard();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 编码同样不占 STA：这里已经是线程池线程。
        var encoded = ScreenshotPngEncoder.EncodeDib(dib, _limits);
        if (!encoded.Ok)
        {
            return Failed(encoded.Code!, encoded.Detail!, decision.Kind);
        }

        var gesture = NativePasteGesture.MintFromNativePaste();
        string captureId;
        try
        {
            captureId = _staging.RegisterNativeCapture(
                gesture,
                new InMemoryNativeCapture(Guid.NewGuid().ToString("N"), ScreenshotDisplayName, encoded.Png!));
        }
        catch (AttachmentStagingException error)
        {
            return Failed(error.Code, error.Message, decision.Kind);
        }

        return new ClipboardReadResult
        {
            Ok = true,
            Kind = decision.Kind,
            CaptureIds = [captureId],
            FileCount = 1,
            BitmapWidth = encoded.Width,
            BitmapHeight = encoded.Height,
            PixelCount = encoded.PixelCount,
            EncodedBytes = encoded.Png!.Length,
            ReadOffSta = true,
        };
    }

    /// <summary>把剪贴板 DIB 复制成托管字节；复制量由探测到的尺寸上界约束。</summary>
    private static byte[]? CopyClipboardDib(ClipboardProbe probe, out string? malformed)
    {
        malformed = null;
        var handle = WindowsClipboardNative.GetClipboardData(WindowsClipboardNative.CfDibV5);
        if (handle == nint.Zero)
        {
            handle = WindowsClipboardNative.GetClipboardData(WindowsClipboardNative.CfDib);
        }

        if (handle == nint.Zero)
        {
            malformed = "剪贴板没有可用的 CF_DIB/CF_DIBV5 句柄";
            return null;
        }

        var size = (long)WindowsClipboardNative.GlobalSize(handle);
        if (size <= 0 || size > int.MaxValue)
        {
            malformed = $"DIB 载荷长度 {size} 非法";
            return null;
        }

        var pointer = WindowsClipboardNative.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            malformed = "GlobalLock(DIB) 失败";
            return null;
        }

        try
        {
            var buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, (int)size);
            return buffer;
        }
        catch (OutOfMemoryException)
        {
            malformed = "DIB 载荷过大，无法分配缓冲区";
            return null;
        }
        finally
        {
            // GlobalUnlock 在解锁计数归零时返回 false 并置 ERROR_SUCCESS：返回值不是失败判据。
            _ = WindowsClipboardNative.GlobalUnlock(handle);
        }
    }

    private static string? ReadDropFile(nint handle, uint index)
    {
        var length = (int)WindowsClipboardNative.DragQueryFileW(handle, index, null, 0);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                var written = WindowsClipboardNative.DragQueryFileW(handle, index, pointer, (uint)buffer.Length);
                return written == 0 ? null : new string(buffer, 0, (int)written);
            }
        }
    }

    private static int CountDropFiles()
    {
        var handle = WindowsClipboardNative.GetClipboardData(WindowsClipboardNative.CfHDrop);
        return handle == nint.Zero
            ? 0
            : (int)WindowsClipboardNative.DragQueryFileW(handle, WindowsClipboardNative.DragQueryFileCount, null, 0);
    }

    /// <summary>只读 DIB 头（≤124 字节）拿尺寸与载荷长度，不复制像素。</summary>
    private static (int Width, int Height, int BitsPerPixel, long PayloadBytes) ReadBitmapHeader()
    {
        var handle = WindowsClipboardNative.GetClipboardData(WindowsClipboardNative.CfDibV5);
        if (handle == nint.Zero)
        {
            handle = WindowsClipboardNative.GetClipboardData(WindowsClipboardNative.CfDib);
        }

        if (handle == nint.Zero)
        {
            // 只有 CF_BITMAP（GDI 位图）时拿不到 DIB 头：如实报告"有位图但读不到尺寸"，
            // 由核心判定以确定码拒绝，而不是猜一个尺寸。
            return (0, 0, 0, 0);
        }

        var size = (long)WindowsClipboardNative.GlobalSize(handle);
        var headerBytes = (int)Math.Min(size, WindowsClipboardNative.MaxDibHeaderBytes);
        var pointer = WindowsClipboardNative.GlobalLock(handle);
        if (pointer == nint.Zero || headerBytes < 16)
        {
            return (0, 0, 0, size);
        }

        try
        {
            var header = new byte[headerBytes];
            Marshal.Copy(pointer, header, 0, headerBytes);
            var width = BitConverter.ToInt32(header, 4);
            var heightField = BitConverter.ToInt32(header, 8);
            var bitsPerPixel = BitConverter.ToUInt16(header, 14);
            return (width, Math.Abs(heightField), bitsPerPixel, size);
        }
        finally
        {
            _ = WindowsClipboardNative.GlobalUnlock(handle);
        }
    }

    /// <summary>有界重试地打开剪贴板；失败原因是占用时给 <c>clipboard-busy</c>。</summary>
    private static bool TryOpenClipboard(out int attempts, out string? code, out string? detail)
    {
        for (var attempt = 1; attempt <= WindowsClipboardNative.OpenAttempts; attempt++)
        {
            if (WindowsClipboardNative.OpenClipboard(nint.Zero))
            {
                attempts = attempt;
                code = null;
                detail = null;
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (attempt == WindowsClipboardNative.OpenAttempts)
            {
                attempts = attempt;
                code = error == ErrorAccessDenied ? ClipboardBusyCode : ClipboardOpenFailedCode;
                detail = error == ErrorAccessDenied
                    ? $"剪贴板被其他进程占用（尝试 {attempt} 次）"
                    : $"打开剪贴板失败（Win32 {error}，尝试 {attempt} 次）";
                return false;
            }

            Thread.Sleep(WindowsClipboardNative.OpenRetryDelayMs);
        }

        attempts = WindowsClipboardNative.OpenAttempts;
        code = ClipboardOpenFailedCode;
        detail = "打开剪贴板失败";
        return false;
    }

    private static ClipboardReadResult Failed(string code, string detail, ClipboardSourceKind kind) =>
        new()
        {
            Ok = false,
            Code = code,
            Detail = detail,
            Kind = kind,
            ReadOffSta = true,
        };
}
