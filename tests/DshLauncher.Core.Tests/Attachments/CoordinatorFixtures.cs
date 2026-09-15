using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>可控时钟：测试直接推进毫秒，任何超时都不需要 sleep。</summary>
internal sealed class ManualClock : IAttachmentClock
{
    public long NowMs { get; set; } = 1_000;

    public void Advance(long milliseconds) => NowMs += milliseconds;
}

/// <summary>
/// 脚本化字节源：支持短读（<see cref="MaxReadSize"/>）、EOF（数据耗尽）、
/// 注入源故障（<see cref="FailAtOffset"/>）与"声明长度大于实际数据"的提前结束，
/// 并记录是否被释放、被读了几次。
/// </summary>
internal sealed class ScriptedByteSource : IAttachmentByteSource
{
    private readonly byte[] _data;
    private int _position;

    public ScriptedByteSource(byte[] data, int maxReadSize = int.MaxValue, int failAtOffset = -1, int? declaredLength = null)
    {
        _data = data;
        MaxReadSize = maxReadSize;
        FailAtOffset = failAtOffset;
        ByteLength = declaredLength ?? data.Length;
    }

    /// <summary>file-begin.byteLength：可与实际数据长度不同，用于构造"提前 EOF"。</summary>
    public int ByteLength { get; }

    /// <summary>单次读取的上限（模拟短读）。</summary>
    public int MaxReadSize { get; }

    /// <summary>读到该偏移后抛 IOException；负数表示不注入故障。</summary>
    public int FailAtOffset { get; }

    public int ReadCount { get; private set; }

    public bool Disposed { get; private set; }

    public int Read(Span<byte> destination)
    {
        ReadCount += 1;
        ObjectDisposedException.ThrowIf(Disposed, this);
        var available = _data.Length - _position;
        var size = Math.Min(Math.Min(destination.Length, MaxReadSize), available);
        if (FailAtOffset >= 0)
        {
            if (_position >= FailAtOffset) throw new IOException($"注入的源故障（offset {FailAtOffset}）");
            size = Math.Min(size, FailAtOffset - _position);
        }

        if (size <= 0) return 0;
        _data.AsSpan(_position, size).CopyTo(destination);
        _position += size;
        return size;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// 记录型通道：<see cref="Send"/> 把帧原样存下来并立刻用生产 codec 解码（协调器发出的帧
/// 必须是合法 D10 报文）；<see cref="Deliver"/> 注入一条对端帧。同时按时间顺序记录出/入站帧，
/// 供"把协调器输出重放进 D10 生产状态机"的整链测试使用。
/// </summary>
internal sealed class RecordingChannel : IAttachmentChannel
{
    private readonly Queue<string> _inbound = new();

    /// <summary>按发生顺序排列的 (direction, json)：out = 协调器发出，in = 对端注入。</summary>
    public List<(string Direction, string Json)> Log { get; } = [];

    public List<string> SentJson { get; } = [];

    public List<AttachmentMessage> Sent { get; } = [];

    public void Send(string wireJson)
    {
        SentJson.Add(wireJson);
        Log.Add(("out", wireJson));
        var decoded = AttachmentCodec.Decode(wireJson);
        Assert.True(decoded.Ok, $"协调器发出了 D10 codec 拒绝的帧：{decoded.Code} {decoded.Detail}");
        Sent.Add(decoded.Message!);
    }

    public bool TryReceive([NotNullWhen(true)] out string? wireJson)
    {
        if (_inbound.Count == 0)
        {
            wireJson = null;
            return false;
        }

        wireJson = _inbound.Dequeue();
        return true;
    }

    /// <summary>注入一条对端帧（会立刻用生产 codec 校验，测试不会悄悄构造非法报文）。</summary>
    public void Deliver(string wireJson)
    {
        var decoded = AttachmentCodec.Decode(wireJson);
        Assert.True(decoded.Ok, $"测试注入的入站帧必须合法：{decoded.Code} {decoded.Detail}");
        Log.Add(("in", wireJson));
        _inbound.Enqueue(wireJson);
    }

    public IReadOnlyList<string> Types => [.. Sent.Select(message => message.Type)];

    public IEnumerable<AttachmentMessage> OfType(string type) => Sent.Where(message => message.Type == type);

    public int CountOfType(string type) => Sent.Count(message => message.Type == type);
}

/// <summary>对端帧构造器：字段顺序与 D10 冻结顺序一致，并经生产 codec 校验后返回文本。</summary>
internal static class PeerWire
{
    public static string Ack(
        string sessionId,
        string batchId,
        string fileId,
        int seq,
        int offset,
        int byteLength,
        int bufferedBytes = 0,
        int inFlight = 0) => Encode(new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "ack",
            ["sessionId"] = sessionId,
            ["batchId"] = batchId,
            ["fileId"] = fileId,
            ["seq"] = seq,
            ["offset"] = offset,
            ["byteLength"] = byteLength,
            ["bufferedBytes"] = bufferedBytes,
            ["inFlight"] = inFlight,
        });

    public static string ImportResult(
        string sessionId,
        string batchId,
        string fileId,
        string status,
        IReadOnlyList<string> attachmentIds,
        string? code = null)
    {
        var fields = new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "import-result",
            ["sessionId"] = sessionId,
            ["batchId"] = batchId,
            ["fileId"] = fileId,
            ["status"] = status,
            ["attachmentIds"] = new JsonArray([.. attachmentIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
        };
        if (code is not null) fields["code"] = code;
        return Encode(fields);
    }

    public static string FileEnd(string sessionId, string batchId, string fileId, int totalBytes, string sha256, int submittedItems = 1) =>
        Encode(new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "file-end",
            ["sessionId"] = sessionId,
            ["batchId"] = batchId,
            ["fileId"] = fileId,
            ["totalBytes"] = totalBytes,
            ["sha256"] = sha256,
            ["submittedItems"] = submittedItems,
        });

    public static string BatchEnd(string sessionId, string batchId, string status, JsonArray results) => Encode(new JsonObject
    {
        ["v"] = AttachmentProtocol.Version,
        ["type"] = "batch-end",
        ["sessionId"] = sessionId,
        ["batchId"] = batchId,
        ["status"] = status,
        ["results"] = results.DeepClone(),
    });

    public static string Cancel(string sessionId, string batchId, string reason = "cancelled", string? stage = null)
    {
        var fields = new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "cancel",
            ["sessionId"] = sessionId,
            ["batchId"] = batchId,
            ["reason"] = reason,
        };
        if (stage is not null) fields["stage"] = stage;
        return Encode(fields);
    }

