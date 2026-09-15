using System.Text;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// D14 暂存适配的<b>本地</b>判定码。线协议（D10）只承载冻结的
/// <see cref="AttachmentProtocol.ErrorCodes"/> 与 <see cref="AttachmentProtocol.WireErrorCodes"/>；
/// 下面这些码只出现在本地结果、trace 与测试断言里，<b>绝不</b>写进任何报文。
/// 与线协议语义重合的失败（file/batch/staging 限额、cancelled）直接复用冻结码，不另造同义码。
/// </summary>
public static class AttachmentStagingCodes
{
    // —— 候选接受（原生动作捕获的普通文件之外一律拒绝）——

    /// <summary>不是普通文件（设备、管道、套接字等）。</summary>
    public const string NotRegularFile = "candidate-not-regular-file";

    /// <summary>目录（不追踪目录，也不枚举其内容）。</summary>
    public const string Directory = "candidate-directory";

    /// <summary>UNC 路径（不接受网络共享）。</summary>
    public const string UncPath = "candidate-unc-path";

    /// <summary>设备/扩展路径（<c>\\?\</c>、<c>\\.\</c>、<c>\??\</c>）。</summary>
    public const string DevicePath = "candidate-device-path";

    /// <summary>非本机绝对路径（相对路径、根相对路径、盘符相对路径）。</summary>
    public const string PathNotAbsolute = "candidate-path-not-absolute";

    /// <summary>路径含 <c>.</c>/<c>..</c> 段（可能绕过包含关系判定）。</summary>
    public const string PathTraversal = "candidate-path-traversal";

    /// <summary>重解析点（符号链接/联接/挂载点）：不跟随，直接拒绝。</summary>
    public const string ReparsePoint = "candidate-reparse-point";

    /// <summary>云占位文件（RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS）：拒绝，避免自动下载。</summary>
    public const string CloudPlaceholder = "candidate-cloud-placeholder";

    /// <summary>脱机文件（FILE_ATTRIBUTE_OFFLINE 族）。</summary>
    public const string OfflineFile = "candidate-offline-file";

    /// <summary>位于远程卷（映射网络驱动器）上的文件。</summary>
    public const string NetworkShare = "candidate-network-share";

    /// <summary>原生捕获 id 未在本适配器登记（页面无法凭任意字符串换到文件）。</summary>
    public const string CaptureIdUnknown = "capture-id-unknown";

    /// <summary>同一原生捕获被消费两次（一次性票据）。</summary>
    public const string CaptureConsumed = "capture-already-consumed";

    /// <summary>未消费的原生捕获票据已达本地上界（不无界堆积）。</summary>
    public const string CaptureRegistryFull = "capture-registry-full";

    // —— 打开与快照 ——

    /// <summary>源文件无法打开（已删除、无权限、路径失效）。</summary>
    public const string SourceUnavailable = "source-unavailable";

    /// <summary>源文件被其他进程独占占用（共享冲突）。</summary>
    public const string SourceLocked = "source-locked";

    /// <summary>源在中途发生变化（长度/最后写入时间/文件标识不符，或提前 EOF/意外增长）。</summary>
    public const string SourceChanged = "source-changed";

    /// <summary>源读取失败（读取异常或返回非法长度）。</summary>
    public const string SourceReadFailed = "source-read-failed";

    /// <summary>暂存文件创建失败。</summary>
    public const string DestinationCreateFailed = "staging-create-failed";

    /// <summary>暂存文件写入失败（含落盘失败）。</summary>
    public const string DestinationWriteFailed = "staging-write-failed";

    /// <summary>解析出的暂存路径逃出自有根（联接/符号链接绕过）：立即拒绝并删除半成品。</summary>
    public const string DestinationOutsideOwnedRoot = "staging-outside-owned-root";

    /// <summary>可用空间不足以写入快照并保留安全余量。</summary>
    public const string InsufficientFreeSpace = "staging-free-space";

