using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DshLauncher.Core.Attachments;
using DshLauncher.Platform.Windows.Attachments;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests.Attachments;

/// <summary>
/// D14 Windows 实机用例：全部依赖真实 Win32 事实（属性位、共享模式、ACL、重解析点、
/// 真实卷上的独占创建与哈希）。<b>本轮没有 Windows 机器，因此一个都没有执行</b>；
/// 每条的 id、执行方式、断言与"不能在 Linux 上跑"的原因见同目录
/// <c>WindowsPendingCases.md</c>。Linux 侧只验证平台中立判定与交叉编译。
/// </summary>
[Trait("triggerTags", "VFY-06")]
public sealed class WindowsStagingCaptureTests : IDisposable
{
    private static readonly byte[] Payload = BuildPayload(300_000);

    private readonly string _layoutRoot = Path.Combine(
        Path.GetTempPath(),
        "dsh-d14-staging-" + Guid.NewGuid().ToString("N"));

    private readonly ApplicationDataLayout _layout;
    private readonly WindowsAttachmentStagingAdapter _adapter;

    public WindowsStagingCaptureTests()
    {
        _layout = new ApplicationDataLayout(_layoutRoot);
        Directory.CreateDirectory(_layoutRoot);
        File.WriteAllText(_layout.OwnershipMarkerPath, ApplicationDataLayout.OwnershipMarkerContent);
        _adapter = new WindowsAttachmentStagingAdapter(_layout, Guid.NewGuid());
        _adapter.Initialize();
    }

    public void Dispose()
    {
        _adapter.Dispose();
        if (Directory.Exists(_layoutRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_layoutRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_layoutRoot, recursive: true);
        }
    }

    [Fact]
    public void CaptureStagesARealFileUnderTheOwnedRootWithTheRealSha256()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-real", 1);
        var captureId = Register(source);

