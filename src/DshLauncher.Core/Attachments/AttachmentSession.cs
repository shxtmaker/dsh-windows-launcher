using System.Security.Cryptography;

namespace DshLauncher.Core.Attachments;

/// <summary>传输层状态：只由 chunk/ack 改变。</summary>
public enum AttachmentTransportState
{
    Idle,
    Buffering,
    Buffered,
}

/// <summary>草稿状态：只由 import-result 改变。</summary>
public enum AttachmentDraftState
{
    None,
    Staged,
    Failed,
    Partial,
}

/// <summary>上传归属：线协议只承认"归 Harness 所有"，不存在 ready/uploaded。</summary>
public enum AttachmentUploadState
{
    None,
    HarnessOwned,
}

/// <summary>单个 fileId 的对外记录。三层状态字段互不合并。</summary>
public sealed record AttachmentFileRecord
{
    public required string FileId { get; init; }

    public required string Name { get; init; }

    public required int ByteLength { get; init; }

    public required string Mime { get; init; }

    public string? DeclaredSha256 { get; init; }

    public required AttachmentTransportState Transport { get; init; }

    public required AttachmentDraftState Draft { get; init; }

    public required AttachmentUploadState Upload { get; init; }

    public required int ReceivedBytes { get; init; }

    public required int AckedBytes { get; init; }

    public required int InFlight { get; init; }

    public required bool Ended { get; init; }

    public required bool Resolved { get; init; }

    public required int SubmittedItems { get; init; }

    public required IReadOnlyList<string> AttachmentIds { get; init; }
}

/// <summary>apply 结果：成功时逐文件可查，失败时给出稳定拒绝码。</summary>
public sealed record AttachmentApplyResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? Path { get; init; }

    public WireMessageKind? Kind { get; init; }

    public string? BatchId { get; init; }

    public string? FileId { get; init; }

    /// <summary>是否判定为已完成操作的幂等重放。</summary>
    public bool Duplicate { get; init; }

    /// <summary>本次 apply 是否触发了一次草稿导入（重复到达必须为 false）。</summary>
    public bool ImportInvoked { get; init; }

    public bool Closed { get; init; }

    public AttachmentFileRecord? File { get; init; }

    public IReadOnlyList<AttachmentFileRecord> Files { get; init; } = [];
}

/// <summary>超时报告（由调用方按需驱动，不自行起定时器）。</summary>
public sealed record AttachmentTimeoutReport(IReadOnlyList<string> StalledFileIds, string? CancelledBatchId);

/// <summary>
/// 线协议 v1 状态机（C# 侧生产实现，与 TS 侧 <c>src/shared/wire/session.ts</c> 逐条对齐）。
///
/// 身份绑定、批次准入、限额、逐文件 seq/offset 与在途窗口、file-end 的累计字节与
/// SHA-256 校验、逐 fileId 结果状态、重放/去重缓存、批次关闭后的迟到拒绝，以及
/// <b>三种互不合并的状态</b>：transport（接收缓冲）≠ draft（草稿 staged）≠
/// upload（上传归 Harness）。C# 侧摘要是同步的（<see cref="IncrementalHash"/>），
/// 因此这里不需要 TS 那样的 async API。
/// </summary>
public sealed class AttachmentSession : IDisposable
{
    private readonly AttachmentReplayCache _cache;
    private readonly Func<long> _now;
    private readonly int? _replayLifetimeMs;
    private readonly List<FileState> _owned = [];
    private AttachmentLimits _limits;
    private AttachmentIdentity? _identity;
    private bool _identityExpired;
    private BatchState? _batch;
    private bool _disposed;

    public AttachmentSession(
        AttachmentLimits? limits = null,
        Func<long>? now = null,
        int? replayCapacity = null,
        int? replayLifetimeMs = null)
    {
        _limits = limits ?? new AttachmentLimits();
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _replayLifetimeMs = replayLifetimeMs;
        _cache = new AttachmentReplayCache(replayCapacity, replayLifetimeMs, _now);
    }

    /// <summary>生效限额（capabilities 协商后的结果）。</summary>
    public AttachmentLimits EffectiveLimits => _limits;

    /// <summary>当前身份；导航/关闭后过期。</summary>
    public AttachmentIdentity? CurrentIdentity => _identity;

    public bool IdentityExpired => _identityExpired;

    public int ReplayCacheSize => _cache.Count;

    public int ReplayCachePinned => _cache.PinnedCount;

    public int ReplayCacheEvicted => _cache.EvictedCount;

    public int ReplayCacheRefusedEvictions => _cache.RefusedEvictionCount;

    public bool BatchClosed => _batch?.Closed ?? false;

    public string? ActiveBatchId => _batch?.BatchId;

