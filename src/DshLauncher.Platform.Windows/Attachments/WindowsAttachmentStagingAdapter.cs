using DshLauncher.Core.Attachments;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// D14 的 Windows 暂存适配器（桥可见表面）。
///
/// <b>无任意路径读取入口</b>（结构性保证，实机边界用例 WP-13）：
/// <list type="bullet">
/// <item>唯一能产生候选的方法是 <c>RegisterNativeCapture</c>，它是 <c>internal</c>，
/// 且第一个参数是只有本程序集能铸造的 <see cref="NativePasteGesture"/>；</item>
/// <item>公开表面只接受不透明 id：<see cref="CaptureForBridge"/> 收 <c>captureId</c>、
/// <see cref="Release"/> 收 <c>snapshotId</c>、<see cref="BeginBatch"/> 收 <c>batchId</c>，
/// 没有任何 public 成员接受路径；</item>
/// <item>回执 <see cref="StagingCaptureReceipt"/> 只有 id、显示名、字节数与 SHA-256，<b>不含本机路径</b>；
/// 暂存路径只经 <c>internal</c> 的 <c>CaptureNative</c> 交给原生传输半区。</item>
/// </list>
///
/// 暂存内容落在应用自有的 <c>&lt;应用数据根&gt;\attachments\staging\&lt;targetId&gt;</c> 下，
/// 写入前先建目录、无跟随复核、再确认所有权标记；清理只动这个根内的直接子项。
/// </summary>
public sealed class WindowsAttachmentStagingAdapter : IDisposable
{
    /// <summary>未消费票据的硬上界（不无界堆积）。</summary>
    public const int MaxPendingCaptures = 32;

    private const int MaxRememberedConsumedIds = 64;

    private readonly AttachmentStagingService _staging;
    private readonly Dictionary<string, INativeStagingCapture> _captures = new(StringComparer.Ordinal);
    private readonly Queue<string> _consumedOrder = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private bool _disposed;

    public WindowsAttachmentStagingAdapter(
        ApplicationDataLayout layout,
        Guid targetId,
        AttachmentLimits? limits = null,
        IStagingFileSystem? fileSystem = null,
        long freeSpaceReserveBytes = AttachmentStagingService.DefaultFreeSpaceReserveBytes)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Root = new AttachmentStagingRoot(GetOwnedStagingRoot(layout, targetId));
        if (!StagingPathPolicy.IsWithinOwnedRoot(layout.RootPath, Root.FullPath))
        {
            throw new ArgumentException("暂存根必须位于应用数据根内。", nameof(layout));
        }