    public static string Chunk(
        string sessionId,
        string batchId,
        string fileId,
        int seq,
        int offset,
        byte[] payload) => Encode(new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "chunk",
            ["sessionId"] = sessionId,
            ["batchId"] = batchId,
            ["fileId"] = fileId,
            ["seq"] = seq,
            ["offset"] = offset,
            ["byteLength"] = payload.Length,
            ["dataBase64"] = Convert.ToBase64String(payload),
        });

    public static JsonObject ResultItem(string fileId, string status, IReadOnlyList<string>? attachmentIds = null, string? code = null)
    {
        var item = new JsonObject { ["fileId"] = fileId, ["status"] = status };
        if (attachmentIds is { Count: > 0 })
        {
            item["attachmentIds"] = new JsonArray([.. attachmentIds.Select(id => (JsonNode?)JsonValue.Create(id))]);
        }

        if (code is not null) item["code"] = code;
        return item;
    }

    private static string Encode(JsonObject fields)
    {
        var text = fields.ToJsonString();
        var decoded = AttachmentCodec.Decode(text);
        Assert.True(decoded.Ok, $"测试构造的对端帧必须合法：{decoded.Code} {decoded.Detail}");
        return text;
    }
}

/// <summary>
/// 确定性对端：按顺序扫描协调器已发出的帧，逐块回 ack、对 file-end 回 import-result，
/// 并记录见过的 batch-end。它只用生产 codec 解读出站帧，不共享协调器的任何实现。
/// 关闭 <see cref="AutoAck"/>/<see cref="AnswerFileEnd"/> 时对应的帧会被<b>留在原地</b>，
/// 重新打开后仍会补答，因此同一个对端可以驱动超时与恢复两种场景。
/// </summary>
internal sealed class AutoPeer
{
    private readonly RecordingChannel _channel;
    private readonly Dictionary<string, HashSet<int>> _acked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private int _scanned;

