using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D17 组合契约的假通道：只交换封包文本，记录每次发送，并按需注入发送失败。
/// 通道本身不做线协议判定——那正是被测组合层必须做的事。
/// </summary>
internal sealed class FakeBridgeTransport(string channelId) : IRemoteBridgeTransport
{
    private readonly Queue<string> _incoming = new();

    public string ChannelId { get; } = channelId;

    public bool IsClosed { get; private set; }

    public string? LastFailureCode { get; private set; }

    public string? LastFailureDetail { get; private set; }

    /// <summary>按顺序记录的出站封包。</summary>
    public List<string> SentEnvelopes { get; } = [];

    /// <summary>按顺序记录的出站帧 JSON（封包里的 frame）。</summary>
    public List<string> SentFrames { get; } = [];

    /// <summary>Close 的原始调用次数（含重复调用）。</summary>
    public int CloseCalls { get; private set; }

    /// <summary>生效的关闭次数（接口契约：重复 Close 必须幂等）。</summary>
    public int CloseCount { get; private set; }

    /// <summary>注入发送失败（例如 WebView2 尚未初始化）。</summary>
    public bool FailSends { get; set; }

    /// <summary>注入失败时的确定码。</summary>
    public string FailureCode { get; set; } = RemoteBridgeCodes.ChannelUnavailable;

    public RemoteBridgeSendOutcome SendFrame(string envelopeJson)
    {
        if (IsClosed)
        {
            return RemoteBridgeSendOutcome.Fail(RemoteBridgeCodes.ChannelUnavailable, "通道已关闭");
        }

        if (FailSends)
        {
            return RemoteBridgeSendOutcome.Fail(FailureCode, "注入的通道失败");
        }

        SentEnvelopes.Add(envelopeJson);
        SentFrames.Add(DecodeEnvelope(envelopeJson).FrameJson);
        return RemoteBridgeSendOutcome.Ok();
    }

    public bool TryReceiveFrame([NotNullWhen(true)] out string? envelopeJson)
    {
        if (_incoming.Count == 0)
        {
            envelopeJson = null;
            return false;
        }

        envelopeJson = _incoming.Dequeue();
        return true;
    }

    /// <summary>模拟页面投递一条封包。</summary>
    public void Enqueue(string envelopeJson) => _incoming.Enqueue(envelopeJson);

    public void Close()
    {
        CloseCalls += 1;
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        CloseCount += 1;
        _incoming.Clear();
    }

    public void Dispose() => Close();

    /// <summary>取出封包里的帧（测试自己也要复核：出站必须始终是合法封包）。</summary>
    public static RemoteBridgeEnvelope DecodeEnvelope(string envelopeJson)
    {
        var decoded = RemoteBridgeEnvelopeCodec.Decode(envelopeJson);
        if (!decoded.Ok || decoded.Envelope is null)
        {
            throw new InvalidOperationException($"出站封包不合法：{decoded.Code}");
        }

        return decoded.Envelope;
    }
}

/// <summary>
/// D17 暂存端口假实现：捕获票据 → 快照（内存字节），打开快照 → 脚本化字节源，
/// 并记录每次 BeginBatch/Capture/Open/Release，用于逐路径断言"每个退出路径都释放"。
/// </summary>
internal sealed class FakeStagingPort : IRemoteStagingPort
{
    private readonly Dictionary<string, StagingCaptureReceipt> _captures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _snapshots = new(StringComparer.Ordinal);

    public List<string> BeginBatchIds { get; } = [];

    public List<string> CapturedIds { get; } = [];

    public List<string> OpenedSnapshotIds { get; } = [];

    public List<string> ReleasedSnapshotIds { get; } = [];

    /// <summary>注入捕获失败（票据未知之外的确定性失败）。</summary>
    public string? CaptureFailureCode { get; set; }

    /// <summary>注入快照打不开。</summary>
    public bool FailSnapshotOpen { get; set; }

    /// <summary>注入释放失败（仍应记账，桥不得把它当成"已释放"以外的成功）。</summary>
    public bool FailRelease { get; set; }

