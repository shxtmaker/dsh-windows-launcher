namespace DshLauncher.Core.Attachments;

/// <summary>
/// 宿主<b>自己产生</b>的字节来源（D16 截图 PNG）的一次性原生捕获票据。
///
/// 与文件候选的区别是它根本<b>没有路径</b>：字节由宿主的编码器产生（见
/// <see cref="ScreenshotPngEncoder"/>），因此不存在"页面给一个路径让宿主去读"的可能。
/// 票据 id 仍由平台半区登记时生成，桥只能拿 id，不能构造票据。
/// </summary>
public sealed class InMemoryNativeCapture : INativeStagingCapture
{
    private readonly byte[] _bytes;

    /// <summary>建立一个内存捕获票据。</summary>
    public InMemoryNativeCapture(string captureId, string displayName, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureId);
        ArgumentNullException.ThrowIfNull(bytes);
        CaptureId = captureId;
        SuggestedLeafName = StagingPathPolicy.NormalizeDisplayName(displayName);
        _bytes = bytes;
    }

    /// <summary>一次性票据 id。</summary>
    public string CaptureId { get; }

    /// <summary>建议显示名（截图默认 <c>screenshot.png</c>）。</summary>
    public string SuggestedLeafName { get; }

    /// <summary>字节长度（供限额判定）。</summary>
    public long ByteLength => _bytes.Length;

    /// <summary>打开一个只读的顺序字节源；每次打开都从头开始。</summary>
    public IStagingSourceHandle OpenSource() => new InMemoryStagingSource(_bytes, SuggestedLeafName);
}

/// <summary>内存字节源：顺序读取、短读合法、0 表示 EOF、身份恒定。</summary>
internal sealed class InMemoryStagingSource : IStagingSourceHandle
{
    private readonly byte[] _bytes;
    private int _offset;
    private bool _disposed;

    internal InMemoryStagingSource(byte[] bytes, string leafName)
    {
        _bytes = bytes;
        Descriptor = new StagingSourceDescriptor
        {
            Origin = StagingSourceOrigin.NativeScreenshot,
            PathKind = StagingPathKind.Synthetic,
            LeafName = leafName,
            Kind = StagingSourceKind.RegularFile,
            Length = bytes.Length,
        };
        InitialIdentity = new StagingSourceIdentity(true, bytes.Length, 0, 0, 0, false);
    }

    public StagingSourceDescriptor Descriptor { get; }

    public StagingSourceIdentity InitialIdentity { get; }

    /// <summary>内存来源的内容在本次快照期间不会变化（身份恒定）。</summary>
    public StagingSourceIdentity ReadIdentity() => _disposed ? StagingSourceIdentity.Missing : InitialIdentity;

    public int Read(Span<byte> destination) => ReadInto(destination);

    public void Dispose() => _disposed = true;

    private int ReadInto(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remaining = _bytes.Length - _offset;
        var count = Math.Min(remaining, destination.Length);
        if (count <= 0)
        {
            return 0;
        }

        _bytes.AsSpan(_offset, count).CopyTo(destination);
        _offset += count;
        return count;
    }
}
