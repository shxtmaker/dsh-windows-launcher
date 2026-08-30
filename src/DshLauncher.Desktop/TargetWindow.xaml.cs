using System.Windows;
using System.Windows.Input;
using DshLauncher.Core;
using DshLauncher.Desktop.Resources;

namespace DshLauncher.Desktop;

public enum TargetWindowState
{
    Initializing,
    Loading,
    Ready,
    Recovering,
    Blocked,
    Failed,
}

public partial class TargetWindow : Window
{
    public TargetWindow(string title)
    {
        InitializeComponent();
        Title = ComposeTitle(title);
    }

    internal static string ComposeTitle(string title) =>
        $"{LauncherBuildIdentity.Current.ProductName} · {title}";

    public event EventHandler? ReloadRequested;

    public void SetHostContent(UIElement content) => HostSurface.Content = content;

    public void SetState(TargetWindowState state, string? detail = null)
    {
        StateDetail.Text = detail ?? string.Empty;
        switch (state)
        {
            case TargetWindowState.Ready:
                StatePanel.Visibility = Visibility.Collapsed;
                break;
            case TargetWindowState.Recovering:
                StatePanel.Visibility = Visibility.Visible;
                StateHeading.Text = Strings.TargetWindowRecovering;
                ReloadButton.Visibility = Visibility.Collapsed;
                break;
            case TargetWindowState.Failed:
            case TargetWindowState.Blocked:
                StatePanel.Visibility = Visibility.Visible;
                StateHeading.Text = Strings.TargetWindowFailed;
                ReloadButton.Visibility = Visibility.Visible;
                break;
            default:
                StatePanel.Visibility = Visibility.Visible;
                StateHeading.Text = Strings.TargetWindowLoading;
                ReloadButton.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ReloadClick(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.R)
        {
            e.Handled = true;
            ReloadRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Key == Key.W)
        {
            e.Handled = true;
            Close();
        }
    }
}