    /// <summary>逐文件记录的只读快照。</summary>
    public IReadOnlyList<AttachmentFileRecord> Files =>
        _batch is null ? [] : _batch.Files.Values.Select(RecordOf).ToList();

    /// <summary>取单个文件记录。</summary>
    public AttachmentFileRecord? FileRecord(string fileId)
    {
        if (_batch is null || !_batch.Files.TryGetValue(fileId, out var file)) return null;
        return RecordOf(file);
    }

    /// <summary>
    /// 导航/关闭：清空重放缓存、关闭当前批次、让当前身份过期。
    /// 之后必须由新的 context 重新建立身份，迟到的 file-end 无法重建操作。
    /// </summary>
    public void Navigate()
    {
        _cache.Clear();
        if (_batch is not null)
        {
            _batch.Closed = true;
            _batch.Cancelled = true;
        }

        _batch = null;
        if (_identity is not null) _identityExpired = true;
    }

    /// <summary>超时判定（纯计算）：ACK 停滞、file-end 等待超时、批次空闲超时。</summary>
    public AttachmentTimeoutReport CheckTimeouts(long? nowMs = null)
    {
        var now = nowMs ?? _now();
        var batch = _batch;
        if (batch is null || batch.Closed || batch.Cancelled) return new AttachmentTimeoutReport([], null);

        var stalled = new List<string>();
        foreach (var file in batch.Files.Values)
        {
            if (file.Resolved) continue;
            var idle = now - file.LastActivityMs;
            if (file.Ended && idle > AttachmentProtocol.FileEndTimeoutMs) stalled.Add(file.FileId);
            else if (!file.Ended && InFlight(file) > 0 && idle > AttachmentProtocol.AckTimeoutMs) stalled.Add(file.FileId);
        }

        var batchIdle = now - batch.LastActivityMs > AttachmentProtocol.BatchIdleTimeoutMs;
        if (stalled.Count == 0 && !batchIdle) return new AttachmentTimeoutReport([], null);

        batch.Cancelled = true;
        foreach (var file in batch.Files.Values)
        {
            file.Sent.Clear();
            PinCacheEntry(file, batch.TargetDocumentEpoch, batch.TargetComposerEpoch, batch.BatchId);
        }

        return new AttachmentTimeoutReport(stalled, batch.BatchId);
    }

    /// <summary>应用一条已解码消息。判定顺序与 README §7 / TS 实现一致。</summary>
    public AttachmentApplyResult Apply(AttachmentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _now();

        switch (message.Kind)
        {
            case WireMessageKind.Hello:
                return Accepted(message.Kind, null, null, duplicate: false, importInvoked: false);
            case WireMessageKind.Capabilities:
                _limits = ReadLimits(message) ?? _limits;
                return Accepted(message.Kind, null, null, duplicate: false, importInvoked: false);
            case WireMessageKind.Context:
                BindContext(message);
                return Accepted(message.Kind, null, null, duplicate: false, importInvoked: false);
            default:
                break;
        }

        // —— 会话绑定：没有有效 sessionId 不接收任何操作消息 ——
        if (message.SessionId is null) return Reject("no-session", "操作消息必须携带 sessionId", string.Empty);
        if (_identity is null) return Reject("no-session", "尚未建立 context，拒绝操作消息", string.Empty);
        if (_identityExpired) return Reject("context-changed", "身份已因导航/关闭过期，需重新 context", string.Empty);
        if (!string.Equals(message.SessionId, _identity.SessionId, StringComparison.Ordinal))
        {
            return Reject("context-changed", $"sessionId 与已绑定上下文不一致（{message.SessionId}）", string.Empty);
        }

        return message.Kind switch
        {
            WireMessageKind.BatchBegin => ApplyBatchBegin(message, now),
            WireMessageKind.Cancel => ApplyCancel(message, now),
            WireMessageKind.FileBegin => ApplyFileBegin(message, now),
            WireMessageKind.Chunk => ApplyChunk(message, now),
            WireMessageKind.Ack => ApplyAck(message, now),
            WireMessageKind.FileEnd => ApplyFileEnd(message, now),
            WireMessageKind.ImportResult => ApplyImportResult(message, now),
            WireMessageKind.BatchEnd => ApplyBatchEnd(message, now),
            _ => Reject("unknown-message-type", "未处理的消息类型", string.Empty),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var file in _owned) file.Dispose();
        _owned.Clear();
    }

    // ————————————————————————————————————————————————————————————
    // 握手
    // ————————————————————————————————————————————————————————————

