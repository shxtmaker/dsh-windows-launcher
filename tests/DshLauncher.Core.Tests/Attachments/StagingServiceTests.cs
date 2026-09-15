using System.Security.Cryptography;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D14 生产暂存服务的 Linux 可执行测试：顺序流式复制、边复制边算 SHA-256、
/// 写入前限额、复制中变化检测、取消、半成品删除、句柄纪律与清理归属。
/// 全部通过假端口驱动真实生产代码，不碰磁盘、不碰 Windows API。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class StagingServiceTests
{
    private const int Chunk = AttachmentProtocol.ChunkBytes;

    /// <summary>统一的取消令牌入口：显式传入 xunit 的测试取消令牌。</summary>
    private static StagingCaptureResult Capture(
        AttachmentStagingService service,
        FakeNativeCapture capture,
        CancellationToken? cancellationToken = null) =>
        service.Capture(capture, cancellationToken ?? TestContext.Current.CancellationToken);

    [Fact]
    public void CaptureStreamsSequentiallyAndReturnsTheRealSha256()
    {
        using var fixture = new StagingFixture();
        var payload = StagingFixture.Payload((2 * Chunk) + 123);
        var capture = StagingFixture.Capture(payload);
        var source = capture.Source!;

        var result = Capture(fixture.Service, capture);

        Assert.True(result.Ok);
        Assert.Equal(payload.LongLength, result.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Sha256);
        Assert.NotNull(result.SnapshotId);
        Assert.NotNull(result.StagedPath);
        Assert.True(result.SourceDisposed);
        Assert.True(result.DestinationDisposed);
        Assert.False(result.StagedFileDeleted);

        var destination = Assert.Single(fixture.FileSystem.Destinations);
        Assert.Equal(payload, destination.Written.ToArray());
        Assert.True(destination.Flushed);
        Assert.Equal(1, destination.DisposeCount);
        Assert.Equal(1, source.DisposeCount);

        // 顺序流式：每次请求都不超过冻结块大小，写入也从不整文件缓冲。
        Assert.All(source.RequestedLengths, length => Assert.InRange(length, 1, Chunk));
        Assert.InRange(destination.MaxWriteLength, 1, Chunk);
        Assert.True(source.RequestedLengths.Count >= 3);

        // 暂存路径必须落在自有根内，且创建请求就是解析后的叶路径。
        Assert.StartsWith(FakeStagingFileSystem.Root + @"\", result.StagedPath, StringComparison.Ordinal);
        Assert.Equal(result.StagedPath, Assert.Single(fixture.FileSystem.CreateRequests));

        // trace 只记录判定码与字节数，不记录完整本机路径。
        Assert.DoesNotContain(@"C:\", fixture.Service.Trace.Entries[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureToleratesShortReadsWithoutBufferingTheWholeFile()
    {
        using var fixture = new StagingFixture();
        var payload = StagingFixture.Payload(5000);
        var capture = StagingFixture.Capture(payload);
        capture.Source!.MaxReturnPerRead = 1000;

        var result = Capture(fixture.Service, capture);

        Assert.True(result.Ok);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Sha256);
        var destination = Assert.Single(fixture.FileSystem.Destinations);
        Assert.Equal(payload, destination.Written.ToArray());
        Assert.InRange(destination.MaxWriteLength, 1, 1000);
        // 5 次内容读取 + 1 次增长复核读取。
        Assert.Equal(6, capture.Source.ReadCount);
    }

    [Fact]
    public void CaptureGivesEverySnapshotADistinctNameUnderTheOwnedRoot()
    {
        using var fixture = new StagingFixture();

        var first = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(8)));
        var second = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(8)));

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.StagedPath, second.StagedPath);
        Assert.All(
            fixture.FileSystem.CreateRequests,
            path => Assert.StartsWith(FakeStagingFileSystem.Root + @"\", path, StringComparison.Ordinal));
        Assert.Equal(2, fixture.Service.StagedSnapshotCount);
        Assert.Equal(16, fixture.Service.StagedBytes);
    }

    [Fact]
    public void CaptureRefusesADirectoryCandidateWithoutCreatingAnything()
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(
            StagingFixture.Payload(4),
            StagingFixture.Descriptor(4, kind: StagingSourceKind.Directory));

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.Directory, result.Code);
        Assert.Empty(fixture.FileSystem.CreateRequests);
        Assert.True(result.SourceDisposed);
        Assert.False(result.DestinationDisposed);
    }

    [Theory]
    [InlineData(true, false, false, false, AttachmentStagingCodes.ReparsePoint)]
    [InlineData(false, true, false, false, AttachmentStagingCodes.CloudPlaceholder)]
    [InlineData(false, false, true, false, AttachmentStagingCodes.OfflineFile)]
    [InlineData(false, false, false, true, AttachmentStagingCodes.NetworkShare)]
    public void CaptureRefusesEveryUnsupportedPlatformFact(
        bool reparse,
        bool placeholder,
        bool offline,
        bool network,
        string expectedCode)
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(
            StagingFixture.Payload(4),
            StagingFixture.Descriptor(
                4,
                isReparsePoint: reparse,
                isCloudPlaceholder: placeholder,
                isOffline: offline,
                isNetworkShare: network));

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(fixture.FileSystem.CreateRequests);
    }

    [Fact]
    public void CaptureMapsAPlatformOpenFailureToItsStableCode()
    {
        using var fixture = new StagingFixture();
        var capture = new FakeNativeCapture
        {
            CaptureId = "capture-locked",
            SuggestedLeafName = "locked.bin",
            OpenFailure = new AttachmentStagingException(AttachmentStagingCodes.SourceLocked, "共享冲突"),
        };

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceLocked, result.Code);
        Assert.Empty(fixture.FileSystem.CreateRequests);
    }

    [Fact]
    public void CaptureFailsWhenTheSourceShrinksMidCopyAndDeletesThePartialFile()
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(
            StagingFixture.Payload(10),
            StagingFixture.Descriptor(4096));
        var source = capture.Source!;

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceChanged, result.Code);
        Assert.True(result.StagedFileDeleted);
        Assert.True(result.SourceDisposed);
        Assert.True(result.DestinationDisposed);
        Assert.Empty(fixture.FileSystem.Entries);
        Assert.Equal(0, fixture.Service.StagedSnapshotCount);
        Assert.Equal(1, source.DisposeCount);

        // 半成品由目标句柄删除，而不是重新按路径删除。
        Assert.Empty(fixture.FileSystem.DeletedPaths);
    }

    [Fact]
    public void CaptureFailsWhenTheSourceGrowsMidCopyAndDeletesThePartialFile()
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(StagingFixture.Payload(64), StagingFixture.Descriptor(32));

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceChanged, result.Code);
        Assert.True(result.StagedFileDeleted);
        Assert.Equal(1, Assert.Single(fixture.FileSystem.Destinations).DisposeCount);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("fileid")]
    [InlineData("missing")]
    public void CaptureFailsWhenTheSourceIdentityChangesMidCopy(string mutation)
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(StagingFixture.Payload(128));
        var source = capture.Source!;
        source.AfterIdentity = mutation switch
        {
            "time" => source.InitialIdentity with { LastWriteTimeUtcTicks = source.InitialIdentity.LastWriteTimeUtcTicks + 1 },
            "fileid" => source.InitialIdentity with { FileId = source.InitialIdentity.FileId + 1 },
            _ => StagingSourceIdentity.Missing,
        };

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceChanged, result.Code);
        Assert.True(result.StagedFileDeleted);
        Assert.Equal(0, fixture.Service.StagedSnapshotCount);
    }

    [Fact]
    public void CaptureFailsWhenTheDestinationWriteFailsAndDeletesThePartialFile()
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(StagingFixture.Payload(2048));
        var failingPath = FakeStagingFileSystem.Root + @"\failing.bin";
        var destination = new FakeDestinationFile(
            fixture.FileSystem,
            new FakeOwnedEntry { FullPath = failingPath, FinalPath = failingPath })
        {
            FailOnWrite = true,
        };
        fixture.FileSystem.NextDestination = destination;

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.DestinationWriteFailed, result.Code);
        Assert.True(result.StagedFileDeleted);
        Assert.True(destination.Deleted);
        Assert.Equal(1, destination.DisposeCount);
        Assert.True(result.SourceDisposed);
        Assert.Equal(1, capture.Source!.DisposeCount);
    }

    [Fact]
    public void CaptureFailsWhenTheDestinationCannotBeCreated()
    {
        using var fixture = new StagingFixture();
        fixture.FileSystem.CreateRefused = _ => true;
        var capture = StagingFixture.Capture(StagingFixture.Payload(32));

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.DestinationCreateFailed, result.Code);
        Assert.False(result.StagedFileDeleted);
        Assert.False(result.DestinationDisposed);
        Assert.True(result.SourceDisposed);
        Assert.Equal(1, capture.Source!.DisposeCount);
    }

    [Fact]
    public void CaptureRefusesWhenFreeSpaceCannotCoverTheFileAndTheReserve()
    {
        using var fixture = new StagingFixture(freeSpaceReserveBytes: 1024);
        fixture.FileSystem.FreeBytes = 2048;
        var capture = StagingFixture.Capture(StagingFixture.Payload(2048));

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.InsufficientFreeSpace, result.Code);
        Assert.Empty(fixture.FileSystem.CreateRequests);
        Assert.False(result.DestinationDisposed);
    }

    [Fact]
    public void CaptureRefusesAFileOverTheFrozenFileLimit()
    {
        using var fixture = new StagingFixture(
            new AttachmentLimits { MaxFileBytes = 16, MaxBatchBytes = 1024, MaxStagingBytesPerTarget = 1024 });
        var capture = StagingFixture.Capture(StagingFixture.Payload(17));

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal("limit-file-bytes", result.Code);
        Assert.Empty(fixture.FileSystem.CreateRequests);
    }

    [Fact]
    public void CaptureRefusesBeyondThePerTargetStagingCap()
    {
        using var fixture = new StagingFixture(
            new AttachmentLimits
            {
                MaxFileBytes = 100,
                MaxBatchBytes = 100_000,
                MaxFilesPerBatch = 10,
                MaxStagingBytesPerTarget = 150,
            });

        Assert.True(Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(100))).Ok);
        var second = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(60)));

        Assert.False(second.Ok);
        Assert.Equal("limit-staging-bytes", second.Code);
        Assert.Equal(1, fixture.Service.StagedSnapshotCount);
        Assert.Equal(100, fixture.Service.StagedBytes);
    }

    [Fact]
    public void CaptureRefusesBeyondTheBatchByteLimit()
    {
        using var fixture = new StagingFixture(
            new AttachmentLimits
            {
                MaxFileBytes = 100,
                MaxBatchBytes = 150,
                MaxFilesPerBatch = 10,
                MaxStagingBytesPerTarget = 10_000,
            });

        Assert.True(Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(100))).Ok);
        var second = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(60)));

        Assert.False(second.Ok);
        Assert.Equal("limit-batch-bytes", second.Code);
        Assert.Equal(100, fixture.Service.BatchBytes);
    }

    [Fact]
    public void CaptureRefusesBeyondTheBatchFileCount()
    {
        using var fixture = new StagingFixture();
        Assert.True(fixture.Service.BeginBatch("batch-2", 2).Ok);
        Assert.True(Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(4))).Ok);
        Assert.True(Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(4))).Ok);

        var third = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(4)));

        Assert.False(third.Ok);
        Assert.Equal("limit-batch-files", third.Code);
        Assert.Equal(2, fixture.Service.BatchFileCount);
    }

    [Fact]
    public void CaptureStopsAtTheBoundedLedgerCapacity()
    {
        using var fixture = new StagingFixture();
        for (var index = 0; index < AttachmentStagingService.MaxStagedSnapshots; index += 1)
        {
            if (index % AttachmentProtocol.MaxFilesPerBatch == 0)
            {
                Assert.True(fixture.Service.BeginBatch("batch-" + index, AttachmentProtocol.MaxFilesPerBatch).Ok);
            }

            Assert.True(Capture(fixture.Service, StagingFixture.Capture([])).Ok);
        }

        var overflow = Capture(fixture.Service, StagingFixture.Capture([]));

        Assert.False(overflow.Ok);
        Assert.Equal("limit-staging-bytes", overflow.Code);
        Assert.Equal(AttachmentStagingService.MaxStagedSnapshots, fixture.Service.StagedSnapshotCount);
    }

    [Fact]
    public void BeginBatchValidatesTheDeclaredFileCount()
    {
        using var fixture = new StagingFixture();

        Assert.False(fixture.Service.BeginBatch("batch-x", 0).Ok);
        Assert.Equal("limit-batch-files", fixture.Service.BeginBatch("batch-x", 0).Code);
        Assert.False(fixture.Service.BeginBatch("batch-x", AttachmentProtocol.MaxFilesPerBatch + 1).Ok);
        Assert.True(fixture.Service.BeginBatch("batch-x", AttachmentProtocol.MaxFilesPerBatch).Ok);
    }

    [Fact]
    public void CaptureRequiresAnOpenBatch()
    {
        using var fixture = new StagingFixture();
        using var service = new AttachmentStagingService(fixture.Root, fixture.FileSystem);

        var result = Capture(service, StagingFixture.Capture(StagingFixture.Payload(4)));

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.BatchNotOpen, result.Code);
    }

    [Fact]
    public void CaptureIsCancelledBetweenChunksAndCleansUpBothHandles()
    {
        using var fixture = new StagingFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var payload = StagingFixture.Payload((2 * Chunk) + 5);
        var capture = StagingFixture.Capture(payload);
        var source = capture.Source!;
        source.OnRead = () =>
        {
            if (source.ReadCount == 2)
            {
                cancellation.Cancel();
            }
        };

        var result = Capture(fixture.Service, capture, cancellation.Token);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.Cancelled, result.Code);
        Assert.True(result.StagedFileDeleted);
        Assert.True(result.SourceDisposed);
        Assert.True(result.DestinationDisposed);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, Assert.Single(fixture.FileSystem.Destinations).DisposeCount);

        // 取消发生在第二块之后：只读了两块，没有整文件读入。
        Assert.Equal(2, source.ReadCount);
        Assert.True(source.Position < payload.Length);
    }

    [Fact]
    public void CaptureRefusesWhenCancelledBeforeOpeningTheSource()
    {
        using var fixture = new StagingFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var capture = StagingFixture.Capture(StagingFixture.Payload(4));

        var result = Capture(fixture.Service, capture, cancellation.Token);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.Cancelled, result.Code);
        Assert.Equal(0, capture.OpenCount);
    }

    [Fact]
    public void CaptureRefusesADestinationWhoseResolvedPathEscapesTheOwnedRoot()
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(StagingFixture.Payload(32));
        var destination = new FakeDestinationFile(
            fixture.FileSystem,
            new FakeOwnedEntry { FullPath = FakeStagingFileSystem.Root + @"\snapshot.bin" });
        destination.EscapeTo(@"C:\Users\me\.ssh\id_rsa");
        fixture.FileSystem.NextDestination = destination;

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.DestinationOutsideOwnedRoot, result.Code);
        Assert.True(result.StagedFileDeleted);
        Assert.True(destination.Deleted);
        Assert.True(result.DestinationDisposed);
        Assert.True(result.SourceDisposed);
        Assert.Empty(fixture.FileSystem.Entries);
    }

    [Fact]
    public void CaptureSurfacesAnIllegalReadLengthAsASourceFailure()
    {
        using var fixture = new StagingFixture();
        var capture = StagingFixture.Capture(StagingFixture.Payload(4096));
        capture.Source!.ReturnIllegalLength = true;

        var result = Capture(fixture.Service, capture);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceReadFailed, result.Code);
        Assert.True(result.StagedFileDeleted);
    }

    [Fact]
    public void ReleaseDeletesOnlyTheLedgerSnapshot()
    {
        using var fixture = new StagingFixture();
        var capture = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(64)));
        Assert.True(capture.Ok);

        var released = fixture.Service.Release(capture.SnapshotId!, TestContext.Current.CancellationToken);

        Assert.True(released.Ok);
        Assert.Equal(1, released.RemovedCount);
        Assert.Empty(fixture.FileSystem.Entries);
        Assert.Equal(capture.StagedPath, Assert.Single(fixture.FileSystem.DeletedPaths));
        Assert.Equal(0, fixture.Service.StagedSnapshotCount);

        var again = fixture.Service.Release(capture.SnapshotId!, TestContext.Current.CancellationToken);
        Assert.False(again.Ok);
        Assert.Equal(AttachmentStagingCodes.SnapshotUnknown, again.Code);
    }

    [Fact]
    public void ReleaseRefusesAnUnknownSnapshotIdWithoutTouchingAnyPath()
    {
        using var fixture = new StagingFixture();
        const string Outside = @"C:\Users\me\.ssh\id_rsa";
        fixture.FileSystem.AddEntry(Outside);

        var result = fixture.Service.Release(Outside, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SnapshotUnknown, result.Code);
        Assert.Empty(fixture.FileSystem.DeletedPaths);
        Assert.True(fixture.FileSystem.Entries.ContainsKey(Outside));
    }

    [Fact]
    public void ReleaseRefusesWhenTheResolvedFinalPathLeavesTheOwnedRoot()
    {
        using var fixture = new StagingFixture();
        var capture = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(64)));
        Assert.True(capture.Ok);
        fixture.FileSystem.Entries[capture.StagedPath!].FinalPath = @"C:\Users\me\.ssh\id_rsa";

        var released = fixture.Service.Release(capture.SnapshotId!, TestContext.Current.CancellationToken);

        Assert.False(released.Ok);
        Assert.Equal(AttachmentStagingCodes.CleanupOutsideOwnedRoot, released.Code);
        Assert.Empty(fixture.FileSystem.DeletedPaths);
        Assert.Equal(1, fixture.Service.StagedSnapshotCount);
    }

    [Fact]
    public void ReleaseRefusesAReparsePointThatReplacedTheSnapshot()
    {
        using var fixture = new StagingFixture();
        var capture = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(64)));
        Assert.True(capture.Ok);
        fixture.FileSystem.Entries[capture.StagedPath!].IsReparsePoint = true;

        var released = fixture.Service.Release(capture.SnapshotId!, TestContext.Current.CancellationToken);

        Assert.False(released.Ok);
        Assert.Equal(AttachmentStagingCodes.CleanupReparsePoint, released.Code);
        Assert.Empty(fixture.FileSystem.DeletedPaths);
    }

    [Fact]
    public void CleanupOrphansDeletesOnlyEntriesConfinedToTheOwnedRoot()
    {
        using var fixture = new StagingFixture();
        fixture.FileSystem.AddEntry(FakeStagingFileSystem.Root + @"\orphan-1.bin");
        fixture.FileSystem.AddEntry(FakeStagingFileSystem.Root + @"\..\..\Windows\evil.dll");
        fixture.FileSystem.AddEntry(@"C:\Windows\System32\evil.dll");
        fixture.FileSystem.AddEntry(@"\\server\share\evil.dll");
        fixture.FileSystem.AddEntry(
            FakeStagingFileSystem.Root + @"\link.bin",
            isReparsePoint: true,
            finalPath: @"C:\Users\me\.ssh\id_rsa");
        fixture.FileSystem.AddEntry(FakeStagingFileSystem.Root + @"\subdir", isDirectory: true);
        fixture.FileSystem.AddEntry(FakeStagingFileSystem.Root + @"\busy.bin", deleteFails: true);

        var result = fixture.Service.CleanupOrphans(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(6, result.RefusedCount);
        Assert.Equal(new[] { FakeStagingFileSystem.Root + @"\orphan-1.bin" }, fixture.FileSystem.DeletedPaths);

        var codes = result.Outcomes.Where(outcome => !outcome.Deleted).Select(outcome => outcome.Code).ToList();
        Assert.Contains(AttachmentStagingCodes.CleanupOutsideOwnedRoot, codes);
        Assert.Contains(AttachmentStagingCodes.CleanupReparsePoint, codes);
        Assert.Contains(AttachmentStagingCodes.CleanupNotRegularFile, codes);
        Assert.Contains(AttachmentStagingCodes.CleanupFailed, codes);

        // 根外文件必须原样保留。
        Assert.True(fixture.FileSystem.Entries.ContainsKey(@"C:\Windows\System32\evil.dll"));
        Assert.True(fixture.FileSystem.Entries.ContainsKey(@"\\server\share\evil.dll"));
    }

    [Fact]
    public void CleanupOrphansDropsLedgerEntriesWhoseFilesAlreadyDisappeared()
    {
        using var fixture = new StagingFixture();
        Assert.True(Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(16))).Ok);
        Assert.Equal(1, fixture.Service.StagedSnapshotCount);
        fixture.FileSystem.Entries.Clear();

        var result = fixture.Service.CleanupOrphans(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(0, fixture.Service.StagedSnapshotCount);
    }

    [Fact]
    public void DisposeStopsFurtherCapturesWithoutDeletingStagedFiles()
    {
        using var fixture = new StagingFixture();
        var capture = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(8)));
        Assert.True(capture.Ok);
        var stagedPath = capture.StagedPath!;

        fixture.Service.Dispose();
        var after = Capture(fixture.Service, StagingFixture.Capture(StagingFixture.Payload(8)));

        Assert.False(after.Ok);
        Assert.Equal(AttachmentCoordinatorCodes.Disposed, after.Code);
        Assert.True(fixture.FileSystem.Entries.ContainsKey(stagedPath));
    }
}
