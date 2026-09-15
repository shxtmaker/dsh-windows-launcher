namespace DshLauncher.Core.Remote;

/// <summary>页面会话的稳定判定码（只在本机使用，不进线协议）。</summary>
public static class RemotePageSessionCodes
{
    /// <summary>能力已按当前代际授予。</summary>
    public const string CapabilityGranted = "capability-granted";

    /// <summary>当前没有能力（尚未验证、来源不符或从未授予）。</summary>
    public const string CapabilityAbsent = "capability-absent";

    /// <summary>导航开始，旧代际能力立即撤销。</summary>
    public const string CapabilityRevokedNavigation = "capability-revoked-navigation";

    /// <summary>会话已关闭，能力撤销且不再授予。</summary>
    public const string CapabilityRevokedClosed = "capability-revoked-closed";

    /// <summary>消息被接受。</summary>
    public const string Accepted = "accepted";

    /// <summary>会话已关闭（目标删除 / 凭据吊销 / 应用退出）。</summary>
    public const string SessionClosed = "session-closed";

    /// <summary>消息来自另一个页面的通道。</summary>
    public const string MessageChannelMismatch = "message-channel-mismatch";

    /// <summary>消息属于已失效的旧代际（迟到消息）。</summary>
    public const string MessageEpochStale = "message-epoch-stale";

    /// <summary>消息所属代际正确，但当前没有文件能力。</summary>
    public const string MessageCapabilityAbsent = "message-capability-absent";

    /// <summary>新操作被接受。</summary>
    public const string WorkAccepted = "work-accepted";

    /// <summary>窗口隐藏或切走标签页期间拒绝<b>新</b>操作。</summary>
    public const string WorkPageSuspended = "work-page-suspended";

    /// <summary>没有能力，不能开始新操作。</summary>
    public const string WorkCapabilityAbsent = "work-capability-absent";

    /// <summary>导航开始触发取消。</summary>
    public const string NavigationStarted = "navigation-started";

    /// <summary>目标被移除触发取消。</summary>
    public const string TargetRemoved = "target-removed";

    /// <summary>凭据被吊销触发取消。</summary>
    public const string CredentialRevoked = "credential-revoked";

    /// <summary>应用退出触发取消。</summary>
    public const string ApplicationExit = "application-exit";
}

/// <summary>页面会话所处的阶段。</summary>
public enum RemotePagePhase
{
    /// <summary>正在等待主文档完成；此时没有能力，也不能开始新操作。</summary>
    AwaitingDocument,

    /// <summary>主文档已验证且页面可见可交互；可以开始新操作。</summary>
    Active,

    /// <summary>窗口隐藏或标签页切走：拒绝新操作，但既有在途操作继续绑定原草稿。</summary>
    Suspended,

    /// <summary>会话已关闭：目标删除 / 凭据吊销 / 应用退出。所有迟到结果一律拒绝。</summary>
    Closed,
}

/// <summary>拒绝新操作的原因（对应方案"隐藏窗口后禁止新操作"）。</summary>
public enum RemotePageSuspensionReason
{
    /// <summary>整个远程窗口被隐藏。</summary>
    WindowHidden,

    /// <summary>另一个目标的标签页成为当前页。</summary>
    TabSwitched,
}

/// <summary>取消页面上在途工作的触发条件（D15 步骤 3）。</summary>
public enum RemotePageCancellationTrigger
{
    /// <summary>主文档导航开始：撤销旧代际能力并取消绑定旧代际的工作。</summary>
    NavigationStarted,

    /// <summary>目标被移除。</summary>
    TargetRemoved,

    /// <summary>配对凭据被吊销。</summary>
    CredentialRevoked,

    /// <summary>应用退出（远程窗口关闭 / 进程收尾）。</summary>
    ApplicationExit,
}

/// <summary>
/// 页面代际。每次主文档导航开始时递增；代际是拒绝迟到消息的唯一依据。
/// </summary>
public readonly record struct RemotePageEpoch(long Value)
{
    /// <summary>页面打开时的第一个代际。</summary>
    public static RemotePageEpoch Initial => new(1);

    /// <summary>下一个代际。</summary>
    public RemotePageEpoch Next() => new(Value + 1);
}

/// <summary>
/// 一次已授予的文件能力：它与「通道 + 代际 + 来源」绑定，三者任一变化都必须重新验证。
/// </summary>
public readonly record struct RemotePageCapability(string ChannelId, RemotePageEpoch Epoch, RemoteOrigin Origin);

/// <summary>能力判定结果。</summary>
public readonly record struct RemotePageCapabilityDecision(
    bool Granted,
    string Code,
    RemotePageCapability? Capability);

