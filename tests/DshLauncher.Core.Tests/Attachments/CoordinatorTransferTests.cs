using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D11 协调器行为测试：全部用注入的假字节源 / 假通道 / 可控时钟驱动**生产**协调器，
/// 不 sleep、不读系统时间、不碰任何 Windows API。覆盖短读、源错误、乱序/重复/未知 ack、
/// 两类超时、取消与迟到结果、每条退出路径的流释放、每目标一次一个文件、≤2 块在途、
/// 全局并发上限、待确认字节/队列/缓存的上界、已确认成功项不重导、身份失效。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class CoordinatorTransferTests
{
    private const int Chunk = AttachmentProtocol.ChunkBytes;

    private const string Bin = "application/octet-stream";

    private static readonly string[] HappyPathFrameTypes =
        ["batch-begin", "file-begin", "chunk", "chunk", "chunk", "file-end", "batch-end"];

    private static readonly int[] HappyPathSeqs = [0, 1, 2];

    private static readonly int[] HappyPathOffsets = [0, Chunk, 2 * Chunk];

    private static readonly int[] HappyPathLengths = [Chunk, Chunk, 512];

    private static readonly int[] ShortReadLengths = [Chunk, 100];

    private static readonly int[] EarlyEofLengths = [100];

    private static readonly string[] StagedAttachmentIds = ["att-file-1"];

    private static readonly string[] FirstStagedIds = ["att-file-a"];

    private static readonly string[] PartialIds = ["att-file-c"];

    private static readonly string[] PromotedTarget = ["target-2"];

    [Fact]
    public void HappyPathTransfersOrderedChunksAndClosesTheBatch()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload((2 * Chunk) + 512));
        Assert.True(coordinator.OpenBatch("batch-happy", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);

        var maxPending = 0;
        var maxInFlight = 0;
        for (var step = 0; step < 100 && coordinator.BatchPhase == AttachmentBatchPhase.Open; step += 1)
        {
            coordinator.Pump();
            maxPending = Math.Max(maxPending, coordinator.PendingBytes);
            maxInFlight = Math.Max(maxInFlight, coordinator.PendingChunks);
            harness.Peer.Respond();
        }

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(HappyPathFrameTypes, harness.Channel.Types);
        var chunks = harness.Channel.OfType("chunk").ToList();
        Assert.Equal(HappyPathSeqs, chunks.Select(message => message.Seq!.Value));
        Assert.Equal(HappyPathOffsets, chunks.Select(message => message.Offset!.Value));
        Assert.Equal(HappyPathLengths, chunks.Select(message => message.ByteLength!.Value));
        Assert.True(maxInFlight <= AttachmentProtocol.MaxChunksInFlight, $"在途块数 {maxInFlight} 超限");
        Assert.True(maxPending <= AttachmentTransferCoordinator.MaxPendingBytes, $"待确认字节 {maxPending} 超限");
        Assert.True(maxPending <= 2 * Chunk);

        var record = coordinator.FileRecord("file-1")!;
        Assert.Equal(AttachmentFilePhase.Staged, record.Phase);
        Assert.Equal("staged", record.ResultStatus);
        Assert.Equal(StagedAttachmentIds, record.AttachmentIds);
        Assert.Equal(source.ByteLength, record.SentBytes);
        Assert.Equal(source.ByteLength, record.AckedBytes);
        Assert.Equal(0, record.InFlightChunks);
        Assert.True(record.ImportInvoked);
        Assert.True(record.SourceDisposed);
        Assert.True(source.Disposed);
        Assert.Equal(1, coordinator.ImportInvocations);
        Assert.Contains(harness.Trace.Entries, entry => entry.Kind == "message-out" && entry.Type == "chunk");
        Assert.Contains(harness.Trace.Entries, entry => entry.Kind == "message-in" && entry.Type == "ack");
    }

    [Fact]
    public void CoordinatorOutputReplaysCleanlyThroughTheProductionD10Session()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk + 17));
        Assert.True(coordinator.OpenBatch("batch-replay", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);

        // 把协调器与对端真实交换过的帧按发生顺序重放进 D10 生产状态机：
        // 两端说的是同一门语言，而不是各写一套"看起来一样"的实现。
        using var session = new AttachmentSession(now: () => harness.Clock.NowMs);
        Assert.True(session.Apply(ContextMessage()).Ok, "D10 会话必须接受同一身份");
        foreach (var (direction, json) in harness.Channel.Log)
        {
            var decoded = AttachmentCodec.Decode(json);
            Assert.True(decoded.Ok, $"{direction} 帧必须能被生产 codec 解码：{decoded.Code}");
            var applied = session.Apply(decoded.Message!);
            Assert.True(applied.Ok, $"{decoded.Message!.Type} 被 D10 生产状态机拒绝：{applied.Code} {applied.Detail}");
        }

        Assert.True(session.BatchClosed, "重放后 D10 会话必须判定批次关闭");
        var record = session.FileRecord("file-1")!;
        Assert.Equal(AttachmentDraftState.Staged, record.Draft);
        Assert.Equal(AttachmentUploadState.HarnessOwned, record.Upload);
        Assert.Equal(StagedAttachmentIds, record.AttachmentIds);
    }

    [Fact]
    public void ShortReadsAreAccumulatedIntoFullChunks()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk + 100), maxReadSize: 65_536);
        Assert.True(coordinator.OpenBatch("batch-short-read", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        CoordinatorFixture.Drive(harness);

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(ShortReadLengths, harness.Channel.OfType("chunk").Select(message => message.ByteLength!.Value));
        Assert.Equal(5, source.ReadCount); // 4 次 64 KiB 短读拼满 256 KiB + 1 次末尾短读
        Assert.True(source.ReadCount > harness.Channel.CountOfType("chunk"), "短读必须被累计，而不是按返回值直接成块");
        Assert.Equal(source.ByteLength, coordinator.FileRecord("file-1")!.AckedBytes);
        Assert.Equal(1, coordinator.ImportInvocations);
    }

    [Fact]
    public void SourceErrorFailsTheFileAndCancelsTheRemainingBatch()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var failing = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk), maxReadSize: 65_536, failAtOffset: Chunk + 1_000);
        var queued = new ScriptedByteSource(CoordinatorFixture.Payload(10));
        Assert.True(coordinator.OpenBatch("batch-fault", 2, failing.ByteLength + queued.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, failing).Ok);
        Assert.True(coordinator.AdmitFile("file-2", "b.bin", Bin, queued).Ok);

        var report = coordinator.Pump();

        Assert.Equal("cancelled", report.Stop);
        Assert.Equal(AttachmentBatchPhase.Cancelled, coordinator.BatchPhase);
        var failed = coordinator.FileRecord("file-1")!;
        Assert.Equal(AttachmentFilePhase.Failed, failed.Phase);
        Assert.Equal(AttachmentCoordinatorCodes.SourceError, failed.Code);
        Assert.True(failed.SourceDisposed);
        Assert.True(failing.Disposed);
        Assert.Equal(1, harness.Channel.CountOfType("chunk"));
        Assert.Equal(0, harness.Channel.CountOfType("file-end"));
        Assert.Equal("cancel", harness.Channel.Types[^1]);
        Assert.Equal("cancelled", harness.Channel.Sent[^1].Reason);
        Assert.Equal("native-capture", harness.Channel.Sent[^1].Stage);

        var other = coordinator.FileRecord("file-2")!;
        Assert.Equal(AttachmentFilePhase.Cancelled, other.Phase);
        Assert.True(other.SourceDisposed);
        Assert.True(queued.Disposed);
        Assert.Equal(0, queued.ReadCount);

        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-fault", "file-1", 0, 0, Chunk));
        Assert.Contains("ack:cancelled", coordinator.Pump().Rejections);
    }

    [Fact]
    public void SourceEofBeforeDeclaredLengthIsASizeMismatch()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(100), declaredLength: 300);
        Assert.True(coordinator.OpenBatch("batch-eof", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);

        coordinator.Pump();

        var record = coordinator.FileRecord("file-1")!;
        Assert.Equal(AttachmentFilePhase.Failed, record.Phase);
        Assert.Equal("size-mismatch", record.Code);
        Assert.True(source.Disposed);
        Assert.Equal(EarlyEofLengths, harness.Channel.OfType("chunk").Select(message => message.ByteLength!.Value));
        Assert.Equal(0, harness.Channel.CountOfType("file-end"));
        Assert.Equal(AttachmentBatchPhase.Cancelled, coordinator.BatchPhase);
    }

    [Fact]
    public void DeclaredHashMismatchFailsBeforeAnyFileEndOrImport()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        Assert.True(coordinator.OpenBatch("batch-hash", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source, declaredSha256: new string('f', 64)).Ok);

        CoordinatorFixture.Drive(harness);

        var record = coordinator.FileRecord("file-1")!;
        Assert.Equal(AttachmentFilePhase.Failed, record.Phase);
        Assert.Equal("hash-mismatch", record.Code);
        Assert.True(source.Disposed);
        Assert.Equal(0, harness.Channel.CountOfType("file-end"));
        Assert.Equal(0, coordinator.ImportInvocations);
        Assert.Equal(AttachmentBatchPhase.Cancelled, coordinator.BatchPhase);
        Assert.Equal("cancel", harness.Channel.Types[^1]);
    }

    [Fact]
    public void OutOfOrderDuplicateAndUnknownAcksDoNotAdvanceState()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        Assert.True(coordinator.OpenBatch("batch-ack", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        harness.Peer.AutoAck = false;
        coordinator.Pump();
        Assert.Equal(2, coordinator.FileRecord("file-1")!.InFlightChunks);

        // 从未发送过的块 / 已发送块的 offset 不符 / sessionId 不符：三条都在进入缓冲前被拒
        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack", "file-1", 9, 0, Chunk));
        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack", "file-1", 0, 1, Chunk));
        harness.Channel.Deliver(PeerWire.Ack("other-session", "batch-ack", "file-1", 0, 0, Chunk));
        var rejected = coordinator.Pump();
        Assert.Contains("ack:sequence-gap", rejected.Rejections);
        Assert.Contains("ack:size-mismatch", rejected.Rejections);
        Assert.Contains("ack:context-changed", rejected.Rejections);
        Assert.Equal(0, coordinator.FileRecord("file-1")!.AckedBytes);
        Assert.Equal(2, coordinator.FileRecord("file-1")!.InFlightChunks);

        // 乱序但内容正确的 ack：D10 只要求该块确实被发送过（重复由幂等分支处理）
        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack", "file-1", 1, Chunk, Chunk));
        Assert.Empty(coordinator.Pump().Rejections);
        Assert.Equal(Chunk, coordinator.FileRecord("file-1")!.AckedBytes);
        Assert.Equal(2, coordinator.FileRecord("file-1")!.InFlightChunks); // 释放的窗口立刻被下一块填满
        Assert.Equal(3 * Chunk, coordinator.FileRecord("file-1")!.SentBytes);

        // 重复 ack：幂等重放，既不重复计数也不触发导入
        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack", "file-1", 1, Chunk, Chunk));
        Assert.Empty(coordinator.Pump().Rejections);
        Assert.Equal(Chunk, coordinator.FileRecord("file-1")!.AckedBytes);
        Assert.Contains(harness.Trace.Entries, entry => entry.Detail.Contains("重复 ack", StringComparison.Ordinal));
        Assert.Equal(0, coordinator.ImportInvocations);

        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack", "file-1", 0, 0, Chunk));
        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack", "file-1", 2, 2 * Chunk, Chunk));
        harness.Peer.AutoAck = true;
        CoordinatorFixture.Drive(harness);

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(3 * Chunk, coordinator.FileRecord("file-1")!.AckedBytes);
        Assert.Equal(1, coordinator.ImportInvocations);
    }

    [Fact]
    public void AckTimeoutCancelsThroughTheInjectedClock()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
        Assert.True(coordinator.OpenBatch("batch-ack-timeout", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        harness.Peer.AutoAck = false;
        coordinator.Pump();
        Assert.Equal(1, coordinator.FileRecord("file-1")!.InFlightChunks);

        harness.Clock.Advance(AttachmentProtocol.AckTimeoutMs + 1);
        var report = coordinator.Pump();

        Assert.Equal("cancelled", report.Stop);
        Assert.Equal(AttachmentBatchPhase.Cancelled, coordinator.BatchPhase);
        var record = coordinator.FileRecord("file-1")!;
        Assert.Equal(AttachmentFilePhase.Failed, record.Phase);
        Assert.Equal(AttachmentCoordinatorCodes.AckTimeout, record.Code);
        Assert.True(source.Disposed);
        Assert.Equal("cancel", harness.Channel.Types[^1]);
        Assert.Equal("protocol-transfer", harness.Channel.Sent[^1].Stage);

        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-ack-timeout", "file-1", 0, 0, Chunk));
        Assert.Contains("ack:cancelled", coordinator.Pump().Rejections);
    }

    [Fact]
    public void ImportResultTimeoutCancelsThroughTheInjectedClock()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        Assert.True(coordinator.OpenBatch("batch-import-timeout", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        harness.Peer.AnswerFileEnd = false;
        for (var step = 0; step < 20 && harness.Channel.CountOfType("file-end") == 0; step += 1)
        {
            coordinator.Pump();
            harness.Peer.Respond();
        }

        Assert.Equal(1, harness.Channel.CountOfType("file-end"));
        Assert.True(source.Disposed, "file-end 发出后字节源必须立刻释放");
        Assert.Equal(AttachmentFilePhase.AwaitingImport, coordinator.FileRecord("file-1")!.Phase);

        harness.Clock.Advance(AttachmentProtocol.FileEndTimeoutMs + 1);
        var report = coordinator.Pump();

        Assert.Equal("cancelled", report.Stop);
        var record = coordinator.FileRecord("file-1")!;
        Assert.Equal(AttachmentFilePhase.Failed, record.Phase);
        Assert.Equal(AttachmentCoordinatorCodes.ImportTimeout, record.Code);
        Assert.Equal(1, coordinator.ImportInvocations);

        harness.Channel.Deliver(PeerWire.ImportResult("session-1", "batch-import-timeout", "file-1", "staged", ["att-file-1"]));
        var late = coordinator.Pump();
        Assert.Contains("import-result:cancelled", late.Rejections);
        Assert.Equal(1, coordinator.ImportInvocations);
    }

    [Fact]
    public void CancelStopsTransferDisposesSourcesAndRejectsLateResults()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var first = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        var second = new ScriptedByteSource(CoordinatorFixture.Payload(10));
        Assert.True(coordinator.OpenBatch("batch-cancel", 2, first.ByteLength + second.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, first).Ok);
        Assert.True(coordinator.AdmitFile("file-2", "b.bin", Bin, second).Ok);
        harness.Peer.AutoAck = false;
        coordinator.Pump();
        Assert.Equal(2, coordinator.FileRecord("file-1")!.InFlightChunks);

        Assert.True(coordinator.Cancel().Ok);
        var repeated = coordinator.Cancel();
        Assert.True(repeated.Ok);
        Assert.True(repeated.Duplicate, "重复 cancel 必须是幂等重放");

        Assert.Equal(1, harness.Channel.CountOfType("cancel"));
        Assert.Equal("cancelled", harness.Channel.Sent[^1].Reason);
        Assert.Equal(AttachmentBatchPhase.Cancelled, coordinator.BatchPhase);
        Assert.Equal(AttachmentFilePhase.Cancelled, coordinator.FileRecord("file-1")!.Phase);
        Assert.Equal(AttachmentFilePhase.Cancelled, coordinator.FileRecord("file-2")!.Phase);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(0, harness.Gate.ActiveCount);
        Assert.Equal(0, coordinator.ImportInvocations);
        Assert.Equal(0, harness.Channel.CountOfType("file-end"));

        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-cancel", "file-1", 0, 0, Chunk));
        harness.Channel.Deliver(PeerWire.ImportResult("session-1", "batch-cancel", "file-1", "staged", ["att-file-1"]));
        var late = coordinator.Pump();
        Assert.Contains("ack:cancelled", late.Rejections);
        Assert.Contains("import-result:cancelled", late.Rejections);
        Assert.Equal(0, coordinator.ImportInvocations);
        Assert.Equal(AttachmentFilePhase.Cancelled, coordinator.FileRecord("file-1")!.Phase);
    }

    [Fact]
    public void SourcesAreDisposedOnEveryExitPath()
    {
        // 成功：file-end 之后立刻释放
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
            harness.Coordinator.OpenBatch("batch-ok", 1, source.ByteLength);
            harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, source);
            CoordinatorFixture.Drive(harness);
            Assert.True(source.Disposed, "成功路径必须释放源");
            Assert.True(harness.Coordinator.FileRecord("file-1")!.SourceDisposed);
        }

        // 源故障
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk), failAtOffset: 100);
            harness.Coordinator.OpenBatch("batch-fault", 1, source.ByteLength);
            harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, source);
            harness.Coordinator.Pump();
            Assert.True(source.Disposed, "源故障路径必须释放源");
        }

        // 取消
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var source = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
            harness.Coordinator.OpenBatch("batch-cancel", 1, source.ByteLength);
            harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, source);
            harness.Peer.AutoAck = false;
            harness.Coordinator.Pump();
            harness.Coordinator.Cancel();
            Assert.True(source.Disposed, "取消路径必须释放源");
        }

        // 超时
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
            harness.Coordinator.OpenBatch("batch-timeout", 1, source.ByteLength);
            harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, source);
            harness.Peer.AutoAck = false;
            harness.Coordinator.Pump();
            harness.Clock.Advance(AttachmentProtocol.AckTimeoutMs + 1);
            harness.Coordinator.Pump();
            Assert.True(source.Disposed, "超时路径必须释放源");
        }

        // 身份失效（导航/关闭）
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var source = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
            harness.Coordinator.OpenBatch("batch-invalidate", 1, source.ByteLength);
            harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, source);
            harness.Peer.AutoAck = false;
            harness.Coordinator.Pump();
            harness.Coordinator.Invalidate();
            Assert.True(source.Disposed, "身份失效路径必须释放源");
        }

        // 协调器释放
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var source = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
            harness.Coordinator.OpenBatch("batch-dispose", 1, source.ByteLength);
            harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, source);
            harness.Coordinator.Dispose();
            Assert.True(source.Disposed, "协调器释放路径必须释放源");
        }

        // 准入失败（重复 fileId）：源在拒绝的同一调用里就被释放
        using (var harness = CoordinatorFixture.NewHarness())
        {
            var first = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
            var duplicate = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
            harness.Coordinator.OpenBatch("batch-admit", 2, 2 * Chunk);
            Assert.True(harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, first).Ok);
            var refused = harness.Coordinator.AdmitFile("file-1", "a.bin", Bin, duplicate);
            Assert.False(refused.Ok);
            Assert.Equal("duplicate-operation", refused.Code);
            Assert.True(duplicate.Disposed, "准入失败路径必须释放源");
            Assert.Equal(0, duplicate.ReadCount);
        }
    }

    [Fact]
    public void OneFilePerTargetAndAtMostTwoChunksInFlight()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var first = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        var second = new ScriptedByteSource(CoordinatorFixture.Payload(10));
        Assert.True(coordinator.OpenBatch("batch-serial", 2, first.ByteLength + second.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-a", "a.bin", Bin, first).Ok);
        Assert.True(coordinator.AdmitFile("file-b", "b.bin", Bin, second).Ok);
        Assert.Equal(2, coordinator.QueueDepth); // 两个文件都已准入、都还没开始
        Assert.Equal(2, coordinator.QueueCapacity);

        harness.Peer.AutoAck = false;
        coordinator.Pump();
        Assert.Equal(1, coordinator.QueueDepth); // file-a 已开始，只有 file-b 还在排队
        Assert.Equal(2, coordinator.FileRecord("file-a")!.InFlightChunks);
        Assert.Equal(0, coordinator.FileRecord("file-b")!.SentBytes);
        Assert.Equal(AttachmentFilePhase.Pending, coordinator.FileRecord("file-b")!.Phase);
        Assert.Equal(0, second.ReadCount);
        Assert.Equal(1, harness.Channel.CountOfType("file-begin"));
        Assert.Equal(2, harness.Channel.CountOfType("chunk"));
        Assert.True(coordinator.PendingBytes <= AttachmentTransferCoordinator.MaxPendingBytes);

        var stalled = coordinator.Pump();
        Assert.Equal(0, stalled.Sent);
        Assert.Equal(0, stalled.ChunksSent);

        harness.Peer.AutoAck = true;
        for (var step = 0; step < 100 && coordinator.BatchPhase == AttachmentBatchPhase.Open; step += 1)
        {
            coordinator.Pump();
            harness.Peer.Respond();
            Assert.True(coordinator.PendingBytes <= AttachmentTransferCoordinator.MaxPendingBytes);
            Assert.True(coordinator.PendingChunks <= AttachmentProtocol.MaxChunksInFlight);
        }

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(2, harness.Channel.CountOfType("file-begin"));
        Assert.Equal(AttachmentFilePhase.Staged, coordinator.FileRecord("file-a")!.Phase);
        Assert.Equal(AttachmentFilePhase.Staged, coordinator.FileRecord("file-b")!.Phase);
        Assert.True(second.ReadCount > 0);
        Assert.Equal(0, coordinator.QueueDepth);
        Assert.Equal(2, coordinator.ImportInvocations);
        // file-b 的块在 file-a 的 file-end 之后才出现
        var types = harness.Channel.Types;
        Assert.True(
            types.ToList().IndexOf("file-end") < types.ToList().LastIndexOf("chunk"),
            "下一个文件必须等上一个文件结束之后才开始");
    }

    [Fact]
    public void GlobalConcurrencyGateBoundsTargetsAndItsQueue()
    {
        var gate = new AttachmentConcurrencyGate(maxConcurrent: 1, queueCapacity: 1);
        using var first = CoordinatorFixture.NewHarness("target-1", gate);
        using var second = CoordinatorFixture.NewHarness("target-2", gate);
        using var third = CoordinatorFixture.NewHarness("target-3", gate);
        var sourceA = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        var sourceB = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        var sourceC = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        Assert.True(first.Coordinator.OpenBatch("batch-g1", 1, sourceA.ByteLength).Ok);
        Assert.True(first.Coordinator.AdmitFile("file-a", "a.bin", Bin, sourceA).Ok);
        Assert.True(second.Coordinator.OpenBatch("batch-g2", 1, sourceB.ByteLength).Ok);
        Assert.True(second.Coordinator.AdmitFile("file-b", "b.bin", Bin, sourceB).Ok);
        Assert.True(third.Coordinator.OpenBatch("batch-g3", 1, sourceC.ByteLength).Ok);
        Assert.True(third.Coordinator.AdmitFile("file-c", "c.bin", Bin, sourceC).Ok);
        first.Peer.AutoAck = false;
        second.Peer.AutoAck = false;
        third.Peer.AutoAck = false;

        first.Coordinator.Pump();
        Assert.Equal(1, gate.ActiveCount);
        var queued = second.Coordinator.Pump();
        Assert.Equal("file-b", queued.QueuedFileId);
        Assert.Equal(1, gate.WaitingCount);
        Assert.Equal(0, sourceB.ReadCount);
        Assert.Equal(AttachmentFilePhase.Pending, second.Coordinator.FileRecord("file-b")!.Phase);

        var starved = third.Coordinator.Pump();
        Assert.Equal("file-c", starved.StarvedFileId);
        Assert.Equal(1, gate.RefusedCount);
        Assert.Equal(1, gate.WaitingCount);
        Assert.Equal(0, sourceC.ReadCount);
        Assert.Equal(AttachmentFilePhase.Pending, third.Coordinator.FileRecord("file-c")!.Phase);
        Assert.True(gate.ActiveCount <= 1);
        Assert.True(gate.WaitingCount <= 1);

        first.Peer.AutoAck = true;
        CoordinatorFixture.Drive(first);
        Assert.Equal(AttachmentBatchPhase.Closed, first.Coordinator.BatchPhase);
        Assert.Equal(1, gate.ActiveCount);
        Assert.Equal(0, gate.WaitingCount);
        Assert.Equal(PromotedTarget, gate.ActiveKeys);

        second.Coordinator.Pump();
        Assert.True(sourceB.ReadCount > 0, "获得全局槽位后必须立刻开始传输");
        second.Peer.AutoAck = true;
        CoordinatorFixture.Drive(second);
        Assert.Equal(AttachmentBatchPhase.Closed, second.Coordinator.BatchPhase);
        Assert.Equal(AttachmentFilePhase.Pending, third.Coordinator.FileRecord("file-c")!.Phase);
        Assert.True(gate.ActiveCount <= 1);
    }

    [Fact]
    public void PendingBytesQueueAndCacheStayWithinTheirBounds()
    {
        using var harness = CoordinatorFixture.NewHarness(replayCapacity: 4);
        var coordinator = harness.Coordinator;
        const int Files = 4;
        var sources = Enumerable.Range(0, Files).Select(_ => new ScriptedByteSource(CoordinatorFixture.Payload(10))).ToList();
        Assert.True(coordinator.OpenBatch("batch-bounds", Files, sources.Sum(source => source.ByteLength)).Ok);
        for (var index = 0; index < Files; index += 1)
        {
            Assert.True(coordinator.AdmitFile($"file-{index}", $"f{index}.bin", Bin, sources[index]).Ok);
        }

        Assert.Equal(Files, coordinator.QueueCapacity);
        Assert.Equal(Files, coordinator.QueueDepth);

        var maxPending = 0;
        var maxQueue = 0;
        var maxScratch = 0;
        for (var step = 0; step < 200 && coordinator.BatchPhase == AttachmentBatchPhase.Open; step += 1)
        {
            coordinator.Pump();
            harness.Peer.Respond();
            maxPending = Math.Max(maxPending, coordinator.PendingBytes);
            maxQueue = Math.Max(maxQueue, coordinator.QueueDepth);
            maxScratch = Math.Max(maxScratch, coordinator.ScratchBytes);
            Assert.True(coordinator.PendingBytes <= AttachmentTransferCoordinator.MaxPendingBytes);
            Assert.True(coordinator.PendingChunks <= AttachmentProtocol.MaxChunksInFlight);
            Assert.True(coordinator.QueueDepth <= coordinator.QueueCapacity);
            Assert.True(coordinator.ReplayCachePinned <= Files + 1);
            Assert.True(coordinator.ReplayCacheSize <= Math.Max(4, coordinator.ReplayCachePinned) + 1);
        }

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.True(maxPending <= AttachmentTransferCoordinator.MaxPendingBytes);
        Assert.True(maxQueue <= Files);
        Assert.True(maxScratch <= Chunk);
        Assert.Equal(0, coordinator.ScratchBytes);
        Assert.True(coordinator.ReplayCacheEvicted > 0, "容量 4 的缓存必须发生过淘汰");
        Assert.True(coordinator.ReplayCacheSize <= Math.Max(4, coordinator.ReplayCachePinned) + 1);

        // 第二批：新条目继续被有界淘汰，容量不会随批次数量增长
        var second = Enumerable.Range(0, Files).Select(_ => new ScriptedByteSource(CoordinatorFixture.Payload(10))).ToList();
        Assert.True(coordinator.OpenBatch("batch-bounds-2", Files, second.Sum(source => source.ByteLength)).Ok);
        for (var index = 0; index < Files; index += 1)
        {
            Assert.True(coordinator.AdmitFile($"file-2-{index}", $"g{index}.bin", Bin, second[index]).Ok);
        }

        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.True(coordinator.ReplayCacheSize <= Math.Max(4, coordinator.ReplayCachePinned) + 1);
        Assert.Equal(2, harness.Peer.CompletedBatches.Count);
    }

    [Fact]
    public void ConfirmedFilesAreNeverReimportedAndRetryNeedsNewIds()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var first = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        Assert.True(coordinator.OpenBatch("batch-one", 1, first.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, first).Ok);
        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(1, coordinator.ImportInvocations);
        Assert.Equal("staged", coordinator.FileRecord("file-1")!.ResultStatus);

        // 复用已关闭批次的 batchId
        var reuse = coordinator.OpenBatch("batch-one", 1, 300);
        Assert.False(reuse.Ok);
        Assert.Equal("duplicate-operation", reuse.Code);

        var framesBefore = harness.Channel.Sent.Count;
        Assert.True(coordinator.OpenBatch("batch-two", 1, 300).Ok);
        var confirmedRetry = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        var refused = coordinator.AdmitFile("file-1", "a.bin", Bin, confirmedRetry);
        Assert.False(refused.Ok);
        Assert.Equal("duplicate-operation", refused.Code);
        Assert.True(confirmedRetry.Disposed);
        Assert.Equal(0, confirmedRetry.ReadCount);
        Assert.Equal(framesBefore + 1, harness.Channel.Sent.Count); // 只多了 batch-begin，没有 file-begin/chunk

        // 用户重新导入失败项才走新 fileId
        var retry = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        Assert.True(coordinator.AdmitFile("file-1-retry", "a.bin", Bin, retry).Ok);
        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(2, coordinator.ImportInvocations);
        Assert.Equal("staged", coordinator.FileRecord("file-1-retry")!.ResultStatus);
        Assert.Equal(2, harness.Peer.CompletedBatches.Count);
    }

    [Fact]
    public void InvalidationClearsCacheAndRejectsStaleIdentity()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        harness.Peer.AutoAck = false;
        Assert.True(coordinator.OpenBatch("batch-invalidate", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        coordinator.Pump();
        Assert.True(coordinator.ReplayCacheSize > 0);
        Assert.Equal(1, harness.Gate.ActiveCount); // 活动文件持有唯一的全局槽位

        // sessionId 不符：在任何接收缓冲状态改变之前就被拒绝
        harness.Channel.Deliver(PeerWire.Ack("other-session", "batch-invalidate", "file-1", 0, 0, Chunk));
        var rejected = coordinator.Pump();
        Assert.Contains("ack:context-changed", rejected.Rejections);
        Assert.Equal(0, coordinator.FileRecord("file-1")!.AckedBytes);

        coordinator.Invalidate();
        Assert.True(coordinator.IdentityExpired);
        Assert.Equal(0, coordinator.ReplayCacheSize);
        Assert.True(source.Disposed);
        Assert.Equal(0, harness.Gate.ActiveCount);
        Assert.Equal("context-changed", coordinator.Pump().Stop);

        var framesAfterInvalidate = harness.Channel.Sent.Count;
        Assert.False(coordinator.OpenBatch("batch-after-invalidate", 1, 10).Ok);
        var orphan = new ScriptedByteSource(CoordinatorFixture.Payload(10));
        var refused = coordinator.AdmitFile("file-x", "x.bin", Bin, orphan);
        Assert.Equal("context-changed", refused.Code);
        Assert.True(orphan.Disposed);
        Assert.Equal(framesAfterInvalidate, harness.Channel.Sent.Count);

        // 新身份（documentEpoch 变化）重新绑定后恢复
        coordinator.BindContext(CoordinatorFixture.Identity(documentEpoch: 8));
        Assert.False(coordinator.IdentityExpired);
        Assert.Equal(0, coordinator.ReplayCacheSize);
        Assert.True(coordinator.OpenBatch("batch-new-epoch", 1, 300).Ok);
        var fresh = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        Assert.True(coordinator.AdmitFile("file-new", "n.bin", Bin, fresh).Ok);
        harness.Peer.AutoAck = true;
        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal("staged", coordinator.FileRecord("file-new")!.ResultStatus);

        // 身份再次变化即失效：旧批次不可继续
        coordinator.BindContext(CoordinatorFixture.Identity(documentEpoch: 9));
        Assert.Equal(0, coordinator.ReplayCacheSize);
        Assert.Equal(AttachmentBatchPhase.None, coordinator.BatchPhase);
    }

    [Fact]
    public void RepeatedFileEndAndBatchEndNeverImportTwiceAndLateFramesDoNotReopen()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        Assert.True(coordinator.OpenBatch("batch-dedup", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "a.bin", Bin, source).Ok);
        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(1, coordinator.ImportInvocations);
        var framesAtClose = harness.Channel.Sent.Count;
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CoordinatorFixture.Payload(300)));

        // 内容一致的重复 batch-end：幂等重放，不产生第二次导入
        harness.Channel.Deliver(PeerWire.BatchEnd(
            "session-1",
            "batch-dedup",
            "staged",
            new JsonArray(PeerWire.ResultItem("file-1", "staged", ["att-file-1"]))));
        var replay = coordinator.Pump();
        Assert.Empty(replay.Rejections);
        Assert.Equal(1, coordinator.ImportInvocations);
        Assert.Equal(framesAtClose, harness.Channel.Sent.Count);

        // 内容冲突的重复 batch-end
        harness.Channel.Deliver(PeerWire.BatchEnd(
            "session-1",
            "batch-dedup",
            "failed",
            new JsonArray(PeerWire.ResultItem("file-1", "failed", null, "draft-import-failed"))));
        Assert.Contains("batch-end:duplicate-operation", coordinator.Pump().Rejections);

        // 迟到的 file-end：batch-closed，不得重开操作
        harness.Channel.Deliver(PeerWire.FileEnd("session-1", "batch-dedup", "file-1", 300, sha256));
        var lateEnd = coordinator.Pump();
        Assert.Contains("file-end:batch-closed", lateEnd.Rejections);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(1, coordinator.ImportInvocations);
        Assert.Equal(framesAtClose, harness.Channel.Sent.Count);

        // 迟到的 chunk 与 import-result 同样拒绝，且不产生第二次导入
        harness.Channel.Deliver(PeerWire.Chunk("session-1", "batch-dedup", "file-1", 0, 0, CoordinatorFixture.Payload(10)));
        harness.Channel.Deliver(PeerWire.ImportResult("session-1", "batch-dedup", "file-1", "staged", ["att-file-1"]));
        var lateAgain = coordinator.Pump();
        Assert.Contains("chunk:batch-closed", lateAgain.Rejections);
        Assert.Contains("import-result:batch-closed", lateAgain.Rejections);
        Assert.Equal(1, coordinator.ImportInvocations);
        Assert.Equal(framesAtClose, harness.Channel.Sent.Count);
        Assert.Equal("staged", coordinator.FileRecord("file-1")!.ResultStatus);
    }

    [Fact]
    public void PerFileResultsAreBookedPerFileIdAndDuplicateImportResultNeverImportsAgain()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var a = new ScriptedByteSource(CoordinatorFixture.Payload(300));
        var b = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk));
        var c = new ScriptedByteSource(CoordinatorFixture.Payload(64));
        Assert.True(coordinator.OpenBatch("batch-partial", 3, a.ByteLength + b.ByteLength + c.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-a", "a.bin", Bin, a).Ok);
        Assert.True(coordinator.AdmitFile("file-b", "b.bin", Bin, b).Ok);
        Assert.True(coordinator.AdmitFile("file-c", "c.bin", Bin, c).Ok);
        harness.Peer.Statuses["file-b"] = "failed";
        harness.Peer.Codes["file-b"] = "draft-import-failed";
        harness.Peer.Statuses["file-c"] = "partial";
        harness.Peer.Codes["file-c"] = "partial-import";

        for (var step = 0; step < 100 && coordinator.FileRecord("file-a")!.Phase != AttachmentFilePhase.Staged; step += 1)
        {
            coordinator.Pump();
            harness.Peer.Respond();
        }

        Assert.Equal(AttachmentFilePhase.Staged, coordinator.FileRecord("file-a")!.Phase);

        // 重复的 import-result：幂等重放，既不是拒绝也不产生第二次导入
        harness.Channel.Deliver(PeerWire.ImportResult("session-1", "batch-partial", "file-a", "staged", ["att-file-a"]));
        var replay = coordinator.Pump();
        Assert.Empty(replay.Rejections);
        Assert.Contains(harness.Trace.Entries, entry => entry.Detail.Contains("重复 import-result", StringComparison.Ordinal));
        Assert.Equal(
            1,
            harness.Channel.OfType("file-end").Count(message => string.Equals(message.FileId, "file-a", StringComparison.Ordinal)));
        Assert.Equal(AttachmentFilePhase.Staged, coordinator.FileRecord("file-a")!.Phase);

        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);

        // 逐 fileId 记账：成功、失败、部分成功各不相同，绝不塌缩成一个批次布尔值
        var staged = coordinator.FileRecord("file-a")!;
        var failed = coordinator.FileRecord("file-b")!;
        var partial = coordinator.FileRecord("file-c")!;
        Assert.Equal(AttachmentFilePhase.Staged, staged.Phase);
        Assert.Equal("staged", staged.ResultStatus);
        Assert.Equal(FirstStagedIds, staged.AttachmentIds);
        Assert.Equal(AttachmentFilePhase.Failed, failed.Phase);
        Assert.Equal("failed", failed.ResultStatus);
        Assert.Equal("draft-import-failed", failed.Code);
        Assert.Empty(failed.AttachmentIds);
        Assert.Equal(AttachmentFilePhase.Partial, partial.Phase);
        Assert.Equal("partial", partial.ResultStatus);
        Assert.Equal("partial-import", partial.Code);
        Assert.Equal(PartialIds, partial.AttachmentIds);
        Assert.Equal(3, coordinator.ImportInvocations);

        // batch-end 汇总逐 fileId 的结果，而不是一个布尔值
        var batchEnd = harness.Channel.Sent[^1];
        Assert.Equal("batch-end", batchEnd.Type);
        Assert.Equal("partial", batchEnd.Status);
        var items = batchEnd.Results!.Select(node => node!.AsObject()).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("staged", items[0]["status"]!.GetValue<string>());
        Assert.Equal("failed", items[1]["status"]!.GetValue<string>());
        Assert.Equal("partial", items[2]["status"]!.GetValue<string>());
        Assert.Equal("draft-import-failed", items[1]["code"]!.GetValue<string>());
    }

    private static AttachmentMessage ContextMessage()
    {
        var identity = CoordinatorFixture.Identity();
        var decoded = AttachmentCodec.Decode(new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "context",
            ["sessionId"] = identity.SessionId,
            ["targetId"] = identity.TargetId,
            ["documentEpoch"] = identity.DocumentEpoch,
            ["composerEpoch"] = identity.ComposerEpoch,
            ["composerScope"] = identity.ComposerScope,
        }.ToJsonString());
        Assert.True(decoded.Ok, $"context 必须合法：{decoded.Code}");
        return decoded.Message!;
    }
}
