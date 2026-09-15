using DshLauncher.Core.Attachments;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// D17：已暂存快照的只读字节源（真实 Win32 句柄）。
///
/// 用途：把 D14 已经落在自有暂存根内的快照，按 256 KiB 块喂给 D11 协调器。
/// 边界：
/// <list type="bullet">
/// <item>构造时用 <c>FILE_FLAG_OPEN_REPARSE_POINT</c>（<see cref="WindowsStagingNative.OpenSourceNoFollow"/>）
/// 打开：即使快照位置被换成符号链接/联接也只会打开链接本身，不会跟随到根外；</item>
/// <item>只读 + <c>FILE_SHARE_READ</c>；<see cref="ByteLength"/> 取自 D14 台账，读取超过声明长度
/// 立即按源故障抛出（协调器转成稳定的 <c>source-error</c>）；</item>
/// <item><see cref="Dispose"/> 释放句柄，可重复调用；协调器在成功、失败、取消、身份失效与自身释放时都会调用。</item>
/// </list>
/// </summary>
public sealed class WindowsStagedByteSource : IAttachmentByteSource
{
    private SafeFileHandle? _handle;
    private long _read;

    public WindowsStagedByteSource(string stagedPath, long byteLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        if (byteLength < 0 || byteLength > AttachmentProtocol.MaxFileBytes)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.SourceUnavailable,
                $"快照字节数 {byteLength} 超出 0..{AttachmentProtocol.MaxFileBytes}");
        }

        _handle = WindowsStagingNative.OpenSourceNoFollow(stagedPath);
        ByteLength = checked((int)byteLength);
    }

    /// <summary>快照声明的原始字节数。</summary>
    public int ByteLength { get; }

    /// <summary>是否已释放句柄。</summary>
    public bool IsDisposed => _handle is null;

    public int Read(Span<byte> destination)
    {
        var handle = _handle;
        if (handle is null)
        {
            throw new AttachmentStagingException(AttachmentStagingCodes.SourceReadFailed, "字节源已释放");
        }

        var read = WindowsStagingNative.ReadFromHandle(handle, destination);
        _read += read;
        if (_read > ByteLength)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.SourceChanged,
                $"快照文件比台账声明的 {ByteLength} 字节更长：内容已被改写");
        }

        return read;
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = null;
        handle?.Dispose();
    }
}
