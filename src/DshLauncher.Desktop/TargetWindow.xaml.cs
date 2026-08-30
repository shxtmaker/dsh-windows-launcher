using System.Windows;
using System.Windows.Input;
using DshLauncher.Compatibility;
using DshLauncher.Core;
using DshLauncher.Desktop.Resources;
using DshLauncher.WebView;

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

    public event EventHandler? CompatibilityConfirmationRevokeRequested;

    public void SetHostContent(UIElement content) => HostSurface.Content = content;

    public void SetCompatibilityStatus(CompatibilityStatusDto? status)
    {
        if (status is null)
        {
            CompatibilityBanner.Visibility = Visibility.Collapsed;
            CompatibilityStatusButton.Visibility = Visibility.Collapsed;
            CompatibilityPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var limited = status.Level == CompatibilityLevel.Base &&
                      status.PrimaryReason != CompatibilityReasonCode.None;
        CompatibilityBanner.Visibility = limited
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompatibilityStatusButton.Visibility = limited
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompatibilityBannerText.Text = limited
            ? Strings.CompatibilityLimited
            : Strings.CompatibilityReady;
        var purposeText = status.Purposes.Count == 0
            ? Strings.CompatibilityNoExternalPurpose
            : string.Join(Environment.NewLine, status.Purposes.Select(
                static purpose => $"• {purpose}"));
        var ruleText = status.RuleVersions.Count == 0
            ? "-"
            : string.Join(", ", status.RuleVersions);
        CompatibilityDetailText.Text = string.Join(
            Environment.NewLine,
            $"{Strings.CompatibilityLevelLabel}: {status.Level}",
            $"{Strings.CompatibilityReasonLabel}: {status.PrimaryReason}",
            $"{Strings.CompatibilityContractLabel}: {status.ContractVersion}",
            $"{Strings.CompatibilityDescriptorSchemaLabel}: {status.DescriptorSchemaVersion}",
            $"{Strings.CompatibilityRegistryLabel}: {status.RegistryVersion}",
            $"{Strings.CompatibilityRulesLabel}: {ruleText}",
            $"{Strings.CompatibilityDigestLabel}: {status.SnapshotSha256}",
            string.Empty,
            Strings.CompatibilityPurposeLabel,
            purposeText);
        RevokeCompatibilityButton.Visibility = status.Purposes.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

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

    private void CompatibilityDetailsClick(object sender, RoutedEventArgs e) =>
        CompatibilityPanel.Visibility = Visibility.Visible;

    private void CompatibilityPanelCloseClick(object sender, RoutedEventArgs e) =>
        CompatibilityPanel.Visibility = Visibility.Collapsed;

    private void CompatibilityRevokeClick(object sender, RoutedEventArgs e) =>
        CompatibilityConfirmationRevokeRequested?.Invoke(this, EventArgs.Empty);

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