    /// <summary>复制被取消（复用冻结码 <c>cancelled</c>）。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>尚未 <see cref="AttachmentStagingService.BeginBatch"/>。</summary>
    public const string BatchNotOpen = "batch-not-open";

    /// <summary>同一暂存服务上已有开放批次。</summary>
    public const string BatchInProgress = "batch-in-progress";

    // —— 清理 ——

    /// <summary>清理目标不在自有根内（穿越/绝对路径/UNC/根外解析结果）。</summary>
    public const string CleanupOutsideOwnedRoot = "cleanup-outside-owned-root";

    /// <summary>清理目标不是普通文件（目录或非常规文件）。</summary>
    public const string CleanupNotRegularFile = "cleanup-not-regular-file";

    /// <summary>清理目标是重解析点：拒绝删除，避免顺着链接删除根外数据。</summary>
    public const string CleanupReparsePoint = "cleanup-reparse-point";

    /// <summary>删除失败（占用、只读、权限）。</summary>
    public const string CleanupFailed = "cleanup-failed";

    /// <summary>快照 id 不在本服务的台账里（调用方不能凭任意路径要求删除）。</summary>
    public const string SnapshotUnknown = "snapshot-unknown";
}

/// <summary>候选路径的字面形态（纯词法分类，不做任何文件系统访问）。</summary>
public enum StagingPathKind
{
    /// <summary>单个叶名，如 <c>a.txt</c>。</summary>
    RelativeLeaf,

    /// <summary>带分隔符的相对路径，如 <c>dir\a.txt</c>。</summary>
    RelativeNested,

    /// <summary>本机绝对路径，如 <c>C:\dir\a.txt</c>。</summary>
    DriveAbsolute,

    /// <summary>盘符相对路径，如 <c>C:a.txt</c>（解析结果依赖每进程当前目录）。</summary>
    DriveRelative,

    /// <summary>根相对路径，如 <c>\dir\a.txt</c>。</summary>
    Rooted,

    /// <summary>UNC 路径，如 <c>\\server\share\a.txt</c>。</summary>
    Unc,

    /// <summary>设备/扩展路径，如 <c>\\?\C:\a.txt</c>。</summary>
    Device,

    /// <summary>含 <c>.</c> 或 <c>..</c> 段的相对路径。</summary>
    Traversal,

    /// <summary>空、含控制字符或非法盘符。</summary>
    Invalid,

    /// <summary>
    /// 根本没有路径的来源（D16：宿主自己编码的截图字节）。它不是文件系统对象，
    /// 因此一切路径形态判定都不适用；候选接受只看来源标记。
    /// </summary>
    Synthetic,
}

/// <summary>路径分类结果。</summary>
public sealed record StagingPathClassification(StagingPathKind Kind, string RawPath, string Detail);

/// <summary>允许/拒绝判定；拒绝时给出稳定码与人类可读说明。</summary>
public sealed record StagingPathDecision
{
    public required bool Allowed { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    /// <summary>允许时的规范化本机绝对路径。</summary>
    public string? FullPath { get; init; }

    public static StagingPathDecision Allow(string fullPath) =>
        new() { Allowed = true, FullPath = fullPath };

    public static StagingPathDecision Deny(string code, string detail) =>
        new() { Allowed = false, Code = code, Detail = detail };
}

/// <summary>
/// D14 的平台中立路径策略。它刻意实现 <b>Windows 路径词法</b>而不是使用
/// <see cref="System.IO.Path"/>：暂存适配只服务 Windows，而把词法写成纯函数后，
/// 穿越/绝对路径/UNC/设备路径/包含关系这些判定可以在 Linux 上被逐条执行。
/// 本类型不做任何文件系统访问，也不含任何 P/Invoke。
/// </summary>
public static class StagingPathPolicy
{
    /// <summary>叶文件名长度上限，与冻结协议一致。</summary>
    public const int MaxLeafNameChars = AttachmentProtocol.MaxNameChars;