    private void BindContext(AttachmentMessage message)
    {
        var next = new AttachmentIdentity(
            message.SessionId!,
            message.TargetId!,
            message.DocumentEpoch!.Value,
            message.ComposerEpoch!.Value,
            message.ComposerScope!);

        var changed = _identity is null
            || !string.Equals(_identity.SessionId, next.SessionId, StringComparison.Ordinal)
            || !string.Equals(_identity.TargetId, next.TargetId, StringComparison.Ordinal)
            || _identity.ComposerEpoch != next.ComposerEpoch
            || !string.Equals(_identity.ComposerScope, next.ComposerScope, StringComparison.Ordinal)
            || _identity.DocumentEpoch != next.DocumentEpoch;

        if (changed)
        {
            // composer 身份变化即失效：旧批次与旧缓存都不得继续复用。
            _cache.Clear();
            if (_batch is not null)
            {
                _batch.Closed = true;
                _batch.Cancelled = true;
            }

            _batch = null;
        }

        _identity = next;
        _identityExpired = false;
    }

    private static AttachmentLimits? ReadLimits(AttachmentMessage message)
    {
        var limits = message.Limits;
        if (limits is null) return null;
        return new AttachmentLimits
        {
            MaxFileBytes = limits["maxFileBytes"]!.GetValue<int>(),
            MaxFilesPerBatch = limits["maxFilesPerBatch"]!.GetValue<int>(),
            MaxBatchBytes = limits["maxBatchBytes"]!.GetValue<int>(),
            MaxScreenshotPixels = limits["maxScreenshotPixels"]!.GetValue<int>(),
            MaxStagingBytesPerTarget = limits["maxStagingBytesPerTarget"]!.GetValue<int>(),
            MaxConcurrentTargets = limits["maxConcurrentTargets"]!.GetValue<int>(),
        };
    }

    // ————————————————————————————————————————————————————————————
    // 批次
    // ————————————————————————————————————————————————————————————

    private AttachmentApplyResult ApplyBatchBegin(AttachmentMessage message, long now)
    {
        var identity = _identity!;
        if (!string.Equals(message.TargetId, identity.TargetId, StringComparison.Ordinal)
            || message.DocumentEpoch != identity.DocumentEpoch
            || message.ComposerEpoch != identity.ComposerEpoch)
        {
            return Reject("context-changed", "批次身份与当前上下文不一致（targetId/双 epoch）", string.Empty);
        }

        if (_batch is not null)
        {
            // 已取消的批次与已关闭的批次一样不再占用会话；取消后必须能用新 batchId 重新开始
            // （与 TS session.ts 的准入规则保持一致，R15 修复）。
            if (!_batch.Closed && !_batch.Cancelled)
            {
                return Reject("batch-in-progress", "同一会话已有开放批次", string.Empty);
            }

            if (string.Equals(_batch.BatchId, message.BatchId, StringComparison.Ordinal))
            {
                return Reject("duplicate-operation", "该 batchId 已关闭；重新导入必须生成新的 batchId", string.Empty);
            }
        }

        var stale = _cache.Get(AttachmentProtocol.ReplayKey(identity.DocumentEpoch, identity.ComposerEpoch, message.BatchId!, string.Empty));
        if (stale is not null)
        {
            return Reject("duplicate-operation", "该 batchId 已关闭；重新导入必须生成新的 batchId", string.Empty);
        }

        if (message.FileCount > _limits.MaxFilesPerBatch)
        {
            return Reject("limit-batch-files", $"fileCount {message.FileCount} > {_limits.MaxFilesPerBatch}", string.Empty);
        }

        if (message.TotalBytes > _limits.MaxBatchBytes)
        {
            return Reject("limit-batch-bytes", $"totalBytes {message.TotalBytes} > {_limits.MaxBatchBytes}", string.Empty);
        }

        _batch = new BatchState
        {
            BatchId = message.BatchId!,
            FileCount = message.FileCount!.Value,
            TotalBytes = message.TotalBytes!.Value,
            TargetId = message.TargetId!,
            TargetDocumentEpoch = message.DocumentEpoch!.Value,
            TargetComposerEpoch = message.ComposerEpoch!.Value,
            StartedAtMs = now,
            LastActivityMs = now,
        };
        _cache.Put(
            "batch",
            identity.DocumentEpoch,
            identity.ComposerEpoch,
            message.BatchId!,
            string.Empty,
            string.Empty,
            new AttachmentReplaySummary(false, [], "none", null));
        return Accepted(message.Kind, message.BatchId, null, duplicate: false, importInvoked: false);
    }

