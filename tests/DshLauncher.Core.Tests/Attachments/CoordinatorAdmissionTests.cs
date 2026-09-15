using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D14 顺带修复的源码错误回归：<see cref="AttachmentTransferCoordinator.AdmitFile"/> 过去只校验限额，
/// 不校验将被写进 file-begin 的元数据；非法 fileId/name/mime/sha256 会被收下，
/// 直到 <c>Pump</c> 里构造 file-begin 时才让冻结 codec 抛异常。
/// 现在准入阶段就用同一 codec 判定并把确定码返回给调用方，绝不让调用方输入变成后续的异常。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class CoordinatorAdmissionTests
{
    private const string Bin = "application/octet-stream";

    [Theory]
    [InlineData("has space", "a.bin", Bin)]
    [InlineData("ok-id", "dir/escape.bin", Bin)]
    [InlineData("ok-id", "back\\slash.bin", Bin)]
    [InlineData("ok-id", "", Bin)]
    [InlineData("ok-id", "a.bin", "not-a-mime")]
    [InlineData("ok-id", "a.bin", "")]
    public void AdmitFileRefusesMetadataTheFrozenCodecRejects(string fileId, string name, string mime)
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(16));
        Assert.True(coordinator.OpenBatch("batch-meta", 1, source.ByteLength).Ok);

        var admitted = coordinator.AdmitFile(fileId, name, mime, source);

        Assert.False(admitted.Ok);
        Assert.Equal("invalid-field-value", admitted.Code);
        Assert.True(source.Disposed);

        // 关键：拒绝必须发生在准入，而不是让 Pump 抛异常（也不再发出 file-begin）。
        var report = coordinator.Pump();
        Assert.Null(report.ActiveFileId);
        Assert.Equal(0, harness.Channel.CountOfType("file-begin"));
    }

    [Fact]
    public void AdmitFileRefusesAnOverlongFileId()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(16));
        Assert.True(coordinator.OpenBatch("batch-long-id", 1, source.ByteLength).Ok);

        var admitted = coordinator.AdmitFile(new string('a', AttachmentProtocol.MaxIdChars + 1), "a.bin", Bin, source);

        Assert.False(admitted.Ok);
        Assert.Equal("field-too-large", admitted.Code);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void AdmitFileRefusesAMalformedDeclaredSha256()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var source = new ScriptedByteSource(CoordinatorFixture.Payload(16));
        Assert.True(coordinator.OpenBatch("batch-bad-hash", 1, source.ByteLength).Ok);

        var admitted = coordinator.AdmitFile("file-1", "a.bin", Bin, source, declaredSha256: "NOT-A-HASH");

        Assert.False(admitted.Ok);
        Assert.Equal("invalid-field-value", admitted.Code);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void AdmitFileStillAcceptsValidMetadataAndTransfersEndToEnd()
    {
        using var harness = CoordinatorFixture.NewHarness();
        var coordinator = harness.Coordinator;
        var payload = CoordinatorFixture.Payload(4096);
        var source = new ScriptedByteSource(payload);
        Assert.True(coordinator.OpenBatch("batch-ok", 1, payload.Length).Ok);
        Assert.True(coordinator.AdmitFile("file-1", "报告 v2.txt", "text/plain", source).Ok);

        CoordinatorFixture.Drive(harness);

        Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
        Assert.Equal(AttachmentFilePhase.Staged, coordinator.FileRecord("file-1")!.Phase);
    }
}
