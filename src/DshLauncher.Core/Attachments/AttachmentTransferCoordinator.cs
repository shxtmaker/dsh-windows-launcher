using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DshLauncher.Core.Attachments;

/// <summary>批次阶段（协调器本地；与线上 batch-end 的幂等语义一一对应）。</summary>
public enum AttachmentBatchPhase
{
    /// <summary>尚未开始任何批次。</summary>
    None,

    /// <summary>batch-begin 已发出，批次进行中。</summary>
    Open,

    /// <summary>batch-end 已发出并接受，批次关闭；迟到的 file-end/chunk/import-result 一律拒绝。</summary>
    Closed,

    /// <summary>已取消（本地失败、超时或对端 cancel）：停止后续传输、释放源、拒绝迟到结果。</summary>
    Cancelled,
}

/// <summary>单文件阶段。</summary>
public enum AttachmentFilePhase
{
    /// <summary>已准入但尚未开始：<b>一个字节都没读</b>（背压的可见证据）。</summary>
    Pending,

    /// <summary>file-begin 已发出，正在发块（≤ 2 块在途）。</summary>
    Transferring,

    /// <summary>file-end 已发出，等待 import-result。</summary>
    AwaitingImport,

    /// <summary>import-result 报 staged。</summary>
    Staged,

    /// <summary>import-result 报 failed，或本地失败（源错误、超时、校验不符）。</summary>
    Failed,

    /// <summary>import-result 报 partial。</summary>
    Partial,

    /// <summary>因取消/超时/身份失效而未完成（已确认的 staged 结果不会被改写）。</summary>
    Cancelled,
}

/// <summary>逐 fileId 的对外记录；不存在"批次级布尔值"这种粒度。</summary>
public sealed record AttachmentTransferFileRecord
{
    public required string FileId { get; init; }

    public required string Name { get; init; }

    public required string Mime { get; init; }

    public required int ByteLength { get; init; }

    /// <summary>已写入通道的原始字节数。</summary>
    public required int SentBytes { get; init; }

    /// <summary>已被 ack 确认进入对端接收缓冲的原始字节数。</summary>
    public required int AckedBytes { get; init; }

    /// <summary>已发送未 ack 的块数（≤ 2）。</summary>
    public required int InFlightChunks { get; init; }

    public required AttachmentFilePhase Phase { get; init; }

    /// <summary>线协议结果状态（staged/failed/partial），未确定时为 null。</summary>
    public string? ResultStatus { get; init; }

    /// <summary>失败码：线协议冻结码或 <see cref="AttachmentCoordinatorCodes"/> 里的本地码。</summary>
    public string? Code { get; init; }

    public required IReadOnlyList<string> AttachmentIds { get; init; }

    /// <summary>该文件是否恰好触发过一次草稿导入（file-end 发出即计一次）。</summary>
    public required bool ImportInvoked { get; init; }

    /// <summary>字节源是否已释放（每条退出路径都必须为 true）。</summary>
    public required bool SourceDisposed { get; init; }
}

/// <summary>协调器本地调用的结果（准入、开始批次、取消等）。</summary>
public sealed record AttachmentCoordinatorResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? BatchId { get; init; }

    public string? FileId { get; init; }

    /// <summary>是否为幂等重放（例如重复 cancel）。</summary>
    public bool Duplicate { get; init; }
}

/// <summary>一次 <see cref="AttachmentTransferCoordinator.Pump"/> 的观测结果。</summary>
public sealed record AttachmentPumpReport
{
    /// <summary>本次处理的入站帧数。</summary>
    public required int Received { get; init; }

    /// <summary>本次发出的帧数。</summary>
    public required int Sent { get; init; }

    /// <summary>本次发出的 chunk 数。</summary>
    public required int ChunksSent { get; init; }

    /// <summary>当前活动文件（每目标同时最多一个）。</summary>
    public string? ActiveFileId { get; init; }

    /// <summary>因全局并发上限仍在等待的文件。</summary>
    public string? QueuedFileId { get; init; }

    /// <summary>因全局等待队列已满而本次未入队的文件（保持 pending，不读字节）。</summary>
    public string? StarvedFileId { get; init; }

    /// <summary>本次入站被拒绝的摘要（"类型:拒绝码"）。</summary>
    public required IReadOnlyList<string> Rejections { get; init; }

    /// <summary>停止原因（无则 null）：context-changed / cancelled / batch-closed / disposed。</summary>
    public string? Stop { get; init; }
}

/// <summary>
/// 可移植分块传输协调器（D11，发送端生产实现）。
///
/// 职责与边界：
/// <list type="bullet">
/// <item>按 256 KiB 原始块驱动 <c>batch-begin → file-begin → chunk/ack → file-end →
/// import-result → batch-end</c>，每文件最多 2 块在途、每目标同时只组装 1 个文件、
/// 全局并发受注入的 <see cref="AttachmentConcurrencyGate"/> 约束（有界队列）；</item>
/// <item>所有帧都由 D10 冻结 codec 构造与校验：<b>出站帧先喂给生产状态机
/// <see cref="AttachmentSession"/>，被它拒绝的帧绝不发出</b>；入站帧同样交给它判定，
/// ack 幂等/序号、import-result 校验、超时策略全部复用 D10 语义，不另写一套编解码或判定；</item>
/// <item>去重与"已确认成功项永不重导"复用 <see cref="AttachmentReplayCache"/>：
/// 键 <c>documentEpoch:composerEpoch:batchId:fileId</c>、容量 64、TTL 120 s、活动条目钉住不可淘汰；</item>
/// <item>字节源、通道、时钟全部注入，因此没有 sleep、没有后台线程、没有 Windows API，
/// 同一套生产代码在 Linux 上可被确定性驱动。</item>
/// </list>
/// </summary>
public sealed class AttachmentTransferCoordinator : IDisposable
{
    private readonly IAttachmentChannel _channel;
    private readonly IAttachmentClock _clock;
    private readonly AttachmentLimits _limits;
    private readonly AttachmentConcurrencyGate _gate;
    private readonly AttachmentTransferTrace _trace;
    private readonly AttachmentReplayCache _cache;
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly int? _replayCapacity;
    private readonly int? _replayLifetimeMs;
    private AttachmentIdentity _identity;
    private AttachmentMessage? _contextMessage;
    private AttachmentSession _mirror;
    private BatchState? _batch;
    private string? _activeFileId;
    private bool _gateHeld;

    /// <summary>是否已在全局闸门的等待队列里排队（排队也是占用，必须在释放时撤销）。</summary>
    private bool _gateQueued;
    private bool _identityExpired;
    private bool _disposed;
    private int _outbound;
    private int _inbound;
    private int _chunksSent;
    private int _imports;
    private int _rejections;

