using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Desktop.RuntimeRepair;

internal sealed partial class WindowsWebView2BootstrapperPathGuard
    : IWebView2BootstrapperPathGuard
{
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileAttributeTagInfo = 9;

    public FileStream OpenExpectedRegularFile(
        string path,
        string expectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDirectory);

        var fullDirectory = NormalizePath(expectedDirectory);
        var expectedPath = NormalizePath(Path.Combine(
            fullDirectory,
            WebView2RuntimeDependency.BootstrapperFileName));
        var fullPath = NormalizePath(path);
        if (!string.Equals(
                fullPath,
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper is not at the fixed adjacent path.");
        }

        EnsurePathHasNoReparsePoint(fullPath);

        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper could not be opened without sharing writes or deletes.",
                exception);
        }

        try
        {
            EnsureHandleMatches(stream.SafeFileHandle, fullPath, expectDirectory: false);
            return stream;
        }
        catch (Exception)
        {
            stream.Dispose();
            throw;
        }
    }

    public SafeFileHandle LockCreatedDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = NormalizePath(path);
        EnsurePathHasNoReparsePoint(fullPath);
        var handle = CreateFileForPathInspection(
            fullPath,
            desiredAccess: 0,
            shareMode: (uint)(FileShare.Read | FileShare.Write),
            securityAttributes: 0,
            creationDisposition: OpenExisting,
            flagsAndAttributes: FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw new UnsafeWebView2BootstrapperPathException(
                "The temporary WebView2 bootstrapper directory could not be locked.",
                error);
        }

        try
        {
            EnsureHandleMatches(handle, fullPath, expectDirectory: true);
            return handle;
        }
        catch (Exception)
        {
            handle.Dispose();
            throw;
        }
    }

    private static void EnsurePathHasNoReparsePoint(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper path has no local root.");
        }

        var current = root;
        EnsureNotReparsePoint(current);
        foreach (var segment in path[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper path contains a reparse point.");
        }
    }

    private static void EnsureHandleMatches(
        SafeFileHandle handle,
        string expectedPath,
        bool expectDirectory)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var facts,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper handle could not be inspected.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var isDirectory = (facts.FileAttributes & FileAttributeDirectory) != 0;
        if ((facts.FileAttributes & FileAttributeReparsePoint) != 0 ||
            isDirectory != expectDirectory)
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper handle does not reference the expected regular path.");
        }

        var finalPath = GetFinalPath(handle);
        if (!string.Equals(
                finalPath,
                NormalizePath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeWebView2BootstrapperPathException(
                "The WebView2 bootstrapper final path escaped the expected location.");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        const int initialCharacterCount = 512;
        var characterCount = initialCharacterCount;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(checked(characterCount * sizeof(char)));
            try
            {
                var result = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)characterCount,
                    flags: 0);
                if (result == 0)
                {
                    throw new UnsafeWebView2BootstrapperPathException(
                        "The WebView2 bootstrapper final path could not be resolved.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                }

                if (result < characterCount)
                {
                    return NormalizePath(
                        Marshal.PtrToStringUni(buffer, checked((int)result)) ??
                        throw new UnsafeWebView2BootstrapperPathException(
                            "The WebView2 bootstrapper final path was empty."));
                }

                characterCount = checked((int)result + 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string uncExtendedPrefix = @"\\?\UNC\";

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
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        nint filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileAttributeTagInformation(
        uint FileAttributes,
        uint ReparseTag);
}
