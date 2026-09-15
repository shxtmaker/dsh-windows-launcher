/**
 * 附件传输协议的共享契约（方案 2.0 第 4 节）。
 *
 * 这里是 host、client 与测试共同读取的**唯一真值**：限额、分块参数、消息类型与
 * 错误码都只在此定义一次，避免两端各自猜测后漂移。D10 会在同一组常量上冻结
 * 字段级 schema 与黄金样本。
 *
 * 所有数字都是可测试的首期配置，不代表代理或宿主已接受同样大小。
 */

/** 协议版本。D10 冻结字段前不允许静默改动。 */
export const PROTOCOL_VERSION = 1

/** 原始字节块大小：256 KiB。Base64 只对单个块编码，禁止整文件拼接。 */
export const CHUNK_BYTES = 256 * 1024

/** 单文件最多两块在途（背压窗口）。 */
export const MAX_CHUNKS_IN_FLIGHT = 2

/** 默认限额（方案 4.1 节首期建议值）。 */
export const DEFAULT_LIMITS = Object.freeze({
  /** 单文件最大原始字节数：20 MiB。 */
  maxFileBytes: 20 * 1024 * 1024,
  /** 单批最多文件数：10。 */
  maxFilesPerBatch: 10,
  /** 单批最大原始字节数：50 MiB。 */
  maxBatchBytes: 50 * 1024 * 1024,
  /** 截图编码前最大像素数：4000 万。 */
  maxScreenshotPixels: 40_000_000,
  /** 单目标暂存上限：100 MiB。 */
  maxStagingBytesPerTarget: 100 * 1024 * 1024,
  /** 同时传输的目标数上限：2。 */
  maxConcurrentTargets: 2
})

/**
 * 传输消息类型。枚举写入 schema，不能由两端分别猜测。
 * @see implementation-plan.md 4.2
 */
export const MESSAGE_TYPES = Object.freeze([
  'hello',
  'capabilities',
  'context',
  'batch-begin',
  'file-begin',
  'chunk',
  'ack',
  'file-end',
  'import-result',
  'batch-end',
  'cancel'
])

/**
 * 错误阶段：原生采集、协议传输、草稿导入、远端上传。
 * 四阶段分开报告，便于定位而不合并成单一状态码。
 */
export const ERROR_STAGES = Object.freeze([
  'native-capture',
  'protocol-transfer',
  'draft-import',
  'remote-upload'
])

/** 可机器识别的错误码。 */
export const ERROR_CODES = Object.freeze([
  'no-session',
  'context-changed',
  'limit-file-bytes',
  'limit-batch-files',
  'limit-batch-bytes',
  'limit-screenshot-pixels',
  'limit-staging-bytes',
  'hash-mismatch',
  'size-mismatch',
  'sequence-gap',
  'window-overflow',
  'cancelled',
  'duplicate-operation',
  'capability-conflict',
  'capability-disabled',
  'draft-import-failed',
  'partial-import',
  'upload-failed',
  'reload-required'
])

/**
 * D10 冻结的线协议附加常量。
 *
 * 这一段与 `schemas/remote-attachments/v1/schema.json`、
 * `schemas/remote-attachments/v1/expected.json` 以及 C# 侧
 * `DshLauncher.Core.Attachments.AttachmentProtocol` 必须逐项一致；
 * 三处任一改动都会让 `scripts/wire-contract-gates.mjs` 失败。
 */

/** 线协议能力枚举（`hello` / `capabilities` 的 `features` 字段）。 */
export const WIRE_FEATURES = Object.freeze([
  'chunked-transfer',
  'file-end-hash',
  'screenshot',
  'multi-file-batch',
  'cancel'
])

/** `import-result` 与 `batch-end` 的单文件结果状态；没有 upload ready。 */
export const RESULT_STATUSES = Object.freeze(['staged', 'failed', 'partial'])

/**
 * codec 与状态机拒绝消息时使用的稳定错误码。
 * 与 `ERROR_CODES` 分工不同：`ERROR_CODES` 是四阶段的结果码，
 * 这里是与 schema / expected.json 对齐的线协议拒绝码。
 */
export const WIRE_ERROR_CODES = Object.freeze([
  'malformed-json',
  'version-mismatch',
  'unknown-message-type',
  'unknown-field',
  'field-name-invalid',
  'missing-field',
  'invalid-field-type',
  'invalid-field-value',
  'unknown-enum-value',
  'integer-out-of-range',
  'negative-integer',
  'message-too-large',
  'field-too-large',
  'limit-batch-files',
  'limit-batch-bytes',
  'limit-file-bytes',
  'limit-staging-bytes',
  'no-session',
  'context-changed',
  'batch-not-open',
  'batch-closed',
  'batch-in-progress',
  'file-in-progress',
  'file-id-mismatch',
  'file-not-ended',
  'sequence-gap',
  'seq-overlap',
  'window-overflow',
  'duplicate-operation',
  'duplicate-file-end',
  'size-mismatch',
  'hash-mismatch',
  'import-id-count-mismatch',
  'result-incomplete',
  'result-conflict',
  'cancelled'
])

/** 单条消息编码后的 UTF-8 字节上限（256 KiB 块的 Base64 约 341 KiB）。 */
export const MAX_MESSAGE_BYTES = 512 * 1024

/** 单个文件最多块数：20 MiB / 256 KiB = 80，因此 seq 合法区间是 0..79。 */
export const MAX_CHUNKS_PER_FILE = Math.ceil(DEFAULT_LIMITS.maxFileBytes / CHUNK_BYTES)

/** 每文件最大 seq。 */
export const MAX_SEQ = MAX_CHUNKS_PER_FILE - 1

