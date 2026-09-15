using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Remote;
using DshLauncher.Desktop.Attachments;
using DshLauncher.Desktop.Remote;
using DshLauncher.Platform.Windows.Attachments;

namespace DshLauncher.Desktop;

/// <summary>
/// A shared remote workspace with persistent, per-target browser pages.
/// The window owns the UI only (tabs, sidebar, chrome); every page's browser
/// lifetime, epoch, channel and cancellation belong to its own
/// <see cref="RemotePageSession"/>, which in turn owns its isolated WebView2
/// instance and user-data folder. Nothing is shared globally between targets.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "窗口的生命周期由 WPF 管理；原生粘贴路由在 Closed/Detach 回调里释放。")]
public partial class RemoteWindow : Window, INativePasteWindowHost, IRemoteBridgeHandshakeSink
{
    public RemoteWindow(
        PairingHub hub,
        Guid targetId,
        string remoteUrl,
        string displayName,
        string baseUrl,
        string userDataFolderPath,
        Func<Guid, Task> openTargetRequest,
        Func<Guid, WindowsAttachmentStagingAdapter?>? attachmentStagingFactory = null)
    {
        InitializeComponent();
        _hub = hub;
        _targetId = targetId;
        _openTargetRequest = openTargetRequest;
        _windowToken = "window-" + Guid.NewGuid().ToString("N");
        SidebarList.ItemsSource = _sidebarRows;
        hub.Changed += OnHubChanged;
        Closed += static (sender, _) => ((RemoteWindow)sender!).Detach();
        if (attachmentStagingFactory is not null)
        {
            _pasteRouter = new NativePasteGestureRouter(this, attachmentStagingFactory);
            _pasteRouter.GestureCompleted += OnPasteOutcome;
            _pasteRouter.Attach(this);
        }

        _ = RefreshSidebarAsync();
        OpenPage(targetId, remoteUrl, displayName, baseUrl, userDataFolderPath);
    }

    private readonly PairingHub _hub;
    private readonly string _windowToken;
    private readonly AttachmentComposerContextStore _composerContexts = new();
    // D17：全局并发闸门由窗口拥有（上限 2 个目标同时传输），因此跨目标的背压是真实的。
    private readonly AttachmentConcurrencyGate _attachmentGate = new();
    private NativePasteGestureRouter? _pasteRouter;
    private Guid _targetId;
    private readonly Func<Guid, Task> _openTargetRequest;
    private readonly ObservableCollection<RemoteTargetRow> _sidebarRows = [];
    private readonly Dictionary<Guid, RemotePageView> _pages = [];
    private bool _detached;
    private bool _updatingSelection;
    private bool _allowClose;
    private bool _sidebarExpanded = true;
    private bool _windowShown;

    private const double SidebarWidth = 236;

    /// <summary>The page sessions hosted by this window (one per target page).</summary>
    public IReadOnlyCollection<RemotePageSession> Pages =>
        _pages.Values.Select(view => view.Session).ToArray();

    public void AllowClose() => _allowClose = true;

