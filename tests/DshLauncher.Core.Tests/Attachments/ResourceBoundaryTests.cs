using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D21 Core 侧资源与所有权边界用例（可移植、Linux 可跑）。
///
/// 与既有 D11 用例的分工：既有用例守"语义正确"，本类守"资源的**可测上界**"，
/// 并且每一例都把实测值写成机器可读的指标行（`DSH_D21_CORE_METRICS_DIR` 指向目录时），
/// 由 `plugins/dsh-remote-attachments/tests/fixtures/d21-resource-gates.mjs` 汇入
/// `artifacts/verify-portable/d21-resource-report.json`。未设置该环境变量时用例照常断言，
/// 只是不落盘（Core profile 单独跑不需要夹具）。
///
/// 覆盖（对应 tests/fixtures/d21-resource-criteria.mjs 的冻结表）：
///   - 慢 ACK：2 块窗口与 MaxPendingBytes 的峰值，以及"一条 ack 只放行一块"；
///   - 暂停消费者：全局并发闸门的有界等待队列（满即拒绝，绝不无界排队）；
///   - 取消：在途窗口归零 + 全部自有字节源恰好释放一次；
///   - 清理所有权：Dispose 释放槽位与排队中的源、幂等、不重复释放；
///   - 去重台账：注入时钟下的 TTL 到期与容量上界（不 sleep）；
///   - 连续 30 轮：托管堆序列的斜率与包络有界，源与缓存不随轮数增长。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
[Trait("d21", "resources")]
public sealed class ResourceBoundaryTests
{
    private const string Bin = "application/octet-stream";

