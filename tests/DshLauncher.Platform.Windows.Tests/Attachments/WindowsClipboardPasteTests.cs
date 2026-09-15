using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DshLauncher.Core.Attachments;
using DshLauncher.Platform.Windows.Attachments;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests.Attachments;

/// <summary>
/// D16 Windows 实机用例：真实剪贴板（CF_HDROP / CF_DIB / CF_DIBV5）、真实 STA 语义、
/// 真实线程池读取与非阻塞探测。<b>本轮没有 Windows 机器，这里一条都没有执行过</b>，
/// 因此不得把任何一条记为 PASS；每条的 id、执行方式、断言与"为何 Linux 不能跑"见
/// <c>WindowsPendingCases.md</c> 的 WP-26…WP-34。Linux 侧真实执行的是平台中立规则
/// （<c>tests/DshLauncher.Core.Tests/Attachments/NativePaste*</c>）。
/// </summary>
[Trait("triggerTags", "VFY-06")]
public sealed class WindowsClipboardPasteTests : IDisposable
{
    private readonly string _layoutRoot = Path.Combine(
        Path.GetTempPath(),
        "dsh-d16-paste-" + Guid.NewGuid().ToString("N"));

    private readonly ApplicationDataLayout _layout;
    private readonly WindowsAttachmentStagingAdapter _adapter;

    public WindowsClipboardPasteTests()
    {
        _layout = new ApplicationDataLayout(_layoutRoot);
        Directory.CreateDirectory(_layoutRoot);
        File.WriteAllText(_layout.OwnershipMarkerPath, ApplicationDataLayout.OwnershipMarkerContent);
        _adapter = new WindowsAttachmentStagingAdapter(_layout, Guid.NewGuid());
        _adapter.Initialize();
        _adapter.BeginBatch("batch-d16", AttachmentProtocol.MaxFilesPerBatch);
    }

    public void Dispose()
    {
        _adapter.Dispose();
        if (Directory.Exists(_layoutRoot))
        {
            Directory.Delete(_layoutRoot, recursive: true);
        }
    }

