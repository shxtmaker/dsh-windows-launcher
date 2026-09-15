using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// 跨语言差分测试：把 TS 生产 codec 在**确定性敌意变异输入**上的结论
/// （<c>artifacts/verify-portable/d10-wire-parity-cases.json</c>，由
/// <c>pnpm --dir plugins/dsh-remote-attachments run test:wire</c> 生成）
/// 逐条喂给 C# 生产 codec，要求 ok/拒绝码/canonical 摘要完全一致。
///
/// 与 <see cref="WireCorpusTests"/> 的区别：语料是冻结的 86 条人工样本，
/// 这里比对的是两端对同一批"带伤的"输入是否给出同一个答案。
/// 差分样例不存在时跳过，并在原因里写清生成命令。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class WireParityTests
{
    private static string FixturePath => Path.Combine(WireCorpusRunner.RepoRoot, "artifacts", "verify-portable", "d10-wire-parity-cases.json");

    [Fact]
    public void CodecAgreesWithTheTypeScriptCodecOnMutatedInputs()
    {
        if (!File.Exists(FixturePath))
        {
            Assert.Skip($"缺少差分样例 {FixturePath}；先运行 pnpm --dir plugins/dsh-remote-attachments run test:wire");
            return;
        }

        var fixture = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
        var cases = fixture["cases"]!.AsArray();
        Assert.True(cases.Count > 0, "差分样例不能为空");

        var mismatches = new List<string>();
        foreach (var node in cases)
        {
            var item = node!.AsObject();
            var text = item["text"]!.GetValue<string>();
            var expectedOk = item["ok"]!.GetValue<bool>();
            var expectedCode = item["code"]?.GetValue<string>();
            var expectedHash = item["canonicalHash"]?.GetValue<string>();

            var decoded = AttachmentCodec.Decode(text);
            if (decoded.Ok != expectedOk)
            {
                mismatches.Add($"{item["file"]}#{item["round"]}: TS ok={expectedOk}，C# ok={decoded.Ok}（{decoded.Code}）");
                continue;
            }

            if (!expectedOk)
            {
                if (!string.Equals(decoded.Code, expectedCode, StringComparison.Ordinal))
                {
                    mismatches.Add($"{item["file"]}#{item["round"]}: TS 拒绝 {expectedCode}，C# 拒绝 {decoded.Code}");
                }

                continue;
            }

            var canonical = AttachmentCodec.CanonicalJson(decoded.Canonical);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
            {
                mismatches.Add($"{item["file"]}#{item["round"]}: canonical 摘要不一致（TS {expectedHash}，C# {hash}）");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"跨语言差分失败 {mismatches.Count}/{cases.Count}：" + string.Join(" | ", mismatches.Take(5)));
        Assert.True(cases.Count >= 200, $"差分样例过少：{cases.Count}");
    }
}
