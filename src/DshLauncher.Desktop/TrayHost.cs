using System.Diagnostics;

namespace DshLauncher.Desktop;

/// <summary>
/// The notification-area presence: double click opens the standalone
/// management window; the menu carries 打开管理窗口 and 退出. The tooltip
/// reflects live pairing counts pushed by the hub.
/// </summary>
public sealed class TrayHost : IDisposable
{
    public TrayHost(string productName, Action openManagement, Action exitRequested)
    {
        _productName = productName;
        _openManagement = openManagement;
        _exitRequested = exitRequested;
        _menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = _menu.Items.Add("打开管理窗口");
        openItem.Click += static (_, _) => _instance?.RaiseOpen();
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var exitItem = _menu.Items.Add("退出");
        exitItem.Click += static (_, _) => _instance?.RaiseExit();
        _instance = this;

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = productName,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += static (_, _) => _instance?.RaiseOpen();
    }

    public void UpdateStatus(string status)
    {
        _icon.Text = Truncate($"{_productName} — {status}");
    }

    public void ShowBalloonHint(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _instance = null;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private static string Truncate(string text)
    {
        const int maximumTooltipCharacters = 60;
        return text.Length <= maximumTooltipCharacters ? text : text[..(maximumTooltipCharacters - 1)] + "…";
    }

    private void RaiseOpen()
    {
        _openManagement();
    }

    private void RaiseExit() => _exitRequested();

    private static TrayHost? _instance;
    private readonly string _productName;
    private readonly Action _openManagement;
    private readonly Action _exitRequested;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private bool _disposed;
}
