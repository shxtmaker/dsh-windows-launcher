using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// 一条协调器决策记录。全部字段都是确定性的：时间来自注入时钟，
/// <see cref="Seq"/> 是单调递增的调用序号，<see cref="Canonical"/> 是
/// <see cref="AttachmentCodec"/> 给出的 canonical 投影（chunk 载荷被替换为
/// <c>data:{bytes,sha256}</c>，因此 trace 不含整块 Base64）。
/// </summary>
public sealed record AttachmentTraceEntry
{
    /// <summary>从 0 开始、按记录顺序递增的序号。</summary>
    public required int Seq { get; init; }

    /// <summary>注入时钟的毫秒读值。</summary>
    public required long AtMs { get; init; }

    /// <summary>"message-out" / "message-in" / "state" / "decision"。</summary>
    public required string Kind { get; init; }

    /// <summary>报文类型或状态对象名。</summary>
    public string? Type { get; init; }

    public string? BatchId { get; init; }

    public string? FileId { get; init; }

    /// <summary>人类可读且稳定的说明（含判定码），测试可逐条断言。</summary>
    public required string Detail { get; init; }

    /// <summary>报文的 canonical 投影；状态/决策条目为 null。</summary>
    public JsonNode? Canonical { get; init; }
}

/// <summary>
/// 协调器 trace（D11）：把"发了什么、收到什么、状态怎么迁移、做了什么判定"
/// 按调用顺序记成结构化条目。它只依赖注入时钟与输入消息，因此同一个场景重复运行
/// 得到逐字节相同的 JSON——测试据此断言确定性，并把它写成可复核的产物。
/// </summary>
public sealed class AttachmentTransferTrace
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly List<AttachmentTraceEntry> _entries = [];

    /// <summary>按记录顺序排列的条目。</summary>
    public IReadOnlyList<AttachmentTraceEntry> Entries => _entries;

    /// <summary>已记录条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>记录一条 trace。</summary>
    public AttachmentTraceEntry Record(
        long atMs,
        string kind,
        string? type,
        string? batchId,
        string? fileId,
        string detail,
        JsonNode? canonical = null)
    {
        var entry = new AttachmentTraceEntry
        {
            Seq = _entries.Count,
            AtMs = atMs,
            Kind = kind,
            Type = type,
            BatchId = batchId,
            FileId = fileId,
            Detail = detail,
            Canonical = canonical,
        };
        _entries.Add(entry);
        return entry;
    }

    /// <summary>投影为 JSON：<c>{ "entries": [ ... ] }</c>；null 字段被省略以保持稳定。</summary>
    public JsonObject ToJson()
    {
        var entries = new JsonArray();
        foreach (var entry in _entries)
        {
            var item = new JsonObject
            {
                ["seq"] = entry.Seq,
                ["atMs"] = entry.AtMs,
                ["kind"] = entry.Kind,
            };
            if (entry.Type is not null) item["type"] = entry.Type;
            if (entry.BatchId is not null) item["batchId"] = entry.BatchId;
            if (entry.FileId is not null) item["fileId"] = entry.FileId;
            item["detail"] = entry.Detail;
            if (entry.Canonical is not null) item["canonical"] = entry.Canonical.DeepClone();
            entries.Add(item);
        }

        return new JsonObject { ["entries"] = entries };
    }

    /// <summary>带缩进的稳定序列化（产物写入与确定性比较都用它）。</summary>
    public static string Serialize(JsonNode? node) => node?.ToJsonString(Indented) ?? "null";
}
