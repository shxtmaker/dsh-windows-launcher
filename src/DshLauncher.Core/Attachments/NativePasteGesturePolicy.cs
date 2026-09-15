using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// D16 原生粘贴手势的<b>本地</b>判定码。与 <see cref="AttachmentStagingCodes"/> 同样的约定：
/// 只出现在本机结果、trace 与测试断言里，<b>绝不</b>写进任何报文；语义与冻结码重合时直接复用
/// （例如文件数超限用 <c>limit-batch-files</c>、无会话用 <c>no-session</c>、取消用 <c>cancelled</c>）。
/// </summary>
public static class NativePasteCodes
{
    // —— 手势授权（铸造侧：真实原生手势的事实不成立）——

    /// <summary>窗口不可见（隐藏/最小化）时不得开始新操作。</summary>
    public const string GestureWindowHidden = "gesture-window-hidden";

    /// <summary>窗口没有焦点（不是前台窗口）时不得开始新操作。</summary>
    public const string GestureWindowInactive = "gesture-window-inactive";

    /// <summary>键盘焦点不在当前活动页面的 WebView 内。</summary>
    public const string GestureFocusOutsidePage = "gesture-focus-outside-page";

    /// <summary>手势发生在非当前标签页的目标上。</summary>
    public const string GestureTabNotActive = "gesture-tab-not-active";

    /// <summary>手势 id 为空或已被铸造过（同一次原生动作只能授权一次）。</summary>
    public const string GestureIdInvalid = "gesture-id-invalid";

    /// <summary>同一手势 id 已经铸造过授权：页面/桥无法重复铸造，原生路由也不得重放。</summary>
    public const string GestureAlreadyMinted = "gesture-already-minted";

    /// <summary>授权有效期请求超出上界（授权只能短命，不能延长）。</summary>
    public const string GestureLifetimeTooLong = "gesture-lifetime-too-long";

    /// <summary>活动授权的本地台账已满（不无界堆积）。</summary>
    public const string GestureRegistryFull = "gesture-registry-full";

    /// <summary>目标标识为空。</summary>
    public const string GestureTargetInvalid = "gesture-target-invalid";

    // —— 手势授权（校验侧：确定拒绝原因）——

    /// <summary>授权 id 从未铸造过（页面无法凭空构造）。</summary>
    public const string AuthorizationUnknown = "gesture-authorization-unknown";

    /// <summary>授权已被消费过（一次性，重放一律拒绝）。</summary>
    public const string AuthorizationReplayed = "gesture-authorization-replayed";

    /// <summary>授权已过期（超过铸造时的短命窗口）。</summary>
    public const string AuthorizationExpired = "gesture-authorization-expired";

    /// <summary>授权尚未生效（时钟回拨或伪造的时间戳）。</summary>
    public const string AuthorizationNotYetValid = "gesture-authorization-not-yet-valid";

    /// <summary>授权绑定的目标与请求目标不同。</summary>
    public const string AuthorizationTargetMismatch = "gesture-authorization-target-mismatch";

    /// <summary>授权绑定的页面代际已失效（导航/刷新/关页）。</summary>
    public const string AuthorizationEpochMismatch = "gesture-authorization-epoch-mismatch";

    /// <summary>授权绑定的窗口/标签焦点与请求不同（切了标签或换了焦点）。</summary>
    public const string AuthorizationFocusMismatch = "gesture-authorization-focus-mismatch";

    /// <summary>授权通过并已消费。</summary>
    public const string AuthorizationGranted = "gesture-authorization-granted";

    /// <summary>没有任何活动授权（尚未发生真实手势）。</summary>
    public const string AuthorizationAbsent = "gesture-authorization-absent";
}