    private AttachmentApplyResult ApplyCancel(AttachmentMessage message, long now)
    {
        var batch = _batch;
        if (batch is null || !string.Equals(batch.BatchId, message.BatchId, StringComparison.Ordinal))
        {
            var stale = ClosedBatchExists(message.BatchId!);
            return Reject(
                stale ? "batch-closed" : "batch-not-open",
                stale ? "批次已关闭" : "未找到该 operation",
                string.Empty);
        }

        var already = batch.Cancelled;
        batch.Cancelled = true;
        batch.LastActivityMs = now;
        // 释放自有缓冲，但保留已 confirm 的草稿结果（部分成功必须可确认）。
        foreach (var file in batch.Files.Values)
        {
            file.Sent.Clear();
            PinCacheEntry(file, batch.TargetDocumentEpoch, batch.TargetComposerEpoch, batch.BatchId);
        }

        return Accepted(message.Kind, message.BatchId, null, duplicate: already, importInvoked: false);
    }

    private AttachmentApplyResult ApplyFileBegin(AttachmentMessage message, long now)
    {
        var guarded = GuardBatch(message.BatchId!);
        if (guarded.Result is not null) return guarded.Result;
        var batch = guarded.Batch!;

        if (message.ByteLength > _limits.MaxFileBytes)
        {
            return Reject("limit-file-bytes", $"byteLength {message.ByteLength} > {_limits.MaxFileBytes}", string.Empty);
        }

        if (batch.Files.ContainsKey(message.FileId!))
        {
            return Reject("duplicate-operation", $"fileId {message.FileId} 已在本批次声明过", string.Empty);
        }

        if (batch.Files.Count >= batch.FileCount)
        {
            return Reject("limit-batch-files", $"批内文件数已达 batch-begin 声明的 {batch.FileCount}", string.Empty);
        }

        if (batch.ActiveFileId is not null
            && batch.Files.TryGetValue(batch.ActiveFileId, out var active)
            && !active.Ended)
        {
            return Reject("file-in-progress", $"每目标同时只处理一个文件，当前活动文件是 {active.FileId}", string.Empty);
        }

        var file = new FileState
        {
            FileId = message.FileId!,
            Name = message.Name!,
            ByteLength = message.ByteLength!.Value,
            Mime = message.Mime!,
            DeclaredSha256 = message.Sha256,
            LastActivityMs = now,
        };
        batch.Files[file.FileId] = file;
        _owned.Add(file);
        batch.ActiveFileId = file.FileId;
        batch.LastActivityMs = now;
        return Accepted(message.Kind, message.BatchId, message.FileId, duplicate: false, importInvoked: false);
    }

    // ————————————————————————————————————————————————————————————
    // 传输
    // ————————————————————————————————————————————————————————————

    private AttachmentApplyResult ApplyChunk(AttachmentMessage message, long now)
    {
        var guarded = GuardBatch(message.BatchId!);
        if (guarded.Result is not null) return guarded.Result;
        var batch = guarded.Batch!;

        if (!batch.Files.TryGetValue(message.FileId!, out var file))
        {
            return Reject("file-id-mismatch", $"fileId {message.FileId} 不在本批次", string.Empty);
        }

        if (file.Ended || file.Resolved)
        {
            return Reject("duplicate-operation", $"fileId {message.FileId} 的传输已结束，重复块不重新入缓冲", string.Empty);
        }

        if (!string.Equals(batch.ActiveFileId, message.FileId, StringComparison.Ordinal))
        {
            return Reject("file-id-mismatch", $"当前活动文件是 {batch.ActiveFileId ?? "（无）"}", string.Empty);
        }

        if (InFlight(file) >= AttachmentProtocol.MaxChunksInFlight)
        {
            return Reject("window-overflow", $"在途块数已达 {AttachmentProtocol.MaxChunksInFlight}", string.Empty);
        }

        if (message.Seq > file.ExpectedSeq)
        {
            return Reject("sequence-gap", $"期望 seq {file.ExpectedSeq}，收到 {message.Seq}", string.Empty);
        }

        if (message.Seq < file.ExpectedSeq)
        {
            return Reject("seq-overlap", $"seq {message.Seq} 已接收过（期望 {file.ExpectedSeq}）", string.Empty);
        }

        if (message.Offset > file.ExpectedOffset)
        {
            return Reject("sequence-gap", $"期望 offset {file.ExpectedOffset}，收到 {message.Offset}", string.Empty);
        }

        if (message.Offset < file.ExpectedOffset)
        {
            return Reject("seq-overlap", $"offset {message.Offset} 回退（期望 {file.ExpectedOffset}）", string.Empty);
        }

        if (file.ReceivedBytes + message.ByteLength > file.ByteLength)
        {
            return Reject(
                "size-mismatch",
                $"块超出文件声明长度：{file.ReceivedBytes} + {message.ByteLength} > {file.ByteLength}",
                string.Empty);
        }

        if (!AttachmentCodec.TryDecodeBase64(message.DataBase64!, out var payload))
        {
            return Reject("invalid-field-value", "Base64 解码失败", "dataBase64");
        }

        if (payload.Length != message.ByteLength)
        {
            return Reject("size-mismatch", $"载荷 {payload.Length} 字节 ≠ 声明 {message.ByteLength}", string.Empty);
        }

        file.Append(payload);
        file.Sent[message.Seq!.Value] = new SentChunk(message.Offset!.Value, message.ByteLength.Value);
        file.ExpectedSeq += 1;
        file.ExpectedOffset += message.ByteLength.Value;
        file.ReceivedBytes += message.ByteLength.Value;
        file.LastActivityMs = now;
        batch.LastActivityMs = now;
        return Accepted(message.Kind, message.BatchId, message.FileId, duplicate: false, importInvoked: false);
    }

