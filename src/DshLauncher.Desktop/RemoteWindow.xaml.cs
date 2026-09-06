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

namespace DshLauncher.Desktop;

/// <summary>
/// A shared remote workspace with persistent, per-target browser pages.
/// Each page owns its isolated WebView2 user-data folder.
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
        SidebarList.ItemsSource = _sidebarRows;
        hub.Changed += OnHubChanged;
        Closed += static (sender, _) => ((RemoteWindow)sender!).Detach();
        _ = RefreshSidebarAsync();
        OpenPage(targetId, remoteUrl, displayName, baseUrl, userDataFolderPath);
    }

    private readonly PairingHub _hub;
    private Guid _targetId;
    private readonly Func<Guid, Task> _openTargetRequest;
    private readonly ObservableCollection<RemoteTargetRow> _sidebarRows = [];
    private readonly Dictionary<Guid, RemotePage> _pages = [];
    private bool _detached;
    private bool _updatingSelection;
    private bool _allowClose;
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

    /// <summary>Updates page lifetimes and sidebar status on the UI thread.</summary>
    private void ApplySnapshot(HubSnapshot snapshot)
    {
        if (_detached) return;
        foreach (var page in _pages.Values.ToArray())
        {
            var target = snapshot.Targets.FirstOrDefault(target => target.TargetId == page.TargetId);
            if (target is null || target.Pairing != PairingState.Paired)
            {
                RemovePage(page.TargetId);
            }
            else
            {
                page.DisplayName = target.EffectiveDisplayName;
                page.Tab.Content = page.DisplayName;
            }
        }
        _updatingSelection = true;
        _sidebarRows.Clear();
        foreach (var target in snapshot.Targets)
        {
            var row = new RemoteTargetRow(target) { IsCurrent = target.TargetId == _targetId };
            _sidebarRows.Add(row);
        }

        SidebarList.SelectedItem = _sidebarRows.FirstOrDefault(row => row.IsCurrent);

        _updatingSelection = false;
        UpdateActivePage();

        var paired = snapshot.Targets.Count(t => t.Pairing == PairingState.Paired);
        var online = snapshot.Targets.Count(t => t.Pairing == PairingState.Paired && t.Connectivity == ConnectivityState.Online);
        HubFooterText.Text = "在线 " + online + " · 已配对 " + paired + " · 共 " + snapshot.Targets.Count
            + " · 心跳 " + Math.Round(snapshot.HeartbeatInterval.TotalSeconds) + " 秒";
    }

    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || SidebarList.SelectedItem is not RemoteTargetRow row || row.TargetId == _targetId)
        {
            return;
        }

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

    [LibraryImport("user32.dll")]
    private static partial int ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial int SendMessage(nint hWnd, int message, nint wParam, nint lParam);

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

    public void OpenPage(Guid targetId, string remoteUrl, string displayName, string baseUrl, string userDataFolderPath)
    {
        if (_detached) return;
        if (!_pages.TryGetValue(targetId, out var page))
        {
            page = new RemotePage(targetId, remoteUrl, displayName, baseUrl, userDataFolderPath);
            _pages.Add(targetId, page);
            page.Web.Loaded += (_, _) => EnsurePageInitialized(page);
            PageHost.Children.Add(page.Web);
            PageTabs.Items.Add(page.Tab);
        }
        PageTabs.SelectedItem = page.Tab;
        UpdateActivePage();
        if (page.Web.IsLoaded) EnsurePageInitialized(page);
    }

    private void EnsurePageInitialized(RemotePage page)
    {
        if (page.Initialized || page.Disposed) return;
        page.Initialized = true;
        SetPageState(page, RemoteLoadState.Loading);
        _ = InitializePageAsync(page);
    }

    public void RemovePage(Guid targetId)
    {
        if (!_pages.Remove(targetId, out var page)) return;
        page.Disposed = true;
        PageHost.Children.Remove(page.Web);
        page.Web.Dispose();
        PageTabs.Items.Remove(page.Tab);
        if (PageTabs.SelectedItem is null && PageTabs.Items.Count > 0)
            PageTabs.SelectedIndex = 0;
        UpdateActivePage();
    }

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActivePage();

    private void UpdateActivePage()
    {
        var active = _pages.Values.FirstOrDefault(page => page.Tab == PageTabs.SelectedItem);
        _targetId = active?.TargetId ?? Guid.Empty;
        foreach (var page in _pages.Values)
            page.Web.Visibility = page == active ? Visibility.Visible : Visibility.Hidden;
        foreach (var row in _sidebarRows) row.IsCurrent = row.TargetId == _targetId;
        _updatingSelection = true;
        SidebarList.SelectedItem = _sidebarRows.FirstOrDefault(row => row.IsCurrent);
        _updatingSelection = false;
        Title = active is null ? "远程界面" : "远程界面 — " + active.DisplayName;
        TargetText.Text = active is null ? "选择一个已配对目标" : active.DisplayName + " · " + DescribeAddress(active.BaseUrl);
        SetLoadState(active?.State ?? RemoteLoadState.Loaded);
        if (active is null) StateText.Text = "尚未打开页面";
    }

    private async Task InitializePageAsync(RemotePage page)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: page.UserDataFolderPath).ConfigureAwait(true);
            if (page.Disposed) return;
            await page.Web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (page.Disposed) return;
            var core = page.Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!page.Disposed) page.Web.Source = new Uri(e.Uri);
            };
            page.Web.NavigationStarting += (_, _) => SetPageState(page, RemoteLoadState.Loading);
            page.Web.NavigationCompleted += (_, e) =>
                SetPageState(page, e.IsSuccess ? RemoteLoadState.Loaded : RemoteLoadState.Failed);
            page.Web.Source = new Uri(page.RemoteUrl);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or
                                          InvalidOperationException or FileNotFoundException or COMException)
        {
            if (page.Disposed) return;
            page.Initialized = false;
            SetPageState(page, RemoteLoadState.Failed);
            page.Tab.ToolTip = "浏览器初始化失败：" + exception.Message;
        }
    }

    private void SetPageState(RemotePage page, RemoteLoadState state)
    {
        if (page.Disposed) return;
        page.State = state;
        if (page.TargetId == _targetId) SetLoadState(state);
    }

    private sealed class RemotePage(Guid targetId, string remoteUrl, string displayName, string baseUrl, string userDataFolderPath)
    {
        public Guid TargetId { get; } = targetId;
        public string RemoteUrl { get; } = remoteUrl;
        public string DisplayName { get; set; } = displayName;
        public string BaseUrl { get; } = baseUrl;
        public string UserDataFolderPath { get; } = userDataFolderPath;
        public Microsoft.Web.WebView2.Wpf.WebView2 Web { get; } = new();
        public ListBoxItem Tab { get; } = new() { Content = displayName, ToolTip = baseUrl };
        public bool Initialized { get; set; }
        public bool Disposed { get; set; }
        public RemoteLoadState State { get; set; } = RemoteLoadState.Loading;
    }

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

    private void Detach()
    {
        _detached = true;
        _hub.Changed -= OnHubChanged;
        foreach (var id in _pages.Keys.ToArray()) RemovePage(id);
    }

    private static string DescribeAddress(string baseUrl)
    {
        try
        {
            return new Uri(baseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
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
