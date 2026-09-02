using System.Windows;

namespace DshLauncher.Desktop;

/// <summary>The remembered close-window behavior. <c>Ask</c> shows the
/// choice dialog on every close; the other two skip the dialog.</summary>
public enum CloseBehavior
{
    Ask,
    MinimizeToTray,
    Exit,
}

/// <summary>
/// The choice shown when the user closes the management window: keep the
/// keep-alive loops running by hiding to the tray, or exit the application
/// for real — optionally remembered so later closes skip this dialog.
/// Dismissing the dialog itself (Escape, its own title bar X) lands on the
/// tray path without remembering anything, so an accidental dismissal can
/// never terminate the hub nor lock the choice in.
/// </summary>
public sealed record CloseChoice(CloseBehavior Behavior, bool Remember);

public partial class CloseChoiceDialog : Window
{
    private CloseChoiceDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows the modal choice; a null result means "just hide to
    /// the tray, don't remember" — the same as the tray-favored button.</summary>
    public static CloseChoice? Show(Window owner)
    {
        var dialog = new CloseChoiceDialog { Owner = owner };
        if (dialog.ShowDialog() is not true)
        {
            return null;
        }

        return new CloseChoice(
            dialog._exit ? CloseBehavior.Exit : CloseBehavior.MinimizeToTray,
            dialog.RememberChoice.IsChecked is true);
    }

    private bool _exit;

    private void OnMinimizeToTray(object sender, RoutedEventArgs e)
    {
        _exit = false;
        DialogResult = true;
    }

    private void OnExitApp(object sender, RoutedEventArgs e)
    {
        _exit = true;
        DialogResult = true;
    }
}
