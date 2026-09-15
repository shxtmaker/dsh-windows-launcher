using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D10 语料一致性测试（C# 侧）。
///
/// 与 TS 侧 <c>plugins/dsh-remote-attachments/test/unit/wire-corpus.test.mjs</c>
/// 读取**同一份** <c>schemas/remote-attachments/v1/expected.json</c> 与同一批样本，
/// 因此"两端一致"是可执行的判据，而不是两套各自为政的测试。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class WireCorpusTests
{
    private static readonly JsonObject Expected = WireCorpusRunner.LoadExpected();

    private static List<JsonObject> Samples()
    {
        var samples = new List<JsonObject>();
        foreach (var node in Expected["samples"]!.AsArray())
        {
            samples.Add(node!.AsObject());
        }

        return samples;
    }

    [Fact]
    public void CorpusCoversEveryMessageType()
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var golden = 0;
        var malicious = 0;
        foreach (var sample in Samples())
        {
            if (sample["kind"]!.GetValue<string>() == "golden")
            {
                golden += 1;
                var decoded = AttachmentCodec.Decode(WireCorpusRunner.LoadSampleText(sample["file"]!.GetValue<string>()));
                Assert.True(decoded.Ok, $"{sample["id"]} 作为 golden 必须能解码：{decoded.Code}");
                covered.Add(decoded.Message!.Type);
            }
            else
            {
                malicious += 1;
            }
        }

        Assert.True(golden > 0, "golden 语料不能为空");
        Assert.True(malicious > 0, "malicious 语料不能为空");
        foreach (var type in AttachmentProtocol.MessageTypes)
        {
            Assert.True(covered.Contains(type), $"golden 未覆盖消息类型 {type}");
        }
    }

    [Fact]
    public void ExpectedJsonHasNoOrphanOrDanglingCorpusReferences()
    {
        var files = WireCorpusRunner.ListCorpusFiles();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in Samples())
        {
            referenced.Add(sample["file"]!.GetValue<string>());
            if (sample["setup"] is JsonArray setup)
            {
                foreach (var entry in setup) referenced.Add(entry!.GetValue<string>());
            }
        }

        Assert.DoesNotContain(files, file => !referenced.Contains(file));
        Assert.DoesNotContain(referenced, file => !files.Contains(file));
    }

    [Fact]
    public void EverySampleMatchesExpectedJson()
    {
        var failures = new List<string>();
        foreach (var sample in Samples())
        {
            var observed = WireCorpusRunner.RunSample(sample);
            foreach (var failure in WireCorpusRunner.CompareExpectation(sample, observed))
            {
                failures.Add($"{observed.Id} {observed.File}: {failure}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(86, Samples().Count);
    }

    [Fact]
    public void EveryRejectionCodeComesFromTheFrozenEnum()
    {
        var frozen = new HashSet<string>(AttachmentProtocol.WireErrorCodes, StringComparer.Ordinal);
        Assert.Equal(AttachmentProtocol.WireErrorCodes.Length, frozen.Count);
        foreach (var sample in Samples())
        {
            var expect = sample["expect"]!.AsObject();
            foreach (var side in new[] { "decode", "apply" })
            {
                var value = expect[side]!;
                if (value is JsonValue scalar) continue; // "accept" / "skip"
                var code = value.AsObject()["reject"]!.GetValue<string>();
                Assert.True(frozen.Contains(code), $"{sample["id"]} 使用了未冻结的拒绝码 {code}");
            }
        }
    }

    /// <summary>
    /// 负向对照：逐样本变异期望后，同一条判定路径必须报错。
    /// 若某个样本的期望从未被真正比较，这条测试就会失败。
    /// </summary>
    [Fact]
    public void MutatedExpectationsMustFailTheSameComparisonPath()
    {
        var samples = Samples();
        var checkedCount = 0;
        foreach (var sample in samples)
        {
            var observed = WireCorpusRunner.RunSample(sample);
            Assert.Empty(WireCorpusRunner.CompareExpectation(sample, observed));

            var mutated = WireCorpusRunner.MutateExpectation(sample);
            var failures = WireCorpusRunner.CompareExpectation(mutated, observed);
            Assert.True(
                failures.Count > 0,
                $"{sample["id"]} 的期望被变异后判定路径仍然通过——该样本的期望从未被真正比较");
            checkedCount += 1;
        }

        Assert.Equal(samples.Count, checkedCount);
        Assert.True(checkedCount >= 80, $"对照样本过少：{checkedCount}");
    }

    /// <summary>负向对照：故意喂错 codec 输入时，golden 期望必须在同一判定路径上失败。</summary>
    [Fact]
    public void WrongCodecInputMustFailTheSameComparisonPath()
    {
        var golden = Samples().Single(sample => sample["id"]!.GetValue<string>() == "G05");
        var wrongDecoded = AttachmentCodec.Decode("{\"v\":1,\"type\":\"batch-begin\"}");
        Assert.False(wrongDecoded.Ok);

        var observed = new WireObservedSample
        {
            Id = "wrong",
            File = golden["file"]!.GetValue<string>(),
            Kind = "golden",
            DecodeOk = false,
            DecodeCode = wrongDecoded.Code,
            Apply = "skip",
        };

        var failures = WireCorpusRunner.CompareExpectation(golden, observed);
        Assert.True(failures.Count > 0, "错误输入竟然通过了 golden 期望");
        Assert.Contains("decode", string.Join(' ', failures), StringComparison.Ordinal);
    }
}
