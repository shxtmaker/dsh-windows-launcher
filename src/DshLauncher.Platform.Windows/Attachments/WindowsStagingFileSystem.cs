using System.ComponentModel;
using System.Runtime.InteropServices;
using DshLauncher.Core.Attachments;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// D14 的 Windows 原生文件系统端口：独占创建暂存文件、无跟随解析条目、句柄式删除与磁盘空间查询。
///
/// 关键安全性质（全部是 Win32 语义，实机用例见
/// <c>tests/DshLauncher.Platform.Windows.Tests/Attachments/WindowsPendingCases.md</c>）：
/// <list type="bullet">
/// <item>创建一律用 <c>CREATE_NEW</c>：目标名已存在（包括符号链接/联接/目录）即失败，绝不覆盖、绝不顺着链接写；</item>
/// <item>所有检查都用 <c>FILE_FLAG_OPEN_REPARSE_POINT</c> 打开，因此拿到的是<b>链接本身</b>的属性，
/// 不会被重解析点改写到根外；</item>
/// <item>删除用句柄上的 <c>SetFileInformationByHandle(FileDispositionInfo)</c>，
/// 删的是已经复核过的那个对象，而不是重新按路径解析的结果（消除 TOCTOU 窗口）；</item>
/// <item>删除前再次做包含关系与重解析点复核，任何根外目标都不触碰。</item>
/// </list>
/// 本类型只在 Windows 上编译与运行；Linux 侧的可移植判定在 <see cref="StagingCleanupPolicy"/> 等纯函数里。
/// </summary>
internal sealed partial class WindowsStagingFileSystem : IStagingFileSystem
{
    public long GetAvailableFreeBytes(string ownedRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRootPath);
        if (!WindowsStagingNative.TryGetDiskFreeSpace(ownedRootPath, out var available))
        {
            throw new IOException(
                "无法查询暂存根所在卷的可用空间。",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return checked((long)Math.Min(available, long.MaxValue));
    }

    public IReadOnlyList<string> ListOwnedEntries(string ownedRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRootPath);
        if (!Directory.Exists(ownedRootPath))
        {
            return [];
        }

        return [.. Directory.EnumerateFileSystemEntries(ownedRootPath)];
    }

    public StagingOwnedEntryInfo InspectOwnedEntry(string ownedRootPath, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (!WindowsStagingNative.TryInspectPathNoFollow(fullPath, out var inspection))
        {
            return new StagingOwnedEntryInfo(fullPath, fullPath, Exists: false, IsDirectory: false, IsReparsePoint: false);
        }

        return new StagingOwnedEntryInfo(
            fullPath,
            inspection.FinalPath,
            Exists: true,
            inspection.IsDirectory,
            inspection.IsReparsePoint);
    }

    public IStagingDestinationFile CreateExclusiveOwnedFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        using var handle = WindowsStagingNative.CreateNewOwnedFile(fullPath);
        var finalPath = WindowsStagingNative.GetNormalizedFinalPath(handle);
        return new WindowsStagingDestinationFile(handle, finalPath, isHandleOwner: true);
    }

    public void DeleteOwnedEntry(string ownedRootPath, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // 复核一：词法包含关系（穿越/绝对/UNC/根外解析结果一律拒绝）。
        var confined = StagingPathPolicy.ConfineToOwnedRoot(ownedRootPath, fullPath);
        if (!confined.Allowed)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                confined.Detail ?? "清理目标不在自有暂存根内");
        }

        // 复核二：无跟随解析（重解析点与目录一律拒绝，避免顺着链接删除根外数据）。
        if (!WindowsStagingNative.TryInspectPathNoFollow(fullPath, out var inspection))
        {
            throw new IOException("清理目标已不存在。");
        }

        if (inspection.IsReparsePoint)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupReparsePoint,
                "清理目标是重解析点：拒绝顺链删除");
        }

        if (inspection.IsDirectory)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupNotRegularFile,
                "清理目标是目录：拒绝删除");
        }

        WindowsStagingNative.EnsureSameNoFollowRootPath(ownedRootPath, inspection.FinalPath);
        if (!StagingPathPolicy.IsWithinOwnedRoot(ownedRootPath, inspection.FinalPath))
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                "解析后的最终路径落在自有根之外");
        }

        // 复核三：按句柄删除已复核的对象本身。
        using var handle = WindowsStagingNative.OpenForDelete(fullPath);
        WindowsStagingNative.MarkHandleForDeletion(handle);
    }
}

