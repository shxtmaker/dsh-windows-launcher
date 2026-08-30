using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DshLauncher.Core;
using DshLauncher.Desktop.Resources;
using DshLauncher.Desktop.RuntimeRepair;
using DshLauncher.Desktop.ViewModels;
using DshLauncher.Platform.Windows;
using DshLauncher.WebView;

namespace DshLauncher.Desktop;

public partial class App : Application, IDisposable
{
    private readonly TaskCompletionSource<bool> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CurrentUserSingleInstance? _singleInstance;
    private ApplicationDataStore? _applicationData;
    private HarnessTargetProbePort? _probe;
    private ITargetRuntimePort? _targetRuntime;
    private TargetManager? _targetManager;
    private RollingDiagnosticLog? _log;
    private WindowCoordinator? _windowCoordinator;
    private LauncherController? _controller;
    private TargetCenterWindow? _targetCenter;
    private bool _maintenanceExitInProgress;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var maintenanceExitRequested = SingleInstanceContract.TryGetMaintenanceExitTimeout(
            e.Args,
            out var maintenanceExitTimeout);
        if (e.Args.Length > 0 && !maintenanceExitRequested)
        {
            Shutdown(2);
            return;
        }

        try
        {
            var buildIdentity = LauncherBuildIdentity.Current;
            var identity = CurrentUserInstanceIdentity.ForCurrentUser(
                buildIdentity.SingleInstanceBaseName);
            _singleInstance = CurrentUserSingleInstance.TryStart(
                identity,
                HandleSingleInstanceRequestAsync);
            if (_singleInstance is null)
            {
                var forwardingStarted = Stopwatch.StartNew();
                var request = maintenanceExitRequested
                    ? SingleInstanceRequest.Create(SingleInstanceRequestKind.MaintenanceExit)
                    : SingleInstanceRequest.Create(SingleInstanceRequestKind.Activate);
                var response = await CurrentUserSingleInstance.ForwardAsync(
                    identity,
                    request,
                    maintenanceExitRequested
                        ? maintenanceExitTimeout
                        : TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                if (maintenanceExitRequested && response == SingleInstanceResponse.Accepted)
                {
                    var remaining = maintenanceExitTimeout - forwardingStarted.Elapsed;
                    if (remaining <= TimeSpan.Zero ||
                        !await WaitForPrimaryReleaseAsync(identity, remaining).ConfigureAwait(true))
                    {
                        Shutdown(3);
                        return;
                    }
                }

                Shutdown(response == SingleInstanceResponse.Accepted ? 0 : 3);
                return;
            }

            if (maintenanceExitRequested)
            {
                await _singleInstance.DisposeAsync().ConfigureAwait(true);
                _singleInstance = null;
                Shutdown(0);
                return;
            }

            if (!EnsureWebView2Runtime())
            {
                _ready.TrySetCanceled();
                Shutdown(1);
                return;
            }

            await InitializeApplicationAsync().ConfigureAwait(true);
            _ready.TrySetResult(true);
            await _controller!.ApplyStartupPolicyAsync(CancellationToken.None).ConfigureAwait(true);
            await _targetCenter!.RefreshAsync().ConfigureAwait(true);
        }
        catch (LauncherPresentationException exception)
        {
            _ready.TrySetException(exception);
            MessageBox.Show(
                exception.UserMessage,
                LauncherBuildIdentity.Current.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            _ready.TrySetCanceled();
            MessageBox.Show(
                Strings.UnexpectedError,
                LauncherBuildIdentity.Current.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private bool EnsureWebView2Runtime()
    {
        var dependency = new WebView2RuntimeDependency(
            new InstalledWebView2RuntimeProbe(),
            new WebView2BootstrapperRunner(
                AppContext.BaseDirectory,
                WebView2RuntimeDependency.BootstrapperTimeout));
        var check = dependency.Check();
        if (check.IsReady)
        {
            return true;
        }

        var previousShutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            using var repairWindow = new RuntimeRepairWindow(dependency, check);
            MainWindow = repairWindow;
            return repairWindow.ShowDialog() == true;
        }
        finally
        {
            ShutdownMode = previousShutdownMode;
        }
    }

    private async ValueTask InitializeApplicationAsync()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LauncherBuildIdentity.Current.ApplicationDataId);
        var layout = new ApplicationDataLayout(root);
        _applicationData = new ApplicationDataStore(layout);
        await _applicationData.InitializeAsync().ConfigureAwait(true);

        _log = new RollingDiagnosticLog(layout);
        await _log.WriteAsync(DiagnosticRecord.Create(
            DateTimeOffset.UtcNow,
            DiagnosticEventCode.ApplicationStarted,
            statusCode: null,
            classification: "startup",
            normalizedPrivateEndpoint: null,
            includePrivateEndpoint: false)).ConfigureAwait(true);

        var storage = new JsonTargetStorage(_applicationData);
        var sessions = new JsonSessionPort(_applicationData);
        var browserData = new TargetBrowserDataStore(_applicationData);
        var pairingDiagnostics = new PairingDiagnosticLogSink(_log);
        _targetRuntime = new WebView2TargetRuntimePort(browserData, pairingDiagnostics);
        var httpProbe = HarnessHttpProbeAdapter.CreateDefault(
            new HarnessHttpProbeOptions(
                TimeSpan.FromSeconds(3),
                maxResponseBytes: 4096));
        _probe = new HarnessTargetProbePort(httpProbe, HarnessProbeFingerprint.V1);
        var clipboard = new WindowsClipboardPort();

        _targetManager = new TargetManager(new TargetManagerPorts(
            storage,
            new SystemClock(),
            new GuidIdGenerator(),
            new WindowsNetworkPort(),
            _probe,
            sessions,
            _targetRuntime,
            clipboard,
            pairingDiagnostics));

        Func<Window?> ownerProvider = () => _targetCenter;
        var externalLauncher = new SystemTargetExternalUriLauncher();
        var externalConsent = new WpfTargetExternalNavigationConsent(ownerProvider, Dispatcher);
        _windowCoordinator = new WindowCoordinator(
            layout,
            externalConsent,
            externalLauncher,
            Dispatcher);
        _windowCoordinator.AllTargetWindowsClosed += (_, _) =>
        {
            if (!_maintenanceExitInProgress && _targetCenter is { IsVisible: false })
            {
                _targetCenter.Close();
            }
        };

        var dialogs = new WpfLauncherDialogs(ownerProvider, clipboard);
        var diagnostics = new DiagnosticExportService(_log, ownerProvider, Dispatcher);
        var updates = new UpdatePageService(
            ReadOfficialReleaseUri(),
            ownerProvider,
            Dispatcher,
            externalLauncher);
        _controller = new LauncherController(
            _targetManager,
            dialogs,
            _windowCoordinator,
            diagnostics,
            updates);
        _targetCenter = new TargetCenterWindow(
            _controller,
            () => _windowCoordinator.HasOpenWindows);
        _controller.SnapshotChanged += OnControllerSnapshotChanged;
        MainWindow = _targetCenter;
        _targetCenter.Show();
        await _targetCenter.InitializeAsync().ConfigureAwait(true);
    }

    private async ValueTask<SingleInstanceResponse> HandleSingleInstanceRequestAsync(
        SingleInstanceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            switch (request.Kind)
            {
                case SingleInstanceRequestKind.Activate:
                    await Dispatcher.InvokeAsync(
                        () => _targetCenter!.ActivateFromRequest(),
                        DispatcherPriority.Normal,
                        cancellationToken);
                    return SingleInstanceResponse.Accepted;

                case SingleInstanceRequestKind.OpenTarget when request.TargetId is { } targetId:
                    await Dispatcher.InvokeAsync(
                        () => _controller!.OpenAsync(targetId, cancellationToken).AsTask(),
                        DispatcherPriority.Normal,
                        cancellationToken).Task.Unwrap().ConfigureAwait(false);
                    return SingleInstanceResponse.Accepted;

                case SingleInstanceRequestKind.MaintenanceExit:
                    _maintenanceExitInProgress = true;
                    await Dispatcher.InvokeAsync(
                        () => _windowCoordinator!.CloseAllAsync(cancellationToken).AsTask(),
                        DispatcherPriority.Normal,
                        cancellationToken).Task.Unwrap().ConfigureAwait(false);
                    _ = CompleteMaintenanceExitAfterResponseAsync();
                    return SingleInstanceResponse.Accepted;

                default:
                    return SingleInstanceResponse.Rejected;
            }
        }
        catch (Exception)
        {
            return SingleInstanceResponse.Rejected;
        }
    }

