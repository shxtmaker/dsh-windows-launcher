using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>剪贴板探测结果（STA 短窗口内取得；失败时给出可恢复的确定码）。</summary>
public sealed record ClipboardProbeResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public ClipboardProbe Probe { get; init; }

    /// <summary>实际尝试打开剪贴板的次数（诊断与实机用例断言用）。</summary>
    public int OpenAttempts { get; init; }
}

/// <summary>剪贴板读取结果：只回不透明捕获 id 与尺寸事实，<b>不含任何本机路径</b>。</summary>
public sealed record ClipboardReadResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public ClipboardSourceKind Kind { get; init; }

    /// <summary>原生捕获票据 id（桥只能拿 id，拿不到路径）。</summary>
    public IReadOnlyList<string> CaptureIds { get; init; } = [];

    public int FileCount { get; init; }

    public int BitmapWidth { get; init; }

    public int BitmapHeight { get; init; }

    public long PixelCount { get; init; }

    /// <summary>截图编码后的 PNG 字节数。</summary>
    public long EncodedBytes { get; init; }

    /// <summary>读取是否发生在 STA 之外（生产实现必须为 <c>true</c>）。</summary>
    public bool ReadOffSta { get; init; }
}

/// <summary>
/// 原生内容读取端口（D16）：按判定读取文件列表或位图。
/// 契约：
/// <list type="bullet">
/// <item>昂贵的部分（复制像素、编码 PNG、路径字符串、登记票据）必须在 <b>STA 之外</b>完成，
/// 且返回时 <see cref="ClipboardReadResult.ReadOffSta"/> 为 <c>true</c>；</item>
/// <item>必须复核 <see cref="ClipboardProbe.SequenceNumber"/>：内容变了就给 <c>clipboard-changed</c>；</item>
/// <item>打开失败、被占用、数据畸形一律返回确定码，不抛异常、不挂住；</item>
/// <item>不接受调用方给的任何路径：内容只能来自剪贴板或原生拖放数据对象。</item>
/// </list>
/// </summary>
public interface INativePasteReadPort
{
    Task<ClipboardReadResult> ReadAsync(
        ClipboardProbe probe,
        ClipboardSourceDecision decision,
        CancellationToken cancellationToken);
}

/// <summary>剪贴板端口：在有界探测之上再加读取能力。</summary>
public interface INativeClipboardPastePort : INativePasteReadPort
{
    /// <summary>有界探测（可由 STA 直接调用；不复制大块数据）。</summary>
    ClipboardProbeResult Probe();
}

/// <summary>一次手势的既定计划：同步判定已经完成，剩下的只是（可能的）异步读取。</summary>
public sealed record NativePastePlan
{
    /// <summary>计划针对的手势。</summary>
    public required NativeGestureEvidence Evidence { get; init; }

    /// <summary>已经定下的结局（拒绝 / 纯文本 / 交给桥）；为 <c>null</c> 时需要读取。</summary>
    public NativePasteOutcome? Immediate { get; init; }

    /// <summary>判定出的来源与路由。</summary>
    public required ClipboardSourceDecision Decision { get; init; }

    /// <summary>探测事实（拖放来源时为合成事实）。</summary>
    public ClipboardProbe Probe { get; init; }

    /// <summary>是否还需要一次（STA 之外的）原生读取。</summary>
    public bool NeedsRead => Immediate is null && Decision.Route == ClipboardImportRoute.NativePaste;

    /// <summary>UI 是否必须抑制浏览器默认粘贴（只有原生通路需要）。</summary>
    public bool SuppressesBrowserDefault => NeedsRead;
}

/// <summary>一次原生粘贴手势的最终结局（UI 与传输半区据此继续）。</summary>
public sealed record NativePasteOutcome
{
    public required bool Accepted { get; init; }

    public required string Code { get; init; }

    public string? Detail { get; init; }

    public string GestureId { get; init; } = string.Empty;

    /// <summary>该手势所属目标（UI 与账目按目标隔离）。</summary>
    public Guid TargetId { get; init; }

