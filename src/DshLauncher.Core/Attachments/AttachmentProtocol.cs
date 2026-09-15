namespace DshLauncher.Core.Attachments;

/// <summary>
/// 远程附件线协议 version=1 的冻结事实（D10）。
///
/// 这里是 C# 侧的单一真值，必须与
/// <c>plugins/dsh-remote-attachments/src/shared/protocol.ts</c>、
/// <c>schemas/remote-attachments/v1/schema.json</c> 和
/// <c>schemas/remote-attachments/v1/expected.json</c> 逐项一致；
/// 三处任一改动都会让 <c>pnpm run test:wire</c> 或本项目的语料测试失败。
/// </summary>
public static class AttachmentProtocol
{
    /// <summary>协议版本，必须精确等于 1。</summary>
    public const int Version = 1;

    /// <summary>原始字节块大小：256 KiB。Base64 只编码单个块。</summary>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>单文件最多两块在途（背压窗口）。</summary>
    public const int MaxChunksInFlight = 2;

    /// <summary>单文件最大原始字节数（20 MiB）。</summary>
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>单批最多文件数。</summary>
    public const int MaxFilesPerBatch = 10;

    /// <summary>单批最大原始字节数（50 MiB）。</summary>
    public const int MaxBatchBytes = 50 * 1024 * 1024;

    /// <summary>截图编码前最大像素数。</summary>
    public const long MaxScreenshotPixels = 40_000_000L;

    /// <summary>单目标暂存上限（100 MiB）。</summary>
    public const int MaxStagingBytesPerTarget = 100 * 1024 * 1024;

    /// <summary>同时传输的目标数上限。</summary>
    public const int MaxConcurrentTargets = 2;

    /// <summary>单文件最多块数：20 MiB / 256 KiB = 80。</summary>
    public const int MaxChunksPerFile = 80;

    /// <summary>每文件最大 seq（0..79）。</summary>
    public const int MaxSeq = MaxChunksPerFile - 1;

    /// <summary>块起始 offset 上限。</summary>
    public const int MaxChunkOffset = MaxFileBytes - 1;

    /// <summary>256 KiB 原始字节的 Base64 字符数上限（无换行）。</summary>
    public const int MaxBase64Chars = 349_528;

    /// <summary>单条消息编码后的 UTF-8 字节上限（512 KiB）。</summary>
    public const int MaxMessageBytes = 512 * 1024;

    /// <summary>标识类字符串最大字符数。</summary>
    public const int MaxIdChars = 128;

    /// <summary>叶文件名最大字符数（禁止路径分隔符）。</summary>
    public const int MaxNameChars = 255;

    /// <summary>MIME 最大字符数。</summary>
    public const int MaxMimeChars = 128;

    /// <summary>单次结果里的新增附件 ID 上限。</summary>
    public const int MaxAttachmentIds = 16;

    /// <summary>一次草稿导入提交项数上限（缺省 1）。</summary>
    public const int MaxSubmittedItems = 16;

    /// <summary>epoch 上限（int32 范围内的非负整数）。</summary>
    public const int MaxEpoch = 2_147_483_647;

    /// <summary>重放/去重缓存容量。</summary>
    public const int ReplayCacheCapacity = 64;

    /// <summary>重放/去重缓存条目生命周期（毫秒）。</summary>
    public const int ReplayCacheLifetimeMs = 120_000;

    /// <summary>hello↔capabilities 握手与 context 建立上限（毫秒）。</summary>
    public const int HandshakeTimeoutMs = 5_000;

    /// <summary>单块 ACK 等待上限（毫秒）。</summary>
    public const int AckTimeoutMs = 10_000;

    /// <summary>file-end 之后等待 import-result 的上限（毫秒）。</summary>
    public const int FileEndTimeoutMs = 30_000;

    /// <summary>批次空闲上限（毫秒）。</summary>
    public const int BatchIdleTimeoutMs = 60_000;

    /// <summary>冻结的 11 种消息类型。</summary>
    public static readonly string[] MessageTypes =
    [
        "hello",
        "capabilities",
        "context",
        "batch-begin",
        "file-begin",
        "chunk",
        "ack",
        "file-end",
        "import-result",
        "batch-end",
        "cancel",
    ];

    /// <summary>线协议能力枚举（hello / capabilities 的 features 字段）。</summary>
    public static readonly string[] WireFeatures =
    [
        "chunked-transfer",
        "file-end-hash",
        "screenshot",
        "multi-file-batch",
        "cancel",
    ];

    /// <summary>单文件结果状态；没有 upload ready/uploaded。</summary>
    public static readonly string[] ResultStatuses = ["staged", "failed", "partial"];

