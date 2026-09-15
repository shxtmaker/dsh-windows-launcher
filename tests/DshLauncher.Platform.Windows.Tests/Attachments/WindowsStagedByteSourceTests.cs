using System.Security.Cryptography;
using System.Text;
using DshLauncher.Core.Attachments;
using DshLauncher.Platform.Windows.Attachments;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests.Attachments;

/// <summary>
/// D17 Windows 实机用例：真实暂存快照 → 真实只读字节源（D11 的注入端口）。
/// <b>本轮没有 Windows 机器，这里一条都没有执行过</b>，因此不得记为 PASS；
/// 每条的 id、执行方式、断言与"不能在 Linux 上跑"的原因见同目录 <c>WindowsPendingCases.md</c>（WP-35…WP-38）。
/// Linux 侧本轮真实执行的是平台中立的组合契约与假暂存端口（<c>tests/DshLauncher.Core.Tests/Attachments/RemoteBridge*</c>）。
/// </summary>
[Trait("triggerTags", "VFY-06")]
public sealed class WindowsStagedByteSourceTests : IDisposable
{
    private static readonly byte[] Payload = BuildPayload(700 * 1024);

    private readonly string _layoutRoot = Path.Combine(
        Path.GetTempPath(),
        "dsh-d17-bridge-" + Guid.NewGuid().ToString("N"));

    private readonly ApplicationDataLayout _layout;
    private readonly WindowsAttachmentStagingAdapter _adapter;

    public WindowsStagedByteSourceTests()
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
    public void WP35AStagedSnapshotOpensAsThisSessionsReadOnlyByteSourceWithTheRealBytes()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-wp35", 1);
        var receipt = _adapter.CaptureForBridge(Register(source), TestContext.Current.CancellationToken);
        Assert.True(receipt.Ok);

        using var bytes = _adapter.OpenStagedSource(receipt.SnapshotId!);
        var read = new byte[bytes.ByteLength];
        var total = 0;
        while (total < read.Length)
        {
            var chunk = bytes.Read(read.AsSpan(total));
            if (chunk == 0)
            {
                break;
            }

            total += chunk;
        }

        Assert.Equal(Payload.Length, bytes.ByteLength);
        Assert.Equal(Payload.Length, total);
        Assert.Equal(Payload, read);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Payload)),
            Convert.ToHexStringLower(SHA256.HashData(read)));
    }

    [Fact]
    public void WP36TheByteSourceClosesItsHandleSoTheSnapshotCanBeReleasedAndDeleted()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-wp36", 1);
        var receipt = _adapter.CaptureForBridge(Register(source), TestContext.Current.CancellationToken);
        var bytes = _adapter.OpenStagedSource(receipt.SnapshotId!);
        Assert.False(((WindowsStagedByteSource)bytes).IsDisposed);
        bytes.Dispose();

        // 句柄未关闭时 Windows 会以共享冲突拒绝删除：释放成功即证明句柄已关。
        var released = _adapter.Release(receipt.SnapshotId!, TestContext.Current.CancellationToken);

        Assert.True(released.Ok);
        Assert.True(((WindowsStagedByteSource)bytes).IsDisposed);
        Assert.Equal(0, _adapter.StagedSnapshotCount);
    }

    [Fact]
    public void WP37TheByteSourceOnlyAcceptsATrackedSnapshotIdAndNeverAPath()
    {
        Assert.Throws<AttachmentStagingException>(() => _adapter.OpenStagedSource("snapshot-not-tracked"));

        // 公开面只有不透明 id：没有任何成员接受路径（与 WP-21/WP-32 同一套护栏）。
        var publicMethods = typeof(WindowsAttachmentStagingAdapter)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => !method.IsSpecialName)
            .ToArray();
        Assert.Contains(
            publicMethods,
            method => method.Name == nameof(WindowsAttachmentStagingAdapter.OpenStagedSource)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string));
        Assert.DoesNotContain(
            publicMethods,
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string[])
                || parameter.Name is "path" or "fullPath" or "filePath"));
    }

    [Fact]
    public void WP38AStagedFileThatGrewAfterTheSnapshotIsReportedAsASourceFault()
    {
        var source = NewSourceFile(Payload);
        _adapter.BeginBatch("batch-wp38", 1);
        var receipt = _adapter.CaptureForBridge(Register(source), TestContext.Current.CancellationToken);
        var stagedPath = Directory.EnumerateFiles(_adapter.Root.FullPath)
            .Single(path => !string.Equals(
                Path.GetFileName(path),
                AttachmentStagingRoot.OwnershipMarkerName,
                StringComparison.Ordinal));

        // 直接改写自有根内的快照，让声明长度与实际长度不一致：读取必须报源故障而不是静默截断。
        File.AppendAllText(stagedPath, "extra", Encoding.UTF8);

        using var bytes = _adapter.OpenStagedSource(receipt.SnapshotId!);
        var buffer = new byte[bytes.ByteLength];
        var total = 0;
        var faulted = false;
        try
        {
            while (total < buffer.Length)
            {
                var chunk = bytes.Read(buffer.AsSpan(total));
                if (chunk == 0)
                {
                    break;
                }

                total += chunk;
            }
        }
        catch (AttachmentStagingException error)
        {
            faulted = true;
            Assert.Equal(AttachmentStagingCodes.SourceChanged, error.Code);
        }

        Assert.True(faulted, "快照被改写后必须得到确定的源故障码");
    }

    private string Register(string path) =>
        _adapter.RegisterNativeCapture(NativePasteGesture.MintFromNativePaste(), path);

    private string NewSourceFile(byte[] payload)
    {
        var path = Path.Combine(_layoutRoot, "source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static byte[] BuildPayload(int size)
    {
        var bytes = new byte[size];
        for (var index = 0; index < size; index += 1)
        {
            bytes[index] = (byte)((index * 7) % 251);
        }

        return bytes;
    }
}