/// <summary>
/// 触发原生读取的一次<b>真实</b>用户手势。三种形态都必须经过窗口级原生输入路径
/// （键盘 <c>Ctrl+V</c>/<c>Shift+Insert</c>、原生上下文菜单的"粘贴"、拖放到窗口），
/// 页面里的 <c>isTrusted</c> 字段、路径或文件名都不能代替它。
/// </summary>
public enum NativeGestureKind
{
    /// <summary>粘贴快捷键（Ctrl+V / Shift+Insert）。</summary>
    PasteHotkey,

    /// <summary>原生上下文菜单里的"粘贴"命令。</summary>
    ContextMenuPaste,

    /// <summary>把文件拖放到窗口/页面上。</summary>
    DragDrop,
}

/// <summary>
/// 手势发生时窗口/标签/焦点的<b>观测事实</b>（由 WPF 层从真实窗口状态读取，不是页面声明的）。
/// </summary>
public readonly record struct NativeWindowFocusState(
    bool WindowVisible,
    bool WindowActive,
    bool FocusInsideActivePage,
    bool ActiveTabMatchesTarget)
{
    /// <summary>全部条件成立，可用于铸造授权。</summary>
    public bool AllSatisfied => WindowVisible && WindowActive && FocusInsideActivePage && ActiveTabMatchesTarget;

    /// <summary>第一项不成立的原因码（顺序固定，因此拒绝原因确定可复现）。</summary>
    public string FirstFailureCode()
    {
        if (!WindowVisible)
        {
            return NativePasteCodes.GestureWindowHidden;
        }

        if (!WindowActive)
        {
            return NativePasteCodes.GestureWindowInactive;
        }

        if (!FocusInsideActivePage)
        {
            return NativePasteCodes.GestureFocusOutsidePage;
        }

        return NativePasteCodes.GestureTabNotActive;
    }
}

/// <summary>
/// 一次真实原生手势的事实集合。<see cref="GestureId"/> 由原生输入路由为每次用户动作生成，
/// 页面/桥拿不到也不能构造；<see cref="FocusToken"/> 标识"当时拥有键盘焦点的窗口+标签+页面"。
/// </summary>
public readonly record struct NativeGestureEvidence(
    string GestureId,
    NativeGestureKind Kind,
    Guid TargetId,
    RemotePageEpoch PageEpoch,
    string FocusToken,
    NativeWindowFocusState Focus,
    long ObservedAtMs);

/// <summary>授权校验时的期望绑定：目标、页面代际与焦点令牌。</summary>
public readonly record struct NativeGestureBinding(Guid TargetId, RemotePageEpoch PageEpoch, string FocusToken);

/// <summary>
/// 一次短命操作授权：绑定目标 + 页面代际 + 焦点令牌 + 过期时刻。
/// 它是不可变值，没有任何"延长/续期"入口；想再读一次剪贴板就必须产生一次新的真实手势。
/// </summary>
public readonly record struct NativeGestureAuthorization(
    string GestureId,
    Guid TargetId,
    RemotePageEpoch PageEpoch,
    string FocusToken,
    long IssuedAtMs,
    long ExpiresAtMs);

/// <summary>授权判定结果；拒绝时 <see cref="Code"/> 给出确定原因，允许时携带铸造出的授权。</summary>
public readonly record struct NativeGestureDecision(
    bool Authorized,
    string Code,
    string Detail,
    NativeGestureAuthorization Authorization)
{
    /// <summary>允许（授权内容随结果返回，避免调用方自行拼装出与本台账不一致的值）。</summary>
    public static NativeGestureDecision Allow(string code, string detail, NativeGestureAuthorization authorization) =>
        new(true, code, detail, authorization);

    /// <summary>拒绝（授权字段为 <c>default</c>，不可被当作有效授权使用）。</summary>
    public static NativeGestureDecision Deny(string code, string detail) =>
        new(false, code, detail, default);
}