/// <summary>
/// 原生暂存目标：句柄 + 解析出的最终路径。写入顺序推进，落盘用 <c>FlushFileBuffers</c>，
/// 删除用句柄处置信息；释放时关闭句柄（由 Core 在 finally 中保证每路都会调用）。
/// </summary>
internal sealed class WindowsStagingDestinationFile : IStagingDestinationFile
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private bool _disposed;

    public WindowsStagingDestinationFile(SafeFileHandle handle, string finalPath, bool isHandleOwner)
    {
        _handle = handle;
        _ownsHandle = isHandleOwner;
        FinalPath = finalPath;
    }

    public string FinalPath { get; }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 顺序写：WriteFile 自己推进文件指针，Core 也保证按偏移递增的顺序写入。
        WindowsStagingNative.WriteAll(_handle, buffer);
    }

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowsStagingNative.FlushToDisk(_handle);
    }

    public void Delete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowsStagingNative.MarkHandleForDeletion(_handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHandle)
        {
            _handle.Dispose();
        }
    }
}

/// <summary>D14 用到的 Win32 常量、结构与入口（集中一处，便于逐项对照实机待验清单）。</summary>
internal static unsafe partial class WindowsStagingNative
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint Delete = 0x00010000;
    public const uint ShareRead = 0x00000001;
    public const uint ShareWrite = 0x00000002;
    public const uint ShareDelete = 0x00000004;
    public const uint CreateNew = 1;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x00000080;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileFlagSequentialScan = 0x08000000;

    public const uint FileAttributeDirectory = 0x00000010;
    public const uint FileAttributeDevice = 0x00000040;
    public const uint FileAttributeReparsePoint = 0x00000400;
    public const uint FileAttributeOffline = 0x00001000;

    /// <summary>云占位（打开即触发下载）。</summary>
    public const uint FileAttributeRecallOnOpen = 0x00040000;

    /// <summary>云占位（读取数据即触发下载）。</summary>
    public const uint FileAttributeRecallOnDataAccess = 0x00400000;

    /// <summary>递归回读占位（OneDrive 文件按需功能的第三态）。</summary>
    public const uint FileAttributeRecallOnDataAccessFamily =
        FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess;

    public const uint FileTypeDisk = 1;
    public const uint DriveRemote = 4;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorFileExists = 80;
    public const int ErrorAlreadyExists = 183;

    private const int FileAttributeTagInfo = 9;
    private const int FileDispositionInfo = 4;

    public static SafeFileHandle CreateNewOwnedFile(string fullPath)
    {
        var handle = CreateFileW(
            fullPath,
            GenericWrite | GenericRead,
            shareMode: 0,
            securityAttributes: 0,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagSequentialScan,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new AttachmentStagingException(
                AttachmentStagingCodes.DestinationCreateFailed,
                $"独占创建暂存文件失败（Win32 {error}）。",
                new Win32Exception(error));
        }

        return handle;
    }

    public static SafeFileHandle OpenForDelete(string fullPath)
    {
        var handle = CreateFileW(
            fullPath,
            Delete,
            ShareRead | ShareWrite | ShareDelete,
            securityAttributes: 0,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupFailed,
                $"打开待删除条目失败（Win32 {error}）。",
                new Win32Exception(error));
        }

        return handle;
    }

    public static SafeFileHandle OpenSourceNoFollow(string fullPath)
    {
        var handle = CreateFileW(
            fullPath,
            GenericRead,
            ShareRead,
            securityAttributes: 0,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagSequentialScan,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            var code = error is ErrorSharingViolation or ErrorLockViolation
                ? AttachmentStagingCodes.SourceLocked
                : AttachmentStagingCodes.SourceUnavailable;
            throw new AttachmentStagingException(
                code,
                $"打开源文件失败（Win32 {error}）。",
                new Win32Exception(error));
        }

        return handle;
    }

    public static bool TryInspectPathNoFollow(string path, out InspectedOwnedPath inspection)
    {
        using var handle = CreateFileW(
            path,
            desiredAccess: 0,
            ShareRead | ShareWrite | ShareDelete,
            securityAttributes: 0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                inspection = default;
                return false;
            }

            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupFailed,
                $"无跟随检查路径失败（Win32 {error}）。",
                new Win32Exception(error));
        }

        var attributes = GetAttributes(handle);
        inspection = new InspectedOwnedPath(
            GetNormalizedFinalPath(handle),
            (attributes & FileAttributeDirectory) != 0,
            (attributes & FileAttributeReparsePoint) != 0);
        return true;
    }

    public static void EnsureOwnedDirectory(string ownedRootPath)
    {
        Directory.CreateDirectory(ownedRootPath);
        if (!TryInspectPathNoFollow(ownedRootPath, out var inspection) || !inspection.IsDirectory)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupFailed,
                "暂存根不存在或不是目录。");
        }

        if (inspection.IsReparsePoint)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.ReparsePoint,
                "暂存根本身是重解析点：拒绝在其下写入。");
        }

        var expected = StagingPathPolicy.NormalizeOwnedRoot(ownedRootPath);
        if (!string.Equals(inspection.FinalPath, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.DestinationOutsideOwnedRoot,
                "暂存根解析后的最终路径与请求路径不一致（可能被重解析点改写）。");
        }
    }

    public static void EnsureOwnershipMarker(string ownedRootPath, string content)
    {
        var markerPath = ownedRootPath + "\\" + AttachmentStagingRoot.OwnershipMarkerName;
        if (TryInspectPathNoFollow(markerPath, out var inspection))
        {
            if (inspection.IsReparsePoint || inspection.IsDirectory)
            {
                throw new AttachmentStagingException(
                    AttachmentStagingCodes.ReparsePoint,
                    "暂存所有权标记被替换为重解析点或目录。");
            }

            var actual = File.ReadAllText(markerPath);
            if (!string.Equals(actual, content, StringComparison.Ordinal))
            {
                throw new AttachmentStagingException(
                    AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                    "暂存所有权标记内容不匹配：这不是本应用的暂存目录。");
            }

            return;
        }

        using var handle = CreateNewOwnedFile(markerPath);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        WriteAll(handle, bytes);
        FlushToDisk(handle);
    }

    /// <summary>解析后的最终路径必须与请求的自有根一致（防止根本身被改写）。</summary>
    public static void EnsureSameNoFollowRootPath(string ownedRootPath, string resolvedFinalPath)
    {
        var expected = StagingPathPolicy.NormalizeOwnedRoot(ownedRootPath);
        if (!StagingPathPolicy.IsWithinOwnedRoot(expected, resolvedFinalPath))
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                "解析后的最终路径不在自有暂存根内。");
        }
    }

    public static uint GetAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var attributeInformation,
                checked((uint)Marshal.SizeOf<FileAttributeTagInformation>())))
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.SourceUnavailable,
                "无法读取文件属性。",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return attributeInformation.FileAttributes;
    }

    public static bool TryReadHandleIdentity(SafeFileHandle handle, out StagingSourceIdentity identity)
    {
        identity = StagingSourceIdentity.Missing;
        if (!GetFileInformationByHandle(handle, out var information))
        {
            return false;
        }

        identity = new StagingSourceIdentity(
            Exists: true,
            Length: ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
            LastWriteTimeUtcTicks: ToTicks(information.LastWriteTime),
            FileId: ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            VolumeSerial: information.VolumeSerialNumber,
            IsReparsePoint: (information.FileAttributes & FileAttributeReparsePoint) != 0);
        return true;
    }

    public static uint GetHandleFileType(SafeFileHandle handle) => GetFileType(handle);

    /// <summary>
    /// 复制后按<b>路径</b>重新解析身份（无跟随）。路径已消失、无法检查或读取失败都返回 false，
    /// 由 Core 判定为源变化——"证明不了没变"就不算没变。
    /// </summary>
    public static bool TryReadPathIdentity(string path, out StagingSourceIdentity identity)
    {
        identity = StagingSourceIdentity.Missing;
        try
        {
            using var handle = CreateFileW(
                path,
                desiredAccess: 0,
                ShareRead | ShareWrite | ShareDelete,
                securityAttributes: 0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                templateFile: 0);
            if (handle.IsInvalid)
            {
                return false;
            }

            var attributes = GetAttributes(handle);
            if (!TryReadHandleIdentity(handle, out var information))
            {
                return false;
            }

            identity = information with
            {
                IsReparsePoint = (attributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0,
            };
            return true;
        }
        catch (AttachmentStagingException)
        {
            return false;
        }
    }

    public static bool IsRemoteVolume(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        return GetDriveTypeW(root) == DriveRemote;
    }

    public static bool TryGetDiskFreeSpace(string path, out ulong availableFreeBytes)
    {
        if (!GetDiskFreeSpaceExW(path, out availableFreeBytes, out _, out _))
        {
            availableFreeBytes = 0;
            return false;
        }

        return true;
    }

    public static void WriteAll(SafeFileHandle handle, ReadOnlySpan<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            uint chunkWritten;
            fixed (byte* pointer = &buffer[written])
            {
                if (!WriteFile(handle, pointer, checked((uint)(buffer.Length - written)), out chunkWritten, 0))
                {
                    throw new IOException(
                        "写入暂存文件失败。",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                }
            }

            if (chunkWritten == 0)
            {
                throw new IOException("写入暂存文件返回 0 字节。");
            }

            written += checked((int)chunkWritten);
        }
    }

    public static void FlushToDisk(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
        {
            throw new IOException(
                "暂存文件落盘失败。",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    public static void MarkHandleForDeletion(SafeFileHandle handle)
    {
        // FILE_DISPOSITION_INFO.DeleteFile 是单字节 BOOLEAN：用 byte 保持结构可 blittable
        // （LibraryImport 不支持在结构里封送 bool）。
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                checked((uint)Marshal.SizeOf<FileDispositionInformation>())))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CleanupFailed,
                $"删除自有暂存条目失败（Win32 {error}）。",
                new Win32Exception(error));
        }
    }

    public static unsafe string GetNormalizedFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandleW(handle, pointer, checked((uint)buffer.Length), flags: 0);
            }

            if (length == 0)
            {
                throw new AttachmentStagingException(
                    AttachmentStagingCodes.SourceUnavailable,
                    "无法解析句柄的最终路径。",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < buffer.Length)
            {
                return NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
            }

            buffer = new char[checked((int)length) + 1];
        }
    }

    public static string NormalizeFinalPath(string path)
    {
        const string uncExtendedPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncExtendedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[uncExtendedPrefix.Length..];
        }
        else if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[extendedPrefix.Length..];
        }

        return StagingPathPolicy.NormalizeOwnedRoot(path);
    }

    private static long ToTicks(FileTime fileTime) =>
        fileTime.High == 0 && fileTime.Low == 0
            ? 0
            : DateTime.FromFileTimeUtc(((long)fileTime.High << 32) | fileTime.Low).Ticks;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFileType(SafeFileHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetDriveTypeW(
        [MarshalAs(UnmanagedType.LPWStr)] string rootPathName);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetDiskFreeSpaceExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(
        SafeFileHandle file,
        byte* buffer,
        uint numberOfBytesToRead,
        out uint numberOfBytesRead,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        SafeFileHandle file,
        byte* buffer,
        uint numberOfBytesToWrite,
        out uint numberOfBytesWritten,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        char* filePath,
        uint filePathLength,
        uint flags);

    /// <summary>读取源块（Core 侧保证目标 span 不超过 256 KiB）。</summary>
    public static int ReadFromHandle(SafeFileHandle handle, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        uint read;
        fixed (byte* pointer = destination)
        {
            if (!ReadFile(handle, pointer, checked((uint)destination.Length), out read, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new AttachmentStagingException(
                    error is ErrorSharingViolation or ErrorLockViolation
                        ? AttachmentStagingCodes.SourceLocked
                        : AttachmentStagingCodes.SourceReadFailed,
                    $"读取源文件失败（Win32 {error}）。",
                    new Win32Exception(error));
            }
        }

        return checked((int)read);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileAttributeTagInformation(uint FileAttributes, uint ReparseTag);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }
}

/// <summary>无跟随检查结果。</summary>
internal readonly record struct InspectedOwnedPath(string FinalPath, bool IsDirectory, bool IsReparsePoint);
