using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Platform.Windows;
using MessageBox = System.Windows.MessageBox;

namespace DshLauncher.Desktop;

/// <summary>
/// The standalone management client: target directory, pairing, keep-alive
/// controls and the entry point to embedded remote UI windows. Live hub
/// state arrives on worker threads and is marshaled onto the UI thread.
/// </summary>
public partial class ManagementWindow : Window
{
    public ManagementWindow(PairingHub hub, ApplicationDataStore applicationData, TrayHost tray)
    {
        InitializeComponent();
        _hub = hub;
        _applicationData = applicationData;
        _tray = tray;
        TargetsList.ItemsSource = _rows;
        hub.Changed += OnHubChanged;
        Closed += static (sender, _) => ((ManagementWindow)sender!).Detach();
        _ = RefreshInitialAsync();
    }

    private async Task RefreshInitialAsync()
    {
        // Never block the UI thread on the hub gate: the constructor already
        // returned, and the first frame arrives through the same marshal as
        // every later change. The stored close behavior (if any) rides the
        // same async path; until it lands, closing simply asks. A preference
        // read failure degrades to Ask instead of killing startup.
        try
        {
            _closeBehavior = DescribeCloseBehavior(
                await _applicationData.ReadCloseBehaviorAsync().ConfigureAwait(true));
        }
        catch (Exception exception) when (exception is HubStorageException or
                                          ApplicationDataOwnershipException or
                                          IOException or
                                          UnauthorizedAccessException)
        {
            _closeBehavior = CloseBehavior.Ask;
        }

        ApplyTrayCloseMenu();
        var snapshot = await _hub.GetSnapshotAsync().ConfigureAwait(true);
        RefreshRows(snapshot);
    }

    private readonly PairingHub _hub;
    private readonly ApplicationDataStore _applicationData;
    private readonly TrayHost _tray;
    private readonly ObservableCollection<TargetRow> _rows = [];
    private readonly Dictionary<Guid, RemoteWindow> _remoteWindows = new();
    private bool _allowClose;
    private bool _minimizeHintShown;
    private CloseBehavior _closeBehavior = CloseBehavior.Ask;

    /// <summary>Stable single-line tokens persisted in close-behavior.txt;
    /// unknown or missing content always degrades to Ask.</summary>
    private static string CloseBehaviorToken(CloseBehavior behavior) => behavior switch
    {
        CloseBehavior.MinimizeToTray => "minimize-tray",
        CloseBehavior.Exit => "exit",
        _ => "ask",
    };

    private static CloseBehavior DescribeCloseBehavior(string? stored) => stored switch
    {
        "minimize-tray" => CloseBehavior.MinimizeToTray,
        "exit" => CloseBehavior.Exit,
        _ => CloseBehavior.Ask,
    };

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void AllowClose()
    {
        _allowClose = true;
        foreach (var remote in _remoteWindows.Values)
        {
            remote.AllowClose();
        }
    }

    private void Detach()
    {
        _hub.Changed -= OnHubChanged;
    }

    private void OnHubChanged(HubSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => RefreshRows(snapshot));

    private void RefreshRows(HubSnapshot snapshot)
    {
        var selectedId = (TargetsList.SelectedItem as TargetRow)?.TargetId;
        var newTargets = snapshot.Targets;
        var newIds = new HashSet<Guid>(newTargets.Count);
        foreach (var target in newTargets)
        {
            newIds.Add(target.TargetId);
        }

        // Remove rows no longer present in the snapshot.
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (!newIds.Contains(_rows[i].TargetId))
            {
                _rows.RemoveAt(i);
            }
        }

        // Update existing rows and insert new ones in snapshot order.
        var existingIndex = 0;
        foreach (var target in newTargets)
        {
            var matchIndex = -1;
            for (var i = existingIndex; i < _rows.Count; i++)
            {
                if (_rows[i].TargetId == target.TargetId)
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                // Move to correct position if needed.
                if (matchIndex != existingIndex)
                {
                    var row = _rows[matchIndex];
                    _rows.RemoveAt(matchIndex);
                    _rows.Insert(existingIndex, row);
                }

                _rows[existingIndex].UpdateFrom(target);
            }
            else
            {
                _rows.Insert(existingIndex, new TargetRow(target));
            }

            existingIndex++;
        }

        if (newTargets.Count == 0)
        {
            HubStatusText.Text = "枢纽运行中 · 心跳间隔 "
                + Math.Round(snapshot.HeartbeatInterval.TotalSeconds) + " 秒 · 尚无目标，点击右上角“添加目标”";
        }
        else
        {
            var online = newTargets.Count(t => t.Pairing == PairingState.Paired && t.Connectivity == ConnectivityState.Online);
            var paired = newTargets.Count(t => t.Pairing == PairingState.Paired);
            HubStatusText.Text = "枢纽运行中 · 心跳间隔 "
                + Math.Round(snapshot.HeartbeatInterval.TotalSeconds) + " 秒 · 已配对 "
                + paired + "（在线 " + online + "）· 共 " + newTargets.Count + " 个目标";
        }

