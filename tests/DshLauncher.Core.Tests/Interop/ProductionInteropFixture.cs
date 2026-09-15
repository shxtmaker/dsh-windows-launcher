using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DshLauncher.Core.Tests.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Interop;

/// <summary>一条可判定的门禁记录（写进 artifacts/verify-portable/d18-interop-gates.json）。</summary>
public sealed record InteropGate(string Id, string Description, bool Ok, string Detail);

/// <summary>一条负向探针记录：<c>Ok=true</c> 表示"反例确实失败了"，即被测判据不是恒真。</summary>
public sealed record InteropProbe(string Id, string Description, bool Ok, string Detail);

/// <summary>
/// 互通门禁台账：用例逐条登记，夹具在收尾时汇总成证据 JSON。
/// 汇总时会把"声明过但没有任何用例上报"的门禁判为失败——避免用例被悄悄跳过却留下绿灯。
/// </summary>
public sealed class InteropLedger
{
    private readonly object _sync = new();
    private readonly List<InteropGate> _gates = [];
    private readonly List<InteropProbe> _probes = [];
    private readonly List<string> _caseLog = [];

    public void Gate(string id, string description, bool ok, string detail)
    {
        lock (_sync)
        {
            _gates.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            _gates.Add(new InteropGate(id, description, ok, detail));
        }
    }

    public void Probe(string id, string description, bool ok, string detail)
    {
        lock (_sync)
        {
            _probes.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            _probes.Add(new InteropProbe(id, description, ok, detail));
        }
    }

    public void Case(string message)
    {
        lock (_sync)
        {
            _caseLog.Add($"{DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {message}");
        }
    }

    public (IReadOnlyList<InteropGate> Gates, IReadOnlyList<InteropProbe> Probes, IReadOnlyList<string> CaseLog) Snapshot()
    {
        lock (_sync)
        {
            return ([.. _gates], [.. _probes], [.. _caseLog]);
        }
    }
}

/// <summary>
/// D18 互通夹具（每个测试类一次）。
///
/// 负责四件事：
///   1. 幂等准备真实夹具（`tests/interop/ensure-fixture.sh`：按需 build + test:pack + down.sh + setup.sh）；
///   2. 启动/停止夹具服务（真实 web profile、0.0.0.0 绑定、LAN 访问地址来自夹具记录）；
///   3. 收尾时 `down.sh` 并断言没有残留进程；
///   4. 把用例登记的判据与"Core/Interop 用例集并集覆盖"核对结果写成
///      `artifacts/verify-portable/d18-interop-gates.json` 与 `artifacts/fixture/evidence/` 副本。
///
/// 注意：每个用例的 finally 会杀自己的 Node 子进程与 Chromium；夹具目录的删除只在
/// 类收尾做一次（`down.sh` 会删除整个 `artifacts/fixture`，逐用例删除会让下一个用例
/// 必须重跑几分钟的 setup.sh）。
/// </summary>
public sealed class ProductionInteropFixture : IAsyncLifetime
{
    /// <summary>冻结的互通门禁清单：缺任何一条即判失败（用户例被跳过）。</summary>
    public static readonly string[] RequiredGateIds =
    [
        "interop-page-production-receiver",
        "interop-transport-two-way",
        "interop-chunk-bytes-fidelity",
        "interop-browser-assembled-file",
        "interop-hash-cross-boundary",
        "interop-batch-file-ids",
        "interop-ack-returned",
        "interop-import-result-returned",
        "interop-draft-native-attachment-ids",
        "interop-subprocess-exit-clean",
        "interop-resource-reclamation",
        "interop-case-set-union-coverage",
    ];

    /// <summary>冻结的负向探针清单：同样缺一即失败（探针被跳过等于判据不可证伪）。</summary>
    public static readonly string[] RequiredProbeIds =
    [
        "probe-tampered-byte-detected",
        "probe-wrong-batch-id-refused",
        "probe-killed-child-fails-loudly",
    ];

