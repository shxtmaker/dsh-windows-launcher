using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using Microsoft.Web.WebView2.Core;

using MessageBox = System.Windows.MessageBox;

namespace DshLauncher.Desktop;

/// <summary>
/// The embedded display of one target's remote UI: the harness's
/// cookieless pair-app entry (pair-app?device=…) hosted in an in-process
/// WebView2 with a per-target user-data folder. A collapsible sidebar
/// mirrors the hub's whole target directory — address plus live pairing
/// and connectivity — and selecting another entry asks the owner window
/// to open that target's own remote window (the per-target user-data
/// folder forbids swapping the view in place). Nothing here launches an
/// external browser; new-window requests are folded back into this view.
/// </summary>
public partial class RemoteWindow : Window
{
    public RemoteWindow(
        PairingHub hub,
        Guid targetId,
        string remoteUrl,
        string displayName,
        string baseUrl,
        string userDataFolderPath,
        Func<Guid, Task> openTargetRequest)
    {
        InitializeComponent();
        _hub = hub;
        _targetId = targetId;
        _openTargetRequest = openTargetRequest;
        Title = "远程界面 — " + displayName;
        TargetText.Text = displayName + " · " + DescribeAddress(baseUrl);
        _remoteUrl = remoteUrl;
        _userDataFolderPath = userDataFolderPath;
        SetLoadState(RemoteLoadState.Loading);
        SidebarList.ItemsSource = _sidebarRows;
        hub.Changed += OnHubChanged;
        Closed += static (sender, _) => ((RemoteWindow)sender!).Detach();
        _ = RefreshSidebarAsync();
        Loaded += OnLoaded;
    }

    private readonly PairingHub _hub;
    private readonly Guid _targetId;
    private readonly Func<Guid, Task> _openTargetRequest;
    private readonly ObservableCollection<RemoteTargetRow> _sidebarRows = [];
    private readonly string _remoteUrl;
    private readonly string _userDataFolderPath;
    private bool _allowClose;
    private bool _initialized;
    private bool _sidebarExpanded = true;

    private const double SidebarWidth = 236;

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

    private async Task RefreshSidebarAsync()
    {
        var snapshot = await _hub.GetSnapshotAsync().ConfigureAwait(true);
        ApplySnapshot(snapshot);
    }

