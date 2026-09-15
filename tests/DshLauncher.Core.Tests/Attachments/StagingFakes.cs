using DshLauncher.Core.Attachments;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D14 平台中立测试用的假文件系统端口：不碰真实磁盘，可精确注入可用空间、
/// 创建/写入失败、解析后路径逃逸（模拟联接/符号链接）与删除失败，
/// 并逐次记录创建、删除与句柄释放，供断言"每条退出路径都关闭句柄"。
/// </summary>
internal sealed class FakeStagingFileSystem : IStagingFileSystem
{
    public const string Root = @"C:\owned\staging";

    public long FreeBytes { get; set; } = 8L * 1024 * 1024 * 1024;

    public Dictionary<string, FakeOwnedEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> CreateRequests { get; } = [];

    public List<string> DeletedPaths { get; } = [];

    public List<FakeDestinationFile> Destinations { get; } = [];

    public Func<string, bool>? CreateRefused { get; set; }

    /// <summary>非 null 时，下一次创建返回这个注入目标（用于写入失败/解析路径逃逸用例）。</summary>
    public IStagingDestinationFile? NextDestination { get; set; }

    public bool ListFails { get; set; }

    public long GetAvailableFreeBytes(string ownedRootPath) => FreeBytes;

    public IReadOnlyList<string> ListOwnedEntries(string ownedRootPath)
    {
        if (ListFails)
        {
            throw new IOException("目录不可枚举");
        }

        return [.. Entries.Values.Where(entry => entry.Listed).Select(entry => entry.FullPath)];
    }

    public StagingOwnedEntryInfo InspectOwnedEntry(string ownedRootPath, string fullPath)
    {
        if (!Entries.TryGetValue(fullPath, out var entry))
        {
            return new StagingOwnedEntryInfo(fullPath, fullPath, Exists: false, IsDirectory: false, IsReparsePoint: false);
        }

        return new StagingOwnedEntryInfo(fullPath, entry.FinalPath, entry.Exists, entry.IsDirectory, entry.IsReparsePoint);
    }

    public IStagingDestinationFile CreateExclusiveOwnedFile(string fullPath)
    {
        CreateRequests.Add(fullPath);
        if (CreateRefused?.Invoke(fullPath) == true)
        {
            throw new IOException("目标已存在或不可创建");
        }

        if (NextDestination is { } injected)
        {
            NextDestination = null;
            return injected;
        }

        var entry = new FakeOwnedEntry { FullPath = fullPath, FinalPath = fullPath };
        Entries[fullPath] = entry;
        var destination = new FakeDestinationFile(this, entry);
        Destinations.Add(destination);
        return destination;
    }

    public void DeleteOwnedEntry(string ownedRootPath, string fullPath)
    {
        if (Entries.TryGetValue(fullPath, out var entry) && entry.DeleteFails)
        {
            throw new IOException("文件被占用");
        }

        DeletedPaths.Add(fullPath);
        Entries.Remove(fullPath);
    }

    internal void RemoveEntry(string fullPath) => Entries.Remove(fullPath);

    public FakeOwnedEntry AddEntry(
        string fullPath,
        bool isReparsePoint = false,
        bool isDirectory = false,
        string? finalPath = null,
        bool deleteFails = false,
        bool listed = true) =>
        Entries[fullPath] = new FakeOwnedEntry
        {
            FullPath = fullPath,
            FinalPath = finalPath ?? fullPath,
            IsReparsePoint = isReparsePoint,
            IsDirectory = isDirectory,
            DeleteFails = deleteFails,
            Listed = listed,
        };
}

internal sealed class FakeOwnedEntry
{
    public required string FullPath { get; init; }

    public string FinalPath { get; set; } = string.Empty;

    public bool Exists { get; set; } = true;

    public bool IsDirectory { get; set; }

    public bool IsReparsePoint { get; set; }

    public bool DeleteFails { get; set; }

    public bool Listed { get; set; } = true;
}

/// <summary>假暂存目标：记录写入片段、最大写入长度与释放次数。</summary>
internal sealed class FakeDestinationFile : IStagingDestinationFile
{
    private readonly FakeStagingFileSystem _fileSystem;
    private readonly FakeOwnedEntry _entry;

    public FakeDestinationFile(FakeStagingFileSystem fileSystem, FakeOwnedEntry entry)
    {
        _fileSystem = fileSystem;
        _entry = entry;
    }

    public string FinalPath => _entry.FinalPath;

    public List<byte> Written { get; } = [];

    public int MaxWriteLength { get; private set; }

    public bool Flushed { get; private set; }

    public bool Deleted { get; private set; }

    public int DisposeCount { get; private set; }

    public bool FailOnWrite { get; set; }

    public bool FailOnFlush { get; set; }

    /// <summary>创建后把解析路径改到根外，模拟父目录被联接替换。</summary>
    public void EscapeTo(string outsidePath) => _entry.FinalPath = outsidePath;