    public AttachmentTransferCoordinator(
        AttachmentIdentity identity,
        IAttachmentChannel channel,
        IAttachmentClock? clock = null,
        AttachmentLimits? limits = null,
        AttachmentConcurrencyGate? gate = null,
        AttachmentTransferTrace? trace = null,
        int? replayCapacity = null,
        int? replayLifetimeMs = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(channel);
        _identity = identity;
        _channel = channel;
        _clock = clock ?? SystemAttachmentClock.Instance;
        _limits = limits ?? new AttachmentLimits();
        _gate = gate ?? new AttachmentConcurrencyGate();
        _trace = trace ?? new AttachmentTransferTrace();
        _replayCapacity = replayCapacity;
        _replayLifetimeMs = replayLifetimeMs;
        _cache = new AttachmentReplayCache(replayCapacity, replayLifetimeMs, () => _clock.NowMs);
        _contextMessage = BuildContextMessage(identity);
        _mirror = NewMirror();
        TraceState("identity", null, "bound", $"documentEpoch={identity.DocumentEpoch} composerEpoch={identity.ComposerEpoch} targetId={identity.TargetId}");
    }

    /// <summary>当前绑定的身份。</summary>
    public AttachmentIdentity Identity => _identity;

    /// <summary>身份是否已因导航/关闭过期（过期后不接收任何操作）。</summary>
    public bool IdentityExpired => _identityExpired;

    /// <summary>批次阶段。</summary>
    public AttachmentBatchPhase BatchPhase => _batch?.Phase ?? AttachmentBatchPhase.None;

    /// <summary>当前批次 ID（无批次时为 null）。</summary>
    public string? BatchId => _batch?.BatchId;

    /// <summary>
    /// 已发送但未被 ack 的原始字节数（背压窗口占用），上界 <see cref="MaxPendingBytes"/>。
    ///
    /// D21 修正：批次进入 <see cref="AttachmentBatchPhase.Cancelled"/> 之后窗口**已被释放**——
    /// 对端不会、也不允许再对已取消批次回 ack（迟到结果一律被拒），此时继续把状态机镜像里
    /// 的历史块算作"在途"会让资源读数永远是脏的（实测取消后仍报 2×256 KiB）。取消即归零，
    /// 逐文件的历史读数仍在镜像记录里可查。
    /// </summary>
    public int PendingBytes
    {
        get
        {
            if (_batch is null || _batch.Phase == AttachmentBatchPhase.Cancelled) return 0;
            var total = 0;
            foreach (var id in _order)
            {
                var record = _mirror.FileRecord(id);
                if (record is not null) total += record.ReceivedBytes - record.AckedBytes;
            }

            return total;
        }
    }

    /// <summary>已发送未 ack 的块数，上界 <see cref="AttachmentProtocol.MaxChunksInFlight"/>；取消后为 0（见 <see cref="PendingBytes"/>）。</summary>
    public int PendingChunks
    {
        get
        {
            if (_batch is null || _batch.Phase == AttachmentBatchPhase.Cancelled) return 0;
            var total = 0;
            foreach (var id in _order) total += _mirror.FileRecord(id)?.InFlight ?? 0;
            return total;
        }
    }

    /// <summary>单文件待确认字节的硬上界：2 块 × 256 KiB（背压窗口的硬上界）。</summary>
    public static int MaxPendingBytes => AttachmentProtocol.MaxChunksInFlight * AttachmentProtocol.ChunkBytes;

    /// <summary>已准入但尚未开始的文件数（队列深度），上界 <see cref="QueueCapacity"/>。</summary>
    public int QueueDepth => _order.Count(id => _files[id].Phase == AttachmentFilePhase.Pending);

    /// <summary>队列容量上界：batch-begin 声明的 fileCount（≤ 10），无批次时为 0。</summary>
    public int QueueCapacity => _batch?.FileCount ?? 0;

    /// <summary>协调器持有的 256 KiB 暂存缓冲字节数：0 或 256 KiB（每目标同时只组装一个文件）。</summary>
    public int ScratchBytes => _files.Values.Sum(entry => entry.Scratch?.Length ?? 0);

    /// <summary>去重缓存条目数（含被钉住的活动条目）。</summary>
    public int ReplayCacheSize => _cache.Count;

    /// <summary>被钉住（活动、不可淘汰）的条目数：1 个批次条目 + 未完成文件条目。</summary>
    public int ReplayCachePinned => _cache.PinnedCount;

    public int ReplayCacheEvicted => _cache.EvictedCount;

    public int ReplayCacheRefusedEvictions => _cache.RefusedEvictionCount;

    /// <summary>已发出的帧数。</summary>
    public int OutboundFrames => _outbound;

    /// <summary>已处理的入站帧数。</summary>
    public int InboundFrames => _inbound;

    /// <summary>被拒绝的入站帧数。</summary>
    public int RejectedFrames => _rejections;

    /// <summary>触发的草稿导入次数：每个 file-end 恰好一次，重复消息永不增加。</summary>
    public int ImportInvocations => _imports;

    /// <summary>全局并发闸门（只读观察用）。</summary>
    public AttachmentConcurrencyGate Gate => _gate;

    /// <summary>trace 记录器。</summary>
    public AttachmentTransferTrace Trace => _trace;

    /// <summary>逐 fileId 的只读快照。</summary>
    public IReadOnlyList<AttachmentTransferFileRecord> Files => _order.Select(id => RecordOf(_files[id])).ToList();

    /// <summary>取单个文件的记录。</summary>
    public AttachmentTransferFileRecord? FileRecord(string fileId) =>
        _files.TryGetValue(fileId, out var entry) ? RecordOf(entry) : null;

    // ————————————————————————————————————————————————————————————
    // 对外驱动
    // ————————————————————————————————————————————————————————————

    /// <summary>
    /// 开始一个批次：发送 batch-begin。复用已关闭批次的 batchId 是
    /// <c>duplicate-operation</c>——重新导入必须生成新的 batchId/fileId。
    /// </summary>
    public AttachmentCoordinatorResult OpenBatch(string batchId, int fileCount, int totalBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        if (_disposed) return Fail(AttachmentCoordinatorCodes.Disposed, "协调器已释放");
        if (_identityExpired) return Fail("context-changed", "身份已过期，需重新绑定 context");

        var current = _batch;
        if (current is not null && current.Phase == AttachmentBatchPhase.Open)
        {
            return Fail("batch-in-progress", $"已有开放批次 {current.BatchId}");
        }

        if (_cache.Get(BatchCacheKey(batchId)) is not null)
        {
            return Fail("duplicate-operation", $"batchId {batchId} 已关闭；重新导入必须生成新的 batchId");
        }

        if (fileCount < 1 || fileCount > _limits.MaxFilesPerBatch)
        {
            return Fail("limit-batch-files", $"fileCount {fileCount} 超出 1..{_limits.MaxFilesPerBatch}");
        }

        if (totalBytes < 0 || totalBytes > _limits.MaxBatchBytes)
        {
            return Fail("limit-batch-bytes", $"totalBytes {totalBytes} 超出 0..{_limits.MaxBatchBytes}");
        }

        StartMirrorForBatch();
        var sent = SendFrame(Wire.BatchBegin(_identity, batchId, fileCount, totalBytes), "batch-begin");
        if (!sent.Ok) return Fail(sent.Code ?? "invalid-field-value", sent.Detail ?? "D10 状态机拒绝 batch-begin");

        _batch = new BatchState { BatchId = batchId, FileCount = fileCount, TotalBytes = totalBytes };
        _cache.Put(
            "batch",
            _identity.DocumentEpoch,
            _identity.ComposerEpoch,
            batchId,
            string.Empty,
            string.Empty,
            new AttachmentReplaySummary(false, [], "none", null));
        TraceState("batch", batchId, "open", $"fileCount={fileCount} totalBytes={totalBytes}");
        return Ok(batchId, null);
    }