/// <summary>是否允许开始一个新操作（例如一次粘贴）。</summary>
public readonly record struct RemotePageWorkAdmission(bool Accepted, string Code);

/// <summary>来自页面桥的一条入站消息：必须自带通道与代际，否则无法判定归属。</summary>
public readonly record struct RemotePageInboundMessage(string ChannelId, RemotePageEpoch Epoch, string Kind);

/// <summary>入站消息的接收结论；拒绝时 <see cref="Code"/> 给出确定原因。</summary>
public readonly record struct RemotePageMessageAdmission(bool Accepted, string Code, string Kind);

/// <summary>一次取消的事实：谁触发、作废了哪个代际、是否真的取消了在途工作。</summary>
public readonly record struct RemotePageCancellation(
    RemotePageCancellationTrigger Trigger,
    RemotePageEpoch Epoch,
    bool InFlightCancelled,
    string Reason);

/// <summary>
/// 每个目标一个页面会话的<b>平台中立状态机</b>（D15）：拥有页面代际、消息通道、
/// 文件能力与取消令牌；WebView2 / WPF 只是它的驱动者。
///
/// 规则（全部由 <c>tests/DshLauncher.Core.Tests/Remote/</c> 在 Linux 上执行）：
/// <list type="bullet">
/// <item>能力只在"主文档完成且来源逐字等于可信来源"时按<b>当前</b>代际授予；</item>
/// <item>主文档导航一开始就撤销旧代际能力并取消旧代际在途工作，因此旧页面的迟到消息
/// 得到确定码 <see cref="RemotePageSessionCodes.MessageEpochStale"/>；</item>
/// <item>子框架/子资源事件既不授予也不撤销能力；外部链接导航不继承能力；</item>
/// <item>隐藏窗口或切走标签页只拒绝<b>新</b>操作（<see cref="RemotePageSessionCodes.WorkPageSuspended"/>），
/// 已授权的在途操作继续并仍可回消息；</item>
/// <item>目标删除 / 凭据吊销 / 应用退出关闭会话：取消在途工作，之后任何消息都被
/// <see cref="RemotePageSessionCodes.SessionClosed"/> 拒绝。</item>
/// </list>
/// 本类型按 UI 线程亲和设计（WPF 中由 Dispatcher 串行驱动）；<see cref="InFlightToken"/>
/// 可安全地被其它线程观察，因为 <see cref="CancellationTokenSource"/> 自身是线程安全的。
/// </summary>
public sealed class RemotePageSessionState : IDisposable
{
    private readonly RemoteOrigin? _trustedOrigin;
    private CancellationTokenSource _epochSource = new();
    private RemotePageCapability? _capability;
    private RemotePageCancellation? _lastCancellation;
    private RemotePageSuspensionReason? _suspension;
    private RemoteNavigationKind? _currentNavigationKind;
    private string _capabilityCode = RemotePageSessionCodes.CapabilityAbsent;
    private bool _disposed;

    /// <summary>为一个目标页面建立会话状态。</summary>
    /// <param name="channelId">该页面专属的消息通道 id（<see cref="RemotePageChannelId.New"/> 生成）。</param>
    /// <param name="pageUrl">目标远程页面地址；不可解析时能力一律失败关闭。</param>
    public RemotePageSessionState(string channelId, string pageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(pageUrl);
        ChannelId = channelId;
        var parsed = RemoteOrigin.Parse(pageUrl);
        _trustedOrigin = parsed.Origin;
        TrustedOriginCode = parsed.Code;
    }

    /// <summary>该页面专属的消息通道 id。</summary>
    public string ChannelId { get; }

    /// <summary>解析后的可信来源；目标地址不是合法 http/https 来源时为 <c>null</c>。</summary>
    public RemoteOrigin? TrustedOrigin => _trustedOrigin;

    /// <summary>目标地址的解析码（<see cref="RemoteOriginCodes"/>）。</summary>
    public string TrustedOriginCode { get; }

    /// <summary>当前代际；主文档导航开始即递增。</summary>
    public RemotePageEpoch Epoch { get; private set; } = RemotePageEpoch.Initial;

    /// <summary>当前阶段。</summary>
    public RemotePagePhase Phase { get; private set; } = RemotePagePhase.AwaitingDocument;

    /// <summary>被拒绝新操作的原因；未挂起时为 <c>null</c>。</summary>
    public RemotePageSuspensionReason? SuspensionReason => _suspension;

    /// <summary>当前已授予的能力；没有能力时为 <c>null</c>。</summary>
    public RemotePageCapability? Capability => _capability;