    public NativeGestureKind GestureKind { get; init; }

    public ClipboardSourceKind SourceKind { get; init; }

    public ClipboardImportRoute Route { get; init; }

    public NativePasteConsumer Consumer { get; init; }

    /// <summary>可恢复性（来自 <see cref="NativePasteFailurePolicy"/>）。</summary>
    public bool Recoverable { get; init; }

    /// <summary>UI 的下一步。</summary>
    public NativePasteRetry Retry { get; init; }

    /// <summary>原生捕获票据 id（原生通路）。</summary>
    public IReadOnlyList<string> CaptureIds { get; init; } = [];

    public int FileCount { get; init; }

    public long PixelCount { get; init; }

    public long EncodedBytes { get; init; }

    /// <summary>读取是否发生在 STA 之外（Windows 实机用例断言 <c>true</c>）。</summary>
    public bool ReadOffSta { get; init; }

    /// <summary>是否应当让浏览器保留默认粘贴行为（纯文本、或宿主拒绝时的兜底）。</summary>
    public bool KeepsBrowserDefault { get; init; }
}

/// <summary>
/// D16 原生粘贴编排器（<b>生产</b>状态机，平台中立，Linux 可执行）。
///
/// 一次真实手势按固定顺序流过：
/// <list type="number">
/// <item><b>手势授权</b>：只有窗口可见、是前台窗口、焦点在本页 WebView、标签就是该目标时才铸造
/// 短命授权（<see cref="NativeGestureAuthorizer"/>）；</item>
/// <item><b>导入闸门</b>：无 session / 锁定 / 子代理提示 / 页面不接受新操作时给出确定码，
/// 且<b>不</b>触碰剪贴板、不自动创建会话；</item>
/// <item><b>有界探测</b>：只取格式、文件个数与位图尺寸；</item>
/// <item><b>来源优先</b>：文件列表优先于位图，纯文本保留原行为；</item>
/// <item><b>唯一消费者</b>：按路由选举（文件列表恒为原生；位图按握手模式），
/// 原生失败/超时后桥不得补导入；</item>
/// <item><b>消费授权并读取</b>：授权一次性消费，读取在 STA 之外进行；成功后占用消费者，
/// 因此同一次动作的导入次数恒 ≤ 1。</item>
/// </list>
///
/// <see cref="Plan"/> 同步完成前 5 步：WPF 必须在键盘/拖放回调<b>返回之前</b>决定是否抑制浏览器
/// 默认粘贴（否则同一次动作会被消费两次），因此这一半刻意是同步的、有界的（只有一次短窗口探测）。
/// <see cref="ExecuteAsync"/> 完成剩下的异步读取与占用。
/// 全部依赖注入（端口、授权器、账本、模式台账、时钟、限额），因此本状态机可以在 Linux 上用假端口逐条驱动。
/// </summary>
public sealed class NativePasteOrchestrator
{
    /// <summary>原生读取的上限（毫秒）：超时即放弃并禁止桥补导入。</summary>
    public const int ReadTimeoutMs = 15_000;

    /// <summary>剪贴板打开失败的确定码。</summary>
    public const string ClipboardBusyCode = "clipboard-busy";

    /// <summary>打开剪贴板失败（非占用原因）。</summary>
    public const string ClipboardOpenFailedCode = "clipboard-open-failed";

    /// <summary>读取期间剪贴板内容变化。</summary>
    public const string ClipboardChangedCode = "clipboard-changed";

    /// <summary>数据畸形。</summary>
    public const string ClipboardMalformedCode = "clipboard-malformed";

    /// <summary>读取超时（放弃该手势）。</summary>
    public const string ClipboardTimeoutCode = "clipboard-timeout";

    /// <summary>原生导入成功。</summary>
    public const string ImportedCode = "native-paste-imported";

    /// <summary>交给页面桥（宿主不碰剪贴板内容）。</summary>
    public const string DelegatedToBridgeCode = "native-paste-delegated-bridge";

    /// <summary>纯文本：宿主完全不介入。</summary>
    public const string TextPassThroughCode = "native-paste-text-passthrough";

