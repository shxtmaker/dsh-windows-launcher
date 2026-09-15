using System.Globalization;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Tests.Attachments;

namespace DshLauncher.Core.Tests.Interop;

/// <summary>一个待发送文件：真实磁盘文件 + 确定性内容 + 期望摘要。</summary>
internal sealed record InteropFileSpec(string FileId, string Name, string Mime, string Path, int ByteLength, string Sha256);

/// <summary>互通场景的四种形态（三种是负向探针）。</summary>
internal enum InteropScenarioKind
{
    /// <summary>正常批次：两个文件、三块、全部 staged。</summary>
    ProductionBatch,

    /// <summary>负向：边界上篡改一个字节 ⇒ 接收端必须报 hash-mismatch 且不导入。</summary>
    TamperedChunk,

    /// <summary>负向：对端回一条 batchId 不属于本批次的 import-result ⇒ 生产协调器必须拒绝。</summary>
    WrongBatchId,

    /// <summary>负向：传输中途强杀 Node 子进程 ⇒ 判据必须响亮失败，不能静默通过。</summary>
    KilledChild,
}

/// <summary>一次互通运行的全部观测（供用例断言与证据记录）。</summary>
internal sealed class InteropRun : IDisposable
{
    public required string CaseId { get; init; }

    public required InteropScenarioKind Kind { get; init; }

    public required InteropDriver Driver { get; init; }

    public required InteropChannel Channel { get; init; }

    public required AttachmentTransferCoordinator Coordinator { get; init; }

    public required ManualClock Clock { get; init; }

    public required AttachmentTransferTrace ProductionTrace { get; init; }

    public required AttachmentIdentity Identity { get; init; }

    public required string BatchId { get; init; }

    public required IReadOnlyList<InteropFileSpec> Files { get; init; }

    /// <summary>本次准入的字节源（协调器已释放；此处只读其读次数，证明短读累计确实发生）。</summary>
    public required IReadOnlyList<FileByteSource> Sources { get; init; }

    public required IReadOnlyList<AttachmentPumpReport> Pumps { get; init; }

    public required bool DriverFinished { get; init; }

    public required bool DriverKilledByProbe { get; init; }

    public required InteropTraceFile Trace { get; init; }

    public required string ProductionTracePath { get; init; }

    public int ChunksSent => Pumps.Sum(report => report.ChunksSent);

    public int InboundFrames => Pumps.Sum(report => report.Received);

    public IEnumerable<string> Rejections => Pumps.SelectMany(report => report.Rejections);

    /// <summary>按 seq 顺序把驱动 trace 里"确实过了边界"的 chunk 载荷拼回原始字节。</summary>
    public byte[] ReassembledBytes(string fileId)
    {
        var chunks = Trace.Frames("c2d", "chunk")
            .Where(entry => string.Equals(entry["fileId"]?.GetValue<string>(), fileId, StringComparison.Ordinal))
            .OrderBy(entry => entry["seq"]!.GetValue<int>())
            .ToList();
        using var buffer = new MemoryStream();
        foreach (var chunk in chunks)
        {
            var payload = Convert.FromBase64String(chunk["frame"]!["dataBase64"]!.GetValue<string>());
            buffer.Write(payload);
        }

        return buffer.ToArray();
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        Driver.Dispose();
    }
}

/// <summary>
/// 把"生产 C# 发送端 ↔ 真实浏览器接收端"的一次完整互通跑起来。
///
/// 生产代码占比（这是本任务的核心约束）：
///   - 出站：<see cref="AttachmentTransferCoordinator"/>（分块、2 块在途背压、seq/offset、
///     SHA-256、批次状态机、D10 镜像预校验、去重缓存）；
///   - 入站：同一协调器的 <c>Pump</c>（ack 幂等、import-result 校验、超时策略走注入时钟）；
///   - 编解码：<see cref="AttachmentCodec"/>。
/// 只有三处是"边界/输入"替身：stdio 测试通道（替代 WebView2 物理消息边界）、
/// <see cref="FileByteSource"/>（替代 Windows 暂存句柄，保持 IAttachmentByteSource 契约）、
/// 以及 context 握手帧的投递（真实承载层在 hello/capabilities 之后回执 context）。
/// </summary>
internal static class InteropScenario
{
    public static readonly TimeSpan PumpDeadline = TimeSpan.FromSeconds(120);

