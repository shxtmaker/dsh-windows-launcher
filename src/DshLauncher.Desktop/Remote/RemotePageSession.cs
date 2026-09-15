using System.IO;
using System.Runtime.InteropServices;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using DshLauncher.Desktop.Attachments;
using DshLauncher.Platform.Windows.Attachments;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshLauncher.Desktop.Remote;

/// <summary>单个目标页面的加载状态；只用于窗口顶栏的状态点与文字。</summary>
public enum RemotePageLoadState
{
    /// <summary>尚未开始初始化。</summary>
    Pending,

    /// <summary>正在加载（导航进行中）。</summary>
    Loading,

    /// <summary>已加载完成。</summary>
    Loaded,

    /// <summary>加载或浏览器初始化失败。</summary>
    Failed,
}

/// <summary>
/// 一个目标页面的<b>局部</b>会话（D15）：拥有该页面的 WebView2 实例、隔离用户数据目录、
/// 页面代际、消息通道与取消令牌，并独占注册该页面的 WebView2 生命周期事件。
///
/// 边界：
/// <list type="bullet">
/// <item>每目标一个实例、一个 <see cref="WebView2"/>、一个 UDF；<b>没有</b>全局 WebView 或
/// 全局 Cookie 容器，也没有跨页面共享的环境对象；</item>
/// <item>判定规则全部委托给 Core 的 <see cref="RemotePageSessionState"/>（Linux 已测），
/// 本类只负责把真实 WebView2 事件翻译成规则输入，并按结论更新 UI 状态；</item>
/// <item>窗口/标签页/侧栏仍由 <see cref="RemoteWindow"/> 拥有；本类不引用窗口类型。</item>
/// </list>
///
/// 事件与规则的对应（真实事件顺序属 WindowsPending，见
/// <c>RemotePageSession.WindowsPending.md</c>）：
/// <list type="bullet">
/// <item><c>NavigationStarting</c>（顶层文档）→ <see cref="RemotePageSessionState.BeginNavigation"/>：
/// 旧代际能力<b>立刻</b>撤销、旧代际在途工作被取消；</item>
/// <item><c>NavigationCompleted</c> → <see cref="RemotePageSessionState.CompleteMainDocumentLoad"/>：
/// 只有最终文档来源逐字等于可信来源时才按当前代际授予能力；</item>
/// <item><c>NewWindowRequested</c>（外部链接）→ <see cref="RemotePageSessionState.BeginExternalLink"/>：
/// 不继承原页面的文件能力；</item>
/// <item>子框架/子资源事件<b>不</b>参与能力判定（因此这里不注册
/// <c>WebResourceRequested</c>，也不用 <c>FrameNavigationStarting</c> 授予或撤销能力）。</item>
/// </list>
/// </summary>
public sealed class RemotePageSession : IDisposable
{
    private CoreWebView2? _core;
    private string? _pendingExternalUri;
    private RemoteAttachmentBridge? _attachments;
    private bool _started;
    private bool _disposed;