/// <summary>
/// 原生手势授权的平台中立状态机（D16）。规则：
/// <list type="bullet">
/// <item>只有<b>全部</b>焦点事实成立（窗口可见、窗口是前台、焦点在本页 WebView、标签就是该目标）
/// 才铸造授权，否则给出确定拒绝码；</item>
/// <item>授权是<b>短命</b>的（默认 <see cref="DefaultLifetimeMs"/>，上界 <see cref="MaxLifetimeMs"/>），
/// 绑定目标 id、页面代际与焦点令牌，且<b>一次性</b>消费；</item>
/// <item>过期 / 旧代际 / 别的标签 / 别的目标 / 重放都有各自的确定码；</item>
/// <item>没有任何"续期/延长"方法：新的读取必须来自新的真实手势，页面无法铸造或延长。</item>
/// </list>
/// 本类型不含任何 P/Invoke，也不接触剪贴板；真实剪贴板读取由 Windows 半区完成。
/// 本类型按 UI 线程串行驱动设计（WPF Dispatcher），不做跨线程同步。
/// </summary>
public sealed class NativeGestureAuthorizer
{
    /// <summary>默认授权有效期（毫秒）：足够完成一次剪贴板探测，远短于任何用户可感知的窗口。</summary>
    public const int DefaultLifetimeMs = 2_000;

    /// <summary>授权有效期上界（毫秒）：调用方不得要求更长的窗口。</summary>
    public const int MaxLifetimeMs = 5_000;

    /// <summary>未消费授权与已消费 id 的本地台账上界。</summary>
    public const int MaxTrackedGestures = 64;

    private readonly Dictionary<string, NativeGestureAuthorization> _live = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>当前活动（未消费且未过期被清理）的授权数。</summary>
    public int LiveCount => _live.Count;

    /// <summary>已经消费掉的授权数。</summary>
    public int ConsumedCount => _consumed.Count;