    private AttachmentApplyResult ApplyAck(AttachmentMessage message, long now)
    {
        var guarded = GuardBatch(message.BatchId!);
        if (guarded.Result is not null) return guarded.Result;
        var batch = guarded.Batch!;

        if (!batch.Files.TryGetValue(message.FileId!, out var file))
        {
            return Reject("file-id-mismatch", $"fileId {message.FileId} 不在本批次", string.Empty);
        }

        if (message.BufferedBytes > _limits.MaxStagingBytesPerTarget)
        {
            return Reject(
                "limit-staging-bytes",
                $"bufferedBytes {message.BufferedBytes} > {_limits.MaxStagingBytesPerTarget}",
                string.Empty);
        }

        if (message.Seq > file.ExpectedSeq - 1) return Reject("sequence-gap", $"ack 指向从未发送的块 seq {message.Seq}", string.Empty);
        if (!file.Sent.TryGetValue(message.Seq!.Value, out var sent)) return Reject("sequence-gap", $"ack 指向从未发送的块 seq {message.Seq}", string.Empty);
        if (sent.Acked)
        {
            // 重复 ack 是幂等重放：不改变已确认字节。
            return Accepted(message.Kind, message.BatchId, message.FileId, duplicate: true, importInvoked: false);
        }

        if (sent.Offset != message.Offset || sent.ByteLength != message.ByteLength)
        {
            return Reject("size-mismatch", "ack 的 offset/byteLength 与该块不符", string.Empty);
        }

        sent.Acked = true;
        file.AckedBytes += sent.ByteLength;
        file.LastActivityMs = now;
        batch.LastActivityMs = now;
        return Accepted(message.Kind, message.BatchId, message.FileId, duplicate: false, importInvoked: false);
    }

    // ————————————————————————————————————————————————————————————
    // 结束与结果
    // ————————————————————————————————————————————————————————————

    private AttachmentApplyResult ApplyFileEnd(AttachmentMessage message, long now)
    {
        var guarded = GuardBatch(message.BatchId!);
        if (guarded.Result is not null) return guarded.Result;
        var batch = guarded.Batch!;

        if (!batch.Files.TryGetValue(message.FileId!, out var file))
        {
            return Reject("file-id-mismatch", $"fileId {message.FileId} 不在本批次", string.Empty);
        }

        var incomingDigest = AttachmentCodec.CanonicalJson(new System.Text.Json.Nodes.JsonObject
        {
            ["totalBytes"] = message.TotalBytes,
            ["sha256"] = message.Sha256,
        });

        if (file.Ended)
        {
            // 重复 file-end 绝不产生第二次导入：内容一致即幂等，冲突则拒绝。
            return string.Equals(file.EndDigest, incomingDigest, StringComparison.Ordinal)
                ? Accepted(message.Kind, message.BatchId, message.FileId, duplicate: true, importInvoked: false)
                : Reject("duplicate-file-end", $"fileId {message.FileId} 已结束且本次内容不同", string.Empty);
        }

        if (!string.Equals(batch.ActiveFileId, message.FileId, StringComparison.Ordinal))
        {
            return Reject("file-id-mismatch", $"当前活动文件是 {batch.ActiveFileId ?? "（无）"}", string.Empty);
        }

        if (message.TotalBytes != file.ByteLength)
        {
            return Reject("size-mismatch", $"file-end 声明 {message.TotalBytes} 字节 ≠ file-begin 的 {file.ByteLength}", string.Empty);
        }

        if (file.ReceivedBytes != message.TotalBytes)
        {
            return Reject("size-mismatch", $"已接收 {file.ReceivedBytes} 字节 ≠ file-end 的 {message.TotalBytes}", string.Empty);
        }

        if (file.DeclaredSha256 is not null && !string.Equals(file.DeclaredSha256, message.Sha256, StringComparison.Ordinal))
        {
            return Reject("hash-mismatch", "file-end 哈希与 file-begin 声明的哈希不符", string.Empty);
        }

        var digest = file.DigestHex();
        if (!string.Equals(digest, message.Sha256, StringComparison.Ordinal))
        {
            return Reject("hash-mismatch", $"实际 SHA-256 {digest} ≠ 声明 {message.Sha256}", string.Empty);
        }

        file.Ended = true;
        file.SubmittedItems = message.SubmittedItems;
        file.EndDigest = incomingDigest;
        file.LastActivityMs = now;
        batch.LastActivityMs = now;
        // 活动操作被钉住：容量淘汰不得让它被重新执行。
        _cache.Put(
            "file",
            batch.TargetDocumentEpoch,
            batch.TargetComposerEpoch,
            batch.BatchId,
            file.FileId,
            incomingDigest,
            new AttachmentReplaySummary(true, [], "none", null));
        return Accepted(message.Kind, message.BatchId, message.FileId, duplicate: false, importInvoked: true);
    }

