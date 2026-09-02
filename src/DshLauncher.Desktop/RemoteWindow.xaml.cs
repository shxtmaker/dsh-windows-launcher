using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

using MessageBox = System.Windows.MessageBox;

namespace DshLauncher.Desktop;

/// <summary>
/// The embedded display of one target's remote UI: the harness's
/// cookieless pair-app entry (pair-app?device=…) hosted in an in-process
/// WebView2 with a per-target user-data folder. Nothing here launches an
/// external browser; new-window requests are folded back into this view.
/// </summary>
public partial class RemoteWindow : Window
{
    public RemoteWindow(string remoteUrl, string displayName, string baseUrl, string userDataFolderPath)
    {
        InitializeComponent();
        Title = "远程界面 — " + displayName;
        TargetText.Text = displayName + " · " + baseUrl;
        _remoteUrl = remoteUrl;
        _userDataFolderPath = userDataFolderPath;
        StateText.Text = "正在加载…";
        Loaded += OnLoaded;
    }

    private readonly string _remoteUrl;
    private readonly string _userDataFolderPath;
    private bool _allowClose;
    private bool _initialized;

    public void AllowClose() => _allowClose = true;

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolderPath).ConfigureAwait(true);
            await Web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += OnNewWindowRequested;
            Web.NavigationCompleted += OnNavigationCompleted;
            Web.Source = new Uri(_remoteUrl);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or
                                          InvalidOperationException or
                                          FileNotFoundException)
        {
            StateText.Text = string.Empty;
            MessageBox.Show(
                this,
                "无法初始化内嵌浏览器组件（WebView2 Runtime）。请安装 Microsoft Edge WebView2 Evergreen Runtime 后重试。\n\n"
                + exception.Message,
                "远程界面",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Keep the user inside the standalone client: popups navigate this
        // view in place instead of spawning an external browser.
        e.Handled = true;
        Web.Source = new Uri(e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        StateText.Text = e.IsSuccess ? "已加载" : "加载失败（检查 Harness 是否在线）";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing a remote window only hides the view; the pairing credential
        // and the keep-alive loop live in the hub, not in this window.
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
