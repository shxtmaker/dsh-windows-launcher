using System.Globalization;

namespace DshLauncher.Desktop.RuntimeRepair;

public interface IWebView2RuntimeProbe
{
    WebView2RuntimeObservation Inspect();
}

public readonly record struct WebView2RuntimeObservation(
    string? AvailableVersion,
    bool IsHealthy);

public interface IWebView2BootstrapperRunner
{
    ValueTask<WebView2BootstrapperRunResult> RunAsync(CancellationToken cancellationToken);
}

public enum WebView2RuntimeStatus
{
    Ready,
    Missing,
    InvalidVersion,
    BelowMinimum,
    Corrupted,
}

public readonly record struct WebView2RuntimeCheck(
    WebView2RuntimeStatus Status,
    string? InstalledVersion)
{
    public bool IsReady => Status == WebView2RuntimeStatus.Ready;
}

public enum WebView2BootstrapperRunStatus
{
    Completed,
    Missing,
    StartFailed,
    NonZeroExit,
    TimedOut,
    Cancelled,
    Untrusted,
}

public readonly record struct WebView2BootstrapperRunResult(
    WebView2BootstrapperRunStatus Status,
    int? ExitCode = null);

public enum WebView2RuntimeRepairStatus
{
    Succeeded,
    BootstrapperMissing,
    ProcessFailed,
    InstallerFailed,
    TimedOut,
    Cancelled,
    RuntimeStillUnavailable,
    BootstrapperUntrusted,
}

public readonly record struct WebView2RuntimeRepairResult(
    WebView2RuntimeRepairStatus Status,
    WebView2RuntimeCheck Runtime,
    int? ExitCode = null)
{
    public bool Succeeded => Status == WebView2RuntimeRepairStatus.Succeeded;
}

public sealed class WebView2RuntimeDependency
{
    public const string BootstrapperFileName = "MicrosoftEdgeWebview2Setup.exe";
    public const string MinimumVersion = "151.0.4129.50";

    public static TimeSpan BootstrapperTimeout { get; } = TimeSpan.FromMinutes(5);

    private static readonly Version RequiredVersion = new(151, 0, 4129, 50);
    private readonly IWebView2RuntimeProbe _probe;
    private readonly IWebView2BootstrapperRunner _bootstrapper;

    public WebView2RuntimeDependency(
        IWebView2RuntimeProbe probe,
        IWebView2BootstrapperRunner bootstrapper)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(bootstrapper);

        _probe = probe;
        _bootstrapper = bootstrapper;
    }

    public WebView2RuntimeCheck Check()
    {
        WebView2RuntimeObservation observation;
        try
        {
            observation = _probe.Inspect();
        }
        catch (Exception)
        {
            return new WebView2RuntimeCheck(WebView2RuntimeStatus.Missing, null);
        }

        var installedVersion = observation.AvailableVersion;
        if (installedVersion is null)
        {
            return new WebView2RuntimeCheck(WebView2RuntimeStatus.Missing, null);
        }

        if (!TryParseVersion(installedVersion, out var parsedVersion))
        {
            return new WebView2RuntimeCheck(
                WebView2RuntimeStatus.InvalidVersion,
                installedVersion);
        }

        if (parsedVersion < RequiredVersion)
        {
            return new WebView2RuntimeCheck(
                WebView2RuntimeStatus.BelowMinimum,
                installedVersion);
        }

        return new WebView2RuntimeCheck(
            observation.IsHealthy
                ? WebView2RuntimeStatus.Ready
                : WebView2RuntimeStatus.Corrupted,
            installedVersion);
    }

    public async ValueTask<WebView2RuntimeRepairResult> RepairAsync(
        CancellationToken cancellationToken)
    {
        WebView2BootstrapperRunResult attempt;
        try
        {
            attempt = await _bootstrapper.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            attempt = new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.Cancelled);
        }
        catch (Exception)
        {
            attempt = new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.StartFailed);
        }

        var runtime = Check();
        var status = attempt.Status switch
        {
            WebView2BootstrapperRunStatus.Completed when runtime.IsReady =>
                WebView2RuntimeRepairStatus.Succeeded,
            WebView2BootstrapperRunStatus.Completed =>
                WebView2RuntimeRepairStatus.RuntimeStillUnavailable,
            WebView2BootstrapperRunStatus.Missing =>
                WebView2RuntimeRepairStatus.BootstrapperMissing,
            WebView2BootstrapperRunStatus.StartFailed =>
                WebView2RuntimeRepairStatus.ProcessFailed,
            WebView2BootstrapperRunStatus.NonZeroExit =>
                WebView2RuntimeRepairStatus.InstallerFailed,
            WebView2BootstrapperRunStatus.TimedOut =>
                WebView2RuntimeRepairStatus.TimedOut,
            WebView2BootstrapperRunStatus.Cancelled =>
                WebView2RuntimeRepairStatus.Cancelled,
            WebView2BootstrapperRunStatus.Untrusted =>
                WebView2RuntimeRepairStatus.BootstrapperUntrusted,
            _ => WebView2RuntimeRepairStatus.ProcessFailed,
        };
        return new WebView2RuntimeRepairResult(status, runtime, attempt.ExitCode);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = RequiredVersion;
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        Span<int> components = stackalloc int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || part.Any(character => character is < '0' or > '9') ||
                !int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out components[index]))
            {
                return false;
            }
        }

        version = new Version(
            components[0],
            components[1],
            components[2],
            components[3]);
        return true;
    }
}
