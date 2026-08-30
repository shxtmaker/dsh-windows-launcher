using System.IO;

namespace DshLauncher.Platform.Windows;

public static class InstallPathPolicy
{
    public static InstallPathResult Evaluate(InstallPathFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var qualification = !facts.IsFullyQualified
            ? InstallPathQualification.NotFullyQualified
            : facts.IsDevicePath
                ? InstallPathQualification.DevicePath
                : facts.IsUnc
                    ? InstallPathQualification.UncPath
                    : !facts.FinalPathResolved
                        ? InstallPathQualification.FinalPathUnresolved
                        : facts.VolumeKind != InstallVolumeKind.Fixed
                            ? InstallPathQualification.NotLocalFixedVolume
                            : facts.ContainsReparsePoint
                                ? InstallPathQualification.ReparsePoint
                                : facts.DirectoryExists && !facts.DirectoryIsEmpty
                                    ? InstallPathQualification.DirectoryNotEmpty
                                    : !facts.IsWritable
                                        ? InstallPathQualification.NotWritable
                                        : facts.AvailableBytes < facts.RequiredBytes
                                            ? InstallPathQualification.InsufficientSpace
                                            : InstallPathQualification.Eligible;

        return new InstallPathResult(qualification);
    }
}

public sealed record InstallPathFacts(
    bool IsFullyQualified,
    bool IsUnc,
    bool IsDevicePath,
    bool FinalPathResolved,
    InstallVolumeKind VolumeKind,
    bool ContainsReparsePoint,
    bool DirectoryExists,
    bool DirectoryIsEmpty,
    bool IsWritable,
    long AvailableBytes,
    long RequiredBytes);

public sealed record InstallPathResult(InstallPathQualification Qualification)
{
    public bool IsEligible => Qualification == InstallPathQualification.Eligible;
}

public enum InstallPathQualification
{
    Eligible,
    NotFullyQualified,
    DevicePath,
    UncPath,
    FinalPathUnresolved,
    NotLocalFixedVolume,
    ReparsePoint,
    DirectoryNotEmpty,
    NotWritable,
    InsufficientSpace,
    InspectionFailed
}

public enum InstallVolumeKind
{
    Fixed,
    Network,
    Removable,
    CdRom,
    Ram,
    Unknown
}

public static class WindowsInstallPathQualifier
{
    public static InstallPathResult Qualify(string path, long requiredBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);

        try
        {
            var fullyQualified = Path.IsPathFullyQualified(path);
            var devicePath = IsDevicePath(path);
            var unc = path.StartsWith("\\\\", StringComparison.Ordinal);
            if (!fullyQualified || devicePath || unc)
            {
                return InstallPathPolicy.Evaluate(new InstallPathFacts(
                    fullyQualified,
                    unc,
                    devicePath,
                    FinalPathResolved: false,
                    InstallVolumeKind.Unknown,
                    ContainsReparsePoint: false,
                    DirectoryExists: false,
                    DirectoryIsEmpty: false,
                    IsWritable: false,
                    AvailableBytes: 0,
                    requiredBytes));
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return new InstallPathResult(
                    InstallPathQualification.FinalPathUnresolved);
            }

            var drive = new DriveInfo(root);
            var existingParent = FindClosestExistingDirectory(fullPath);
            var directoryExists = Directory.Exists(fullPath);
            var directoryIsEmpty = directoryExists &&
                                   !Directory.EnumerateFileSystemEntries(fullPath).Any();
            var reparse = existingParent is null ||
                          ContainsReparsePoint(existingParent, root);
            var writable = existingParent is not null &&
                           CanCreateTemporaryFile(
                               directoryExists ? fullPath : existingParent);
            var availableBytes = drive.IsReady ? drive.AvailableFreeSpace : 0;

            return InstallPathPolicy.Evaluate(new InstallPathFacts(
                IsFullyQualified: true,
                IsUnc: false,
                IsDevicePath: false,
                FinalPathResolved: existingParent is not null,
                VolumeKind: MapDriveType(drive.DriveType),
                ContainsReparsePoint: reparse,
                DirectoryExists: directoryExists,
                DirectoryIsEmpty: directoryIsEmpty,
                IsWritable: writable,
                AvailableBytes: availableBytes,
                RequiredBytes: requiredBytes));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return new InstallPathResult(
                InstallPathQualification.InspectionFailed);
        }
    }

    private static bool IsDevicePath(string path)
    {
        return path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
               path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
               path.StartsWith("\\??\\", StringComparison.Ordinal);
    }

    private static string? FindClosestExistingDirectory(string path)
    {
        var current = path;
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }

        return current;
    }

    private static bool ContainsReparsePoint(
        string existingPath,
        string root)
    {
        var current = existingPath;
        while (!string.IsNullOrEmpty(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(
                Path.TrimEndingDirectorySeparator(current),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return false;
    }

    private static bool CanCreateTemporaryFile(string directory)
    {
        var path = Path.Combine(
            directory,
            $".dsh-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static InstallVolumeKind MapDriveType(DriveType driveType)
    {
        return driveType switch
        {
            DriveType.Fixed => InstallVolumeKind.Fixed,
            DriveType.Network => InstallVolumeKind.Network,
            DriveType.Removable => InstallVolumeKind.Removable,
            DriveType.CDRom => InstallVolumeKind.CdRom,
            DriveType.Ram => InstallVolumeKind.Ram,
            _ => InstallVolumeKind.Unknown,
        };
    }
}
