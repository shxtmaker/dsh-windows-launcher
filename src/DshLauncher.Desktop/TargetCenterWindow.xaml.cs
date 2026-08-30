using System.Windows;
using DshLauncher.Desktop.ViewModels;

namespace DshLauncher.Desktop;

public partial class TargetCenterWindow : Window
{
    private readonly Func<bool> _hasOpenTargetWindows;
    private Task? _initializationTask;

    public TargetCenterWindow(
        ILauncherController controller,
        Func<bool>? hasOpenTargetWindows = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _hasOpenTargetWindows = hasOpenTargetWindows ?? (() => false);
        InitializeComponent();
        DataContext = new TargetCenterViewModel(controller);
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_hasOpenTargetWindows())
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void ActivateFromRequest()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public Task InitializeAsync()
    {
        if (DataContext is not TargetCenterViewModel viewModel)
        {
            return Task.CompletedTask;
        }

        return _initializationTask ??= viewModel.InitializeAsync();
    }

    public Task RefreshAsync() =>
        DataContext is TargetCenterViewModel viewModel
            ? viewModel.RefreshAsync()
            : Task.CompletedTask;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await InitializeAsync().ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        Closing -= OnClosing;
        if (DataContext is TargetCenterViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
