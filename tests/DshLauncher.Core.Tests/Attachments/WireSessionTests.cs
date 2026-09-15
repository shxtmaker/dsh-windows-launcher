using System.Text.Json.Nodes;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D10 状态机与完整性测试（C# 侧），与 TS 侧
/// <c>test/unit/wire-session.test.mjs</c> 覆盖同一组语义。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class WireSessionTests
{
    private static readonly string[] TwoStagedIds = ["att-1", "att-2"];

    private static readonly string[] CancelStagedIds = ["att-9"];

    private static readonly string[] StalledIdList = ["file-1"];

    private static AttachmentMessage Read(string relativePath)
    {
        var decoded = AttachmentCodec.Decode(WireCorpusRunner.LoadSampleText(relativePath));
        Assert.True(decoded.Ok, $"{relativePath} 必须解码成功：{decoded.Code}");
        return decoded.Message!;
    }

    private static AttachmentApplyResult Feed(AttachmentSession session, string relativePath) =>
        session.Apply(Read(relativePath));

    private static AttachmentMessage Mint(string json)
    {
        var decoded = AttachmentCodec.Decode(json);
        Assert.True(decoded.Ok, $"测试消息必须解码成功：{decoded.Code}");
        return decoded.Message!;
    }

    /// <summary>三态断言：任何一层被另一层顶替都会返回失败描述。</summary>
    private static string? TriStateFailure(
        AttachmentFileRecord? record,
        string transport,
        string draft,
        string upload)
    {
        if (record is null) return "缺少单文件记录";
        var actualTransport = record.Transport switch
        {
            AttachmentTransportState.Buffering => "buffering",
            AttachmentTransportState.Buffered => "buffered",
            _ => "idle",
        };
        var actualDraft = record.Draft switch
        {
            AttachmentDraftState.Staged => "staged",
            AttachmentDraftState.Failed => "failed",
            AttachmentDraftState.Partial => "partial",
            _ => "none",
        };
        var actualUpload = record.Upload == AttachmentUploadState.HarnessOwned ? "harness-owned" : "none";
        if (actualTransport != transport) return $"transport 期望 {transport}，实际 {actualTransport}";
        if (actualDraft != draft) return $"draft 期望 {draft}，实际 {actualDraft}";
        if (actualUpload != upload) return $"upload 期望 {upload}，实际 {actualUpload}";
        return null;
    }

    [Fact]
    public void TransportAckOnlyProvesTheReceiveBufferAcceptedBytes()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Feed(session, "golden/chunk-8b-seq0.json");
        Assert.Null(TriStateFailure(session.FileRecord("file-1"), "buffering", "none", "none"));

        Feed(session, "golden/ack-8b-seq0.json");
        var afterAck = session.FileRecord("file-1");
        Assert.Null(TriStateFailure(afterAck, "buffered", "none", "none"));

        var ended = Feed(session, "golden/file-end-8b.json");
        Assert.True(ended.Ok);
        Assert.True(ended.ImportInvoked, "file-end 干净时必须触发一次草稿导入");
        Assert.Null(TriStateFailure(session.FileRecord("file-1"), "buffered", "none", "none"));

        Feed(session, "golden/import-result-staged-1.json");
        Assert.Null(TriStateFailure(session.FileRecord("file-1"), "buffered", "staged", "harness-owned"));

        // 负向对照：同一断言函数必须能拒绝被顶替的三态，否则上面的断言是空的。
        Assert.NotNull(TriStateFailure(afterAck, "buffered", "staged", "none"));
        Assert.NotNull(TriStateFailure(afterAck, "staged", "none", "none"));
        Assert.NotNull(TriStateFailure(afterAck, "buffered", "none", "ready"));
    }

    [Fact]
    public void ImportResultStagedDoesNotMeanUploadReady()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Feed(session, "golden/chunk-8b-seq0.json");
        Feed(session, "golden/ack-8b-seq0.json");
        Feed(session, "golden/file-end-8b.json");
        Feed(session, "golden/import-result-staged-1.json");

        var record = session.FileRecord("file-1")!;
        Assert.Equal(AttachmentDraftState.Staged, record.Draft);
        Assert.Equal(AttachmentUploadState.HarnessOwned, record.Upload);

        // 线协议里根本没有 upload ready/uploaded 取值。
        Assert.DoesNotContain(AttachmentProtocol.ResultStatuses, status => status.Contains("ready", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(AttachmentProtocol.ResultStatuses, status => status.Contains("uploaded", StringComparison.OrdinalIgnoreCase));
        var uploadReady = AttachmentCodec.Decode(
            "{\"v\":1,\"type\":\"import-result\",\"sessionId\":\"session-1\",\"batchId\":\"batch-1\",\"fileId\":\"file-1\",\"status\":\"upload-ready\",\"attachmentIds\":[]}");
        Assert.False(uploadReady.Ok);
        Assert.Equal("unknown-enum-value", uploadReady.Code);
    }

    [Fact]
    public void HashAndByteCountMismatchesNeverImport()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Feed(session, "golden/chunk-8b-seq0.json");
        Feed(session, "golden/ack-8b-seq0.json");

        var wrongBytes = session.Apply(Read("malicious/file-end-byte-count-mismatch.json"));
        Assert.False(wrongBytes.Ok);
        Assert.Equal("size-mismatch", wrongBytes.Code);
        Assert.False(session.FileRecord("file-1")!.Ended, "校验失败不得把文件标记为已结束");

        var wrongHash = session.Apply(Mint(
            "{\"v\":1,\"type\":\"file-end\",\"sessionId\":\"session-1\",\"batchId\":\"batch-1\",\"fileId\":\"file-1\",\"totalBytes\":8,\"sha256\":\"" + new string('f', 64) + "\"}"));
        Assert.False(wrongHash.Ok);
        Assert.Equal("hash-mismatch", wrongHash.Code);

        var good = Feed(session, "golden/file-end-8b.json");
        Assert.True(good.Ok);
        Assert.True(good.ImportInvoked);
    }

    [Fact]
    public void SubmittedItemsDecidesTheExpectedNewIdCount()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Feed(session, "golden/chunk-8b-seq0.json");
        Feed(session, "golden/ack-8b-seq0.json");

        var ended = session.Apply(Read("golden/file-end-submitted-2.json"));
        Assert.True(ended.Ok);
        Assert.True(ended.ImportInvoked);

        var oneId = session.Apply(Mint(
            "{\"v\":1,\"type\":\"import-result\",\"sessionId\":\"session-1\",\"batchId\":\"batch-1\",\"fileId\":\"file-1\",\"status\":\"staged\",\"attachmentIds\":[\"att-1\"]}"));
        Assert.False(oneId.Ok);
        Assert.Equal("import-id-count-mismatch", oneId.Code);

        var twoIds = session.Apply(Mint(
            "{\"v\":1,\"type\":\"import-result\",\"sessionId\":\"session-1\",\"batchId\":\"batch-1\",\"fileId\":\"file-1\",\"status\":\"staged\",\"attachmentIds\":[\"att-1\",\"att-2\"]}"));
        Assert.True(twoIds.Ok, twoIds.Code);
        Assert.Equal(TwoStagedIds, session.FileRecord("file-1")!.AttachmentIds);
    }

    [Fact]
    public void RepeatedChunkFileEndAndBatchEndNeverImportTwice()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Feed(session, "golden/chunk-8b-seq0.json");

        var repeatedChunk = Feed(session, "golden/chunk-8b-seq0.json");
        Assert.False(repeatedChunk.Ok);
        Assert.Equal("seq-overlap", repeatedChunk.Code);

        Feed(session, "golden/ack-8b-seq0.json");
        Assert.True(Feed(session, "golden/file-end-8b.json").ImportInvoked);

        var secondEnd = Feed(session, "golden/file-end-8b.json");
        Assert.True(secondEnd.Ok);
        Assert.True(secondEnd.Duplicate);
        Assert.False(secondEnd.ImportInvoked, "重复 file-end 不得再次导入");

        Feed(session, "golden/import-result-staged-1.json");
        Assert.True(Feed(session, "golden/batch-end-staged.json").Ok);

        var secondBatchEnd = Feed(session, "golden/batch-end-staged.json");
        Assert.True(secondBatchEnd.Ok);
        Assert.True(secondBatchEnd.Duplicate);
        Assert.False(secondBatchEnd.ImportInvoked, "重复 batch-end 不得再次导入");

        var lateChunk = Feed(session, "golden/chunk-8b-seq0.json");
        Assert.False(lateChunk.Ok);
        Assert.Equal("batch-closed", lateChunk.Code);
    }

    [Fact]
    public void ReplayCacheNeverEvictsActiveOperationsAndExpiresByIdTtl()
    {
        long now = 5_000;
        var summary = new AttachmentReplaySummary(true, [], "none", null);
        var cache = new AttachmentReplayCache(capacity: 1, lifetimeMs: AttachmentProtocol.ReplayCacheLifetimeMs, now: () => now);

        cache.Put("file", 7, 3, "batch-1", "file-1", "a", summary);
        cache.Put("file", 7, 3, "batch-1", "file-2", "b", summary);
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.RefusedEvictionCount);
        Assert.NotNull(cache.Get(AttachmentProtocol.ReplayKey(7, 3, "batch-1", "file-1")));

        cache.Complete(AttachmentProtocol.ReplayKey(7, 3, "batch-1", "file-1"), summary, "a");
        cache.Put("file", 7, 3, "batch-1", "file-3", "c", summary);
        Assert.Equal(1, cache.EvictedCount);
        Assert.NotNull(cache.Get(AttachmentProtocol.ReplayKey(7, 3, "batch-1", "file-2")));

        now += AttachmentProtocol.ReplayCacheLifetimeMs - 1;
        Assert.Equal(2, cache.Count);
        now += 2;
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Get(AttachmentProtocol.ReplayKey(7, 3, "batch-1", "file-2")));
    }

    [Fact]
    public void CancelKeepsConfirmedDraftsAndRejectsLateMessages()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-two.json");
        Feed(session, "golden/file-begin-two-a.json");

        var chunkJson = JsonNode.Parse(WireCorpusRunner.LoadSampleText("golden/chunk-8b-seq0.json"))!.AsObject();
        chunkJson["batchId"] = "batch-6";
        var chunk = Mint(chunkJson.ToJsonString());
        Assert.True(session.Apply(chunk).Ok);

        var ack = Mint("{\"v\":1,\"type\":\"ack\",\"sessionId\":\"session-1\",\"batchId\":\"batch-6\",\"fileId\":\"file-1\",\"seq\":0,\"offset\":0,\"byteLength\":8,\"bufferedBytes\":8,\"inFlight\":0}");
        Assert.True(session.Apply(ack).Ok);

        var end = Mint("{\"v\":1,\"type\":\"file-end\",\"sessionId\":\"session-1\",\"batchId\":\"batch-6\",\"fileId\":\"file-1\",\"totalBytes\":8,\"sha256\":\"9c56cc51b374c3ba189210d5b6d4bf57790d351c96c47c02190ecf1e430635ab\"}");
        Assert.True(session.Apply(end).Ok);

        var staged = Mint("{\"v\":1,\"type\":\"import-result\",\"sessionId\":\"session-1\",\"batchId\":\"batch-6\",\"fileId\":\"file-1\",\"status\":\"staged\",\"attachmentIds\":[\"att-9\"]}");
        Assert.True(session.Apply(staged).Ok);

        var cancel = Mint("{\"v\":1,\"type\":\"cancel\",\"sessionId\":\"session-1\",\"batchId\":\"batch-6\",\"reason\":\"cancelled\",\"stage\":\"protocol-transfer\"}");
        Assert.True(session.Apply(cancel).Ok);
        Assert.Equal(CancelStagedIds, session.FileRecord("file-1")!.AttachmentIds);

        var late = session.Apply(chunk);
        Assert.False(late.Ok);
        Assert.Equal("cancelled", late.Code);

        // 又：幂等重放一个已 staged 的结果同样不再导入。
        var repeat = session.Apply(staged);
        Assert.False(repeat.Ok);
        Assert.Equal("cancelled", repeat.Code);
    }

    [Fact]
    public void CancelReleasesTheSessionSoANewBatchIdIsAdmitted()
    {
        // R15 回归：取消只置 Cancelled 不置 Closed，若准入只看 !Closed，用户取消一次就
        // 再也贴不进来（新批次被永久拒为 batch-in-progress）。取消必须释放会话。
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");

        var cancel = Mint("{\"v\":1,\"type\":\"cancel\",\"sessionId\":\"session-1\",\"batchId\":\"batch-1\",\"reason\":\"cancelled\",\"stage\":\"protocol-transfer\"}");
        Assert.True(session.Apply(cancel).Ok);

        // 复用被取消的 batchId 仍是重复操作（此时会话里没有别的开放批次）。
        var reused = session.Apply(Mint(WireCorpusRunner.LoadSampleText("golden/batch-begin-single.json")));
        Assert.False(reused.Ok);
        Assert.Equal("duplicate-operation", reused.Code);

        // 换一个新的 batchId：必须被 batch-begin 承认，而不是 batch-in-progress（回归点）。
        var reopened = JsonNode.Parse(WireCorpusRunner.LoadSampleText("golden/batch-begin-single.json"))!.AsObject();
        reopened["batchId"] = "batch-1-reopened";
        var admitted = session.Apply(Mint(reopened.ToJsonString()));
        Assert.True(admitted.Ok, admitted.Ok ? string.Empty : admitted.Code);
        Assert.NotEqual("batch-in-progress", admitted.Code);
    }

    [Fact]
    public void NavigationClearsTheCacheAndExpiresIdentity()
    {
        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Assert.True(session.ReplayCacheSize > 0);

        session.Navigate();
        Assert.Equal(0, session.ReplayCacheSize);
        Assert.True(session.IdentityExpired);

        var late = Feed(session, "golden/file-end-8b.json");
        Assert.False(late.Ok);
        Assert.Equal("context-changed", late.Code);
        Assert.Null(session.FileRecord("file-1"));

        Feed(session, "golden/context-basic.json");
        Assert.False(session.IdentityExpired);
    }

    [Fact]
    public void TimeoutsCancelStalledOperationsAndReleaseBuffers()
    {
        long now = 1_000;
        using var session = new AttachmentSession(now: () => now);
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-single.json");
        Feed(session, "golden/file-begin-8b.json");
        Feed(session, "golden/chunk-8b-seq0.json");

        var quiet = session.CheckTimeouts();
        Assert.Empty(quiet.StalledFileIds);
        Assert.Null(quiet.CancelledBatchId);

        now += AttachmentProtocol.AckTimeoutMs + 1;
        var stalled = session.CheckTimeouts();
        Assert.Equal(StalledIdList, stalled.StalledFileIds);
        Assert.Equal("batch-1", stalled.CancelledBatchId);

        var late = Feed(session, "golden/chunk-8b-seq0.json");
        Assert.False(late.Ok);
        Assert.Equal("cancelled", late.Code);

        long idleNow = 2_000;
        using var idleSession = new AttachmentSession(now: () => idleNow);
        Feed(idleSession, "golden/context-basic.json");
        Feed(idleSession, "golden/batch-begin-single.json");
        idleNow += AttachmentProtocol.BatchIdleTimeoutMs + 1;
        Assert.Equal("batch-1", idleSession.CheckTimeouts().CancelledBatchId);
    }

    [Fact]
    public void WindowAndSequenceRulesCannotBeBypassed()
    {
        Assert.Equal(262_144, AttachmentProtocol.ChunkBytes);
        Assert.Equal(2, AttachmentProtocol.MaxChunksInFlight);

        using var session = new AttachmentSession();
        Feed(session, "golden/context-basic.json");
        Feed(session, "golden/batch-begin-24b.json");
        Feed(session, "golden/file-begin-24b.json");
        Feed(session, "golden/chunk-24b-seq0.json");
        Feed(session, "golden/chunk-24b-seq1.json");
        var third = session.Apply(Read("malicious/window-overflow.json"));
        Assert.False(third.Ok);
        Assert.Equal("window-overflow", third.Code);

        var tooBig = AttachmentCodec.Decode(
            "{\"v\":1,\"type\":\"chunk\",\"sessionId\":\"session-1\",\"batchId\":\"batch-3\",\"fileId\":\"file-1\",\"seq\":0,\"offset\":0,\"byteLength\":262145,\"dataBase64\":\"AAAA\"}");
        Assert.False(tooBig.Ok);
        Assert.Equal("integer-out-of-range", tooBig.Code);
    }

    [Fact]
    public void StrictBase64IsRequired()
    {
        Assert.True(AttachmentCodec.IsCanonicalBase64("YWJjZGVmZ2g="));
        Assert.False(AttachmentCodec.IsCanonicalBase64("YWJjZGVmZ2g"));
        Assert.False(AttachmentCodec.IsCanonicalBase64("YWJj ZGVmZ2g="));
        Assert.False(AttachmentCodec.IsCanonicalBase64("YWJjZGVmZ2g=="));

        var invalid = AttachmentCodec.Decode(
            "{\"v\":1,\"type\":\"chunk\",\"sessionId\":\"session-1\",\"batchId\":\"batch-1\",\"fileId\":\"file-1\",\"seq\":0,\"offset\":0,\"byteLength\":8,\"dataBase64\":\"!!!!not-base64!!!!\"}");
        Assert.False(invalid.Ok);
        Assert.Equal("invalid-field-value", invalid.Code);
    }

    [Fact]
    public void CaseVariantFieldNamesAndUnknownFieldsAreRejectedSeparately()
    {
        var caseVariant = AttachmentCodec.Decode(
            "{\"v\":1,\"type\":\"chunk\",\"SessionId\":\"session-1\",\"batchId\":\"batch-1\",\"fileId\":\"file-1\",\"seq\":0,\"offset\":0,\"byteLength\":8,\"dataBase64\":\"YWJjZGVmZ2g=\"}");
        Assert.False(caseVariant.Ok);
        Assert.Equal("field-name-invalid", caseVariant.Code);

        var unknown = AttachmentCodec.Decode(
            "{\"v\":1,\"type\":\"context\",\"sessionId\":\"session-1\",\"targetId\":\"target-1\",\"documentEpoch\":7,\"composerEpoch\":3,\"composerScope\":\"scope-a\",\"localPath\":\"/etc/passwd\"}");
        Assert.False(unknown.Ok);
        Assert.Equal("unknown-field", unknown.Code);
    }
}
