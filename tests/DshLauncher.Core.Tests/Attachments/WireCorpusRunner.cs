using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>一个样本的观测结果（由生产 codec 跑出来，测试不复制 codec 逻辑）。</summary>
internal sealed record WireObservedSample
{
    public required string Id { get; init; }

    public required string File { get; init; }

    public required string Kind { get; init; }

    public required bool DecodeOk { get; init; }

    public string? DecodeCode { get; init; }

    public JsonNode? Canonical { get; init; }

    /// <summary>"skip" / "accept" / "reject"。</summary>
    public required string Apply { get; init; }

    public string? ApplyCode { get; init; }

    public string? ApplyDetail { get; init; }

    public bool Duplicate { get; init; }

    public bool ImportInvoked { get; init; }

    public AttachmentFileRecord? FileRecord { get; init; }
}

/// <summary>
/// 语料运行器（C# 侧）：读取与 TS 侧**同一份**
/// <c>schemas/remote-attachments/v1/expected.json</c>，驱动生产 codec
/// <see cref="AttachmentCodec"/> / <see cref="AttachmentSession"/>，并按期望逐字段判定。
/// </summary>
internal static class WireCorpusRunner
{
    private static readonly Lazy<string> CorpusDirLazy = new(FindCorpusDir);

    public static string CorpusDir => CorpusDirLazy.Value;

    /// <summary>仓库根（语料目录上溯三层）。</summary>
    public static string RepoRoot => Path.GetFullPath(Path.Combine(CorpusDir, "..", "..", ".."));