        _tray.UpdateStatus(pairedStatus(snapshot));

        if (selectedId is { } id)
        {
            var restored = _rows.FirstOrDefault(row => row.TargetId == id);
            if (restored is not null)
            {
                TargetsList.SelectedItem = restored;
            }
        }

        static string pairedStatus(HubSnapshot snap)
        {
            var paired = snap.Targets.Count(t => t.Pairing == PairingState.Paired);
            var online = snap.Targets.Count(t => t.Pairing == PairingState.Paired && t.Connectivity == ConnectivityState.Online);
            return "配对 " + paired + "（在线 " + online + "）· 心跳 "
                + Math.Round(snap.HeartbeatInterval.TotalSeconds) + " 秒";
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasTarget = TargetsList.SelectedItem is TargetRow;
        HeartbeatButton.IsEnabled = hasTarget;
        OpenRemoteButton.IsEnabled = hasTarget;
        RePairButton.IsEnabled = hasTarget;
        RenameButton.IsEnabled = hasTarget;
        DeleteButton.IsEnabled = hasTarget;
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OnOpenRemote(sender, e);

    private async void OnAddTarget(object sender, RoutedEventArgs e)
    {
        var dialog = InputDialog.ShowPairingLinkDialog(this);
        if (dialog is null)
        {
            return;
        }

        var result = dialog.PairingLink.Length > 0
            ? await _hub.AddFromPairingLinkAsync(dialog.PairingLink, dialog.DisplayName).ConfigureAwait(true)
            : await _hub.AddEndpointAsync(dialog.BaseUrl, dialog.DisplayName).ConfigureAwait(true);
        ReportResult("添加目标", result);
    }

    private async void OnHeartbeatNow(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is not TargetRow row)
        {
            return;
        }

        var result = await _hub.HeartbeatNowAsync(row.TargetId).ConfigureAwait(true);
        ReportResult("立即心跳", result);
    }

    private async void OnOpenRemote(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is not TargetRow row)
        {
            return;
        }

