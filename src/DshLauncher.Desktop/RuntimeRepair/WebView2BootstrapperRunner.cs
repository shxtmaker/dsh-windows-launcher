using System.Diagnostics;
using System.IO;

namespace DshLauncher.Desktop.RuntimeRepair;

internal sealed class WebView2BootstrapperRunner : IWebView2BootstrapperRunner
{
    private readonly string _executableDirectory;
    private readonly TimeSpan _timeout;
    private readonly IWebView2BootstrapperPreparer _preparer;
    private readonly IWebView2BootstrapperProcessRunner _process;

    public WebView2BootstrapperRunner(string executableDirectory, TimeSpan timeout)
        : this(
            executableDirectory,
            timeout,
            new SecureWebView2BootstrapperPreparer(
                WebView2BootstrapperTrustPolicy.LoadEmbedded(),
                new WindowsWebView2BootstrapperPathGuard(),
                new WindowsWebView2AuthenticodeVerifier(),
                Path.GetTempPath),
            new WindowsWebView2BootstrapperProcessRunner())
    {
    }

    internal WebView2BootstrapperRunner(
        string executableDirectory,
        TimeSpan timeout,
        IWebView2BootstrapperPreparer preparer,
        IWebView2BootstrapperProcessRunner process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(preparer);
        ArgumentNullException.ThrowIfNull(process);

        _executableDirectory = Path.GetFullPath(executableDirectory);
        _timeout = timeout;
        _preparer = preparer;
        _process = process;
    }

    public async ValueTask<WebView2BootstrapperRunResult> RunAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.Cancelled);
        }

        var sourcePath = Path.Combine(
            _executableDirectory,
            WebView2RuntimeDependency.BootstrapperFileName);
        WebView2BootstrapperPreparation preparation;
        try
        {
            preparation = await _preparer.PrepareAsync(
                sourcePath,
                _executableDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.Cancelled);
        }
        catch (Exception)
        {
            return new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.Untrusted);
        }

        await using (preparation.ConfigureAwait(false))
        {
            if (preparation.Status == WebView2BootstrapperPreparationStatus.Missing)
            {
                return new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.Missing);
            }

            if (preparation.Status != WebView2BootstrapperPreparationStatus.Prepared ||
                preparation.Artifact is null)
            {
                return new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.Untrusted);
            }

            try
            {
                return await _process.RunAsync(
                    preparation.Artifact.ExecutablePath,
                    _executableDirectory,
                    _timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.Cancelled);
            }
            catch (Exception)
            {
                return new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.StartFailed);
            }
        }
    }
}

internal sealed class WindowsWebView2BootstrapperProcessRunner
    : IWebView2BootstrapperProcessRunner
{
    public async ValueTask<WebView2BootstrapperRunResult> RunAsync(
        string executablePath,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, workingDirectory),
        };

        try
        {
            if (!process.Start())
            {
                return new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.StartFailed);
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryStop(process);
                return new WebView2BootstrapperRunResult(
                    cancellationToken.IsCancellationRequested
                        ? WebView2BootstrapperRunStatus.Cancelled
                        : WebView2BootstrapperRunStatus.TimedOut);
            }

            return process.ExitCode == 0
                ? new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.Completed,
                    process.ExitCode)
                : new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.NonZeroExit,
                    process.ExitCode);
        }
        catch (Exception)
        {
            TryStop(process);
            return new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.StartFailed);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/silent");
        startInfo.ArgumentList.Add("/install");
        return startInfo;
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