    /// <summary>显示名兜底值（源叶名不可用时使用）。</summary>
    public const string DefaultDisplayName = "attachment.bin";

    private static readonly char[] Separators = ['\\', '/'];

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>按字面形态分类一条路径；不访问文件系统，不解析 <c>..</c>。</summary>
    public static StagingPathClassification Classify(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new StagingPathClassification(StagingPathKind.Invalid, path ?? string.Empty, "路径为空");
        }

        if (path != path.Trim())
        {
            return new StagingPathClassification(StagingPathKind.Invalid, path, "路径首尾不得有空白");
        }

        foreach (var character in path)
        {
            if (char.IsControl(character))
            {
                return new StagingPathClassification(StagingPathKind.Invalid, path, "路径含控制字符");
            }
        }

        if (HasDevicePrefix(path))
        {
            return new StagingPathClassification(StagingPathKind.Device, path, "设备/扩展路径");
        }

        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return new StagingPathClassification(StagingPathKind.Unc, path, "UNC 路径");
        }

        if (path.Length >= 2 && path[1] == ':')
        {
            if (!IsAsciiLetter(path[0]))
            {
                return new StagingPathClassification(StagingPathKind.Invalid, path, "非法盘符");
            }

            var rest = path.Length > 2 ? path[2..] : string.Empty;
            if (rest.Length == 0)
            {
                return new StagingPathClassification(StagingPathKind.DriveRelative, path, "只有盘符");
            }

            if (!IsSeparator(rest[0]))
            {
                return new StagingPathClassification(StagingPathKind.DriveRelative, path, "盘符相对路径");
            }

            return HasTraversal(path)
                ? new StagingPathClassification(StagingPathKind.Traversal, path, "含 . 或 .. 段")
                : new StagingPathClassification(StagingPathKind.DriveAbsolute, path, "本机绝对路径");
        }

        if (IsSeparator(path[0]))
        {
            return new StagingPathClassification(StagingPathKind.Rooted, path, "根相对路径");
        }

        if (HasTraversal(path))
        {
            return new StagingPathClassification(StagingPathKind.Traversal, path, "含 . 或 .. 段");
        }