    private void OnHubChanged(HubSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));

    /// <summary>Rebuilds the sidebar from a hub snapshot; runs on the UI
    /// thread. The selection is pinned to this window's own target so the
    /// highlight always marks the view being displayed.</summary>
    private void ApplySnapshot(HubSnapshot snapshot)
    {
        _sidebarRows.Clear();
        foreach (var target in snapshot.Targets)
        {
            var row = new RemoteTargetRow(target) { IsCurrent = target.TargetId == _targetId };
            _sidebarRows.Add(row);
        }

        SidebarList.SelectedItem = _sidebarRows.FirstOrDefault(row => row.IsCurrent);

        var paired = snapshot.Targets.Count(t => t.Pairing == PairingState.Paired);
        var online = snapshot.Targets.Count(t => t.Pairing == PairingState.Paired && t.Connectivity == ConnectivityState.Online);
        HubFooterText.Text = "在线 " + online + " · 已配对 " + paired + " · 共 " + snapshot.Targets.Count
            + " · 心跳 " + Math.Round(snapshot.HeartbeatInterval.TotalSeconds) + " 秒";
    }

    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is not RemoteTargetRow row || row.TargetId == _targetId)
        {
            return;
        }

        // Opening another target yields its own window; restore the visual
        // highlight to the target this window still displays.
        _ = _openTargetRequest(row.TargetId);
        SidebarList.SelectedItem = _sidebarRows.FirstOrDefault(candidate => candidate.IsCurrent);
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        if (_sidebarExpanded)
        {
            CollapseSidebar();
        }
        else
        {
            ExpandSidebar();
        }
    }

    // The system title bar is folded into the in-content top bar; these
    // handlers supply the caption gestures (drag, double-click restore) and
    // the window buttons on its right edge.
    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        // Same-size glyph swap: square for maximize, double frame for restore.
        MaximizeGlyph.Data = Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M4.5,7.5 H13.5 V13.5 H4.5 Z M7,7.5 V5 H15.5 V13.5 H13.5"
                : "M4.5,4.5 H13.5 V13.5 H4.5 Z");
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnCaptionMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        // Double-click on the caption toggles maximize. ClickCount arrives
        // from the underlying WM_LBUTTONDBLCLK, so no manual timing needed.
        if (e.ClickCount == 2)
        {
            OnMaximizeWindow(sender, e);
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Dragging a maximized window restores it under the cursor.
            var point = PointToScreen(e.GetPosition(this));
            double ratio = point.X / SystemParameters.PrimaryScreenWidth;
            Top = point.Y - Height / 2;
            Left = Math.Max(point.X - Width * ratio, -Width * (ratio - 1));
            WindowState = WindowState.Normal;
        }

        _ = ReleaseCapture();
        _ = SendMessage(new WindowInteropHelper(this).Handle, WmNclButtonDown, HtCaption, 0);
    }

    private void OnCaptionMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    private void OnCaptionMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
    }

    private const int WmNclButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    [DllImport("user32.dll")]
    private static extern int ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern int SendMessage(nint hWnd, int message, nint wParam, nint lParam);

    private void CollapseSidebar()
    {
        _sidebarExpanded = false;
        var animation = CollapseAnimation(0);
        animation.Completed += (_, _) =>
        {
            if (!_sidebarExpanded)
            {
                Sidebar.BeginAnimation(WidthProperty, null);
                Sidebar.Visibility = Visibility.Collapsed;
            }
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private void ExpandSidebar()
    {
        _sidebarExpanded = true;
        Sidebar.Visibility = Visibility.Visible;
        var animation = CollapseAnimation(SidebarWidth);
        animation.Completed += (_, _) =>
        {
            if (_sidebarExpanded)
            {
                Sidebar.BeginAnimation(WidthProperty, null);
                Sidebar.Width = SidebarWidth;
            }
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private static CubicEase SidebarEase() => new() { EasingMode = EasingMode.EaseInOut };

    private static DoubleAnimation CollapseAnimation(double to) =>
        new(to, TimeSpan.FromMilliseconds(190)) { EasingFunction = SidebarEase() };

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
            SetLoadState(RemoteLoadState.Failed);
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

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) =>
        SetLoadState(e.IsSuccess ? RemoteLoadState.Loaded : RemoteLoadState.Failed);

    private void SetLoadState(RemoteLoadState state)
    {
        StateDot.Fill = state switch
        {
            RemoteLoadState.Loaded => UiPalette.SuccessBrush,
            RemoteLoadState.Failed => UiPalette.DangerBrush,
            _ => UiPalette.AccentBrush,
        };
        StateText.Text = state switch
        {
            RemoteLoadState.Loaded => "已加载",
            RemoteLoadState.Failed => "加载失败（检查 Harness 是否在线）",
            _ => "正在加载…",
        };
    }

    private enum RemoteLoadState
    {
        Loading,
        Loaded,
        Failed,
    }

    private void Detach() => _hub.Changed -= OnHubChanged;

    private static string DescribeAddress(string baseUrl)
    {
        try
        {
            return new Uri(baseUrl, UriKind.Absolute).Authority;
        }
        catch (UriFormatException)
        {
            return baseUrl;
        }
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

    /// <summary>One sidebar entry: the hub snapshot facts the sidebar shows,
    /// plus a current marker the template turns into the accent bar.</summary>
    private sealed class RemoteTargetRow : INotifyPropertyChanged
    {
        public RemoteTargetRow(TargetSnapshot target)
        {
            TargetId = target.TargetId;
            DisplayName = target.EffectiveDisplayName;
            BaseUrl = target.BaseUrl;
            AddressDisplay = DescribeAddress(target.BaseUrl);
            Pairing = target.Pairing;
            Connectivity = target.Connectivity;
            PairingLabel = target.Pairing switch
            {
                PairingState.Pairing => "配对中…",
                PairingState.AwaitingPairing => "待配对",
                PairingState.Revoked => "已失效",
                _ => string.Empty,
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid TargetId { get; }

        public string DisplayName { get; }

        public string BaseUrl { get; }

        public string AddressDisplay { get; }

        public PairingState Pairing { get; }

        public ConnectivityState Connectivity { get; }

        public string PairingLabel { get; }

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value)
                {
                    return;
                }

                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        private bool _isCurrent;
    }
}