    public void AddCapture(string captureId, string snapshotId, string displayName, byte[] bytes)
    {
        _captures[captureId] = new StagingCaptureReceipt
        {
            Ok = true,
            SnapshotId = snapshotId,
            DisplayName = displayName,
            ByteLength = bytes.Length,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
        };
        _snapshots[snapshotId] = bytes;
    }

    public AttachmentCoordinatorResult BeginBatch(string batchId, int fileCount)
    {
        BeginBatchIds.Add(batchId);
        return new AttachmentCoordinatorResult { Ok = true, BatchId = batchId };
    }

    public StagingCaptureReceipt Capture(string captureId, CancellationToken cancellationToken = default)
    {
        CapturedIds.Add(captureId);
        if (CaptureFailureCode is { } injected)
        {
            return new StagingCaptureReceipt { Ok = false, Code = injected, Detail = "注入的暂存失败" };
        }

        if (!_captures.TryGetValue(captureId, out var receipt))
        {
            return new StagingCaptureReceipt
            {
                Ok = false,
                Code = AttachmentStagingCodes.CaptureIdUnknown,
                Detail = "捕获 id 未登记",
            };
        }

        if (!_consumed.Add(captureId))
        {
            return new StagingCaptureReceipt
            {
                Ok = false,
                Code = AttachmentStagingCodes.CaptureConsumed,
                Detail = "捕获票据已被消费",
            };
        }

        return receipt;
    }

    public IAttachmentByteSource OpenSnapshot(string snapshotId)
    {
        OpenedSnapshotIds.Add(snapshotId);
        if (FailSnapshotOpen || !_snapshots.TryGetValue(snapshotId, out var bytes))
        {
            throw new AttachmentStagingException(AttachmentStagingCodes.SnapshotUnknown, "快照不存在");
        }

        return new ScriptedByteSource(bytes);
    }

    public StagingCleanupResult Release(string snapshotId, CancellationToken cancellationToken = default)
    {
        ReleasedSnapshotIds.Add(snapshotId);
        if (FailRelease)
        {
            return new StagingCleanupResult
            {
                Ok = false,
                Code = AttachmentStagingCodes.CleanupFailed,
                Detail = "注入的释放失败",
                SnapshotId = snapshotId,
            };
        }

        _snapshots.Remove(snapshotId);
        return new StagingCleanupResult { Ok = true, SnapshotId = snapshotId, RemovedCount = 1 };
    }
}

/// <summary>
/// D17 握手消费者：直接写进 <b>D16 的真实台账</b>（截图所有者登记表 + composer 上下文台账），
/// 因此"路由到正确消费者"是可断言的：测试随后用真实 <see cref="NativePasteOrchestrator"/>
/// 观察位图粘贴的路由变化。
/// </summary>
internal sealed class RecordingHandshakeSink : IRemoteBridgeHandshakeSink
{
    public ScreenshotOwnerRegistry Owners { get; } = new();

    public AttachmentComposerContextStore Contexts { get; } = new();

    public List<ScreenshotOwnerFacts> ScreenshotCalls { get; } = [];

    public List<ImportContextFacts> ContextCalls { get; } = [];

    public ScreenshotOwnerDecision RecordScreenshotOwner(
        Guid targetId,
        RemotePageEpoch epoch,
        ScreenshotOwnerFacts facts)
    {
        ScreenshotCalls.Add(facts);
        return Owners.Record(targetId, epoch, facts);
    }

    public bool RecordComposerContext(
        Guid targetId,
        RemotePageEpoch epoch,
        ImportContextFacts facts,
        bool pageAdmitted)
    {
        ContextCalls.Add(facts);
        return Contexts.Record(targetId, epoch, facts, pageAdmitted);
    }
}

/// <summary>
/// D17 组合契约夹具：一个真实 <see cref="RemotePageSessionState"/> + 假通道 + 假暂存 + 真实台账，
/// 以及"页面侧"的最小线协议构造器（帧仍由真实 codec 校验）。
/// </summary>
internal sealed class RemoteBridgeRig : IDisposable
{
    public const string PageUrl = "https://harness.local/remote";