    /// <summary>
    /// 由一次真实原生手势铸造短命授权。任一项焦点事实不成立、手势 id 非法或重复、
    /// 有效期超界或台账已满都会拒绝，且<b>不</b>改变任何既有状态。
    /// </summary>
    public NativeGestureDecision Mint(
        NativeGestureEvidence evidence,
        long nowMs,
        int lifetimeMs = DefaultLifetimeMs)
    {
        if (string.IsNullOrWhiteSpace(evidence.GestureId))
        {
            return NativeGestureDecision.Deny(NativePasteCodes.GestureIdInvalid, "手势 id 为空");
        }

        if (evidence.TargetId == Guid.Empty)
        {
            return NativeGestureDecision.Deny(NativePasteCodes.GestureTargetInvalid, "手势目标为空");
        }

        if (lifetimeMs < 1 || lifetimeMs > MaxLifetimeMs)
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.GestureLifetimeTooLong,
                $"授权有效期 {lifetimeMs} 超出 1..{MaxLifetimeMs}");
        }

        if (_live.ContainsKey(evidence.GestureId) || _consumed.Contains(evidence.GestureId))
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.GestureAlreadyMinted,
                "同一次手势只能铸造一次授权");
        }

        if (!evidence.Focus.AllSatisfied)
        {
            return NativeGestureDecision.Deny(
                evidence.Focus.FirstFailureCode(),
                "手势发生时窗口/标签/焦点条件不成立");
        }

        if (_live.Count + _consumed.Count >= MaxTrackedGestures)
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.GestureRegistryFull,
                $"授权台账已达上界 {MaxTrackedGestures}：先消费或过期清理");
        }

        var authorization = new NativeGestureAuthorization(
            evidence.GestureId,
            evidence.TargetId,
            evidence.PageEpoch,
            evidence.FocusToken,
            nowMs,
            nowMs + lifetimeMs);
        _live[evidence.GestureId] = authorization;
        return NativeGestureDecision.Allow(
            NativePasteCodes.AuthorizationGranted,
            "手势授权已铸造",
            authorization);
    }

    /// <summary>
    /// 校验并<b>消费</b>一次授权。判定顺序固定：
    /// 未知 → 重放 → 过期 → 未生效 → 目标不符 → 代际不符 → 焦点不符；
    /// 只有全部通过才消费，焦点/目标不符的噪声请求不会烧掉合法授权。
    /// </summary>
    public NativeGestureDecision Authorize(
        NativeGestureAuthorization authorization,
        NativeGestureBinding binding,
        long nowMs)
    {
        if (!_live.TryGetValue(authorization.GestureId, out var live))
        {
            if (_expired.Contains(authorization.GestureId))
            {
                return NativeGestureDecision.Deny(
                    NativePasteCodes.AuthorizationExpired,
                    "该手势授权已过期（已不在活动台账里）");
            }

            if (_consumed.Contains(authorization.GestureId))
            {
                return NativeGestureDecision.Deny(
                    NativePasteCodes.AuthorizationReplayed,
                    "该手势授权已被消费（一次性）");
            }

            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationUnknown,
                "该手势授权从未铸造过");
        }

        if (live != authorization)
        {
            // 页面/桥无法构造授权；内容不符说明调用方拿的不是本台账里的那一份。
            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationUnknown,
                "授权内容与本台账记录不一致");
        }

        if (nowMs > authorization.ExpiresAtMs)
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationExpired,
                $"授权已于 {authorization.ExpiresAtMs}ms 过期（当前 {nowMs}ms）");
        }

        if (nowMs < authorization.IssuedAtMs)
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationNotYetValid,
                "授权尚未生效（时钟回拨）");
        }

        if (binding.TargetId != authorization.TargetId)
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationTargetMismatch,
                "授权绑定的目标与请求目标不同");
        }

        if (binding.PageEpoch != authorization.PageEpoch)
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationEpochMismatch,
                "授权绑定的页面代际已失效（导航/刷新/关页）");
        }

        if (!string.Equals(binding.FocusToken, authorization.FocusToken, StringComparison.Ordinal))
        {
            return NativeGestureDecision.Deny(
                NativePasteCodes.AuthorizationFocusMismatch,
                "授权绑定的窗口/标签焦点与请求不同");
        }

        _live.Remove(authorization.GestureId);
        Remember(authorization.GestureId);
        return NativeGestureDecision.Allow(NativePasteCodes.AuthorizationGranted, "授权已消费", live);
    }

    /// <summary>显式作废一次授权（例如窗口隐藏、标签切走、目标删除）。已消费的 id 保持已消费。</summary>
    public bool Revoke(string gestureId) => !string.IsNullOrEmpty(gestureId) && _live.Remove(gestureId);

    /// <summary>
    /// 读取一次手势当前的活动授权；没有（从未铸造、已消费、过期被清理或已作废）时返回 <c>null</c>。
    /// 只用于把铸造结果交回同一个编排器去消费，不构成铸造/续期入口。
    /// </summary>
    public NativeGestureAuthorization? AuthorizationFor(string gestureId)
    {
        if (string.IsNullOrEmpty(gestureId))
        {
            return null;
        }

        return _live.TryGetValue(gestureId, out var authorization) ? authorization : null;
    }

    /// <summary>作废全部活动授权并清空台账（应用退出 / 页面重载）。</summary>
    public void RevokeAll()
    {
        _live.Clear();
        _consumed.Clear();
        _expired.Clear();
        _order.Clear();
    }

    /// <summary>丢弃已过期的活动授权，返回清理条数（不改变已消费记录）。</summary>
    public int PruneExpired(long nowMs)
    {
        var expired = _live
            .Where(pair => nowMs > pair.Value.ExpiresAtMs)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var gestureId in expired)
        {
            _live.Remove(gestureId);
            _expired.Add(gestureId);
            Remember(gestureId);
        }

        return expired.Length;
    }

    private void Remember(string gestureId)
    {
        _consumed.Add(gestureId);
        _order.Enqueue(gestureId);
        while (_order.Count > MaxTrackedGestures)
        {
            var evicted = _order.Dequeue();
            _consumed.Remove(evicted);
            _expired.Remove(evicted);
        }
    }
}
