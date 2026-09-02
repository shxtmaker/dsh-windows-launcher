using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Platform.Windows;

/// <summary>
/// Layout of the hub's application data root: an ownership marker plus the
/// primary and backup hub documents. The root itself stays free of any other
/// content, so an unmarked non-empty directory is treated as foreign.
/// </summary>
public sealed class ApplicationDataLayout
{
    public const string OwnershipMarkerName = ".dsh-windows-launcher-owner";
    public const string OwnershipMarkerContent = "DshWindowsLauncher:v2";
    public const string LegacyOwnershipMarkerContent = "DshWindowsLauncher:v1";

    public ApplicationDataLayout(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException(
                "Application data root must be an absolute path.",
                nameof(rootPath));
        }

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        OwnershipMarkerPath = Path.Combine(RootPath, OwnershipMarkerName);
        HubDocumentPath = Path.Combine(RootPath, "pairing-hub.json");
        HubDocumentBackupPath = Path.Combine(RootPath, "pairing-hub.json.bak");
    }

    public string RootPath { get; }

    public string OwnershipMarkerPath { get; }

    public string HubDocumentPath { get; }

    public string HubDocumentBackupPath { get; }

    public string TargetsRoot => Path.Combine(RootPath, "targets");

    public string GetTargetRoot(Guid targetId)
    {
        ValidateTargetId(targetId);
        return Path.Combine(TargetsRoot, targetId.ToString("N"));
    }

    public string GetUdfPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), "udf");

    private static void ValidateTargetId(Guid targetId)
    {
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target identity must not be empty.",
                nameof(targetId));
        }
    }
}