    private async Task CompleteMaintenanceExitAfterResponseAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_targetCenter is { } center)
                {
                    center.Close();
                }
                else
                {
                    Shutdown();
                }
            });
        }
        catch (Exception)
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() => Shutdown(1));
            }
        }
    }

    private static async ValueTask<bool> WaitForPrimaryReleaseAsync(
        CurrentUserInstanceIdentity identity,
        TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            var claimed = CurrentUserSingleInstance.TryStart(
                identity,
                static (_, _) => ValueTask.FromResult(SingleInstanceResponse.Rejected));
            if (claimed is not null)
            {
                await claimed.DisposeAsync().ConfigureAwait(false);
                return true;
            }

            var remaining = timeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(100)
                    ? remaining
                    : TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return false;
    }

    private void DisposeResources()
    {
        if (_controller is not null)
        {
            _controller.SnapshotChanged -= OnControllerSnapshotChanged;
        }

        try
        {
            _singleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        _targetManager?.Dispose();
        if (_targetRuntime is IAsyncDisposable asyncRuntime)
        {
            try
            {
                asyncRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }

        try
        {
            _probe?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _log?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        _applicationData?.Dispose();
    }

    private async void OnControllerSnapshotChanged(object? sender, EventArgs args)
    {
        try
        {
            if (_targetCenter is not null &&
                !Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                await _targetCenter.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            // Background session cleanup is complete even if presentation refresh fails.
        }
    }

    private static Uri? ReadOfficialReleaseUri()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(
            "DshLauncher.Desktop.ReleaseConstants.json") ??
            throw new InvalidOperationException("Embedded release constants are missing.");
        using var document = JsonDocument.Parse(stream);
        var value = document.RootElement
            .GetProperty("distribution")
            .GetProperty("officialReleaseUri");
        return value.ValueKind == JsonValueKind.String &&
               Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            ? uri
            : null;
    }
}
