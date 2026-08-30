using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Platform.Windows;

public sealed class ApplicationDataLayout
{
    public const string OwnershipMarkerName = ".dsh-windows-launcher-owner";
    public const string OwnershipMarkerContent = "DshWindowsLauncher:v1";
    public const string TargetOwnershipMarkerName = ".dsh-target-owner";

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
        CatalogPath = Path.Combine(RootPath, "targets.json");
        CatalogBackupPath = Path.Combine(RootPath, "targets.json.bak");
    }

    public string RootPath { get; }

    public string OwnershipMarkerPath { get; }

    public string CatalogPath { get; }

    public string CatalogBackupPath { get; }

    public string TargetsRoot => Path.Combine(RootPath, "targets");

    public string RegistryWatermarkPath =>
        Path.Combine(RootPath, "compatibility-registry-watermark.json");

    public string RegistryWatermarkBackupPath =>
        Path.Combine(RootPath, "compatibility-registry-watermark.json.bak");

    public string GetTargetRoot(Guid targetId)
    {
        ValidateTargetId(targetId);
        return Path.Combine(TargetsRoot, targetId.ToString("N"));
    }

    public string GetUdfPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), "udf");

    public string GetTargetOwnershipMarkerPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), TargetOwnershipMarkerName);

    public string GetExternalCapabilityConfirmationPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), "external-capability-confirmation.json");

    public string GetExternalCapabilityConfirmationBackupPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), "external-capability-confirmation.json.bak");

    public string GetCompatibilityDiagnosticPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), "compatibility-diagnostic.json");

    public string GetCompatibilityDiagnosticBackupPath(Guid targetId) =>
        Path.Combine(GetTargetRoot(targetId), "compatibility-diagnostic.json.bak");

    public static string GetTargetOwnershipMarkerContent(Guid targetId)
    {
        ValidateTargetId(targetId);
        return $"DshWindowsLauncher:target:{targetId:N}:v1";
    }

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

public sealed partial class ApplicationDataStore : IDisposable
{
    private const int MaximumCatalogBytes = 1024 * 1024;
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
    private readonly SemaphoreSlim _compatibilityStateGate = new(1, 1);

    public ApplicationDataStore(ApplicationDataLayout layout)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public ApplicationDataLayout Layout { get; }

    internal SemaphoreSlim CompatibilityStateGate => _compatibilityStateGate;

    public void Dispose()
    {
        _compatibilityStateGate.Dispose();
        _writeGate.Dispose();
    }

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var existed = TryInspectDirectoryNoFollow(
            Layout.RootPath,
            out var applicationRoot);
        if (!existed)
        {
            Directory.CreateDirectory(Layout.RootPath);
            applicationRoot = InspectRequiredDirectoryNoFollow(Layout.RootPath);
        }

        if (TryInspectPathNoFollow(Layout.OwnershipMarkerPath, out _))
        {
            await VerifyMarkerAsync(
                Layout.OwnershipMarkerPath,
                ApplicationDataLayout.OwnershipMarkerContent,
                applicationRoot.FinalPath,
                cancellationToken).ConfigureAwait(false);
            return;
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

    public async ValueTask PrepareTargetDataAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
            cancellationToken)
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

        var targetRoot = Layout.GetTargetRoot(targetId);
        var existed = TryInspectDirectoryNoFollow(
            targetRoot,
            out var targetRootInspection);
        if (!existed)
        {
            Directory.CreateDirectory(targetRoot);
            targetRootInspection = InspectRequiredDirectoryNoFollow(targetRoot);
        }

        EnsureImmediateFinalChild(
            targetRootInspection.FinalPath,
            targetsRoot.FinalPath,
            targetId.ToString("N"));

        var markerPath = Layout.GetTargetOwnershipMarkerPath(targetId);
        var expectedMarker = ApplicationDataLayout.GetTargetOwnershipMarkerContent(
            targetId);

