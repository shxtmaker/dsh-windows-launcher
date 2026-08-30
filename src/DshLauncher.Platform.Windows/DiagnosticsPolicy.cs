using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Platform.Windows;

public static partial class DiagnosticRedactor
{
    private const int MaximumInputCharacters = 2048;
    private const int MaximumOutputCharacters = 512;

    public static string RedactExternalError(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bounded = value.Length <= MaximumInputCharacters
            ? value
            : value[..MaximumInputCharacters];
        var redacted = UriPattern().Replace(bounded, "[REDACTED]");
        redacted = BearerPattern().Replace(redacted, "Bearer [REDACTED]");
        redacted = SensitiveAssignmentPattern().Replace(
            redacted,
            "$1=[REDACTED]");

        return redacted.Length <= MaximumOutputCharacters
            ? redacted
            : redacted[..MaximumOutputCharacters];
    }

    [GeneratedRegex(
        @"\bhttps?://[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriPattern();

    [GeneratedRegex(
        @"\bBearer\s+[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(
        @"\b(token|cookie|authorization)\s*[:=]\s*[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();
}

public static class DiagnosticExportWhitelist
{
    private static readonly HashSet<string> FixedEntries = new(
        StringComparer.Ordinal)
    {
        "product.json",
        "os.json",
        "runtime.json",
        "dependency-baseline.json",
        "target-status.json",
        "logs/launcher.log",
    };

    public static bool IsAllowed(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) ||
            entryName.Contains('\\', StringComparison.Ordinal) ||
            entryName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (FixedEntries.Contains(entryName))
        {
            return true;
        }

        const string prefix = "logs/launcher.";
        const string suffix = ".log";
        if (!entryName.StartsWith(prefix, StringComparison.Ordinal) ||
            !entryName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var number = entryName.AsSpan(
            prefix.Length,
            entryName.Length - prefix.Length - suffix.Length);
        return int.TryParse(
                   number,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var index) &&
               index is >= 1 and <= 6;
    }
}

public sealed record DiagnosticRecord(
    DateTimeOffset TimestampUtc,
    DiagnosticEventCode EventCode,
    int? StatusCode,
    string? Classification,
    string? NormalizedPrivateEndpoint)
{
    public static DiagnosticRecord Create(
        DateTimeOffset timestampUtc,
        DiagnosticEventCode eventCode,
        int? statusCode,
        string? classification,
        string? normalizedPrivateEndpoint,
        bool includePrivateEndpoint)
    {
        if (timestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Diagnostic timestamps must be UTC.",
                nameof(timestampUtc));
        }

        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (classification is { Length: > 80 } ||
            (classification is not null && !IsSafeCode(classification)))
        {
            throw new ArgumentException(
                "Diagnostic classification must be a bounded code.",
                nameof(classification));
        }

        var endpoint = includePrivateEndpoint
            ? ValidatePrivateEndpoint(normalizedPrivateEndpoint)
            : null;
        return new DiagnosticRecord(
            timestampUtc,
            eventCode,
            statusCode,
            classification,
            endpoint);
    }

    private static bool IsSafeCode(string value)
    {
        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');
    }

    private static string? ValidatePrivateEndpoint(string? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 ||
            !ushort.TryParse(
                endpoint.AsSpan(separator + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var port) ||
            port == 0 ||
            !IPAddress.TryParse(endpoint.AsSpan(0, separator), out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !IsRfc1918(address))
        {
            throw new ArgumentException(
                "Diagnostic endpoint must be a normalized RFC1918 authority.",
                nameof(endpoint));
        }

        return $"{address}:{port}";
    }

    private static bool IsRfc1918(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}

public enum DiagnosticEventCode
{
    ApplicationStarted,
    TargetProbeClassified,
    PairingStateChanged,
    TargetContentStateChanged,
    DataRecoveryStateChanged,
    RuntimeStateChanged
}

public sealed class RollingDiagnosticLogPolicy
{
    public const int MaximumFiles = 7;
    public const int MaximumFileBytes = 5 * 1024 * 1024;

    public RollingDiagnosticLogPolicy(
        int maxFiles = MaximumFiles,
        int maxFileBytes = MaximumFileBytes)
    {
        if (maxFiles is < 1 or > MaximumFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        if (maxFileBytes is < 128 or > MaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        MaxFiles = maxFiles;
        MaxFileBytes = maxFileBytes;
    }

    public int MaxFiles { get; }

    public int MaxFileBytes { get; }
}

public sealed class RollingDiagnosticLog : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ApplicationDataLayout _layout;
    private readonly RollingDiagnosticLogPolicy _policy;
    private int _disposed;

    public RollingDiagnosticLog(
        ApplicationDataLayout layout,
        RollingDiagnosticLogPolicy? policy = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _policy = policy ?? new RollingDiagnosticLogPolicy();
    }

    public async ValueTask WriteAsync(
        DiagnosticRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            record,
            SerializerOptions);
        var line = new byte[serialized.Length + 1];
        serialized.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        if (line.Length > _policy.MaxFileBytes)
        {
            throw new InvalidOperationException(
                "A diagnostic record exceeds the per-file limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var directory = await OwnedDiagnosticDirectory.OpenAsync(
                _layout,
                createLogsDirectory: true,
                cancellationToken).ConfigureAwait(false);
            var files = new SafeFileHandle?[_policy.MaxFiles];
            try
            {
                for (var index = 0; index < files.Length; index++)
                {
                    directory.TryOpenLogFile(
                        index,
                        writable: true,
                        out files[index]);
                }

                if (files[0] is { } current &&
                    RandomAccess.GetLength(current) >
                    _policy.MaxFileBytes - line.Length)
                {
                    Rotate(directory, files);
                }

                files[0] ??= directory.CreateLogFile(0, writable: true);
                var offset = RandomAccess.GetLength(files[0]!);
                if (offset > _policy.MaxFileBytes - line.Length)
                {
                    throw new IOException(
                        "The diagnostic log changed while it was being written.");
                }

                await RandomAccess.WriteAsync(
                    files[0]!,
                    line,
                    offset,
                    cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(files[0]!);
            }
            finally
            {
                foreach (var file in files)
                {
                    file?.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<byte[]?> ReadForExportAsync(
        int index,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (index < 0 || index >= _policy.MaxFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var directory = await OwnedDiagnosticDirectory.OpenAsync(
                _layout,
                createLogsDirectory: false,
                cancellationToken).ConfigureAwait(false);
            if (!directory.HasLogsDirectory ||
                !directory.TryOpenLogFile(
                    index,
                    writable: false,
                    out var file) ||
                file is null)
            {
                return null;
            }

            using (file)
            {
                try
                {
                    return await ReadBoundedAsync(
                        file,
                        _policy.MaxFileBytes,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask<byte[]?> ReadBoundedAsync(
        SafeFileHandle file,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (RandomAccess.GetLength(file) > maximumBytes)
        {
            return null;
        }

        var bytes = GC.AllocateUninitializedArray<byte>(maximumBytes + 1);
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(
                file,
                bytes.AsMemory(total),
                total,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total <= maximumBytes
            ? bytes.AsSpan(0, total).ToArray()
            : null;
    }

    private static void Rotate(
        OwnedDiagnosticDirectory directory,
        SafeFileHandle?[] files)
    {
        var oldestIndex = files.Length - 1;
        if (files[oldestIndex] is { } oldest)
        {
            directory.DeleteLogFile(oldest);
            oldest.Dispose();
            files[oldestIndex] = null;
        }

        for (var index = files.Length - 2; index >= 0; index--)
        {
            if (files[index] is not { } source)
            {
                continue;
            }

            directory.RenameLogFile(source, index + 1);
            files[index + 1] = source;
            files[index] = null;
        }
    }
}

internal sealed partial class OwnedDiagnosticDirectory : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int InitialFinalPathCapacity = 512;

    private readonly SafeFileHandle _applicationRoot;
    private readonly SafeFileHandle _ownershipMarker;
    private readonly SafeFileHandle? _logsDirectory;
    private readonly string? _logsFinalPath;
    private int _disposed;

    private OwnedDiagnosticDirectory(
        SafeFileHandle applicationRoot,
        SafeFileHandle ownershipMarker,
        SafeFileHandle? logsDirectory,
        string? logsFinalPath)
    {
        _applicationRoot = applicationRoot;
        _ownershipMarker = ownershipMarker;
        _logsDirectory = logsDirectory;
        _logsFinalPath = logsFinalPath;
    }

    public bool HasLogsDirectory => _logsDirectory is not null;

    public static async ValueTask<OwnedDiagnosticDirectory> OpenAsync(
        ApplicationDataLayout layout,
        bool createLogsDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        SafeFileHandle? applicationRoot = null;
        SafeFileHandle? ownershipMarker = null;
        SafeFileHandle? logsDirectory = null;
        try
        {
            applicationRoot = OpenRequiredPathNoFollow(
                layout.RootPath,
                FileReadAttributes,
                FileShareRead | FileShareWrite,
                FileFlagBackupSemantics,
                expectedDirectory: true);
            var applicationRootFinalPath = GetNormalizedFinalPath(applicationRoot);

            ownershipMarker = OpenRequiredPathNoFollow(
                layout.OwnershipMarkerPath,
                GenericRead,
                FileShareRead,
                FileFlagSequentialScan | FileFlagOverlapped,
                expectedDirectory: false);
            EnsureImmediateFinalChild(
                GetNormalizedFinalPath(ownershipMarker),
                applicationRootFinalPath,
                ApplicationDataLayout.OwnershipMarkerName);
            await VerifyOwnershipMarkerAsync(
                ownershipMarker,
                cancellationToken).ConfigureAwait(false);

            var logsPath = Path.Combine(layout.RootPath, "logs");
            if (!TryOpenPathNoFollow(
                    logsPath,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite,
                    FileFlagBackupSemantics,
                    expectedDirectory: true,
                    out logsDirectory) &&
                createLogsDirectory)
            {
                Directory.CreateDirectory(logsPath);
                logsDirectory = OpenRequiredPathNoFollow(
                    logsPath,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite,
                    FileFlagBackupSemantics,
                    expectedDirectory: true);
            }

            string? logsFinalPath = null;
            if (logsDirectory is not null)
            {
                logsFinalPath = GetNormalizedFinalPath(logsDirectory);
                EnsureImmediateFinalChild(
                    logsFinalPath,
                    applicationRootFinalPath,
                    "logs");
            }

            return new OwnedDiagnosticDirectory(
                applicationRoot,
                ownershipMarker,
                logsDirectory,
                logsFinalPath);
        }
        catch
        {
            logsDirectory?.Dispose();
            ownershipMarker?.Dispose();
            applicationRoot?.Dispose();
            throw;
        }
    }

    public bool TryOpenLogFile(
        int index,
        bool writable,
        out SafeFileHandle? file)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_logsDirectory is null || _logsFinalPath is null)
        {
            file = null;
            return false;
        }

        var path = Path.Combine(_logsFinalPath, GetLogFileName(index));
        return TryOpenPathNoFollow(
            path,
            writable
                ? GenericRead | GenericWrite | DeleteAccess
                : GenericRead,
            FileShareRead,
            FileFlagSequentialScan |
            FileFlagOverlapped |
            (writable ? FileFlagWriteThrough : 0),
            expectedDirectory: false,
            out file);
    }

    public SafeFileHandle CreateLogFile(int index, bool writable)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_logsDirectory is null || _logsFinalPath is null)
        {
            throw new ApplicationDataOwnershipException(
                "The diagnostic log directory is unavailable.");
        }

        var path = Path.Combine(_logsFinalPath, GetLogFileName(index));
        var file = CreateFileForPathInspection(
            path,
            writable
                ? GenericRead | GenericWrite | DeleteAccess
                : GenericRead,
            FileShareRead,
            securityAttributes: 0,
            CreateNew,
            FileFlagOpenReparsePoint |
            FileFlagSequentialScan |
            FileFlagOverlapped |
            (writable ? FileFlagWriteThrough : 0),
            templateFile: 0);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new ApplicationDataOwnershipException(
                "The diagnostic log path changed while it was being created.",
                new Win32Exception(error));
        }

        try
        {
            ValidateOpenedPath(
                file,
                expectedDirectory: false,
                path,
                _logsFinalPath);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void DeleteLogFile(SafeFileHandle file)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(file);
        var information = new FileDispositionInformation(1);
        if (!SetFileDispositionInformation(
                file,
                FileDispositionInfo,
                in information,
                checked((uint)Marshal.SizeOf<FileDispositionInformation>())))
        {
            throw new ApplicationDataOwnershipException(
                "The diagnostic log could not be deleted safely.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    public void RenameLogFile(SafeFileHandle file, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_logsFinalPath is null)
        {
            throw new ApplicationDataOwnershipException(
                "The diagnostic log directory is unavailable.");
        }

        var destination = Path.Combine(
            _logsFinalPath,
            GetLogFileName(destinationIndex));
        var fileName = Encoding.Unicode.GetBytes(destination);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(int);
        var minimumStructureSize = IntPtr.Size == 8 ? 24 : 16;
        var bufferSize = Math.Max(
            minimumStructureSize,
            checked(fileNameOffset + fileName.Length + sizeof(char)));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileName.Length);
            Marshal.Copy(fileName, 0, buffer + fileNameOffset, fileName.Length);
            if (!SetFileRenameInformation(
                    file,
                    FileRenameInfo,
                    buffer,
                    checked((uint)bufferSize)))
            {
                throw new ApplicationDataOwnershipException(
                    "The diagnostic log could not be rotated safely.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _logsDirectory?.Dispose();
        _ownershipMarker.Dispose();
        _applicationRoot.Dispose();
    }

    private static async ValueTask VerifyOwnershipMarkerAsync(
        SafeFileHandle marker,
        CancellationToken cancellationToken)
    {
        var expected = Encoding.UTF8.GetBytes(
            ApplicationDataLayout.OwnershipMarkerContent);
        if (RandomAccess.GetLength(marker) != expected.Length)
        {
            throw new ApplicationDataOwnershipException(
                "Application data ownership marker content does not match.");
        }

        var actual = new byte[expected.Length];
        var total = 0;
        while (total < actual.Length)
        {
            var read = await RandomAccess.ReadAsync(
                marker,
                actual.AsMemory(total),
                total,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total != expected.Length || !actual.AsSpan().SequenceEqual(expected))
        {
            throw new ApplicationDataOwnershipException(
                "Application data ownership marker content does not match.");
        }
    }

    private static SafeFileHandle OpenRequiredPathNoFollow(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint additionalFlags,
        bool expectedDirectory)
    {
        if (!TryOpenPathNoFollow(
                path,
                desiredAccess,
                shareMode,
                additionalFlags,
                expectedDirectory,
                out var handle))
        {
            throw new ApplicationDataOwnershipException(
                "A required diagnostic storage path is missing.");
        }

        return handle!;
    }

    private static bool TryOpenPathNoFollow(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint additionalFlags,
        bool expectedDirectory,
        out SafeFileHandle? handle)
    {
        handle = CreateFileForPathInspection(
            path,
            desiredAccess,
            shareMode,
            securityAttributes: 0,
            OpenExisting,
            FileFlagOpenReparsePoint | additionalFlags,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            handle = null;
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return false;
            }

            throw new ApplicationDataOwnershipException(
                "The diagnostic storage path could not be inspected safely.",
                new Win32Exception(error));
        }

        try
        {
            ValidateOpenedPath(
                handle,
                expectedDirectory,
                path,
                expectedParentFinalPath: null);
            return true;
        }
        catch
        {
            handle.Dispose();
            handle = null;
            throw;
        }
    }

    private static void ValidateOpenedPath(
        SafeFileHandle handle,
        bool expectedDirectory,
        string path,
        string? expectedParentFinalPath)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var attributeInformation,
                checked((uint)Marshal.SizeOf<FileAttributeTagInformation>())))
        {
            throw new ApplicationDataOwnershipException(
                "The diagnostic storage path attributes could not be inspected safely.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var attributes = (FileAttributes)attributeInformation.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ApplicationDataOwnershipException(
                "Diagnostic storage operations do not follow reparse points.");
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectedDirectory)
        {
            throw new ApplicationDataOwnershipException(
                "A diagnostic storage path has an unexpected kind.");
        }

        if (!isDirectory)
        {
            if (!GetFileInformationByHandle(handle, out var fileInformation))
            {
                throw new ApplicationDataOwnershipException(
                    "The diagnostic file identity could not be inspected safely.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (fileInformation.NumberOfLinks != 1)
            {
                throw new ApplicationDataOwnershipException(
                    "Diagnostic storage does not accept hard-linked files.");
            }
        }

        if (expectedParentFinalPath is not null)
        {
            EnsureImmediateFinalChild(
                GetNormalizedFinalPath(handle),
                expectedParentFinalPath,
                Path.GetFileName(path));
        }
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
                    "The diagnostic storage final path could not be resolved safely.",
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
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(childFinalPath),
                Path.TrimEndingDirectorySeparator(expectedFinalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationDataOwnershipException(
                "A diagnostic storage path escapes its verified parent.");
        }
    }

    private static string GetLogFileName(int index)
    {
        if (index < 0 || index >= RollingDiagnosticLogPolicy.MaximumFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return index == 0 ? "launcher.log" : $"launcher.{index}.log";
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char* filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileDispositionInformation(
        SafeFileHandle file,
        int fileInformationClass,
        in FileDispositionInformation fileInformation,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileRenameInformation(
        SafeFileHandle file,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileAttributeTagInformation(
        uint FileAttributes,
        uint ReparseTag);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeFileTime(
        uint LowDateTime,
        uint HighDateTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ByHandleFileInformation(
        uint FileAttributes,
        NativeFileTime CreationTime,
        NativeFileTime LastAccessTime,
        NativeFileTime LastWriteTime,
        uint VolumeSerialNumber,
        uint FileSizeHigh,
        uint FileSizeLow,
        uint NumberOfLinks,
        uint FileIndexHigh,
        uint FileIndexLow);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileDispositionInformation(
        byte DeleteFile);
}