/** 块起始 offset 上限（块必须落在文件内）。 */
export const MAX_CHUNK_OFFSET = DEFAULT_LIMITS.maxFileBytes - 1

/** 256 KiB 原始字节的 Base64 字符数（无换行）。 */
export const MAX_BASE64_CHARS = 4 * Math.ceil(CHUNK_BYTES / 3)

/** 标识类字符串（sessionId/batchId/fileId/targetId/composerScope）最大字符数。 */
export const MAX_ID_CHARS = 128

/** 叶文件名最大字符数；禁止路径分隔符。 */
export const MAX_NAME_CHARS = 255

/** MIME 最大字符数。 */
export const MAX_MIME_CHARS = 128

/** 单次 import-result / batch-end 结果里的附件 ID 上限。 */
export const MAX_ATTACHMENT_IDS = 16

/** file-end 声明的本次草稿导入提交项数上限（默认 1）。 */
export const MAX_SUBMITTED_ITEMS = 16

/** epoch 都是 int32 范围内的非负整数。 */
export const MAX_EPOCH = 2_147_483_647

/** 标识类字符串允许的字符集。 */
export const ID_PATTERN = /^[A-Za-z0-9._:-]+$/

/** SHA-256 十六进制小写表示。 */
export const SHA256_PATTERN = /^[0-9a-f]{64}$/

/** 冻结超时（毫秒）。由 WireSession/AttachmentSession 的 tick/checkTimeouts 判定。 */
export const WIRE_TIMEOUTS = Object.freeze({
  /** hello↔capabilities 握手与 context 建立。 */
  handshakeMs: 5_000,
  /** 单块 ACK 等待上限；超过即视为该块停滞。 */
  ackMs: 10_000,
  /** file-end 之后等待 import-result 的上限。 */
  fileEndMs: 30_000,
  /** 批次整体空闲上限。 */
  batchIdleMs: 60_000
})

/** 重放/去重缓存容量（可被活动操作钉住而暂时超出）。 */
export const REPLAY_CACHE_CAPACITY = 64

/** 重放/去重缓存条目生命周期（毫秒）。 */
export const REPLAY_CACHE_LIFETIME_MS = 120_000

/** 协议版本与两端一致性的判定结果。 */
export type VersionCheck =
  | { ok: true; version: number }
  | { ok: false; code: 'version-mismatch'; expected: number; received: unknown }

/** 限额判定结果。 */
export type LimitCheck =
  | { ok: true }
  | { ok: false; code: string; limit: number; actual: number }

/** 限额配置形状。 */
export interface Limits {
  maxFileBytes: number
  maxFilesPerBatch: number
  maxBatchBytes: number
  maxScreenshotPixels: number
  maxStagingBytesPerTarget: number
  maxConcurrentTargets: number
}

/** 计划中的单个分块。 */
export interface ChunkPlan {
  seq: number
  offset: number
  byteLength: number
}

/**
 * 校验对端声明的协议版本。
 * @param value 对端声明值
 */
export function checkProtocolVersion(value: unknown): VersionCheck {
  if (value === PROTOCOL_VERSION) return { ok: true, version: PROTOCOL_VERSION }
  return { ok: false, code: 'version-mismatch', expected: PROTOCOL_VERSION, received: value }
}

/**
 * 判断一批文件是否满足批次级限额。
 * 只做纯计算，不读取任何外部状态，便于两端与测试复用同一判据。
 */
export function checkBatchLimits(
  fileCount: number,
  totalBytes: number,
  limits: Pick<Limits, 'maxFilesPerBatch' | 'maxBatchBytes'> = DEFAULT_LIMITS
): LimitCheck {
  if (!Number.isInteger(fileCount) || fileCount < 0) {
    return { ok: false, code: 'invalid-file-count', limit: 0, actual: fileCount }
  }
  if (fileCount > limits.maxFilesPerBatch) {
    return { ok: false, code: 'limit-batch-files', limit: limits.maxFilesPerBatch, actual: fileCount }
  }
  if (totalBytes > limits.maxBatchBytes) {
    return { ok: false, code: 'limit-batch-bytes', limit: limits.maxBatchBytes, actual: totalBytes }
  }
  return { ok: true }
}

/**
 * 判断单个文件是否满足文件级限额。
 */
export function checkFileLimits(
  byteLength: number,
  limits: Pick<Limits, 'maxFileBytes'> = DEFAULT_LIMITS
): LimitCheck {
  if (!Number.isInteger(byteLength) || byteLength < 0) {
    return { ok: false, code: 'invalid-file-size', limit: 0, actual: byteLength }
  }
  if (byteLength > limits.maxFileBytes) {
    return { ok: false, code: 'limit-file-bytes', limit: limits.maxFileBytes, actual: byteLength }
  }
  return { ok: true }
}

/**
 * 按固定块大小切分总字节数，返回每块的 seq 与原始字节长度。
 * seq 从每个文件的 0 开始，offset 连续；尾块允许小于块大小。
 */
export function planChunks(totalBytes: number, chunkBytes: number = CHUNK_BYTES): ChunkPlan[] {
  if (!Number.isInteger(totalBytes) || totalBytes < 0) {
    throw new TypeError('totalBytes 必须是非负整数')
  }
  if (!Number.isInteger(chunkBytes) || chunkBytes <= 0) {
    throw new TypeError('chunkBytes 必须是正整数')
  }
  const chunks: ChunkPlan[] = []
  let offset = 0
  let seq = 0
  while (offset < totalBytes) {
    const byteLength = Math.min(chunkBytes, totalBytes - offset)
    chunks.push({ seq, offset, byteLength })
    offset += byteLength
    seq += 1
  }
  return chunks
}
