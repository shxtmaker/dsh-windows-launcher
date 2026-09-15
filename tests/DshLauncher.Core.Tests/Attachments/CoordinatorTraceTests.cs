using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D11 协议 trace 产物：把协调器的完整决策序列（出/入站报文、状态迁移、判定、
/// 注入时钟毫秒）在两条路径上写成一个确定性 JSON——
/// <c>artifacts/verify-portable/d11-coordinator-trace.json</c>。
///
/// 产物由测试运行器直接生成（无需额外入口）：
/// <code>
/// source /tmp/dsh-env.sh
/// dotnet build DshWindowsLauncher.slnx -c Release --no-restore --warnaserror
/// dotnet run --project tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj -c Release \
///   --no-build --no-restore --no-launch-profile -- -result-xml /tmp/out.xml
/// </code>
/// 本测试同时断言：同一脚本重复生成得到逐字节相同的文本（确定性），
/// 且快乐路径与取消路径都含有报文与状态条目。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class CoordinatorTraceTests
{
    private const int Chunk = AttachmentProtocol.ChunkBytes;

    private static readonly string[] HappyPathFrameTypes =
        ["batch-begin", "file-begin", "chunk", "chunk", "file-end", "file-begin", "chunk", "file-end", "batch-end"];

    private static string ArtifactPath =>
        Path.Combine(WireCorpusRunner.RepoRoot, "artifacts", "verify-portable", "d11-coordinator-trace.json");

    [Fact]
    public void TraceArtifactCoversHappyPathAndCancelPathAndIsDeterministic()
    {
        var first = BuildArtifact();
        var second = BuildArtifact();
        Assert.Equal(first, second);

        var directory = Path.GetDirectoryName(ArtifactPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(ArtifactPath, first);

        Assert.True(File.Exists(ArtifactPath), $"trace 产物未写出：{ArtifactPath}");
        var parsed = JsonNode.Parse(File.ReadAllText(ArtifactPath))!.AsObject();
        Assert.Equal(1, parsed["schemaVersion"]!.GetValue<int>());
        Assert.Equal("D11", parsed["task"]!.GetValue<string>());
        var scenarios = parsed["scenarios"]!.AsArray();
        Assert.Equal(2, scenarios.Count);

        var happy = scenarios[0]!.AsObject();
        Assert.Equal("happy-path", happy["name"]!.GetValue<string>());
        Assert.Equal("batch-trace-happy", happy["batchId"]!.GetValue<string>());
        var happyEntries = happy["trace"]!["entries"]!.AsArray();
        Assert.Equal(
            HappyPathFrameTypes,
            happyEntries
                .Where(entry => entry!["kind"]!.GetValue<string>() == "message-out")
                .Select(entry => entry!["type"]!.GetValue<string>()));

        var cancel = scenarios[1]!.AsObject();
        Assert.Equal("cancel-path", cancel["name"]!.GetValue<string>());
        Assert.Equal("batch-trace-cancel", cancel["batchId"]!.GetValue<string>());

        foreach (var scenario in scenarios)
        {
            var entries = scenario!["trace"]!["entries"]!.AsArray();
            Assert.True(entries.Count > 0);
            var kinds = entries.Select(entry => entry!["kind"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("message-out", kinds);
            Assert.Contains("message-in", kinds);
            Assert.Contains("state", kinds);
            Assert.Contains("decision", kinds);
            Assert.All(entries, entry => Assert.NotNull(entry!["atMs"]));
        }

        // 取消路径必须留下"迟到结果被拒"的证据
        Assert.Contains(
            cancel["trace"]!["entries"]!.AsArray(),
            entry => entry!["detail"]!.GetValue<string>().Contains("拒绝 cancelled", StringComparison.Ordinal));
        Assert.Equal(0, cancel["bounds"]!["importInvocations"]!.GetValue<int>());
        Assert.Equal(2, happy["bounds"]!["importInvocations"]!.GetValue<int>());
    }

    private static string BuildArtifact()
    {
        var artifact = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["task"] = "D11",
            ["generatedBy"] = "DshLauncher.Core.Tests.Attachments.CoordinatorTraceTests",
            ["clock"] = "injected ManualClock（无 sleep、无系统时间）",
            ["reproduce"] =
                "source /tmp/dsh-env.sh && dotnet build DshWindowsLauncher.slnx -c Release --no-restore --warnaserror && "
                + "dotnet run --project tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj -c Release --no-build --no-restore --no-launch-profile -- -result-xml /tmp/out.xml",
            ["scenarios"] = new JsonArray(BuildHappyPath(), BuildCancelPath()),
        };
        return AttachmentTransferTrace.Serialize(artifact) + "\n";
    }

    /// <summary>快乐路径：两个文件、三种块大小边界（整块 / 尾块不足 / 单字节），全部 staged 并关闭批次。</summary>
    private static JsonObject BuildHappyPath()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var first = new ScriptedByteSource(CoordinatorFixture.Payload(Chunk + 17));
        var second = new ScriptedByteSource(CoordinatorFixture.Payload(1));
        Assert.True(coordinator.OpenBatch("batch-trace-happy", 2, first.ByteLength + second.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "photo.png", "image/png", first).Ok);
        Assert.True(coordinator.AdmitFile("file-2", "notes.txt", "text/plain", second).Ok);

        var maxPending = 0;
        var maxInFlight = 0;
        var maxQueue = 0;
        var maxScratch = 0;
        for (var step = 0; step < 100 && coordinator.BatchPhase == AttachmentBatchPhase.Open; step += 1)
        {
            coordinator.Pump();
            harness.Peer.Respond();
            maxPending = Math.Max(maxPending, coordinator.PendingBytes);
            maxInFlight = Math.Max(maxInFlight, coordinator.PendingChunks);
            maxQueue = Math.Max(maxQueue, coordinator.QueueDepth);
            maxScratch = Math.Max(maxScratch, coordinator.ScratchBytes);
        }

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        return Scenario(
            "happy-path",
            "batch-trace-happy",
            harness,
            maxPending,
            maxInFlight,
            maxQueue,
            maxScratch);
    }

    /// <summary>取消路径：两块在途时取消，随后注入迟到的 ack 与 import-result，全部被拒。</summary>
    private static JsonObject BuildCancelPath()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var first = new ScriptedByteSource(CoordinatorFixture.Payload(3 * Chunk));
        var second = new ScriptedByteSource(CoordinatorFixture.Payload(64));
        Assert.True(coordinator.OpenBatch("batch-trace-cancel", 2, first.ByteLength + second.ByteLength).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "video.bin", "application/octet-stream", first).Ok);
        Assert.True(coordinator.AdmitFile("file-2", "notes.txt", "text/plain", second).Ok);
        harness.Peer.AutoAck = false;

        coordinator.Pump();
        var maxPending = coordinator.PendingBytes;
        var maxInFlight = coordinator.PendingChunks;
        var maxQueue = coordinator.QueueDepth;
        var maxScratch = coordinator.ScratchBytes;

        Assert.True(coordinator.Cancel().Ok);
        harness.Channel.Deliver(PeerWire.Ack("session-1", "batch-trace-cancel", "file-1", 0, 0, Chunk));
        harness.Channel.Deliver(PeerWire.ImportResult("session-1", "batch-trace-cancel", "file-1", "staged", ["att-file-1"]));
        var report = coordinator.Pump();
        Assert.Equal(2, report.Rejections.Count);

        Assert.Equal(AttachmentBatchPhase.Cancelled, coordinator.BatchPhase);
        return Scenario(
            "cancel-path",
            "batch-trace-cancel",
            harness,
            maxPending,
            maxInFlight,
            maxQueue,
            maxScratch);
    }

    private static JsonObject Scenario(
        string name,
        string batchId,
        Harness harness,
        int maxPendingBytes,
        int maxInFlightChunks,
        int maxQueueDepth,
        int maxScratchBytes)
    {
        var coordinator = harness.Coordinator;
        var identity = coordinator.Identity;
        return new JsonObject
        {
            ["name"] = name,
            ["batchId"] = batchId,
            ["identity"] = new JsonObject
            {
                ["sessionId"] = identity.SessionId,
                ["targetId"] = identity.TargetId,
                ["documentEpoch"] = identity.DocumentEpoch,
                ["composerEpoch"] = identity.ComposerEpoch,
                ["composerScope"] = identity.ComposerScope,
            },
            ["batchPhase"] = coordinator.BatchPhase.ToString(),
            ["bounds"] = new JsonObject
            {
                ["maxPendingBytes"] = AttachmentTransferCoordinator.MaxPendingBytes,
                ["observedMaxPendingBytes"] = maxPendingBytes,
                ["maxChunksInFlight"] = AttachmentProtocol.MaxChunksInFlight,
                ["observedMaxChunksInFlight"] = maxInFlightChunks,
                ["queueCapacity"] = coordinator.QueueCapacity,
                ["observedMaxQueueDepth"] = maxQueueDepth,
                ["maxScratchBytes"] = AttachmentProtocol.ChunkBytes,
                ["observedMaxScratchBytes"] = maxScratchBytes,
                ["replayCacheCapacity"] = AttachmentProtocol.ReplayCacheCapacity,
                ["replayCacheSize"] = coordinator.ReplayCacheSize,
                ["replayCachePinned"] = coordinator.ReplayCachePinned,
                ["replayCacheEvicted"] = coordinator.ReplayCacheEvicted,
                ["outboundFrames"] = coordinator.OutboundFrames,
                ["inboundFrames"] = coordinator.InboundFrames,
                ["rejectedFrames"] = coordinator.RejectedFrames,
                ["importInvocations"] = coordinator.ImportInvocations,
            },
            ["files"] = new JsonArray([.. coordinator.Files.Select(FileJson)]),
            ["trace"] = coordinator.Trace.ToJson(),
        };
    }

    private static JsonNode FileJson(AttachmentTransferFileRecord record) => new JsonObject
    {
        ["fileId"] = record.FileId,
        ["phase"] = record.Phase.ToString(),
        ["resultStatus"] = record.ResultStatus,
        ["code"] = record.Code,
        ["sentBytes"] = record.SentBytes,
        ["ackedBytes"] = record.AckedBytes,
        ["inFlightChunks"] = record.InFlightChunks,
        ["importInvoked"] = record.ImportInvoked,
        ["sourceDisposed"] = record.SourceDisposed,
        ["attachmentIds"] = new JsonArray([.. record.AttachmentIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
    };
}