    private readonly INativeClipboardPastePort _clipboard;
    private readonly NativeGestureAuthorizer _authorizer;
    private readonly NativePasteConsumerLedger _ledger;
    private readonly ScreenshotOwnerRegistry _owners;
    private readonly AttachmentLimits _limits;
    private readonly TimeProvider _time;
    private readonly int _readTimeoutMs;

    public NativePasteOrchestrator(
        INativeClipboardPastePort clipboard,
        NativeGestureAuthorizer authorizer,
        NativePasteConsumerLedger ledger,
        ScreenshotOwnerRegistry owners,
        AttachmentLimits? limits = null,
        TimeProvider? timeProvider = null,
        int readTimeoutMs = ReadTimeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readTimeoutMs, 1);
        _readTimeoutMs = readTimeoutMs;
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(owners);
        _clipboard = clipboard;
        _authorizer = authorizer;
        _ledger = ledger;
        _owners = owners;
        _limits = limits ?? new AttachmentLimits();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>本编排器的唯一消费者账本（窗口据此断言"没有双导入"）。</summary>
    public NativePasteConsumerLedger Ledger => _ledger;

    /// <summary>手势授权器（窗口的手势入口用它铸造授权）。</summary>
    public NativeGestureAuthorizer Authorizer => _authorizer;

    /// <summary>剪贴板来源计划（同步、有界）。</summary>
    public NativePastePlan Plan(NativeGestureEvidence evidence, ImportContextFacts context)
    {
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var mint = _authorizer.Mint(evidence, nowMs);
        if (!mint.Authorized)
        {
            return Immediate(evidence, Failure(evidence, mint.Code, mint.Detail, ClipboardSourceKind.Empty));
        }

        // 闸门先于剪贴板：不能导入时连读都不读，也绝不自动创建会话。
        var gate = AttachmentImportGate.Evaluate(context);
        if (!gate.Allowed)
        {
            // 墓碑：该动作由原生侧接管过，桥不得再导入同一个动作。
            _ledger.Abandon(evidence.GestureId, gate.Code, gate.Detail);
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(
                evidence,
                Failure(evidence, gate.Code, gate.Detail, ClipboardSourceKind.Empty));
        }

        var probeResult = _clipboard.Probe();
        if (!probeResult.Ok)
        {
            var probeCode = probeResult.Code ?? ClipboardOpenFailedCode;
            _ledger.Abandon(evidence.GestureId, probeCode, probeResult.Detail ?? "剪贴板探测失败");
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(
                evidence,
                Failure(
                    evidence,
                    probeCode,
                    probeResult.Detail ?? "剪贴板探测失败",
                    ClipboardSourceKind.Empty));
        }

        var mode = _owners.ModeFor(evidence.TargetId, evidence.PageEpoch);
        var decision = ClipboardSourcePolicy.Decide(probeResult.Probe, mode, _limits);
        return Route(evidence, decision, probeResult.Probe);
    }

    /// <summary>
    /// 拖放来源的计划：拖放不经过剪贴板，文件个数与逐个路径由平台半区在读取时判定，
    /// 因此这里只按"数据对象是否声明了文件列表"选路（仍然要过手势授权与导入闸门）。
    /// </summary>
    public NativePastePlan PlanDrop(NativeGestureEvidence evidence, ImportContextFacts context, bool hasFileList)
    {
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var mint = _authorizer.Mint(evidence, nowMs);
        if (!mint.Authorized)
        {
            return Immediate(evidence, Failure(evidence, mint.Code, mint.Detail, ClipboardSourceKind.Empty));
        }

        var gate = AttachmentImportGate.Evaluate(context);
        if (!gate.Allowed)
        {
            _ledger.Abandon(evidence.GestureId, gate.Code, gate.Detail);
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(evidence, Failure(evidence, gate.Code, gate.Detail, ClipboardSourceKind.Empty));
        }

        var decision = ClipboardSourcePolicy.DecideDrop(hasFileList);
        if (decision.Route != ClipboardImportRoute.NativePaste)
        {
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(
                evidence,
                Failure(
                    evidence,
                    decision.Code,
                    decision.Detail ?? "拖放数据不含文件列表",
                    decision.Kind));
        }

        return Route(evidence, decision, default);
    }