    public RemoteBridgeRig(
        bool nativeCaptureAvailable = true,
        AttachmentLimits? limits = null,
        bool attachTransport = true)
    {
        Page = new RemotePageSessionState(RemotePageChannelId.New(), PageUrl);
        TargetId = Guid.NewGuid();
        Transport = new FakeBridgeTransport(Page.ChannelId);
        Staging = new FakeStagingPort();
        Sink = new RecordingHandshakeSink();
        Clock = new ManualClock();
        Composition = new RemoteBridgeComposition(
            TargetId,
            Page,
            Staging,
            Sink,
            new RemoteBridgeOptions
            {
                ClientBuild = "test-host/1",
                NativeCaptureAvailable = nativeCaptureAvailable,
                Limits = limits ?? new AttachmentLimits(),
            },
            new AttachmentConcurrencyGate(),
            Clock);
        if (attachTransport)
        {
            Composition.AttachTransport(Transport);
        }
    }

    public RemotePageSessionState Page { get; }

    public Guid TargetId { get; }

    public FakeBridgeTransport Transport { get; }

    public FakeStagingPort Staging { get; }

    public RecordingHandshakeSink Sink { get; }

    public ManualClock Clock { get; }

    public RemoteBridgeComposition Composition { get; }

    /// <summary>完成一次主文档加载并授予能力（D15 规则）。</summary>
    public void LoadDocument()
    {
        Page.BeginNavigation(PageUrl, RemoteNavigationKind.MainDocument);
        var capability = Page.CompleteMainDocumentLoad(PageUrl);
        if (!capability.Granted)
        {
            throw new InvalidOperationException("夹具未能授予页面能力");
        }
    }

    /// <summary>从某个下标开始的出站帧（跳过握手帧）。</summary>
    public IReadOnlyList<string> FramesSince(int index) => [.. Transport.SentFrames.Skip(index)];

    /// <summary>页面侧封包：通道与代际取自页面会话（错代际可显式指定）。</summary>
    public string Envelope(string frameJson, int? epoch = null)
    {
        var frame = JsonNode.Parse(frameJson) as JsonObject
            ?? throw new InvalidOperationException("帧必须是 JSON 对象");
        var wireEpoch = epoch
            ?? (RemoteBridgeEnvelopeCodec.TryToWireEpoch(Page.Epoch, out var current) ? current : 0);
        return RemoteBridgeEnvelopeCodec.Encode(Page.ChannelId, wireEpoch, frame);
    }

    public static string PageFrame(string type, JsonObject fields)
    {
        fields["v"] = AttachmentProtocol.Version;
        fields["type"] = type;
        // 冻结字段顺序：v、type 在最前（JsonObject 保持插入顺序，故重建一次）。
        var ordered = new JsonObject { ["v"] = AttachmentProtocol.Version, ["type"] = type };
        foreach (var pair in fields)
        {
            if (pair.Key is "v" or "type")
            {
                continue;
            }

            ordered[pair.Key] = pair.Value?.DeepClone();
        }

        return ordered.ToJsonString();
    }

    public RemoteBridgeInboundResult Deliver(string envelopeJson) =>
        Composition.DeliverInbound(envelopeJson);

    public RemoteBridgeInboundResult DeliverPageFrame(string type, JsonObject fields, int? epoch = null) =>
        Deliver(Envelope(PageFrame(type, fields), epoch));

    /// <summary>宿主完成一次握手：宿主 hello+capabilities，页面 hello+capabilities+context。</summary>
    public void Handshake(bool screenshotFeature = false, string sessionId = "session-1", int composerEpoch = 3)
    {
        LoadDocument();
        Composition.StartHandshake();
        DeliverPageFrame("hello", new JsonObject { ["clientBuild"] = "addon/0.1.0" });
        DeliverPageFrame("capabilities", CapabilitiesFields(screenshotFeature));
        DeliverPageFrame("context", ContextFields(sessionId, composerEpoch));
    }