        return Segments(path).Length > 1
            ? new StagingPathClassification(StagingPathKind.RelativeNested, path, "相对路径含分隔符")
            : new StagingPathClassification(StagingPathKind.RelativeLeaf, path, "相对叶名");
    }

    /// <summary>是否含 <c>.</c> 或 <c>..</c> 段（两处分隔符都算）。</summary>
    public static bool HasTraversal(string path)
    {
        foreach (var segment in Segments(path))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>自有根必须是本机绝对路径且不含 <c>..</c>；返回去掉尾部分隔符的规范形式。</summary>
    public static string NormalizeOwnedRoot(string ownedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRoot);
        var classification = Classify(ownedRoot);
        if (classification.Kind != StagingPathKind.DriveAbsolute || HasTraversal(ownedRoot))
        {
            throw new ArgumentException(
                $"暂存根必须是本机绝对路径且不含 . 或 .. 段：{classification.Detail}",
                nameof(ownedRoot));
        }

        var normalized = TrimTrailingSeparators(ownedRoot).Replace('/', '\\');

        // Windows 规范化会把盘符大写；保持一致才能做大小写不敏感的包含关系判定。
        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    /// <summary>候选路径是否落在自有根内（严格子路径；大小写不敏感，与 Windows 语义一致）。</summary>
    public static bool IsWithinOwnedRoot(string ownedRoot, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(ownedRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var root = TrimTrailingSeparators(ownedRoot);
        var candidate = TrimTrailingSeparators(candidatePath);
        if (candidate.Length <= root.Length)
        {
            return false;
        }

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSeparator(candidate[root.Length]);
    }

    /// <summary>
    /// 把候选路径限制到自有根：先按词法拒绝穿越、UNC、设备路径与根相对路径，
    /// 再要求严格位于根内。文件系统解析后的复核（重解析点）由清理策略负责。
    /// </summary>
    public static StagingPathDecision ConfineToOwnedRoot(string ownedRoot, string? candidatePath)
    {
        _ = NormalizeOwnedRoot(ownedRoot);
        var classification = Classify(candidatePath);
        switch (classification.Kind)
        {
            case StagingPathKind.DriveAbsolute:
                break;
            case StagingPathKind.Unc:
                return StagingPathDecision.Deny(AttachmentStagingCodes.UncPath, classification.Detail);
            case StagingPathKind.Device:
                return StagingPathDecision.Deny(AttachmentStagingCodes.DevicePath, classification.Detail);
            case StagingPathKind.Traversal:
                return StagingPathDecision.Deny(AttachmentStagingCodes.PathTraversal, classification.Detail);
            case StagingPathKind.RelativeLeaf:
            case StagingPathKind.RelativeNested:
            case StagingPathKind.DriveRelative:
            case StagingPathKind.Rooted:
                return StagingPathDecision.Deny(AttachmentStagingCodes.PathNotAbsolute, classification.Detail);
            default:
                return StagingPathDecision.Deny(AttachmentStagingCodes.PathNotAbsolute, classification.Detail);
        }

        if (HasTraversal(classification.RawPath))
        {
            return StagingPathDecision.Deny(AttachmentStagingCodes.PathTraversal, "路径含 . 或 .. 段");
        }

        if (!IsWithinOwnedRoot(ownedRoot, classification.RawPath))
        {
            return StagingPathDecision.Deny(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                "路径不在自有暂存根内");
        }

        return StagingPathDecision.Allow(NormalizeFullPath(classification.RawPath));
    }

    /// <summary>把叶名解析为自有根下的绝对路径；叶名本身必须合法（无分隔符、无 <c>..</c>）。</summary>
    public static StagingPathDecision ResolveOwnedLeaf(string ownedRoot, string? leafName)
    {
        var root = NormalizeOwnedRoot(ownedRoot);
        if (!IsValidLeafName(leafName))
        {
            return StagingPathDecision.Deny(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                "叶名非法：不允许分隔符、: 或 . / .. 段");
        }

        var fullPath = root + "\\" + leafName;
        return ConfineToOwnedRoot(root, fullPath);
    }

    /// <summary>
    /// 叶名合法性：非空、≤255 字符、无分隔符/冒号（数据流）、无控制字符、
    /// 不是 <c>.</c>/<c>..</c>、不以点或空格结尾、不是保留设备名。
    /// </summary>
    public static bool IsValidLeafName(string? leafName)
    {
        if (string.IsNullOrEmpty(leafName) || leafName.Length > MaxLeafNameChars)
        {
            return false;
        }

        if (leafName is "." or "..")
        {
            return false;
        }

        if (leafName != leafName.Trim() || leafName.EndsWith('.') || leafName.EndsWith(' '))
        {
            return false;
        }

        foreach (var character in leafName)
        {
            if (char.IsControl(character) || IsSeparator(character) || character == ':')
            {
                return false;
            }
        }

        var stem = leafName;
        var dot = leafName.IndexOf('.');
        if (dot >= 0)
        {
            stem = leafName[..dot];
        }

        return !ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把任意来源文件名规范成可安全使用的显示名（仅用于展示与记录）。</summary>
    public static string NormalizeDisplayName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return DefaultDisplayName;
        }

        var leaf = rawName;
        var lastSeparator = leaf.LastIndexOfAny(Separators);
        if (lastSeparator >= 0)
        {
            leaf = leaf[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(leaf.Length);
        foreach (var character in leaf)
        {
            if (char.IsControl(character))
            {
                continue;
            }

            builder.Append(InvalidNameChars.Contains(character) ? '_' : character);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            return DefaultDisplayName;
        }

        if (cleaned.Length > MaxLeafNameChars)
        {
            var extension = ExtensionOf(cleaned);
            var keep = MaxLeafNameChars - extension.Length;
            cleaned = keep > 0 ? cleaned[..keep] + extension : cleaned[..MaxLeafNameChars];
        }

        return IsValidLeafName(cleaned) ? cleaned : "_" + cleaned;
    }

    /// <summary>暂存文件名：<c>{snapshotId}.{扩展名}</c>；扩展名只保留安全字符，否则用 <c>bin</c>。</summary>
    public static string CreateStagingLeafName(string displayName, string snapshotId)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);
        var extension = ExtensionOf(NormalizeDisplayName(displayName)).TrimStart('.');
        var safe = new StringBuilder(extension.Length);
        foreach (var character in extension)
        {
            if (IsAsciiLetterOrDigit(character))
            {
                safe.Append(char.ToLowerInvariant(character));
            }
        }

        var suffix = safe.Length is > 0 and <= 16 ? safe.ToString() : "bin";
        return snapshotId + "." + suffix;
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..] : string.Empty;
    }

    private static string NormalizeFullPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\\', segments);
    }

    private static string TrimTrailingSeparators(string path) => path.TrimEnd(Separators);

    private static string[] Segments(string path) =>
        path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static bool HasDevicePrefix(string path)
    {
        // Win32 扩展前缀：\\?\ 与 \\.\
        if (path.Length >= 4 && IsSeparator(path[0]) && IsSeparator(path[1]) && path[2] is '?' or '.')
        {
            return IsSeparator(path[3]);
        }

        // NT 对象管理器前缀：\??\（单个前导分隔符）
        return path.Length >= 4 && IsSeparator(path[0]) && path[1] == '?' && path[2] == '?' && IsSeparator(path[3]);
    }

    private static bool IsSeparator(char character) => character is '\\' or '/';

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiLetterOrDigit(char character) =>
        IsAsciiLetter(character) || character is >= '0' and <= '9';
}