    public static InteropRun Run(ProductionInteropFixture fixture, InteropScenarioKind kind, string caseId)
    {
        var probe = kind == InteropScenarioKind.WrongBatchId ? "wrong-batch-id" : "none";
        var driver = InteropDriver.Start(fixture, caseId, probe, TimeSpan.FromSeconds(180));
        try
        {
            var sessionId = driver.PageSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException($"驱动握手未给出页面会话 id：{driver.PageHandshake?.ToJsonString()}");
            }

            var identity = new AttachmentIdentity(sessionId, $"d18-{caseId}", 11, 22, $"d18-scope-{caseId}");
            var channel = new InteropChannel(driver);
            if (kind == InteropScenarioKind.TamperedChunk) channel.TamperNextChunkPayload = true;

            var clock = new ManualClock();
            var productionTrace = new AttachmentTransferTrace();
            var coordinator = new AttachmentTransferCoordinator(identity, channel, clock, trace: productionTrace);

            // 边界交付 context：生产协调器不发送握手帧，真实 WebView2 承载层负责投递；
            // 帧本身仍由生产 codec 校验并规范化（测试不手写"看起来像"的报文）。
            channel.SendBoundaryFrame(InteropWire.Context(identity));

            var files = CreateFiles(fixture, kind, caseId);
            var batchId = $"d18-{caseId}-batch";
            var totalBytes = files.Sum(file => file.ByteLength);

            var opened = coordinator.OpenBatch(batchId, files.Count, totalBytes);
            if (!opened.Ok)
            {
                throw new InvalidOperationException($"batch-begin 未被生产协调器接受：{opened.Code} {opened.Detail}");
            }

            var sources = new List<FileByteSource>();
            foreach (var file in files)
            {
                // 短读源：协调器必须自己累计到满块（96 KiB 上限 ⇒ 266240B 至少要读 3 次）。
                var source = new FileByteSource(file.Path);
                var admitted = coordinator.AdmitFile(file.FileId, file.Name, file.Mime, source, file.Sha256);
                if (!admitted.Ok)
                {
                    throw new InvalidOperationException($"AdmitFile({file.FileId}) 失败：{admitted.Code} {admitted.Detail}");
                }

                sources.Add(source);
            }

            var pumps = new List<AttachmentPumpReport>();
            var deadline = DateTime.UtcNow + PumpDeadline;
            var killed = false;
            var clockAdvanced = false;
            while (coordinator.BatchPhase == AttachmentBatchPhase.Open &&
                !driver.HasExited &&
                DateTime.UtcNow < deadline)
            {
                var report = coordinator.Pump();
                pumps.Add(report);

                if (kind == InteropScenarioKind.KilledChild && !killed && report.ChunksSent > 0)
                {
                    // 负向探针：传输一开始就强杀子进程（模拟浏览器崩溃/承载层断开）。
                    driver.Kill();
                    killed = true;
                    continue;
                }

                if (kind == InteropScenarioKind.TamperedChunk && !clockAdvanced &&
                    driver.Rejections.Any(rejection => string.Equals(rejection["code"]?.GetValue<string>(), "hash-mismatch", StringComparison.Ordinal)))
                {
                    // 生产协调器不会凭空失败：它按注入时钟的 import 超时结束该文件（不睡眠、不轮询）。
                    clock.Advance(AttachmentProtocol.FileEndTimeoutMs + 1);
                    clockAdvanced = true;
                    continue;
                }

                if (report.Sent == 0 && report.Received == 0)
                {
                    driver.WaitForInbound(TimeSpan.FromMilliseconds(200));
                }
            }

            var finished = driver.Finish(TimeSpan.FromSeconds(30));
            var productionTracePath = Path.Combine(fixture.TraceDirectory, $"{caseId}.csharp-trace.json");
            File.WriteAllText(
                productionTracePath,
                AttachmentTransferTrace.Serialize(productionTrace.ToJson()) + "\n");

            return new InteropRun
            {
                CaseId = caseId,
                Kind = kind,
                Driver = driver,
                Channel = channel,
                Coordinator = coordinator,
                Clock = clock,
                ProductionTrace = productionTrace,
                Identity = identity,
                BatchId = batchId,
                Files = files,
                Sources = sources,
                Pumps = pumps,
                DriverFinished = finished,
                DriverKilledByProbe = killed,
                Trace = InteropTraceFile.Read(driver.TracePath),
                ProductionTracePath = productionTracePath,
            };
        }
        catch
        {
            driver.Dispose();
            throw;
        }
    }

    /// <summary>把确定性素材写成真实磁盘文件（字节源是注入端口，不是发送算法）。</summary>
    private static List<InteropFileSpec> CreateFiles(ProductionInteropFixture fixture, InteropScenarioKind kind, string caseId)
    {
        var specs = new List<InteropFileSpec>();
        switch (kind)
        {
            case InteropScenarioKind.ProductionBatch:
            case InteropScenarioKind.WrongBatchId:
                specs.Add(new InteropFileSpec("alpha", "d18-interop-alpha.bin", "application/octet-stream", string.Empty, 0, string.Empty));
                specs.Add(new InteropFileSpec("beta", "d18-interop-beta.txt", "text/plain", string.Empty, 0, string.Empty));
                break;
            case InteropScenarioKind.TamperedChunk:
                specs.Add(new InteropFileSpec("tamper", "d18-interop-tamper.bin", "application/octet-stream", string.Empty, 0, string.Empty));
                break;
            case InteropScenarioKind.KilledChild:
                specs.Add(new InteropFileSpec("killed", "d18-interop-killed.bin", "application/octet-stream", string.Empty, 0, string.Empty));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知场景");
        }

        var directory = Path.Combine(fixture.TraceDirectory, "payloads");
        Directory.CreateDirectory(directory);
        var result = new List<InteropFileSpec>();
        foreach (var spec in specs)
        {
            // alpha 覆盖"满块 + 尾块"（256 KiB + 4 KiB）；其余为单块小文件。
            var size = string.Equals(spec.FileId, "alpha", StringComparison.Ordinal)
                ? (256 * 1024) + 4096
                : 5 * 1024 + 321;
            var bytes = InteropBytes.MakeText(size, $"DSH-D18-INTEROP-{spec.FileId.ToUpperInvariant()}-{caseId}");
            var path = Path.Combine(directory, $"{caseId}-{spec.Name}");
            File.WriteAllBytes(path, bytes);
            result.Add(spec with { Path = path, ByteLength = bytes.Length, Sha256 = InteropBytes.Sha256Hex(bytes) });
        }

        return result;
    }

    /// <summary>驱动 trace 里每个 chunk 的载荷摘要（用于证明"边界上的字节确实被改过"）。</summary>
    public static string PayloadSha256OfTrace(JsonObject chunkEntry) =>
        chunkEntry["payloadSha256"]?.GetValue<string>() ?? string.Empty;
}

