using System.Windows;

using MessageBox = System.Windows.MessageBox;

namespace DshLauncher.Desktop;

public sealed record AddTargetInput(string PairingLink, string BaseUrl, string? DisplayName);

/// <summary>
/// A minimal modal text input used by every management action that needs one
/// or two strings from the user (pairing link, display name, harness URL).
/// </summary>
public partial class InputDialog : Window
{
    private InputDialog()
    {
        InitializeComponent();
    }

    private string Value => ValueText.Text.Trim();

    private string Extra => ExtraText.Visibility == Visibility.Visible ? ExtraText.Text.Trim() : string.Empty;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? ShowText(
        Window owner,
        string title,
        string label,
        string initialValue = "",
        bool multiLine = false)
    {
        var dialog = new InputDialog
        {
            Owner = owner,
            Title = title,
        };
        dialog.LabelText.Text = label;
        dialog.ValueText.Text = initialValue;
        if (multiLine)
        {
            dialog.ValueText.AcceptsReturn = true;
            dialog.ValueText.Height = 96;
        }

        return dialog.ShowDialog() is true ? dialog.Value : null;
    }

    public static AddTargetInput? ShowPairingLinkDialog(Window owner)
    {
        var dialog = new InputDialog
        {
            Owner = owner,
            Title = "添加 Harness 目标",
        };
        dialog.LabelText.Text =
            "配对链接（支持局域网、公网域名或公网 IP；推荐从 Harness 桌面端「远程访问」面板复制）：";
        dialog.ValueText.Height = 60;
        dialog.ValueText.AcceptsReturn = true;
        dialog.ExtraLabelText.Visibility = Visibility.Visible;
        dialog.ExtraText.Visibility = Visibility.Visible;
        dialog.HintText.Text = "或者仅填地址（http(s)://域名或 IP[:端口]），稍后配对；公网建议使用 HTTPS。";

        if (dialog.ShowDialog() is not true)
        {
            return null;
        }

        var value = dialog.Value;
        if (value.Length == 0)
        {
            MessageBox.Show(dialog, "请粘贴配对链接，或填写 Harness 地址。", "添加目标",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var displayName = dialog.Extra.Length > 0 ? dialog.Extra : null;

        // A pairing link is anything that looks like the remote access entry
        // page; everything else is treated as a bare origin address. The hub
        // performs the strict validation either way.
        return value.Contains("/pair-accept?", StringComparison.OrdinalIgnoreCase)
            ? new AddTargetInput(value, string.Empty, displayName)
            : new AddTargetInput(string.Empty, value, displayName);
    }
}