    public void Write(ReadOnlySpan<byte> buffer)
    {
        if (FailOnWrite)
        {
            throw new IOException("磁盘写入失败");
        }

        MaxWriteLength = Math.Max(MaxWriteLength, buffer.Length);
        Written.AddRange(buffer.ToArray());
    }

    public void FlushToDisk()
    {
        if (FailOnFlush)
        {
            throw new IOException("落盘失败");
        }

        Flushed = true;
    }

    public void Delete()
    {
        Deleted = true;
        _fileSystem.RemoveEntry(_entry.FullPath);
    }

    public void Dispose() => DisposeCount += 1;
}

/// <summary>假源句柄：可按脚本返回内容、短读、EOF、增长、非法长度或读取异常。</summary>
internal sealed class FakeStagingSource : IStagingSourceHandle
{
    private readonly byte[] _content;

    public FakeStagingSource(byte[] content, StagingSourceDescriptor descriptor)
    {
        _content = content;
        Descriptor = descriptor;
        InitialIdentity = new StagingSourceIdentity(
            Exists: true,
            Length: descriptor.Length,
            LastWriteTimeUtcTicks: 638_000_000_000_000_000,
            FileId: 42,
            VolumeSerial: 7,
            IsReparsePoint: false);
        AfterIdentity = InitialIdentity;
    }

    public StagingSourceDescriptor Descriptor { get; }

    public StagingSourceIdentity InitialIdentity { get; set; }

    public StagingSourceIdentity AfterIdentity { get; set; }

    /// <summary>每次 <see cref="Read"/> 的请求长度（断言没有整文件缓冲）。</summary>
    public List<int> RequestedLengths { get; } = [];

    public int ReadCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>读取时最多返回的字节数（模拟短读）。</summary>
    public int MaxReturnPerRead { get; set; } = int.MaxValue;

    public bool FailOnRead { get; set; }

    public bool ReturnIllegalLength { get; set; }

    public Action? OnRead { get; set; }

    public int Read(Span<byte> destination)
    {
        ReadCount += 1;
        RequestedLengths.Add(destination.Length);
        OnRead?.Invoke();
        if (FailOnRead)
        {
            throw new IOException("源读取失败");
        }

        if (ReturnIllegalLength)
        {
            return destination.Length + 1;
        }

        var offset = Position;
        if (offset >= _content.Length)
        {
            return 0;
        }

        var count = Math.Min(Math.Min(destination.Length, MaxReturnPerRead), _content.Length - offset);
        _content.AsSpan(offset, count).CopyTo(destination);
        Position += count;
        return count;
    }

    public int Position { get; private set; }

    public StagingSourceIdentity ReadIdentity() => AfterIdentity;

    public void Dispose() => DisposeCount += 1;
}

/// <summary>假原生捕获票据：可返回句柄，也可抛出带码的平台异常。</summary>
internal sealed class FakeNativeCapture : INativeStagingCapture
{
    public required string CaptureId { get; init; }

    public required string SuggestedLeafName { get; init; }

    public FakeStagingSource? Source { get; init; }

    public AttachmentStagingException? OpenFailure { get; init; }

    public int OpenCount { get; private set; }

    public IStagingSourceHandle OpenSource()
    {
        OpenCount += 1;
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        return Source!;
    }
}

/// <summary>D14 测试夹具：自有根 + 假文件系统 + 生产暂存服务。</summary>
internal sealed class StagingFixture : IDisposable
{
    public StagingFixture(AttachmentLimits? limits = null, long freeSpaceReserveBytes = 1024 * 1024)
    {
        FileSystem = new FakeStagingFileSystem();
        Root = new AttachmentStagingRoot(FakeStagingFileSystem.Root);
        Service = new AttachmentStagingService(Root, FileSystem, limits, freeSpaceReserveBytes);
        Service.BeginBatch("batch-1", 10);
    }

    public FakeStagingFileSystem FileSystem { get; }

    public AttachmentStagingRoot Root { get; }

    public AttachmentStagingService Service { get; }

    public void Dispose() => Service.Dispose();

    public static StagingSourceDescriptor Descriptor(
        long length,
        StagingSourceKind kind = StagingSourceKind.RegularFile,
        StagingPathKind pathKind = StagingPathKind.DriveAbsolute,
        string leafName = "报告 v2.txt",
        bool isReparsePoint = false,
        bool isCloudPlaceholder = false,
        bool isOffline = false,
        bool isNetworkShare = false) =>
        new()
        {
            PathKind = pathKind,
            LeafName = leafName,
            Kind = kind,
            Length = length,
            IsReparsePoint = isReparsePoint,
            IsCloudPlaceholder = isCloudPlaceholder,
            IsOffline = isOffline,
            IsNetworkShare = isNetworkShare,
        };

    public static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index += 1)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    public static FakeNativeCapture Capture(byte[] content, StagingSourceDescriptor? descriptor = null) =>
        new()
        {
            CaptureId = "capture-1",
            SuggestedLeafName = "报告 v2.txt",
            Source = new FakeStagingSource(content, descriptor ?? Descriptor(content.Length)),
        };
}
