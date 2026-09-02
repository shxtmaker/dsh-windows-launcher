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
        // every later change.
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
        _rows.Clear();
        foreach (var target in snapshot.Targets)
        {
            _rows.Add(new TargetRow(target));
        }

        if (snapshot.Targets.Count == 0)
        {
            HubStatusText.Text = "枢纽运行中 · 心跳间隔 "
                + Math.Round(snapshot.HeartbeatInterval.TotalSeconds) + " 秒 · 尚无目标，点击右上角“添加目标”";
        }
        else
        {
            var online = snapshot.Targets.Count(t => t.Pairing == PairingState.Paired && t.Connectivity == ConnectivityState.Online);
            var paired = snapshot.Targets.Count(t => t.Pairing == PairingState.Paired);
            HubStatusText.Text = "枢纽运行中 · 心跳间隔 "
                + Math.Round(snapshot.HeartbeatInterval.TotalSeconds) + " 秒 · 已配对 "
                + paired + "（在线 " + online + "）· 共 " + snapshot.Targets.Count + " 个目标";
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
        HubErrorCode.InvalidBaseUrl => "Harness 地址无效，应为 http(s)://主机:端口。",
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

        // Closing the window keeps the hub (and every keep-alive loop) running
        // in the tray; the tray menu's 退出 is the real exit.
        e.Cancel = true;
        Hide();
        if (!_minimizeHintShown)
        {
            _minimizeHintShown = true;
            _tray.ShowBalloonHint("已最小化到托盘", "配对保活继续运行；双击托盘图标重新打开管理窗口。");
        }
    }

    private sealed class TargetRow
    {
        public TargetRow(TargetSnapshot target)
        {
            TargetId = target.TargetId;
            DisplayName = target.EffectiveDisplayName;
            BaseUrl = target.BaseUrl;
            AddressDisplay = DescribeAddress(target.BaseUrl);
            Pairing = target.Pairing;
            Connectivity = target.Connectivity;
            PairingText = target.Pairing switch
            {
                PairingState.Paired => "已配对",
                PairingState.Pairing => "配对中…",
                PairingState.AwaitingPairing => "待配对",
                _ => "已失效",
            };
            ConnectivityText = target.Connectivity switch
            {
                ConnectivityState.Online => "在线",
                ConnectivityState.Offline => "离线",
                _ => "未知",
            };
            LastHeartbeatText = target.LastHeartbeatUtc is { } heartbeat
                ? heartbeat.ToLocalTime().ToString("MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : "—";
            FailureText = target.LastFailure is null
                ? string.Empty
                : target.LastFailure + (target.ConsecutiveFailures > 0
                    ? " × " + target.ConsecutiveFailures
                    : string.Empty);
            KeepAliveEnabled = target.KeepAliveEnabled;
        }

        public Guid TargetId { get; }

        public string DisplayName { get; }

        public string BaseUrl { get; }

        public string AddressDisplay { get; }

        public PairingState Pairing { get; }

        public ConnectivityState Connectivity { get; }

        public string PairingText { get; }

        public string ConnectivityText { get; }

        public string LastHeartbeatText { get; }

        public string FailureText { get; }

        public bool HasFailure => FailureText.Length > 0;

        public bool KeepAliveEnabled { get; }

        // Rows show the bare host:port; the scheme never varies between
        // targets in practice and only adds noise.
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
    }
}
