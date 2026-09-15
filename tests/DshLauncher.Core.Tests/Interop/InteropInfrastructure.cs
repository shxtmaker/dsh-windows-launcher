using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;

namespace DshLauncher.Core.Tests.Interop;

/// <summary>
/// D18 互通基础设施：把 Node 驱动当成**测试通道**（只替代 WebView2 的物理消息边界），
/// 生产发送算法（<see cref="AttachmentTransferCoordinator"/> + <see cref="AttachmentCodec"/>）
/// 一行不改地跑在它上面。
/// </summary>
public sealed record InteropCommandResult(int ExitCode, string Output, string Error, bool TimedOut);

internal static class InteropProcess
{
    /// <summary>同步跑一个子进程并收全 stdout/stderr；超时则杀掉整棵进程树。</summary>
    public static InteropCommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        int timeoutMs = 1_200_000)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动子进程：{fileName}");
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) output.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) error.AppendLine(eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        if (!process.WaitForExit(timeoutMs))
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
        }

        process.WaitForExit();
        return new InteropCommandResult(process.ExitCode, output.ToString(), error.ToString(), timedOut);
    }

    /// <summary>跑 bash 脚本（夹具准备/清理只用这一条通路，避免 C# 复制脚本逻辑）。</summary>
    public static InteropCommandResult RunBashScript(string scriptPath, IEnumerable<string>? extraArguments = null, string? workingDirectory = null)
    {
        var arguments = new List<string> { scriptPath };
        if (extraArguments is not null) arguments.AddRange(extraArguments);
        return Run("bash", arguments, workingDirectory ?? ProductionInteropFixture.RepoRoot);
    }
}

/// <summary>驱动 stdout 上的一条结构化记录（原样保留，供断言与证据）。</summary>
internal sealed record InteropRecord(string Direction, string Kind, string Json, long AtUnixMs);

/// <summary>
/// Node 驱动的 C# 侧句柄：spawn、逐行解析 stdout 协议、收帧、等待退出、释放。
///
/// 线程模型：stdout 由专用后台线程逐行读取（阻塞 ReadLine），入站帧进有界队列并通过
/// <see cref="Monitor"/> 通知等待方；测试线程只做 Pump 与断言，不睡眠轮询。
/// </summary>
internal sealed class InteropDriver : IDisposable
{
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly List<InteropRecord> _transcript = [];
    private readonly Queue<string> _inboundFrames = new();
    private readonly List<JsonObject> _rejections = [];
    private readonly List<string> _diagnostics = [];
    private readonly StringBuilder _stdoutRaw = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _sync = new();
    private readonly Thread _stdoutThread;
    private readonly Thread _stderrThread;
    private bool _exited;
    private bool _stdinClosed;
    private bool _killRequested;
    private JsonObject? _doneRecord;

    private InteropDriver(Process process, string caseId, string probe, string tracePath, string transcriptPath)
    {
        _process = process;
        CaseId = caseId;
        Probe = probe;
        TracePath = tracePath;
        TranscriptPath = transcriptPath;
        _stdoutThread = new Thread(ReadStdout) { IsBackground = true, Name = $"d18-interop-stdout-{caseId}" };
        _stderrThread = new Thread(ReadStderr) { IsBackground = true, Name = $"d18-interop-stderr-{caseId}" };
        _stdoutThread.Start();
        _stderrThread.Start();
    }

    public string CaseId { get; }

    public string Probe { get; }

    public string TracePath { get; }

    public string TranscriptPath { get; }

    public JsonObject? PageHandshake { get; private set; }

    /// <summary>页面声明的当前会话 id（协调器身份里的 sessionId 必须与它一致）。</summary>
    public string? PageSessionId => PageHandshake?["sessionId"]?.GetValue<string>();

    public int ReceivedFrameCount { get; private set; }

    public int SentFrameCount { get; private set; }

