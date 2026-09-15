using System.Security.Cryptography;

namespace DshLauncher.Core.Attachments;

/// <summary>一次成功暂存的快照台账（只在本服务内流通；桥只能拿 id 与摘要）。</summary>
public sealed record StagingSnapshot(
    string SnapshotId,
    string DisplayName,
    string StagedPath,
    long ByteLength,
    string Sha256);

/// <summary>快照结果。失败时 <see cref="Code"/> 是本地判定码；成功时携带真实 SHA-256 与暂存路径。</summary>
public sealed record StagingCaptureResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? SnapshotId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>自有暂存根内的文件路径；供原生半区打开字节源，<b>不得</b>跨桥下发。</summary>
    public string? StagedPath { get; init; }

    public long ByteLength { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>源句柄是否已释放（每条退出路径都必须为 true）。</summary>
    public bool SourceDisposed { get; init; }

    /// <summary>暂存目标句柄是否已释放（每条退出路径都必须为 true）。</summary>
    public bool DestinationDisposed { get; init; }

    /// <summary>失败/取消时半成品是否已从自有根删除。</summary>
    public bool StagedFileDeleted { get; init; }
}

/// <summary>桥可见的暂存回执：只有 id、显示名、字节数与 SHA-256，<b>没有本机路径</b>。</summary>
public sealed record StagingCaptureReceipt
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? SnapshotId { get; init; }

    public string? DisplayName { get; init; }

    public long ByteLength { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>单个清理目标的结局（只记录叶名，不记录完整本机路径）。</summary>
public sealed record StagingCleanupOutcome(string EntryName, bool Deleted, string? Code, string? Detail);

/// <summary>清理结果：<see cref="Ok"/> 为 false 表示至少有一个目标被拒绝删除。</summary>
public sealed record StagingCleanupResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? SnapshotId { get; init; }

    public int RemovedCount { get; init; }

    public int RefusedCount { get; init; }

    public IReadOnlyList<StagingCleanupOutcome> Outcomes { get; init; } = [];
}

/// <summary>
/// D14 受控暂存快照服务（生产实现，平台中立）。
///
/// 职责与边界：
/// <list type="bullet">
/// <item><b>候选只来自票据</b>：唯一入口 <see cref="Capture"/> 接受 <see cref="INativeStagingCapture"/>，
/// 因此没有任何方法能凭调用方给的路径字符串读取本机文件；</item>
/// <item><b>写入前判定</b>：候选接受（目录/UNC/设备/重解析点/云占位/脱机/远程卷）与
/// 单文件/批文件数/批字节/单目标暂存/可用空间五类限额都在创建文件之前完成；</item>
/// <item><b>顺序流式复制</b>：固定 256 KiB 缓冲（复用 D10 冻结块大小），边读边写边算 SHA-256，
/// 绝不整文件进内存；</item>
/// <item><b>变化检测</b>：打开时与复制后各取一次身份（长度/最后写入时间/文件标识/卷），
/// 提前 EOF、意外增长、同名换文件都判定 <c>source-changed</c> 并删除半成品；</item>
/// <item><b>句柄纪律</b>：源与目标句柄在 finally 中无条件释放，成功/失败/取消无一例外；</item>
/// <item><b>清理只动自有根</b>：删除只接受台账里的快照 id 或自有根下的一层条目，
/// 词法包含关系与平台解析结果都必须落在自有根内，重解析点与目录一律拒绝。</item>
/// </list>
/// 全部依赖注入，无 sleep、无后台线程、无 Windows API。
/// </summary>
public sealed class AttachmentStagingService : IDisposable
{
    /// <summary>默认磁盘保留量（64 MiB）：为系统与其他进程留出余量。</summary>
    public const long DefaultFreeSpaceReserveBytes = 64L * 1024 * 1024;

    /// <summary>台账条目硬上界（与字节上限一起构成可观测的资源边界）。</summary>
    public const int MaxStagedSnapshots = 64;

    private readonly IStagingFileSystem _fileSystem;
    private readonly IAttachmentClock _clock;
    private readonly AttachmentTransferTrace _trace;
    private readonly Dictionary<string, StagingSnapshot> _snapshots = new(StringComparer.Ordinal);
    private string? _batchId;
    private int _batchFileLimit;
    private int _batchFiles;
    private long _batchBytes;
    private bool _disposed;