    /// <summary>最近一次能力判定码（<see cref="RemotePageSessionCodes"/> / <see cref="RemoteOriginCodes"/>）。</summary>
    public string CapabilityCode => _capabilityCode;

    /// <summary>最近一次导航判定码。</summary>
    public string? LastNavigationCode { get; private set; }

    /// <summary>最近一次取消事实；从未取消时为 <c>null</c>。</summary>
    public RemotePageCancellation? LastCancellation => _lastCancellation;

    /// <summary>真正取消过在途工作的代际数（导航开始与关闭都会作废一个代际）。</summary>
    public int CancelledEpochCount { get; private set; }

    /// <summary>当前代际在途工作的取消令牌；会话关闭后为已取消令牌。</summary>
    public CancellationToken InFlightToken => _disposed ? new CancellationToken(canceled: true) : _epochSource.Token;

    /// <summary>主文档导航开始：<b>立即</b>撤销旧代际能力、取消旧代际在途工作，然后推进代际。</summary>
    public RemoteNavigationVerdict BeginNavigation(string? uri, RemoteNavigationKind kind)
    {
        if (Phase == RemotePagePhase.Closed)
        {
            LastNavigationCode = RemotePageSessionCodes.SessionClosed;
            return new RemoteNavigationVerdict(false, true, RemotePageSessionCodes.SessionClosed, null);
        }

        var verdict = RemotePageOriginPolicy.Evaluate(_trustedOrigin, uri, kind);
        if (!verdict.MainDocument)
        {
            // 子框架 / 子资源：对能力状态没有任何影响。
            LastNavigationCode = verdict.Code;
            return verdict;
        }

        _lastCancellation = CancelInFlight(
            RemotePageCancellationTrigger.NavigationStarted,
            RemotePageSessionCodes.NavigationStarted);
        Epoch = Epoch.Next();
        _epochSource = new CancellationTokenSource();
        _capability = null;
        _capabilityCode = kind == RemoteNavigationKind.ExternalLink
            ? RemoteOriginCodes.ExternalNavigation
            : RemotePageSessionCodes.CapabilityRevokedNavigation;
        _currentNavigationKind = kind;
        Phase = RemotePagePhase.AwaitingDocument;
        LastNavigationCode = verdict.Code;
        return verdict;
    }

    /// <summary>
    /// 顶层文档加载完成（<c>CoreWebView2.NavigationCompleted</c>）：只有最终文档来源等于可信来源、
    /// 且这次导航不是外部链接导航时，才按当前代际授予能力。
    /// </summary>
    public RemotePageCapabilityDecision CompleteMainDocumentLoad(string? documentSource)
    {
        if (Phase == RemotePagePhase.Closed)
        {
            return new RemotePageCapabilityDecision(false, RemotePageSessionCodes.SessionClosed, null);
        }

        var kind = _currentNavigationKind ?? RemoteNavigationKind.MainDocument;
        _currentNavigationKind = null;
        if (kind == RemoteNavigationKind.ExternalLink)
        {
            _capability = null;
            _capabilityCode = RemoteOriginCodes.ExternalNavigation;
            return new RemotePageCapabilityDecision(false, RemoteOriginCodes.ExternalNavigation, null);
        }

        var verdict = RemotePageOriginPolicy.Evaluate(_trustedOrigin, documentSource, RemoteNavigationKind.MainDocument);
        if (!verdict.Trusted || verdict.Origin is null)
        {
            _capability = null;
            _capabilityCode = verdict.Code;
            return new RemotePageCapabilityDecision(false, verdict.Code, null);
        }

        _capability = new RemotePageCapability(ChannelId, Epoch, verdict.Origin);
        _capabilityCode = RemotePageSessionCodes.CapabilityGranted;
        Phase = _suspension is null ? RemotePagePhase.Active : RemotePagePhase.Suspended;
        return new RemotePageCapabilityDecision(true, RemotePageSessionCodes.CapabilityGranted, _capability);
    }

    /// <summary>
    /// 外部链接导航开始（<c>NewWindowRequested</c>）：与主文档导航一样立即撤销能力并推进代际，
    /// 且这次导航的完成事件永远不会重新授予能力。
    /// </summary>
    public RemoteNavigationVerdict BeginExternalLink(string? uri) =>
        BeginNavigation(uri, RemoteNavigationKind.ExternalLink);

    /// <summary>
    /// 窗口隐藏 / 标签页切走：只拒绝<b>新</b>操作。不推进代际、不取消在途工作，
    /// 因为方案要求"已授权任务可继续绑定原草稿"。
    /// </summary>
    public bool SuspendForNewWork(RemotePageSuspensionReason reason)
    {
        if (Phase == RemotePagePhase.Closed)
        {
            return false;
        }

        _suspension = reason;
        if (Phase != RemotePagePhase.AwaitingDocument)
        {
            Phase = RemotePagePhase.Suspended;
        }

        return true;
    }