    /// <summary>执行计划剩下的部分：消费授权 → STA 之外读取 → 占用消费者（或放弃）。</summary>
    public async Task<NativePasteOutcome> ExecuteAsync(
        NativePastePlan plan,
        INativePasteReadPort port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(port);
        if (plan.Immediate is not null)
        {
            return plan.Immediate;
        }

        var evidence = plan.Evidence;
        var authorization = _authorizer.AuthorizationFor(evidence.GestureId);
        if (authorization is null)
        {
            _ledger.Abandon(evidence.GestureId, NativePasteCodes.AuthorizationAbsent, "授权已不可用");
            return Failure(
                evidence,
                NativePasteCodes.AuthorizationAbsent,
                "授权已不可用（可能已过期或被作废）",
                plan.Decision.Kind,
                retryOverride: NativePasteRetry.RetryAfterUserAction);
        }

        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var binding = new NativeGestureBinding(evidence.TargetId, evidence.PageEpoch, evidence.FocusToken);
        var authorize = _authorizer.Authorize(authorization.Value, binding, nowMs);
        if (!authorize.Authorized)
        {
            _ledger.Abandon(evidence.GestureId, authorize.Code, authorize.Detail);
            return Failure(
                evidence,
                authorize.Code,
                authorize.Detail,
                plan.Decision.Kind,
                retryOverride: NativePasteRetry.RetryAfterUserAction);
        }

        ClipboardReadResult read;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_readTimeoutMs);
            read = await port.ReadAsync(plan.Probe, plan.Decision, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ledger.Abandon(evidence.GestureId, ClipboardTimeoutCode, "原生读取超时");
            return Failure(
                evidence,
                ClipboardTimeoutCode,
                "原生读取超时（超时后不得由桥补导入）",
                plan.Decision.Kind,
                retryOverride: NativePasteRetry.RetryAfterUserAction);
        }

        if (!read.Ok)
        {
            var code = read.Code ?? ClipboardOpenFailedCode;
            _ledger.Abandon(evidence.GestureId, code, read.Detail ?? "原生读取失败");
            return Failure(
                evidence,
                code,
                read.Detail,
                plan.Decision.Kind,
                read,
                NativePasteRetry.RetryAfterUserAction);
        }

        var claimed = _ledger.Claim(evidence.GestureId, NativePasteConsumer.NativePaste);
        if (!claimed.Accepted)
        {
            return Failure(evidence, claimed.Code, claimed.Detail, plan.Decision.Kind, read);
        }