    /// <summary>为一个目标建立页面会话；WebView2 实例按目标独立创建。</summary>
    public RemotePageSession(
        Guid targetId,
        string remoteUrl,
        string displayName,
        string baseUrl,
        string userDataFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolderPath);
        TargetId = targetId;
        RemoteUrl = remoteUrl;
        DisplayName = displayName;
        BaseUrl = baseUrl;
        UserDataFolderPath = userDataFolderPath;
        Lifecycle = new RemotePageSessionState(RemotePageChannelId.New(), remoteUrl);
    }

    /// <summary>所属目标。</summary>
    public Guid TargetId { get; }

    /// <summary>目标远程页面地址。</summary>
    public string RemoteUrl { get; }

    /// <summary>侧栏/标签页显示的当前名称。</summary>
    public string DisplayName { get; set; }

    /// <summary>目标基地址（仅显示）。</summary>
    public string BaseUrl { get; }

    /// <summary>该页面独占的 WebView2 用户数据目录（保持每目标隔离，不做全局共享）。</summary>
    public string UserDataFolderPath { get; }

    /// <summary>本页面的 WebView2 实例：每目标一个，绝不复用其他页面的实例。</summary>
    public WebView2 Web { get; } = new();

    /// <summary>页面代际 / 通道 / 能力 / 取消的平台中立状态机（Core，Linux 已测）。</summary>
    public RemotePageSessionState Lifecycle { get; }

    /// <summary>本页面专属的消息通道 id。</summary>
    public string ChannelId => Lifecycle.ChannelId;

    /// <summary>
    /// D17 附件桥（原生输入 → 暂存 → Core 传输 → WebView2 WebMessage）。
    /// 它是本会话的<b>组件</b>：生命周期完全由本类驱动，没有第二个生命周期。
    /// 尚未配置暂存适配器时为 <c>null</c>。
    /// </summary>
    public RemoteAttachmentBridge? AttachmentBridge => _attachments;

    /// <summary>当前已授予的文件能力（没有则为 <c>null</c>）。</summary>
    public RemotePageCapability? Capability => Lifecycle.Capability;

    /// <summary>当前加载状态。</summary>
    public RemotePageLoadState State { get; private set; } = RemotePageLoadState.Pending;

    /// <summary>浏览器初始化失败时的标签提示；成功时为 <c>null</c>。</summary>
    public string? FailureToolTip { get; private set; }

    /// <summary>本会话是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>加载状态变化（窗口据此更新状态点/标签提示）。</summary>
    public event Action<RemotePageSession, RemotePageLoadState>? StateChanged;

    /// <summary>
    /// D17：给本页面接上附件桥（每个目标一个暂存适配器 + 一个握手消费者）。
    /// 只接受一次（第二次返回 <c>false</c>，即"第二个消费者"确定拒绝）；
    /// WebView2 若已就绪则立即接通道，否则等初始化完成时自动接上。
    /// </summary>
    public bool AttachAttachments(
        WindowsAttachmentStagingAdapter staging,
        IRemoteBridgeHandshakeSink sink,
        AttachmentConcurrencyGate? gate = null,
        bool nativeCaptureAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(sink);
        if (_disposed || !IsUsable || _attachments is not null)
        {
            return false;
        }

        _attachments = new RemoteAttachmentBridge(this, staging, sink, gate, nativeCaptureAvailable);
        if (_core is not null)
        {
            _attachments.AttachTo(_core);
        }

        return true;
    }

    /// <summary>
    /// 开始初始化：创建<b>本页面自己的</b> CoreWebView2 环境（独立 UDF）、注册事件并导航到目标地址。
    /// 幂等；已释放或已关闭时不做事。
    /// </summary>
    public void Start()
    {
        if (_started || !IsUsable)
        {
            return;
        }

        _started = true;
        SetState(RemotePageLoadState.Loading);
        _ = InitializeAsync();
    }

    /// <summary>
    /// 外部链接导航（<c>NewWindowRequested</c> / <c>target=_blank</c>）：立即撤销本页能力并推进代际，
    /// 再让同一个 WebView 导航过去（不新开窗口）。
    /// </summary>
    public void OpenExternalLink(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsUsable)
        {
            return;
        }

        Lifecycle.BeginExternalLink(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
        {
            SetState(RemotePageLoadState.Failed);
            return;
        }

        _pendingExternalUri = uri;
        SetState(RemotePageLoadState.Loading);
        Web.Source = target;
    }

    /// <summary>窗口隐藏 / 标签页切走：拒绝新操作，但保留在途工作（方案：已授权任务继续绑定原草稿）。</summary>
    public bool SuspendForNewWork(RemotePageSuspensionReason reason) => Lifecycle.SuspendForNewWork(reason);

    /// <summary>页面重新可见且是当前标签页：恢复接受新操作。</summary>
    public bool ResumeNewWork() => Lifecycle.ResumeNewWork();

    /// <summary>目标删除 / 凭据吊销 / 应用退出：取消本页在途工作并关闭会话（幂等）。</summary>
    public RemotePageCancellation Close(RemotePageCancellationTrigger trigger)
    {
        // D17：先终止附件桥（取消在途批次、释放快照与 WebMessage 监听），再关本页会话。
        _attachments?.Close("session-closed");
        var cancellation = Lifecycle.Close(trigger);
        DetachCoreEvents();
        return cancellation;
    }

    /// <summary>判定一条来自本页桥的消息（D16 接入点）：跨页面通道或旧代际一律拒绝。</summary>
    public RemotePageMessageAdmission AdmitMessage(RemotePageInboundMessage message) =>
        Lifecycle.AdmitMessage(message);

    /// <summary>判定能否在本页开始一次新操作（例如一次新的粘贴）。</summary>
    public RemotePageWorkAdmission AdmitNewWork() => Lifecycle.AdmitNewWork();

    /// <summary>释放本页面：关闭会话并销毁自己的 WebView2。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close(RemotePageCancellationTrigger.ApplicationExit);
        _attachments?.Dispose();
        _attachments = null;
        Web.Dispose();
    }

    private bool IsUsable => !_disposed && Lifecycle.Phase != RemotePagePhase.Closed;

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: UserDataFolderPath).ConfigureAwait(true);
            if (!IsUsable)
            {
                return;
            }

            await Web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (!IsUsable)
            {
                return;
            }

            var core = Web.CoreWebView2;
            _core = core;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // D16：外部拖放一律不交给页面，由窗口的原生手势路由处理（见 NativePasteGestureRouter）。
            // 页面因此拿不到"用户拖进来的本机文件"，也就不存在"网页读取任意本机文件"的入口。
            Web.AllowExternalDrop = false;
            core.NewWindowRequested += OnNewWindowRequested;
            Web.NavigationStarting += OnNavigationStarting;
            Web.NavigationCompleted += OnNavigationCompleted;
            // D17：WebView2 就绪后再接附件通道（WebMessage 发送必须在 UI 线程上）。
            _attachments?.AttachTo(core);
            Web.Source = new Uri(RemoteUrl);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or
                                          InvalidOperationException or FileNotFoundException or COMException)
        {
            if (!IsUsable)
            {
                return;
            }

            _started = false;
            FailureToolTip = "浏览器初始化失败：" + exception.Message;
            SetState(RemotePageLoadState.Failed);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsUsable)
        {
            return;
        }

        if (_pendingExternalUri is not null && string.Equals(_pendingExternalUri, e.Uri, StringComparison.Ordinal))
        {
            // OpenExternalLink 已经撤销过能力并推进了代际，这里不重复推进。
            _pendingExternalUri = null;
        }
        else
        {
            Lifecycle.BeginNavigation(e.Uri, RemoteNavigationKind.MainDocument);
        }

        // D17：导航一开始就作废附件桥的本代际身份与资源（不发帧给正在卸载的旧文档）。
        _attachments?.OnPageAdvanced();
        SetState(RemotePageLoadState.Loading);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsUsable)
        {
            return;
        }

        // 判据是最终文档 URL（含服务器重定向之后），不是发起导航时的 URL。
        var capability = Lifecycle.CompleteMainDocumentLoad(Web.Source?.AbsoluteUri);
        if (capability.Granted)
        {
            // D17：能力已按当前代际授予，宿主开始握手（hello + capabilities）。
            _attachments?.OnMainDocumentLoaded();
        }
        else
        {
            _attachments?.OnCapabilityLost();
        }

        SetState(e.IsSuccess ? RemotePageLoadState.Loaded : RemotePageLoadState.Failed);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsUsable)
        {
            OpenExternalLink(e.Uri);
        }
    }

    private void DetachCoreEvents()
    {
        Web.NavigationStarting -= OnNavigationStarting;
        Web.NavigationCompleted -= OnNavigationCompleted;
        if (_core is not null)
        {
            _core.NewWindowRequested -= OnNewWindowRequested;
            _core = null;
        }
    }

    private void SetState(RemotePageLoadState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