    [Fact]
    public void SlowAckKeepsTheInflightWindowAtTwoChunksAndMaxPendingBytes()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var clock = harness.Clock;
        // 慢 ACK：对端完全不回，窗口由协调器自己顶住。
        harness.Peer.AutoAck = false;
        const int chunk = AttachmentProtocol.ChunkBytes;
        const int size = 5 * chunk;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(size));
        Assert.True(coordinator.OpenBatch("batch-slow-ack", 1, size).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "slow.bin", Bin, source).Ok);

        var maxPendingBytes = 0;
        var maxPendingChunks = 0;
        var noAckPumps = 0;
        for (var step = 0; step < 8; step += 1)
        {
            coordinator.Pump();
            maxPendingBytes = Math.Max(maxPendingBytes, coordinator.PendingBytes);
            maxPendingChunks = Math.Max(maxPendingChunks, coordinator.PendingChunks);
            noAckPumps += 1;
        }

        // 无 ACK 时空转：不得无界重发，也不得推进字节。
        Assert.Equal(AttachmentProtocol.MaxChunksInFlight, coordinator.PendingChunks);
        Assert.Equal(AttachmentProtocol.MaxChunksInFlight * chunk, coordinator.PendingBytes);
        Assert.True(coordinator.PendingBytes <= AttachmentTransferCoordinator.MaxPendingBytes);
        Assert.Equal(AttachmentProtocol.MaxChunksInFlight, harness.Channel.CountOfType("chunk"));
        Assert.Equal(AttachmentProtocol.MaxChunksInFlight * chunk, coordinator.FileRecord("file-1")!.SentBytes);
        Assert.Equal(0, coordinator.FileRecord("file-1")!.AckedBytes);

        // 慢 ACK：一条 ack 只放行一块（窗口不被绕过）。
        var maxNewChunksPerAck = 0;
        var ackedSeq = new HashSet<int>();
        for (var round = 0; round < 6; round += 1)
        {
            var target = harness.Channel.Sent.FirstOrDefault(message => message.Kind == WireMessageKind.Chunk && !ackedSeq.Contains(message.Seq!.Value));
            if (target is null) break;
            ackedSeq.Add(target.Seq!.Value);
            var before = harness.Channel.CountOfType("chunk");
            harness.Channel.Deliver(PeerWire.Ack(
                harness.Peer.SessionId,
                target.BatchId!,
                target.FileId!,
                target.Seq!.Value,
                target.Offset!.Value,
                target.ByteLength!.Value));
            coordinator.Pump();
            maxNewChunksPerAck = Math.Max(maxNewChunksPerAck, harness.Channel.CountOfType("chunk") - before);
            maxPendingBytes = Math.Max(maxPendingBytes, coordinator.PendingBytes);
            maxPendingChunks = Math.Max(maxPendingChunks, coordinator.PendingChunks);
        }

        Assert.Equal(size / chunk, source.ReadCount); // 每块一次读（短读合法，这里每块恰好一次）
        Assert.True(maxNewChunksPerAck <= 1, $"一条 ack 放行了 {maxNewChunksPerAck} 块");
        Assert.True(maxPendingBytes <= AttachmentTransferCoordinator.MaxPendingBytes);
        Assert.True(maxPendingChunks <= AttachmentProtocol.MaxChunksInFlight);

        // 控制：同一路径在正常 ACK 下必须能收尾（断言不是"卡死即通过"）。
        harness.Peer.AutoAck = true;
        CoordinatorFixture.Drive(harness);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal("staged", coordinator.FileRecord("file-1")!.ResultStatus);
        clock.Advance(1_000); // 显式推进注入时钟：超时判定只认它，不用 sleep

        D21CoreMetrics.Write(
            nameof(SlowAckKeepsTheInflightWindowAtTwoChunksAndMaxPendingBytes),
            [
                D21CoreMetrics.Row("C01-pending-bytes-peak", maxPendingBytes, $"无 ACK 泵 {noAckPumps} 次 + 逐条慢 ACK；MaxPendingBytes={AttachmentTransferCoordinator.MaxPendingBytes}"),
                D21CoreMetrics.Row("C02-pending-chunks-peak", maxPendingChunks, $"MaxChunksInFlight={AttachmentProtocol.MaxChunksInFlight}"),
                D21CoreMetrics.Row("C03-slow-ack-progress-per-ack", maxNewChunksPerAck, $"size={size}B chunk={chunk}B 共 {ackedSeq.Count} 条 ack")
            ]);
    }

    [Fact]
    public void PausedConsumerKeepsTheGlobalGateQueueBoundedAndRefusesOverflow()
    {
        // 默认闸门：MaxConcurrent=2、QueueCapacity=32（都要来自产品常量，不另抄数字）。
        var gate = new AttachmentConcurrencyGate();
        Assert.Equal(AttachmentConcurrencyGate.DefaultQueueCapacity, gate.QueueCapacity);
        Assert.True(gate.QueueCapacity <= AttachmentConcurrencyGate.MaxQueueCapacity, "队列容量必须自身有上界");

        var maxWaiting = 0;
        var maxActive = 0;
        const int attempts = 64;
        for (var index = 0; index < attempts; index += 1)
        {
            var outcome = gate.TryAcquire($"paused-target-{index}");
            if (index < AttachmentProtocol.MaxConcurrentTargets) Assert.Equal(AttachmentGateOutcome.Active, outcome);
            else if (index < AttachmentProtocol.MaxConcurrentTargets + gate.QueueCapacity) Assert.Equal(AttachmentGateOutcome.Queued, outcome);
            else Assert.Equal(AttachmentGateOutcome.Refused, outcome);
            maxWaiting = Math.Max(maxWaiting, gate.WaitingCount);
            maxActive = Math.Max(maxActive, gate.ActiveCount);
        }

        Assert.Equal(gate.QueueCapacity, maxWaiting);
        Assert.Equal(AttachmentProtocol.MaxConcurrentTargets, maxActive);
        // 超出的申请必须被拒绝而不是排队：64 - 2（活动）- 32（排队）= 30。
        Assert.Equal(attempts - maxActive - maxWaiting, gate.RefusedCount);
        Assert.True(gate.RefusedCount > 0, "队列满必须拒绝，否则有界队列无从谈起");

        // 消费者恢复：按 FIFO 释放，队列不会残留（有界不等于永远满着）。
        // 释放活动槽位会把队首提升为活动，因此必须循环到两边都空。
        while (gate.ActiveCount > 0) gate.Release(gate.ActiveKeys[0]);
        while (gate.WaitingCount > 0) gate.Release(gate.WaitingKeys[0]);
        Assert.Equal(0, gate.ActiveCount);
        Assert.Equal(0, gate.WaitingCount);

        D21CoreMetrics.Write(
            nameof(PausedConsumerKeepsTheGlobalGateQueueBoundedAndRefusesOverflow),
            [
                D21CoreMetrics.Row("C04-gate-waiting-peak", maxWaiting, $"queueCapacity={gate.QueueCapacity} 尝试 {gate.QueueCapacity * 2} 个键"),
                D21CoreMetrics.Row("C05-gate-refused-on-overflow", gate.RefusedCount, $"refused={gate.RefusedCount} 全部来自队列满"),
                D21CoreMetrics.Row("C06-gate-active-peak", maxActive, $"maxConcurrent={gate.MaxConcurrent}")
            ]);
    }

    [Fact]
    public void CancelReleasesPendingBytesChunksAndEveryOwnedSource()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        harness.Peer.AutoAck = false; // 取消时窗口里确实有在途块
        var sources = new List<CountingByteSource>
        {
            new(CoordinatorFixture.Payload(3 * AttachmentProtocol.ChunkBytes)),
            new(CoordinatorFixture.Payload(64)),
            new(CoordinatorFixture.Payload(64))
        };
        Assert.True(coordinator.OpenBatch("batch-cancel", sources.Count, sources.Sum(item => item.ByteLength)).Ok);
        for (var index = 0; index < sources.Count; index += 1)
        {
            Assert.True(coordinator.AdmitFile($"file-{index}", $"c{index}.bin", Bin, sources[index]).Ok);
        }

        coordinator.Pump();
        coordinator.Pump();
        Assert.True(coordinator.PendingBytes > 0, "取消前必须有在途字节，否则归零是空检查");
        Assert.True(sources[0].DisposeCount == 0);

        Assert.True(coordinator.Cancel().Ok);
        var disposed = sources.Count(item => item.DisposeCount == 1);
        Assert.Equal(0, coordinator.PendingBytes);
        Assert.Equal(0, coordinator.PendingChunks);
        Assert.Equal(0, coordinator.ScratchBytes);
        Assert.Equal(sources.Count, disposed);
        Assert.All(sources, item => Assert.True(item.DisposeCount == 1, $"{item.DisposeCount} 次释放（必须恰好一次）"));

        D21CoreMetrics.Write(
            nameof(CancelReleasesPendingBytesChunksAndEveryOwnedSource),
            [
                D21CoreMetrics.Row("C07-cancel-pending-bytes", coordinator.PendingBytes, $"取消后 PendingBytes；ScratchBytes={coordinator.ScratchBytes}"),
                D21CoreMetrics.Row("C08-cancel-pending-chunks", coordinator.PendingChunks, $"取消后 PendingChunks；BatchPhase={coordinator.BatchPhase}"),
                D21CoreMetrics.Ratio("C09-cancel-sources-disposed", disposed, sources.Count, $"每个源 DisposeCount=1：[{string.Join(',', sources.Select(item => item.DisposeCount))}]")
            ]);
    }

    [Fact]
    public void DisposeReleasesGateSlotsQueuedSourcesAndIsIdempotent()
    {
        // 全局并发 1 + 队列 1：第二个 target 排队，验证"释放即让位"，以及释放后不残留槽位。
        var gate = new AttachmentConcurrencyGate(maxConcurrent: 1, queueCapacity: 1);
        var first = CoordinatorFixture.NewHarness(targetId: "target-d21-a", gate: gate);
        var second = CoordinatorFixture.NewHarness(targetId: "target-d21-b", gate: gate);
        try
        {
            var sources = new List<CountingByteSource>
            {
                new(CoordinatorFixture.Payload(3 * AttachmentProtocol.ChunkBytes)),
                new(CoordinatorFixture.Payload(64)),
                new(CoordinatorFixture.Payload(64))
            };
            first.Peer.AutoAck = false;
            second.Peer.AutoAck = false;
            Assert.True(first.Coordinator.OpenBatch("batch-dispose-a", sources.Count, sources.Sum(item => item.ByteLength)).Ok);
            for (var index = 0; index < sources.Count; index += 1)
            {
                Assert.True(first.Coordinator.AdmitFile($"file-{index}", $"d{index}.bin", Bin, sources[index]).Ok);
            }

            var secondSource = new CountingByteSource(CoordinatorFixture.Payload(64));
            Assert.True(second.Coordinator.OpenBatch("batch-dispose-b", 1, secondSource.ByteLength).Ok);
            Assert.True(second.Coordinator.AdmitFile("file-b", "b.bin", Bin, secondSource).Ok);

            first.Coordinator.Pump();   // 占用唯一活动槽位；窗口顶住，不再推进
            second.Coordinator.Pump();  // 排队等待
            // 负向探针：队列（容量 1）已满时第三个申请必须被拒绝，而不是排成无界队列。
            Assert.Equal(AttachmentGateOutcome.Refused, gate.TryAcquire("probe-d21"));
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal(1, gate.WaitingCount);
            Assert.Equal(1, gate.RefusedCount);

            first.Coordinator.Dispose(); // 释放活动槽位并把队首提升为活动
            Assert.Equal(0, gate.WaitingCount);
            Assert.Equal(1, gate.ActiveCount);

            first.Coordinator.Dispose(); // 幂等：不得再次改变状态
            Assert.Equal(1, gate.ActiveCount);

            second.Coordinator.Dispose(); // 排队中的源同样属协调器所有：必须释放
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.WaitingCount);
            second.Coordinator.Dispose();

            var disposed = sources.Count(item => item.DisposeCount == 1);
            Assert.Equal(sources.Count, disposed);
            Assert.True(secondSource.DisposeCount == 1, $"排队中的源必须恰好释放一次，实际 {secondSource.DisposeCount}");
            var idempotent = sources.All(item => item.DisposeCount == 1) && secondSource.DisposeCount == 1;
            Assert.True(idempotent, "重复 Dispose 不得造成重复释放");

            D21CoreMetrics.Write(
                nameof(DisposeReleasesGateSlotsQueuedSourcesAndIsIdempotent),
                [
                    D21CoreMetrics.Row("C17-dispose-gate-slots", gate.ActiveCount + gate.WaitingCount, $"Dispose 后 active={gate.ActiveCount} waiting={gate.WaitingCount}"),
                    D21CoreMetrics.Row("C18-dispose-idempotent", idempotent ? 1 : 0, $"DisposeCount=[{string.Join(',', sources.Select(item => item.DisposeCount))},{secondSource.DisposeCount}]"),
                    D21CoreMetrics.Ratio("C19-dispose-queued-released", disposed, sources.Count, $"含排队未开始的源；排队源 DisposeCount={secondSource.DisposeCount}")
                ]);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void DedupLedgerExpiresExactlyAtTheInjectedTtl()
    {
        const int lifetime = 1_000;
        var clock = new ManualClock();
        var channel = new RecordingChannel();
        var trace = new AttachmentTransferTrace();
        var gate = new AttachmentConcurrencyGate();
        var peer = new AutoPeer(channel, "session-1");
        using var coordinator = new AttachmentTransferCoordinator(
            CoordinatorFixture.Identity("target-d21-ttl"),
            channel,
            clock,
            gate: gate,
            trace: trace,
            replayCapacity: 4,
            replayLifetimeMs: lifetime);
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(64));
        Assert.True(coordinator.OpenBatch("batch-ttl", 1, source.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-ttl", "ttl.bin", Bin, source).Ok);
        CoordinatorFixture.Drive(coordinator, peer);
        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.True(coordinator.ReplayCacheSize > 0, "已确认批次必须留下去重台账");

        // 反例读数：时钟不推进时，同一条台账必须仍然在（否则"到期消失"是恒真）。
        var frozenPresent = coordinator.ReplayCacheSize > 0;

        clock.Advance(lifetime - 1);
        var sizeBeforeTtl = coordinator.ReplayCacheSize;
        var liveBefore = sizeBeforeTtl > 0;
        Assert.True(liveBefore, "TTL 前 1ms 不得提前失效");

        clock.Advance(1);
        var sizeAtTtl = coordinator.ReplayCacheSize;
        var goneAt = sizeAtTtl == 0;
        Assert.True(goneAt, $"TTL 到期必须失效，实际 size={sizeAtTtl}");
        Assert.True(frozenPresent, "反例前提：未推进时钟时必须有台账");

        D21CoreMetrics.Write(
            nameof(DedupLedgerExpiresExactlyAtTheInjectedTtl),
            [
                D21CoreMetrics.Row("C10-dedup-ttl-live-before", liveBefore ? 1 : 0, $"注入时钟 +{lifetime - 1}ms 时 size={sizeBeforeTtl}"),
                D21CoreMetrics.Row("C11-dedup-ttl-gone-at", goneAt ? 1 : 0, $"注入时钟 +{lifetime}ms 时 size={sizeAtTtl}")
            ],
            controls:
            [
                D21CoreMetrics.Control(
                    "N5-frozen-clock-keeps-entry",
                    "反例：注入时钟不推进时同一条台账必须仍在（证明 C11 的\"到期消失\"不是恒真）",
                    frozenPresent,
                    $"frozenClock 下 present={frozenPresent}")
            ]);
    }

    [Fact]
    public void ThirtyConsecutiveBatchesShowNoUnboundedGrowth()
    {
        const int rounds = 30;
        using var harness = CoordinatorFixture.NewHarness(replayCapacity: 8);
        var coordinator = harness.Coordinator;
        var sources = new List<CountingByteSource>();
        var series = new List<long>();
        var disposedPerRound = 0;
        for (var round = 0; round < rounds; round += 1)
        {
            var source = new CountingByteSource(CoordinatorFixture.Payload(300));
            sources.Add(source);
            Assert.True(coordinator.OpenBatch($"batch-series-{round}", 1, source.ByteLength).Ok);
            Assert.True(coordinator.AdmitFile($"file-series-{round}", $"s{round}.bin", Bin, source).Ok);
            CoordinatorFixture.Drive(harness);
            Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
            Assert.Equal(1, source.DisposeCount);
            disposedPerRound += 1;
            // 采样点：强制完整 GC（含终结器队列）后连读 3 次取**最小值**，见 SampleManagedHeap。
            series.Add(SampleManagedHeap());
        }

        var analysis = D21CoreMetrics.Analyze(series);
        var slope = analysis["slopeBytesPerRound"]!.GetValue<double>();
        var envelope = analysis["envelope"]!.GetValue<long>();
        // 采样点的适用范围（D21 实测两次打脸后的结论，必须写清楚）：
        //   本用例与其余 540 例**共享**同一个 MTP 进程时，`GC.GetTotalMemory(true)` 的读数里
        //   含别的用例的活动对象与 xUnit 运行态——全量跑实测斜率 131 KB/轮、包络 11.2 MB，
        //   而隔离跑同一序列只有 ~26 KB/轮、0.75 MB。也就是说：**共享进程不是"无增长"的有效
        //   采样点**，它会同时制造假阳（别的用例在涨）与假阴（别的用例在释放）。
        //   因此：紧判据（斜率 ≤ 64 KiB/轮、包络 ≤ 8 MiB，即冻结表 C13/C14）只在
        //   **隔离的 D21 用例集进程**（D21 夹具用 `-trait d21=resources` 起的那次，环境变量
        //   DSH_D21_ISOLATED_HEAP_SAMPLING=1 由夹具注入）里断言；共享全量跑只断言粗界
        //   （斜率 ≤ 512 KiB/轮、包络 ≤ 32 MiB），用来抓"真的失控"，不冒充泄漏判据。
        var isolated = Environment.GetEnvironmentVariable("DSH_D21_ISOLATED_HEAP_SAMPLING") == "1";
        var slopeLimit = isolated ? 64 * 1024 : 512 * 1024;
        var envelopeLimit = isolated ? 8L * 1024 * 1024 : 32L * 1024 * 1024;
        Assert.True(slope <= slopeLimit, $"托管堆斜率过大（isolated={isolated}）：{slope} > {slopeLimit}");
        Assert.True(envelope <= envelopeLimit, $"托管堆包络过大（isolated={isolated}）：{envelope} > {envelopeLimit}");
        Assert.Equal(rounds, disposedPerRound);
        Assert.Equal(rounds, sources.Count(item => item.DisposeCount == 1));
        Assert.True(coordinator.ReplayCacheSize <= 8, $"容量 8 的去重台账不得随轮数增长：size={coordinator.ReplayCacheSize}");
        Assert.Equal(0, coordinator.QueueDepth);
        Assert.Equal(0, coordinator.PendingBytes);

        D21CoreMetrics.Write(
            nameof(ThirtyConsecutiveBatchesShowNoUnboundedGrowth),
            [
                D21CoreMetrics.Row("C12-dedup-capacity-bound", coordinator.ReplayCacheSize, $"注入容量 8；30 轮后 size={coordinator.ReplayCacheSize} evicted={coordinator.ReplayCacheEvicted}"),
                D21CoreMetrics.Row("C13-series-heap-slope", analysis["slopeBytesPerRound"]!.GetValue<double>(), $"30 轮 GC.GetTotalMemory(true) 序列：min={analysis["min"]} max={analysis["max"]}"),
                D21CoreMetrics.Row("C14-series-heap-envelope", analysis["envelope"]!.GetValue<long>(), $"max-min；first={analysis["first"]} last={analysis["last"]}"),
                D21CoreMetrics.Row("C15-series-sources-disposed", disposedPerRound, $"{rounds} 轮每轮 1 个源，全部 DisposeCount=1"),
                D21CoreMetrics.Row("C16-series-replay-cache-size", coordinator.ReplayCacheSize, $"capacity={AttachmentProtocol.ReplayCacheCapacity}（产品常量）")
            ],
            series: new JsonObject
            {
                ["managedHeapBytesPerRound"] = new JsonArray([.. series.Select(value => (JsonNode?)JsonValue.Create(value))]),
                ["samplingPoint"] = "每轮批次 Closed 之后 GC.Collect + WaitForPendingFinalizers + GetTotalMemory(true) 连读 3 次取最小；紧判据只在隔离的 D21 用例集进程里成立（DSH_D21_ISOLATED_HEAP_SAMPLING=1）",
                ["unit"] = "bytes"
            });
    }

    /// <summary>
    /// 托管堆采样：完整 GC（含终结器队列）后连读 3 次取最小值。
    ///
    /// 为什么不是单次读：本用例与其余 540 个用例共享同一个测试进程，`GC.GetTotalMemory(true)`
    /// 的读数里含**别的用例**当时活着的对象与 xUnit 运行态；单次读的包络实测可达 8.7 MB
    /// （隔离运行同一序列只有 0.75 MB）。取 3 次最小是噪声鲁棒估计，且不改变泄漏敏感统计量
    /// （30 点最小二乘斜率）。
    /// </summary>
    private static long SampleManagedHeap()
    {
        var min = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt += 1)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            min = Math.Min(min, GC.GetTotalMemory(forceFullCollection: true));
        }

        return min;
    }

    /// <summary>可计数的字节源：D21 的"所有权恰好一次"判据要读释放次数，不能只读布尔。</summary>
    private sealed class CountingByteSource(byte[] data) : IAttachmentByteSource
    {
        private int _position;

        public int ByteLength { get; } = data.Length;

        public int DisposeCount { get; private set; }

        public int Read(Span<byte> destination)
        {
            var size = Math.Min(destination.Length, data.Length - _position);
            if (size <= 0) return 0;
            data.AsSpan(_position, size).CopyTo(destination);
            _position += size;
            return size;
        }

        public void Dispose() => DisposeCount += 1;
    }
}