        return new NativePasteOutcome
        {
            Accepted = true,
            Code = ImportedCode,
            Detail = "原生 paste 已产生一次导入",
            GestureId = evidence.GestureId,
            TargetId = evidence.TargetId,
            GestureKind = evidence.Kind,
            SourceKind = read.Kind,
            Route = ClipboardImportRoute.NativePaste,
            Consumer = NativePasteConsumer.NativePaste,
            CaptureIds = read.CaptureIds,
            FileCount = read.FileCount,
            PixelCount = read.PixelCount,
            EncodedBytes = read.EncodedBytes,
            ReadOffSta = read.ReadOffSta,
        };
    }

    /// <summary>计划 + 执行（剪贴板来源）的一次性入口。</summary>
    public async Task<NativePasteOutcome> HandleAsync(
        NativeGestureEvidence evidence,
        ImportContextFacts context,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(evidence, context);
        return plan.Immediate ?? await ExecuteAsync(plan, _clipboard, cancellationToken).ConfigureAwait(false);
    }

    private NativePastePlan Route(
        NativeGestureEvidence evidence,
        ClipboardSourceDecision decision,
        ClipboardProbe probe)
    {
        if (decision.Route == ClipboardImportRoute.OriginalTextBehaviour)
        {
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(
                evidence,
                new NativePasteOutcome
                {
                    Accepted = false,
                    Code = TextPassThroughCode,
                    Detail = decision.Detail,
                    GestureId = evidence.GestureId,
                    TargetId = evidence.TargetId,
                    GestureKind = evidence.Kind,
                    SourceKind = decision.Kind,
                    Route = ClipboardImportRoute.OriginalTextBehaviour,
                    KeepsBrowserDefault = true,
                },
                decision,
                probe);
        }

        if (decision.Route == ClipboardImportRoute.None)
        {
            // 来源不可用：先把确定判定码交回去，台账只留一块不许桥补导入的墓碑。
            _ledger.Abandon(evidence.GestureId, decision.Code, decision.Detail ?? "来源不可用");
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(
                evidence,
                Failure(evidence, decision.Code, decision.Detail ?? "来源不可用", decision.Kind),
                decision,
                probe);
        }

        var elect = _ledger.Elect(evidence.GestureId, decision.Route);
        if (!elect.Accepted)
        {
            _authorizer.Revoke(evidence.GestureId);
            return Immediate(evidence, Failure(evidence, elect.Code, elect.Detail, decision.Kind), decision, probe);
        }

        if (decision.Route == ClipboardImportRoute.Bridge)
        {
            var claim = _ledger.Claim(evidence.GestureId, NativePasteConsumer.Bridge);
            _authorizer.Revoke(evidence.GestureId);
            if (!claim.Accepted)
            {
                return Immediate(evidence, Failure(evidence, claim.Code, claim.Detail, decision.Kind), decision, probe);
            }

            return Immediate(
                evidence,
                new NativePasteOutcome
                {
                    Accepted = true,
                    Code = DelegatedToBridgeCode,
                    Detail = "该手势由页面桥消费（宿主不读取剪贴板内容）",
                    GestureId = evidence.GestureId,
                    TargetId = evidence.TargetId,
                    GestureKind = evidence.Kind,
                    SourceKind = decision.Kind,
                    Route = ClipboardImportRoute.Bridge,
                    Consumer = NativePasteConsumer.Bridge,
                    KeepsBrowserDefault = true,
                },
                decision,
                probe);
        }

        return new NativePastePlan
        {
            Evidence = evidence,
            Decision = decision,
            Probe = probe,
        };
    }

    private static NativePastePlan Immediate(
        NativeGestureEvidence evidence,
        NativePasteOutcome outcome,
        ClipboardSourceDecision? decision = null,
        ClipboardProbe probe = default) =>
        new()
        {
            Evidence = evidence,
            Immediate = outcome,
            Decision = decision ?? new ClipboardSourceDecision
            {
                Kind = outcome.SourceKind,
                Route = ClipboardImportRoute.None,
                Code = outcome.Code,
                Detail = outcome.Detail,
            },
            Probe = probe,
        };

    private static NativePasteOutcome Failure(
        NativeGestureEvidence evidence,
        string code,
        string? detail,
        ClipboardSourceKind kind,
        ClipboardReadResult? read = null,
        NativePasteRetry? retryOverride = null)
    {
        var classification = NativePasteFailurePolicy.Classify(code);
        var retry = retryOverride ?? classification.Retry;
        return new NativePasteOutcome
        {
            Accepted = false,
            Code = code,
            Detail = detail ?? classification.Detail,
            GestureId = evidence.GestureId,
            TargetId = evidence.TargetId,
            GestureKind = evidence.Kind,
            SourceKind = read?.Kind ?? kind,
            Route = ClipboardImportRoute.None,
            Consumer = NativePasteConsumer.None,
            Recoverable = classification.Recoverable,
            Retry = retry,
            FileCount = read?.FileCount ?? 0,
            PixelCount = read?.PixelCount ?? 0,
            EncodedBytes = read?.EncodedBytes ?? 0,
            ReadOffSta = read?.ReadOffSta ?? false,
            KeepsBrowserDefault = retry == NativePasteRetry.RetryAfterUserAction,
        };
    }
}
