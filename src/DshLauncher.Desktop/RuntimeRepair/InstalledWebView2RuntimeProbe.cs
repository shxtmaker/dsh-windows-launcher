using Microsoft.Web.WebView2.Core;
using System.IO;

namespace DshLauncher.Desktop.RuntimeRepair;

internal sealed class InstalledWebView2RuntimeProbe : IWebView2RuntimeProbe
{
    private readonly Func<string?> _versionProvider;
    private readonly Action _healthCheck;

    public InstalledWebView2RuntimeProbe()
        : this(GetStableRuntimeVersion, VerifyStableRuntimeEnvironment)
    {
    }

    internal InstalledWebView2RuntimeProbe(
        Func<string?> versionProvider,
        Action healthCheck)
    {
        ArgumentNullException.ThrowIfNull(versionProvider);
        ArgumentNullException.ThrowIfNull(healthCheck);

        _versionProvider = versionProvider;
        _healthCheck = healthCheck;
    }

    public WebView2RuntimeObservation Inspect()
    {
        var version = _versionProvider();
        if (version is null)
        {
            return new WebView2RuntimeObservation(null, IsHealthy: false);
        }

        try
        {
            _healthCheck();
            return new WebView2RuntimeObservation(version, IsHealthy: true);
        }
        catch (Exception)
        {
            return new WebView2RuntimeObservation(version, IsHealthy: false);
        }
    }

    private static string? GetStableRuntimeVersion() =>
        CoreWebView2Environment.GetAvailableBrowserVersionString(
            browserExecutableFolder: null,
            CreateStableOptions());

    private static void VerifyStableRuntimeEnvironment()
    {
        var probeUserDataFolder = Path.Combine(
            Path.GetTempPath(),
            "DshWindowsLauncher",
            $"WebView2RuntimeProbe.{Guid.NewGuid():N}");
        try
        {
            _ = CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: probeUserDataFolder,
                    CreateStableOptions())
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            TryDeleteProbeDirectory(probeUserDataFolder);
        }
    }

    private static CoreWebView2EnvironmentOptions CreateStableOptions() => new()
    {
        ReleaseChannels = CoreWebView2ReleaseChannels.Stable,
    };

    private static void TryDeleteProbeDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                DeleteWithoutFollowingReparsePoints(directory);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void DeleteWithoutFollowingReparsePoints(string directory)
    {
        EnsureNotReparsePoint(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        EnsureNotReparsePoint(directory);
        Directory.Delete(directory);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The WebView2 runtime probe does not follow reparse points.");
        }
    }
}
