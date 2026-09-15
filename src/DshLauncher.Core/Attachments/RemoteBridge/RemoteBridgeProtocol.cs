using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// D17 组合层的<b>本地</b>判定码。与 <see cref="AttachmentStagingCodes"/>、<see cref="NativePasteCodes"/>
/// 同一约定：只出现在本机结果、trace 与测试断言里，<b>绝不</b>写进任何线协议报文；
/// 与冻结线协议码语义重合的失败（<c>message-too-large</c>、<c>context-changed</c>、
/// <c>no-session</c>、<c>cancelled</c>、<c>batch-closed</c> 等）直接复用冻结码，不另造同义码。
/// </summary>
public static class RemoteBridgeCodes
{
    /// <summary>页面通道适配器已关闭或不可用（WebView2 尚未初始化 / 已释放 / 投递被拒）。</summary>
    public const string ChannelUnavailable = "bridge-channel-unavailable";

    /// <summary>尚未把 WebView2 通道接到本桥（页面还没初始化完成）。</summary>
    public const string NotAttached = "bridge-not-attached";

    /// <summary>同一桥只接受一个通道适配器：第二个消费者一律拒绝。</summary>
    public const string AlreadyAttached = "bridge-already-attached";

    /// <summary>握手尚未完成（没有 capabilities / context），不能开始传输。</summary>
    public const string NotReady = "bridge-not-ready";

    /// <summary>桥已因确定失败关闭（详见 <see cref="RemoteBridgeComposition.FailureCode"/>）。</summary>
    public const string Failed = "bridge-failed";

    /// <summary>握手次序不完整（例如没有 capabilities 就来了 context）。</summary>
    public const string HandshakeIncomplete = "bridge-handshake-incomplete";

    /// <summary>同一页面代际内出现第二个不同身份（换会话/换 composer）：一律拒绝，不换绑。</summary>
    public const string SecondContext = "bridge-second-context";

    /// <summary>入站队列已满：不再接收，避免页面把宿主内存撑爆（fail-closed）。</summary>
    public const string InboundOverflow = "bridge-inbound-overflow";

    /// <summary>原生手势没有产生任何捕获票据。</summary>
    public const string NoCapture = "bridge-no-capture";

    /// <summary>暂存快照的字节源打不开（自有根内文件缺失/被换）。</summary>
    public const string SnapshotUnavailable = "bridge-snapshot-unavailable";

    /// <summary>桥没有拿到可用的全局并发槽位（保持 pending，不读字节）。</summary>
    public const string QueueStarved = "bridge-queue-starved";

    /// <summary>封包结构不合法（不是对象、缺字段、字段过多）。</summary>
    public const string EnvelopeInvalid = "bridge-envelope-invalid";

    /// <summary>桥自身被释放（Dispose）：与"页面会话关闭"区分开，便于诊断。</summary>
    public const string Disposed = "bridge-disposed";
}

/// <summary>
/// D17 的 WebMessage 封包：一条封包只承载<b>一条</b>冻结线协议帧，并额外带上页面通道与页面代际。
///
/// <code>{"v":1,"channel":"page-…","epoch":7,"frame":{"v":1,"type":"chunk",…}}</code>
///
/// 为什么要封包而不是裸发线协议帧：冻结协议本身没有"这条消息属于哪个页面/哪个页面代际"的字段，
/// 而 WebView2 的 <c>WebMessageReceived</c> 不保证来源窗口，因此"每次收发再次检查身份"必须有一个
/// 宿主可控的载体。封包只加这三个字段，线协议字段集一字不改（帧仍由生产 codec 判定）。
/// </summary>
public sealed record RemoteBridgeEnvelope(string ChannelId, int Epoch, JsonObject Frame)
{
    /// <summary>帧的 JSON 文本（交给生产 codec 的解码入口，不在事件处理器里另写字段判定）。</summary>
    public string FrameJson => Frame.ToJsonString();
}

/// <summary>封包编解码结果。</summary>
public sealed record RemoteBridgeEnvelopeResult
{
    /// <summary>是否通过全部结构校验。</summary>
    public required bool Ok { get; init; }

    /// <summary>成功时的封包。</summary>
    public RemoteBridgeEnvelope? Envelope { get; init; }

    /// <summary>失败时的确定码（冻结线协议码或 <see cref="RemoteBridgeCodes"/>）。</summary>
    public string? Code { get; init; }

    /// <summary>失败说明。</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// D17 封包编解码（平台中立、无 P/Invoke、无状态）。判定顺序固定：
/// 文本形态归一 → 字节上界 → JSON 结构 → 版本 → 字段名集合 → 字段类型/范围 → 帧字节上界。
/// 任何一步失败都给出确定码，绝不"看着像就收下"。
/// </summary>
public static class RemoteBridgeEnvelopeCodec
{
    /// <summary>封包版本。</summary>
    public const int Version = 1;

    /// <summary>单条封包的 UTF-8 字节上界：冻结消息上界 + 封包字段余量。</summary>
    public const int MaxEnvelopeBytes = AttachmentProtocol.MaxMessageBytes + 1024;

