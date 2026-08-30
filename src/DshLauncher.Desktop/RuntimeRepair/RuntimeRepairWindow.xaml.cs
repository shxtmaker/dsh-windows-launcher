using System.Windows;
using DshLauncher.Desktop.Resources;

namespace DshLauncher.Desktop.RuntimeRepair;

public partial class RuntimeRepairWindow : Window, IDisposable
{
    private readonly WebView2RuntimeDependency _dependency;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _repairInProgress;
    private bool _disposed;

    public RuntimeRepairWindow(
        WebView2RuntimeDependency dependency,
        WebView2RuntimeCheck initialCheck)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        _dependency = dependency;
        InitializeComponent();
        DescriptionText.Text = Strings.RuntimeRepairDescription.Replace(
            "{0}",
            WebView2RuntimeDependency.MinimumVersion,
            StringComparison.Ordinal);
        OnlineRepairText.Text = Strings.RuntimeRepairOnlineGuidance.Replace(
            "{0}",
            WebView2RuntimeDependency.BootstrapperFileName,
            StringComparison.Ordinal);
        StatusText.Text = DescribeRuntime(initialCheck.Status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    private async void RepairClick(object sender, RoutedEventArgs e)
    {
        if (_repairInProgress || _disposed)
        {
            return;
        }

        _repairInProgress = true;
        RepairButton.IsEnabled = false;
        RepairProgress.Visibility = Visibility.Visible;
        StatusText.Text = Strings.RuntimeRepairRunning;

        try
        {
            var result = await _dependency.RepairAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (result.Succeeded)
            {
                DialogResult = true;
                return;
            }

            StatusText.Text = DescribeFailure(result.Status);
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                StatusText.Text = Strings.RuntimeRepairProcessFailed;
            }
        }
        finally
        {
            if (!_disposed)
            {
                _repairInProgress = false;
                RepairButton.IsEnabled = true;
                RepairProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static string DescribeRuntime(WebView2RuntimeStatus status) => status switch
    {
        WebView2RuntimeStatus.Missing => Strings.RuntimeRepairMissing,
        WebView2RuntimeStatus.InvalidVersion => Strings.RuntimeRepairInvalidVersion,
        WebView2RuntimeStatus.BelowMinimum => Strings.RuntimeRepairBelowMinimum,
        _ => Strings.RuntimeRepairRequired,
    };

    private static string DescribeFailure(WebView2RuntimeRepairStatus status) => status switch
    {
        WebView2RuntimeRepairStatus.BootstrapperMissing =>
            Strings.RuntimeRepairBootstrapperMissing,
        WebView2RuntimeRepairStatus.InstallerFailed =>
            Strings.RuntimeRepairInstallerFailed,
        WebView2RuntimeRepairStatus.TimedOut => Strings.RuntimeRepairTimedOut,
        WebView2RuntimeRepairStatus.Cancelled => Strings.RuntimeRepairCancelled,
        WebView2RuntimeRepairStatus.RuntimeStillUnavailable =>
            Strings.RuntimeRepairStillUnavailable,
        _ => Strings.RuntimeRepairProcessFailed,
    };
}