    public void ShowAndActivate()
    {
        Show();
        _windowShown = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        UpdateActivePage();
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
        foreach (var view in _pages.Values.ToArray())
        {
            var target = snapshot.Targets.FirstOrDefault(target => target.TargetId == view.Session.TargetId);
            if (target is null)
            {
                RemovePage(view.Session.TargetId, RemotePageCancellationTrigger.TargetRemoved);
            }
            else if (target.Pairing != PairingState.Paired)
            {
                // 凭据吊销与目标删除是不同的取消触发，必须分别记录。
                RemovePage(view.Session.TargetId, RemotePageCancellationTrigger.CredentialRevoked);
            }
            else
            {
                view.Session.DisplayName = target.EffectiveDisplayName;
                view.Tab.Content = view.Session.DisplayName;
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
        if (!_pages.TryGetValue(targetId, out var view))
        {
            var session = new RemotePageSession(targetId, remoteUrl, displayName, baseUrl, userDataFolderPath);
            session.StateChanged += OnPageStateChanged;
            session.Web.Loaded += (_, _) => session.Start();
            view = new RemotePageView(session);
            _pages.Add(targetId, view);
            PageHost.Children.Add(session.Web);
            PageTabs.Items.Add(view.Tab);
            _pasteRouter?.AttachPage(session);

            // D17 组合根：暂存适配器就绪时立刻把附件桥接到本页面上。
            // 暂存不可用（磁盘/权限/未配置）时不建桥：传输会以确定码拒绝，而不是假装成功。
            if (_pasteRouter?.StagingFor(targetId) is { } staging
                && session.AttachAttachments(staging, this, _attachmentGate)
                && session.AttachmentBridge is { } bridge)
            {
                bridge.StatusChanged += OnAttachmentStatusChanged;
            }
        }
        PageTabs.SelectedItem = view.Tab;
        UpdateActivePage();
        if (view.Session.Web.IsLoaded) view.Session.Start();
    }

    public void RemovePage(Guid targetId) =>
        RemovePage(targetId, RemotePageCancellationTrigger.TargetRemoved);

    /// <summary>Removes a page and cancels its in-flight work with the given trigger.</summary>
    public void RemovePage(Guid targetId, RemotePageCancellationTrigger trigger)
    {
        if (!_pages.Remove(targetId, out var view)) return;
        view.Session.StateChanged -= OnPageStateChanged;
        if (view.Session.AttachmentBridge is { } bridge)
        {
            bridge.StatusChanged -= OnAttachmentStatusChanged;
        }

        // Cancel the page's own work before tearing its browser down.
        view.Session.Close(trigger);
        _pasteRouter?.DetachPage(view.Session);
        _composerContexts.Invalidate(targetId);
        PageHost.Children.Remove(view.Session.Web);
        view.Session.Dispose();
        PageTabs.Items.Remove(view.Tab);
        if (PageTabs.SelectedItem is null && PageTabs.Items.Count > 0)
        {
            PageTabs.SelectedIndex = 0;
        }

        UpdateActivePage();
    }

    // ————————————————————————————————————————————————————————————
    // D16 原生粘贴手势的窗口侧事实（只提供真实 WPF 状态，不做判定）
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 记录一次页面声明的 composer 上下文（D17 从已验证页面的消息里调用）。
    /// <paramref name="pageAdmitted"/> 必须来自页面会话的准入结果；未准入的声明不记录。
    /// 记录与页面代际绑定，导航后自动回落到"无会话"。
    /// </summary>
    public bool ReportComposerContext(
        Guid targetId,
        RemotePageEpoch epoch,
        ImportContextFacts facts,
        bool pageAdmitted) =>
        _composerContexts.Record(targetId, epoch, facts, pageAdmitted);

    /// <summary>
    /// 记录该目标的截图输入所有者（D17 从已验证页面的 capabilities 消息调用）。
    /// 在握手完成之前，只有位图的粘贴会以 <c>consumer-mode-undecided</c> 被确定拒绝。
    /// </summary>
    public ScreenshotOwnerDecision ReportScreenshotHandshake(
        Guid targetId,
        RemotePageEpoch epoch,
        ScreenshotOwnerFacts facts) =>
        _pasteRouter?.RecordScreenshotHandshake(targetId, epoch, facts)
        ?? new ScreenshotOwnerDecision(ScreenshotOwnerMode.Undecided, ScreenshotOwnerHandshake.UndecidedCode, "窗口未配置原生粘贴路由");

    // ————————————————————————————————————————————————————————————
    // D17 握手消费者：解析出的 capabilities/context 直接进入 D16 的真实台账
    // ————————————————————————————————————————————————————————————

    /// <summary>D17：截图所有者握手结论进入该目标的截图模式台账（决定后续位图粘贴走哪条路）。</summary>
    ScreenshotOwnerDecision IRemoteBridgeHandshakeSink.RecordScreenshotOwner(
        Guid targetId,
        RemotePageEpoch epoch,
        ScreenshotOwnerFacts facts) =>
        ReportScreenshotHandshake(targetId, epoch, facts);

    /// <summary>D17：composer 上下文声明进入该目标的导入闸门台账（未准入的声明不记录）。</summary>
    bool IRemoteBridgeHandshakeSink.RecordComposerContext(
        Guid targetId,
        RemotePageEpoch epoch,
        ImportContextFacts facts,
        bool pageAdmitted) =>
        ReportComposerContext(targetId, epoch, facts, pageAdmitted);

    /// <summary>某个目标当前已准入的 composer 上下文（实机用例与诊断用；未声明时为保守值）。</summary>
    public ImportContextFacts ImportContextFor(RemotePageSession page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _composerContexts.Current(page.TargetId, page.Lifecycle.Epoch);
    }

    /// <summary>某个目标是否已经有已准入的 composer 上下文声明。</summary>
    public bool HasImportContext(RemotePageSession page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _composerContexts.Current(page.TargetId, page.Lifecycle.Epoch).SessionId is not null;
    }

    /// <summary>某个目标的截图所有者台账（实机用例与诊断用）；没有原生作用域时为 <c>null</c>。</summary>
    public ScreenshotOwnerRegistry? PasteOwners(Guid targetId) => _pasteRouter?.OwnersFor(targetId);

    /// <summary>某个目标最近的唯一消费者账目（测试与诊断用）。</summary>
    public NativePasteGestureAccounting? PasteAccounting(Guid targetId, string gestureId) =>
        _pasteRouter?.LedgerFor(targetId)?.Accounting(gestureId);

    bool INativePasteWindowHost.IsWindowVisible => IsVisible && _windowShown;

    bool INativePasteWindowHost.IsWindowActive => IsActive;

    RemotePageSession? INativePasteWindowHost.ActivePage =>
        _pages.Values.FirstOrDefault(view => ReferenceEquals(view.Tab, PageTabs.SelectedItem))?.Session;

    bool INativePasteWindowHost.IsFocusInsideActivePage =>
        ((INativePasteWindowHost)this).ActivePage is { } page && WindowFocusProbe.IsFocusInside(page.Web, this);

    ImportContextFacts INativePasteWindowHost.ImportContext(RemotePageSession page) =>
        _composerContexts.Current(page.TargetId, page.Lifecycle.Epoch);

    string INativePasteWindowHost.FocusToken(RemotePageSession page) =>
        _windowToken + ":" + page.ChannelId;

    /// <summary>一次原生粘贴手势的结局（含确定拒绝码）；实机用例与诊断据此断言。</summary>
    public event Action<NativePasteOutcome>? PasteGestureCompleted;

    private void OnPasteOutcome(NativePasteOutcome outcome)
    {
        PasteGestureCompleted?.Invoke(outcome);

        // D17：原生通路已经产生捕获票据时，立即把这次动作交给该页面的附件桥
        // （暂存 → Core 传输 → WebMessage）。桥不可用时由桥给出确定码，不静默丢弃。
        if (outcome is { Accepted: true, Route: ClipboardImportRoute.NativePaste }
            && _pages.TryGetValue(outcome.TargetId, out var view))
        {
            view.Session.AttachmentBridge?.StartTransfer(outcome);
        }

        if (_detached) return;
        PasteStatusText.Text = outcome switch
        {
            { Accepted: true, Route: ClipboardImportRoute.Bridge } => "截图粘贴交给页面处理",
            { Accepted: true } when outcome.FileCount > 1 => $"已导入 {outcome.FileCount} 个附件",
            { Accepted: true } => "已导入 1 个附件",
            { Recoverable: true } => "无法导入：" + outcome.Code,
            _ => "粘贴失败：" + outcome.Code,
        };
        PasteStatusText.ToolTip = outcome.Detail;
    }

    /// <summary>D17：附件桥的状态（开始/完成/取消/确定失败）显示在顶栏。</summary>
    private void OnAttachmentStatusChanged(RemoteAttachmentBridge bridge)
    {
        if (_detached || bridge.LastStatusText is not { } text)
        {
            return;
        }

        PasteStatusText.Text = text;
        PasteStatusText.ToolTip = bridge.LastStatusCode;
    }

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActivePage();

    private void OnPageStateChanged(RemotePageSession session, RemotePageLoadState state)
    {
        if (_detached) return;
        if (session.FailureToolTip is { } failureToolTip && _pages.TryGetValue(session.TargetId, out var view))
        {
            view.Tab.ToolTip = failureToolTip;
        }

        if (session.TargetId == _targetId)
        {
            SetLoadState(state);
        }
    }

    private void UpdateActivePage()
    {
        var active = _pages.Values.FirstOrDefault(view => view.Tab == PageTabs.SelectedItem);
        _targetId = active?.Session.TargetId ?? Guid.Empty;
        foreach (var view in _pages.Values)
        {
            var isActive = ReferenceEquals(view, active);
            view.Session.Web.Visibility = isActive ? Visibility.Visible : Visibility.Hidden;
            // Plan §5: hiding the window or switching tabs forbids NEW work, while
            // already-authorised work keeps running bound to its draft. Only a
            // navigation, target removal, credential revocation or app exit
            // cancels in-flight work (handled inside the page session).
            if (isActive && _windowShown)
            {
                view.Session.ResumeNewWork();
            }
            else
            {
                view.Session.SuspendForNewWork(
                    isActive ? RemotePageSuspensionReason.WindowHidden : RemotePageSuspensionReason.TabSwitched);
            }
        }

        foreach (var row in _sidebarRows) row.IsCurrent = row.TargetId == _targetId;
        _updatingSelection = true;
        SidebarList.SelectedItem = _sidebarRows.FirstOrDefault(row => row.IsCurrent);
        _updatingSelection = false;
        Title = active is null ? "远程界面" : "远程界面 — " + active.Session.DisplayName;
        TargetText.Text = active is null ? "选择一个已配对目标" : active.Session.DisplayName + " · " + DescribeAddress(active.Session.BaseUrl);
        SetLoadState(active?.Session.State ?? RemotePageLoadState.Loaded);
        if (active is null) StateText.Text = "尚未打开页面";
    }

    /// <summary>One hosted page: its session (browser + lifecycle) and its tab.</summary>
    private sealed class RemotePageView(RemotePageSession session)
    {
        public RemotePageSession Session { get; } = session;

        public ListBoxItem Tab { get; } = new() { Content = session.DisplayName, ToolTip = session.BaseUrl };
    }

    private void SetLoadState(RemotePageLoadState state)
    {
        StateDot.Fill = state switch
        {
            RemotePageLoadState.Loaded => UiPalette.SuccessBrush,
            RemotePageLoadState.Failed => UiPalette.DangerBrush,
            _ => UiPalette.AccentBrush,
        };
        StateText.Text = state switch
        {
            RemotePageLoadState.Loaded => "已加载",
            RemotePageLoadState.Failed => "加载失败（检查 Harness 是否在线）",
            _ => "正在加载…",
        };
    }

    private void Detach()
    {
        _detached = true;
        _hub.Changed -= OnHubChanged;
        foreach (var id in _pages.Keys.ToArray())
        {
            RemovePage(id, RemotePageCancellationTrigger.ApplicationExit);
        }

        _pasteRouter?.Dispose();
        _pasteRouter = null;
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
        _windowShown = false;
        Hide();
        SuspendAllPages();
    }

    private void SuspendAllPages()
    {
        foreach (var view in _pages.Values)
        {
            view.Session.SuspendForNewWork(RemotePageSuspensionReason.WindowHidden);
        }
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