    public AttachmentStagingService(
        AttachmentStagingRoot root,
        IStagingFileSystem fileSystem,
        AttachmentLimits? limits = null,
        long freeSpaceReserveBytes = DefaultFreeSpaceReserveBytes,
        IAttachmentClock? clock = null,
        AttachmentTransferTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (freeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(freeSpaceReserveBytes),
                freeSpaceReserveBytes,
                "磁盘保留量不得为负");
        }

        Root = root;
        _fileSystem = fileSystem;
        Limits = limits ?? new AttachmentLimits();
        FreeSpaceReserveBytes = freeSpaceReserveBytes;
        _clock = clock ?? SystemAttachmentClock.Instance;
        _trace = trace ?? new AttachmentTransferTrace();
    }

    /// <summary>自有暂存根。</summary>
    public AttachmentStagingRoot Root { get; }

    /// <summary>生效限额（与传输层同一套冻结上限）。</summary>
    public AttachmentLimits Limits { get; }

    /// <summary>写入前必须保留的可用空间。</summary>
    public long FreeSpaceReserveBytes { get; }

    /// <summary>当前开放批次 id；未开始为 null。</summary>
    public string? BatchId => _batchId;

    /// <summary>当前批声明的文件数上限（≤ 冻结的单批上限）。</summary>
    public int BatchFileLimit => _batchFileLimit;

    /// <summary>当前批已暂存文件数。</summary>
    public int BatchFileCount => _batchFiles;

    /// <summary>当前批已暂存字节数。</summary>
    public long BatchBytes => _batchBytes;

    /// <summary>台账里的快照数（≤ <see cref="MaxStagedSnapshots"/>）。</summary>
    public int StagedSnapshotCount => _snapshots.Count;

    /// <summary>台账里的暂存总字节数（≤ 单目标暂存上限）。</summary>
    public long StagedBytes
    {
        get
        {
            var total = 0L;
            foreach (var snapshot in _snapshots.Values)
            {
                total += snapshot.ByteLength;
            }

            return total;
        }
    }

    /// <summary>台账快照的只读视图（原生半区据此打开字节源）。</summary>
    public IReadOnlyList<StagingSnapshot> Snapshots => [.. _snapshots.Values];

    /// <summary>trace 记录器（与 D11 同一套，记录判定码而不记录完整本机路径）。</summary>
    public AttachmentTransferTrace Trace => _trace;

    /// <summary>
    /// 开始一个暂存批次。文件数必须在 1..MaxFilesPerBatch 之间；
    /// 重复调用会重置批计数（调用方负责与线上批次一一对应）。
    /// </summary>
    public AttachmentCoordinatorResult BeginBatch(string batchId, int fileCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        if (_disposed)
        {
            return Fail(AttachmentCoordinatorCodes.Disposed, "暂存服务已释放");
        }

        if (fileCount < 1 || fileCount > Limits.MaxFilesPerBatch)
        {
            return Fail("limit-batch-files", $"fileCount {fileCount} 超出 1..{Limits.MaxFilesPerBatch}");
        }

        _batchId = batchId;
        _batchFileLimit = fileCount;
        _batchFiles = 0;
        _batchBytes = 0;
        TraceDecision("staging", null, "批开始", $"fileCount={fileCount}");
        return new AttachmentCoordinatorResult { Ok = true, BatchId = batchId };
    }

    /// <summary>
    /// 从一次性原生捕获票据做受控快照。返回真实 SHA-256 与自有根内的暂存路径；
    /// 任何失败都会删除半成品并释放两个句柄。
    /// </summary>
    public StagingCaptureResult Capture(
        INativeStagingCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (_disposed)
        {
            return FailCapture(AttachmentCoordinatorCodes.Disposed, "暂存服务已释放");
        }

        if (_batchId is null)
        {
            return FailCapture(AttachmentStagingCodes.BatchNotOpen, "尚未 BeginBatch");
        }

        if (_snapshots.Count >= MaxStagedSnapshots)
        {
            return FailCapture(
                "limit-staging-bytes",
                $"暂存台账已有 {_snapshots.Count} 条，超过本地上界 {MaxStagedSnapshots}");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return FailCapture(AttachmentStagingCodes.Cancelled, "快照在开始前已取消");
        }

        IStagingSourceHandle? source = null;
        IStagingDestinationFile? destination = null;
        var deleted = false;
        StagingCaptureResult result;
        var sourceDisposed = false;
        var destinationDisposed = false;
        try
        {
            result = RunCapture(capture, cancellationToken, ref source, ref destination, ref deleted);
        }
        finally
        {
            if (destination is not null)
            {
                DisposeQuietly(destination);
                destinationDisposed = true;
            }

            if (source is not null)
            {
                DisposeQuietly(source);
                sourceDisposed = true;
            }
        }

        return result with
        {
            SourceDisposed = sourceDisposed,
            DestinationDisposed = destinationDisposed,
            StagedFileDeleted = deleted,
        };
    }

    /// <summary>释放一个快照（只接受台账 id，不接受调用方给的路径）。</summary>
    public StagingCleanupResult Release(string snapshotId, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return FailCleanup(AttachmentCoordinatorCodes.Disposed, "暂存服务已释放", snapshotId);
        }

        if (string.IsNullOrEmpty(snapshotId) || !_snapshots.TryGetValue(snapshotId, out var snapshot))
        {
            return FailCleanup(AttachmentStagingCodes.SnapshotUnknown, "快照 id 不在本服务台账里", snapshotId);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return FailCleanup(AttachmentStagingCodes.Cancelled, "清理已取消", snapshotId);
        }

        StagingOwnedEntryInfo info;
        try
        {
            info = _fileSystem.InspectOwnedEntry(Root.FullPath, snapshot.StagedPath);
        }
        catch (IOException error)
        {
            return FailCleanup(AttachmentStagingCodes.CleanupFailed, error.Message, snapshotId);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailCleanup(AttachmentStagingCodes.CleanupFailed, error.Message, snapshotId);
        }

        var decision = StagingCleanupPolicy.Evaluate(Root.FullPath, snapshot.StagedPath, info);
        if (!decision.Allowed)
        {
            return FailCleanup(decision.Code!, decision.Detail!, snapshotId);
        }

        if (!info.Exists)
        {
            _snapshots.Remove(snapshotId);
            TraceDecision("staging", snapshotId, "清理", "文件已不存在，仅移除台账条目");
            return OkCleanup(snapshotId, removed: 0);
        }

        try
        {
            _fileSystem.DeleteOwnedEntry(Root.FullPath, snapshot.StagedPath);
        }
        catch (IOException error)
        {
            return FailCleanup(AttachmentStagingCodes.CleanupFailed, error.Message, snapshotId);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailCleanup(AttachmentStagingCodes.CleanupFailed, error.Message, snapshotId);
        }
        catch (AttachmentStagingException error)
        {
            return FailCleanup(error.Code, error.Message, snapshotId);
        }

        _snapshots.Remove(snapshotId);
        TraceDecision("staging", snapshotId, "清理", "已删除自有暂存文件并移除台账条目");
        return OkCleanup(snapshotId, removed: 1);
    }

    /// <summary>
    /// 启动清理：只扫自有根<b>一层</b>条目，逐条做包含关系与重解析点复核后删除。
    /// 任何根外/重解析点/目录目标都会被拒绝，且不会被删除。
    /// </summary>
    public StagingCleanupResult CleanupOrphans(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return FailCleanup(AttachmentCoordinatorCodes.Disposed, "暂存服务已释放", snapshotId: null);
        }

        IReadOnlyList<string> entries;
        try
        {
            entries = _fileSystem.ListOwnedEntries(Root.FullPath);
        }
        catch (IOException error)
        {
            return FailCleanup(AttachmentStagingCodes.CleanupFailed, error.Message, snapshotId: null);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailCleanup(AttachmentStagingCodes.CleanupFailed, error.Message, snapshotId: null);
        }

        var outcomes = new List<StagingCleanupOutcome>();
        var removed = 0;
        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var name = LeafOf(entry);
            StagingOwnedEntryInfo info;
            try
            {
                info = _fileSystem.InspectOwnedEntry(Root.FullPath, entry);
            }
            catch (IOException error)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, AttachmentStagingCodes.CleanupFailed, error.Message));
                continue;
            }
            catch (UnauthorizedAccessException error)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, AttachmentStagingCodes.CleanupFailed, error.Message));
                continue;
            }

            var decision = StagingCleanupPolicy.Evaluate(Root.FullPath, entry, info);
            if (!decision.Allowed)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, decision.Code, decision.Detail));
                continue;
            }

            if (!info.Exists)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, null, null));
                continue;
            }

            try
            {
                _fileSystem.DeleteOwnedEntry(Root.FullPath, entry);
                removed += 1;
                outcomes.Add(new StagingCleanupOutcome(name, true, null, null));
            }
            catch (IOException error)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, AttachmentStagingCodes.CleanupFailed, error.Message));
            }
            catch (UnauthorizedAccessException error)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, AttachmentStagingCodes.CleanupFailed, error.Message));
            }
            catch (AttachmentStagingException error)
            {
                outcomes.Add(new StagingCleanupOutcome(name, false, error.Code, error.Message));
            }
        }

        DropMissingLedgerEntries();
        var refused = outcomes.Count(outcome => !outcome.Deleted && outcome.Code is not null);
        var firstRefusal = outcomes.FirstOrDefault(outcome => !outcome.Deleted && outcome.Code is not null);
        TraceDecision("staging", null, "启动清理", $"removed={removed} refused={refused}");
        return new StagingCleanupResult
        {
            Ok = refused == 0,
            Code = firstRefusal?.Code,
            Detail = firstRefusal?.Detail,
            RemovedCount = removed,
            RefusedCount = refused,
            Outcomes = outcomes,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _snapshots.Clear();
        _batchId = null;
        _batchFileLimit = 0;
        _batchBytes = 0;
        _batchFiles = 0;
    }

    // ————————————————————————————————————————————————————————————
    // 快照流水线
    // ————————————————————————————————————————————————————————————

    private StagingCaptureResult RunCapture(
        INativeStagingCapture capture,
        CancellationToken cancellationToken,
        ref IStagingSourceHandle? source,
        ref IStagingDestinationFile? destination,
        ref bool deleted)
    {
        try
        {
            source = capture.OpenSource();
        }
        catch (AttachmentStagingException error)
        {
            return FailCapture(error.Code, error.Message);
        }
        catch (IOException error)
        {
            return FailCapture(AttachmentStagingCodes.SourceUnavailable, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailCapture(AttachmentStagingCodes.SourceUnavailable, error.Message);
        }
        catch (NotSupportedException error)
        {
            return FailCapture(AttachmentStagingCodes.SourceUnavailable, error.Message);
        }

        var descriptor = source.Descriptor;
        var acceptance = StagingCandidatePolicy.Evaluate(descriptor);
        if (!acceptance.Allowed)
        {
            TraceDecision("staging", capture.CaptureId, "拒绝候选", $"{acceptance.Code}：{acceptance.Detail}");
            return FailCapture(acceptance.Code!, acceptance.Detail!);
        }

        if (source.InitialIdentity.Length != descriptor.Length)
        {
            return FailCapture(
                AttachmentStagingCodes.SourceChanged,
                $"声明长度 {descriptor.Length} 与句柄身份 {source.InitialIdentity.Length} 不一致");
        }

        var displayName = StagingPathPolicy.NormalizeDisplayName(descriptor.LeafName);
        var snapshotId = Guid.NewGuid().ToString("N");
        var stagingLeaf = StagingPathPolicy.CreateStagingLeafName(displayName, snapshotId);
        var resolved = Root.ResolveOwnedLeaf(stagingLeaf);
        if (!resolved.Allowed)
        {
            return FailCapture(resolved.Code!, resolved.Detail!);
        }

        var fullPath = resolved.FullPath!;

        // —— 写入之前的限额与磁盘空间判定 ——
        long freeBytes;
        try
        {
            freeBytes = _fileSystem.GetAvailableFreeBytes(Root.FullPath);
        }
        catch (IOException error)
        {
            return FailCapture(AttachmentStagingCodes.DestinationCreateFailed, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailCapture(AttachmentStagingCodes.DestinationCreateFailed, error.Message);
        }

        // 批内文件数取"冻结上限"与"本批声明值"的较小者：只声明 2 个文件时不得暂存第 3 个。
        var effectiveLimits = Limits with
        {
            MaxFilesPerBatch = Math.Min(Limits.MaxFilesPerBatch, _batchFileLimit),
        };
        var quota = StagingQuotaPolicy.Evaluate(
            effectiveLimits,
            new StagingQuotaState(StagedBytes, _batchFiles, _batchBytes),
            descriptor.Length,
            freeBytes,
            FreeSpaceReserveBytes);
        if (!quota.Allowed)
        {
            TraceDecision("staging", capture.CaptureId, "拒绝快照", $"{quota.Code}：{quota.Detail}");
            return FailCapture(quota.Code!, quota.Detail!);
        }

        try
        {
            destination = _fileSystem.CreateExclusiveOwnedFile(fullPath);
        }
        catch (AttachmentStagingException error)
        {
            return FailCapture(error.Code, error.Message);
        }
        catch (IOException error)
        {
            return FailCapture(AttachmentStagingCodes.DestinationCreateFailed, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailCapture(AttachmentStagingCodes.DestinationCreateFailed, error.Message);
        }

        if (!StagingPathPolicy.IsWithinOwnedRoot(Root.FullPath, destination.FinalPath))
        {
            DeletePartial(ref destination, fullPath, ref deleted);
            return FailCapture(
                AttachmentStagingCodes.DestinationOutsideOwnedRoot,
                "独占创建后的最终路径落在自有暂存根之外（父目录可能是重解析点）");
        }

        var buffer = new byte[AttachmentProtocol.ChunkBytes];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var total = 0L;
        while (total < descriptor.Length)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CancelCapture(ref destination, fullPath, ref deleted);
            }

            var want = (int)Math.Min(buffer.Length, descriptor.Length - total);
            int read;
            try
            {
                read = source.Read(buffer.AsSpan(0, want));
            }
            catch (AttachmentStagingException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, error.Code, error.Message);
            }
            catch (IOException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
            }
            catch (UnauthorizedAccessException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
            }
            catch (ObjectDisposedException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
            }

            if (read < 0 || read > want)
            {
                return FailAfterCreate(
                    ref destination,
                    fullPath,
                    ref deleted,
                    AttachmentStagingCodes.SourceReadFailed,
                    $"字节源返回非法长度 {read}（本次请求 {want}）");
            }

            if (read == 0)
            {
                return FailAfterCreate(
                    ref destination,
                    fullPath,
                    ref deleted,
                    AttachmentStagingCodes.SourceChanged,
                    $"源在 {total}/{descriptor.Length} 字节处 EOF：快照将不完整");
            }

            try
            {
                hasher.AppendData(buffer, 0, read);
                destination.Write(buffer.AsSpan(0, read));
            }
            catch (IOException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.DestinationWriteFailed, error.Message);
            }
            catch (UnauthorizedAccessException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.DestinationWriteFailed, error.Message);
            }
            catch (ObjectDisposedException error)
            {
                return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.DestinationWriteFailed, error.Message);
            }

            total += read;
        }

        // —— 变化检测：增长、身份、最后写入时间、同名换文件 ——
        int extra;
        try
        {
            extra = source.Read(buffer.AsSpan(0, 1));
        }
        catch (IOException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
        }
        catch (ObjectDisposedException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
        }

        if (extra < 0)
        {
            return FailAfterCreate(
                ref destination,
                fullPath,
                ref deleted,
                AttachmentStagingCodes.SourceReadFailed,
                $"字节源返回非法长度 {extra}（增长复核）");
        }

        if (extra != 0)
        {
            return FailAfterCreate(
                ref destination,
                fullPath,
                ref deleted,
                AttachmentStagingCodes.SourceChanged,
                "源在复制期间增长：声明长度之外仍有字节");
        }

        StagingSourceIdentity after;
        try
        {
            after = source.ReadIdentity();
        }
        catch (IOException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
        }
        catch (ObjectDisposedException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.SourceReadFailed, error.Message);
        }

        var change = StagingSourceChangePolicy.Compare(source.InitialIdentity, after);
        if (!change.Unchanged)
        {
            TraceDecision("staging", capture.CaptureId, "源变化", $"source-changed：{change.Reason}");
            return FailAfterCreate(
                ref destination,
                fullPath,
                ref deleted,
                AttachmentStagingCodes.SourceChanged,
                change.Reason ?? "源在复制期间发生变化");
        }

        try
        {
            destination.FlushToDisk();
        }
        catch (IOException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.DestinationWriteFailed, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.DestinationWriteFailed, error.Message);
        }
        catch (ObjectDisposedException error)
        {
            return FailAfterCreate(ref destination, fullPath, ref deleted, AttachmentStagingCodes.DestinationWriteFailed, error.Message);
        }

        var digest = Convert.ToHexStringLower(hasher.GetHashAndReset());
        var snapshot = new StagingSnapshot(snapshotId, displayName, fullPath, descriptor.Length, digest);
        _snapshots[snapshotId] = snapshot;
        _batchFiles += 1;
        _batchBytes += descriptor.Length;
        TraceDecision(
            "staging",
            snapshotId,
            "快照完成",
            $"bytes={descriptor.Length} sha256={digest} handleDiscipline=finally");
        return new StagingCaptureResult
        {
            Ok = true,
            SnapshotId = snapshotId,
            DisplayName = displayName,
            StagedPath = fullPath,
            ByteLength = descriptor.Length,
            Sha256 = digest,
        };
    }

    private StagingCaptureResult CancelCapture(
        ref IStagingDestinationFile? destination,
        string fullPath,
        ref bool deleted)
    {
        DeletePartial(ref destination, fullPath, ref deleted);
        TraceDecision("staging", null, "取消", "cancelled：删除半成品并释放句柄");
        return FailCapture(AttachmentStagingCodes.Cancelled, "快照在复制期间被取消");
    }

    private StagingCaptureResult FailAfterCreate(
        ref IStagingDestinationFile? destination,
        string fullPath,
        ref bool deleted,
        string code,
        string detail)
    {
        DeletePartial(ref destination, fullPath, ref deleted);
        TraceDecision("staging", null, "快照失败", $"{code}：已删除半成品={deleted}");
        return FailCapture(code, detail);
    }

    /// <summary>删除半成品：删除前再次做包含关系判定，绝不删除自有根之外的东西。</summary>
    private void DeletePartial(ref IStagingDestinationFile? destination, string fullPath, ref bool deleted)
    {
        if (destination is null)
        {
            return;
        }

        var confined = StagingPathPolicy.ConfineToOwnedRoot(Root.FullPath, fullPath);
        if (!confined.Allowed)
        {
            return;
        }

        try
        {
            destination.Delete();
            deleted = true;
        }
        catch (IOException)
        {
            // 保留原始失败码；半成品未删掉的事实由 StagedFileDeleted=false 如实上报。
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AttachmentStagingException)
        {
        }
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void DropMissingLedgerEntries()
    {
        foreach (var snapshotId in _snapshots.Keys.ToList())
        {
            var snapshot = _snapshots[snapshotId];
            StagingOwnedEntryInfo info;
            try
            {
                info = _fileSystem.InspectOwnedEntry(Root.FullPath, snapshot.StagedPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!info.Exists)
            {
                _snapshots.Remove(snapshotId);
            }
        }
    }

    private void TraceDecision(string type, string? snapshotId, string state, string detail) =>
        _trace.Record(_clock.NowMs, "decision", type, null, snapshotId, $"{state}：{detail}");

    private static StagingCaptureResult FailCapture(string code, string detail) =>
        new() { Ok = false, Code = code, Detail = detail };

    private static StagingCleanupResult FailCleanup(string code, string detail, string? snapshotId) =>
        new() { Ok = false, Code = code, Detail = detail, SnapshotId = snapshotId };

    private static StagingCleanupResult OkCleanup(string snapshotId, int removed) =>
        new() { Ok = true, SnapshotId = snapshotId, RemovedCount = removed };

    private static AttachmentCoordinatorResult Fail(string code, string detail) =>
        new() { Ok = false, Code = code, Detail = detail };

    private static string LeafOf(string fullPath)
    {
        var index = fullPath.LastIndexOf('\\');
        return index >= 0 && index < fullPath.Length - 1 ? fullPath[(index + 1)..] : fullPath;
    }
}
