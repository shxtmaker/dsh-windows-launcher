using System.Diagnostics;
using System.IO;
using System.Windows;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.Platform.Windows;
using DshLauncher.WebUi;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DshLauncher.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The composition root disposes its components explicitly on the BeginExit path.")]
public partial class App : Application
{
    private CurrentUserSingleInstance? _singleInstance;
    private ApplicationDataStore? _applicationData;
    private PairingHub? _hub;
    private HubWebServer? _webServer;
    private TrayHost? _tray;
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var maintenanceExitRequested = SingleInstanceContract.TryGetMaintenanceExitTimeout(
            e.Args,
            out var maintenanceExitTimeout);
        var port = ParsePortArgument(e.Args);

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
                var request = maintenanceExitRequested
                    ? SingleInstanceRequest.Create(SingleInstanceRequestKind.MaintenanceExit)
                    : SingleInstanceRequest.Create(SingleInstanceRequestKind.Activate);
                var response = await CurrentUserSingleInstance.ForwardAsync(
                    identity,
                    request,
                    maintenanceExitRequested
                        ? maintenanceExitTimeout
                        : TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                Shutdown(response == SingleInstanceResponse.Accepted ? 0 : 3);
                return;
            }

            if (maintenanceExitRequested)
            {
                BeginExit();
                return;
            }

            await StartHubAsync(buildIdentity, port).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ReportFatalStartupError(exception);
            Shutdown(1);
        }
    }

    private async Task StartHubAsync(LauncherBuildIdentity buildIdentity, int port)
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            buildIdentity.ApplicationDataId);
        _applicationData = new ApplicationDataStore(new ApplicationDataLayout(dataRoot));
        await _applicationData.InitializeAsync().ConfigureAwait(true);

        var hub = new PairingHub(
            new PairingHubOptions(),
            new JsonTargetStore(_applicationData),
            new HttpPairingTransport(new PairingTransportOptions
            {
                UserAgent = $"DshWindowsLauncher/{BuildVersionString()}",
            }),
            new SystemClock(),
            new GuidIdGenerator());
        _hub = hub;
        await hub.StartAsync().ConfigureAwait(true);

        _webServer = new HubWebServer(hub, new HubWebServerOptions { Port = port });
        try
        {
            await _webServer.StartAsync().ConfigureAwait(true);
        }
        catch (IOException) when (port != 0)
        {
            // The default port is taken (another instance or another app):
            // fall back to a free loopback port instead of refusing to start.
            await _webServer.DisposeAsync().ConfigureAwait(true);
            _webServer = new HubWebServer(hub, new HubWebServerOptions { Port = 0 });
            await _webServer.StartAsync().ConfigureAwait(true);
        }

        _tray = new TrayHost(buildIdentity.ProductName, _webServer.DashboardUrl);
        _tray.ExitRequested += BeginExit;
        hub.Changed += HubStateChanged;
        HubStateChanged(await hub.GetSnapshotAsync().ConfigureAwait(true));
    }

    private void HubStateChanged(HubSnapshot snapshot)
    {
        if (_tray is null)
        {
            return;
        }

        var online = snapshot.Targets.Count(target =>
            target.Pairing == PairingState.Paired && target.Connectivity == ConnectivityState.Online);
        var paired = snapshot.Targets.Count(target => target.Pairing == PairingState.Paired);
        _tray.UpdateStatus($"配对 {paired}（在线 {online}）· 心跳 {Math.Round(snapshot.HeartbeatInterval.TotalSeconds)} 秒");
    }

    private ValueTask<SingleInstanceResponse> HandleSingleInstanceRequestAsync(
        SingleInstanceRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case SingleInstanceRequestKind.Activate:
                _tray?.OpenDashboard();
                return ValueTask.FromResult(SingleInstanceResponse.Accepted);
            case SingleInstanceRequestKind.MaintenanceExit:
                Dispatcher.BeginInvoke(BeginExit);
                return ValueTask.FromResult(SingleInstanceResponse.Accepted);
            default:
                return ValueTask.FromResult(SingleInstanceResponse.Rejected);
        }
    }

    private void BeginExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (_hub is not null)
                {
                    _hub.Changed -= HubStateChanged;
                    await _hub.DisposeAsync().ConfigureAwait(true);
                }

                if (_webServer is not null)
                {
                    await _webServer.DisposeAsync().ConfigureAwait(true);
                }

                if (_tray is not null)
                {
                    _tray.ExitRequested -= BeginExit;
                    _tray.Dispose();
                }

                if (_singleInstance is not null)
                {
                    await _singleInstance.DisposeAsync().ConfigureAwait(true);
                }

                _applicationData?.Dispose();
            }
            finally
            {
                Shutdown(0);
            }
        });
    }

    private static void ReportFatalStartupError(Exception exception)
    {
        var safe = exception is HubStorageException or ApplicationDataOwnershipException or IOException
            ? exception.Message
            : "启动失败，请查看 Windows 事件查看器。";
        MessageBox.Show(safe, LauncherBuildIdentity.Current.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string BuildVersionString() =>
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0))
            .ToString(3);

    private static int ParsePortArgument(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], "--port", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], out var port) &&
                port is >= 0 and <= 65535)
            {
                return port;
            }
        }

        return HubWebServerOptions.DefaultPort;
    }
}