        if (TryInspectPathNoFollow(markerPath, out _))
        {
            await VerifyMarkerAsync(
                markerPath,
                expectedMarker,
                targetRootInspection.FinalPath,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (existed && Directory.EnumerateFileSystemEntries(targetRoot).Any())
            {
                throw new ApplicationDataOwnershipException(
                    "Existing target data has no valid ownership marker.");
            }

            await WriteNewMarkerAsync(
                markerPath,
                expectedMarker,
                cancellationToken).ConfigureAwait(false);
            await VerifyMarkerAsync(
                markerPath,
                expectedMarker,
                targetRootInspection.FinalPath,
                cancellationToken).ConfigureAwait(false);
        }

        var udfPath = Layout.GetUdfPath(targetId);
        if (!TryInspectDirectoryNoFollow(udfPath, out var udfInspection))
        {
            Directory.CreateDirectory(udfPath);
            udfInspection = InspectRequiredDirectoryNoFollow(udfPath);
        }

        EnsureImmediateFinalChild(
            udfInspection.FinalPath,
            targetRootInspection.FinalPath,
            "udf");
    }

    public async ValueTask WriteCatalogSnapshotAsync(
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken = default)
    {
        await VerifyApplicationOwnershipAsync(cancellationToken)
            .ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            temporaryPath = Path.Combine(
                Layout.RootPath,
                $"targets.{Guid.NewGuid():N}.tmp");
            await WriteThroughAsync(
                temporaryPath,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            if (File.Exists(Layout.CatalogPath))
            {
                File.Replace(
                    temporaryPath,
                    Layout.CatalogPath,
                    Layout.CatalogBackupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, Layout.CatalogPath);
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

    public async ValueTask<CatalogSnapshots> ReadCatalogSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        await VerifyApplicationOwnershipAsync(cancellationToken)
            .ConfigureAwait(false);

        var primary = await ReadBoundedRegularFileIfPresentAsync(
            Layout.CatalogPath,
            cancellationToken).ConfigureAwait(false);
        var backup = await ReadBoundedRegularFileIfPresentAsync(
            Layout.CatalogBackupPath,
            cancellationToken).ConfigureAwait(false);
        return new CatalogSnapshots(primary, backup);
    }

    internal async ValueTask<ApplicationStateSnapshots> ReadApplicationStateSnapshotsAsync(
        string primaryFileName,
        string backupFileName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateSnapshotArguments(primaryFileName, backupFileName, maximumBytes);
        var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
            cancellationToken).ConfigureAwait(false);
        return await ReadStateSnapshotsAsync(
            Layout.RootPath,
            applicationRootFinalPath,
            primaryFileName,
            backupFileName,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ApplicationStateSnapshots> ReadTargetStateSnapshotsAsync(
        Guid targetId,
        string primaryFileName,
        string backupFileName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateSnapshotArguments(primaryFileName, backupFileName, maximumBytes);
        var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
            cancellationToken).ConfigureAwait(false);
        if (!TryInspectTargetRoot(
                targetId,
                applicationRootFinalPath,
                out var targetRootInspection))
        {
            return new ApplicationStateSnapshots(null, null);
        }

        await VerifyMarkerAsync(
            Layout.GetTargetOwnershipMarkerPath(targetId),
            ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
            targetRootInspection.FinalPath,
            cancellationToken).ConfigureAwait(false);
        return await ReadStateSnapshotsAsync(
            Layout.GetTargetRoot(targetId),
            targetRootInspection.FinalPath,
            primaryFileName,
            backupFileName,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask WriteApplicationStateSnapshotAsync(
        string primaryFileName,
        string backupFileName,
        ReadOnlyMemory<byte> snapshot,
        int maximumBytes,
        bool preserveExistingBackup,
        CancellationToken cancellationToken)
    {
        ValidateSnapshotArguments(primaryFileName, backupFileName, maximumBytes);
        ValidateSnapshotContent(snapshot, maximumBytes);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
                cancellationToken).ConfigureAwait(false);
            await WriteStateSnapshotCoreAsync(
                Layout.RootPath,
                applicationRootFinalPath,
                primaryFileName,
                backupFileName,
                snapshot,
                preserveExistingBackup,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async ValueTask WriteTargetStateSnapshotAsync(
        Guid targetId,
        string primaryFileName,
        string backupFileName,
        ReadOnlyMemory<byte> snapshot,
        int maximumBytes,
        bool preserveExistingBackup,
        CancellationToken cancellationToken)
    {
        ValidateSnapshotArguments(primaryFileName, backupFileName, maximumBytes);
        ValidateSnapshotContent(snapshot, maximumBytes);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareTargetDataAsync(targetId, cancellationToken).ConfigureAwait(false);
            var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
                cancellationToken).ConfigureAwait(false);
            if (!TryInspectTargetRoot(
                    targetId,
                    applicationRootFinalPath,
                    out var targetRootInspection))
            {
                throw new ApplicationDataOwnershipException(
                    "Target data root disappeared before the state write.");
            }

            await VerifyMarkerAsync(
                Layout.GetTargetOwnershipMarkerPath(targetId),
                ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
                targetRootInspection.FinalPath,
                cancellationToken).ConfigureAwait(false);
            await WriteStateSnapshotCoreAsync(
                Layout.GetTargetRoot(targetId),
                targetRootInspection.FinalPath,
                primaryFileName,
                backupFileName,
                snapshot,
                preserveExistingBackup,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async ValueTask DeleteTargetStateSnapshotsAsync(
        Guid targetId,
        string primaryFileName,
        string backupFileName,
        CancellationToken cancellationToken)
    {
        ValidateSnapshotFilePair(primaryFileName, backupFileName);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
                cancellationToken).ConfigureAwait(false);
            if (!TryInspectTargetRoot(
                    targetId,
                    applicationRootFinalPath,
                    out var targetRootInspection))
            {
                return;
            }

            await VerifyMarkerAsync(
                Layout.GetTargetOwnershipMarkerPath(targetId),
                ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
                targetRootInspection.FinalPath,
                cancellationToken).ConfigureAwait(false);
            DeleteStateSnapshotsCore(
                Layout.GetTargetRoot(targetId),
                targetRootInspection.FinalPath,
                primaryFileName,
                backupFileName);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DeleteTargetDataAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        await _compatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
                    cancellationToken)
                    .ConfigureAwait(false);

                if (!TryInspectTargetRoot(
                        targetId,
                        applicationRootFinalPath,
                        out var targetRootInspection))
                {
                    return;
                }

                await VerifyMarkerAsync(
                    Layout.GetTargetOwnershipMarkerPath(targetId),
                    ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
                    targetRootInspection.FinalPath,
                    cancellationToken).ConfigureAwait(false);

                DeleteDirectoryWithoutFollowingReparsePoints(
                    Layout.GetTargetRoot(targetId),
                    targetRootInspection.FinalPath,
                    cancellationToken);

                if (Directory.Exists(Layout.GetTargetRoot(targetId)))
                {
                    throw new IOException("Target data could not be completely removed.");
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _compatibilityStateGate.Release();
        }
    }

    public async ValueTask DeleteTargetUdfAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
            cancellationToken)
            .ConfigureAwait(false);

        if (!TryInspectTargetRoot(
                targetId,
                applicationRootFinalPath,
                out var targetRootInspection))
        {
            return;
        }

        await VerifyMarkerAsync(
            Layout.GetTargetOwnershipMarkerPath(targetId),
            ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
            targetRootInspection.FinalPath,
            cancellationToken).ConfigureAwait(false);

        if (!TryInspectUdf(
                targetId,
                targetRootInspection.FinalPath,
                out var udfInspection))
        {
            return;
        }

        var udfPath = Layout.GetUdfPath(targetId);
        DeleteDirectoryWithoutFollowingReparsePoints(
            udfPath,
            udfInspection.FinalPath,
            cancellationToken);
        if (Directory.Exists(udfPath))
        {
            throw new IOException("Target browser data could not be completely removed.");
        }
    }

    public async ValueTask<bool> TargetUdfExistsAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var applicationRootFinalPath = await VerifyApplicationOwnershipAsync(
            cancellationToken)
            .ConfigureAwait(false);
        if (!TryInspectTargetRoot(
                targetId,
                applicationRootFinalPath,
                out var targetRootInspection))
        {
            return false;
        }

        await VerifyMarkerAsync(
            Layout.GetTargetOwnershipMarkerPath(targetId),
            ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
            targetRootInspection.FinalPath,
            cancellationToken).ConfigureAwait(false);

        return TryInspectUdf(
            targetId,
            targetRootInspection.FinalPath,
            out _);
    }

    private async ValueTask<string> VerifyApplicationOwnershipAsync(
        CancellationToken cancellationToken)
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
        var actual = await File.ReadAllTextAsync(
            markerPath,
            cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<ApplicationStateSnapshots> ReadStateSnapshotsAsync(
        string directory,
        string directoryFinalPath,
        string primaryFileName,
        string backupFileName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var primary = await ReadBoundedStateFileIfPresentAsync(
            Path.Combine(directory, primaryFileName),
            directoryFinalPath,
            primaryFileName,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        var backup = await ReadBoundedStateFileIfPresentAsync(
            Path.Combine(directory, backupFileName),
            directoryFinalPath,
            backupFileName,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        return new ApplicationStateSnapshots(primary, backup);
    }

    private static async ValueTask<byte[]?> ReadBoundedStateFileIfPresentAsync(
        string path,
        string expectedParentFinalPath,
        string expectedName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!TryInspectRegularFileNoFollow(path, out var inspection))
        {
            return null;
        }

        EnsureImmediateFinalChild(
            inspection.FinalPath,
            expectedParentFinalPath,
            expectedName);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            return new byte[maximumBytes + 1];
        }

        var content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            return new byte[maximumBytes + 1];
        }

        return content;
    }

    private static async ValueTask WriteStateSnapshotCoreAsync(
        string directory,
        string directoryFinalPath,
        string primaryFileName,
        string backupFileName,
        ReadOnlyMemory<byte> snapshot,
        bool preserveExistingBackup,
        CancellationToken cancellationToken)
    {
        var primaryPath = Path.Combine(directory, primaryFileName);
        var backupPath = Path.Combine(directory, backupFileName);
        if (TryInspectRegularFileNoFollow(primaryPath, out var primaryInspection))
        {
            EnsureImmediateFinalChild(
                primaryInspection.FinalPath,
                directoryFinalPath,
                primaryFileName);
        }

        if (TryInspectRegularFileNoFollow(backupPath, out var backupInspection))
        {
            EnsureImmediateFinalChild(
                backupInspection.FinalPath,
                directoryFinalPath,
                backupFileName);
        }

        var temporaryFileName = $".{primaryFileName}.{Guid.NewGuid():N}.tmp";
        var temporaryPath = Path.Combine(directory, temporaryFileName);
        try
        {
            await WriteThroughAsync(
                temporaryPath,
                snapshot,
                cancellationToken).ConfigureAwait(false);
            var temporaryInspection = InspectRequiredRegularFileNoFollow(temporaryPath);
            EnsureImmediateFinalChild(
                temporaryInspection.FinalPath,
                directoryFinalPath,
                temporaryFileName);

            if (File.Exists(primaryPath))
            {
                File.Replace(
                    temporaryPath,
                    primaryPath,
                    preserveExistingBackup ? null : backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, primaryPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                var temporaryInspection = InspectRequiredRegularFileNoFollow(temporaryPath);
                EnsureImmediateFinalChild(
                    temporaryInspection.FinalPath,
                    directoryFinalPath,
                    temporaryFileName);
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteStateSnapshotsCore(
        string directory,
        string directoryFinalPath,
        string primaryFileName,
        string backupFileName)
    {
        var primaryPath = Path.Combine(directory, primaryFileName);
        var backupPath = Path.Combine(directory, backupFileName);
        var primaryExists = TryInspectRegularFileNoFollow(
            primaryPath,
            out var primaryInspection);
        if (primaryExists)
        {
            EnsureImmediateFinalChild(
                primaryInspection.FinalPath,
                directoryFinalPath,
                primaryFileName);
        }

        var backupExists = TryInspectRegularFileNoFollow(
            backupPath,
            out var backupInspection);
        if (backupExists)
        {
            EnsureImmediateFinalChild(
                backupInspection.FinalPath,
                directoryFinalPath,
                backupFileName);
        }

        // Delete the older decision first so an interrupted revocation never
        // resurrects it as an automatic backup recovery.
        if (backupExists)
        {
            File.Delete(backupPath);
        }

        if (primaryExists)
        {
            File.Delete(primaryPath);
        }

        if (TryInspectPathNoFollow(primaryPath, out _) ||
            TryInspectPathNoFollow(backupPath, out _))
        {
            throw new IOException("Target state files could not be completely removed.");
        }
    }

    private static InspectedPath InspectRequiredRegularFileNoFollow(string path)
    {
        if (!TryInspectRegularFileNoFollow(path, out var inspection))
        {
            throw new ApplicationDataOwnershipException(
                "Required application data file is missing.");
        }

        return inspection;
    }

    private static void ValidateSnapshotArguments(
        string primaryFileName,
        string backupFileName,
        int maximumBytes)
    {
        ValidateSnapshotFilePair(primaryFileName, backupFileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
    }

    private static void ValidateSnapshotFilePair(
        string primaryFileName,
        string backupFileName)
    {
        ValidateSnapshotFileName(primaryFileName, nameof(primaryFileName));
        ValidateSnapshotFileName(backupFileName, nameof(backupFileName));
        if (string.Equals(primaryFileName, backupFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Primary and backup state files must be distinct.");
        }
    }

    private static void ValidateSnapshotFileName(string fileName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName, parameterName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName is "." or "..")
        {
            throw new ArgumentException(
                "State snapshot file name must be a leaf name.",
                parameterName);
        }
    }

    private static void ValidateSnapshotContent(
        ReadOnlyMemory<byte> snapshot,
        int maximumBytes)
    {
        if (snapshot.IsEmpty || snapshot.Length > maximumBytes)
        {
            throw new InvalidDataException("Application state snapshot size is invalid.");
        }
    }

    private static async ValueTask<byte[]?> ReadBoundedRegularFileIfPresentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureNotReparsePoint(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ApplicationDataOwnershipException(
                "Catalog snapshot is not a regular file.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumCatalogBytes)
        {
            return new byte[MaximumCatalogBytes + 1];
        }

        var content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            return new byte[MaximumCatalogBytes + 1];
        }

        return content;
    }

    private void EnsureContainedTargetPath(string targetRoot)
    {
        var relative = Path.GetRelativePath(Layout.TargetsRoot, targetRoot);
        if (relative == "." ||
            relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ApplicationDataOwnershipException(
                "Target data path escapes the application data root.");
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(
        string directory,
        string expectedFinalPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directoryInspection = InspectRequiredDirectoryNoFollow(directory);
        EnsureSameFinalPath(
            directoryInspection.FinalPath,
            expectedFinalPath);

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryInspectPathNoFollow(entry, out var entryInspection))
            {
                throw new IOException(
                    "Application data changed while it was being removed.");
            }

            EnsureImmediateFinalChild(
                entryInspection.FinalPath,
                directoryInspection.FinalPath,
                Path.GetFileName(entry));
            if (entryInspection.IsDirectory)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(
                    entry,
                    entryInspection.FinalPath,
                    cancellationToken);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    private bool TryInspectTargetRoot(
        Guid targetId,
        string applicationRootFinalPath,
        out InspectedPath targetRootInspection)
    {
        var targetRoot = Layout.GetTargetRoot(targetId);
        EnsureContainedTargetPath(targetRoot);
        if (!TryInspectDirectoryNoFollow(Layout.TargetsRoot, out var targetsRoot))
        {
            targetRootInspection = default;
            return false;
        }

        EnsureImmediateFinalChild(
            targetsRoot.FinalPath,
            applicationRootFinalPath,
            "targets");
        if (!TryInspectDirectoryNoFollow(targetRoot, out targetRootInspection))
        {
            return false;
        }

        EnsureImmediateFinalChild(
            targetRootInspection.FinalPath,
            targetsRoot.FinalPath,
            targetId.ToString("N"));
        return true;
    }

    private bool TryInspectUdf(
        Guid targetId,
        string targetRootFinalPath,
        out InspectedPath udfInspection)
    {
        if (!TryInspectDirectoryNoFollow(
                Layout.GetUdfPath(targetId),
                out udfInspection))
        {
            return false;
        }

        EnsureImmediateFinalChild(
            udfInspection.FinalPath,
            targetRootFinalPath,
            "udf");
        return true;
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

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ApplicationDataOwnershipException(
                "Application data operations do not follow reparse points.");
        }
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

public sealed record CatalogSnapshots(byte[]? Primary, byte[]? Backup);

internal sealed record ApplicationStateSnapshots(byte[]? Primary, byte[]? Backup);

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