    /// <summary>
    /// 准入一个文件：只登记元数据并占用一个<b>有界</b>队列位置，
    /// 此刻不会读源、不会发 file-begin。真正开始传输要等 <see cref="Pump"/>
    /// 拿到全局并发槽位，且该目标当前没有未完成文件。
    /// </summary>
    public AttachmentCoordinatorResult AdmitFile(
        string fileId,
        string name,
        string mime,
        IAttachmentByteSource source,
        string? declaredSha256 = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileId);
        ArgumentNullException.ThrowIfNull(source);

        if (_disposed)
        {
            source.Dispose();
            return Fail(AttachmentCoordinatorCodes.Disposed, "协调器已释放", fileId);
        }

        if (_identityExpired)
        {
            source.Dispose();
            return Fail("context-changed", "身份已过期，需重新绑定 context", fileId);
        }

        var batch = _batch;
        if (batch is null)
        {
            source.Dispose();
            return Fail("batch-not-open", "尚未 batch-begin", fileId);
        }

        if (batch.Phase == AttachmentBatchPhase.Closed)
        {
            source.Dispose();
            return Fail("batch-closed", "批次已关闭；新批次必须经 batch-begin 承认", fileId);
        }

        if (batch.Phase == AttachmentBatchPhase.Cancelled)
        {
            source.Dispose();
            return Fail("cancelled", "批次已取消，拒绝新文件", fileId);
        }

        if (_files.ContainsKey(fileId))
        {
            source.Dispose();
            return Fail("duplicate-operation", $"fileId {fileId} 已在本批次声明过", fileId);
        }

        if (ConfirmedSuccess(fileId))
        {
            source.Dispose();
            var detail = $"fileId {fileId} 已确认成功（staged）；重新导入必须使用新的 batchId/fileId";
            TraceDecision("decision", "admit", fileId, $"拒绝 duplicate-operation：{detail}");
            return Fail("duplicate-operation", detail, fileId);
        }

        if (_order.Count >= batch.FileCount)
        {
            source.Dispose();
            return Fail("limit-batch-files", $"批内文件数已达 batch-begin 声明的 {batch.FileCount}", fileId);
        }

        if (source.ByteLength < 0 || source.ByteLength > _limits.MaxFileBytes)
        {
            source.Dispose();
            return Fail("limit-file-bytes", $"byteLength {source.ByteLength} 超出 0..{_limits.MaxFileBytes}", fileId);
        }

        // 用同一份冻结 codec 校验即将写进 file-begin 的元数据（fileId/name/mime/sha256）：
        // 非法输入必须在这里得到确定拒绝码，绝不能被收下后才在 Pump 里以构造异常的形式爆出。
        var probe = AttachmentCodec.Decode(
            Wire.FileBegin(_identity, batch.BatchId, fileId, name, mime, source.ByteLength, declaredSha256).ToJsonString());
        if (!probe.Ok)
        {
            source.Dispose();
            return Fail(
                probe.Code ?? "invalid-field-value",
                $"file-begin 元数据不符合冻结线协议：{probe.Detail}",
                fileId);
        }