/// <summary>候选在文件系统里的种类（由平台层用无跟随语义判定）。</summary>
public enum StagingSourceKind
{
    /// <summary>普通文件。</summary>
    RegularFile,

    /// <summary>目录。</summary>
    Directory,

    /// <summary>设备、管道、套接字等非常规对象。</summary>
    Device,

    /// <summary>其他（不认识的类型）。</summary>
    Other,
}

/// <summary>
/// 候选从哪来。D16 增加 <see cref="NativeScreenshot"/>：宿主自己编码的内存字节，
/// 没有任何路径可被调用方指定。
/// </summary>
public enum StagingSourceOrigin
{
    /// <summary>本机磁盘上的普通文件（D14 原生捕获）。</summary>
    LocalFile,

    /// <summary>宿主自己产生的截图 PNG 字节（D16），不是文件系统对象。</summary>
    NativeScreenshot,
}

/// <summary>
/// 平台层对一个候选的描述。路径形态由 <see cref="StagingPathPolicy.Classify"/> 给出；
/// 重解析点/云占位/脱机/网络卷都是平台事实（Windows 侧来自 GetFileInformationByHandleEx、
/// GetFileType 与 GetDriveType），Core 只按它们做判定，不自行猜测。
/// </summary>
public sealed record StagingSourceDescriptor
{
    /// <summary>来源类别（默认本机文件）。</summary>
    public StagingSourceOrigin Origin { get; init; } = StagingSourceOrigin.LocalFile;

    public required StagingPathKind PathKind { get; init; }

    public required string LeafName { get; init; }

    public required StagingSourceKind Kind { get; init; }

    public required long Length { get; init; }

    public bool IsReparsePoint { get; init; }

    public bool IsCloudPlaceholder { get; init; }

    public bool IsOffline { get; init; }

    public bool IsNetworkShare { get; init; }
}