        _staging = new AttachmentStagingService(
            Root,
            fileSystem ?? new WindowsStagingFileSystem(),
            limits,
            freeSpaceReserveBytes);
    }

    /// <summary>自有暂存根（本应用自己的路径；不接受调用方给的路径）。</summary>
    internal AttachmentStagingRoot Root { get; }

    /// <summary>待消费的原生捕获数（≤ <see cref="MaxPendingCaptures"/>）。</summary>
    public int PendingCaptureCount => _captures.Count;

    /// <summary>台账里的快照数。</summary>
    public int StagedSnapshotCount => _staging.StagedSnapshotCount;

    /// <summary>当前暂存批次 id。</summary>
    public string? BatchId => _staging.BatchId;

    /// <summary>计算某个目标的暂存根路径（纯计算，不触碰文件系统）。</summary>
    public static string GetOwnedStagingRoot(ApplicationDataLayout layout, Guid targetId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("目标标识不得为空。", nameof(targetId));
        }

        return Path.Combine(layout.RootPath, "attachments", "staging", targetId.ToString("N"));
    }

    /// <summary>
    /// 建立并复核自有暂存根：创建目录、无跟随检查（根自己不得是重解析点、
    /// 解析后的最终路径必须与请求路径一致）、再写入/核对所有权标记。
    /// </summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowsStagingNative.EnsureOwnedDirectory(Root.FullPath);
        WindowsStagingNative.EnsureOwnershipMarker(Root.FullPath, AttachmentStagingRoot.OwnershipMarkerContent);
    }

    /// <summary>
    /// 由原生粘贴手势登记一个候选并返回一次性票据 id。
    /// <b>internal</b> + 手势令牌 = 外部程序集无法用任意路径调用。
    /// </summary>
    internal string RegisterNativeCapture(NativePasteGesture gesture, string droppedPath)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_captures.Count >= MaxPendingCaptures)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CaptureRegistryFull,
                $"未消费捕获已达上界 {MaxPendingCaptures}：先消费或丢弃旧票据。");
        }

        // 只做"非空"这一项便宜校验：候选接受判定（UNC/设备/目录/占位/链接）在打开句柄时
        // 由 StagingCandidatePolicy 统一给出确定拒绝码，避免两处各判一套。
        var captureId = Guid.NewGuid().ToString("N");
        _captures[captureId] = new WindowsNativeDropCapture(captureId, droppedPath);
        return captureId;
    }

    /// <summary>
    /// 登记一个<b>没有路径</b>的原生捕获（D16 截图 PNG：字节由宿主自己编码）。
    /// 与文件候选一样是 <b>internal</b> + 需要同程序集铸造的手势令牌，
    /// 因此外部程序集既不能凭路径登记，也不能凭字节登记。
    /// </summary>
    internal string RegisterNativeCapture(NativePasteGesture gesture, INativeStagingCapture capture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(capture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_captures.Count >= MaxPendingCaptures)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CaptureRegistryFull,
                $"未消费捕获已达上界 {MaxPendingCaptures}：先消费或丢弃旧票据。");
        }

        if (_captures.ContainsKey(capture.CaptureId) || _consumed.Contains(capture.CaptureId))
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.CaptureConsumed,
                "该捕获 id 已在台账或已消费：一次性票据不得复用。");
        }

        _captures[capture.CaptureId] = capture;
        return capture.CaptureId;
    }

    /// <summary>原生传输半区用：消费票据并拿到含暂存路径的完整结果。</summary>
    internal StagingCaptureResult CaptureNative(string captureId, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return new StagingCaptureResult { Ok = false, Code = AttachmentCoordinatorCodes.Disposed, Detail = "适配器已释放" };
        }

        var (capture, code, detail) = ConsumeCapture(captureId);
        if (capture is null)
        {
            return new StagingCaptureResult { Ok = false, Code = code, Detail = detail };
        }

        return _staging.Capture(capture, cancellationToken);
    }

    /// <summary>桥可见入口：只收不透明 captureId，只回不含路径的回执。</summary>
    public StagingCaptureReceipt CaptureForBridge(string captureId, CancellationToken cancellationToken = default)
    {
        var result = CaptureNative(captureId, cancellationToken);
        return new StagingCaptureReceipt
        {
            Ok = result.Ok,
            Code = result.Code,
            Detail = result.Detail,
            SnapshotId = result.SnapshotId,
            DisplayName = result.DisplayName,
            ByteLength = result.ByteLength,
            Sha256 = result.Sha256,
        };
    }

    /// <summary>开始暂存批次（与线上批次一一对应）。</summary>
    public AttachmentCoordinatorResult BeginBatch(string batchId, int fileCount) =>
        _staging.BeginBatch(batchId, fileCount);

    /// <summary>
    /// D17：按<b>不透明快照 id</b>打开受控字节源，交给 Core 的 D11 协调器分块发送。
    ///
    /// 边界与"无任意路径读取入口"一致：
    /// <list type="bullet">
    /// <item>只接受本台账里的 snapshotId，调用方（组合层/页面）拿不到也构造不出路径；</item>
    /// <item>打开前复核快照路径确实落在自有暂存根内（词法包含关系 + 平台无跟随打开）；</item>
    /// <item>真实读取用只读句柄（<c>FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN</c>）。</item>
    /// </list>
    /// </summary>
    public IAttachmentByteSource OpenStagedSource(string snapshotId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);
        var snapshot = _staging.Snapshots.FirstOrDefault(
            candidate => string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal));
        if (snapshot is null)
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.SnapshotUnknown,
                "快照 id 不在本适配器台账里");
        }

        if (!StagingPathPolicy.IsWithinOwnedRoot(Root.FullPath, snapshot.StagedPath))
        {
            throw new AttachmentStagingException(
                AttachmentStagingCodes.DestinationOutsideOwnedRoot,
                "快照路径不在自有暂存根内：拒绝打开");
        }

        return new WindowsStagedByteSource(snapshot.StagedPath, snapshot.ByteLength);
    }

    /// <summary>释放一个快照：只接受台账 id，不接受路径。</summary>
    public StagingCleanupResult Release(string snapshotId, CancellationToken cancellationToken = default) =>
        _staging.Release(snapshotId, cancellationToken);

    /// <summary>启动清理自有根内的残留条目（拒绝一切根外/重解析点/目录目标）。</summary>
    public StagingCleanupResult CleanupOrphans(CancellationToken cancellationToken = default) =>
        _staging.CleanupOrphans(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _captures.Clear();
        _consumed.Clear();
        _consumedOrder.Clear();
        _staging.Dispose();
    }

    private (INativeStagingCapture? Capture, string? Code, string? Detail) ConsumeCapture(string captureId)
    {
        if (string.IsNullOrEmpty(captureId))
        {
            return (null, AttachmentStagingCodes.CaptureIdUnknown, "捕获 id 为空");
        }

        if (_captures.Remove(captureId, out var capture))
        {
            RememberConsumed(captureId);
            return (capture, null, null);
        }

        if (_consumed.Contains(captureId))
        {
            return (null, AttachmentStagingCodes.CaptureConsumed, "该原生捕获已被消费（一次性票据）");
        }

        return (null, AttachmentStagingCodes.CaptureIdUnknown, "捕获 id 未在本适配器登记");
    }

    private void RememberConsumed(string captureId)
    {
        _consumed.Add(captureId);
        _consumedOrder.Enqueue(captureId);
        while (_consumedOrder.Count > MaxRememberedConsumedIds)
        {
            _consumed.Remove(_consumedOrder.Dequeue());
        }
    }
}