    public static JsonObject CapabilitiesFields(bool screenshotFeature = false, int maxFileBytes = AttachmentProtocol.MaxFileBytes)
    {
        var features = new JsonArray();
        foreach (var feature in AttachmentProtocol.WireFeatures)
        {
            if (!screenshotFeature && feature == "screenshot")
            {
                continue;
            }

            features.Add(feature);
        }

        return new JsonObject
        {
            ["features"] = features,
            ["limits"] = new JsonObject
            {
                ["maxFileBytes"] = maxFileBytes,
                ["maxFilesPerBatch"] = AttachmentProtocol.MaxFilesPerBatch,
                ["maxBatchBytes"] = AttachmentProtocol.MaxBatchBytes,
                ["maxScreenshotPixels"] = AttachmentProtocol.MaxScreenshotPixels,
                ["maxStagingBytesPerTarget"] = AttachmentProtocol.MaxStagingBytesPerTarget,
                ["maxConcurrentTargets"] = AttachmentProtocol.MaxConcurrentTargets,
            },
        };
    }

    public static JsonObject ContextFields(string sessionId = "session-1", int composerEpoch = 3) => new()
    {
        ["sessionId"] = sessionId,
        ["targetId"] = "page-target-1",
        ["documentEpoch"] = 1,
        ["composerEpoch"] = composerEpoch,
        ["composerScope"] = "scope-1",
    };

    /// <summary>
    /// 页面侧的最小应答器：把宿主新发出的 chunk/file-end 翻译成 ack/import-result/batch-end
    /// 并投回宿主。帧仍由真实 codec 生成与校验。
    /// </summary>
    public int AutoRespond(int attachmentCount = 1, string importStatus = "staged", string? importCode = null)
    {
        var replies = new List<string>();
        for (var index = _responded; index < Transport.SentFrames.Count; index += 1)
        {
            var decoded = AttachmentCodec.Decode(Transport.SentFrames[index]);
            if (!decoded.Ok)
            {
                throw new InvalidOperationException($"宿主发出非法帧：{decoded.Code}");
            }

            var message = decoded.Message!;
            switch (message.Kind)
            {
                case WireMessageKind.Chunk:
                    replies.Add(PageFrame("ack", new JsonObject
                    {
                        ["sessionId"] = message.SessionId,
                        ["batchId"] = message.BatchId,
                        ["fileId"] = message.FileId,
                        ["seq"] = message.Seq,
                        ["offset"] = message.Offset,
                        ["byteLength"] = message.ByteLength,
                        ["bufferedBytes"] = message.Offset + message.ByteLength,
                        ["inFlight"] = 0,
                    }));
                    break;
                case WireMessageKind.FileEnd:
                    var ids = new JsonArray();
                    for (var id = 0; id < attachmentCount; id += 1)
                    {
                        ids.Add($"att-{message.BatchId}-{message.FileId}-{id}");
                    }

                    var result = new JsonObject
                    {
                        ["sessionId"] = message.SessionId,
                        ["batchId"] = message.BatchId,
                        ["fileId"] = message.FileId,
                        ["status"] = importStatus,
                        ["attachmentIds"] = importStatus == "staged" ? ids : new JsonArray(),
                    };
                    if (importCode is not null)
                    {
                        result["code"] = importCode;
                    }

                    replies.Add(PageFrame("import-result", result));
                    break;
                default:
                    break;
            }
        }

        _responded = Transport.SentFrames.Count;
        foreach (var reply in replies)
        {
            Transport.Enqueue(Envelope(reply));
        }

        return Composition.DrainTransport();
    }

    /// <summary>
    /// 反复"页面应答 → 宿主推进"，直到批次进入终态或没有新帧可答。
    /// 每次应答都走真实 codec 构造，因此这是一次真实的双向线协议往返，不是把状态机抄一遍。
    /// </summary>
    public int Settle(int maxRounds = 64, string importStatus = "staged", string? importCode = null)
    {
        var rounds = 0;
        while (rounds < maxRounds)
        {
            rounds += 1;
            if (Composition.Coordinator is not { } coordinator
                || coordinator.BatchPhase != AttachmentBatchPhase.Open
                || Transport.SentFrames.Count <= _responded)
            {
                break;
            }

            AutoRespond(importStatus: importStatus, importCode: importCode);
            Composition.Pump();
        }

        return rounds;
    }

    private int _responded;

    public void Dispose() => Composition.Dispose();
}