/// <summary>
/// Owns the application data root: creates it with an ownership marker,
/// verifies the marker against reparse-point attacks on every access, and
/// reads/writes the hub document as atomic write-through snapshots whose
/// replaced primary lands in the backup slot.
/// </summary>
public sealed partial class ApplicationDataStore : IDisposable
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfo = 9;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int InitialFinalPathCapacity = 512;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ApplicationDataStore(ApplicationDataLayout layout)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public ApplicationDataLayout Layout { get; }

    public void Dispose() => _writeGate.Dispose();

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var existed = TryInspectDirectoryNoFollow(Layout.RootPath, out var applicationRoot);
        if (!existed)
        {
            Directory.CreateDirectory(Layout.RootPath);
            applicationRoot = InspectRequiredDirectoryNoFollow(Layout.RootPath);
        }

        if (TryInspectPathNoFollow(Layout.OwnershipMarkerPath, out _))
        {
            var content = await File.ReadAllTextAsync(
                Layout.OwnershipMarkerPath,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(content, ApplicationDataLayout.OwnershipMarkerContent, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(content, ApplicationDataLayout.LegacyOwnershipMarkerContent, StringComparison.Ordinal))
            {
                // A V1 root is ours by construction: adopt it by upgrading the
                // marker in place. The V1 payload (browser sessions, target
                // catalog) stays untouched as residue; the hub reads only its
                // own pairing-hub.json, which a V1 root never has.
                await VerifyMarkerAsync(
                    Layout.OwnershipMarkerPath,
                    ApplicationDataLayout.LegacyOwnershipMarkerContent,
                    applicationRoot.FinalPath,
                    cancellationToken).ConfigureAwait(false);
                await ReplaceMarkerContentAsync(
                    Layout.OwnershipMarkerPath,
                    ApplicationDataLayout.OwnershipMarkerContent,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new ApplicationDataOwnershipException(
                "Ownership marker content does not match.");
        }

        if (existed && Directory.EnumerateFileSystemEntries(Layout.RootPath).Any())
        {
            throw new ApplicationDataOwnershipException(
                "Existing application data root has no valid ownership marker.");
        }

        await WriteNewMarkerAsync(
            Layout.OwnershipMarkerPath,
            ApplicationDataLayout.OwnershipMarkerContent,
            cancellationToken).ConfigureAwait(false);
        await VerifyMarkerAsync(
            Layout.OwnershipMarkerPath,
            ApplicationDataLayout.OwnershipMarkerContent,
            applicationRoot.FinalPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates (or verifies) the per-target browser user-data directory for
    /// the embedded remote UI host and returns its path. The root ownership
    /// is re-verified and the created directories are inspected no-follow so
    /// a planted reparse point cannot divert the location.
    /// </summary>
    public async ValueTask<string> PrepareTargetDataAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!TryInspectDirectoryNoFollow(Layout.TargetsRoot, out var targetsRoot))
        {
            Directory.CreateDirectory(Layout.TargetsRoot);
            targetsRoot = InspectRequiredDirectoryNoFollow(Layout.TargetsRoot);
        }

        EnsureImmediateFinalChild(
            targetsRoot.FinalPath,
            applicationRootFinalPath,
            "targets");

        var udfPath = Layout.GetUdfPath(targetId);
        if (!TryInspectDirectoryNoFollow(udfPath, out var udfInspection))
        {
            Directory.CreateDirectory(udfPath);
            udfInspection = InspectRequiredDirectoryNoFollow(udfPath);
        }

        EnsureImmediateFinalChild(
            udfInspection.FinalPath,
            targetsRoot.FinalPath,
            targetId.ToString("N") + Path.DirectorySeparatorChar + "udf");

        return udfPath;
    }

    public async ValueTask WriteDocumentSnapshotAsync(
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.IsEmpty || snapshot.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The hub document snapshot size is invalid.");
        }

        await VerifyApplicationOwnershipAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            temporaryPath = Path.Combine(
                Layout.RootPath,
                $"pairing-hub.{Guid.NewGuid():N}.tmp");
            await WriteThroughAsync(temporaryPath, snapshot, cancellationToken).ConfigureAwait(false);

            if (File.Exists(Layout.HubDocumentPath))
            {
                File.Replace(
                    temporaryPath,
                    Layout.HubDocumentPath,
                    Layout.HubDocumentBackupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, Layout.HubDocumentPath);
            }

            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            _writeGate.Release();
        }
    }

    public async ValueTask<DocumentSnapshots> ReadDocumentSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        var rootFinalPath = await VerifyApplicationOwnershipAsync(cancellationToken).ConfigureAwait(false);
        var primary = await ReadBoundedRegularFileIfPresentAsync(Layout.HubDocumentPath, rootFinalPath, cancellationToken)
            .ConfigureAwait(false);
        var backup = await ReadBoundedRegularFileIfPresentAsync(Layout.HubDocumentBackupPath, rootFinalPath, cancellationToken)
            .ConfigureAwait(false);
        return new DocumentSnapshots(primary, backup);
    }

    private async ValueTask<string> VerifyApplicationOwnershipAsync(CancellationToken cancellationToken)
    {
        if (!TryInspectDirectoryNoFollow(Layout.RootPath, out var applicationRoot))
        {
            throw new ApplicationDataOwnershipException(
                "Application data root does not exist.");
        }

        await VerifyMarkerAsync(
            Layout.OwnershipMarkerPath,
            ApplicationDataLayout.OwnershipMarkerContent,
            applicationRoot.FinalPath,
            cancellationToken).ConfigureAwait(false);
        return applicationRoot.FinalPath;
    }

    private static async ValueTask VerifyMarkerAsync(
        string markerPath,
        string expectedContent,
        string expectedParentFinalPath,
        CancellationToken cancellationToken)
    {
        if (!TryInspectRegularFileNoFollow(markerPath, out var markerInspection))
        {
            throw new ApplicationDataOwnershipException(
                "Required ownership marker is missing.");
        }

        EnsureImmediateFinalChild(
            markerInspection.FinalPath,
            expectedParentFinalPath,
            Path.GetFileName(markerPath));
        var actual = await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedContent, StringComparison.Ordinal))
        {
            throw new ApplicationDataOwnershipException(
                "Ownership marker content does not match.");
        }
    }

    private static async ValueTask WriteNewMarkerAsync(
        string markerPath,
        string content,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask ReplaceMarkerContentAsync(
        string markerPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = markerPath + $".{Guid.NewGuid():N}.tmp";
        await WriteNewMarkerAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
        File.Replace(temporaryPath, markerPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }

    private static async ValueTask WriteThroughAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask<byte[]?> ReadBoundedRegularFileIfPresentAsync(
        string path,
        string expectedParentFinalPath,
        CancellationToken cancellationToken)
    {
        if (!TryInspectRegularFileNoFollow(path, out var inspection))
        {
            return null;
        }

        EnsureImmediateFinalChild(
            inspection.FinalPath,
            expectedParentFinalPath,
            Path.GetFileName(path));
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > MaximumDocumentBytes)
        {
            return new byte[MaximumDocumentBytes + 1];
        }

        var content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            return new byte[MaximumDocumentBytes + 1];
        }

        return content;
    }

    private static void EnsureImmediateFinalChild(
        string childFinalPath,
        string parentFinalPath,
        string expectedName)
    {
        var expectedFinalPath = Path.Combine(parentFinalPath, expectedName);
        EnsureSameFinalPath(childFinalPath, expectedFinalPath);
    }

    private static void EnsureSameFinalPath(
        string actualFinalPath,
        string expectedFinalPath)
    {
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(actualFinalPath),
                Path.TrimEndingDirectorySeparator(expectedFinalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationDataOwnershipException(
                "Application data final path escapes its verified parent.");
        }
    }

    private static InspectedPath InspectRequiredDirectoryNoFollow(string path)
    {
        if (!TryInspectDirectoryNoFollow(path, out var inspection))
        {
            throw new ApplicationDataOwnershipException(
                "Required application data directory is missing.");
        }

        return inspection;
    }

    private static bool TryInspectDirectoryNoFollow(
        string path,
        out InspectedPath inspection)
    {
        if (!TryInspectPathNoFollow(path, out inspection))
        {
            return false;
        }

        if (!inspection.IsDirectory)
        {
            throw new ApplicationDataOwnershipException(
                "Expected application data path is not a directory.");
        }

        return true;
    }

    private static bool TryInspectRegularFileNoFollow(
        string path,
        out InspectedPath inspection)
    {
        if (!TryInspectPathNoFollow(path, out inspection))
        {
            return false;
        }

        if (inspection.IsDirectory)
        {
            throw new ApplicationDataOwnershipException(
                "Expected application data path is not a regular file.");
        }

        return true;
    }

    private static bool TryInspectPathNoFollow(
        string path,
        out InspectedPath inspection)
    {
        using var handle = CreateFileForPathInspection(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
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

            throw new ApplicationDataOwnershipException(
                "Application data path could not be inspected safely.",
                new Win32Exception(error));
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var attributeInformation,
                checked((uint)Marshal.SizeOf<FileAttributeTagInformation>())))
        {
            throw new ApplicationDataOwnershipException(
                "Application data path attributes could not be inspected safely.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var attributes = (FileAttributes)attributeInformation.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ApplicationDataOwnershipException(
                "Application data operations do not follow reparse points.");
        }

        inspection = new InspectedPath(
            GetNormalizedFinalPath(handle),
            (attributes & FileAttributes.Directory) != 0);
        return true;
    }

    private static unsafe string GetNormalizedFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[InitialFinalPathCapacity];
        while (true)
        {
            uint length;
            fixed (char* bufferPointer = buffer)
            {
                length = GetFinalPathNameByHandle(
                    handle,
                    bufferPointer,
                    checked((uint)buffer.Length),
                    flags: 0);
            }

            if (length == 0)
            {
                throw new ApplicationDataOwnershipException(
                    "Application data final path could not be resolved safely.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < buffer.Length)
            {
                return NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
            }

            buffer = new char[checked((int)length + 1)];
        }
    }

    private static string NormalizeFinalPath(string path)
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

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileForPathInspection(
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

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char* filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileAttributeTagInformation(
        uint FileAttributes,
        uint ReparseTag);

    private readonly record struct InspectedPath(
        string FinalPath,
        bool IsDirectory);
}

public sealed record DocumentSnapshots(byte[]? Primary, byte[]? Backup);

public sealed class ApplicationDataOwnershipException : InvalidOperationException
{
    public ApplicationDataOwnershipException()
    {
    }

    public ApplicationDataOwnershipException(string? message)
        : base(message)
    {
    }

    public ApplicationDataOwnershipException(
        string? message,
        Exception? innerException)
        : base(message, innerException)
    {
    }
}