/// <summary>
/// D21 指标落盘：只有采样脚本设置了 <c>DSH_D21_CORE_METRICS_DIR</c> 才写文件。
/// 一个用例一个文件（MTP 可能并行执行同一类的用例，集中写一个文件会互相覆盖）。
/// </summary>
internal static class D21CoreMetrics
{
    private static readonly string? Directory =
        Environment.GetEnvironmentVariable("DSH_D21_CORE_METRICS_DIR");

    public static JsonObject Row(string id, double measured, string detail) => new()
    {
        ["id"] = id,
        ["measured"] = measured,
        ["detail"] = detail
    };

    public static JsonObject Ratio(string id, int numerator, int denominator, string detail) => new()
    {
        ["id"] = id,
        ["numerator"] = numerator,
        ["denominator"] = denominator,
        ["detail"] = detail
    };

    public static JsonObject Control(string id, string description, bool ok, string detail) => new()
    {
        ["id"] = id,
        ["description"] = description,
        ["ok"] = ok,
        ["detail"] = detail
    };

    /// <summary>与判据模块同式的序列读数（斜率/包络/极值），Core 侧不依赖 Node。</summary>
    public static JsonObject Analyze(IReadOnlyList<long> series)
    {
        long min = long.MaxValue;
        long max = long.MinValue;
        foreach (var value in series)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        var meanX = (series.Count - 1) / 2.0;
        var meanY = series.Average(value => (double)value);
        double numerator = 0;
        double denominator = 0;
        for (var index = 0; index < series.Count; index += 1)
        {
            numerator += (index - meanX) * (series[index] - meanY);
            denominator += (index - meanX) * (index - meanX);
        }

        return new JsonObject
        {
            ["rounds"] = series.Count,
            ["first"] = series[0],
            ["last"] = series[^1],
            ["min"] = min,
            ["max"] = max,
            ["envelope"] = max - min,
            ["slopeBytesPerRound"] = denominator == 0 ? 0 : numerator / denominator
        };
    }

    public static void Write(
        string caseName,
        IReadOnlyList<JsonObject> rows,
        JsonObject? series = null,
        IReadOnlyList<JsonObject>? controls = null)
    {
        if (string.IsNullOrEmpty(Directory)) return;
        System.IO.Directory.CreateDirectory(Directory);
        var slug = new string([.. caseName.Select(character => char.IsLetterOrDigit(character) ? character : '-')]);
        var payload = new JsonObject
        {
            ["case"] = caseName,
            ["rows"] = new JsonArray([.. rows.Select(row => (JsonNode?)row.DeepClone())]),
            ["controls"] = new JsonArray([.. (controls ?? []).Select(row => (JsonNode?)row.DeepClone())]),
            ["series"] = series?.DeepClone() ?? new JsonObject()
        };
        File.WriteAllText(Path.Combine(Directory, $"{slug}.json"), payload.ToJsonString() + "\n");
    }
}
