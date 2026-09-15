using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>D17 桥所处阶段。</summary>
public enum RemoteBridgePhase
{
    /// <summary>尚未开始本页面代际的握手（刚导航完、页面还没加载完，或刚换了一代）。</summary>
    Detached,

    /// <summary>宿主已发出 hello/capabilities，正在等页面的 capabilities/context。</summary>
    Handshaking,

    /// <summary>身份已绑定：可以开始批次。</summary>
    Ready,

    /// <summary>通道或协议层出现确定失败：停止一切收发并释放资源，等待新页面代际。</summary>
    Failed,

    /// <summary>已关闭（目标删除 / 凭据吊销 / 应用退出 / 桥自身释放）。</summary>
    Closed,
}

/// <summary>一次批次的请求：原生手势已经产生的捕获票据序列（顺序即批内文件顺序）。</summary>
public sealed record RemoteBridgeBatchRequest(string BatchId, IReadOnlyList<string> CaptureIds);

/// <summary>一次批次开始的结局（失败也带确定码与已释放的快照）。</summary>
public sealed record RemoteBridgeBatchResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? BatchId { get; init; }

    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public IReadOnlyList<string> FileIds { get; init; } = [];

    public IReadOnlyList<string> SnapshotIds { get; init; } = [];
}

/// <summary>一条入站封包的接收结论（拒绝时 <see cref="Code"/> 是确定判定码）。</summary>
public sealed record RemoteBridgeInboundResult
{
    public required bool Accepted { get; init; }

    public required string Code { get; init; }

    public string? Detail { get; init; }

    /// <summary>线协议消息类型（封包解不出来时为 <c>null</c>）。</summary>
    public string? Type { get; init; }

    /// <summary>握手消息（hello/capabilities/context）为 <c>true</c>：它不进传输状态机。</summary>
    public bool Handshake { get; init; }
}

/// <summary>一条出站帧的发送结论。</summary>
public sealed record RemoteBridgeOutboundResult(bool Sent, string? Code, string? Detail, string? Type);

/// <summary>一次被拒绝的收发记录（最近若干条，用于诊断与测试逐条断言）。</summary>
public sealed record RemoteBridgeRejection(long AtMs, bool Outbound, string Code, string? Detail, string? Type);

/// <summary>
/// D17 组合根（<b>平台中立</b>、无 P/Invoke、每个目标页面一个实例）：
/// 原生输入（D16）→ 受控暂存（D14）→ 生产 Core 传输（D10/D11）→ 页面通道（WebMessage 封包）。
///
/// 它同时是 D11 协调器的注入通道（<see cref="IAttachmentChannel"/> 的显式实现）：
/// 出站帧先过身份与大小复核、再封装、再交给通道适配器；入站封包先过封包结构、页面准入、
/// 身份复核、生产 codec，再入队交给协调器。事件处理器里<b>没有</b>第二套状态机：
/// 序号/在途窗口/ACK 幂等/迟到拒绝全部仍由 D10/D11 判定。
///
/// 生命周期由页面会话（<c>RemotePageSession</c>）拥有：本类只提供
/// <see cref="OnPageAdvanced"/>（导航：静默作废并释放）、<see cref="CancelBatch"/>（主动取消：
/// 发 cancel 并释放）与 <see cref="Close"/>（终止：取消、释放、解除监听），
/// 并且<b>不引用配对/心跳的任何类型或成员</b>——关闭桥与配对保活是两条互不相干的路径。
/// </summary>
public sealed class RemoteBridgeComposition : IAttachmentChannel, IDisposable
{
    /// <summary>入站队列硬上界：满了即 fail-closed，绝不无界增长。</summary>
    public const int MaxInboundQueue = 64;

    /// <summary>拒绝记录的保留条数（有界）。</summary>
    public const int MaxRetainedRejections = 64;

    private static readonly AttachmentPumpReport EmptyPump = new()
    {
        Received = 0,
        Sent = 0,
        ChunksSent = 0,
        Rejections = [],
    };

    private readonly RemotePageSessionState _page;
    private readonly IRemoteStagingPort _staging;
    private readonly IRemoteBridgeHandshakeSink _sink;
    private readonly AttachmentConcurrencyGate _gate;
    private readonly AttachmentLimits _hostLimits;
    private readonly IAttachmentClock _clock;
    private readonly Queue<AttachmentMessage> _inbound = new();
    private readonly List<RemoteBridgeRejection> _rejections = [];
    private readonly List<string> _releasedSnapshots = [];
    private IRemoteBridgeTransport? _transport;
    private AttachmentTransferCoordinator? _coordinator;
    private List<string> _activeSnapshots = [];
    private AttachmentIdentity? _identity;
    private string? _peerBuild;
    private AttachmentLimits _effectiveLimits;
    private RemoteBridgeCapabilities? _capabilities;
    private string? _failureCode;
    private string? _failureDetail;
    private string? _closeCode;
    private int _generation;
    private int _boundGeneration = -1;
    private int _outbound;
    private int _inboundFrames;
    private int _rejectedInbound;
    private int _rejectedOutbound;
    private bool _identityExpired;
    private bool _disposed;