    /// <summary>本类里属于 Interop 用例集的用例名（必须与 manifest 的 interop 集合逐一对应）。</summary>
    public static readonly string[] InteropCaseNames =
    [
        "DshLauncher.Core.Tests.Interop.ProductionInteropTests.InteropSendsBatchThroughRealBrowserReceiverAndReturnsAckAndImportResult",
        "DshLauncher.Core.Tests.Interop.ProductionInteropTests.InteropTamperedChunkByteIsDetectedAsHashMismatch",
        "DshLauncher.Core.Tests.Interop.ProductionInteropTests.InteropWrongBatchIdResultIsRefusedByProductionCoordinator",
        "DshLauncher.Core.Tests.Interop.ProductionInteropTests.InteropKilledChildMakesTheHarnessFailLoudly",
    ];

    private Process? _service;
    private StreamWriter? _serviceLog;

    public ProductionInteropFixture()
    {
        PluginRoot = Path.Combine(RepoRoot, "plugins", "dsh-remote-attachments");
        NodePath = ResolveExecutable("DSH_ATTACH_NODE", "node");
        DshBin = Environment.GetEnvironmentVariable("DSH_BIN")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm-global", "bin", "dsh");
        FixtureRoot = Path.Combine(RepoRoot, "artifacts", "fixture");
        DshHome = Path.Combine(FixtureRoot, "dsh-home");
        FixtureProfileName = "dsh-attachments-fixture";
        HeadlessProfileName = "dsh-attachments-headless";
        Port = 3099;
        PortText = Port.ToString(CultureInfo.InvariantCulture);
        LoopbackBase = $"http://127.0.0.1:{PortText}";
        EvidenceDirectory = Path.Combine(RepoRoot, "artifacts", "verify-portable");
        FixtureEvidenceDirectory = Path.Combine(FixtureRoot, "evidence");
        TraceDirectory = Path.Combine(EvidenceDirectory, "d18-interop");
        DriverPath = Path.Combine(PluginRoot, "tests", "interop", "d18-interop-driver.mjs");
        EnsureFixtureScript = Path.Combine(PluginRoot, "tests", "interop", "ensure-fixture.sh");
        DownScript = Path.Combine(PluginRoot, "tests", "fixtures", "down.sh");
        LanBase = string.Empty; // InitializeAsync 里按夹具记录填充。
        ProvisionResult = new InteropCommandResult(-1, string.Empty, "not-run", false);
    }

    /// <summary>仓库根：复用 D10 语料运行器的定位逻辑（从测试输出目录向上找 schemas/）。</summary>
    public static string RepoRoot { get; } = WireCorpusRunner.RepoRoot;

    public string PluginRoot { get; }

    public string NodePath { get; }

    public string DshBin { get; }

    public string FixtureRoot { get; }

    public string DshHome { get; }

    public string FixtureProfileName { get; }

    public string HeadlessProfileName { get; }

    public int Port { get; }

    public string PortText { get; }

    public string LoopbackBase { get; }

    public string LanBase { get; private set; }

    public string EvidenceDirectory { get; }

    public string FixtureEvidenceDirectory { get; }

    public string TraceDirectory { get; }

    public string DriverPath { get; }

    public string EnsureFixtureScript { get; }

    public string DownScript { get; }

    public InteropCommandResult ProvisionResult { get; private set; }

    public InteropLedger Ledger { get; } = new();

    public InteropCommandResult DownResult { get; private set; } = new(-1, string.Empty, "not-run", false);

    public IReadOnlyList<string> LeftoverProcesses { get; private set; } = [];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(EvidenceDirectory);
        Directory.CreateDirectory(TraceDirectory);
        Directory.CreateDirectory(FixtureEvidenceDirectory);

        // 1) 幂等准备夹具：保证被测的是**当前**构建产物（陈旧 tarball 会静默装旧代码）。
        var force = string.Equals(Environment.GetEnvironmentVariable("DSH_INTEROP_FORCE_PROVISION"), "1", StringComparison.Ordinal)
            ? new[] { "--force" }
            : [];
        ProvisionResult = InteropProcess.RunBashScript(EnsureFixtureScript, force, RepoRoot);
        File.WriteAllText(Path.Combine(TraceDirectory, "ensure-fixture.log"), ProvisionResult.Output + ProvisionResult.Error);
        if (ProvisionResult.ExitCode != 0 || ProvisionResult.TimedOut)
        {
            throw new InvalidOperationException(
                $"夹具准备失败（exit={ProvisionResult.ExitCode} timedOut={ProvisionResult.TimedOut}）：{Tail(ProvisionResult.Error)}");
        }

