namespace DshLauncher.Core.Attachments;

/// <summary>重放时可直接复用的结论摘要。</summary>
public sealed record AttachmentReplaySummary(
    bool ImportInvoked,
    IReadOnlyList<string> AttachmentIds,
    string Draft,
    string? Status);

/// <summary>
/// 重放/去重缓存条目。键至少包含 documentEpoch + composerEpoch + batchId + fileId
/// （批级操作用空 fileId）；活动条目被钉住，容量淘汰不得驱逐它。
/// </summary>
public sealed class AttachmentReplayEntry
{
    public required string Kind { get; init; }

    public required string Key { get; init; }

    public required int DocumentEpoch { get; init; }

    public required int ComposerEpoch { get; init; }

    public required string BatchId { get; init; }

    public required string FileId { get; init; }

    public required long CreatedAtMs { get; init; }

    public required long ExpiresAtMs { get; set; }

    public string PayloadDigest { get; set; } = string.Empty;

    /// <summary>"active"（钉住）或 "completed"（可淘汰）。</summary>
    public string State { get; set; } = "active";

    public AttachmentReplaySummary Summary { get; set; } = new(false, [], "none", null);
}

/// <summary>
/// 有容量与生命周期上限的去重缓存：查询时丢弃过期条目，容量淘汰只动已完成条目。
/// </summary>
public sealed class AttachmentReplayCache
{
    private readonly Dictionary<string, AttachmentReplayEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly int _lifetimeMs;
    private readonly Func<long> _now;

    public AttachmentReplayCache(int? capacity = null, int? lifetimeMs = null, Func<long>? now = null)
    {
        _capacity = capacity ?? AttachmentProtocol.ReplayCacheCapacity;
        _lifetimeMs = lifetimeMs ?? AttachmentProtocol.ReplayCacheLifetimeMs;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>未过期条目数；查询时顺带丢弃已过期条目。</summary>
    public int Count
    {
        get
        {
            PurgeExpired();
            return _entries.Count;
        }
    }

    /// <summary>被钉住（活动）的条目数。</summary>
    public int PinnedCount => _entries.Values.Count(entry => entry.State == "active");

    public int EvictedCount { get; private set; }

    /// <summary>容量已满且无可淘汰条目时拒绝淘汰的次数。</summary>
    public int RefusedEvictionCount { get; private set; }

    /// <summary>读取未过期条目；过期即删除。</summary>
    public AttachmentReplayEntry? Get(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        if (entry.ExpiresAtMs <= _now())
        {
            _entries.Remove(key);
            return null;
        }

        return entry;
    }

    /// <summary>创建并写入活动条目。</summary>
    public AttachmentReplayEntry Put(
        string kind,
        int documentEpoch,
        int composerEpoch,
        string batchId,
        string fileId,
        string payloadDigest,
        AttachmentReplaySummary summary)
    {
        var now = _now();
        var entry = new AttachmentReplayEntry
        {
            Kind = kind,
            Key = AttachmentProtocol.ReplayKey(documentEpoch, composerEpoch, batchId, fileId),
            DocumentEpoch = documentEpoch,
            ComposerEpoch = composerEpoch,
            BatchId = batchId,
            FileId = fileId,
            CreatedAtMs = now,
            ExpiresAtMs = now + _lifetimeMs,
            PayloadDigest = payloadDigest,
            Summary = summary,
        };
        if (!_entries.ContainsKey(entry.Key) && _entries.Count >= _capacity) EvictOne();
        _entries[entry.Key] = entry;
        return entry;
    }

    /// <summary>标记完成：解除钉住并顺延生命周期。</summary>
    public AttachmentReplayEntry? Complete(string key, AttachmentReplaySummary summary, string payloadDigest)
    {
        var entry = Get(key);
        if (entry is null) return null;
        entry.State = "completed";
        entry.Summary = summary;
        entry.PayloadDigest = payloadDigest;
        entry.ExpiresAtMs = _now() + _lifetimeMs;
        return entry;
    }

    public void Delete(string key) => _entries.Remove(key);

    /// <summary>清空（导航/关闭）。</summary>
    public void Clear() => _entries.Clear();

    private void PurgeExpired()
    {
        var now = _now();
        foreach (var key in _entries.Where(pair => pair.Value.ExpiresAtMs <= now).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private void EvictOne()
    {
        AttachmentReplayEntry? oldest = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.State == "active") continue;
            if (oldest is null || entry.CreatedAtMs < oldest.CreatedAtMs) oldest = entry;
        }

        if (oldest is null)
        {
            // 全部被钉住：宁可持续超出容量，也不让活动操作失去去重保护。
            RefusedEvictionCount += 1;
            return;
        }

        _entries.Remove(oldest.Key);
        EvictedCount += 1;
    }
}
