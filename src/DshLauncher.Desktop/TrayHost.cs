using System.Diagnostics;

namespace DshLauncher.Desktop;

/// <summary>
/// The notification-area presence: double click opens the standalone
/// management window; the menu carries 打开管理窗口, a 关闭窗口 submenu that
/// mirrors (and resets) the remembered close behavior, and 退出. The tooltip
/// reflects live pairing counts pushed by the hub.
/// </summary>
public sealed class TrayHost : IDisposable
{
    public TrayHost(string productName, Action openManagement, Action exitRequested)
    {
        if (_instance is not null)
        {
            throw new InvalidOperationException("TrayHost has already been initialized.");
        }

        _productName = productName;
        _openManagement = openManagement;
        _exitRequested = exitRequested;
        _menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = _menu.Items.Add("打开管理窗口");
        openItem.Click += static (_, _) => _instance?.RaiseOpen();

        // The close-behavior submenu is rebuilt by UpdateCloseBehaviorMenu
        // once the management window knows the stored choice.
        _closeBehaviorItem = new System.Windows.Forms.ToolStripMenuItem("关闭窗口时");
        _menu.Items.Add(_closeBehaviorItem);

        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var exitItem = _menu.Items.Add("退出");
        exitItem.Click += static (_, _) => _instance?.RaiseExit();
        _instance = this;

        var (trayIcon, ownsTrayIcon) = ResolveTrayIcon();
        _trayIcon = trayIcon;
        _ownsTrayIcon = ownsTrayIcon;
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = productName,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += static (_, _) => _instance?.RaiseOpen();
    }

    /// <summary>Resolves the tray icon from the running executable so the
    /// installed product shows its own brand mark; falls back to the generic
    /// application icon when extraction is unavailable.</summary>
    private static (System.Drawing.Icon Icon, bool Owned) ResolveTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (processPath is not null)
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                {
                    return (extracted, true);
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the generic icon below.
        }

        return (System.Drawing.SystemIcons.Application, false);
    }

    public void UpdateStatus(string status)
    {
        _icon.Text = Truncate($"{_productName} — {status}");
    }

    /// <summary>Raises the app-level exit from inside a window flow (the
    /// management close dialog); same path as the tray menu's 退出.</summary>
    public void RequestExit() => RaiseExit();

    /// <summary>Rebuilds the 关闭窗口时 submenu: one entry per behavior with a
    /// check mark on the active one; picking an entry reports its token to
    /// the callback (which persists it) and the menu re-checks.</summary>
    public void UpdateCloseBehaviorMenu(string activeToken, Action<string> tokenSelected)
    {
        _closeBehaviorSelected = tokenSelected;
        _closeBehaviorItem.DropDownItems.Clear();
        foreach (var (token, label) in new[]
                 {
                     ("ask", "每次询问"),
                     ("minimize-tray", "最小化到托盘（不再询问）"),
                     ("exit", "退出程序（不再询问）"),
                 })
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(label)
            {
                Checked = string.Equals(token, activeToken, StringComparison.Ordinal),
            };
            item.Click += static (sender, _) =>
            {
                var menuItem = (System.Windows.Forms.ToolStripMenuItem)sender!;
                _instance?._closeBehaviorSelected?.Invoke(TokenOfLabel(menuItem.Text ?? string.Empty));
            };
            _closeBehaviorItem.DropDownItems.Add(item);
        }

        static string TokenOfLabel(string label) => label switch
        {
            "最小化到托盘（不再询问）" => "minimize-tray",
            "退出程序（不再询问）" => "exit",
            _ => "ask",
        };
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
        if (_ownsTrayIcon)
        {
            _trayIcon.Dispose();
        }
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
    private readonly System.Windows.Forms.ToolStripMenuItem _closeBehaviorItem;
    private readonly System.Drawing.Icon _trayIcon;
    private readonly bool _ownsTrayIcon;
    private Action<string>? _closeBehaviorSelected;
    private bool _disposed;
}
