using System.Diagnostics;

namespace DshLauncher.Desktop;

/// <summary>
/// The notification-area presence: double click or the menu item opens the
/// independent web dashboard; the menu also carries the exit action. The
/// tooltip reflects live pairing counts pushed by the hub.
/// </summary>
public sealed class TrayHost : IDisposable
{
    public TrayHost(string productName, string dashboardUrl)
    {
        _productName = productName;
        _dashboardUrl = dashboardUrl;
        _menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = _menu.Items.Add("打开管理页面");
        openItem.Click += static (_, _) => _instance?.OpenDashboard();
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
        _icon.DoubleClick += static (_, _) => _instance?.OpenDashboard();
    }

    public event Action? ExitRequested;

    public void UpdateStatus(string status)
    {
        _icon.Text = Truncate($"{_productName} — {status}");
    }

    public void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_dashboardUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
        }
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

    private void RaiseExit() => ExitRequested?.Invoke();

    private static TrayHost? _instance;
    private readonly string _productName;
    private readonly string _dashboardUrl;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private bool _disposed;
}
