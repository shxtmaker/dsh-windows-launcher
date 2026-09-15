using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D17 组合链路（Linux 真实执行）：原生捕获票据 → D14 暂存快照 → D11 生产协调器 →
/// 封包 → 通道；页面侧的 ACK/import-result/batch-end 再回到同一个协调器。
/// 同时逐条断言取消、迟到、错误与释放语义（每个退出路径都必须擦干净暂存与监听）。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemoteBridgeTransferTests
{
    private static readonly byte[] Payload = BuildPayload(700 * 1024);

    [Fact]
    public void ANativeCaptureBecomesARealChunkedBatchThatThePageAcknowledges()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "报告.txt", Payload);
        var baseline = rig.Transport.SentFrames.Count;

        var batch = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        Assert.True(batch.Ok);
        Assert.Equal(1, batch.FileCount);
        Assert.Equal(Payload.Length, batch.TotalBytes);
        Assert.Equal(["batch-1-0"], batch.FileIds);
        Assert.Equal(["snapshot-1"], batch.SnapshotIds);
        Assert.Contains("capture-1", rig.Staging.CapturedIds);
        Assert.Contains("snapshot-1", rig.Staging.OpenedSnapshotIds);

        rig.Settle();

        var types = rig.FramesSince(baseline)
            .Select(frame => AttachmentCodec.Decode(frame).Message!.Type)
            .ToArray();
        Assert.Equal("batch-begin", types[0]);
        Assert.Equal("file-begin", types[1]);
        Assert.Equal("chunk", types[2]);
        Assert.Contains("file-end", types);
        Assert.Equal("batch-end", types[^1]);

        // 逐字节证据：把页面收到的块按 offset 拼回去，SHA-256 必须等于快照。
        var reassembled = ReassembleChunks(rig, "batch-1-0", baseline);
        Assert.Equal(Payload, reassembled);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Payload)),
            Convert.ToHexStringLower(SHA256.HashData(reassembled)));

        var coordinator = rig.Composition.Coordinator!;
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(1, coordinator.ImportInvocations);
        Assert.Equal(AttachmentFilePhase.Staged, coordinator.FileRecord("batch-1-0")!.Phase);
        Assert.Single(coordinator.FileRecord("batch-1-0")!.AttachmentIds);
    }

    [Fact]
    public void TheFileBeginDeclaresTheStagedLeafNameAndMimeWithoutAnyLocalPath()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "截图.png", Payload);

        var baseline = rig.Transport.SentFrames.Count;
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        var fileBegin = AttachmentCodec.Decode(rig.FramesSince(baseline)[1]).Message!;
        Assert.Equal("截图.png", fileBegin.Name);
        Assert.Equal("image/png", fileBegin.Mime);
        Assert.Equal(Payload.Length, fileBegin.ByteLength);
        var leafName = fileBegin.Name!;
        Assert.False(leafName.Contains('\\', StringComparison.Ordinal));
        Assert.False(leafName.Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public void TheBatchReleasesTheStagingScratchOnceThePageClosesIt()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);

        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));
        rig.Settle();

        Assert.Equal(["snapshot-1"], rig.Staging.ReleasedSnapshotIds);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);
        Assert.Empty(rig.Staging.OpenedSnapshotIds.Except(rig.Staging.ReleasedSnapshotIds));
    }

    [Fact]
    public void AnImportFailureKeepsItsDeterminateCodeAndStillReleases()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        // 页面自己报确定的导入失败：宿主如实记录，不把它当成 staged。
        rig.Settle(importStatus: "failed", importCode: "draft-import-failed");

        var record = rig.Composition.Coordinator!.FileRecord("batch-1-0")!;
        Assert.Equal(AttachmentFilePhase.Failed, record.Phase);
        Assert.Equal("draft-import-failed", record.Code);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);
    }

    [Fact]
    public void ACancelStopsInFlightWorkReleasesScratchAndRefusesLateResponses()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        var cancelled = rig.Composition.CancelBatch("cancelled", "protocol-transfer");

        Assert.True(cancelled.Ok);
        Assert.Equal(AttachmentBatchPhase.Cancelled, rig.Composition.Coordinator!.BatchPhase);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);
        Assert.Contains(
            rig.Transport.SentFrames,
            frame => AttachmentCodec.Decode(frame).Message!.Kind == WireMessageKind.Cancel);

        // 迟到 ACK：必须拿到确定的拒绝码，而不是被悄悄忽略。
        var late = rig.DeliverPageFrame("ack", new JsonObject
        {
            ["sessionId"] = "session-1",
            ["batchId"] = "batch-1",
            ["fileId"] = "batch-1-0",
            ["seq"] = 0,
            ["offset"] = 0,
            ["byteLength"] = AttachmentProtocol.ChunkBytes,
            ["bufferedBytes"] = AttachmentProtocol.ChunkBytes,
            ["inFlight"] = 0,
        });

        Assert.False(late.Accepted);
        Assert.Equal("cancelled", late.Code);
    }

    [Fact]
    public void NavigationDropsTheGenerationReleasesEverythingAndRequiresANewHandshake()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));
        var staleEpoch = (int)rig.Page.Epoch.Value;

        rig.Page.BeginNavigation(RemoteBridgeRig.PageUrl, RemoteNavigationKind.MainDocument);
        rig.Composition.OnPageAdvanced();

        Assert.Equal(RemoteBridgePhase.Detached, rig.Composition.Phase);
        Assert.Null(rig.Composition.Identity);
        Assert.Null(rig.Composition.Coordinator);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);

        var late = rig.DeliverPageFrame(
            "import-result",
            new JsonObject
            {
                ["sessionId"] = "session-1",
                ["batchId"] = "batch-1",
                ["fileId"] = "batch-1-0",
                ["status"] = "staged",
                ["attachmentIds"] = new JsonArray("att-1"),
            },
            staleEpoch);
        Assert.False(late.Accepted);
        Assert.Equal(RemotePageSessionCodes.MessageEpochStale, late.Code);

        // 新代际必须先重新握手才能再传：不能凭一个迟到的 file-end/import-result 复活操作。
        rig.LoadDocument();
        var refused = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-2", ["capture-1"]));
        Assert.False(refused.Ok);
        Assert.Equal(RemoteBridgeCodes.NotReady, refused.Code);
    }

    [Fact]
    public void ATargetRemovalClosesTheBridgeAndRejectsEveryLateFrameWithSessionClosed()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));
        var epoch = (int)rig.Page.Epoch.Value;

        rig.Page.Close(RemotePageCancellationTrigger.TargetRemoved);
        rig.Composition.Close("session-closed");

        var late = rig.DeliverPageFrame(
            "import-result",
            new JsonObject
            {
                ["sessionId"] = "session-1",
                ["batchId"] = "batch-1",
                ["fileId"] = "batch-1-0",
                ["status"] = "staged",
                ["attachmentIds"] = new JsonArray("att-1"),
            },
            epoch);

        Assert.False(late.Accepted);
        Assert.Equal(RemotePageSessionCodes.SessionClosed, late.Code);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);
        Assert.Equal(RemoteBridgePhase.Closed, rig.Composition.Phase);
    }

    [Fact]
    public void ATransferBeforeTheHandshakeIsRefusedWithBridgeNotReady()
    {
        using var rig = new RemoteBridgeRig();
        rig.LoadDocument();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);

        var baseline = rig.Transport.SentFrames.Count;
        var result = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        Assert.False(result.Ok);
        Assert.Equal(RemoteBridgeCodes.NotReady, result.Code);
        Assert.Empty(rig.Staging.CapturedIds);
        Assert.Empty(rig.FramesSince(baseline));
    }

    [Fact]
    public void ATransferWhileThePageIsSuspendedIsRefusedWithThePageCode()
    {
        using var rig = Ready();
        rig.Page.SuspendForNewWork(RemotePageSuspensionReason.TabSwitched);
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);

        var result = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        Assert.False(result.Ok);
        Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, result.Code);
        Assert.Empty(rig.Staging.CapturedIds);
    }

    [Fact]
    public void AStagingCaptureFailureIsSurfacedAndNothingIsSent()
    {
        using var rig = Ready();
        rig.Staging.CaptureFailureCode = "source-locked";
        var baseline = rig.Transport.SentFrames.Count;

        var result = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        Assert.False(result.Ok);
        Assert.Equal("source-locked", result.Code);
        Assert.Empty(rig.Composition.ReleasedSnapshots);
        Assert.DoesNotContain(
            rig.FramesSince(baseline),
            frame => AttachmentCodec.Decode(frame).Message!.Kind == WireMessageKind.BatchBegin);
    }

    [Fact]
    public void AFailureAfterTheFirstCaptureReleasesTheAlreadyStagedSnapshot()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "first.txt", Payload);
        rig.Staging.AddCapture("capture-2", "snapshot-2", "second.txt", Payload);
        var baseline = rig.Transport.SentFrames.Count;

        var result = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1", "missing"]));

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.CaptureIdUnknown, result.Code);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);
        Assert.Empty(rig.FramesSince(baseline));
    }

    [Fact]
    public void AnUnopenableSnapshotIsRefusedWithTheStagingCodeAndEverythingIsReleased()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Staging.FailSnapshotOpen = true;

        var result = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SnapshotUnknown, result.Code);
        Assert.Equal(["snapshot-1"], rig.Composition.ReleasedSnapshots);
        Assert.Null(rig.Composition.Coordinator!.BatchId);
    }

    [Fact]
    public void ASecondBatchCannotStartWhileTheFirstIsStillOpen()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Staging.AddCapture("capture-2", "snapshot-2", "note2.txt", Payload);
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        var second = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-2", ["capture-2"]));

        Assert.False(second.Ok);
        Assert.Equal("batch-in-progress", second.Code);
        Assert.Empty(rig.Staging.ReleasedSnapshotIds);
    }

    [Fact]
    public void ASecondBatchAfterTheFirstClosedUsesANewBatchAndFileId()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Payload);
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));
        rig.Settle();
        rig.Staging.AddCapture("capture-2", "snapshot-2", "note2.txt", Payload);

        var second = rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-2", ["capture-2"]));

        Assert.True(second.Ok);
        Assert.Equal(["batch-2-0"], second.FileIds);
        Assert.Equal(["snapshot-1"], rig.Staging.ReleasedSnapshotIds);
    }

    [Fact]
    public void TheTraceRecordsTheCompositionAndTransferDecisionsDeterministically()
    {
        using var rig = Ready();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Encoding.UTF8.GetBytes("trace-me"));
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));
        rig.Settle();

        var json = AttachmentTransferTrace.Serialize(rig.Composition.Trace.ToJson());

        Assert.Contains("\"bridge-out\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bridge-in\"", json, StringComparison.Ordinal);
        Assert.Contains("\"release\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("trace-me", json, StringComparison.Ordinal);
    }

    private static RemoteBridgeRig Ready()
    {
        var rig = new RemoteBridgeRig();
        rig.Handshake();
        return rig;
    }

    private static byte[] ReassembleChunks(RemoteBridgeRig rig, string fileId, int baseline)
    {
        var buffer = new byte[Payload.Length];
        foreach (var frame in rig.FramesSince(baseline))
        {
            var message = AttachmentCodec.Decode(frame).Message!;
            if (message.Kind != WireMessageKind.Chunk || message.FileId != fileId)
            {
                continue;
            }

            Assert.True(AttachmentCodec.TryDecodeBase64(message.DataBase64!, out var bytes));
            bytes.CopyTo(buffer, message.Offset!.Value);
        }

        return buffer;
    }

    private static byte[] BuildPayload(int size)
    {
        var bytes = new byte[size];
        for (var index = 0; index < size; index += 1)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }
}
