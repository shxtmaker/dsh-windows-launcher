using System.Windows;

namespace DshLauncher.Desktop.Dialogs;

public partial class TrustDialog : Window
{
    public TrustDialog(string endpointAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointAuthority);
        InitializeComponent();
        EndpointText.Text = endpointAuthority;
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (LanCheck.IsChecked == true &&
            FirewallCheck.IsChecked == true &&
            PrivilegeCheck.IsChecked == true)
        {
            DialogResult = true;
        }
    }
}
