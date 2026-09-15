using DshLauncher.Core.Attachments;
using DshLauncher.Platform.Windows.Attachments;

namespace DshLauncher.Desktop.Attachments;

/// <summary>
/// D17：把 D14 的 Windows 暂存适配器接到可移植暂存端口上。
///
/// 这一层是<b>唯一</b>接触真实文件系统的位置：组合层只递不透明 id（captureId / snapshotId / batchId），
/// 因此页面与 Core 都拿不到本机路径；真实读取由
/// <see cref="WindowsAttachmentStagingAdapter.OpenStagedSource"/> 用只读、无跟随的 Win32 句柄完成。
/// </summary>
internal sealed class StagingAdapterPort(WindowsAttachmentStagingAdapter staging) : IRemoteStagingPort
{
    private readonly WindowsAttachmentStagingAdapter _staging =
        staging ?? throw new ArgumentNullException(nameof(staging));

    public AttachmentCoordinatorResult BeginBatch(string batchId, int fileCount) =>
        _staging.BeginBatch(batchId, fileCount);

    public StagingCaptureReceipt Capture(string captureId, CancellationToken cancellationToken = default) =>
        _staging.CaptureForBridge(captureId, cancellationToken);

    public IAttachmentByteSource OpenSnapshot(string snapshotId) =>
        _staging.OpenStagedSource(snapshotId);

    public StagingCleanupResult Release(string snapshotId, CancellationToken cancellationToken = default) =>
        _staging.Release(snapshotId, cancellationToken);
}