    /// <summary>从测试输出目录向上找到仓库根下的语料目录。</summary>
    private static string FindCorpusDir()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "remote-attachments", "v1");
            if (System.IO.File.Exists(Path.Combine(candidate, "expected.json"))) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"未能从 {AppContext.BaseDirectory} 向上找到 schemas/remote-attachments/v1/expected.json");
    }

    public static JsonObject LoadExpected() =>
        JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(CorpusDir, "expected.json")))!.AsObject();

    public static string LoadSampleText(string relativePath) =>
        System.IO.File.ReadAllText(Path.Combine(CorpusDir, relativePath));

    public static IReadOnlyList<string> ListCorpusFiles()
    {
        var files = new List<string>();
        foreach (var kind in new[] { "golden", "malicious" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(CorpusDir, kind), "*.json"))
            {
                files.Add($"{kind}/{Path.GetFileName(path)}");
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>跑一个样本：先按 setup 顺序喂给新会话，再解码并（如需要）应用样本本身。</summary>
    public static WireObservedSample RunSample(JsonObject sample)
    {
        var id = sample["id"]!.GetValue<string>();
        var file = sample["file"]!.GetValue<string>();
        var kind = sample["kind"]!.GetValue<string>();
        var expect = sample["expect"]!.AsObject();

        var decoded = AttachmentCodec.Decode(LoadSampleText(file));
        var skipApply = expect["apply"] is JsonValue applyScalar
            && applyScalar.TryGetValue<string>(out var applyText)
            && applyText == "skip";

        if (skipApply)
        {
            return new WireObservedSample
            {
                Id = id,
                File = file,
                Kind = kind,
                DecodeOk = decoded.Ok,
                DecodeCode = decoded.Code,
                Canonical = decoded.Canonical,
                Apply = "skip",
            };
        }

        using var session = new AttachmentSession();
        if (sample["setup"] is JsonArray setup)
        {
            foreach (var entry in setup)
            {
                var setupFile = entry!.GetValue<string>();
                var setupDecoded = AttachmentCodec.Decode(LoadSampleText(setupFile));
                if (!setupDecoded.Ok)
                {
                    return Failed(id, file, kind, decoded, "setup-decode-failed", $"{setupFile}: {setupDecoded.Code}");
                }

                var setupApplied = session.Apply(setupDecoded.Message!);
                if (!setupApplied.Ok)
                {
                    return Failed(id, file, kind, decoded, "setup-apply-failed", $"{setupFile}: {setupApplied.Code}");
                }
            }
        }

        if (!decoded.Ok)
        {
            return new WireObservedSample
            {
                Id = id,
                File = file,
                Kind = kind,
                DecodeOk = false,
                DecodeCode = decoded.Code,
                Apply = "skip",
            };
        }

        var applied = session.Apply(decoded.Message!);
        return new WireObservedSample
        {
            Id = id,
            File = file,
            Kind = kind,
            DecodeOk = true,
            Canonical = decoded.Canonical,
            Apply = applied.Ok ? "accept" : "reject",
            ApplyCode = applied.Code,
            Duplicate = applied.Duplicate,
            ImportInvoked = applied.ImportInvoked,
            FileRecord = applied.File,
        };
    }

    private static WireObservedSample Failed(
        string id,
        string file,
        string kind,
        AttachmentDecodeResult decoded,
        string code,
        string detail) => new()
        {
            Id = id,
            File = file,
            Kind = kind,
            DecodeOk = decoded.Ok,
            DecodeCode = decoded.Code,
            Canonical = decoded.Canonical,
            Apply = "reject",
            ApplyCode = code,
            ApplyDetail = detail,
            FileRecord = null,
        };

    /// <summary>按 expected.json 判定一次观测结果，返回失败描述列表（空表示通过）。</summary>
    public static List<string> CompareExpectation(JsonObject sample, WireObservedSample observed)
    {
        var failures = new List<string>();
        var expect = sample["expect"]!.AsObject();

        var decodeExpect = expect["decode"]!;
        if (decodeExpect is JsonValue decodeValue && decodeValue.GetValue<string>() == "accept")
        {
            if (!observed.DecodeOk)
            {
                failures.Add($"decode: 期望接受，实际拒绝 {observed.DecodeCode}");
            }
            else if (!AttachmentCodec.CanonicalEquals(observed.Canonical, expect["canonical"]))
            {
                failures.Add(
                    $"canonical: 期望 {AttachmentCodec.CanonicalJson(expect["canonical"])}，实际 {AttachmentCodec.CanonicalJson(observed.Canonical)}");
            }
        }
        else
        {
            var wanted = decodeExpect.AsObject()["reject"]!.GetValue<string>();
            if (observed.DecodeOk) failures.Add($"decode: 期望拒绝 {wanted}，实际接受");
            else if (observed.DecodeCode != wanted) failures.Add($"decode: 期望拒绝 {wanted}，实际 {observed.DecodeCode}");
        }

        var applyExpect = expect["apply"]!;
        if (applyExpect is JsonValue applyValue && applyValue.GetValue<string>() == "skip")
        {
            if (observed.Apply != "skip") failures.Add($"apply: 期望 skip，实际 {observed.Apply}({observed.ApplyCode} {observed.ApplyDetail})");
        }
        else if (applyExpect is JsonValue acceptValue && acceptValue.GetValue<string>() == "accept")
        {
            if (observed.Apply != "accept")
            {
                failures.Add($"apply: 期望接受，实际 {observed.Apply}({observed.ApplyCode} {observed.ApplyDetail})");
            }
            else
            {
                var expectedDuplicate = expect["duplicate"]?.GetValue<bool>() ?? false;
                var expectedImport = expect["importInvoked"]?.GetValue<bool>() ?? false;
                if (expectedDuplicate != observed.Duplicate) failures.Add($"duplicate: 期望 {expectedDuplicate}，实际 {observed.Duplicate}");
                if (expectedImport != observed.ImportInvoked) failures.Add($"importInvoked: 期望 {expectedImport}，实际 {observed.ImportInvoked}");
                if (expect["file"] is JsonObject expectedFile)
                {
                    if (observed.FileRecord is null)
                    {
                        failures.Add("file: 期望有单文件记录，实际为 null");
                    }
                    else
                    {
                        var actualFile = FileRecordJson(observed.FileRecord);
                        foreach (var pair in expectedFile)
                        {
                            var actual = actualFile[pair.Key];
                            if (!AttachmentCodec.CanonicalEquals(actual, pair.Value))
                            {
                                failures.Add(
                                    $"file.{pair.Key}: 期望 {AttachmentCodec.CanonicalJson(pair.Value)}，实际 {AttachmentCodec.CanonicalJson(actual)}");
                            }
                        }
                    }
                }
            }
        }
        else
        {
            var wanted = applyExpect.AsObject()["reject"]!.GetValue<string>();
            if (observed.Apply == "accept") failures.Add($"apply: 期望拒绝 {wanted}，实际接受");
            else if (observed.Apply == "skip") failures.Add($"apply: 期望拒绝 {wanted}，实际未执行");
            else if (observed.ApplyCode != wanted) failures.Add($"apply: 期望拒绝 {wanted}，实际 {observed.ApplyCode} {observed.ApplyDetail}");
        }

        return failures;
    }

    /// <summary>
    /// 负向对照：故意把期望值改错。判定路径必须对这些样本报错，
    /// 否则说明该样本的期望从未被真正比较。
    /// </summary>
    public static JsonObject MutateExpectation(JsonObject sample)
    {
        var mutated = JsonNode.Parse(sample.ToJsonString())!.AsObject();
        var expect = mutated["expect"]!.AsObject();
        const string WrongCode = "version-mismatch";

        if (expect["decode"] is JsonValue decodeValue && decodeValue.GetValue<string>() == "accept")
        {
            expect["decode"] = new JsonObject { ["reject"] = WrongCode };
            if (expect["canonical"] is JsonObject canonical) canonical["v"] = 999;
        }
        else if (expect["decode"]!.AsObject()["reject"]!.GetValue<string>() != WrongCode)
        {
            expect["decode"] = new JsonObject { ["reject"] = WrongCode };
        }
        else
        {
            expect["decode"] = new JsonObject { ["reject"] = "unknown-field" };
        }

        if (expect["apply"] is JsonValue applyValue && applyValue.GetValue<string>() == "accept")
        {
            expect["apply"] = new JsonObject { ["reject"] = "cancelled" };
        }
        else if (expect["apply"] is JsonValue skipValue && skipValue.GetValue<string>() == "skip")
        {
            expect["apply"] = "accept";
        }
        else if (expect["apply"]!.AsObject()["reject"]!.GetValue<string>() != "cancelled")
        {
            expect["apply"] = new JsonObject { ["reject"] = "cancelled" };
        }
        else
        {
            expect["apply"] = new JsonObject { ["reject"] = "unknown-field" };
        }

        return mutated;
    }

    /// <summary>把单文件记录投影成可与 expected.json 比较的 JSON。</summary>
    public static JsonObject FileRecordJson(AttachmentFileRecord record) => new()
    {
        ["fileId"] = record.FileId,
        ["name"] = record.Name,
        ["byteLength"] = record.ByteLength,
        ["mime"] = record.Mime,
        ["declaredSha256"] = record.DeclaredSha256,
        ["transport"] = record.Transport switch
        {
            AttachmentTransportState.Buffering => "buffering",
            AttachmentTransportState.Buffered => "buffered",
            _ => "idle",
        },
        ["draft"] = record.Draft switch
        {
            AttachmentDraftState.Staged => "staged",
            AttachmentDraftState.Failed => "failed",
            AttachmentDraftState.Partial => "partial",
            _ => "none",
        },
        ["upload"] = record.Upload == AttachmentUploadState.HarnessOwned ? "harness-owned" : "none",
        ["receivedBytes"] = record.ReceivedBytes,
        ["ackedBytes"] = record.AckedBytes,
        ["inFlight"] = record.InFlight,
        ["ended"] = record.Ended,
        ["resolved"] = record.Resolved,
        ["submittedItems"] = record.SubmittedItems,
        ["attachmentIds"] = new JsonArray([.. record.AttachmentIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
    };

    /// <summary>语料目录中的样例文本，供测试构造反例。</summary>
    public static JsonNode ParseSample(string relativePath) => JsonNode.Parse(LoadSampleText(relativePath))!;
}