        var entry = new FileEntry
        {
            FileId = fileId,
            Name = name,
            Mime = mime,
            ByteLength = source.ByteLength,
            Source = source,
            DeclaredSha256 = declaredSha256,
        };
        _files[fileId] = entry;
        _order.Add(fileId);
        _cache.Put(
            "file",
            _identity.DocumentEpoch,
            _identity.ComposerEpoch,
            batch.BatchId,
            fileId,
            string.Empty,
            new AttachmentReplaySummary(false, [], "none", null));
        TraceState("file", fileId, "pending", $"name={name} mime={mime} byteLength={entry.ByteLength}");
        return Ok(batch.BatchId, fileId);
    }

    /// <summary>
    /// 驱动一次：先排空入站帧（ack / import-result / cancel / batch-end），
    /// 再按注入时钟判定超时，最后在背压窗口允许的范围内推进传输。
    /// 不睡眠、不等待——超时只由调用方推进时钟后再次调用本方法触发。
    /// </summary>
    public AttachmentPumpReport Pump()
    {
        var sentBefore = _outbound;
        var chunksBefore = _chunksSent;
        var rejections = new List<string>();
        var received = 0;
        var queued = (string?)null;
        var starved = (string?)null;
        string? stop = null;

        if (_disposed) stop = AttachmentCoordinatorCodes.Disposed;
        else if (_identityExpired) stop = "context-changed";

        if (stop is null) received = DrainInbound(rejections);

        var batch = _batch;
        if (stop is null && batch is not null)
        {
            switch (batch.Phase)
            {
                case AttachmentBatchPhase.Open:
                    var timeouts = _mirror.CheckTimeouts();
                    if (timeouts.CancelledBatchId is not null) HandleTimeouts(timeouts);
                    else (queued, starved) = AdvanceFiles();
                    break;
                case AttachmentBatchPhase.Closed:
                    stop = "batch-closed";
                    break;
                default:
                    stop = "cancelled";
                    break;
            }
        }

        if (stop is null && _batch is { Phase: AttachmentBatchPhase.Cancelled }) stop = "cancelled";

        return new AttachmentPumpReport
        {
            Received = received,
            Sent = _outbound - sentBefore,
            ChunksSent = _chunksSent - chunksBefore,
            ActiveFileId = _activeFileId,
            QueuedFileId = queued,
            StarvedFileId = starved,
            Rejections = rejections,
            Stop = stop,
        };
    }

    /// <summary>本地取消：发出一条 cancel 报文，停止后续传输、释放全部源、拒绝迟到结果。
    /// 已确认的 staged 结果保留。重复调用是幂等重放，不会发出第二条 cancel。</summary>
    public AttachmentCoordinatorResult Cancel(string? reason = null, string? stage = null)
    {
        if (_disposed) return Fail(AttachmentCoordinatorCodes.Disposed, "协调器已释放");
        var batch = _batch;
        if (batch is null) return Fail("batch-not-open", "尚未 batch-begin");
        if (batch.Phase == AttachmentBatchPhase.Closed) return Fail("batch-closed", "批次已关闭");
        if (batch.Phase == AttachmentBatchPhase.Cancelled)
        {
            TraceDecision("decision", "cancel", batch.BatchId, "重复 cancel 幂等重放：不再发送第二条 cancel");
            return new AttachmentCoordinatorResult { Ok = true, BatchId = batch.BatchId, Duplicate = true };
        }

        var wireReason = reason ?? "cancelled";
        if (!AttachmentProtocol.ErrorCodes.Contains(wireReason, StringComparer.Ordinal))
        {
            return Fail("invalid-field-value", $"reason {wireReason} 不是冻结的 ERROR_CODES");
        }

        if (stage is not null && !AttachmentProtocol.ErrorStages.Contains(stage, StringComparer.Ordinal))
        {
            return Fail("invalid-field-value", $"stage {stage} 不是冻结的 ERROR_STAGES");
        }

        SendFrame(Wire.Cancel(_identity, batch.BatchId, wireReason, stage), $"cancel（{wireReason}）");
        TraceDecision(
            "decision",
            "cancel",
            null,
            $"本地取消：reason={wireReason} stage={stage ?? "(none)"}，停止后续传输、释放字节源、拒绝迟到结果");
        CancelLocally(wireReason);
        return Ok(batch.BatchId, null);
    }

    /// <summary>
    /// 绑定/替换身份（新的 context）。身份变化即失效：清空去重缓存、
    /// 取消并释放当前批次，直到新身份绑定完成才恢复。
    /// </summary>
    public void BindContext(AttachmentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (_disposed) return;
        if (!_identityExpired && _identity == identity) return;
        Invalidate();
        _identity = identity;
        _identityExpired = false;
        _contextMessage = BuildContextMessage(identity);
        TraceState("identity", null, "bound", $"documentEpoch={identity.DocumentEpoch} composerEpoch={identity.ComposerEpoch} targetId={identity.TargetId}");
    }

    /// <summary>
    /// 导航/关闭：清空去重缓存、释放全部源与槽位、让身份过期。
    /// 之后必须由新的 context 重新建立身份，迟到的 file-end 无法重建操作。
    /// </summary>
    public void Invalidate()
    {
        if (_disposed) return;
        _cache.Clear();
        ReleaseGateSlot();
        foreach (var entry in _files.Values) entry.Dispose();
        _files.Clear();
        _order.Clear();
        _activeFileId = null;
        _batch = null;
        _identityExpired = true;
        TraceState("identity", null, "expired", "导航/关闭：清空去重缓存、释放全部字节源、身份过期");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseGateSlot();
        foreach (var entry in _files.Values) entry.Dispose();
        _files.Clear();
        _order.Clear();
        _activeFileId = null;
        _mirror.Dispose();
        _cache.Clear();
        TraceState("coordinator", null, "disposed", "释放全部字节源与 D10 镜像状态机");
    }

    // ————————————————————————————————————————————————————————————
    // 入站
    // ————————————————————————————————————————————————————————————

    private int DrainInbound(List<string> rejections)
    {
        var count = 0;
        while (_channel.TryReceive(out var payload))
        {
            count += 1;
            _inbound += 1;
            var decoded = AttachmentCodec.Decode(payload);
            if (!decoded.Ok)
            {
                _rejections += 1;
                rejections.Add($"malformed-frame:{decoded.Code}");
                TraceFrame("message-in", "malformed", null, null, $"D10 codec 拒绝入站帧：{decoded.Code}（{decoded.Detail}）");
                continue;
            }

            var message = decoded.Message!;
            var code = HandleInbound(message);
            TraceFrame(
                "message-in",
                message.Type,
                message.BatchId,
                message.FileId,
                code is null ? "接受" : $"拒绝 {code}",
                decoded.Canonical);
            if (code is not null)
            {
                _rejections += 1;
                rejections.Add($"{message.Type}:{code}");
            }
        }

        return count;
    }

    /// <summary>判定顺序：会话绑定（no-session/context-changed）→ 类型白名单 → 批次准入
    /// （batch-not-open/batch-closed/cancelled：迟到消息不得重建操作）→ D10 镜像状态机。
    /// 批次状态先于类型判定，因此批次关闭后的迟到 file-end/chunk 会得到 <c>batch-closed</c>，
    /// 而不是被当成"未知类型"轻轻放过。</summary>
    private string? HandleInbound(AttachmentMessage message)
    {
        if (message.Kind is WireMessageKind.Hello or WireMessageKind.Capabilities or WireMessageKind.Context)
        {
            return "unknown-message-type";
        }

        if (message.SessionId is null) return "no-session";
        if (_identityExpired) return "context-changed";
        if (!string.Equals(message.SessionId, _identity.SessionId, StringComparison.Ordinal)) return "context-changed";

        var batch = _batch;
        if (batch is null || !string.Equals(batch.BatchId, message.BatchId, StringComparison.Ordinal))
        {
            if (_cache.Get(BatchCacheKey(message.BatchId!)) is not null)
            {
                return message.Kind == WireMessageKind.BatchEnd
                    ? HandleBatchEndReplay(message)
                    : "batch-closed";
            }

            return "batch-not-open";
        }

        if (batch.Phase == AttachmentBatchPhase.Cancelled) return "cancelled";

        if (batch.Phase == AttachmentBatchPhase.Closed)
        {
            return message.Kind == WireMessageKind.BatchEnd ? HandleBatchEndReplay(message) : "batch-closed";
        }

        var expected = message.Kind is WireMessageKind.Ack
            or WireMessageKind.ImportResult
            or WireMessageKind.Cancel
            or WireMessageKind.BatchEnd;
        if (!expected) return "unknown-message-type";

        var applied = _mirror.Apply(message);
        if (!applied.Ok) return applied.Code ?? "invalid-field-value";

        switch (message.Kind)
        {
            case WireMessageKind.Ack:
                if (applied.Duplicate)
                {
                    TraceDecision("decision", "ack", message.FileId, $"重复 ack seq={message.Seq} 幂等重放：不改变已确认字节，不触发导入");
                }

                return null;
            case WireMessageKind.ImportResult:
                OnImportResult(message, applied);
                return null;
            case WireMessageKind.Cancel:
                if (!applied.Duplicate) CancelLocally("cancelled");
                return null;
            default:
                OnPeerBatchEnd();
                return null;
        }
    }

    /// <summary>重复的 batch-end：内容一致即幂等重放，冲突即 duplicate-operation（D10 §5）。</summary>
    private string? HandleBatchEndReplay(AttachmentMessage message)
    {
        var entry = _cache.Get(BatchCacheKey(message.BatchId!));
        if (entry is null) return "batch-not-open";
        var digest = AttachmentCodec.CanonicalJson(new JsonObject
        {
            ["status"] = message.Status,
            ["results"] = message.Results?.DeepClone(),
        });
        if (string.Equals(entry.PayloadDigest, digest, StringComparison.Ordinal))
        {
            TraceDecision("decision", "batch-end", message.BatchId, "重复 batch-end 幂等重放：不产生第二次导入");
            return null;
        }

        return "duplicate-operation";
    }

    private void OnImportResult(AttachmentMessage message, AttachmentApplyResult applied)
    {
        var fileId = message.FileId!;
        var entry = _files[fileId];
        if (applied.Duplicate)
        {
            TraceDecision(
                "decision",
                "import-result",
                fileId,
                $"重复 import-result（status={message.Status}）幂等重放：不产生第二次导入，importInvocations={_imports}");
            return;
        }

        entry.Phase = message.Status switch
        {
            "staged" => AttachmentFilePhase.Staged,
            "failed" => AttachmentFilePhase.Failed,
            _ => AttachmentFilePhase.Partial,
        };
        entry.Code = message.Code;
        var digest = AttachmentCodec.CanonicalJson(new JsonObject
        {
            ["status"] = message.Status,
            ["attachmentIds"] = new JsonArray([.. message.AttachmentIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
            ["code"] = message.Code,
        });
        _cache.Complete(
            FileCacheKey(_batch!.BatchId, fileId),
            new AttachmentReplaySummary(true, [.. message.AttachmentIds], message.Status ?? "none", message.Status),
            digest);
        if (entry.Phase == AttachmentFilePhase.Staged) RecordConfirmedSuccess(entry);
        ReleaseActiveFile(entry);
        TraceState(
            "file",
            fileId,
            entry.Phase.ToString().ToLowerInvariant(),
            $"status={message.Status} attachmentIds={message.AttachmentIds.Count}");
    }

    private void OnPeerBatchEnd()
    {
        var batch = _batch!;
        batch.Phase = AttachmentBatchPhase.Closed;
        UnpinFileEntries();
        _cache.Complete(BatchCacheKey(batch.BatchId), new AttachmentReplaySummary(false, [], "peer", "peer"), string.Empty);
        TraceState("batch", batch.BatchId, "closed", "对端 batch-end：批次关闭，迟到消息一律拒绝");
    }

    // ————————————————————————————————————————————————————————————
    // 发送与推进
    // ————————————————————————————————————————————————————————————

    private (string? Queued, string? Starved) AdvanceFiles()
    {
        var batch = _batch!;
        var unresolved = _order.Where(id => !IsResolved(_files[id])).ToList();
        if (unresolved.Count == 0)
        {
            if (_order.Count < batch.FileCount)
            {
                // 声明的文件没被全部准入：线上无法构造覆盖全部 fileId 的 batch-end，
                // 只能本地取消，避免把一个不完整的批次结果推给对端。
                TraceDecision("decision", "batch", batch.BatchId, $"批内只准入 {_order.Count}/{batch.FileCount} 个文件，无法构造完整 batch-end");
                CancelBatchAfterFailure("result-incomplete");
                return (null, null);
            }

            SendBatchEnd();
            return (null, null);
        }

        if (_activeFileId is null)
        {
            var next = unresolved.Select(id => _files[id]).FirstOrDefault(entry => entry.Phase == AttachmentFilePhase.Pending);
            if (next is null) return (null, null);
            var outcome = _gate.TryAcquire(_identity.TargetId);
            if (outcome == AttachmentGateOutcome.Queued)
            {
                _gateQueued = true;
                TraceDecision(
                    "decision",
                    "gate",
                    next.FileId,
                    $"全局并发已满：{_identity.TargetId} 进入有界等待队列（waiting={_gate.WaitingCount}/{_gate.QueueCapacity}）");
                return (next.FileId, null);
            }

            if (outcome == AttachmentGateOutcome.Refused)
            {
                _gateQueued = false;
                TraceDecision(
                    "decision",
                    "gate",
                    next.FileId,
                    $"全局等待队列已满：{_identity.TargetId} 本次未入队（refused={_gate.RefusedCount}），文件保持 pending 且不读字节");
                return (null, next.FileId);
            }

            _gateQueued = false;
            if (!StartFile(next)) return (null, null);
        }

        if (_activeFileId is not null)
        {
            var active = _files[_activeFileId];
            if (active.Phase == AttachmentFilePhase.Transferring) AdvanceTransfer(active);
        }

        return (null, null);
    }

    private bool StartFile(FileEntry entry)
    {
        var sent = SendFrame(
            Wire.FileBegin(_identity, _batch!.BatchId, entry.FileId, entry.Name, entry.Mime, entry.ByteLength, entry.DeclaredSha256),
            "file-begin");
        if (!sent.Ok)
        {
            FailFile(entry, sent.Code ?? "invalid-field-value", sent.Detail ?? "D10 状态机拒绝 file-begin");
            CancelBatchAfterFailure(entry.Code!);
            return false;
        }

        entry.Phase = AttachmentFilePhase.Transferring;
        entry.Scratch = new byte[AttachmentProtocol.ChunkBytes];
        _gateHeld = true;
        _activeFileId = entry.FileId;
        TraceState("file", entry.FileId, "transferring", $"byteLength={entry.ByteLength}");
        return true;
    }

    /// <summary>在背压窗口内推进一个文件：读一块（允许短读）→ 发一块；
    /// 只有窗口有空位才会读，因此"在途未确认字节"永远 ≤ 2 × 256 KiB。</summary>
    private void AdvanceTransfer(FileEntry entry)
    {
        while (true)
        {
            var record = _mirror.FileRecord(entry.FileId);
            if (record is null) return;
            if (record.InFlight >= AttachmentProtocol.MaxChunksInFlight) return;
            if (record.ReceivedBytes >= entry.ByteLength)
            {
                SendFileEnd(entry);
                return;
            }

            var want = Math.Min(AttachmentProtocol.ChunkBytes, entry.ByteLength - record.ReceivedBytes);
            var scratch = entry.Scratch ??= new byte[AttachmentProtocol.ChunkBytes];
            var total = 0;
            string? fault = null;
            while (total < want)
            {
                int read;
                try
                {
                    read = entry.Source.Read(scratch.AsSpan(total, want - total));
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
                {
                    fault = $"{error.GetType().Name}: {error.Message}";
                    break;
                }

                if (read < 0 || read > want - total)
                {
                    fault = $"字节源返回非法长度 {read}（本次请求 {want - total}）";
                    break;
                }

                if (read == 0)
                {
                    // EOF：本块剩余部分按短读处理，由下面的 total 分支判定是否提前结束。
                    break;
                }

                total += read;
                entry.Hasher.AppendData(scratch.AsSpan(total - read, read));
            }

            if (fault is not null)
            {
                FailFile(entry, AttachmentCoordinatorCodes.SourceError, fault);
                CancelBatchAfterFailure(AttachmentCoordinatorCodes.SourceError);
                return;
            }

            if (total == 0)
            {
                FailFile(
                    entry,
                    "size-mismatch",
                    $"字节源在 {record.ReceivedBytes}/{entry.ByteLength} 字节处 EOF：声明长度未读满");
                CancelBatchAfterFailure("size-mismatch");
                return;
            }

            if (!SendChunk(entry, record.ReceivedBytes, total)) return;
        }
    }

    private bool SendChunk(FileEntry entry, int offset, int byteLength)
    {
        var scratch = entry.Scratch!;
        var seq = entry.NextSeq;
        var sent = SendFrame(
            Wire.Chunk(_identity, _batch!.BatchId, entry.FileId, seq, offset, scratch.AsSpan(0, byteLength)),
            $"chunk seq={seq} offset={offset} bytes={byteLength}");
        if (!sent.Ok)
        {
            FailFile(entry, sent.Code ?? "invalid-field-value", sent.Detail ?? "D10 状态机拒绝 chunk");
            CancelBatchAfterFailure(entry.Code!);
            return false;
        }

        entry.NextSeq += 1;
        _chunksSent += 1;
        return true;
    }

    /// <summary>累计字节与 SHA-256 都干净时才发 file-end；发出即代表恰好一次草稿导入请求，
    /// 随后立刻释放字节源与暂存缓冲。</summary>
    private void SendFileEnd(FileEntry entry)
    {
        var record = _mirror.FileRecord(entry.FileId);
        if (record is null || record.InFlight > 0 || record.ReceivedBytes < entry.ByteLength) return;

        var digest = Convert.ToHexStringLower(entry.Hasher.GetHashAndReset());
        if (entry.DeclaredSha256 is not null && !string.Equals(entry.DeclaredSha256, digest, StringComparison.Ordinal))
        {
            FailFile(entry, "hash-mismatch", $"本地 SHA-256 {digest} 与 file-begin 声明的 {entry.DeclaredSha256} 不符");
            CancelBatchAfterFailure("hash-mismatch");
            return;
        }

        var sent = SendFrame(Wire.FileEnd(_identity, _batch!.BatchId, entry.FileId, entry.ByteLength, digest), "file-end");
        if (!sent.Ok)
        {
            FailFile(entry, sent.Code ?? "hash-mismatch", sent.Detail ?? "D10 状态机拒绝 file-end");
            CancelBatchAfterFailure(entry.Code!);
            return;
        }

        entry.Phase = AttachmentFilePhase.AwaitingImport;
        entry.ImportInvoked = true;
        _imports += 1;
        entry.DisposeSource();
        entry.Scratch = null;
        TraceState("file", entry.FileId, "awaiting-import", $"totalBytes={entry.ByteLength} sha256={digest}");
    }

    private void SendBatchEnd()
    {
        var batch = _batch!;
        var status = AggregateStatus();
        var results = new JsonArray();
        foreach (var id in _order)
        {
            var entry = _files[id];
            var record = _mirror.FileRecord(id)!;
            var item = new JsonObject
            {
                ["fileId"] = id,
                ["status"] = ResultStatusOf(entry),
            };
            if (record.AttachmentIds.Count > 0)
            {
                item["attachmentIds"] = new JsonArray([.. record.AttachmentIds.Select(x => (JsonNode?)JsonValue.Create(x))]);
            }

            var code = entry.Code;
            if (!string.IsNullOrEmpty(code)) item["code"] = code;
            results.Add(item);
        }

        var digest = AttachmentCodec.CanonicalJson(new JsonObject { ["status"] = status, ["results"] = results.DeepClone() });
        var sent = SendFrame(Wire.BatchEnd(_identity, batch.BatchId, status, results), "batch-end");
        if (!sent.Ok)
        {
            CancelBatchAfterFailure(sent.Code ?? "duplicate-operation");
            return;
        }

        batch.Phase = AttachmentBatchPhase.Closed;
        UnpinFileEntries();
        _cache.Complete(BatchCacheKey(batch.BatchId), new AttachmentReplaySummary(false, [], status, status), digest);
        TraceState("batch", batch.BatchId, "closed", $"status={status} results={results.Count}");
    }

    // ————————————————————————————————————————————————————————————
    // 失败、取消、超时
    // ————————————————————————————————————————————————————————————

    private void FailFile(FileEntry entry, string code, string detail)
    {
        if (IsResolved(entry)) return;
        entry.Phase = AttachmentFilePhase.Failed;
        entry.Code = code;
        entry.DisposeSource();
        entry.Scratch = null;
        ReleaseGateSlot();
        _activeFileId = null;
        if (_batch is not null) _cache.Delete(FileCacheKey(_batch.BatchId, entry.FileId));
        TraceState("file", entry.FileId, "failed", $"{code}：{detail}");
    }

    /// <summary>本地失败无法在线上单独中止一个文件：发 cancel 结束整批，
    /// 但已 staged 的逐文件结果全部保留（不存在批次级布尔值）。</summary>
    private void CancelBatchAfterFailure(string code)
    {
        var batch = _batch;
        if (batch is null || batch.Phase != AttachmentBatchPhase.Open) return;
        var reason = AttachmentProtocol.ErrorCodes.Contains(code, StringComparer.Ordinal) ? code : "cancelled";
        SendFrame(Wire.Cancel(_identity, batch.BatchId, reason, WireStageOf(code)), $"cancel（本地失败 {code}）");
        CancelLocally(code);
    }

    private void CancelLocally(string code)
    {
        var batch = _batch;
        if (batch is null) return;
        batch.Phase = AttachmentBatchPhase.Cancelled;
        ReleaseGateSlot();
        _activeFileId = null;
        foreach (var id in _order)
        {
            var entry = _files[id];
            if (entry.Phase is AttachmentFilePhase.Staged or AttachmentFilePhase.Failed or AttachmentFilePhase.Partial) continue;
            entry.Phase = AttachmentFilePhase.Cancelled;
            entry.Code ??= code;
            entry.DisposeSource();
            entry.Scratch = null;
        }

        UnpinFileEntries();
        _cache.Complete(BatchCacheKey(batch.BatchId), new AttachmentReplaySummary(false, [], "cancelled", code), string.Empty);
        TraceState("batch", batch.BatchId, "cancelled", $"code={code}：停止后续传输、释放全部源、拒绝迟到结果");
    }

    private void HandleTimeouts(AttachmentTimeoutReport report)
    {
        var batch = _batch!;
        var code = AttachmentCoordinatorCodes.IdleTimeout;
        foreach (var fileId in report.StalledFileIds)
        {
            if (!_files.TryGetValue(fileId, out var entry)) continue;
            var record = _mirror.FileRecord(fileId);
            code = record?.Ended == true ? AttachmentCoordinatorCodes.ImportTimeout : AttachmentCoordinatorCodes.AckTimeout;
            entry.Phase = AttachmentFilePhase.Failed;
            entry.Code = code;
            entry.DisposeSource();
            entry.Scratch = null;
            TraceDecision("decision", "timeout", fileId, $"{code}（注入时钟 {_clock.NowMs}ms，未起任何定时器）");
        }

        ReleaseGateSlot();
        _activeFileId = null;
        SendFrame(Wire.Cancel(_identity, batch.BatchId, "cancelled", WireStageOf(code)), $"cancel（超时 {code}）");
        CancelLocally(code);
    }

    // ————————————————————————————————————————————————————————————
    // 记账与工具
    // ————————————————————————————————————————————————————————————

    private void ReleaseActiveFile(FileEntry entry)
    {
        ReleaseGateSlot();
        if (_activeFileId is not null && string.Equals(_activeFileId, entry.FileId, StringComparison.Ordinal)) _activeFileId = null;
    }

    /// <summary>
    /// 释放本目标在全局闸门里的位置（活动槽位**或**等待队列中的位置）。
    ///
    /// D21 修正：只判断 <see cref="_gateHeld"/> 会漏掉"已排队、尚未获得槽位"的目标。
    /// 实测场景：target-b 排队 → target-a 释放时把 b 提升为活动 → 此时 b 的协调器被释放
    /// （导航/取消），`_gateHeld` 仍为 false，于是这个全局槽位再也不会被释放
    /// （Dispose 后 `Gate.ActiveCount` 仍为 1）。排队同样是"占用闸门中的一个位置"，
    /// 因此必须单独记账并在释放时一并撤销。
    /// </summary>
    private void ReleaseGateSlot()
    {
        if (!_gateHeld && !_gateQueued) return;
        _gateHeld = false;
        _gateQueued = false;
        _gate.Release(_identity.TargetId);
    }

    /// <summary>已确认成功（staged）的台账条目已存在：同一个 fileId 永不重导。</summary>
    private bool ConfirmedSuccess(string fileId) => _cache.Get(ConfirmedKey(fileId)) is not null;

    private void RecordConfirmedSuccess(FileEntry entry)
    {
        var summary = new AttachmentReplaySummary(
            true,
            [.. _mirror.FileRecord(entry.FileId)?.AttachmentIds ?? []],
            "staged",
            "staged");
        var key = ConfirmedKey(entry.FileId);
        _cache.Put("confirmed", _identity.DocumentEpoch, _identity.ComposerEpoch, string.Empty, entry.FileId, "staged", summary);
        _cache.Complete(key, summary, "staged");
        TraceDecision("decision", "cache", entry.FileId, $"登记已确认成功台账 {key}：该 fileId 不再重导（容量与 TTL 均有界）");
    }

    /// <summary>批次关闭/取消后解除文件条目的钉住状态（内容保留，仍受容量与 TTL 约束），
    /// 与 D10 在 batch-end/cancel 时的处理一致。</summary>
    private void UnpinFileEntries()
    {
        var batch = _batch;
        if (batch is null) return;
        foreach (var id in _order)
        {
            var entry = _cache.Get(FileCacheKey(batch.BatchId, id));
            if (entry is not null) entry.State = "completed";
        }
    }

    private static bool IsResolved(FileEntry entry) =>
        entry.Phase is AttachmentFilePhase.Staged or AttachmentFilePhase.Failed or AttachmentFilePhase.Partial;

    private string AggregateStatus()
    {
        if (_order.All(id => _files[id].Phase == AttachmentFilePhase.Staged)) return "staged";
        if (_order.All(id => _files[id].Phase == AttachmentFilePhase.Failed)) return "failed";
        return "partial";
    }

    private static string ResultStatusOf(FileEntry entry) => entry.Phase switch
    {
        AttachmentFilePhase.Staged => "staged",
        AttachmentFilePhase.Partial => "partial",
        _ => "failed",
    };

    private static string WireStageOf(string code) => code switch
    {
        AttachmentCoordinatorCodes.SourceError => "native-capture",
        "size-mismatch" => "native-capture",
        AttachmentCoordinatorCodes.ImportTimeout => "draft-import",
        "hash-mismatch" => "protocol-transfer",
        _ => "protocol-transfer",
    };

    private AttachmentTransferFileRecord RecordOf(FileEntry entry)
    {
        var record = _mirror.FileRecord(entry.FileId);
        return new AttachmentTransferFileRecord
        {
            FileId = entry.FileId,
            Name = entry.Name,
            Mime = entry.Mime,
            ByteLength = entry.ByteLength,
            SentBytes = record?.ReceivedBytes ?? 0,
            AckedBytes = record?.AckedBytes ?? 0,
            InFlightChunks = record?.InFlight ?? 0,
            Phase = entry.Phase,
            ResultStatus = IsResolved(entry) ? ResultStatusOf(entry) : null,
            Code = entry.Code,
            AttachmentIds = record?.AttachmentIds ?? [],
            ImportInvoked = entry.ImportInvoked,
            SourceDisposed = entry.SourceDisposed,
        };
    }

    private static AttachmentMessage BuildContextMessage(AttachmentIdentity identity)
    {
        var decoded = AttachmentCodec.Decode(Wire.Context(identity).ToJsonString());
        if (!decoded.Ok)
        {
            throw new ArgumentException($"身份不符合 D10 线协议：{decoded.Code}（{decoded.Detail}）", nameof(identity));
        }

        return decoded.Message!;
    }

    private AttachmentSession NewMirror()
    {
        var mirror = new AttachmentSession(_limits, () => _clock.NowMs, _replayCapacity, _replayLifetimeMs);
        var applied = mirror.Apply(_contextMessage!);
        if (!applied.Ok)
        {
            mirror.Dispose();
            throw new InvalidOperationException($"D10 镜像状态机拒绝绑定身份：{applied.Code}");
        }

        return mirror;
    }

    /// <summary>每个批次换一个新的 D10 镜像：镜像自身的批次生命周期与线上批次一一对应，
    /// 跨批次的去重与"已确认成功"台账由协调器持有的 <see cref="AttachmentReplayCache"/> 负责。</summary>
    private void StartMirrorForBatch()
    {
        _mirror.Dispose();
        foreach (var entry in _files.Values) entry.Dispose();
        _files.Clear();
        _order.Clear();
        _activeFileId = null;
        ReleaseGateSlot();
        _mirror = NewMirror();
    }

    /// <summary>构造一条出站帧：先由冻结 codec 校验，再喂给 D10 生产状态机；
    /// 状态机拒绝的帧绝不发出（保证协调器不会说出 D10 不认识的话）。</summary>
    private SendResult SendFrame(JsonObject fields, string detail)
    {
        var decoded = AttachmentCodec.Decode(fields.ToJsonString());
        if (!decoded.Ok)
        {
            throw new InvalidOperationException($"协调器构造了 D10 codec 拒绝的报文：{decoded.Code}（{decoded.Detail}）");
        }

        var message = decoded.Message!;
        var applied = _mirror.Apply(message);
        if (!applied.Ok)
        {
            TraceFrame("decision", message.Type, message.BatchId, message.FileId, $"D10 状态机拒绝出站帧：{applied.Code}（{applied.Detail}）");
            return new SendResult(false, applied);
        }

        _channel.Send(AttachmentCodec.Encode(message));
        _outbound += 1;
        TraceFrame("message-out", message.Type, message.BatchId, message.FileId, detail, decoded.Canonical);
        return new SendResult(true, applied);
    }

    private void TraceDecision(string kind, string? type, string? fileId, string detail) =>
        _trace.Record(_clock.NowMs, kind, type, _batch?.BatchId, fileId, detail);

    private void TraceFrame(string kind, string? type, string? batchId, string? fileId, string detail, JsonNode? canonical = null) =>
        _trace.Record(_clock.NowMs, kind, type, batchId, fileId, detail, canonical);

    private void TraceState(string type, string? fileId, string state, string detail) =>
        _trace.Record(_clock.NowMs, "state", type, _batch?.BatchId, fileId, $"{state}：{detail}");

    private string BatchCacheKey(string batchId) => AttachmentProtocol.ReplayKey(_identity.DocumentEpoch, _identity.ComposerEpoch, batchId, string.Empty);

    private string FileCacheKey(string batchId, string fileId) => AttachmentProtocol.ReplayKey(_identity.DocumentEpoch, _identity.ComposerEpoch, batchId, fileId);

    private string ConfirmedKey(string fileId) => AttachmentProtocol.ReplayKey(_identity.DocumentEpoch, _identity.ComposerEpoch, string.Empty, fileId);

    private static AttachmentCoordinatorResult Ok(string? batchId, string? fileId) =>
        new() { Ok = true, BatchId = batchId, FileId = fileId };

    private static AttachmentCoordinatorResult Fail(string code, string detail, string? fileId = null) =>
        new() { Ok = false, Code = code, Detail = detail, FileId = fileId };

    /// <summary>出站帧的构造器。字段顺序必须与 D10 冻结顺序一致，构造后一律再过 codec。</summary>
    private static class Wire
    {
        public static JsonObject Context(AttachmentIdentity identity) => new()
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "context",
            ["sessionId"] = identity.SessionId,
            ["targetId"] = identity.TargetId,
            ["documentEpoch"] = identity.DocumentEpoch,
            ["composerEpoch"] = identity.ComposerEpoch,
            ["composerScope"] = identity.ComposerScope,
        };

        public static JsonObject BatchBegin(AttachmentIdentity identity, string batchId, int fileCount, int totalBytes) => new()
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "batch-begin",
            ["sessionId"] = identity.SessionId,
            ["batchId"] = batchId,
            ["targetId"] = identity.TargetId,
            ["documentEpoch"] = identity.DocumentEpoch,
            ["composerEpoch"] = identity.ComposerEpoch,
            ["fileCount"] = fileCount,
            ["totalBytes"] = totalBytes,
        };

        public static JsonObject FileBegin(
            AttachmentIdentity identity,
            string batchId,
            string fileId,
            string name,
            string mime,
            int byteLength,
            string? declaredSha256)
        {
            var fields = new JsonObject
            {
                ["v"] = AttachmentProtocol.Version,
                ["type"] = "file-begin",
                ["sessionId"] = identity.SessionId,
                ["batchId"] = batchId,
                ["fileId"] = fileId,
                ["name"] = name,
                ["byteLength"] = byteLength,
                ["mime"] = mime,
            };
            if (declaredSha256 is not null) fields["sha256"] = declaredSha256;
            return fields;
        }

        public static JsonObject Chunk(
            AttachmentIdentity identity,
            string batchId,
            string fileId,
            int seq,
            int offset,
            ReadOnlySpan<byte> data) => new()
            {
                ["v"] = AttachmentProtocol.Version,
                ["type"] = "chunk",
                ["sessionId"] = identity.SessionId,
                ["batchId"] = batchId,
                ["fileId"] = fileId,
                ["seq"] = seq,
                ["offset"] = offset,
                ["byteLength"] = data.Length,
                ["dataBase64"] = Convert.ToBase64String(data),
            };

        public static JsonObject FileEnd(AttachmentIdentity identity, string batchId, string fileId, int totalBytes, string sha256) => new()
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "file-end",
            ["sessionId"] = identity.SessionId,
            ["batchId"] = batchId,
            ["fileId"] = fileId,
            ["totalBytes"] = totalBytes,
            ["sha256"] = sha256,
            ["submittedItems"] = 1,
        };

        public static JsonObject BatchEnd(AttachmentIdentity identity, string batchId, string status, JsonArray results) => new()
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "batch-end",
            ["sessionId"] = identity.SessionId,
            ["batchId"] = batchId,
            ["status"] = status,
            ["results"] = results.DeepClone(),
        };

        public static JsonObject Cancel(AttachmentIdentity identity, string batchId, string reason, string? stage)
        {
            var fields = new JsonObject
            {
                ["v"] = AttachmentProtocol.Version,
                ["type"] = "cancel",
                ["sessionId"] = identity.SessionId,
                ["batchId"] = batchId,
                ["reason"] = reason,
            };
            if (stage is not null) fields["stage"] = stage;
            return fields;
        }
    }

    private readonly record struct SendResult(bool Sent, AttachmentApplyResult Applied)
    {
        public bool Ok => Sent && Applied.Ok;

        public string? Code => Applied.Code;

        public string? Detail => Applied.Detail;
    }

    private sealed class BatchState
    {
        public required string BatchId { get; init; }

        public required int FileCount { get; init; }

        public required int TotalBytes { get; init; }

        public AttachmentBatchPhase Phase { get; set; } = AttachmentBatchPhase.Open;
    }

    private sealed class FileEntry : IDisposable
    {
        public required string FileId { get; init; }

        public required string Name { get; init; }

        public required string Mime { get; init; }

        public required int ByteLength { get; init; }

        public required IAttachmentByteSource Source { get; init; }

        public string? DeclaredSha256 { get; init; }

        public IncrementalHash Hasher { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public AttachmentFilePhase Phase { get; set; } = AttachmentFilePhase.Pending;

        public string? Code { get; set; }

        public int NextSeq { get; set; }

        public byte[]? Scratch { get; set; }

        public bool ImportInvoked { get; set; }

        public bool SourceDisposed { get; private set; }

        public void DisposeSource()
        {
            if (SourceDisposed) return;
            SourceDisposed = true;
            Source.Dispose();
        }

        public void Dispose()
        {
            DisposeSource();
            Scratch = null;
            Hasher.Dispose();
        }
    }
}