/// <summary>候选接受判定：只接受原生用户动作捕获的普通文件。</summary>
public static class StagingCandidatePolicy
{
    /// <summary>路径形态对应的稳定拒绝码（原生层与 Core 共用同一张表）。</summary>
    public static string CodeForPathKind(StagingPathKind kind) => kind switch
    {
        StagingPathKind.Unc => AttachmentStagingCodes.UncPath,
        StagingPathKind.Device => AttachmentStagingCodes.DevicePath,
        StagingPathKind.Traversal => AttachmentStagingCodes.PathTraversal,
        _ => AttachmentStagingCodes.PathNotAbsolute,
    };

    /// <summary>
    /// 判定顺序固定为：来源类别 → 对象种类 → 路径形态 → 重解析点 → 云占位 → 脱机 → 远程卷 → 长度。
    /// 顺序固定使拒绝原因确定可复现（例如 UNC 目录报 <c>candidate-directory</c>）。
    ///
    /// <see cref="StagingSourceOrigin.NativeScreenshot"/> 没有路径：它由宿主自己的编码器产生，
    /// 不是从任何路径读来的，因此跳过全部路径/属性判定，只要求"普通字节来源 + 合法长度"。
    /// </summary>
    public static StagingPathDecision Evaluate(StagingSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Origin == StagingSourceOrigin.NativeScreenshot)
        {
            if (descriptor.Kind != StagingSourceKind.RegularFile || descriptor.Length < 0)
            {
                return StagingPathDecision.Deny(AttachmentStagingCodes.NotRegularFile, "截图字节来源非法");
            }

            return StagingPathDecision.Allow(descriptor.LeafName);
        }

        switch (descriptor.Kind)
        {
            case StagingSourceKind.Directory:
                return StagingPathDecision.Deny(AttachmentStagingCodes.Directory, "候选是目录");
            case StagingSourceKind.Device:
                return StagingPathDecision.Deny(AttachmentStagingCodes.NotRegularFile, "候选是设备/管道等非常规对象");
            case StagingSourceKind.Other:
                return StagingPathDecision.Deny(AttachmentStagingCodes.NotRegularFile, "候选不是普通文件");
            default:
                break;
        }

        switch (descriptor.PathKind)
        {
            case StagingPathKind.DriveAbsolute:
                break;
            case StagingPathKind.Unc:
                return StagingPathDecision.Deny(AttachmentStagingCodes.UncPath, "不接受 UNC 路径");
            case StagingPathKind.Device:
                return StagingPathDecision.Deny(AttachmentStagingCodes.DevicePath, "不接受设备/扩展路径");
            case StagingPathKind.Traversal:
                return StagingPathDecision.Deny(AttachmentStagingCodes.PathTraversal, "路径含 . 或 .. 段");
            default:
                return StagingPathDecision.Deny(AttachmentStagingCodes.PathNotAbsolute, "候选必须是本机绝对路径");
        }

        if (descriptor.IsReparsePoint)
        {
            return StagingPathDecision.Deny(AttachmentStagingCodes.ReparsePoint, "候选是重解析点（符号链接/联接/挂载点）");
        }

        if (descriptor.IsCloudPlaceholder)
        {
            return StagingPathDecision.Deny(
                AttachmentStagingCodes.CloudPlaceholder,
                "候选是云占位文件：拒绝以免触发自动下载");
        }

        if (descriptor.IsOffline)
        {
            return StagingPathDecision.Deny(AttachmentStagingCodes.OfflineFile, "候选是脱机文件");
        }

        if (descriptor.IsNetworkShare)
        {
            return StagingPathDecision.Deny(AttachmentStagingCodes.NetworkShare, "候选位于远程卷");
        }

        if (descriptor.Length < 0)
        {
            return StagingPathDecision.Deny(AttachmentStagingCodes.NotRegularFile, "候选长度非法");
        }