    /// <summary>建立一个页面的组合根。通道可以在 WebView2 就绪后再接（<see cref="AttachTransport"/>）。</summary>
    public RemoteBridgeComposition(
        Guid targetId,
        RemotePageSessionState page,
        IRemoteStagingPort staging,
        IRemoteBridgeHandshakeSink sink,
        RemoteBridgeOptions? options = null,
        AttachmentConcurrencyGate? gate = null,
        IAttachmentClock? clock = null,
        AttachmentTransferTrace? trace = null)
    {
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("目标标识不得为空。", nameof(targetId));
        }

        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(sink);
        TargetId = targetId;
        _page = page;
        _staging = staging;
        _sink = sink;
        Options = options ?? new RemoteBridgeOptions();
        _hostLimits = Options.Limits;
        _effectiveLimits = Options.Limits;
        _gate = gate ?? new AttachmentConcurrencyGate();
        _clock = clock ?? SystemAttachmentClock.Instance;
        Trace = trace ?? new AttachmentTransferTrace();
    }

    /// <summary>本桥所属目标。</summary>
    public Guid TargetId { get; }

    /// <summary>构造选项。</summary>
    public RemoteBridgeOptions Options { get; }

    /// <summary>页面通道 id（来自页面会话；封包与通道适配器必须与它一致）。</summary>
    public string ChannelId => _page.ChannelId;

    /// <summary>当前阶段。</summary>
    public RemoteBridgePhase Phase { get; private set; } = RemoteBridgePhase.Detached;

    /// <summary>页面代际计数（每次导航 +1；身份与快照都按代际作废）。</summary>
    public int Generation => _generation;

    /// <summary>已绑定的线协议身份；尚未收到 context 时为 <c>null</c>。</summary>
    public AttachmentIdentity? Identity => _identity;

    /// <summary>对端 <c>hello.clientBuild</c>（未收到时为 <c>null</c>）。</summary>
    public string? PeerBuild => _peerBuild;

    /// <summary>生效限额（宿主策略与对端声明的逐项 min；未握手时为宿主策略）。</summary>
    public AttachmentLimits EffectiveLimits => _effectiveLimits;

    /// <summary>最近一次 capabilities 解析结果（未收到时为 <c>null</c>）。</summary>
    public RemoteBridgeCapabilities? Capabilities => _capabilities;

    /// <summary>当前截图所有者模式（未握手、或代际已变时为 <see cref="ScreenshotOwnerMode.Undecided"/>）。</summary>
    public ScreenshotOwnerMode ScreenshotMode =>
        _capabilities is not null && Phase is RemoteBridgePhase.Ready or RemoteBridgePhase.Handshaking
            ? _capabilities.Decision.Mode
            : ScreenshotOwnerMode.Undecided;

    /// <summary>最近一次截图所有者结论。</summary>
    public ScreenshotOwnerDecision ScreenshotDecision =>
        _capabilities?.Decision
        ?? new ScreenshotOwnerDecision(ScreenshotOwnerMode.Undecided, ScreenshotOwnerHandshake.UndecidedCode, "尚未收到 capabilities");

    /// <summary>已准入的 composer 上下文（未绑定时为"无会话"的保守值）。</summary>
    public ImportContextFacts ComposerContext { get; private set; } = AttachmentComposerContextStore.Unknown;

    /// <summary>桥是否可用（未关闭、未失败、未释放）。</summary>
    public bool IsUsable => !_disposed && Phase is not (RemoteBridgePhase.Closed or RemoteBridgePhase.Failed);

    /// <summary>通道适配器是否已接上。</summary>
    public bool IsAttached => _transport is not null;

    /// <summary>确定失败码（正常时为 <c>null</c>）。</summary>
    public string? FailureCode => _failureCode;

    /// <summary>确定失败说明。</summary>
    public string? FailureDetail => _failureDetail;

    /// <summary>关闭/失败码（诊断用）。</summary>
    public string? CloseCode => _closeCode;

    /// <summary>D11 协调器（尚未绑定身份时为 <c>null</c>）；测试与 UI 只读观察。</summary>
    public AttachmentTransferCoordinator? Coordinator => _coordinator;

    /// <summary>已发出的线协议帧数。</summary>
    public int OutboundFrames => _outbound;

    /// <summary>已接收并处理的入站封包数。</summary>
    public int InboundFrames => _inboundFrames;

    /// <summary>被拒绝的入站封包数。</summary>
    public int RejectedInbound => _rejectedInbound;

    /// <summary>被拒绝的出站帧数。</summary>
    public int RejectedOutbound => _rejectedOutbound;

    /// <summary>最近若干条拒绝记录（有界）。</summary>
    public IReadOnlyList<RemoteBridgeRejection> Rejections => _rejections;

    /// <summary>已释放的快照 id（每个退出路径都必须把暂存擦干净）。</summary>
    public IReadOnlyList<string> ReleasedSnapshots => _releasedSnapshots;

    /// <summary>组合层与协调器共用的确定性 trace。</summary>
    public AttachmentTransferTrace Trace { get; }

    // ————————————————————————————————————————————————————————————
    // 通道接入与释放
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 接上页面通道适配器。同一桥只接受一个适配器：第二个消费者一律拒绝
    /// （<see cref="RemoteBridgeCodes.AlreadyAttached"/>），且通道 id 必须与页面会话一致。
    /// </summary>
    public bool AttachTransport(IRemoteBridgeTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!IsUsable)
        {
            Reject(true, _closeCode ?? RemoteBridgeCodes.Failed, "桥不可用，拒绝接入通道", null);
            return false;
        }

        if (_transport is not null)
        {
            Reject(true, RemoteBridgeCodes.AlreadyAttached, "同一页面桥只接受一个通道适配器", null);
            return false;
        }

        if (!string.Equals(transport.ChannelId, ChannelId, StringComparison.Ordinal))
        {
            Reject(
                true,
                RemotePageSessionCodes.MessageChannelMismatch,
                "通道适配器的通道 id 与页面会话不一致",
                null);
            return false;
        }

        _transport = transport;
        Trace.Record(_clock.NowMs, "state", "bridge", null, null, "transport：通道适配器已接入");
        return true;
    }

    /// <summary>当前接上的通道适配器（未接入时为 <c>null</c>）。</summary>
    public IRemoteBridgeTransport? Transport => _transport;

    // ————————————————————————————————————————————————————————————
    // 握手
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 主文档已验证、能力已授予：宿主发出自己的 <c>hello</c> 与 <c>capabilities</c>。
    /// 重复调用在同一代际内是幂等重放（不再发第二遍）。
    /// </summary>
    public RemoteBridgeOutboundResult StartHandshake()
    {
        if (!Usable(out var refusal))
        {
            return refusal;
        }

        if (_page.Capability is null)
        {
            return RefuseOutbound(RemoteBridgeCodes.NotReady, "页面尚未获得能力：不发握手", "hello");
        }

        if (Phase == RemoteBridgePhase.Ready || Phase == RemoteBridgePhase.Handshaking)
        {
            return new RemoteBridgeOutboundResult(true, null, "握手已开始（幂等重放）", "hello");
        }

        var hello = SendHostFrame(Wire.Hello(Options.ClientBuild));
        if (!hello.Sent)
        {
            return hello;
        }

        var capabilities = SendHostFrame(Wire.Capabilities(_hostLimits));
        if (!capabilities.Sent)
        {
            return capabilities;
        }

        Phase = RemoteBridgePhase.Handshaking;
        Trace.Record(_clock.NowMs, "state", "bridge", null, null, $"handshake：宿主 hello+capabilities 已发出（generation={_generation}）");
        return capabilities;
    }

    /// <summary>宿主向页面发送一条<b>宿主发起</b>的线协议帧（握手、context 回执）。每次发送都复核身份与大小。</summary>
    public RemoteBridgeOutboundResult SendHostFrame(string wireJson)
    {
        ArgumentNullException.ThrowIfNull(wireJson);
        if (!Usable(out var refusal))
        {
            return refusal;
        }

        var decoded = AttachmentCodec.Decode(wireJson);
        if (!decoded.Ok)
        {
            return RefuseOutbound(decoded.Code!, decoded.Detail ?? "codec 拒绝宿主帧", null);
        }

        return SendMessage(decoded.Message!);
    }

    // ————————————————————————————————————————————————————————————
    // 入站
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 处理一条来自页面的 WebMessage 封包。顺序固定：
    /// 关闭/失败 → 文本归一 → 大小 → 封包结构 → 页面准入（通道/代际/能力）→ 生产 codec 解码
    /// → 握手路由 或 身份复核后入队 → 驱动一次 <see cref="Pump"/>。
    /// 任何拒绝都返回确定码并记账，绝不静默丢弃。
    /// </summary>
    /// <param name="payload">入站封包文本。</param>
    /// <param name="drivePump">
    /// 是否在这次投递里立即驱动一次 <see cref="Pump"/>。生产通道适配器按帧投递（<c>true</c>）；
    /// 调用方若一次灌入多条，可先用 <c>false</c> 入队再显式驱动，队列因此有硬上界。
    /// </param>
    public RemoteBridgeInboundResult DeliverInbound(string? payload, bool drivePump = true)
    {
        _inboundFrames += 1;
        if (!IsUsable)
        {
            return RejectInbound(_closeCode ?? RemoteBridgeCodes.Failed, "桥已关闭或已失败", null);
        }

        var envelope = RemoteBridgeEnvelopeCodec.Decode(payload);
        if (!envelope.Ok || envelope.Envelope is null)
        {
            return RejectInbound(envelope.Code!, envelope.Detail ?? "封包不合法", null);
        }

        var pageEpoch = new RemotePageEpoch(envelope.Envelope.Epoch);
        var admission = _page.AdmitMessage(new RemotePageInboundMessage(
            envelope.Envelope.ChannelId,
            pageEpoch,
            "bridge-envelope"));
        if (!admission.Accepted)
        {
            return RejectInbound(admission.Code, $"封包准入被拒：{admission.Code}", null);
        }

        var frameJson = envelope.Envelope.FrameJson;
        var decoded = AttachmentCodec.Decode(frameJson);
        if (!decoded.Ok)
        {
            return RejectInbound(decoded.Code!, decoded.Detail ?? "codec 拒绝入站帧", null);
        }

        var message = decoded.Message!;
        if (message.Kind is WireMessageKind.Hello or WireMessageKind.Capabilities or WireMessageKind.Context)
        {
            return RouteHandshake(message, admission.Code);
        }

        if (_identity is null || _coordinator is null)
        {
            return RejectInbound("no-session", "尚未建立 context 身份：拒绝操作消息", message.Type);
        }

        if (_identityExpired || _generation != _boundGeneration)
        {
            return RejectInbound("context-changed", "页面代际已变：拒绝操作消息", message.Type);
        }

        if (!FrameIdentityMatches(message, out var detail))
        {
            return RejectInbound("context-changed", detail, message.Type);
        }

        if (_inbound.Count >= MaxInboundQueue)
        {
            Fail(RemoteBridgeCodes.InboundOverflow, $"入站队列已达上界 {MaxInboundQueue}");
            return RejectInbound(RemoteBridgeCodes.InboundOverflow, "入站队列已满：停止接收", message.Type);
        }

        _inbound.Enqueue(message);
        Trace.Record(
            _clock.NowMs,
            "bridge-in",
            message.Type,
            message.BatchId,
            message.FileId,
            "入站封包通过身份与大小复核，交给 D10/D11 判定");
        if (!drivePump)
        {
            return Accept(message.Type, handshake: false);
        }

        // D10/D11 的判定是权威的：迟到（batch-closed / cancelled）、重复、序号问题的确定码
        // 必须原样返回给调用方，绝不"收下了但内部丢掉"。
        var report = Pump();
        if (report.Rejections.Count > 0)
        {
            var rejection = report.Rejections[^1];
            var separator = rejection.IndexOf(':', StringComparison.Ordinal);
            var code = separator >= 0 ? rejection[(separator + 1)..] : rejection;
            return RejectInbound(code, $"D10/D11 拒绝入站帧：{code}", message.Type);
        }

        return new RemoteBridgeInboundResult
        {
            Accepted = true,
            Code = RemotePageSessionCodes.Accepted,
            Type = message.Type,
        };
    }

    private RemoteBridgeInboundResult RouteHandshake(AttachmentMessage message, string admissionCode)
    {
        switch (message.Kind)
        {
            case WireMessageKind.Hello:
                _peerBuild = message.ClientBuild;
                Trace.Record(_clock.NowMs, "bridge-in", "hello", null, null, $"对端构建：{_peerBuild}");
                return Accept(message.Type, handshake: true);

            case WireMessageKind.Capabilities:
                {
                    var parsed = RemoteBridgeHandshake.ParseCapabilities(
                        message,
                        _hostLimits,
                        Options.NativeCaptureAvailable);
                    _capabilities = parsed;
                    _effectiveLimits = parsed.EffectiveLimits;
                    if (Phase == RemoteBridgePhase.Detached)
                    {
                        Phase = RemoteBridgePhase.Handshaking;
                    }

                    // 真实消费者一：截图所有者台账（决定后续位图粘贴走 native-paste 还是 bridge）。
                    var decision = _sink.RecordScreenshotOwner(TargetId, _page.Epoch, parsed.Facts);
                    Trace.Record(
                        _clock.NowMs,
                        "decision",
                        "capabilities",
                        null,
                        null,
                        $"截图所有者={decision.Mode}（{decision.Code}），生效限额已按对端声明钳制",
                        AttachmentCodec.ToCanonical(message));
                    return Accept(message.Type, handshake: true);
                }

            case WireMessageKind.Context:
                {
                    if (_capabilities is null)
                    {
                        return RejectInbound(
                            RemoteBridgeCodes.HandshakeIncomplete,
                            "尚未收到 capabilities：拒绝建立身份",
                            message.Type);
                    }

                    var parsed = RemoteBridgeHandshake.ParseContext(message, pageAdmitted: true, admissionCode);
                    if (string.IsNullOrWhiteSpace(parsed.Identity.SessionId))
                    {
                        return RejectInbound("no-session", RemoteBridgeHandshake.NoSessionDetail, message.Type);
                    }

                    if (_identity is not null)
                    {
                        if (_identity != parsed.Identity)
                        {
                            return RejectInbound(
                                RemoteBridgeCodes.SecondContext,
                                "同一页面代际内出现第二个不同身份：拒绝换绑",
                                message.Type);
                        }

                        return Accept(message.Type, handshake: true);
                    }

                    _identity = parsed.Identity;
                    _identityExpired = false;
                    _boundGeneration = _generation;
                    _coordinator = new AttachmentTransferCoordinator(
                        parsed.Identity,
                        this,
                        _clock,
                        _effectiveLimits,
                        _gate,
                        Trace);
                    ComposerContext = parsed.Facts;

                    // 真实消费者二：composer 上下文台账（决定导入闸门）。
                    _sink.RecordComposerContext(TargetId, _page.Epoch, parsed.Facts, pageAdmitted: true);
                    Phase = RemoteBridgePhase.Ready;
                    Trace.Record(
                        _clock.NowMs,
                        "state",
                        "context",
                        null,
                        null,
                        $"身份已绑定：sessionId={parsed.Identity.SessionId} documentEpoch={parsed.Identity.DocumentEpoch} composerEpoch={parsed.Identity.ComposerEpoch}");

                    // 回执宿主 context：页面据此绑定同一身份（字段与页面声明逐字一致）。
                    var echo = SendMessage(AttachmentCodec.Decode(Wire.Context(parsed.Identity)).Message!);
                    if (!echo.Sent)
                    {
                        return RejectInbound(echo.Code!, echo.Detail ?? "宿主 context 回执发送失败", message.Type);
                    }

                    return Accept(message.Type, handshake: true);
                }

            default:
                return RejectInbound("unknown-message-type", "不是握手消息", message.Type);
        }
    }

    // ————————————————————————————————————————————————————————————
    // 传输驱动
    // ————————————————————————————————————————————————————————————

    /// <summary>驱动一次协调器（排空入站、判定超时、推进传输）；尚未绑定身份时为空转。</summary>
    public AttachmentPumpReport Pump()
    {
        if (_coordinator is null)
        {
            return EmptyPump;
        }

        var report = _coordinator.Pump();
        AfterPump();
        return report;
    }

    /// <summary>空闲时也要推进超时判定（生产由页面会话的定时器驱动）。</summary>
    public void PumpIdle()
    {
        if (_coordinator is null || _coordinator.BatchPhase != AttachmentBatchPhase.Open)
        {
            return;
        }

        Pump();
    }

    /// <summary>把当前通道里已经排队的封包全部取走并处理（生产由 WebMessage 事件驱动）。</summary>
    public int DrainTransport()
    {
        var transport = _transport;
        if (transport is null)
        {
            return 0;
        }

        var count = 0;
        while (transport.TryReceiveFrame(out var payload))
        {
            count += 1;
            DeliverInbound(payload);
        }

        return count;
    }

    // ————————————————————————————————————————————————————————————
    // 批次：原生捕获 → 暂存 → 协调器
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 用一次原生手势产生的捕获票据开始一个批次：先过页面准入与桥状态，再做 D14 受控快照，
    /// 然后把快照字节源交给 D11 协调器。任何一步失败都释放已经产生的快照并返回确定码。
    /// </summary>
    public RemoteBridgeBatchResult StartBatch(RemoteBridgeBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsUsable)
        {
            return RefuseBatch(_closeCode ?? RemoteBridgeCodes.Failed, "桥已关闭或已失败", request.BatchId);
        }

        var work = _page.AdmitNewWork();
        if (!work.Accepted)
        {
            return RefuseBatch(work.Code, "页面当前不接受新操作", request.BatchId);
        }

        var coordinator = _coordinator;
        if (Phase != RemoteBridgePhase.Ready || coordinator is null || _identity is null)
        {
            return RefuseBatch(RemoteBridgeCodes.NotReady, "握手尚未完成：不能开始批次", request.BatchId);
        }

        if (_identityExpired || _generation != _boundGeneration)
        {
            return RefuseBatch("context-changed", "页面代际已变：不能开始批次", request.BatchId);
        }

        if (coordinator.BatchPhase == AttachmentBatchPhase.Open)
        {
            return RefuseBatch("batch-in-progress", $"已有开放批次 {coordinator.BatchId}", request.BatchId);
        }

        if (request.CaptureIds.Count == 0)
        {
            return RefuseBatch(RemoteBridgeCodes.NoCapture, "本次手势没有产生任何捕获票据", request.BatchId);
        }

        if (request.CaptureIds.Count > _effectiveLimits.MaxFilesPerBatch)
        {
            return RefuseBatch(
                "limit-batch-files",
                $"捕获 {request.CaptureIds.Count} 个文件，超过上限 {_effectiveLimits.MaxFilesPerBatch}",
                request.BatchId);
        }

        var begin = _staging.BeginBatch(request.BatchId, request.CaptureIds.Count);
        if (!begin.Ok)
        {
            return RefuseBatch(begin.Code ?? "batch-not-open", begin.Detail, request.BatchId);
        }

        var receipts = new List<StagingCaptureReceipt>(request.CaptureIds.Count);
        foreach (var captureId in request.CaptureIds)
        {
            var receipt = _staging.Capture(captureId);
            if (!receipt.Ok || string.IsNullOrEmpty(receipt.SnapshotId))
            {
                ReleaseReceipts(receipts, "capture-failed");
                return RefuseBatch(
                    receipt.Code ?? RemoteBridgeCodes.SnapshotUnavailable,
                    receipt.Detail ?? "暂存快照失败",
                    request.BatchId);
            }

            receipts.Add(receipt);
        }

        var totalBytes = receipts.Sum(receipt => receipt.ByteLength);
        if (totalBytes > _effectiveLimits.MaxBatchBytes)
        {
            ReleaseReceipts(receipts, "limit-batch-bytes");
            return RefuseBatch(
                "limit-batch-bytes",
                $"批总字节 {totalBytes} 超过上限 {_effectiveLimits.MaxBatchBytes}",
                request.BatchId);
        }

        var opened = coordinator.OpenBatch(request.BatchId, receipts.Count, checked((int)totalBytes));
        if (!opened.Ok)
        {
            ReleaseReceipts(receipts, "batch-open-failed");
            return RefuseBatch(opened.Code ?? "batch-not-open", opened.Detail, request.BatchId);
        }

        // 出站帧被通道拒绝时桥已经 fail-closed：这里必须立刻停手，不能继续读字节。
        if (Phase == RemoteBridgePhase.Failed)
        {
            ReleaseReceipts(receipts, "channel-failed");
            return RefuseBatch(FailureCode ?? RemoteBridgeCodes.ChannelUnavailable, FailureDetail, request.BatchId);
        }

        var fileIds = new List<string>(receipts.Count);
        var snapshotIds = new List<string>(receipts.Count);
        for (var index = 0; index < receipts.Count; index += 1)
        {
            var receipt = receipts[index];
            var fileId = string.Create(CultureInfo.InvariantCulture, $"{request.BatchId}-{index}");
            var name = receipt.DisplayName ?? "attachment";
            IAttachmentByteSource source;
            try
            {
                source = _staging.OpenSnapshot(receipt.SnapshotId!);
            }
            catch (AttachmentStagingException error)
            {
                coordinator.Invalidate();
                ReleaseReceipts(receipts, "snapshot-open-failed");
                return RefuseBatch(error.Code, error.Message, request.BatchId);
            }

            if (source.ByteLength != receipt.ByteLength)
            {
                source.Dispose();
                coordinator.Invalidate();
                ReleaseReceipts(receipts, "snapshot-size-mismatch");
                return RefuseBatch(
                    "size-mismatch",
                    $"快照 {receipt.SnapshotId} 的字节数 {source.ByteLength} 与回执 {receipt.ByteLength} 不符",
                    request.BatchId);
            }

            var admitted = coordinator.AdmitFile(fileId, name, RemoteBridgeMime.FromLeafName(name), source, receipt.Sha256);
            if (!admitted.Ok)
            {
                coordinator.Invalidate();
                ReleaseReceipts(receipts, "admit-failed");
                return RefuseBatch(admitted.Code ?? "invalid-field-value", admitted.Detail, request.BatchId);
            }

            if (Phase == RemoteBridgePhase.Failed)
            {
                ReleaseReceipts(receipts, "channel-failed");
                return RefuseBatch(FailureCode ?? RemoteBridgeCodes.ChannelUnavailable, FailureDetail, request.BatchId);
            }

            fileIds.Add(fileId);
            snapshotIds.Add(receipt.SnapshotId!);
        }

        _activeSnapshots = snapshotIds;
        Trace.Record(
            _clock.NowMs,
            "decision",
            "batch",
            request.BatchId,
            null,
            $"批次开始：files={fileIds.Count} bytes={totalBytes}（快照在批结束/取消/失败时释放）");
        Pump();
        if (Phase == RemoteBridgePhase.Failed)
        {
            _activeSnapshots = [];
            return RefuseBatch(FailureCode ?? RemoteBridgeCodes.ChannelUnavailable, FailureDetail, request.BatchId);
        }

        return new RemoteBridgeBatchResult
        {
            Ok = true,
            BatchId = request.BatchId,
            FileCount = fileIds.Count,
            TotalBytes = totalBytes,
            FileIds = fileIds,
            SnapshotIds = snapshotIds,
        };
    }

    /// <summary>主动取消：发出一条 cancel、停止后续传输、释放全部源与暂存快照，迟到结果一律拒绝。</summary>
    public AttachmentCoordinatorResult CancelBatch(string reason = "cancelled", string? stage = null)
    {
        var coordinator = _coordinator;
        if (coordinator is null)
        {
            return new AttachmentCoordinatorResult { Ok = false, Code = "batch-not-open", Detail = "尚未建立身份" };
        }

        var result = coordinator.Cancel(reason, stage);
        ReleaseSnapshots(_activeSnapshots, "cancelled");
        return result;
    }

    // ————————————————————————————————————————————————————————————
    // 生命周期
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 页面导航/换代际：静默作废本代际的一切（不发任何帧——旧文档已经在卸载），
    /// 释放协调器、字节源与暂存快照，回到 <see cref="RemoteBridgePhase.Detached"/>。
    /// 旧代际的迟到封包此后得到 <c>message-epoch-stale</c>（由页面会话代际判定）。
    /// </summary>
    public void OnPageAdvanced()
    {
        if (Phase == RemoteBridgePhase.Closed)
        {
            return;
        }

        _generation += 1;
        _identityExpired = true;
        if (_coordinator is not null)
        {
            _coordinator.Invalidate();
            _coordinator.Dispose();
            _coordinator = null;
        }

        _identity = null;
        _boundGeneration = -1;
        _inbound.Clear();
        ReleaseSnapshots(_activeSnapshots, "navigation");
        _activeSnapshots = [];
        _peerBuild = null;
        _capabilities = null;
        _effectiveLimits = _hostLimits;
        ComposerContext = AttachmentComposerContextStore.Unknown;
        if (Phase != RemoteBridgePhase.Failed)
        {
            Phase = RemoteBridgePhase.Detached;
        }

        Trace.Record(_clock.NowMs, "state", "bridge", null, null, $"navigation：代际推进到 {_generation}，身份与快照全部作废");
    }

    /// <summary>能力丢失（来源不符/外部链接/未加载完成）：不再接受新批次，等待下一次主文档加载。</summary>
    public void OnCapabilityLost()
    {
        if (Phase is RemoteBridgePhase.Closed or RemoteBridgePhase.Failed)
        {
            return;
        }

        _identityExpired = true;
        Phase = RemoteBridgePhase.Detached;
        ReleaseSnapshots(_activeSnapshots, "capability-lost");
        _activeSnapshots = [];
        Trace.Record(_clock.NowMs, "state", "bridge", null, null, "capability-lost：停止接收新操作");
    }

    /// <summary>
    /// 终止释放：取消在途批次（发 cancel）、释放快照与协调器、解除通道监听与入站队列。
    /// 幂等；本方法<b>只</b>触碰桥自己的资源，不触碰配对、心跳或目标库。
    /// </summary>
    public void Close(string code)
    {
        if (Phase == RemoteBridgePhase.Closed)
        {
            return;
        }

        _closeCode = code;
        if (_coordinator is { BatchPhase: AttachmentBatchPhase.Open })
        {
            _coordinator.Cancel("cancelled");
        }

        ReleaseSnapshots(_activeSnapshots, "close");
        _activeSnapshots = [];
        _coordinator?.Dispose();
        _coordinator = null;
        _identity = null;
        _identityExpired = true;
        _inbound.Clear();
        _transport?.Close();
        Phase = RemoteBridgePhase.Closed;
        Trace.Record(_clock.NowMs, "state", "bridge", null, null, $"close：{code}（桥自有资源已全部释放）");
    }

    /// <summary>释放本桥（重复调用安全）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close(RemoteBridgeCodes.Disposed);
        _transport?.Dispose();
        _transport = null;
        _disposed = true;
    }

    // ————————————————————————————————————————————————————————————
    // IAttachmentChannel（D11 的注入通道）
    // ————————————————————————————————————————————————————————————

    void IAttachmentChannel.Send(string wireJson)
    {
        var decoded = AttachmentCodec.Decode(wireJson);
        if (!decoded.Ok)
        {
            Fail(decoded.Code!, decoded.Detail ?? "协调器发出了 codec 拒绝的帧");
            return;
        }

        var result = SendMessage(decoded.Message!);
        if (!result.Sent)
        {
            Fail(result.Code!, result.Detail ?? "出站帧被拒绝");
        }
    }

    bool IAttachmentChannel.TryReceive([NotNullWhen(true)] out string? wireJson)
    {
        if (_inbound.Count == 0)
        {
            wireJson = null;
            return false;
        }

        wireJson = AttachmentCodec.Encode(_inbound.Dequeue());
        return true;
    }

    // ————————————————————————————————————————————————————————————
    // 内部
    // ————————————————————————————————————————————————————————————

    private bool FrameIdentityMatches(AttachmentMessage message, out string detail)
    {
        var identity = _identity!;
        if (message.SessionId is not null && !string.Equals(message.SessionId, identity.SessionId, StringComparison.Ordinal))
        {
            detail = $"sessionId {message.SessionId} 与已绑定身份不符";
            return false;
        }

        if (message.TargetId is not null && !string.Equals(message.TargetId, identity.TargetId, StringComparison.Ordinal))
        {
            detail = $"targetId {message.TargetId} 与已绑定身份不符";
            return false;
        }

        if (message.DocumentEpoch is not null && message.DocumentEpoch != identity.DocumentEpoch)
        {
            detail = $"documentEpoch {message.DocumentEpoch} 与已绑定身份不符";
            return false;
        }

        if (message.ComposerEpoch is not null && message.ComposerEpoch != identity.ComposerEpoch)
        {
            detail = $"composerEpoch {message.ComposerEpoch} 与已绑定身份不符";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    private RemoteBridgeOutboundResult SendMessage(AttachmentMessage message)
    {
        var transport = _transport;
        if (transport is null)
        {
            return RefuseOutbound(RemoteBridgeCodes.NotAttached, "通道适配器尚未接入", message.Type);
        }

        if (!Usable(out var refusal))
        {
            return refusal with { Type = message.Type };
        }

        if (_identity is not null && message.Kind is not (WireMessageKind.Hello or WireMessageKind.Capabilities))
        {
            if (_identityExpired || _generation != _boundGeneration)
            {
                return RefuseOutbound("context-changed", "页面代际已变：拒绝出站帧", message.Type);
            }

            if (!FrameIdentityMatches(message, out var detail))
            {
                return RefuseOutbound("context-changed", detail, message.Type);
            }
        }

        var frame = message.Fields;
        var frameJson = frame.ToJsonString();
        if (Encoding.UTF8.GetByteCount(frameJson) > AttachmentProtocol.MaxMessageBytes)
        {
            return RefuseOutbound("message-too-large", "出站帧超过冻结消息上界", message.Type);
        }

        if (!RemoteBridgeEnvelopeCodec.TryToWireEpoch(_page.Epoch, out var wireEpoch))
        {
            return RefuseOutbound("integer-out-of-range", "页面代际超出封包 epoch 表示范围", message.Type);
        }

        var envelope = RemoteBridgeEnvelopeCodec.Encode(ChannelId, wireEpoch, frame);
        var outcome = transport.SendFrame(envelope);
        if (!outcome.Sent)
        {
            return RefuseOutbound(
                outcome.Code ?? RemoteBridgeCodes.ChannelUnavailable,
                outcome.Detail ?? "通道拒绝发送",
                message.Type);
        }

        _outbound += 1;
        Trace.Record(
            _clock.NowMs,
            "bridge-out",
            message.Type,
            message.BatchId,
            message.FileId,
            "出站帧通过身份与大小复核，已交给页面通道",
            AttachmentCodec.ToCanonical(message));
        return new RemoteBridgeOutboundResult(true, null, null, message.Type);
    }

    private void AfterPump()
    {
        var coordinator = _coordinator;
        if (coordinator is null)
        {
            return;
        }

        if (coordinator.BatchPhase is AttachmentBatchPhase.Closed or AttachmentBatchPhase.Cancelled)
        {
            ReleaseSnapshots(_activeSnapshots, $"batch-{coordinator.BatchPhase.ToString().ToLowerInvariant()}");
            _activeSnapshots = [];
        }
    }

    private void ReleaseSnapshots(List<string> snapshotIds, string because)
    {
        if (snapshotIds.Count == 0)
        {
            return;
        }

        foreach (var snapshotId in snapshotIds)
        {
            var result = _staging.Release(snapshotId);
            _releasedSnapshots.Add(snapshotId);
            Trace.Record(
                _clock.NowMs,
                "decision",
                "release",
                null,
                null,
                result.Ok
                    ? $"释放暂存快照（{because}）"
                    : $"释放暂存快照失败（{because}）：{result.Code}");
        }
    }

    private void ReleaseReceipts(List<StagingCaptureReceipt> receipts, string because) =>
        ReleaseSnapshots([.. receipts.Select(receipt => receipt.SnapshotId ?? string.Empty).Where(id => id.Length > 0)], because);

    private void Fail(string code, string detail)
    {
        if (Phase is RemoteBridgePhase.Closed or RemoteBridgePhase.Failed)
        {
            return;
        }

        _failureCode = code;
        _failureDetail = detail;
        Phase = RemoteBridgePhase.Failed;
        _identityExpired = true;
        _coordinator?.Invalidate();
        _coordinator?.Dispose();
        _coordinator = null;
        ReleaseSnapshots(_activeSnapshots, "failed");
        _activeSnapshots = [];
        _inbound.Clear();
        _transport?.Close();
        Trace.Record(_clock.NowMs, "state", "bridge", null, null, $"failed：{code}（{detail}）");
    }

    private bool Usable(out RemoteBridgeOutboundResult refusal)
    {
        if (!IsUsable)
        {
            refusal = RefuseOutbound(_closeCode ?? RemoteBridgeCodes.Failed, "桥已关闭或已失败", null);
            return false;
        }

        refusal = new RemoteBridgeOutboundResult(true, null, null, null);
        return true;
    }

    private RemoteBridgeOutboundResult RefuseOutbound(string code, string detail, string? type)
    {
        _rejectedOutbound += 1;
        Reject(true, code, detail, type);
        return new RemoteBridgeOutboundResult(false, code, detail, type);
    }

    private RemoteBridgeInboundResult RejectInbound(string code, string detail, string? type)
    {
        _rejectedInbound += 1;
        Reject(false, code, detail, type);
        return new RemoteBridgeInboundResult { Accepted = false, Code = code, Detail = detail, Type = type };
    }

    private void Reject(bool outbound, string code, string detail, string? type)
    {
        _rejections.Add(new RemoteBridgeRejection(_clock.NowMs, outbound, code, detail, type));
        while (_rejections.Count > MaxRetainedRejections)
        {
            _rejections.RemoveAt(0);
        }
    }

    private static RemoteBridgeInboundResult Accept(string? type, bool handshake) =>
        new()
        {
            Accepted = true,
            Code = RemotePageSessionCodes.Accepted,
            Type = type,
            Handshake = handshake,
        };

    private static RemoteBridgeBatchResult RefuseBatch(string code, string? detail, string? batchId) =>
        new() { Ok = false, Code = code, Detail = detail, BatchId = batchId };

    /// <summary>宿主出站帧的构造器（字段顺序必须与冻结顺序一致；构造后一律再过 codec）。</summary>
    private static class Wire
    {
        public static string Hello(string clientBuild) => new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "hello",
            ["clientBuild"] = clientBuild,
        }.ToJsonString();

        public static string Capabilities(AttachmentLimits limits) => new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "capabilities",
            ["features"] = new JsonArray([.. AttachmentProtocol.WireFeatures.Select(feature => (JsonNode?)JsonValue.Create(feature))]),
            ["limits"] = new JsonObject
            {
                ["maxFileBytes"] = limits.MaxFileBytes,
                ["maxFilesPerBatch"] = limits.MaxFilesPerBatch,
                ["maxBatchBytes"] = limits.MaxBatchBytes,
                ["maxScreenshotPixels"] = limits.MaxScreenshotPixels,
                ["maxStagingBytesPerTarget"] = limits.MaxStagingBytesPerTarget,
                ["maxConcurrentTargets"] = limits.MaxConcurrentTargets,
            },
        }.ToJsonString();

        public static string Context(AttachmentIdentity identity) => new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "context",
            ["sessionId"] = identity.SessionId,
            ["targetId"] = identity.TargetId,
            ["documentEpoch"] = identity.DocumentEpoch,
            ["composerEpoch"] = identity.ComposerEpoch,
            ["composerScope"] = identity.ComposerScope,
        }.ToJsonString();
    }
}
