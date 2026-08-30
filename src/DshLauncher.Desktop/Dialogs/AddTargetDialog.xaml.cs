using System.Windows;
using DshLauncher.Desktop.Resources;

namespace DshLauncher.Desktop.Dialogs;

public sealed record AddTargetDialogResult(string Ipv4, string? Port, string? DisplayName);

public partial class AddTargetDialog : Window
{
    public AddTargetDialogResult? Result { get; private set; }

    public AddTargetDialog()
    {
        InitializeComponent();
        ContentRendered += (_, _) => Ipv4Box.Focus();
    }

    private void ContinueClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Ipv4Box.Text))
        {
            ValidationText.Text = Strings.InvalidIpv4;
            Ipv4Box.Focus();
            return;
        }

        Result = new AddTargetDialogResult(
            Ipv4Box.Text,
            string.IsNullOrWhiteSpace(PortBox.Text) ? null : PortBox.Text,
            string.IsNullOrEmpty(DisplayNameBox.Text) ? null : DisplayNameBox.Text);
        DialogResult = true;
    }
}