        return StagingPathDecision.Allow(descriptor.LeafName);
    }
}

/// <summary>
/// 源身份：长度、最后写入时间、文件标识（卷序列号 + 文件索引）与重解析点标记。
/// Windows 侧来自<b>同一个句柄</b>的 GetFileInformationByHandle，
/// 并在复制后重新解析源路径比较文件标识，因此"同名换文件"也会被识别为变化。
/// </summary>
public readonly record struct StagingSourceIdentity(
    bool Exists,
    long Length,
    long LastWriteTimeUtcTicks,
    ulong FileId,
    uint VolumeSerial,
    bool IsReparsePoint)
{
    /// <summary>源已不存在。</summary>
    public static StagingSourceIdentity Missing => new(false, 0, 0, 0, 0, false);
}

/// <summary>源变化判定结果。</summary>
public sealed record StagingChangeDecision(bool Unchanged, string? Reason)
{
    public static StagingChangeDecision Same { get; } = new(true, null);

    public static StagingChangeDecision Changed(string reason) => new(false, reason);
}

/// <summary>复制前后身份比较（纯函数：不访问文件系统）。</summary>
public static class StagingSourceChangePolicy
{
    /// <summary>任一维度不同即判定源已变化；失败码统一为 <see cref="AttachmentStagingCodes.SourceChanged"/>。</summary>
    public static StagingChangeDecision Compare(StagingSourceIdentity before, StagingSourceIdentity after)
    {
        if (!after.Exists)
        {
            return StagingChangeDecision.Changed("源已不存在");
        }

        if (after.Length != before.Length)
        {
            return StagingChangeDecision.Changed($"长度变化：{before.Length} → {after.Length}");
        }

        if (after.LastWriteTimeUtcTicks != before.LastWriteTimeUtcTicks)
        {
            return StagingChangeDecision.Changed("最后写入时间变化");
        }

        if (after.FileId != before.FileId || after.VolumeSerial != before.VolumeSerial)
        {
            return StagingChangeDecision.Changed("文件标识变化（同名换文件或换卷）");
        }

        if (after.IsReparsePoint)
        {
            return StagingChangeDecision.Changed("复制后变成重解析点");
        }

        return StagingChangeDecision.Same;
    }
}

/// <summary>当前暂存台账（用于写入前的限额判定）。</summary>
public readonly record struct StagingQuotaState(long StagedBytesForTarget, int FilesInBatch, long BatchBytes);

/// <summary>限额判定结果。</summary>
public sealed record StagingQuotaDecision
{
    public required bool Allowed { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public static StagingQuotaDecision Allow() => new() { Allowed = true };

    public static StagingQuotaDecision Deny(string code, string detail) =>
        new() { Allowed = false, Code = code, Detail = detail };
}

/// <summary>
/// 写入<b>之前</b>的限额与磁盘空间判定（纯函数）。判定顺序固定：
/// 单文件 → 批内文件数 → 批字节 → 单目标暂存字节 → 可用空间（含保留量）。
/// 复用冻结线协议限额，不另立一套数字。
/// </summary>
public static class StagingQuotaPolicy
{
    public static StagingQuotaDecision Evaluate(
        AttachmentLimits limits,
        StagingQuotaState state,
        long requestedBytes,
        long availableFreeBytes,
        long freeSpaceReserveBytes)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (requestedBytes < 0 || requestedBytes > limits.MaxFileBytes)
        {
            return StagingQuotaDecision.Deny(
                "limit-file-bytes",
                $"文件 {requestedBytes} 字节超出 0..{limits.MaxFileBytes}");
        }

        if (state.FilesInBatch + 1 > limits.MaxFilesPerBatch)
        {
            return StagingQuotaDecision.Deny(
                "limit-batch-files",
                $"批内文件数将达到 {state.FilesInBatch + 1}，超过 {limits.MaxFilesPerBatch}");
        }

