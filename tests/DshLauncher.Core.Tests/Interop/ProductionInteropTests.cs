using System.Globalization;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Interop;

/// <summary>
/// D18 生产互通用例（Interop 用例集）。
///
/// 与同程序集的 Core 用例集的差别（manifest 里显式区分）：
///   - 需要 Node 22+、真实 dsh CLI、真实 Chromium 与真实夹具（私有 DSH_HOME + 两个 profile）；
///   - 需要先跑 `tests/interop/ensure-fixture.sh` 才能保证夹具装的是**当前** tarball；
///   - 因此它们不进 Core profile，只在 Development 门禁的 `production-interop-l13` 里执行
///     （见 eng/verification-profiles.json 的 productionInterop 段与 eng/expected-test-dataset.json 的 caseSets）。
///
/// 反向约束：本类**不允许**跳过。任何原因（缺 Chromium、缺夹具、子进程异常）都必须让用例
/// 响亮失败，并在证据里留下确定原因，绝不静默通过。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
[Trait("interop", "production")]
public sealed class ProductionInteropTests(ProductionInteropFixture fixture) : IClassFixture<ProductionInteropFixture>
{
    /// <summary>登记一条门禁：不通过就直接让用例失败（绝不留绿灯）。</summary>
    private void Gate(string id, string description, bool ok, string detail)
    {
        fixture.Ledger.Gate(id, description, ok, detail);
        fixture.Ledger.Case($"{id}: {(ok ? "pass" : "FAIL")} — {detail}");
        Assert.True(ok, $"{id}：{description} —— {detail}");
    }

    private void Probe(string id, string description, bool ok, string detail)
    {
        fixture.Ledger.Probe(id, description, ok, detail);
        fixture.Ledger.Case($"{id}: {(ok ? "rejected" : "NOT-REJECTED")} — {detail}");
        Assert.True(ok, $"{id}：{description} —— {detail}");
    }

    // ————————————————————————————————————————————————————————————
    // 1) 正向：真实 C# 生产发送端 → 真实浏览器生产接收端 → ACK + import-result 回到同一 Core
    // ————————————————————————————————————————————————————————————