        var lanAddressFile = Path.Combine(FixtureRoot, "lan-address.txt");
        if (!File.Exists(lanAddressFile))
        {
            throw new InvalidOperationException($"夹具缺少 {lanAddressFile}（setup.sh 未完成）");
        }

        LanBase = $"http://{File.ReadAllText(lanAddressFile).Trim()}:{PortText}";
        StartService();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        StopService();
        DownResult = InteropProcess.RunBashScript(DownScript, null, RepoRoot);
        LeftoverProcesses = FindLeftoverProcesses();

        Ledger.Gate(
            "interop-resource-reclamation",
            "互通资源回收：Node 驱动/Chromium/夹具服务均无残留，夹具目录经 down.sh 收拢",
            LeftoverProcesses.Count == 0 && DownResult.ExitCode == 0,
            $"down.sh exit={DownResult.ExitCode}；残留进程=[{string.Join(" | ", LeftoverProcesses)}]");

        var unionOk = CheckCaseSetUnion(out var unionDetail);
        Ledger.Gate(
            "interop-case-set-union-coverage",
            "同一程序集 manifest 显式区分 Core/Interop 用例集，且两者并集覆盖全量冻结基线",
            unionOk,
            unionDetail);

        WriteEvidence();
        return ValueTask.CompletedTask;
    }

    /// <summary>启动夹具服务（真实 web profile，0.0.0.0:3099，LAN 地址用于非 loopback 访问）。</summary>
    private void StartService()
    {
        var logDirectory = Path.Combine(FixtureRoot, "evidence");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "service-d18-interop.log");
        var startInfo = new ProcessStartInfo(DshBin)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(FixtureProfileName);
        startInfo.ArgumentList.Add("--no-open");
        startInfo.Environment["DSH_HOME"] = DshHome;

        _service = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动夹具服务：{DshBin}");
        _serviceLog = new StreamWriter(logPath, append: false);
        _service.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _serviceLog.WriteLine(eventArgs.Data); };
        _service.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _serviceLog.WriteLine(eventArgs.Data); };
        _service.BeginOutputReadLine();
        _service.BeginErrorReadLine();
        File.WriteAllText(Path.Combine(FixtureRoot, "service.pid"), _service.Id.ToString(CultureInfo.InvariantCulture));

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            if (_service.HasExited)
            {
                _serviceLog.Flush();
                throw new InvalidOperationException(
                    $"夹具服务启动即退出（exit={_service.ExitCode}）；日志尾部：{Tail(File.ReadAllText(logPath))}");
            }

            if (ProbeServiceReady())
            {
                _serviceLog.Flush();
                return;
            }

            Thread.Sleep(500);
        }

        _serviceLog.Flush();
        throw new InvalidOperationException($"夹具服务未在 180s 内就绪；日志尾部：{Tail(File.ReadAllText(logPath))}");
    }

    private bool ProbeServiceReady()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = client.GetAsync(LoopbackBase).GetAwaiter().GetResult();
            return (int)response.StatusCode is >= 200 and < 500;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private void StopService()
    {
        if (_service is null) return;
        try
        {
            if (!_service.HasExited)
            {
                _service.Kill(entireProcessTree: true);
                _service.WaitForExit(10_000);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            Ledger.Case($"停止夹具服务失败：{error.Message}");
        }
        finally
        {
            _service.Dispose();
            _service = null;
            _serviceLog?.Dispose();
            _serviceLog = null;
        }
    }

    private static IReadOnlyList<string> FindLeftoverProcesses()
    {
        var result = InteropProcess.Run(
            "bash",
            ["-lc", "pgrep -af 'd18-interop-driver|dsh-cdp-|dsh-attachments-fixture|dsh-attachments-headless' || true"],
            RepoRoot);
        return [.. result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("pgrep", StringComparison.Ordinal))];
    }

    /// <summary>
    /// 核对 manifest 的用例集划分：Interop 集合必须恰好包含本类的互通用例，
    /// Core 与 Interop 必须不相交，且并集等于冻结的全量基线（union）。
    /// </summary>
    private static bool CheckCaseSetUnion(out string detail)
    {
        var manifestPath = Path.Combine(RepoRoot, "eng", "expected-test-dataset.json");
        if (!File.Exists(manifestPath))
        {
            detail = $"缺少 manifest：{manifestPath}";
            return false;
        }

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var assembly = manifest["assemblies"]!.AsArray()
            .Select(node => node!.AsObject())
            .FirstOrDefault(entry => string.Equals(entry["assembly"]!.GetValue<string>(), "DshLauncher.Core.Tests", StringComparison.Ordinal));
        if (assembly is null)
        {
            detail = "manifest 里没有 DshLauncher.Core.Tests 条目";
            return false;
        }

        var sets = assembly["caseSets"]?.AsArray();
        if (sets is null || sets.Count < 2)
        {
            detail = "manifest 未显式区分 Core/Interop 用例集（缺少 caseSets 或不足两组）";
            return false;
        }

        var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            var name = set!["name"]!.GetValue<string>();
            byName[name] = [.. set["caseNameSha256"]!.AsArray().Select(item => item!.GetValue<string>())];
        }

        if (!byName.TryGetValue("core", out var core) || !byName.TryGetValue("interop", out var interop))
        {
            detail = $"manifest 缺少 core/interop 集合（实际：{string.Join(",", byName.Keys)}）";
            return false;
        }

        var union = assembly["caseNameSha256"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var combined = new HashSet<string>(core, StringComparer.Ordinal);
        combined.UnionWith(interop);
        var overlap = core.Intersect(interop, StringComparer.Ordinal).Count();
        // manifest 存的哈希是大写十六进制（Get-DshTextSha256 的输出），这里统一大小写后再比对。
        var expectedInterop = InteropCaseNames
            .Select(name => InteropBytes.Sha256Hex(name).ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var missingInterop = expectedInterop.Except(interop, StringComparer.Ordinal).Count();
        var extraInterop = interop.Except(expectedInterop, StringComparer.Ordinal).Count();
        var unionCovered = combined.SetEquals(union);
        var ok = overlap == 0 && unionCovered && missingInterop == 0 && extraInterop == 0;
        detail = string.Create(
            CultureInfo.InvariantCulture,
            $"core={core.Count} interop={interop.Count} union={union.Count} 交集={overlap} 并集覆盖={unionCovered} interop 缺失={missingInterop} interop 多余={extraInterop}");
        return ok;
    }

    private void WriteEvidence()
    {
        var (gates, probes, caseLog) = Ledger.Snapshot();
        var reported = gates.Select(gate => gate.Id).ToHashSet(StringComparer.Ordinal);
        var allGates = new List<InteropGate>(gates);
        foreach (var required in RequiredGateIds.Where(id => !reported.Contains(id)))
        {
            allGates.Add(new InteropGate(required, "声明过的互通门禁", false, "没有任何用例上报该门禁（用例可能被跳过或未执行）"));
        }

        var reportedProbes = probes.Select(probe => probe.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var required in RequiredProbeIds.Where(id => !reportedProbes.Contains(id)))
        {
            allGates.Add(new InteropGate(required, "声明过的负向探针", false, "没有任何用例上报该负向探针（用例可能被跳过或未执行）"));
        }

        var failed = allGates.Where(gate => !gate.Ok).Select(gate => gate.Id).ToList();
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["task"] = "D18",
            ["purpose"] = "production-interop",
            ["platform"] = "linux",
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["method"] = "真实 C# 生产发送端（AttachmentTransferCoordinator + AttachmentCodec + D10 镜像状态机）经 stdio 测试通道"
                + "（只替代 WebView2 物理消息边界）把批次发给真实 Chromium 页面里的生产接收端（D12 页面全局），"
                + "接收端组装真实 File 并经生产草稿桥（D08 适配器）导入，ack 与 import-result 回到同一 Core",
            ["gates"] = new JsonArray([.. allGates.Select(gate => (JsonNode)new JsonObject
            {
                ["id"] = gate.Id,
                ["description"] = gate.Description,
                ["ok"] = gate.Ok,
                ["detail"] = gate.Detail,
            })]),
            ["failedGateIds"] = new JsonArray([.. failed.Select(id => (JsonNode)JsonValue.Create(id))]),
            ["result"] = failed.Count == 0 ? "pass" : "fail",
            ["negativeProbes"] = new JsonArray([.. probes.Select(probe => (JsonNode)new JsonObject
            {
                ["id"] = probe.Id,
                ["description"] = probe.Description,
                ["ok"] = probe.Ok,
                ["detail"] = probe.Detail,
            })]),
            ["caseSetLayout"] = new JsonObject
            {
                ["assembly"] = "DshLauncher.Core.Tests",
                ["interopCaseNames"] = new JsonArray([.. InteropCaseNames.Select(name => (JsonNode)JsonValue.Create(name))]),
                ["manifest"] = "eng/expected-test-dataset.json（caseSets: core / interop，并集 = caseNameSha256）",
            },
            ["driverDependencies"] = new JsonObject
            {
                ["node"] = "Node 22+（内置 fetch/WebSocket）；由 DSH_ATTACH_NODE 覆盖",
                ["dshCli"] = $"{DshBin}（版本必须等于 compatibility-lock.json 的 harness.version）",
                ["chromium"] = "真实 Chromium（cdp.mjs 解析顺序：DSH_ATTACH_CHROME → /usr/bin/google-chrome → chromium）",
                ["fixture"] = $"私有 DSH_HOME={DshHome} + profile={FixtureProfileName}/{HeadlessProfileName}（setup.sh 建立，ensure-fixture.sh 保证当前 tarball）",
                ["driver"] = DriverPath,
                ["traceDirectory"] = TraceDirectory,
            },
            ["windowsRequirements"] = new JsonArray(
                "Windows 上要跑同一批互通用例，除上述工具外还需要：",
                "1) 真实 WebView2 运行时（Edge WebView2 Runtime）——D18 只替代它的物理消息边界，Windows 全量验证必须回到真实 WebView2 承载；",
                "2) Windows 侧的字节源端口实现（Platform.Windows 的 WindowsStagedByteSource / 暂存适配器），本用例在 Linux 用等价的只读文件源注入同一 IAttachmentByteSource 契约；",
                "3) pwsh 7 与 eng/verify.ps1 的正式门禁环境（本任务是 Linux Development gate 的 L13 检查，不是 Windows 发布门禁）；",
                "4) Windows 上同一批用例的用例名会进入 DshLauncher.Core.Tests 的 Interop 集合，manifest 基线需按同一算法复核。"),
            ["caseLog"] = new JsonArray([.. caseLog.Select(line => (JsonNode)JsonValue.Create(line))]),
            ["cleanup"] = new JsonObject
            {
                ["downShExitCode"] = DownResult.ExitCode,
                ["leftoverProcesses"] = new JsonArray([.. LeftoverProcesses.Select(line => (JsonNode)JsonValue.Create(line))]),
            },
        };

        var text = report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        Directory.CreateDirectory(EvidenceDirectory);
        File.WriteAllText(Path.Combine(EvidenceDirectory, "d18-interop-gates.json"), text);

        // down.sh 会删除整个夹具目录，因此副本在收尾之后补写（与 D12/D13 判据脚本同款顺序）。
        Directory.CreateDirectory(FixtureEvidenceDirectory);
        File.WriteAllText(Path.Combine(FixtureEvidenceDirectory, "d18-interop-gates.json"), text);
    }

    private static string ResolveExecutable(string environmentVariable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var result = InteropProcess.Run("bash", ["-lc", $"command -v {fallback}"], AppContext.BaseDirectory);
        var resolved = result.Output.Trim();
        return resolved.Length > 0 ? resolved : fallback;
    }

    private static string Tail(string text, int maxChars = 1200) =>
        text.Length <= maxChars ? text : text[^maxChars..];
}
