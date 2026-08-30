using System.Windows;

namespace DshLauncher.Desktop.Dialogs;

public partial class RenameTargetDialog : Window
{
    public RenameTargetDialog(string? currentName)
    {
        InitializeComponent();
        DisplayNameBox.Text = currentName ?? string.Empty;
        ContentRendered += (_, _) =>
        {
            DisplayNameBox.Focus();
            DisplayNameBox.SelectAll();
        };
    }

    public string? DisplayName { get; private set; }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        DisplayName = string.IsNullOrEmpty(DisplayNameBox.Text) ? null : DisplayNameBox.Text;
        DialogResult = true;
    }
}
