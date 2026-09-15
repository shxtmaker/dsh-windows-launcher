using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D17 通道适配契约（Linux 真实执行）：封包编解码、出站身份/大小复核、
/// 入站准入与身份复核、第二个消费者的拒绝、关闭释放与失败上报。
/// 组合层不重写状态机：帧的判定全部仍由生产 codec 与 D10/D11 完成。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemoteBridgeChannelTests
{
    [Fact]
    public void TheEnvelopeCarriesTheFrozenWireFrameUnchanged()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();
        rig.Composition.StartHandshake();

        var envelope = FakeBridgeTransport.DecodeEnvelope(rig.Transport.SentEnvelopes[0]);
        var decoded = AttachmentCodec.Decode(envelope.FrameJson);

        Assert.True(decoded.Ok);
        Assert.Equal(WireMessageKind.Hello, decoded.Message!.Kind);
        Assert.Equal("test-host/1", decoded.Message.ClientBuild);
        Assert.Equal(rig.Page.ChannelId, envelope.ChannelId);
        Assert.Equal(rig.Page.Epoch.Value, envelope.Epoch);
        Assert.Equal(2, rig.Transport.SentFrames.Count);
        Assert.Equal(
            WireMessageKind.Capabilities,
            AttachmentCodec.Decode(rig.Transport.SentFrames[1]).Message!.Kind);
    }

    [Fact]
    public void APageFrameThatIsNotAJsonObjectIsRefusedAsMalformed()
    {
        using var rig = new RemoteBridgeRig();

        var result = rig.Composition.DeliverInbound("\"just a string\"");

        Assert.False(result.Accepted);
        Assert.Equal("malformed-json", result.Code);
        Assert.Empty(rig.Transport.SentFrames);
    }

    [Fact]
    public void AnEnvelopeWithAnUnknownFieldIsRefused()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["channel"] = rig.Page.ChannelId,
            ["epoch"] = 1,
            ["frame"] = new JsonObject { ["v"] = 1, ["type"] = "hello", ["clientBuild"] = "addon/0.1.0" },
            ["extra"] = true,
        }.ToJsonString();

        var result = rig.Composition.DeliverInbound(envelope);

        Assert.False(result.Accepted);
        Assert.Equal("unknown-field", result.Code);
    }

    [Fact]
    public void AnEnvelopeForAnotherPagesChannelIsRefusedWithTheChannelMismatchCode()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        var envelope = RemoteBridgeEnvelopeCodec.Encode(
            RemotePageChannelId.New(),
            1,
            JsonNode.Parse(RemoteBridgeRig.PageFrame("hello", new JsonObject { ["clientBuild"] = "addon/0.1.0" }))!.AsObject());

        var result = rig.Composition.DeliverInbound(envelope);

        Assert.False(result.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageChannelMismatch, result.Code);
        Assert.Equal(1, rig.Composition.RejectedInbound);
    }

    [Fact]
    public void AFollowerFrameFromAStaleEpochIsRefusedWithTheEpochStaleCode()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        var staleEpoch = (int)rig.Page.Epoch.Value;
        var frame = RemoteBridgeRig.PageFrame("ack", new JsonObject
        {
            ["sessionId"] = "session-1",
            ["batchId"] = "batch-1",
            ["fileId"] = "file-1",
            ["seq"] = 0,
            ["offset"] = 0,
            ["byteLength"] = 1,
            ["bufferedBytes"] = 1,
            ["inFlight"] = 0,
        });

        // 页面导航：代际推进，旧封包（旧代际）必须被确定拒绝而不是被忽略。
        rig.Page.BeginNavigation(RemoteBridgeRig.PageUrl, RemoteNavigationKind.MainDocument);
        rig.Composition.OnPageAdvanced();

        var result = rig.Deliver(rig.Envelope(frame, staleEpoch));

        Assert.False(result.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale, result.Code);
    }

    [Fact]
    public void AnOversizedEnvelopeIsRefusedBeforeItIsParsedOrQueued()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        var padding = new string('a', RemoteBridgeEnvelopeCodec.MaxEnvelopeBytes + 16);
        var envelope = RemoteBridgeEnvelopeCodec.Encode(
            rig.Page.ChannelId,
            1,
            new JsonObject { ["v"] = 1, ["type"] = "hello", ["clientBuild"] = padding[..64], ["junk"] = padding });

        var result = rig.Composition.DeliverInbound(envelope);

        Assert.False(result.Accepted);
        Assert.Equal("message-too-large", result.Code);
    }

    [Fact]
    public void AnOutboundFrameWhoseIdentityDoesNotMatchIsRefusedAndNeverReachesTheChannel()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        var sentBefore = rig.Transport.SentEnvelopes.Count;

        var result = rig.Composition.SendHostFrame(new JsonObject
        {
            ["v"] = 1,
            ["type"] = "batch-begin",
            ["sessionId"] = "another-session",
            ["batchId"] = "batch-x",
            ["targetId"] = "page-target-1",
            ["documentEpoch"] = 1,
            ["composerEpoch"] = 3,
            ["fileCount"] = 1,
            ["totalBytes"] = 1,
        }.ToJsonString());

        Assert.False(result.Sent);
        Assert.Equal("context-changed", result.Code);
        Assert.Equal(sentBefore, rig.Transport.SentEnvelopes.Count);
        Assert.Equal(1, rig.Composition.RejectedOutbound);
    }

    [Fact]
    public void AnOutboundFrameAboveTheFrozenMessageLimitIsRefusedBeforeItReachesTheChannel()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        var sentBefore = rig.Transport.SentEnvelopes.Count;

        var result = rig.Composition.SendHostFrame(
            new JsonObject { ["v"] = 1, ["type"] = "hello", ["clientBuild"] = new string('a', AttachmentProtocol.MaxMessageBytes + 64) }
                .ToJsonString());

        Assert.False(result.Sent);
        Assert.Equal("message-too-large", result.Code);
        Assert.Equal(sentBefore, rig.Transport.SentEnvelopes.Count);
    }

    [Fact]
    public void ASecondTransportCannotBeAttachedToTheSamePageBridge()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();

        var second = new FakeBridgeTransport(rig.Page.ChannelId);
        var attached = rig.Composition.AttachTransport(second);

        Assert.False(attached);
        Assert.Equal(RemoteBridgeCodes.AlreadyAttached, rig.Composition.Rejections[^1].Code);
        Assert.Same(rig.Transport, rig.Composition.Transport);
        Assert.Equal(0, second.CloseCount);
    }

    [Fact]
    public void ATransportForAnotherChannelIsRefused()
    {
        using var rig = new RemoteBridgeRig(attachTransport: false);
        var foreign = new FakeBridgeTransport(RemotePageChannelId.New());

        Assert.False(rig.Composition.AttachTransport(foreign));
        Assert.Equal(RemotePageSessionCodes.MessageChannelMismatch, rig.Composition.Rejections[^1].Code);
        Assert.False(rig.Composition.IsAttached);
        Assert.False(foreign.IsClosed);
    }

    [Fact]
    public void AClosedBridgeRejectsFurtherFramesWithTheDeterminateCloseCode()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();

        rig.Composition.Close("session-closed");
        var result = rig.DeliverPageFrame("hello", new JsonObject { ["clientBuild"] = "addon/0.1.0" });

        Assert.Equal(RemoteBridgePhase.Closed, rig.Composition.Phase);
        Assert.False(result.Accepted);
        Assert.Equal("session-closed", result.Code);
        Assert.True(rig.Transport.IsClosed);
        Assert.Equal(1, rig.Transport.CloseCount);
    }

    [Fact]
    public void ClosingTheBridgeIsIdempotentAndReleasesTheListenerOnce()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();

        rig.Composition.Close("session-closed");
        rig.Composition.Close("session-closed");
        rig.Composition.Dispose();

        Assert.Equal(1, rig.Transport.CloseCount);
        Assert.True(rig.Transport.CloseCalls >= 2);
        Assert.Equal(RemoteBridgePhase.Closed, rig.Composition.Phase);
    }

    [Fact]
    public void AnInboundQueueOverflowFailsClosedInsteadOfGrowingWithoutBound()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        var ack = RemoteBridgeRig.PageFrame("ack", new JsonObject
        {
            ["sessionId"] = "session-1",
            ["batchId"] = "batch-none",
            ["fileId"] = "file-none",
            ["seq"] = 0,
            ["offset"] = 0,
            ["byteLength"] = 1,
            ["bufferedBytes"] = 1,
            ["inFlight"] = 0,
        });

        // drivePump:false = 只入队不驱动：页面可以在一次 UI 事件里灌多条消息，
        // 队列必须有硬上界，溢出即 fail-closed（生产路径由通道适配器的队列上界先行兜住）。
        for (var index = 0; index < RemoteBridgeComposition.MaxInboundQueue; index += 1)
        {
            var accepted = rig.Composition.DeliverInbound(rig.Envelope(ack), drivePump: false);
            Assert.True(accepted.Accepted);
        }

        var overflow = rig.Composition.DeliverInbound(rig.Envelope(ack), drivePump: false);

        Assert.False(overflow.Accepted);
        Assert.Equal(RemoteBridgeCodes.InboundOverflow, overflow.Code);
        Assert.Equal(RemoteBridgePhase.Failed, rig.Composition.Phase);
        Assert.Equal(RemoteBridgeCodes.InboundOverflow, rig.Composition.FailureCode);
        Assert.True(rig.Transport.IsClosed);
    }

    [Fact]
    public void AFailedChannelSendFailsTheBridgeClosedAndReleasesEverything()
    {
        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Encoding.UTF8.GetBytes("hello"));
        rig.Transport.FailSends = true;

        var batch = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-fail", ["capture-1"]));

        // 协调器以为帧发出去了，但通道拒绝：桥必须 fail-closed（阶段 Failed、快照释放、监听解除），
        // 绝不把"没发出去"当成成功继续跑。
        Assert.False(batch.Ok);
        Assert.Equal(RemoteBridgeCodes.ChannelUnavailable, batch.Code);
        Assert.Equal(RemoteBridgePhase.Failed, rig.Composition.Phase);
        Assert.Equal(RemoteBridgeCodes.ChannelUnavailable, rig.Composition.FailureCode);
        Assert.Contains("snapshot-1", rig.Staging.ReleasedSnapshotIds);
        Assert.Contains("snapshot-1", rig.Composition.ReleasedSnapshots);
        Assert.True(rig.Transport.IsClosed);
        Assert.Null(rig.Composition.Coordinator);
    }
}