    public AutoPeer(RecordingChannel channel, string sessionId)
    {
        _channel = channel;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    /// <summary>file-end 之后回报的结果状态（staged/failed/partial）。</summary>
    public string Status { get; set; } = "staged";

    /// <summary>结果报文里的 code（failed/partial 必填）。</summary>
    public string? Code { get; set; } = "draft-import-failed";

    /// <summary>按 fileId 覆盖 <see cref="Status"/>（用于逐文件成功/失败/部分成功的混合批次）。</summary>
    public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>按 fileId 覆盖 <see cref="Code"/>。</summary>
    public Dictionary<string, string> Codes { get; } = new(StringComparer.Ordinal);

    /// <summary>是否为 staged 结果返回新增 ID（默认为是，且恰好 1 个）。</summary>
    public bool ReturnAttachmentId { get; set; } = true;

    /// <summary>是否自动 ack。</summary>
    public bool AutoAck { get; set; } = true;

    /// <summary>是否自动回答 file-end。</summary>
    public bool AnswerFileEnd { get; set; } = true;

    /// <summary>回报的 bufferedBytes。</summary>
    public int BufferedBytes { get; set; }

    /// <summary>见过的 batch-end（顺序）。</summary>
    public List<string> CompletedBatches { get; } = [];

    public void Respond()
    {
        while (_scanned < _channel.Sent.Count)
        {
            var message = _channel.Sent[_scanned];
            switch (message.Kind)
            {
                case WireMessageKind.Chunk when message.FileId is not null:
                    if (!AutoAck) return;
                    Acknowledge(message);
                    break;
                case WireMessageKind.FileEnd when message.FileId is not null:
                    if (!AnswerFileEnd) return;
                    Answer(message);
                    break;
                case WireMessageKind.BatchEnd when message.BatchId is not null:
                    if (_closed.Add(message.BatchId)) CompletedBatches.Add(message.BatchId);
                    break;
                default:
                    break;
            }

            _scanned += 1;
        }
    }

    private void Acknowledge(AttachmentMessage message)
    {
        var fileId = message.FileId!;
        var acked = _acked.TryGetValue(fileId, out var set) ? set : _acked[fileId] = [];
        var seq = message.Seq!.Value;
        if (!acked.Add(seq)) return;
        _channel.Deliver(PeerWire.Ack(
            SessionId,
            message.BatchId!,
            fileId,
            seq,
            message.Offset!.Value,
            message.ByteLength!.Value,
            BufferedBytes));
    }

    private void Answer(AttachmentMessage message)
    {
        var fileId = message.FileId!;
        if (!_answered.Add(fileId)) return;
        var status = Statuses.TryGetValue(fileId, out var specificStatus) ? specificStatus : Status;
        var code = Codes.TryGetValue(fileId, out var specificCode) ? specificCode : Code ?? "draft-import-failed";
        var ids = status is "staged" or "partial" && ReturnAttachmentId ? new[] { $"att-{fileId}" } : [];
        _channel.Deliver(PeerWire.ImportResult(
            SessionId,
            message.BatchId!,
            fileId,
            status,
            ids,
            status == "staged" ? null : code));
    }
}

/// <summary>一个被完整接线的协调器：可控时钟 + 记录通道 + 确定性对端 + trace。</summary>
internal sealed record Harness(
    AttachmentTransferCoordinator Coordinator,
    RecordingChannel Channel,
    AutoPeer Peer,
    ManualClock Clock,
    AttachmentTransferTrace Trace,
    AttachmentConcurrencyGate Gate) : IDisposable
{
    public void Dispose() => Coordinator.Dispose();
}

/// <summary>测试夹具：身份、负载、接线与驱动循环。</summary>
internal static class CoordinatorFixture
{
    public static AttachmentIdentity Identity(string targetId = "target-1", int documentEpoch = 7, int composerEpoch = 3) =>
        new("session-1", targetId, documentEpoch, composerEpoch, "scope-a");

    /// <summary>确定性负载（不依赖随机数，重复运行逐字节一致）。</summary>
    public static byte[] Payload(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index += 1) data[index] = (byte)(index * 31 % 251);
        return data;
    }

    /// <summary>接线一个全新的协调器（默认全局并发上限与默认去重缓存容量）。</summary>
    public static Harness NewHarness(
        string targetId = "target-1",
        AttachmentConcurrencyGate? gate = null,
        int? replayCapacity = null,
        int documentEpoch = 7)
    {
        var clock = new ManualClock();
        var channel = new RecordingChannel();
        var trace = new AttachmentTransferTrace();
        var effectiveGate = gate ?? new AttachmentConcurrencyGate();
        var coordinator = new AttachmentTransferCoordinator(
            Identity(targetId, documentEpoch),
            channel,
            clock,
            gate: effectiveGate,
            trace: trace,
            replayCapacity: replayCapacity);
        return new Harness(coordinator, channel, new AutoPeer(channel, "session-1"), clock, trace, effectiveGate);
    }

    /// <summary>把协调器驱动到终态：pump → 对端应答 → 再 pump，直到批次关闭/取消或达到步数上限。</summary>
    public static void Drive(Harness harness, int maxSteps = 200) => Drive(harness.Coordinator, harness.Peer, maxSteps);

    /// <summary>把协调器驱动到终态：pump → 对端应答 → 再 pump，直到批次关闭/取消或达到步数上限。</summary>
    public static void Drive(AttachmentTransferCoordinator coordinator, AutoPeer peer, int maxSteps = 200)
    {
        for (var step = 0; step < maxSteps; step += 1)
        {
            coordinator.Pump();
            peer.Respond();
            if (coordinator.BatchPhase is AttachmentBatchPhase.Closed or AttachmentBatchPhase.Cancelled) return;
        }
    }
}