    [Fact]
    public void InteropSendsBatchThroughRealBrowserReceiverAndReturnsAckAndImportResult()
    {
        const string caseId = "production-batch";
        InteropRun? run = null;
        try
        {
            run = InteropScenario.Run(fixture, InteropScenarioKind.ProductionBatch, caseId);

            // ---- 页面确实是生产接收端（不是测试替身） ----
            var handshake = run.Driver.PageHandshake!;
            var wiring = handshake["wiring"]!;
            var pageOk =
                handshake["receiverVersion"]?.GetValue<int>() == 1 &&
                handshake["bridgeVersion"]?.GetValue<int>() == 1 &&
                handshake["isSecureContext"]?.GetValue<bool>() == false &&
                string.Equals(handshake["hashBackend"]?.GetValue<string>(), "pure-js", StringComparison.Ordinal) &&
                wiring["hasOrigin"]?.GetValue<bool>() == true &&
                wiring["hasCurrentSession"]?.GetValue<bool>() == true &&
                wiring["hasCapability"]?.GetValue<bool>() == true &&
                string.Equals(handshake["sessionId"]?.GetValue<string>(), run.Identity.SessionId, StringComparison.Ordinal) &&
                handshake["initialDraftCount"] is not null;
            Gate(
                "interop-page-production-receiver",
                "报文进入的是真实 Chromium 里已构建插件的生产接收端（版本 1、组合依赖齐全），身份会话与协调器一致",
                pageOk,
                $"receiverVersion={handshake["receiverVersion"]} bridge={handshake["bridgeVersion"]} secureContext={handshake["isSecureContext"]} " +
                $"hashBackend={handshake["hashBackend"]} wiring={wiring.ToJsonString()} session={handshake["sessionId"]} 初始草稿={handshake["initialDraftCount"]}");

            // ---- 双向传输：帧数与内容都对得上（不匹配就让用例失败，而不是只记一笔） ----
            var expectedChunks = run.ChunksSent;
            var expectedC2d = 1 + 1 + run.Files.Count + expectedChunks + run.Files.Count + 1; // context/batch-begin/file-begin/chunk/file-end/batch-end
            var expectedD2c = expectedChunks + run.Files.Count; // ack / import-result
            var c2dFrames = run.Trace.OfDirection("c2d").Count(entry => string.Equals(entry["kind"]?.GetValue<string>(), "frame", StringComparison.Ordinal));
            var d2cFrames = run.Trace.OfDirection("d2c").Count(entry => string.Equals(entry["kind"]?.GetValue<string>(), "frame", StringComparison.Ordinal));
            var codecMismatches = run.Trace.OfDirection("d2c")
                .Where(entry => string.Equals(entry["kind"]?.GetValue<string>(), "frame", StringComparison.Ordinal))
                .Count(entry => entry["codecOk"]?.GetValue<bool>() != true);
            var transportOk =
                c2dFrames == expectedC2d &&
                d2cFrames == expectedD2c &&
                run.Driver.SentFrameCount == expectedC2d &&
                run.Driver.ReceivedFrameCount == expectedD2c &&
                codecMismatches == 0 &&
                run.Channel.SendFailures == 0 &&
                run.Channel.BoundaryFrames == 1;
            Gate(
                "interop-transport-two-way",
                "stdio 双向传输的帧数逐条可对账，且每条对端帧都通过 TS 生产 codec 复核",
                transportOk,
                $"c2d={c2dFrames}/{expectedC2d} d2c={d2cFrames}/{expectedD2c} 驱动计数={run.Driver.SentFrameCount}/{run.Driver.ReceivedFrameCount} " +
                $"TS codec 不合法={codecMismatches} 发送失败={run.Channel.SendFailures} 边界握手帧={run.Channel.BoundaryFrames}");

            // ---- 边界上的字节保真：把 trace 里的 chunk 载荷拼回原始字节 ----
            var fidelity = new List<string>();
            var fidelityOk = true;
            foreach (var file in run.Files)
            {
                var reassembled = run.ReassembledBytes(file.FileId);
                var source = File.ReadAllBytes(file.Path);
                var same = reassembled.Length == source.Length && InteropBytes.Sha256Hex(reassembled) == file.Sha256;
                fidelityOk &= same;
                fidelity.Add($"{file.FileId}:{reassembled.Length}B/{file.Sha256[..16]}…{(same ? "OK" : "MISMATCH")}");
            }

            var readDetail = run.Sources
                .Select(source => $"{source.ByteLength}B/{source.ReadCount}次读")
                .ToList();
            Gate(
                "interop-chunk-bytes-fidelity",
                "驱动 trace 里跨过 stdio 的 chunk 载荷按 seq 拼回后与真实源文件逐字节一致（长度 + SHA-256）；字节源为短读源，累计由生产代码完成",
                fidelityOk,
                string.Join(" | ", fidelity) + " ‖ 短读： " + string.Join("，", readDetail));

            // ---- 浏览器里组装出的真实 File ----
            var assembledEntries = run.Trace.OfKind("assembled").ToList();
            var assembledOk = assembledEntries.Count == run.Files.Count;
            var assembledDetail = new List<string>();
            foreach (var file in run.Files)
            {
                var entry = assembledEntries.FirstOrDefault(item =>
                    string.Equals(item["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal));
                if (entry is null)
                {
                    assembledOk = false;
                    assembledDetail.Add($"{file.FileId}:缺少组装记录");
                    continue;
                }

                var ok =
                    string.Equals(entry["name"]?.GetValue<string>(), file.Name, StringComparison.Ordinal) &&
                    string.Equals(entry["mime"]?.GetValue<string>(), file.Mime, StringComparison.Ordinal) &&
                    entry["byteLength"]?.GetValue<int>() == file.ByteLength &&
                    string.Equals(entry["sha256"]?.GetValue<string>(), file.Sha256, StringComparison.Ordinal) &&
                    entry["isFile"]?.GetValue<bool>() == true &&
                    entry["isBlob"]?.GetValue<bool>() == true;
                assembledOk &= ok;
                assembledDetail.Add($"{file.Name}={entry["byteLength"]}B type={entry["mime"]} sha={Short(entry["sha256"]?.GetValue<string>())} isFile={entry["isFile"]}");
            }

            Gate(
                "interop-browser-assembled-file",
                "浏览器里组装出的是真实 File/Blob，名称/类型/长度正确，且页面回读字节由 Node 独立计算的 SHA-256 等于源哈希",
                assembledOk,
                string.Join(" | ", assembledDetail));

            // ---- 哈希跨边界一致：协调器算的、file-end 声明的、接收端核对的、Node 回读的都是同一个 ----
            var hashOk = true;
            var hashDetail = new List<string>();
            foreach (var file in run.Files)
            {
                var endFrame = run.Trace.Frames("c2d", "file-end")
                    .FirstOrDefault(entry => string.Equals(entry["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal));
                var assembled = assembledEntries.FirstOrDefault(item =>
                    string.Equals(item["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal));
                var declared = endFrame?["sha256"]?.GetValue<string>();
                var receiverVerified = assembled?["declaredSha256"]?.GetValue<string>();
                var record = run.Coordinator.FileRecord(file.FileId)!;
                var ok = string.Equals(declared, file.Sha256, StringComparison.Ordinal) &&
                    string.Equals(receiverVerified, file.Sha256, StringComparison.Ordinal) &&
                    string.Equals(InteropBytes.Sha256Hex(run.ReassembledBytes(file.FileId)), file.Sha256, StringComparison.Ordinal) &&
                    string.Equals(record.ResultStatus, "staged", StringComparison.Ordinal) &&
                    record.Code is null;
                hashOk &= ok;
                hashDetail.Add($"{file.FileId}:file-end={Short(declared)} 接收端核对={Short(receiverVerified)} 记录={record.ResultStatus}");
            }

            Gate(
                "interop-hash-cross-boundary",
                "C# 生产协调器计算的 SHA-256、file-end 声明值、接收端实际核对值与 Node 回读哈希四处一致，且文件记录为 staged 无错误码",
                hashOk,
                string.Join(" | ", hashDetail));

            // ---- 批次/文件 ID 跨边界一致 ----
            // context 是边界握手帧（协议上不带 batchId），其余每一帧都必须属于本批次。
            var nonHandshake = run.Trace.OfDirection("c2d")
                .Where(entry => !string.Equals(entry["type"]?.GetValue<string>(), "context", StringComparison.Ordinal))
                .ToList();
            var wrongBatch = nonHandshake.Count(entry => !string.Equals(entry["batchId"]?.GetValue<string>(), run.BatchId, StringComparison.Ordinal));
            var fileIds = run.Files.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
            var seenFileIds = run.Trace.Entries
                .Select(entry => entry["fileId"]?.GetValue<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);
            var recordIds = run.Coordinator.Files.Select(record => record.FileId).ToHashSet(StringComparer.Ordinal);
            var idsOk =
                wrongBatch == 0 &&
                run.Coordinator.BatchId == run.BatchId &&
                run.Coordinator.BatchPhase == AttachmentBatchPhase.Closed &&
                recordIds.SetEquals(fileIds) &&
                fileIds.IsSubsetOf(seenFileIds) &&
                run.Files.All(file => File.Exists(file.Path));
            Gate(
                "interop-batch-file-ids",
                "批次与文件 ID 在 C# 协调器、每一帧、浏览器接收端与最终批次状态之间完全一致",
                idsOk,
                $"batchId={run.BatchId} 不一致帧={wrongBatch} 协调器批次={run.Coordinator.BatchId}/{run.Coordinator.BatchPhase} 文件集=[{string.Join(",", fileIds.Order(StringComparer.Ordinal))}]");

            // ---- ACK 回到 Core：逐块确认，背压窗口归零 ----
            var ackFrames = run.Trace.Frames("d2c", "ack").ToList();
            var ackOk = ackFrames.Count == expectedChunks && run.Coordinator.PendingBytes == 0 && run.Coordinator.PendingChunks == 0;
            var ackDetail = new List<string>();
            foreach (var file in run.Files)
            {
                var record = run.Coordinator.FileRecord(file.FileId)!;
                var fileAcks = ackFrames
                    .Where(entry => string.Equals(entry["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal))
                    .OrderBy(entry => entry["seq"]!.GetValue<int>())
                    .ToList();
                var expectedSeqs = (file.ByteLength + AttachmentProtocol.ChunkBytes - 1) / AttachmentProtocol.ChunkBytes;
                var contiguous = fileAcks.Count == expectedSeqs &&
                    fileAcks.Select((entry, index) => entry["seq"]!.GetValue<int>() == index).All(flag => flag) &&
                    fileAcks.Select(entry => entry["byteLength"]!.GetValue<int>()).Sum() == file.ByteLength &&
                    fileAcks.Select(entry => entry["offset"]!.GetValue<int>()).Distinct().Count() == expectedSeqs;
                ackOk &= contiguous && record.AckedBytes == file.ByteLength && record.SentBytes == file.ByteLength && record.InFlightChunks == 0;
                ackDetail.Add($"{file.FileId}:ack={fileAcks.Count} acked={record.AckedBytes}/{file.ByteLength} inFlight={record.InFlightChunks}");
            }

            Gate(
                "interop-ack-returned",
                "浏览器接收端产出的 ACK 逐块回到同一 Core：序号连续、偏移/长度正确、已确认字节等于文件长度、在途窗口归零",
                ackOk,
                $"acks={ackFrames.Count} pendingBytes={run.Coordinator.PendingBytes} pendingChunks={run.Coordinator.PendingChunks} | {string.Join(" | ", ackDetail)}");

            // ---- import-result 回到 Core ----
            var importFrames = run.Trace.Frames("d2c", "import-result").ToList();
            var importOk = importFrames.Count == run.Files.Count &&
                run.InboundFrames == expectedD2c &&
                run.Coordinator.ImportInvocations == run.Files.Count &&
                !run.Rejections.Any();
            var importDetail = new List<string>();
            foreach (var file in run.Files)
            {
                var frame = importFrames.FirstOrDefault(entry => string.Equals(entry["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal));
                var ok = frame is not null &&
                    string.Equals(frame["batchId"]?.GetValue<string>(), run.BatchId, StringComparison.Ordinal) &&
                    string.Equals(frame["status"]?.GetValue<string>(), "staged", StringComparison.Ordinal);
                importOk &= ok;
                importDetail.Add($"{file.FileId}:status={frame?["status"]} ids={frame?["attachmentIds"]?.ToJsonString() ?? "(none)"}");
            }

            Gate(
                "interop-import-result-returned",
                "每个文件的 import-result 都回到同一 Core，batchId/fileId 正确、状态为 staged，且协调器没有任何入站拒绝",
                importOk,
                $"imports={importFrames.Count}/{run.Files.Count} 入站帧={run.InboundFrames}/{expectedD2c} 导入调用={run.Coordinator.ImportInvocations} 拒绝=[{string.Join(",", run.Rejections)}] | {string.Join(" | ", importDetail)}");

            // ---- 原生草稿真的拿到了文件（attachmentIds 来自原生输入状态） ----
            var draftIds = new List<string>();
            var draftOk = true;
            foreach (var file in run.Files)
            {
                var record = run.Coordinator.FileRecord(file.FileId)!;
                var frame = importFrames.First(entry => string.Equals(entry["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal));
                var frameIds = frame["attachmentIds"]?.AsArray().Select(item => item!.GetValue<string>()).ToList() ?? [];
                var assembled = assembledEntries.First(entry => string.Equals(entry["fileId"]?.GetValue<string>(), file.FileId, StringComparison.Ordinal));
                var draftAdded = assembled["draftAdded"]?.AsArray().Select(item => item!.GetValue<string>()).ToList() ?? [];
                var previous = assembled["draftPrevious"]?.AsArray().Select(item => item!.GetValue<string>()).ToList() ?? [];
                var ok = record.AttachmentIds.Count == 1 &&
                    record.AttachmentIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
                    record.AttachmentIds.SequenceEqual(frameIds.Order(StringComparer.Ordinal)) &&
                    record.AttachmentIds.SequenceEqual(draftAdded.Order(StringComparer.Ordinal)) &&
                    previous.Count >= 1;
                draftOk &= ok;
                draftIds.AddRange(record.AttachmentIds);
                fixture.Ledger.Case($"{caseId}/{file.FileId} attachmentIds={string.Join(",", record.AttachmentIds)}");
            }

            var initialProbe = run.Trace.OfKind("page-ready").FirstOrDefault();
            var finalProbe = run.Trace.OfKind("draft-final").FirstOrDefault();
            var initialCount = initialProbe?["initialProbe"]?["previous"]?.AsArray().Count;
            var finalCount = finalProbe?["probe"]?["previous"]?.AsArray().Count;
            // 每次草稿探针自身会新增一个原生 id：初始探针 + 批次结束时探针都要扣除。
            var intermediateProbes = run.Trace.OfKind("draft").Count();
            var probeDelta = initialCount is not null && finalCount is not null
                ? finalCount.Value - initialCount.Value - 1 - intermediateProbes
                : (int?)null;
            draftOk &= probeDelta == run.Files.Count && draftIds.Distinct(StringComparer.Ordinal).Count() == run.Files.Count;
            Gate(
                "interop-draft-native-attachment-ids",
                "生产草稿适配器真的把文件写进了原生输入状态：协调器记录的 attachmentIds 与对端帧、页面草稿新增项三方一致，且草稿探针净增数等于文件数",
                draftOk,
                $"ids=[{string.Join(",", draftIds)}] 草稿净增={probeDelta?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}/{run.Files.Count}（初始={initialCount} 结束={finalCount}，初始探针 +1 与批次结束探针 +{intermediateProbes} 已扣除）");

            // ---- 子进程干净退出 ----
            var (cleanOk, cleanDetail) = InteropChecks.SubprocessExitedCleanly(run);
            Gate(
                "interop-subprocess-exit-clean",
                "Node 驱动按协议 EOF 自行收尾并以退出码 0 退出（没有被强杀）",
                cleanOk,
                cleanDetail);

            // ---- 资源回收：进程与句柄都不残留 ----
            var (reclaimOk, reclaimDetail) = InteropChecks.ResourcesReclaimed(fixture, run);
            Gate(
                "interop-resource-reclamation",
                "子进程已退出、按用例 id 无残留进程、双向 trace 与 C# 侧 trace 可被独占打开（无泄漏句柄）",
                reclaimOk,
                reclaimDetail);
        }
        finally
        {
            run?.Dispose();
        }
    }

    // ————————————————————————————————————————————————————————————
    // 2) 负向：边界上篡改一个字节 ⇒ 必须被哈希校验抓住
    // ————————————————————————————————————————————————————————————

    [Fact]
    public void InteropTamperedChunkByteIsDetectedAsHashMismatch()
    {
        const string caseId = "tampered-byte";
        InteropRun? run = null;
        try
        {
            run = InteropScenario.Run(fixture, InteropScenarioKind.TamperedChunk, caseId);
            var file = run.Files[0];

            var rejects = run.Driver.Rejections
                .Where(entry => string.Equals(entry["type"]?.GetValue<string>(), "file-end", StringComparison.Ordinal))
                .ToList();
            var hashReject = rejects.FirstOrDefault(entry => string.Equals(entry["code"]?.GetValue<string>(), "hash-mismatch", StringComparison.Ordinal));
            var reassembled = run.ReassembledBytes(file.FileId);
            var source = File.ReadAllBytes(file.Path);
            var endFrame = run.Trace.Frames("c2d", "file-end").FirstOrDefault();
            var record = run.Coordinator.FileRecord(file.FileId)!;
            var finalProbe = run.Trace.OfKind("draft-final").FirstOrDefault();
            var initialProbe = run.Trace.OfKind("page-ready").FirstOrDefault();
            var initialCount = initialProbe?["initialProbe"]?["previous"]?.AsArray().Count;
            var finalCount = finalProbe?["probe"]?["previous"]?.AsArray().Count;
            // 探针自身会新增 id，逐条扣除（初始探针恒有；批次结束时探针只在收到 batch-end 时产生）。
            var probeOverhead = 1 + run.Trace.OfKind("draft").Count();
            var draftDelta = initialCount is not null && finalCount is not null
                ? finalCount.Value - initialCount.Value - probeOverhead
                : (int?)null;

            var ok =
                run.Channel.TamperedChunks == 1 &&
                hashReject is not null &&
                reassembled.Length == source.Length &&
                !reassembled.AsSpan().SequenceEqual(source) &&
                !string.Equals(InteropBytes.Sha256Hex(reassembled), file.Sha256, StringComparison.Ordinal) &&
                string.Equals(endFrame?["sha256"]?.GetValue<string>(), file.Sha256, StringComparison.Ordinal) &&
                !run.Trace.OfKind("assembled").Any() &&
                !run.Trace.Frames("d2c", "import-result").Any() &&
                record.Phase == AttachmentFilePhase.Failed &&
                string.Equals(record.Code, AttachmentCoordinatorCodes.ImportTimeout, StringComparison.Ordinal) &&
                record.AttachmentIds.Count == 0 &&
                run.Coordinator.BatchPhase == AttachmentBatchPhase.Cancelled &&
                draftDelta == 0;

            Probe(
                "probe-tampered-byte-detected",
                "边界上取反一个字节 ⇒ 接收端以 hash-mismatch 拒绝 file-end、不组装 File、不导入，草稿净增为 0；C# 协调器按注入时钟判 import-timeout 且批次取消",
                ok,
                $"篡改块={run.Channel.TamperedChunks} 拒绝码={hashReject?["code"] ?? "(none)"} 拒绝详情={Short(hashReject?["detail"]?.GetValue<string>())} " +
                $"边界字节={reassembled.Length}B sha={Short(InteropBytes.Sha256Hex(reassembled))} 源 sha={Short(file.Sha256)} file-end 声明={Short(endFrame?["sha256"]?.GetValue<string>())} " +
                $"协调器={record.Phase}/{record.Code} 批次={run.Coordinator.BatchPhase} 组装记录={run.Trace.OfKind("assembled").Count()} import-result={run.Trace.Frames("d2c", "import-result").Count()} 草稿净增={draftDelta}");
        }
        finally
        {
            run?.Dispose();
        }
    }

    // ————————————————————————————————————————————————————————————
    // 3) 负向：batchId 不属于本批次的 import-result ⇒ 生产协调器必须拒绝
    // ————————————————————————————————————————————————————————————

    [Fact]
    public void InteropWrongBatchIdResultIsRefusedByProductionCoordinator()
    {
        const string caseId = "wrong-batch-id";
        InteropRun? run = null;
        try
        {
            run = InteropScenario.Run(fixture, InteropScenarioKind.WrongBatchId, caseId);

            var probeFrames = run.Trace.OfDirection("d2c")
                .Where(entry => string.Equals(entry["origin"]?.GetValue<string>(), "probe-wrong-batch-id", StringComparison.Ordinal))
                .ToList();
            var probeFrame = probeFrames.FirstOrDefault();
            var markerIds = probeFrames
                .SelectMany(entry => entry["frame"]?["attachmentIds"]?.AsArray().Select(item => item!.GetValue<string>()) ?? [])
                .ToList();
            var rejections = run.Rejections.ToList();
            var genuineResults = run.Trace.Frames("d2c", "import-result")
                .Where(entry => string.Equals(entry["origin"]?.GetValue<string>(), "receiver", StringComparison.Ordinal))
                .ToList();
            var recordIds = run.Coordinator.Files.SelectMany(record => record.AttachmentIds).ToList();

            var ok =
                probeFrames.Count == run.Files.Count &&
                probeFrame is not null &&
                probeFrames.All(entry => !string.Equals(entry["batchId"]?.GetValue<string>(), run.BatchId, StringComparison.Ordinal)) &&
                markerIds.Count == run.Files.Count &&
                markerIds.All(id => string.Equals(id, "d18-probe-wrong-id", StringComparison.Ordinal)) &&
                rejections.Count(rejection => string.Equals(rejection, "import-result:batch-not-open", StringComparison.Ordinal)) == probeFrames.Count &&
                run.Coordinator.RejectedFrames >= probeFrames.Count &&
                genuineResults.Count == run.Files.Count &&
                run.Coordinator.Files.All(record => record.Phase == AttachmentFilePhase.Staged) &&
                run.Coordinator.BatchPhase == AttachmentBatchPhase.Closed &&
                // 故意标记过的假 attachmentId 绝不能进入任何文件记录
                recordIds.All(id => !markerIds.Contains(id, StringComparer.Ordinal)) &&
                recordIds.Count == run.Files.Count;

            Probe(
                "probe-wrong-batch-id-refused",
                "对端回一条 batchId 不属于本批次、且带假 attachmentId 的 import-result ⇒ 生产协调器以 batch-not-open 拒绝，假 ID 不进入任何记录，真实结果照常 staged",
                ok,
                $"探针帧 batchId={probeFrame?["batchId"]} 假 ID=[{string.Join(",", markerIds)}] C# 拒绝=[{string.Join(",", rejections)}] 拒绝计数={run.Coordinator.RejectedFrames} " +
                $"真实 import-result={genuineResults.Count}/{run.Files.Count} 记录 ID=[{string.Join(",", recordIds)}] 批次={run.Coordinator.BatchPhase}");
        }
        finally
        {
            run?.Dispose();
        }
    }

    // ————————————————————————————————————————————————————————————
    // 4) 负向：传输中途强杀子进程 ⇒ 判据必须响亮失败（不是静默通过）
    // ————————————————————————————————————————————————————————————

    [Fact]
    public void InteropKilledChildMakesTheHarnessFailLoudly()
    {
        const string caseId = "killed-child";
        InteropRun? run = null;
        try
        {
            run = InteropScenario.Run(fixture, InteropScenarioKind.KilledChild, caseId);

            var (cleanOk, cleanDetail) = InteropChecks.SubprocessExitedCleanly(run);
            var record = run.Coordinator.FileRecord(run.Files[0].FileId)!;
            var doneEntries = run.Trace.OfKind("done").Count();
            var importResults = run.Trace.Frames("d2c", "import-result").Count();
            var batchEnds = run.Trace.Frames("d2c", "batch-end").Count();

            // 同一套"干净退出"判据在正向路径必须为真（见用例 1），这里必须为假——
            // 若子进程被杀却被判为通过，说明判据是恒真的，本用例会立即失败。
            var ok =
                run.DriverKilledByProbe &&
                run.Driver.KillRequested &&
                !cleanOk &&
                run.Driver.ExitCode != 0 &&
                run.Coordinator.BatchPhase == AttachmentBatchPhase.Open &&
                record.Phase is AttachmentFilePhase.Pending or AttachmentFilePhase.Transferring &&
                importResults == 0 &&
                batchEnds == 0 &&
                doneEntries == 0 &&
                File.Exists(run.Driver.TracePath);

            Probe(
                "probe-killed-child-fails-loudly",
                "传输中强杀 Node 驱动 ⇒ 子进程非 0 退出、批次永不关闭、无 import-result/batch-end、驱动 trace 停在半途；且正向路径的同一判据此时必须判为失败",
                ok,
                $"杀进程={run.DriverKilledByProbe} {cleanDetail} 批次={run.Coordinator.BatchPhase} 文件={record.Phase} import-result={importResults} batch-end={batchEnds} done 记录={doneEntries} trace 存在={File.Exists(run.Driver.TracePath)}");
        }
        finally
        {
            run?.Dispose();
        }
    }

    private static string Short(string? value) =>
        string.IsNullOrEmpty(value) ? "(none)" : value.Length <= 16 ? value : value[..16] + "…";
}