    /// <summary>WP-26：真实 CF_HDROP → 原生捕获 → 暂存字节与真实文件逐一相等。</summary>
    [Fact]
    public async Task WP26ARealFileDropListIsReadOffTheStaAndStagedWithTheRealBytes()
    {
        var payload = BuildPayload(64_000);
        var source = NewSourceFile(payload);
        SetClipboardFileDropList([source]);
        var source2 = new WindowsClipboardPasteSource(_adapter);

        var probe = source2.Probe();
        var decision = ClipboardSourcePolicy.Decide(probe.Probe, ScreenshotOwnerMode.NativePaste, new AttachmentLimits());
        var read = await source2.ReadAsync(probe.Probe, decision, TestContext.Current.CancellationToken);

        Assert.True(probe.Ok);
        Assert.True(probe.Probe.HasFileList);
        Assert.Equal(1, probe.Probe.FileCount);
        Assert.True(read.Ok);
        Assert.True(read.ReadOffSta);
        var captureId = Assert.Single(read.CaptureIds);
        Assert.False(string.IsNullOrWhiteSpace(captureId));

        var captured = _adapter.CaptureNative(captureId, TestContext.Current.CancellationToken);
        Assert.True(captured.Ok);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), captured.Sha256);
        Assert.Equal(payload, File.ReadAllBytes(captured.StagedPath!));
    }

    /// <summary>WP-27：剪贴板被其他线程占用时给出可恢复码，且 STA 不被拖住。</summary>
    [Fact]
    public void WP27ABusyClipboardIsARecoverableResultWithoutBlockingTheSta()
    {
        var holder = new Thread(() =>
        {
            Assert.True(WindowsClipboardNative.OpenClipboard(nint.Zero));
            Thread.Sleep(2_000);
            _ = WindowsClipboardNative.CloseClipboard();
        });
        holder.SetApartmentState(ApartmentState.STA);
        holder.Start();
        Thread.Sleep(200);
        try
        {
            var source = new WindowsClipboardPasteSource(_adapter);
            var watch = Stopwatch.StartNew();

            var probe = source.Probe();

            watch.Stop();
            Assert.False(probe.Ok);
            Assert.Contains(probe.Code, new[] { WindowsClipboardPasteSource.ClipboardBusyCode, WindowsClipboardPasteSource.ClipboardOpenFailedCode });
            Assert.InRange(probe.OpenAttempts, 1, WindowsClipboardNative.OpenAttempts);
            Assert.True(watch.ElapsedMilliseconds < 500, $"探测耗时 {watch.ElapsedMilliseconds}ms，不应拖住 STA");
        }
        finally
        {
            holder.Join();
        }
    }

    /// <summary>WP-28：探测只读 DIB 头，不复制像素（大位图也很快）。</summary>
    [Fact]
    public void WP28ProbingALargeBitmapDoesNotCopyPixelData()
    {
        SetClipboardDib(width: 1920, height: 1080);
        var source = new WindowsClipboardPasteSource(_adapter);
        var watch = Stopwatch.StartNew();

        var probe = source.Probe();

        watch.Stop();
        Assert.True(probe.Ok);
        Assert.Equal(1920, probe.Probe.BitmapWidth);
        Assert.Equal(1080, probe.Probe.BitmapHeight);
        Assert.Equal(32, probe.Probe.BitmapBitsPerPixel);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 100);
    }

    /// <summary>WP-29：真实位图 → STA 之外复制与编码 → 暂存里是合法 PNG。</summary>
    [Fact]
    public async Task WP29ABitmapBecomesAPngSnapshotWithoutBlockingTheSta()
    {
        SetClipboardDib(width: 64, height: 32);
        var source = new WindowsClipboardPasteSource(_adapter);
        var probe = source.Probe();
        var decision = ClipboardSourcePolicy.Decide(probe.Probe, ScreenshotOwnerMode.NativePaste, new AttachmentLimits());

        var read = await source.ReadAsync(probe.Probe, decision, TestContext.Current.CancellationToken);

        Assert.True(read.Ok);
        Assert.True(read.ReadOffSta);
        var captured = _adapter.CaptureNative(Assert.Single(read.CaptureIds), TestContext.Current.CancellationToken);
        Assert.True(captured.Ok);
        Assert.Equal("screenshot.png", captured.DisplayName);
        var png = File.ReadAllBytes(captured.StagedPath!);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(png)), captured.Sha256);
        Assert.InRange(captured.ByteLength, 1, AttachmentProtocol.MaxFileBytes);
    }

    /// <summary>WP-30：荒谬尺寸在复制之前就被拒绝（不分配像素缓冲）。</summary>
    [Fact]
    public async Task WP30AnOversizedClipboardBitmapIsRefusedBeforeAnyAllocation()
    {
        SetClipboardDibHeaderOnly(width: 40_000, height: 30_000);
        var source = new WindowsClipboardPasteSource(_adapter);
        var probe = source.Probe();
        Assert.Equal(40_000, probe.Probe.BitmapWidth);

        var read = await source.ReadAsync(
            probe.Probe,
            new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Bitmap,
                Route = ClipboardImportRoute.NativePaste,
                Code = ClipboardSourcePolicy.BitmapNativePasteCode,
            },
            TestContext.Current.CancellationToken);

        Assert.False(read.Ok);
        Assert.Equal("limit-screenshot-pixels", read.Code);
    }

    /// <summary>WP-31：声明了尺寸却没有像素载荷的畸形 DIB 给确定码而不是异常。</summary>
    [Fact]
    public async Task WP31MalformedClipboardBitmapDataIsRecoverable()
    {
        // 头部声明 4×4/32bpp，但 HGLOBAL 里只有 40 字节头：真实畸形数据。
        SetClipboardRawDib(BuildDibHeader(4, 4));
        var source = new WindowsClipboardPasteSource(_adapter);
        var probe = source.Probe();

        var read = await source.ReadAsync(
            probe.Probe,
            new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Bitmap,
                Route = ClipboardImportRoute.NativePaste,
                Code = ClipboardSourcePolicy.BitmapNativePasteCode,
            },
            TestContext.Current.CancellationToken);

        Assert.False(read.Ok);
        Assert.Equal(ScreenshotPngEncoder.MalformedCode, read.Code);
        Assert.True(NativePasteFailurePolicy.Classify(read.Code!).Recoverable);
    }

    /// <summary>WP-32：拖放数据对象是唯一入口，公开 API 里没有路径参数。</summary>
    [Fact]
    public void WP32TheDropSurfaceAcceptsADataObjectAndNeverAPath()
    {
        var sourceMethod = typeof(WindowsDroppedFilesSource).GetMethod(nameof(WindowsDroppedFilesSource.RegisterDroppedFiles));
        Assert.NotNull(sourceMethod);
        Assert.All(
            sourceMethod!.GetParameters(),
            parameter => Assert.NotEqual(typeof(string), parameter.ParameterType));

        var clipboardProbe = typeof(WindowsClipboardPasteSource).GetMethod(nameof(WindowsClipboardPasteSource.Probe));
        Assert.NotNull(clipboardProbe);
        Assert.Empty(clipboardProbe!.GetParameters());
        Assert.All(
            typeof(WindowsClipboardPasteSource).GetMethods().Where(method => method.IsPublic),
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(string[])));
    }

    /// <summary>WP-33：拖放的真实 DROPFILES 载荷被解析成捕获票据。</summary>
    [Fact]
    public void WP33ARealDropDataObjectYieldsCaptureTickets()
    {
        var payload = BuildPayload(1_024);
        var file = NewSourceFile(payload);
        using var dataObject = new FileDropDataObject([file]);

        var result = new WindowsDroppedFilesSource(_adapter)
            .RegisterDroppedFiles(dataObject, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        var captureId = Assert.Single(result.CaptureIds);
        var captured = _adapter.CaptureNative(captureId, TestContext.Current.CancellationToken);
        Assert.True(captured.Ok);
        Assert.Equal(payload, File.ReadAllBytes(captured.StagedPath!));
    }

    /// <summary>WP-34：拖放文件数超限被拒绝，不注册任何票据。</summary>
    [Fact]
    public void WP34ADropBeyondTheBatchLimitIsRefusedWithoutRegistering()
    {
        var files = Enumerable.Range(0, AttachmentProtocol.MaxFilesPerBatch + 1)
            .Select(_ => NewSourceFile([0x01]))
            .ToArray();
        using var dataObject = new FileDropDataObject(files);

        var result = new WindowsDroppedFilesSource(_adapter)
            .RegisterDroppedFiles(dataObject, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("limit-batch-files", result.Code);
        Assert.Empty(result.CaptureIds);
        Assert.Equal(0, _adapter.PendingCaptureCount);
    }

    private static byte[] BuildPayload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private string NewSourceFile(byte[] content)
    {
        var path = Path.Combine(_layoutRoot, "source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    // ————————————————————————————————————————————————————————————
    // 真实剪贴板写入（用例自己准备被测状态；全部属 WindowsPending）
    // ————————————————————————————————————————————————————————————

    private static void SetClipboardFileDropList(IReadOnlyList<string> paths)
    {
        // DROPFILES + 宽字符双 NUL 结尾列表，放进 HGLOBAL 后 SetClipboardData(CF_HDROP)。
        var headerSize = Marshal.SizeOf<DropFiles>();
        var textBytes = paths.Sum(path => (path.Length + 1) * 2) + 2;
        var handle = Marshal.AllocHGlobal(headerSize + textBytes);
        try
        {
            var header = new DropFiles { pFiles = (uint)headerSize, fWide = 1 };
            Marshal.StructureToPtr(header, handle, fDeleteOld: false);
            var cursor = handle + headerSize;
            foreach (var path in paths)
            {
                var bytes = Encoding.Unicode.GetBytes(path + "\0");
                Marshal.Copy(bytes, 0, cursor, bytes.Length);
                cursor += bytes.Length;
            }

            Marshal.WriteInt16(cursor, 0);
            Assert.True(WindowsClipboardNative.OpenClipboard(nint.Zero));
            try
            {
                Assert.True(ClipboardWriterNative.EmptyClipboard());
                Assert.NotEqual(nint.Zero, ClipboardWriterNative.SetClipboardData(WindowsClipboardNative.CfHDrop, handle));
            }
            finally
            {
                _ = WindowsClipboardNative.CloseClipboard();
            }

            // SetClipboardData 成功后所有权归系统：替换或进程退出时由系统释放，测试不再 FreeHGlobal。
        }
        catch
        {
            Marshal.FreeHGlobal(handle);
            throw;
        }
    }

    private static void SetClipboardDib(int width, int height) => SetClipboardRawDib(BuildDib(width, height));

    private static void SetClipboardDibHeaderOnly(int width, int height) =>
        SetClipboardRawDib(BuildDibHeader(width, height));

    private static void SetClipboardRawDib(byte[] dib)
    {
        var handle = Marshal.AllocHGlobal(dib.Length);
        Marshal.Copy(dib, 0, handle, dib.Length);
        Assert.True(WindowsClipboardNative.OpenClipboard(nint.Zero));
        try
        {
            Assert.True(ClipboardWriterNative.EmptyClipboard());
            Assert.NotEqual(nint.Zero, ClipboardWriterNative.SetClipboardData(WindowsClipboardNative.CfDib, handle));
        }
        finally
        {
            _ = WindowsClipboardNative.CloseClipboard();
        }
    }


    private static byte[] BuildDibHeader(int width, int height)
    {
        var dib = new byte[40];
        BitConverter.TryWriteBytes(dib.AsSpan(0, 4), 40u);
        BitConverter.TryWriteBytes(dib.AsSpan(4, 4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8, 4), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12, 2), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14, 2), (ushort)32);
        return dib;
    }

    private static byte[] BuildDib(int width, int height)
    {
        var stride = width * 4;
        var dib = new byte[40 + (stride * height)];
        BuildDibHeader(width, height).CopyTo(dib, 0);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var offset = 40 + (row * stride) + (column * 4);
                dib[offset] = (byte)(column % 256);
                dib[offset + 1] = (byte)(row % 256);
                dib[offset + 2] = 0x40;
                dib[offset + 3] = 0xFF;
            }
        }

        return dib;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint pFiles;

        public int x;

        public int y;

        public int fNC;

        public int fWide;
    }

    /// <summary>用真实 DROPFILES HGLOBAL 实现的最小 OLE 数据对象（只支持 CF_HDROP）。</summary>
    private sealed class FileDropDataObject : System.Runtime.InteropServices.ComTypes.IDataObject, IDisposable
    {
        private readonly nint _handle;
        private bool _handedOver;

        public FileDropDataObject(IReadOnlyList<string> paths)
        {
            var headerSize = Marshal.SizeOf<DropFiles>();
            var textBytes = paths.Sum(path => (path.Length + 1) * 2) + 2;
            _handle = Marshal.AllocHGlobal(headerSize + textBytes);
            var header = new DropFiles { pFiles = (uint)headerSize, fWide = 1 };
            Marshal.StructureToPtr(header, _handle, fDeleteOld: false);
            var cursor = _handle + headerSize;
            foreach (var path in paths)
            {
                var bytes = Encoding.Unicode.GetBytes(path + "\0");
                Marshal.Copy(bytes, 0, cursor, bytes.Length);
                cursor += bytes.Length;
            }

            Marshal.WriteInt16(cursor, 0);
        }

        /// <summary>
        /// GetData 之后 HGLOBAL 的所有权已经交给调用方（由 ReleaseStgMedium 释放），
        /// 因此这里只释放"从未交出过"的句柄，避免双重释放。
        /// </summary>
        public void Dispose()
        {
            if (!_handedOver)
            {
                Marshal.FreeHGlobal(_handle);
            }
        }

        public int QueryGetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format) =>
            format.cfFormat == WindowsClipboardNative.CfHDrop ? 0 : unchecked((int)0x80040064);

        public void GetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
        {
            _handedOver = true;
            medium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM
            {
                tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
                unionmember = _handle,
            };
        }

        public void GetDataHere(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium) =>
            throw new NotSupportedException();

        public int GetCanonicalFormatEtc(ref System.Runtime.InteropServices.ComTypes.FORMATETC formatIn, out System.Runtime.InteropServices.ComTypes.FORMATETC formatOut)
        {
            formatOut = formatIn;
            return unchecked((int)0x80040064);
        }

        public void SetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC formatIn, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium, bool release) =>
            throw new NotSupportedException();

        public System.Runtime.InteropServices.ComTypes.IEnumFORMATETC EnumFormatEtc(
            System.Runtime.InteropServices.ComTypes.DATADIR direction) =>
            throw new NotSupportedException();

        public int DAdvise(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, System.Runtime.InteropServices.ComTypes.ADVF advf, System.Runtime.InteropServices.ComTypes.IAdviseSink adviseSink, out int connection) =>
            throw new NotSupportedException();

        public void DUnadvise(int connection) => throw new NotSupportedException();

        public int EnumDAdvise(out System.Runtime.InteropServices.ComTypes.IEnumSTATDATA enumAdvise) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// 用例专用的剪贴板写入入口（生产代码只读剪贴板，从不写）。
/// 这里保留 <c>DllImport</c> 并显式抑制"改用 LibraryImport"的建议，
/// 因为测试程序集没有开启 <c>AllowUnsafeBlocks</c>，而固定平台身份不应为测试便利改动。
/// </summary>
internal static class ClipboardWriterNative
{
    [DllImport("user32.dll", SetLastError = true)]
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time",
        Justification = "测试程序集未开启 AllowUnsafeBlocks；生产侧仍使用源生成的 LibraryImport。")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time",
        Justification = "测试程序集未开启 AllowUnsafeBlocks；生产侧仍使用源生成的 LibraryImport。")]
    internal static extern nint SetClipboardData(uint format, nint memory);
}