    /// <summary>页面重新可见/可交互：恢复接受新操作（能力仍需已验证且在有效代际内）。</summary>
    public bool ResumeNewWork()
    {
        if (Phase == RemotePagePhase.Closed)
        {
            return false;
        }

        _suspension = null;
        Phase = _capability is null ? RemotePagePhase.AwaitingDocument : RemotePagePhase.Active;
        return true;
    }

    /// <summary>关闭会话：撤销能力、取消当前代际在途工作；重复调用安全且返回同一事实。</summary>
    public RemotePageCancellation Close(RemotePageCancellationTrigger trigger)
    {
        if (Phase == RemotePagePhase.Closed)
        {
            return _lastCancellation ?? new RemotePageCancellation(trigger, Epoch, false, RemotePageSessionCodes.SessionClosed);
        }

        Phase = RemotePagePhase.Closed;
        _suspension = null;
        _currentNavigationKind = null;
        _capability = null;
        _capabilityCode = RemotePageSessionCodes.CapabilityRevokedClosed;
        var cancellation = CancelInFlight(trigger, ReasonFor(trigger));
        _lastCancellation = cancellation;
        return cancellation;
    }

    /// <summary>判定一条入站消息：关闭 → 通道 → 代际 → 能力，顺序固定且拒绝码确定。</summary>
    public RemotePageMessageAdmission AdmitMessage(RemotePageInboundMessage message)
    {
        if (Phase == RemotePagePhase.Closed)
        {
            return new RemotePageMessageAdmission(false, RemotePageSessionCodes.SessionClosed, message.Kind);
        }

        if (!string.Equals(message.ChannelId, ChannelId, StringComparison.Ordinal))
        {
            return new RemotePageMessageAdmission(false, RemotePageSessionCodes.MessageChannelMismatch, message.Kind);
        }

        if (message.Epoch != Epoch)
        {
            return new RemotePageMessageAdmission(false, RemotePageSessionCodes.MessageEpochStale, message.Kind);
        }

        if (_capability is null)
        {
            return new RemotePageMessageAdmission(false, RemotePageSessionCodes.MessageCapabilityAbsent, message.Kind);
        }

        return new RemotePageMessageAdmission(true, RemotePageSessionCodes.Accepted, message.Kind);
    }

    /// <summary>判定能否开始一个新操作（一次新的粘贴/批次）。在途操作的回执不受此限制。</summary>
    public RemotePageWorkAdmission AdmitNewWork()
    {
        if (Phase == RemotePagePhase.Closed)
        {
            return new RemotePageWorkAdmission(false, RemotePageSessionCodes.SessionClosed);
        }

        if (Phase == RemotePagePhase.Suspended)
        {
            return new RemotePageWorkAdmission(false, RemotePageSessionCodes.WorkPageSuspended);
        }

        if (_capability is null)
        {
            return new RemotePageWorkAdmission(false, RemotePageSessionCodes.WorkCapabilityAbsent);
        }

        return new RemotePageWorkAdmission(true, RemotePageSessionCodes.WorkAccepted);
    }

    /// <summary>释放状态机：未关闭时按应用退出关闭，并释放取消令牌源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close(RemotePageCancellationTrigger.ApplicationExit);
        _disposed = true;
        _epochSource.Dispose();
    }

    private RemotePageCancellation CancelInFlight(RemotePageCancellationTrigger trigger, string reason)
    {
        var cancelled = false;
        if (!_epochSource.IsCancellationRequested)
        {
            _epochSource.Cancel();
            cancelled = true;
            CancelledEpochCount++;
        }

        return new RemotePageCancellation(trigger, Epoch, cancelled, reason);
    }

    private static string ReasonFor(RemotePageCancellationTrigger trigger) => trigger switch
    {
        RemotePageCancellationTrigger.NavigationStarted => RemotePageSessionCodes.NavigationStarted,
        RemotePageCancellationTrigger.TargetRemoved => RemotePageSessionCodes.TargetRemoved,
        RemotePageCancellationTrigger.CredentialRevoked => RemotePageSessionCodes.CredentialRevoked,
        _ => RemotePageSessionCodes.ApplicationExit,
    };
}

/// <summary>页面消息通道 id 的生成器：每个目标页面一个，绝不复用。</summary>
public static class RemotePageChannelId
{
    /// <summary>生成一个新的通道 id。</summary>
    public static string New() => "page-" + Guid.NewGuid().ToString("N");
}