        await OpenRemoteForTargetAsync(row.TargetId);
    }

    /// <summary>Opens (or focuses) one target's remote window. Shared by the
    /// management list and the sidebar entries inside remote windows.</summary>
    public async Task OpenRemoteForTargetAsync(Guid targetId)
    {
        var row = _rows.FirstOrDefault(candidate => candidate.TargetId == targetId);
        if (row is null)
        {
            return;
        }

        if (_remoteWindows.TryGetValue(row.TargetId, out var existing))
        {
            existing.ShowAndActivate();
            return;
        }

        var url = await _hub.GetRemoteUiUrlAsync(row.TargetId).ConfigureAwait(true);
        if (url.Error is not null || url.Url is null)
        {
            MessageBox.Show(this, "该目标尚未配对或配对已失效，无法打开远程界面。",
                "打开远程界面", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string udfPath;
        try
        {
            udfPath = await _applicationData.PrepareTargetDataAsync(row.TargetId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ApplicationDataOwnershipException or IOException)
        {
            MessageBox.Show(this, "无法准备远程界面数据目录：" + exception.Message,
                "打开远程界面", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var remote = new RemoteWindow(
            _hub, row.TargetId, url.Url, row.DisplayName, row.BaseUrl, udfPath, OpenRemoteForTargetAsync);
        remote.Closed += (sender, _) =>
        {
            var window = (RemoteWindow)sender!;
            foreach (var entry in _remoteWindows.Where(entry => entry.Value == window).ToArray())
            {
                _remoteWindows.Remove(entry.Key);
            }
        };
        _remoteWindows[row.TargetId] = remote;
        remote.Show();
    }

    private async void OnRePair(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is not TargetRow row)
        {
            return;
        }

        var link = InputDialog.ShowText(
            this, "重新配对", "新的配对链接（在 Harness 桌面端「远程访问」面板复制）：", multiLine: true);
        if (link is null || link.Trim().Length == 0)
        {
            return;
        }

        var result = await _hub.AttachPairingAsync(row.TargetId, link).ConfigureAwait(true);
        ReportResult("重新配对", result);
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is not TargetRow row)
        {
            return;
        }

        var name = InputDialog.ShowText(this, "重命名", "显示名称（留空恢复为地址显示）：", row.DisplayName, multiLine: false);
        if (name is null)
        {
            return;
        }

        var result = await _hub.RenameAsync(row.TargetId, name).ConfigureAwait(true);
        ReportResult("重命名", result);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (TargetsList.SelectedItem is not TargetRow row)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "确定删除目标「" + row.DisplayName + "」？\n仅删除本机的配对凭据；主机端撤销请在远程访问面板操作。",
            "删除目标",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        if (_remoteWindows.Remove(row.TargetId, out var remote))
        {
            remote.AllowClose();
            remote.Close();
        }

        var result = await _hub.RemoveAsync(row.TargetId).ConfigureAwait(true);
        ReportResult("删除目标", result);
    }

    private void ReportResult(string title, HubOperationResult result)
    {
        if (result.Error is { } error)
        {
            MessageBox.Show(this, DescribeError(error.Code), title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DescribeError(HubErrorCode code) => code switch
    {
        HubErrorCode.InvalidPairingLink => "配对链接格式无效，请检查后重新粘贴。",
        HubErrorCode.InvalidBaseUrl => "Harness 地址无效，应为 http(s)://域名或 IP[:端口]。",
        HubErrorCode.TargetLimitReached => "已达目标数量上限。",
        HubErrorCode.TargetNotFound => "目标不存在，请刷新窗口。",
        HubErrorCode.TargetNotPaired => "该目标尚未配对或配对已失效。",
        HubErrorCode.PairingInvalidToken => "配对令牌无效或已过期，请在主机端刷新二维码。",
        HubErrorCode.PairingTokenUsed => "配对令牌已被使用，请刷新二维码后重试。",
        HubErrorCode.PairingForbidden => "主机端拒绝了配对请求（来源受限）。",
        HubErrorCode.PairingRateLimited => "配对请求过于频繁，请稍后重试。",
        HubErrorCode.PairingUnreachable => "无法连接 Harness 主机，请检查地址与网络。",
        HubErrorCode.PairingFailed => "配对失败，主机端返回异常。",
        HubErrorCode.StorageFailure => "本地目标存储读写失败。",
        _ => "操作失败：" + code,
    };

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // The window button honors the remembered close behavior; Ask shows
        // the choice dialog. Dismissing that dialog keeps the tray path so
        // nothing dies by accident, and the tray menu's 关闭窗口 submenu is
        // always available to inspect or reset the remembered choice.
        e.Cancel = true;
        BeginCloseFlowAsync();
    }

    private async void BeginCloseFlowAsync()
    {
        try
        {
            await RunCloseFlowAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HubStorageException or IOException or UnauthorizedAccessException)
        {
            // A preference read/write failure must never kill the process:
            // fall back to the plain hide-to-tray path.
            MinimizeToTray(showHint: true);
        }
    }

    private async Task RunCloseFlowAsync()
    {
        if (_closeBehavior == CloseBehavior.Ask)
        {
            var choice = CloseChoiceDialog.Show(this);
            if (choice is null)
            {
                MinimizeToTray(showHint: true);
                return;
            }

            _closeBehavior = choice.Behavior;
            if (choice.Remember)
            {
                await PersistCloseBehaviorAsync(_closeBehavior).ConfigureAwait(true);
            }

            ApplyTrayCloseMenu();
            if (_closeBehavior == CloseBehavior.Exit)
            {
                _tray.RequestExit();
                return;
            }

            MinimizeToTray(showHint: true);
            return;
        }

        if (_closeBehavior == CloseBehavior.Exit)
        {
            _tray.RequestExit();
            return;
        }

        MinimizeToTray(showHint: !_minimizeHintShown);
    }

    private void MinimizeToTray(bool showHint)
    {
        Hide();
        if (showHint && !_minimizeHintShown)
        {
            _minimizeHintShown = true;
            _tray.ShowBalloonHint("已最小化到托盘", "配对保活继续运行；双击托盘图标重新打开管理窗口。");
        }
    }

    /// <summary>Syncs the tray's 关闭窗口 submenu with the live behavior so
    /// the remembered choice stays discoverable and resettable.</summary>
    private void ApplyTrayCloseMenu() =>
        _tray.UpdateCloseBehaviorMenu(
            CloseBehaviorToken(_closeBehavior),
            OnCloseBehaviorSelected);

    private async void OnCloseBehaviorSelected(string token)
    {
        try
        {
            _closeBehavior = DescribeCloseBehavior(token);
            await PersistCloseBehaviorAsync(_closeBehavior).ConfigureAwait(true);
            ApplyTrayCloseMenu();
        }
        catch (Exception exception) when (exception is HubStorageException or IOException or UnauthorizedAccessException)
        {
            // The menu choice stays for this session; persisting can wait
            // for the next close or menu use.
        }
    }

    private async Task PersistCloseBehaviorAsync(CloseBehavior behavior) =>
        await _applicationData.WriteCloseBehaviorAsync(CloseBehaviorToken(behavior)).ConfigureAwait(true);

    private sealed class TargetRow : INotifyPropertyChanged
    {
        public TargetRow(TargetSnapshot target)
        {
            TargetId = target.TargetId;
            _displayName = target.EffectiveDisplayName;
            _baseUrl = target.BaseUrl;
            _addressDisplay = DescribeAddress(target.BaseUrl);
            _pairing = target.Pairing;
            _connectivity = target.Connectivity;
            _pairingText = DescribePairing(target.Pairing);
            _connectivityText = DescribeConnectivity(target.Connectivity);
            _lastHeartbeatText = DescribeHeartbeat(target.LastHeartbeatUtc);
            _failureText = DescribeFailure(target.LastFailure, target.ConsecutiveFailures);
            _keepAliveEnabled = target.KeepAliveEnabled;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid TargetId { get; }

        public string DisplayName
        {
            get => _displayName;
            private set => SetField(ref _displayName, value, nameof(DisplayName));
        }

        public string BaseUrl
        {
            get => _baseUrl;
            private set => SetField(ref _baseUrl, value, nameof(BaseUrl));
        }

        public string AddressDisplay
        {
            get => _addressDisplay;
            private set => SetField(ref _addressDisplay, value, nameof(AddressDisplay));
        }

        public PairingState Pairing
        {
            get => _pairing;
            private set => SetField(ref _pairing, value, nameof(Pairing));
        }

        public ConnectivityState Connectivity
        {
            get => _connectivity;
            private set => SetField(ref _connectivity, value, nameof(Connectivity));
        }

        public string PairingText
        {
            get => _pairingText;
            private set => SetField(ref _pairingText, value, nameof(PairingText));
        }

        public string ConnectivityText
        {
            get => _connectivityText;
            private set => SetField(ref _connectivityText, value, nameof(ConnectivityText));
        }

        public string LastHeartbeatText
        {
            get => _lastHeartbeatText;
            private set => SetField(ref _lastHeartbeatText, value, nameof(LastHeartbeatText));
        }

        public string FailureText
        {
            get => _failureText;
            private set
            {
                if (SetField(ref _failureText, value, nameof(FailureText)))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFailure)));
                }
            }
        }

        public bool HasFailure => _failureText.Length > 0;

        public bool KeepAliveEnabled
        {
            get => _keepAliveEnabled;
            private set => SetField(ref _keepAliveEnabled, value, nameof(KeepAliveEnabled));
        }

        /// <summary>Updates all mutable properties from a new snapshot entry,
        /// raising PropertyChanged only for values that actually changed.</summary>
        public void UpdateFrom(TargetSnapshot target)
        {
            DisplayName = target.EffectiveDisplayName;
            BaseUrl = target.BaseUrl;
            AddressDisplay = DescribeAddress(target.BaseUrl);
            Pairing = target.Pairing;
            Connectivity = target.Connectivity;
            PairingText = DescribePairing(target.Pairing);
            ConnectivityText = DescribeConnectivity(target.Connectivity);
            LastHeartbeatText = DescribeHeartbeat(target.LastHeartbeatUtc);
            FailureText = DescribeFailure(target.LastFailure, target.ConsecutiveFailures);
            KeepAliveEnabled = target.KeepAliveEnabled;
        }

        private bool SetField<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private static string DescribePairing(PairingState state) => state switch
        {
            PairingState.Paired => "已配对",
            PairingState.Pairing => "配对中…",
            PairingState.AwaitingPairing => "待配对",
            _ => "已失效",
        };

        private static string DescribeConnectivity(ConnectivityState state) => state switch
        {
            ConnectivityState.Online => "在线",
            ConnectivityState.Offline => "离线",
            _ => "未知",
        };

        private static string DescribeHeartbeat(DateTimeOffset? heartbeatUtc) => heartbeatUtc is { } heartbeat
            ? heartbeat.LocalDateTime.ToString("MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : "—";

        private static string DescribeFailure(string? lastFailure, int consecutiveFailures) => lastFailure is null
            ? string.Empty
            : lastFailure + (consecutiveFailures > 0
                ? " × " + consecutiveFailures
                : string.Empty);

        // Keep the scheme visible: public targets commonly use HTTPS while
        // local Harness instances often use HTTP, and the distinction is
        // important when two targets share the same authority.
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

        private string _displayName;
        private string _baseUrl;
        private string _addressDisplay;
        private PairingState _pairing;
        private ConnectivityState _connectivity;
        private string _pairingText;
        private string _connectivityText;
        private string _lastHeartbeatText;
        private string _failureText;
        private bool _keepAliveEnabled;
    }
}
