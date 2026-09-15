using System.Runtime.InteropServices;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// D16 剪贴板相关的真实 Win32 入口（user32/shell32/kernel32）。
///
/// 设计要点：
/// <list type="bullet">
/// <item>只使用<b>非阻塞</b>查询：<c>GetClipboardSequenceNumber</c>、<c>CountClipboardFormats</c>、
/// <c>IsClipboardFormatAvailable</c> 都不需要打开剪贴板；</item>
/// <item><c>DragQueryFileW(hDrop, 0xFFFFFFFF, …)</c> 只取文件个数，不搬路径字符串；</item>
/// <item><c>GlobalLock</c> 只加锁不复制：探测阶段只读 DIB 头（≤124 字节），
/// 大块像素复制一律发生在 STA 之外（见 <see cref="WindowsClipboardPasteSource"/>）；</item>
/// <item>每次打开都用 <c>try/finally</c> 关闭，绝不在异常路径上把剪贴板锁住。</item>
/// </list>
/// 这些调用的真实行为（打开失败码、占用语义、句柄生命周期）全部属 WindowsPending，
/// 见 <c>WindowsPendingCases.md</c>。
/// </summary>
internal static unsafe partial class WindowsClipboardNative
{
    /// <summary>文本格式。</summary>
    internal const uint CfText = 1;

    /// <summary>位图句柄格式（GDI 位图，不是 DIB 字节）。</summary>
    internal const uint CfBitmap = 2;

    /// <summary>DIB 字节（BITMAPINFOHEADER + 像素）。</summary>
    internal const uint CfDib = 8;

    /// <summary>Unicode 文本。</summary>
    internal const uint CfUnicodeText = 13;

    /// <summary>文件列表（HDROP）。</summary>
    internal const uint CfHDrop = 15;

    /// <summary>DIB V5（带 alpha 掩码）。</summary>
    internal const uint CfDibV5 = 17;

    /// <summary><c>DragQueryFileW</c> 的"只取个数"索引。</summary>
    internal const uint DragQueryFileCount = 0xFFFFFFFF;

    /// <summary>探测阶段读取的 DIB 头上限（BITMAPV5HEADER = 124 字节）。</summary>
    internal const int MaxDibHeaderBytes = 124;

    /// <summary>打开剪贴板的最大尝试次数（短时有界重试，绝不超过 <see cref="OpenRetryDelayMs"/> × 次数）。</summary>
    internal const int OpenAttempts = 3;

    /// <summary>每次重试之间的等待毫秒数（很短：宁可给出 clipboard-busy 也不拖住 STA）。</summary>
    internal const int OpenRetryDelayMs = 5;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int CountClipboardFormats();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint mem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint mem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint mem);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW", SetLastError = true)]
    internal static partial uint DragQueryFileW(nint hDrop, uint fileIndex, char* fileName, uint fileNameLength);
}