    public IReadOnlyList<JsonObject> Rejections
    {
        get
        {
            lock (_sync) return [.. _rejections];
        }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_sync) return [.. _diagnostics];
        }
    }

    public string StderrText
    {
        get
        {
            lock (_sync) return _stderr.ToString();
        }
    }

    public bool HasExited
    {
        get
        {
            lock (_sync) return _exited;
        }
    }

    public bool KillRequested
    {
        get
        {
            lock (_sync) return _killRequested;
        }
    }

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public JsonObject? DoneRecord
    {
        get
        {
            lock (_sync) return _doneRecord;
        }
    }

    /// <summary>启动驱动并等它报告"真实页面 + 生产接收端就绪"。</summary>
    public static InteropDriver Start(
        ProductionInteropFixture fixture,
        string caseId,
        string probe,
        TimeSpan readyTimeout)
    {
        var tracePath = Path.Combine(fixture.TraceDirectory, $"{caseId}.trace.jsonl");
        var transcriptPath = Path.Combine(fixture.TraceDirectory, $"{caseId}.csharp-transcript.jsonl");
        var startInfo = new ProcessStartInfo(fixture.NodePath)
        {
            WorkingDirectory = ProductionInteropFixture.RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            fixture.DriverPath,
            "--loopback-base", fixture.LoopbackBase,
            "--lan-base", fixture.LanBase,
            "--trace", tracePath,
            "--probe", probe,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DSH_HOME"] = fixture.DshHome;
        startInfo.Environment["DSH_ATTACH_FIXTURE_ROOT"] = fixture.FixtureRoot;
        startInfo.Environment["DSH_BIN"] = fixture.DshBin;
        startInfo.Environment["DSH_ATTACH_FIXTURE_PROFILE"] = fixture.FixtureProfileName;
        startInfo.Environment["DSH_ATTACH_HEADLESS_PROFILE"] = fixture.HeadlessProfileName;
        startInfo.Environment["DEEPSEEK_API_KEY"] = "stub-key";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 D18 互通驱动：{fixture.NodePath}");
        var driver = new InteropDriver(process, caseId, probe, tracePath, transcriptPath);
        if (!driver.WaitForPage(readyTimeout))
        {
            var reason = driver.HasExited
                ? $"D18 驱动在页面就绪前退出（exit={driver.ExitCode}）"
                : $"D18 驱动在 {readyTimeout.TotalSeconds:F0}s 内未报告页面就绪";
            driver.Kill();
            throw new InvalidOperationException($"{reason}：{driver.StderrTail()}");
        }

        return driver;
    }

    /// <summary>等待驱动报告"真实页面 + 生产接收端就绪"。</summary>
    public bool WaitForPage(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_sync)
        {
            while (PageHandshake is null && !_exited && DateTime.UtcNow < deadline)
            {
                Monitor.Wait(_sync, TimeSpan.FromMilliseconds(50));
            }

            return PageHandshake is not null;
        }
    }

    /// <summary>发一条线协议帧（原样文本 + 换行）。驱动退出/管道关闭时返回 false，绝不抛异常。</summary>
    public bool SendFrame(string wireJson, string kind)
    {
        lock (_sync)
        {
            if (_stdinClosed || _exited) return false;
        }

        try
        {
            _process.StandardInput.Write(wireJson);
            _process.StandardInput.Write('\n');
            _process.StandardInput.Flush();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            lock (_sync)
            {
                _diagnostics.Add($"发送帧失败：{error.GetType().Name} {error.Message}");
            }

            return false;
        }

        lock (_sync)
        {
            SentFrameCount += 1;
            _transcript.Add(new InteropRecord("c2d", kind, wireJson, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        return true;
    }

    /// <summary>非阻塞取一条入站帧（驱动 stdout 的 frame 记录）。</summary>
    public bool TryReceive(out string? wireJson)
    {
        lock (_sync)
        {
            if (_inboundFrames.Count == 0)
            {
                wireJson = null;
                return false;
            }

            wireJson = _inboundFrames.Dequeue();
            return true;
        }
    }

    /// <summary>等到至少一条入站帧、驱动退出或超时；返回当前可用帧数。</summary>
    public int WaitForInbound(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_sync)
        {
            while (_inboundFrames.Count == 0 && !_exited && DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                Monitor.Wait(_sync, remaining < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : remaining);
            }

            return _inboundFrames.Count;
        }
    }

    /// <summary>强杀驱动（"子进程被杀"负向探针专用），并等待它真正退出。</summary>
    public void Kill()
    {
        lock (_sync)
        {
            _killRequested = true;
        }

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            lock (_sync)
            {
                _diagnostics.Add($"终止驱动失败：{error.Message}");
            }
        }

        _process.WaitForExit((int)KillGrace.TotalMilliseconds);
        lock (_sync)
        {
            _exited = true;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>正常收尾：关 stdin（协议 EOF）→ 等驱动自行清理并退出；超时才升级为强杀。</summary>
    public bool Finish(TimeSpan timeout)
    {
        lock (_sync)
        {
            if (_stdinClosed) return _exited;
            _stdinClosed = true;
        }

        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            lock (_sync)
            {
                _diagnostics.Add($"关闭 stdin 失败：{error.Message}");
            }
        }

        var exited = _process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited) Kill();
        else
        {
            lock (_sync)
            {
                _exited = true;
                Monitor.PulseAll(_sync);
            }
        }

        WriteTranscript();
        return exited;
    }

    public void Dispose()
    {
        if (!_process.HasExited) Kill();
        _stdoutThread.Join(TimeSpan.FromSeconds(2));
        _stderrThread.Join(TimeSpan.FromSeconds(2));
        _process.Dispose();
    }

    public string StderrTail(int maxChars = 1200)
    {
        var text = StderrText;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    /// <summary>把 C# 侧的两向消息 trace 落盘（与驱动侧 trace 互为对照）。</summary>
    public void WriteTranscript()
    {
        var lines = new List<string>();
        lock (_sync)
        {
            foreach (var record in _transcript)
            {
                lines.Add(new JsonObject
                {
                    ["dir"] = record.Direction,
                    ["kind"] = record.Kind,
                    ["atUnixMs"] = record.AtUnixMs,
                    ["sha256"] = InteropBytes.Sha256Hex(record.Json),
                    ["json"] = record.Json,
                }.ToJsonString());
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TranscriptPath)!);
        File.WriteAllLines(TranscriptPath, lines);
    }

    private void ReadStdout()
    {
        try
        {
            string? line;
            while ((line = _process.StandardOutput.ReadLine()) is not null)
            {
                lock (_sync)
                {
                    _stdoutRaw.AppendLine(line);
                }

                HandleRecord(line);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            lock (_sync)
            {
                _diagnostics.Add($"读取驱动 stdout 结束：{error.GetType().Name} {error.Message}");
            }
        }
        finally
        {
            lock (_sync)
            {
                _exited = true;
                Monitor.PulseAll(_sync);
            }
        }
    }

    private void ReadStderr()
    {
        try
        {
            string? line;
            while ((line = _process.StandardError.ReadLine()) is not null)
            {
                lock (_sync)
                {
                    _stderr.AppendLine(line);
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // 进程退出时读到 EOF/管道关闭属正常收尾，不再上报。
            _ = error;
        }
    }

    private void HandleRecord(string line)
    {
        JsonObject? record;
        try
        {
            record = JsonNode.Parse(line) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            record = null;
        }

        if (record is null)
        {
            lock (_sync)
            {
                _diagnostics.Add($"驱动 stdout 出现非 JSON 行：{Truncate(line)}");
            }

            return;
        }

        var kind = record["kind"]?.GetValue<string>() ?? "unknown";
        switch (kind)
        {
            case "page":
                lock (_sync)
                {
                    PageHandshake = record;
                    _transcript.Add(new InteropRecord("d2c", "page", record.ToJsonString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    Monitor.PulseAll(_sync);
                }

                break;
            case "frame":
                var frame = record["frame"] as JsonObject;
                if (frame is null)
                {
                    lock (_sync)
                    {
                        _diagnostics.Add($"frame 记录缺少 frame 对象：{Truncate(line)}");
                    }

                    break;
                }

                var frameJson = frame.ToJsonString();
                lock (_sync)
                {
                    ReceivedFrameCount += 1;
                    _inboundFrames.Enqueue(frameJson);
                    _transcript.Add(new InteropRecord("d2c", "frame", frameJson, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    Monitor.PulseAll(_sync);
                }

                break;
            case "reject":
                lock (_sync)
                {
                    _rejections.Add(record);
                    _transcript.Add(new InteropRecord("d2c", "reject", record.ToJsonString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }

                break;
            case "done":
                lock (_sync)
                {
                    _doneRecord = record;
                    _transcript.Add(new InteropRecord("d2c", "done", record.ToJsonString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }

                break;
            default:
                lock (_sync)
                {
                    _diagnostics.Add($"{kind}: {Truncate(record.ToJsonString())}");
                }

                break;
        }
    }

    private static string Truncate(string text, int maxChars = 300) => text.Length <= maxChars ? text : text[..maxChars];
}

/// <summary>
/// 测试通道：把协调器的出站帧写进 Node 驱动，把驱动读回的帧交给协调器。
/// 唯一额外能力是显式开启的"篡改一个字节"钩子（负向探针）——它改的是**边界上的字节**，
/// 不是发送算法：分块、序号、窗口、哈希仍全部由生产代码决定。
/// </summary>
internal sealed class InteropChannel(InteropDriver driver) : IAttachmentChannel
{
    public bool TamperNextChunkPayload { get; set; }

    public int TamperedChunks { get; private set; }

    public int SendFailures { get; private set; }

    public int BoundaryFrames { get; private set; }

    public void Send(string wireJson)
    {
        var payload = wireJson;
        if (TamperNextChunkPayload && IsChunk(wireJson))
        {
            payload = TamperChunkPayload(wireJson);
            TamperNextChunkPayload = false;
            TamperedChunks += 1;
        }

        if (!driver.SendFrame(payload, "frame"))
        {
            SendFailures += 1;
        }
    }

    public bool TryReceive([NotNullWhen(true)] out string? wireJson) => driver.TryReceive(out wireJson);

    /// <summary>边界自有的握手帧（context）：生产协调器不发送它，真实 WebView2 承载层负责投递。</summary>
    public void SendBoundaryFrame(string wireJson)
    {
        BoundaryFrames += 1;
        if (!driver.SendFrame(wireJson, "handshake"))
        {
            SendFailures += 1;
        }
    }

    private static bool IsChunk(string wireJson) =>
        wireJson.Contains("\"type\":\"chunk\"", StringComparison.Ordinal);

    /// <summary>把 chunk 载荷的第一个字节取反：长度不变（仍是合法 D10 报文），内容必变。</summary>
    private static string TamperChunkPayload(string wireJson)
    {
        var node = JsonNode.Parse(wireJson)!.AsObject();
        var decoded = Convert.FromBase64String(node["dataBase64"]!.GetValue<string>());
        decoded[0] ^= 0x01;
        node["dataBase64"] = Convert.ToBase64String(decoded);
        var tampered = node.ToJsonString();
        var recheck = AttachmentCodec.Decode(tampered);
        if (!recheck.Ok)
        {
            throw new InvalidOperationException($"篡改后的帧不再是合法 D10 报文：{recheck.Code}（{recheck.Detail}）");
        }

        return AttachmentCodec.Encode(recheck.Message!);
    }
}

/// <summary>边界侧的握手帧构造：字段由生产 codec 校验并规范化，测试不手写"看起来像"的报文。</summary>
internal static class InteropWire
{
    public static string Context(AttachmentIdentity identity)
    {
        var fields = new JsonObject
        {
            ["v"] = AttachmentProtocol.Version,
            ["type"] = "context",
            ["sessionId"] = identity.SessionId,
            ["targetId"] = identity.TargetId,
            ["documentEpoch"] = identity.DocumentEpoch,
            ["composerEpoch"] = identity.ComposerEpoch,
            ["composerScope"] = identity.ComposerScope,
        };
        var decoded = AttachmentCodec.Decode(fields.ToJsonString());
        if (!decoded.Ok)
        {
            throw new InvalidOperationException($"context 握手帧不符合冻结线协议：{decoded.Code}（{decoded.Detail}）");
        }

        return AttachmentCodec.Encode(decoded.Message!);
    }
}

/// <summary>文件字节源（Linux 侧注入端口）：真实文件 + 短读，协调器的累计逻辑照常生效。</summary>
internal sealed class FileByteSource : IAttachmentByteSource
{
    private readonly FileStream _stream;

    public FileByteSource(string path, int maxReadSize = 96 * 1024)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ByteLength = (int)_stream.Length;
        MaxReadSize = maxReadSize;
    }

    public int ByteLength { get; }

    public int MaxReadSize { get; }

    public int ReadCount { get; private set; }

    public int Read(Span<byte> destination)
    {
        ReadCount += 1;
        var size = Math.Min(destination.Length, MaxReadSize);
        return _stream.Read(destination[..size]);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>确定性素材与摘要工具。</summary>
internal static class InteropBytes
{
    /// <summary>生成恰好 size 字节的确定性 ASCII 文本（与 D12 判据同款构造，便于人工比对）。</summary>
    public static byte[] MakeText(int size, string marker)
    {
        var builder = new StringBuilder(size + 64);
        var index = 0;
        while (builder.Length < size)
        {
            builder.Append(marker)
                .Append(" line ")
                .Append(index.ToString("D6", CultureInfo.InvariantCulture))
                .Append(' ', 1)
                .Append('x', 24)
                .Append('\n');
            index += 1;
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, size));
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
}

/// <summary>驱动侧 trace 的一条记录（原样 JSON）。</summary>
internal sealed class InteropTraceFile
{
    private InteropTraceFile(string path, IReadOnlyList<JsonObject> entries)
    {
        Path = path;
        Entries = entries;
    }

    public string Path { get; }

    public IReadOnlyList<JsonObject> Entries { get; }

    public static InteropTraceFile Read(string path)
    {
        var entries = new List<JsonObject>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is JsonObject entry) entries.Add(entry);
        }

        return new InteropTraceFile(path, entries);
    }

    public IEnumerable<JsonObject> OfKind(string kind) =>
        Entries.Where(entry => string.Equals(entry["kind"]?.GetValue<string>(), kind, StringComparison.Ordinal));

    public IEnumerable<JsonObject> OfDirection(string direction) =>
        Entries.Where(entry => string.Equals(entry["dir"]?.GetValue<string>(), direction, StringComparison.Ordinal));

    public IEnumerable<JsonObject> Frames(string direction, string type) =>
        OfDirection(direction).Where(entry =>
            string.Equals(entry["kind"]?.GetValue<string>(), "frame", StringComparison.Ordinal) &&
            string.Equals(entry["type"]?.GetValue<string>(), type, StringComparison.Ordinal));
}