    /// <summary>封包 envelope 字段名集合（多一个字段即拒绝）。</summary>
    public static readonly string[] FieldNames = ["v", "channel", "epoch", "frame"];

    /// <summary>把一条封包编码为 JSON 文本（字段顺序固定）。</summary>
    public static string Encode(string channelId, int epoch, JsonObject frame)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(frame);
        return new JsonObject
        {
            ["v"] = Version,
            ["channel"] = channelId,
            ["epoch"] = epoch,
            ["frame"] = frame.DeepClone(),
        }.ToJsonString();
    }

    /// <summary>
    /// 归一入站文本：接受"对象"形态，也接受"JSON 字符串里再放一条封包"的形态
    /// （页面用 <c>postMessage(JSON.stringify(envelope))</c> 时 WebView2 给出的是后一种）。
    /// 无法归一返回 <c>null</c>。
    /// </summary>
    public static string? NormalizePayload(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed[0] != '"')
        {
            return trimmed;
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解码并逐项校验一条封包；帧的内部字段交给生产 codec（本方法不重写状态机）。</summary>
    public static RemoteBridgeEnvelopeResult Decode(string? payload)
    {
        var normalized = NormalizePayload(payload);
        if (normalized is null)
        {
            return Fail("malformed-json", "封包文本为空或不是合法 JSON");
        }

        if (Encoding.UTF8.GetByteCount(normalized) > MaxEnvelopeBytes)
        {
            return Fail(
                "message-too-large",
                $"封包 {Encoding.UTF8.GetByteCount(normalized)} 字节 > {MaxEnvelopeBytes}");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(normalized);
        }
        catch (JsonException error)
        {
            return Fail("malformed-json", error.Message);
        }

        if (root is not JsonObject envelope)
        {
            return Fail(EnvelopeInvalidCode, "封包根节点必须是对象");
        }

        foreach (var pair in envelope)
        {
            if (!FieldNames.Contains(pair.Key, StringComparer.Ordinal))
            {
                return Fail("unknown-field", $"封包出现未知字段 {pair.Key}");
            }
        }

        if (!envelope.TryGetPropertyValue("v", out var versionNode))
        {
            return Fail("missing-field", "封包缺少 v");
        }

        if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
        {
            return Fail("invalid-field-type", "封包 v 必须是整数");
        }

        if (version != Version)
        {
            return Fail("version-mismatch", $"封包版本必须为 {Version}");
        }

        if (!envelope.TryGetPropertyValue("channel", out var channelNode))
        {
            return Fail("missing-field", "封包缺少 channel");
        }

        if (channelNode is not JsonValue channelValue || !channelValue.TryGetValue<string>(out var channel))
        {
            return Fail("invalid-field-type", "封包 channel 必须是字符串");
        }

        if (channel.Length is < 1 or > AttachmentProtocol.MaxIdChars
            || !AttachmentCodec.IsIdShaped(channel))
        {
            return Fail("invalid-field-value", "封包 channel 不是合法的通道 id");
        }

        if (!envelope.TryGetPropertyValue("epoch", out var epochNode))
        {
            return Fail("missing-field", "封包缺少 epoch");
        }

        if (epochNode is not JsonValue epochValue || !epochValue.TryGetValue<int>(out var epoch))
        {
            return Fail("invalid-field-type", "封包 epoch 必须是整数");
        }

        if (epoch < 0)
        {
            return Fail("negative-integer", "封包 epoch 不得为负");
        }

        if (epoch > AttachmentProtocol.MaxEpoch)
        {
            return Fail("integer-out-of-range", $"封包 epoch 超出 int32 表示范围");
        }

        if (!envelope.TryGetPropertyValue("frame", out var frameNode))
        {
            return Fail("missing-field", "封包缺少 frame");
        }

        if (frameNode is not JsonObject frame)
        {
            return Fail("invalid-field-type", "封包 frame 必须是对象");
        }

        var frameJson = frame.ToJsonString();
        if (Encoding.UTF8.GetByteCount(frameJson) > AttachmentProtocol.MaxMessageBytes)
        {
            return Fail(
                "message-too-large",
                $"封包内帧 {Encoding.UTF8.GetByteCount(frameJson)} 字节 > {AttachmentProtocol.MaxMessageBytes}");
        }

        return new RemoteBridgeEnvelopeResult
        {
            Ok = true,
            Envelope = new RemoteBridgeEnvelope(channel, epoch, frame),
        };
    }

    /// <summary>页面代际到封包整数字段的映射；超出 int32 时拒绝（不截断、不猜测）。</summary>
    public static bool TryToWireEpoch(RemotePageEpoch epoch, out int wireEpoch)
    {
        if (epoch.Value < 0 || epoch.Value > AttachmentProtocol.MaxEpoch)
        {
            wireEpoch = 0;
            return false;
        }

        wireEpoch = (int)epoch.Value;
        return true;
    }

    private const string EnvelopeInvalidCode = RemoteBridgeCodes.EnvelopeInvalid;

    private static RemoteBridgeEnvelopeResult Fail(string code, string detail) =>
        new() { Ok = false, Code = code, Detail = detail };
}