    /// <summary>codec 与状态机拒绝消息时使用的稳定错误码（36 个）。</summary>
    public static readonly string[] WireErrorCodes =
    [
        "malformed-json",
        "version-mismatch",
        "unknown-message-type",
        "unknown-field",
        "field-name-invalid",
        "missing-field",
        "invalid-field-type",
        "invalid-field-value",
        "unknown-enum-value",
        "integer-out-of-range",
        "negative-integer",
        "message-too-large",
        "field-too-large",
        "limit-batch-files",
        "limit-batch-bytes",
        "limit-file-bytes",
        "limit-staging-bytes",
        "no-session",
        "context-changed",
        "batch-not-open",
        "batch-closed",
        "batch-in-progress",
        "file-in-progress",
        "file-id-mismatch",
        "file-not-ended",
        "sequence-gap",
        "seq-overlap",
        "window-overflow",
        "duplicate-operation",
        "duplicate-file-end",
        "size-mismatch",
        "hash-mismatch",
        "import-id-count-mismatch",
        "result-incomplete",
        "result-conflict",
        "cancelled",
    ];

    /// <summary>四阶段结果码，只出现在报文的 code 字段里，不用作拒绝码。</summary>
    public static readonly string[] ErrorCodes =
    [
        "no-session",
        "context-changed",
        "limit-file-bytes",
        "limit-batch-files",
        "limit-batch-bytes",
        "limit-screenshot-pixels",
        "limit-staging-bytes",
        "hash-mismatch",
        "size-mismatch",
        "sequence-gap",
        "window-overflow",
        "cancelled",
        "duplicate-operation",
        "capability-conflict",
        "capability-disabled",
        "draft-import-failed",
        "partial-import",
        "upload-failed",
        "reload-required",
    ];

    /// <summary>错误四阶段：原生采集、协议传输、草稿导入、远端上传。</summary>
    public static readonly string[] ErrorStages =
    [
        "native-capture",
        "protocol-transfer",
        "draft-import",
        "remote-upload",
    ];

    /// <summary>标识类字符串允许的字符集。</summary>
    public const string IdPattern = "^[A-Za-z0-9._:-]+$";

    /// <summary>SHA-256 十六进制小写表示。</summary>
    public const string Sha256Pattern = "^[0-9a-f]{64}$";

    /// <summary>叶文件名：禁止路径分隔符与 NUL（不提供任意本机路径接口）。</summary>
    public const string LeafNamePattern = "^[^/\\\\\u0000]+$";

    /// <summary>MIME 类型。</summary>
    public const string MimePattern = "^[A-Za-z0-9.+-]+/[A-Za-z0-9.+-]+$";

    /// <summary>构建标识。</summary>
    public const string BuildIdPattern = "^[A-Za-z0-9._/+:-]+$";

    /// <summary>规范 Base64（不含 padding 部分）。</summary>
    public const string Base64Pattern = "^[A-Za-z0-9+/]+={0,2}$";

    /// <summary>消息类型名到枚举的映射（未知类型返回 null）。</summary>
    public static WireMessageKind? KindOf(string type) => type switch
    {
        "hello" => WireMessageKind.Hello,
        "capabilities" => WireMessageKind.Capabilities,
        "context" => WireMessageKind.Context,
        "batch-begin" => WireMessageKind.BatchBegin,
        "file-begin" => WireMessageKind.FileBegin,
        "chunk" => WireMessageKind.Chunk,
        "ack" => WireMessageKind.Ack,
        "file-end" => WireMessageKind.FileEnd,
        "import-result" => WireMessageKind.ImportResult,
        "batch-end" => WireMessageKind.BatchEnd,
        "cancel" => WireMessageKind.Cancel,
        _ => null,
    };

    /// <summary>重放缓存键：documentEpoch:composerEpoch:batchId:fileId（批级 fileId 为空）。</summary>
    public static string ReplayKey(int documentEpoch, int composerEpoch, string batchId, string fileId) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{documentEpoch}:{composerEpoch}:{batchId}:{fileId}");
}

/// <summary>冻结的 11 种消息类型。</summary>
public enum WireMessageKind
{
    Hello,
    Capabilities,
    Context,
    BatchBegin,
    FileBegin,
    Chunk,
    Ack,
    FileEnd,
    ImportResult,
    BatchEnd,
    Cancel,
}

/// <summary>生效限额（capabilities 协商后的结果，不得超过冻结上限）。</summary>
public sealed record AttachmentLimits
{
    public int MaxFileBytes { get; init; } = AttachmentProtocol.MaxFileBytes;

    public int MaxFilesPerBatch { get; init; } = AttachmentProtocol.MaxFilesPerBatch;

    public int MaxBatchBytes { get; init; } = AttachmentProtocol.MaxBatchBytes;

    public long MaxScreenshotPixels { get; init; } = AttachmentProtocol.MaxScreenshotPixels;

    public int MaxStagingBytesPerTarget { get; init; } = AttachmentProtocol.MaxStagingBytesPerTarget;

    public int MaxConcurrentTargets { get; init; } = AttachmentProtocol.MaxConcurrentTargets;
}

/// <summary>身份：文档 epoch 由原生端掌握，composer 身份由插件当前会话 scope 产生。</summary>
public sealed record AttachmentIdentity(
    string SessionId,
    string TargetId,
    int DocumentEpoch,
    int ComposerEpoch,
    string ComposerScope);