        if (state.BatchBytes + requestedBytes > limits.MaxBatchBytes)
        {
            return StagingQuotaDecision.Deny(
                "limit-batch-bytes",
                $"批字节将达到 {state.BatchBytes + requestedBytes}，超过 {limits.MaxBatchBytes}");
        }

        if (state.StagedBytesForTarget + requestedBytes > limits.MaxStagingBytesPerTarget)
        {
            return StagingQuotaDecision.Deny(
                "limit-staging-bytes",
                $"单目标暂存将达到 {state.StagedBytesForTarget + requestedBytes}，超过 {limits.MaxStagingBytesPerTarget}");
        }

        var reserve = Math.Max(0, freeSpaceReserveBytes);
        if (availableFreeBytes < requestedBytes + reserve)
        {
            return StagingQuotaDecision.Deny(
                AttachmentStagingCodes.InsufficientFreeSpace,
                $"可用空间 {availableFreeBytes} 字节不足以写入 {requestedBytes} 字节并保留 {reserve} 字节");
        }

        return StagingQuotaDecision.Allow();
    }
}

/// <summary>清理判定结果。</summary>
public sealed record StagingCleanupDecision
{
    public required bool Allowed { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public static StagingCleanupDecision Allow() => new() { Allowed = true };

    public static StagingCleanupDecision Deny(string code, string detail) =>
        new() { Allowed = false, Code = code, Detail = detail };
}

/// <summary>
/// 清理只允许动自有暂存根内的普通文件：词法包含关系（穿越/绝对/UNC/根相对一律拒绝）
/// 加上平台层给出的<b>解析后最终路径</b>与重解析点标记复核。
/// 本类型是纯函数，因此这些拒绝路径可以在 Linux 上逐条验证。
/// </summary>
public static class StagingCleanupPolicy
{
    /// <summary>
    /// <paramref name="entryFullPath"/> 必须恰好是自有根下的一个叶文件；
    /// <paramref name="info"/> 是平台层用无跟随语义取得的解析结果。
    /// </summary>
    public static StagingCleanupDecision Evaluate(string ownedRoot, string? entryFullPath, StagingOwnedEntryInfo? info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedRoot);
        ArgumentNullException.ThrowIfNull(info);

        var root = StagingPathPolicy.NormalizeOwnedRoot(ownedRoot);
        var confined = StagingPathPolicy.ConfineToOwnedRoot(root, entryFullPath);
        if (!confined.Allowed)
        {
            return StagingCleanupDecision.Deny(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                confined.Detail ?? "清理目标不在自有根内");
        }

        var leaf = LeafOf(confined.FullPath!);
        if (!StagingPathPolicy.IsValidLeafName(leaf)
            || !string.Equals(confined.FullPath, root + "\\" + leaf, StringComparison.OrdinalIgnoreCase))
        {
            return StagingCleanupDecision.Deny(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                "清理目标不是自有根的直接子项");
        }

        if (!info.Exists)
        {
            return StagingCleanupDecision.Allow();
        }

        if (info.IsReparsePoint)
        {
            return StagingCleanupDecision.Deny(
                AttachmentStagingCodes.CleanupReparsePoint,
                "清理目标是重解析点：拒绝顺链删除");
        }

        if (info.IsDirectory)
        {
            return StagingCleanupDecision.Deny(
                AttachmentStagingCodes.CleanupNotRegularFile,
                "清理目标是目录");
        }

        if (!StagingPathPolicy.IsWithinOwnedRoot(root, info.FinalPath))
        {
            return StagingCleanupDecision.Deny(
                AttachmentStagingCodes.CleanupOutsideOwnedRoot,
                "解析后的最终路径落在自有根之外");
        }

        return StagingCleanupDecision.Allow();
    }

    private static string LeafOf(string fullPath)
    {
        var index = fullPath.LastIndexOf('\\');
        return index >= 0 ? fullPath[(index + 1)..] : fullPath;
    }
}