    private AttachmentApplyResult ApplyImportResult(AttachmentMessage message, long now)
    {
        var guarded = GuardBatch(message.BatchId!);
        if (guarded.Result is not null) return guarded.Result;
        var batch = guarded.Batch!;

        if (!batch.Files.TryGetValue(message.FileId!, out var file))
        {
            return Reject("file-id-mismatch", $"fileId {message.FileId} 不在本批次", string.Empty);
        }

        if (!file.Ended) return Reject("file-not-ended", $"fileId {message.FileId} 尚未 file-end", string.Empty);

        var incomingDigest = AttachmentCodec.CanonicalJson(new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = message.Status,
            ["attachmentIds"] = new System.Text.Json.Nodes.JsonArray([.. message.AttachmentIds.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id))]),
            ["code"] = message.Code,
        });

        if (file.Resolved)
        {
            return string.Equals(file.ResultDigest, incomingDigest, StringComparison.Ordinal)
                ? Accepted(message.Kind, message.BatchId, message.FileId, duplicate: true, importInvoked: false)
                : Reject("duplicate-operation", $"fileId {message.FileId} 的结果已确定，重复内容冲突", string.Empty);
        }

        var ids = message.AttachmentIds;
        int? expectedIds = message.Status switch
        {
            "staged" => file.SubmittedItems,
            "failed" => 0,
            _ => null,
        };
        if (expectedIds is not null && ids.Count != expectedIds)
        {
            return Reject(
                "import-id-count-mismatch",
                $"status={message.Status} 期望 {expectedIds} 个新增 ID，实际 {ids.Count}",
                string.Empty);
        }

        if (message.Status == "partial" && ids.Count == 0)
        {
            return Reject("import-id-count-mismatch", "partial 必须至少包含 1 个已确认的新增 ID", string.Empty);
        }

        if ((message.Status == "failed" || message.Status == "partial") && message.Code is null)
        {
            return Reject("missing-field", $"status={message.Status} 必须给出 code", string.Empty);
        }

        var known = new HashSet<string>(
            batch.Files.Values.SelectMany(other => other.AttachmentIds),
            StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (known.Contains(id)) return Reject("duplicate-operation", $"附件 ID {id} 已在本批次出现过", string.Empty);
        }

        var draft = message.Status switch
        {
            "staged" => AttachmentDraftState.Staged,
            "failed" => AttachmentDraftState.Failed,
            _ => AttachmentDraftState.Partial,
        };
        file.Draft = draft;
        // staged 只代表草稿接收；上传与发送仍归 Harness，线协议里没有 ready。
        file.Upload = draft == AttachmentDraftState.Staged ? AttachmentUploadState.HarnessOwned : AttachmentUploadState.None;
        file.AttachmentIds.Clear();
        file.AttachmentIds.AddRange(ids);
        file.Resolved = true;
        file.ResultDigest = incomingDigest;
        file.LastActivityMs = now;
        batch.LastActivityMs = now;
        _cache.Complete(
            AttachmentProtocol.ReplayKey(batch.TargetDocumentEpoch, batch.TargetComposerEpoch, batch.BatchId, file.FileId),
            new AttachmentReplaySummary(true, [.. ids], message.Status ?? "none", message.Status),
            incomingDigest);
        return Accepted(message.Kind, message.BatchId, message.FileId, duplicate: false, importInvoked: false);
    }

    private AttachmentApplyResult ApplyBatchEnd(AttachmentMessage message, long now)
    {
        var batch = _batch;
        var incomingDigest = AttachmentCodec.CanonicalJson(new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = message.Status,
            ["results"] = message.Results?.DeepClone(),
        });

        if (batch is null || !string.Equals(batch.BatchId, message.BatchId, StringComparison.Ordinal))
        {
            // 批次关闭后只再接受重复的 batch-end（幂等/冲突）与 cancel。
            var identity = _identity;
            var stale = identity is null
                ? null
                : _cache.Get(AttachmentProtocol.ReplayKey(identity.DocumentEpoch, identity.ComposerEpoch, message.BatchId!, string.Empty));
            if (stale is null) return Reject("batch-not-open", "未找到该 operation", string.Empty);
            return string.Equals(stale.PayloadDigest, incomingDigest, StringComparison.Ordinal)
                ? Accepted(message.Kind, message.BatchId, null, duplicate: true, importInvoked: false)
                : Reject("duplicate-operation", "批次已关闭且本次 batch-end 内容冲突", string.Empty);
        }

        if (batch.Cancelled) return Reject("cancelled", "批次已取消，迟到结果被拒绝", string.Empty);

        if (batch.Closed)
        {
            var stale = _cache.Get(AttachmentProtocol.ReplayKey(batch.TargetDocumentEpoch, batch.TargetComposerEpoch, batch.BatchId, string.Empty));
            return stale is not null && string.Equals(stale.PayloadDigest, incomingDigest, StringComparison.Ordinal)
                ? Accepted(message.Kind, message.BatchId, null, duplicate: true, importInvoked: false)
                : Reject("duplicate-operation", "批次已关闭且本次 batch-end 内容冲突", string.Empty);
        }

        foreach (var file in batch.Files.Values)
        {
            if (!file.Resolved) return Reject("result-incomplete", $"fileId {file.FileId} 尚无确定结果，不能关闭批次", string.Empty);
        }

        var results = message.Results!;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var declaredList = new List<string>();
        foreach (var item in results)
        {
            var fileId = item!["fileId"]!.GetValue<string>();
            if (!declared.Add(fileId)) return Reject("invalid-field-value", "batch-end 的 fileId 重复", "results");
            declaredList.Add(fileId);
        }

        if (declared.Count != batch.Files.Count)
        {
            return Reject("result-incomplete", "batch-end 必须覆盖批内全部 fileId", string.Empty);
        }

        foreach (var item in results)
        {
            var obj = item!.AsObject();
            var fileId = obj["fileId"]!.GetValue<string>();
            if (!batch.Files.TryGetValue(fileId, out var file)) return Reject("file-id-mismatch", $"fileId {fileId} 不在本批次", "results");
            var status = obj["status"]!.GetValue<string>();
            var ids = obj["attachmentIds"] is { } idsNode && idsNode is System.Text.Json.Nodes.JsonArray array
                ? array.Select(node => node!.GetValue<string>()).ToList()
                : [];
            var draftName = DraftName(file.Draft);
            if (!string.Equals(status, draftName, StringComparison.Ordinal))
            {
                return Reject("result-conflict", $"fileId {fileId} 的状态 {status} 与记录的 {draftName} 冲突", "results");
            }

            if (!AttachmentCodec.CanonicalEquals(
                    new System.Text.Json.Nodes.JsonArray([.. ids.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id))]),
                    new System.Text.Json.Nodes.JsonArray([.. file.AttachmentIds.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id))])))
            {
                return Reject("result-conflict", $"fileId {fileId} 的 attachmentIds 与记录冲突", "results");
            }
        }

        batch.Closed = true;
        batch.LastActivityMs = now;
        foreach (var file in batch.Files.Values)
        {
            PinCacheEntry(file, batch.TargetDocumentEpoch, batch.TargetComposerEpoch, batch.BatchId);
        }

        _cache.Complete(
            AttachmentProtocol.ReplayKey(batch.TargetDocumentEpoch, batch.TargetComposerEpoch, batch.BatchId, string.Empty),
            new AttachmentReplaySummary(false, [], message.Status ?? "none", message.Status),
            incomingDigest);
        return Accepted(message.Kind, message.BatchId, null, duplicate: false, importInvoked: false);
    }

    // ————————————————————————————————————————————————————————————
    // 内部工具
    // ————————————————————————————————————————————————————————————

    private (BatchState? Batch, AttachmentApplyResult? Result) GuardBatch(string batchId)
    {
        var batch = _batch;
        if (batch is not null && string.Equals(batch.BatchId, batchId, StringComparison.Ordinal))
        {
            if (batch.Cancelled) return (null, Reject("cancelled", "批次已取消，迟到消息被拒绝", string.Empty));
            if (batch.Closed) return (null, Reject("batch-closed", "批次已关闭；新批次必须经 batch-begin 承认", string.Empty));
            return (batch, null);
        }

        if (ClosedBatchExists(batchId))
        {
            return (null, Reject("batch-closed", "批次已关闭；新批次必须经 batch-begin 承认", string.Empty));
        }

        return (null, Reject("batch-not-open", "未找到该 operation", string.Empty));
    }

    private bool ClosedBatchExists(string batchId)
    {
        var identity = _identity;
        if (identity is null) return false;
        return _cache.Get(AttachmentProtocol.ReplayKey(identity.DocumentEpoch, identity.ComposerEpoch, batchId, string.Empty)) is not null;
    }

    private static int InFlight(FileState file) => file.Sent.Values.Count(sent => !sent.Acked);

    private static string DraftName(AttachmentDraftState draft) => draft switch
    {
        AttachmentDraftState.Staged => "staged",
        AttachmentDraftState.Failed => "failed",
        AttachmentDraftState.Partial => "partial",
        _ => "none",
    };

    private static AttachmentTransportState TransportOf(FileState file)
    {
        if (file.Sent.Count == 0) return file.Ended ? AttachmentTransportState.Buffered : AttachmentTransportState.Idle;
        return InFlight(file) == 0 ? AttachmentTransportState.Buffered : AttachmentTransportState.Buffering;
    }

    private static AttachmentFileRecord RecordOf(FileState file) => new()
    {
        FileId = file.FileId,
        Name = file.Name,
        ByteLength = file.ByteLength,
        Mime = file.Mime,
        DeclaredSha256 = file.DeclaredSha256,
        Transport = TransportOf(file),
        Draft = file.Draft,
        Upload = file.Upload,
        ReceivedBytes = file.ReceivedBytes,
        AckedBytes = file.AckedBytes,
        InFlight = InFlight(file),
        Ended = file.Ended,
        Resolved = file.Resolved,
        SubmittedItems = file.SubmittedItems,
        AttachmentIds = [.. file.AttachmentIds],
    };

    private void PinCacheEntry(FileState file, int documentEpoch, int composerEpoch, string batchId)
    {
        var entry = _cache.Get(AttachmentProtocol.ReplayKey(documentEpoch, composerEpoch, batchId, file.FileId));
        if (entry is not null) entry.State = "completed";
    }

    private AttachmentApplyResult Accepted(
        WireMessageKind kind,
        string? batchId,
        string? fileId,
        bool duplicate,
        bool importInvoked) => new()
        {
            Ok = true,
            Kind = kind,
            BatchId = batchId,
            FileId = fileId,
            Duplicate = duplicate,
            ImportInvoked = importInvoked,
            Closed = BatchClosed,
            File = fileId is null ? null : FileRecord(fileId),
            Files = Files,
        };

    private static AttachmentApplyResult Reject(string code, string detail, string path) =>
        new() { Ok = false, Code = code, Detail = detail, Path = path };

    private sealed class SentChunk(int offset, int byteLength)
    {
        public int Offset { get; } = offset;

        public int ByteLength { get; } = byteLength;

        public bool Acked { get; set; }
    }

    private sealed class FileState : IDisposable
    {
        private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _digest;

        public required string FileId { get; init; }

        public required string Name { get; init; }

        public required int ByteLength { get; init; }

        public required string Mime { get; init; }

        public string? DeclaredSha256 { get; init; }

        public int ExpectedSeq { get; set; }

        public int ExpectedOffset { get; set; }

        public Dictionary<int, SentChunk> Sent { get; } = [];

        public int ReceivedBytes { get; set; }

        public int AckedBytes { get; set; }

        public bool Ended { get; set; }

        public bool Resolved { get; set; }

        public AttachmentDraftState Draft { get; set; } = AttachmentDraftState.None;

        public AttachmentUploadState Upload { get; set; } = AttachmentUploadState.None;

        public int SubmittedItems { get; set; } = 1;

        public List<string> AttachmentIds { get; } = [];

        public string? EndDigest { get; set; }

        public string? ResultDigest { get; set; }

        public long LastActivityMs { get; set; }

        public void Append(byte[] payload) => _hasher.AppendData(payload);

        public string DigestHex()
        {
            _digest ??= Convert.ToHexStringLower(_hasher.GetHashAndReset());
            return _digest;
        }

        public void Dispose() => _hasher.Dispose();
    }

    private sealed class BatchState
    {
        public required string BatchId { get; init; }

        public required int FileCount { get; init; }

        public required int TotalBytes { get; init; }

        public required string TargetId { get; init; }

        public required int TargetDocumentEpoch { get; init; }

        public required int TargetComposerEpoch { get; init; }

        public required long StartedAtMs { get; init; }

        public Dictionary<string, FileState> Files { get; } = new(StringComparer.Ordinal);

        public string? ActiveFileId { get; set; }

        public bool Closed { get; set; }

        public bool Cancelled { get; set; }

        public long LastActivityMs { get; set; }
    }
}
