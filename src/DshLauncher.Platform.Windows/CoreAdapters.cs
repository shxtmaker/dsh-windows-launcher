using System.Collections;
using System.Runtime.InteropServices;
using DshLauncher.Core;

namespace DshLauncher.Platform.Windows;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

public sealed class WindowsNetworkPort : INetworkPort
{
    public ValueTask<NetworkCategory> GetCurrentCategoryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadCurrentCategory());
    }

    public static NetworkCategory AggregateCategories(
        IEnumerable<NetworkCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var foundPrivate = false;
        var foundUnknown = false;
        foreach (var category in categories)
        {
            if (category == NetworkCategory.Public)
            {
                return NetworkCategory.Public;
            }

            foundPrivate |= category == NetworkCategory.Private;
            foundUnknown |= category == NetworkCategory.Unknown;
        }

        return foundPrivate && !foundUnknown
            ? NetworkCategory.Private
            : NetworkCategory.Unknown;
    }

    private static NetworkCategory ReadCurrentCategory()
    {
        object? managerObject = null;
        object? networksObject = null;
        var categories = new List<NetworkCategory>();
        try
        {
            managerObject = new NetworkListManagerComObject();
            var manager = (INetworkListManager)managerObject;
            networksObject = manager.GetNetworks(NetworkEnumeration.Connected);
            foreach (var item in (IEnumerable)networksObject)
            {
                try
                {
                    if (item is INetwork network)
                    {
                        categories.Add(network.GetCategory() switch
                        {
                            NativeNetworkCategory.Private or NativeNetworkCategory.DomainAuthenticated =>
                                NetworkCategory.Private,
                            NativeNetworkCategory.Public => NetworkCategory.Public,
                            _ => NetworkCategory.Unknown,
                        });
                    }
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }

            return AggregateCategories(categories);
        }
        catch (COMException)
        {
            return NetworkCategory.Unknown;
        }
        catch (InvalidCastException)
        {
            return NetworkCategory.Unknown;
        }
        finally
        {
            ReleaseComObject(networksObject);
            ReleaseComObject(managerObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [Flags]
    private enum NetworkEnumeration
    {
        Connected = 0x01,
    }

    private enum NativeNetworkCategory
    {
        Public = 0,
        Private = 1,
        DomainAuthenticated = 2,
    }

    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private sealed class NetworkListManagerComObject;

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(NetworkEnumeration flags);
    }

    [ComImport]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

        Guid GetNetworkId();

        int GetDomainType();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();

        void GetTimeCreatedAndConnected(
            out uint lowDateTimeCreated,
            out uint highDateTimeCreated,
            out uint lowDateTimeConnected,
            out uint highDateTimeConnected);

        bool IsConnectedToInternet { get; }

        bool IsConnected { get; }

        int GetConnectivity();

        NativeNetworkCategory GetCategory();

        void SetCategory(NativeNetworkCategory category);
    }
}

public sealed partial class WindowsClipboardPort : IClipboardPort
{
    private const uint UnicodeTextFormat = 13;

    public ValueTask ClearIfUnchangedAsync(
        ReadOnlyMemory<char> pastedText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pastedText.IsEmpty || !OpenClipboard(IntPtr.Zero))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var handle = GetClipboardData(UnicodeTextFormat);
            if (handle == IntPtr.Zero || !ContentEquals(handle, pastedText.Span))
            {
                return ValueTask.CompletedTask;
            }

            _ = EmptyClipboard();
        }
        finally
        {
            _ = CloseClipboard();
        }

        return ValueTask.CompletedTask;
    }

    private static bool ContentEquals(IntPtr handle, ReadOnlySpan<char> expected)
    {
        var byteCount = GlobalSize(handle);
        if (byteCount < 2 || byteCount > int.MaxValue)
        {
            return false;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var capacity = checked((int)byteCount / sizeof(char));
            if (expected.Length >= capacity)
            {
                return false;
            }

            for (var index = 0; index < expected.Length; index++)
            {
                if ((char)Marshal.ReadInt16(pointer, index * sizeof(char)) != expected[index])
                {
                    return false;
                }
            }

            return Marshal.ReadInt16(pointer, expected.Length * sizeof(char)) == 0;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr newOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint GlobalSize(IntPtr memory);
}
