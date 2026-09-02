using System.Windows;

namespace DshLauncher.Desktop;

/// <summary>
/// The choice shown when the user closes the management window: keep the
/// keep-alive loops running by hiding to the tray, or exit the application
/// for real. Dismissing the dialog itself (Escape, its own title bar X)
/// lands on the tray path, so an accidental dismissal can never terminate
/// the hub.
/// </summary>
public sealed record CloseChoice(bool ExitApplication);

public partial class CloseChoiceDialog : Window
{
    private CloseChoiceDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows the modal choice; a null result means "just hide to
    /// the tray" — the same as pressing the tray-favored button.</summary>
    public static CloseChoice Show(Window owner)
    {
        var dialog = new CloseChoiceDialog { Owner = owner };
        if (dialog.ShowDialog() is not true)
        {
            return new CloseChoice(ExitApplication: false);
        }

        return new CloseChoice(dialog._exit);
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