/// <summary>可复用的判据实现：正常路径与负向探针用**同一个**判定函数，避免"探针另写一套"。</summary>
internal static class InteropChecks
{
    /// <summary>子进程是否干净退出：正常路径要求 true，被强杀的探针要求 false。</summary>
    public static (bool Ok, string Detail) SubprocessExitedCleanly(InteropRun run)
    {
        var driver = run.Driver;
        var exitCode = driver.ExitCode;
        var clean = run.DriverFinished && !driver.KillRequested && exitCode == 0;
        return (clean,
            $"finished={run.DriverFinished} killRequested={driver.KillRequested} exitCode={exitCode?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
            $"done={driver.DoneRecord?["exitReason"]?.GetValue<string>() ?? "(none)"}");
    }

    /// <summary>本次运行的进程/句柄是否回收干净（按用例 id 精确定位，不误伤其它进程）。</summary>
    public static (bool Ok, string Detail) ResourcesReclaimed(ProductionInteropFixture fixture, InteropRun run)
    {
        var processes = InteropProcess.Run(
            "bash",
            ["-lc", $"pgrep -af '{run.CaseId}' || true"],
            ProductionInteropFixture.RepoRoot);
        var leftovers = processes.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("pgrep", StringComparison.Ordinal))
            .ToList();
        var handlesOk = true;
        var detail = new List<string>();
        foreach (var path in new[] { run.Driver.TracePath, run.Driver.TranscriptPath, run.ProductionTracePath })
        {
            try
            {
                // 独占打开：本进程没有残留句柄，文件也已完整落盘。
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                detail.Add($"{Path.GetFileName(path)}={stream.Length}B");
            }
            catch (IOException error)
            {
                handlesOk = false;
                detail.Add($"{Path.GetFileName(path)} 独占打开失败：{error.Message}");
            }
        }

        var ok = leftovers.Count == 0 && handlesOk && run.Driver.HasExited;
        return (ok, $"hasExited={run.Driver.HasExited} 残留=[{string.Join(" | ", leftovers)}] 句柄：{string.Join("；", detail)}");
    }

}