        var result = _adapter.CaptureNative(captureId, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(Payload.Length, result.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Payload)), result.Sha256);
        Assert.NotNull(result.StagedPath);
        Assert.StartsWith(_adapter.Root.FullPath + "\\", result.StagedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.StagedPath));
        Assert.Equal(Payload, File.ReadAllBytes(result.StagedPath));
        Assert.True(result.SourceDisposed);
        Assert.True(result.DestinationDisposed);
        Assert.True(File.Exists(Path.Combine(_adapter.Root.FullPath, AttachmentStagingRoot.OwnershipMarkerName)));

        var released = _adapter.Release(result.SnapshotId!, TestContext.Current.CancellationToken);
        Assert.True(released.Ok);
        Assert.False(File.Exists(result.StagedPath));
    }

    [Fact]
    public void CaptureClosesEveryHandleSoSourceAndSnapshotCanBothBeDeleted()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-handles", 1);

        var result = _adapter.CaptureNative(Register(source), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        // 句柄未关闭时 Windows 会以共享冲突拒绝删除：删除成功即证明两个句柄都已释放。
        File.Delete(source);
        Assert.True(_adapter.Release(result.SnapshotId!, TestContext.Current.CancellationToken).Ok);
        Assert.False(File.Exists(result.StagedPath));
    }

    [Fact]
    public void CaptureRefusesADirectoryCandidate()
    {
        var directory = Path.Combine(_layoutRoot, "a-directory");
        Directory.CreateDirectory(directory);
        _adapter.BeginBatch("batch-dir", 1);

        var result = _adapter.CaptureNative(Register(directory), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.Directory, result.Code);
        Assert.Equal(0, _adapter.StagedSnapshotCount);
    }

    [Fact]
    public void CaptureRefusesAnUncCandidateWithoutTouchingTheNetwork()
    {
        _adapter.BeginBatch("batch-unc", 1);
        var captureId = Register(@"\\localhost\C$\Windows\win.ini");

        var result = _adapter.CaptureNative(captureId, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.UncPath, result.Code);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(_adapter.Root.FullPath),
            path => !path.EndsWith(AttachmentStagingRoot.OwnershipMarkerName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\??\C:\Windows\win.ini")]
    [InlineData("C:win.ini")]
    [InlineData(@"\Windows\win.ini")]
    public void CaptureRefusesDeviceDriveRelativeAndRootedCandidatePaths(string path)
    {
        // 期望码按输入字面量写死（不从被测判定推导），签名保持稳定以免重建固定用例清单。
        var expectedCode = path switch
        {
            @"\\.\NUL" => AttachmentStagingCodes.DevicePath,
            @"\\?\C:\Windows\win.ini" => AttachmentStagingCodes.DevicePath,
            @"\??\C:\Windows\win.ini" => AttachmentStagingCodes.DevicePath,
            _ => AttachmentStagingCodes.PathNotAbsolute,
        };

        _adapter.BeginBatch("batch-path-form", 1);
        var captureId = Register(path);

        var result = _adapter.CaptureNative(captureId, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(0, _adapter.StagedSnapshotCount);
    }

    [Fact]
    public void CaptureRefusesAFileSymbolicLinkWithoutReadingItsTarget()
    {
        // 前置：需要开发者模式或提升权限才能创建文件符号链接；否则本用例以环境错误失败。
        var target = NewSourceFile(Payload);
        var link = Path.Combine(_layoutRoot, "link-" + Guid.NewGuid().ToString("N") + ".bin");
        File.CreateSymbolicLink(link, target);
        _adapter.BeginBatch("batch-link", 1);

        var result = _adapter.CaptureNative(Register(link), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.ReparsePoint, result.Code);
        Assert.Equal(0, _adapter.StagedSnapshotCount);
    }

    [Fact]
    public void CaptureRefusesADirectoryJunctionAsADirectoryCandidate()
    {
        // 目录联接不需要管理员权限：属性里同时带 DIRECTORY 与 REPARSE_POINT，按目录拒绝。
        var target = Path.Combine(_layoutRoot, "junction-target");
        var link = Path.Combine(_layoutRoot, "junction-link");
        Directory.CreateDirectory(target);
        CreateJunction(link, target);

        _adapter.BeginBatch("batch-junction", 1);
        var result = _adapter.CaptureNative(Register(link), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.Directory, result.Code);
    }

    [Fact]
    public void CaptureRefusesASourceLockedExclusivelyByAnotherProcess()
    {
        var source = NewSourceFile(Payload);
        using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _adapter.BeginBatch("batch-locked", 1);

        var result = _adapter.CaptureNative(Register(source), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceLocked, result.Code);
    }

    [Fact]
    public void CaptureRefusesAnOfflineFile()
    {
        var source = NewSourceFile(Payload);
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.Offline);
        _adapter.BeginBatch("batch-offline", 1);

        var result = _adapter.CaptureNative(Register(source), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.OfflineFile, result.Code);
    }

    [Fact]
    public void CaptureRefusesAnAclDeniedSource()
    {
        var source = NewSourceFile(Payload);
        var user = Environment.UserName;
        RunIcacls($"\"{source}\" /deny \"{user}:(R)\"");
        try
        {
            _adapter.BeginBatch("batch-acl", 1);
            var result = _adapter.CaptureNative(Register(source), TestContext.Current.CancellationToken);

            Assert.False(result.Ok);
            Assert.Equal(AttachmentStagingCodes.SourceUnavailable, result.Code);
        }
        finally
        {
            RunIcacls($"\"{source}\" /remove:d \"{user}\"");
        }
    }

    [Fact]
    public void CaptureFailsWhenTheSourceWasDeletedAfterTheNativeGesture()
    {
        var source = NewSourceFile(Payload);
        var captureId = Register(source);
        File.Delete(source);
        _adapter.BeginBatch("batch-deleted", 1);

        var result = _adapter.CaptureNative(captureId, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.SourceUnavailable, result.Code);
    }

    [Fact]
    public void SourceHandleBlocksConcurrentWritersWhileTheSnapshotIsOpen()
    {
        var source = NewSourceFile(Payload);
        using var handle = new WindowsStagingSourceHandle(source);

        // 源句柄只共享 FILE_SHARE_READ：任何新的写句柄都必须以共享冲突失败。
        var refused = Assert.Throws<IOException>(
            () => new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.Equal(32, refused.HResult & 0xFFFF);

        Assert.True(handle.InitialIdentity.Exists);
    }

    [Fact]
    public void ReplacingTheFileAtTheSamePathIsDetectedByFileIdentity()
    {
        var source = NewSourceFile(Payload);
        var timestamp = File.GetLastWriteTimeUtc(source);
        Assert.True(WindowsStagingNative.TryReadPathIdentity(source, out var before));

        // 同名换文件：长度与最后写入时间完全相同，只有文件标识（卷序列号 + 文件索引）会变。
        File.Delete(source);
        File.WriteAllBytes(source, Payload);
        File.SetLastWriteTimeUtc(source, timestamp);
        Assert.True(WindowsStagingNative.TryReadPathIdentity(source, out var after));

        Assert.NotEqual(before.FileId, after.FileId);
        Assert.False(StagingSourceChangePolicy.Compare(before, after).Unchanged);
    }

    [Fact]
    public void ExclusiveCreateRefusesToOverwriteAnExistingFile()
    {
        var existing = Path.Combine(_adapter.Root.FullPath, "already-there.bin");
        File.WriteAllBytes(existing, "keep me"u8.ToArray());
        var fileSystem = new WindowsStagingFileSystem();

        var refused = Assert.Throws<AttachmentStagingException>(
            () => fileSystem.CreateExclusiveOwnedFile(existing));

        Assert.Equal(AttachmentStagingCodes.DestinationCreateFailed, refused.Code);
        Assert.Equal("keep me"u8.ToArray(), File.ReadAllBytes(existing));
    }

    [Fact]
    public void CleanupRemovesOwnedRegularFilesAndRefusesEverythingElse()
    {
        var orphan = Path.Combine(_adapter.Root.FullPath, "orphan.bin");
        File.WriteAllBytes(orphan, "orphan"u8.ToArray());
        var outsideDirectory = Path.Combine(_layoutRoot, "outside-directory");
        Directory.CreateDirectory(outsideDirectory);
        var outside = Path.Combine(outsideDirectory, "must-survive.txt");
        File.WriteAllText(outside, "must survive");
        var junction = Path.Combine(_adapter.Root.FullPath, "escape-junction");
        CreateJunction(junction, outsideDirectory);
        var directory = Path.Combine(_adapter.Root.FullPath, "subdir");
        Directory.CreateDirectory(directory);

        var result = _adapter.CleanupOrphans(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(1, result.RemovedCount);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(outside));
        Assert.Equal("must survive", File.ReadAllText(outside));
        Assert.True(Directory.Exists(directory));
        Assert.True(Directory.Exists(junction));
        Assert.Contains(result.Outcomes, outcome => outcome.Code == AttachmentStagingCodes.CleanupReparsePoint);
        Assert.Contains(result.Outcomes, outcome => outcome.Code == AttachmentStagingCodes.CleanupNotRegularFile);
    }

    [Fact]
    public void InitializeRefusesAStagingRootThatIsAReparsePoint()
    {
        var target = Path.Combine(_layoutRoot, "real-staging");
        Directory.CreateDirectory(target);
        var targetId = Guid.Parse("11111111111111111111111111111111");
        var stagingRoot = WindowsAttachmentStagingAdapter.GetOwnedStagingRoot(_layout, targetId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingRoot)!);
        CreateJunction(stagingRoot, target);

        using var adapter = new WindowsAttachmentStagingAdapter(_layout, targetId);

        Assert.Throws<AttachmentStagingException>(adapter.Initialize);
    }

    [Fact]
    public void InitializeRefusesATamperedOwnershipMarker()
    {
        File.WriteAllText(
            Path.Combine(_adapter.Root.FullPath, AttachmentStagingRoot.OwnershipMarkerName),
            "not ours");

        Assert.Throws<AttachmentStagingException>(_adapter.Initialize);
    }

    [Fact]
    public void CaptureForBridgeReturnsAReceiptWithoutAnyLocalPath()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-bridge", 1);
        var captureId = Register(source);

        var receipt = _adapter.CaptureForBridge(captureId, TestContext.Current.CancellationToken);

        Assert.True(receipt.Ok);
        Assert.Equal(Payload.Length, receipt.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Payload)), receipt.Sha256);
        StagingBoundaryAssertions.NoPropertyNameContainsPath(typeof(StagingCaptureReceipt));
        StagingBoundaryAssertions.NoPropertyNameContainsPath(receipt.GetType());
    }

    [Fact]
    public void AConsumedCaptureIdCannotBeReplayed()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-replay", 1);
        var captureId = Register(source);

        Assert.True(_adapter.CaptureForBridge(captureId, TestContext.Current.CancellationToken).Ok);
        var replay = _adapter.CaptureForBridge(captureId, TestContext.Current.CancellationToken);

        Assert.False(replay.Ok);
        Assert.Equal(AttachmentStagingCodes.CaptureConsumed, replay.Code);
        Assert.Equal(1, _adapter.StagedSnapshotCount);
    }

    [Fact]
    public void AnUnregisteredCaptureIdIsRefused()
    {
        _adapter.BeginBatch("batch-unknown", 1);

        var result = _adapter.CaptureForBridge("not-a-real-capture", TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(AttachmentStagingCodes.CaptureIdUnknown, result.Code);
    }

    private static byte[] BuildPayload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index += 1)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private string NewSourceFile(byte[] content)
    {
        var path = Path.Combine(_layoutRoot, "source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private string Register(string path) =>
        _adapter.RegisterNativeCapture(NativePasteGesture.MintFromNativePaste(), path);

    /// <summary>目录联接（junction）不需要管理员权限，可用来构造重解析点场景。</summary>
    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void RunIcacls(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
