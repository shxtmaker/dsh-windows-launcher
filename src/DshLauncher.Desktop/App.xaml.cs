using System.IO;
using System.Windows;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.Platform.Windows;
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
    private TrayHost? _tray;
    private ManagementWindow? _management;
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var maintenanceExitRequested = SingleInstanceContract.TryGetMaintenanceExitTimeout(
            e.Args,
            out var maintenanceExitTimeout);

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

            await StartHubAsync(buildIdentity).ConfigureAwait(true);
            OpenManagement();
        }
        catch (Exception exception)
        {
            ReportFatalStartupError(exception);
            Shutdown(1);
        }
    }

    private async Task StartHubAsync(LauncherBuildIdentity buildIdentity)
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

        _tray = new TrayHost(
            buildIdentity.ProductName,
            OpenManagement,
            BeginExit);
        _tray.UpdateStatus("配对保活运行中");
    }

    private ValueTask<SingleInstanceResponse> HandleSingleInstanceRequestAsync(
        SingleInstanceRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case SingleInstanceRequestKind.Activate:
                Dispatcher.BeginInvoke(OpenManagement);
                return ValueTask.FromResult(SingleInstanceResponse.Accepted);
            case SingleInstanceRequestKind.MaintenanceExit:
                Dispatcher.BeginInvoke(BeginExit);
                return ValueTask.FromResult(SingleInstanceResponse.Accepted);
            case SingleInstanceRequestKind.OpenTarget:
                var requestedTargetId = request.TargetId;
                if (requestedTargetId is null || requestedTargetId == Guid.Empty)
                {
                    return ValueTask.FromResult(SingleInstanceResponse.Rejected);
                }

                var targetId = requestedTargetId.Value;
                Dispatcher.BeginInvoke(async () =>
                {
                    if (_exitRequested || _hub is null)
                    {
                        return;
                    }

                    OpenManagement();
                    if (_management is not null)
                    {
                        await _management
                            .OpenRemoteForTargetAsync(targetId)
                            .ConfigureAwait(true);
                    }
                });
                return ValueTask.FromResult(SingleInstanceResponse.Accepted);
            default:
                return ValueTask.FromResult(SingleInstanceResponse.Rejected);
        }
    }

    private void OpenManagement()
    {
        if (_exitRequested)
        {
            return;
        }

        if (_management is null)
        {
            _management = new ManagementWindow(_hub!, _applicationData!, _tray!);
        }

        _management.ShowAndActivate();
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
            var cleanupFailures = new List<string>();

            if (_management is not null)
            {
                try
                {
                    _management.AllowClose();
                    _management.Close();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception.Message);
                }
                finally
                {
                    _management = null;
                }
            }

            if (_hub is not null)
            {
                try
                {
                    await _hub.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception.Message);
                }
            }

            if (_tray is not null)
            {
                try
                {
                    _tray.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception.Message);
                }
            }

            if (_singleInstance is not null)
            {
                try
                {
                    await _singleInstance.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception.Message);
                }
            }

            try
            {
                _applicationData?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception.Message);
            }

            if (cleanupFailures.Count > 0)
            {
                System.Diagnostics.Trace.TraceError(
                    "关闭清理失败：{0}",
                    string.Join(" | ", cleanupFailures));
                Shutdown(1);
            }
            else
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
}
