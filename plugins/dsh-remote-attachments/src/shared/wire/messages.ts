/**
 * 线协议 v1 消息 codec（D10 冻结面，TS 侧生产实现）。
 *
 * 契约真值：`schemas/remote-attachments/v1/schema.json` 与
 * `schemas/remote-attachments/v1/expected.json`。本模块不重新定义常量，
 * 一律从 `../protocol.js` 复用，保证与 schema、C# 侧、语料三方同源。
 *
 * 判定顺序（与 README §7、C# 实现完全一致，否则同一恶意样本会得到不同的码）：
 *   malformed-json → message-too-large → v → type → 字段名扫描 → 逐字段 →
 *   （状态机部分见 session.ts）
 *
 * `decodeMessage` 是纯函数、同步的：canonical 投影里 chunk 的载荷摘要走
 * 纯 JS 同步摘要（两条后端实现已被向量测试证明等价）；真正的完整性校验在
 * session 的 file-end 上，走 `createSha256()`（有 Web Crypto 就用 Web Crypto）。
 */

import {
  ERROR_CODES,
  ERROR_STAGES,
  ID_PATTERN,
  MAX_ATTACHMENT_IDS,
  MAX_BASE64_CHARS,
  MAX_CHUNK_OFFSET,
  MAX_EPOCH,
  MAX_ID_CHARS,
  MAX_MESSAGE_BYTES,
  MAX_MIME_CHARS,
  MAX_NAME_CHARS,
  MAX_SEQ,
  MAX_SUBMITTED_ITEMS,
  MESSAGE_TYPES,
  RESULT_STATUSES,
  PROTOCOL_VERSION,
  SHA256_PATTERN,
  CHUNK_BYTES,
  DEFAULT_LIMITS,
  WIRE_FEATURES
} from '../protocol.js'
import { BASE64_PATTERN, base64Decode, base64Encode, isCanonicalBase64 } from './base64.js'
import { sha256HexSync } from './sha256.js'

/** 消息类型联合。运行时以 `MESSAGE_TYPES` 为准（单元测试断言两者集合相等）。 */
export type WireMessageType =
  | 'hello'
  | 'capabilities'
  | 'context'
  | 'batch-begin'
  | 'file-begin'
  | 'chunk'
  | 'ack'
  | 'file-end'
  | 'import-result'
  | 'batch-end'
  | 'cancel'

/** `protocol.ts` 的 `MESSAGE_TYPES` 的类型化视图；集合相等由测试守卫。 */
export const WIRE_MESSAGE_TYPES = MESSAGE_TYPES as readonly WireMessageType[]

/** 单文件结果状态（没有 upload ready）。 */
export type ResultStatus = (typeof RESULT_STATUSES)[number]

/** 限额形状（与 protocol.ts 的 Limits 一致）。 */
export interface WireLimits {
  maxFileBytes: number
  maxFilesPerBatch: number
  maxBatchBytes: number
  maxScreenshotPixels: number
  maxStagingBytesPerTarget: number
  maxConcurrentTargets: number
}

export interface HelloMessage {
  readonly v: 1
  readonly type: 'hello'
  readonly clientBuild: string
}

export interface CapabilitiesMessage {
  readonly v: 1
  readonly type: 'capabilities'
  readonly features: readonly string[]
  readonly limits: WireLimits
}

export interface ContextMessage {
  readonly v: 1
  readonly type: 'context'
  readonly sessionId: string
  readonly targetId: string
  readonly documentEpoch: number
  readonly composerEpoch: number
  readonly composerScope: string
}

export interface BatchBeginMessage {
  readonly v: 1
  readonly type: 'batch-begin'
  readonly sessionId?: string
  readonly batchId: string
  readonly targetId: string
  readonly documentEpoch: number
  readonly composerEpoch: number
  readonly fileCount: number
  readonly totalBytes: number
}

export interface FileBeginMessage {
  readonly v: 1
  readonly type: 'file-begin'
  readonly sessionId?: string
  readonly batchId: string
  readonly fileId: string
  readonly name: string
  readonly byteLength: number
  readonly mime: string
  readonly sha256?: string
}

export interface ChunkMessage {
  readonly v: 1
  readonly type: 'chunk'
  readonly sessionId?: string
  readonly batchId: string
  readonly fileId: string
  readonly seq: number
  readonly offset: number
  readonly byteLength: number
  readonly dataBase64: string
}

export interface AckMessage {
  readonly v: 1
  readonly type: 'ack'
  readonly sessionId?: string
  readonly batchId: string
  readonly fileId: string
  readonly seq: number
  readonly offset: number
  readonly byteLength: number
  readonly bufferedBytes: number
  readonly inFlight: number
}

export interface FileEndMessage {
  readonly v: 1
  readonly type: 'file-end'
  readonly sessionId?: string
  readonly batchId: string
  readonly fileId: string
  readonly totalBytes: number
  readonly sha256: string
  readonly submittedItems?: number
}

export interface ImportResultMessage {
  readonly v: 1
  readonly type: 'import-result'
  readonly sessionId?: string
  readonly batchId: string
  readonly fileId: string
  readonly status: ResultStatus
  readonly attachmentIds: readonly string[]
  readonly code?: string
}

export interface BatchEndResultItem {
  readonly fileId: string
  readonly status: ResultStatus
  readonly attachmentIds?: readonly string[]
  readonly code?: string
}

export interface BatchEndMessage {
  readonly v: 1
  readonly type: 'batch-end'
  readonly sessionId?: string
  readonly batchId: string
  readonly status: ResultStatus
  readonly results: readonly BatchEndResultItem[]
}

export interface CancelMessage {
  readonly v: 1
  readonly type: 'cancel'
  readonly sessionId?: string
  readonly batchId: string
  readonly reason?: string
  readonly stage?: string
}

/** 解码后的消息联合。 */
export type WireMessage =
  | HelloMessage
  | CapabilitiesMessage
  | ContextMessage
  | BatchBeginMessage
  | FileBeginMessage
  | ChunkMessage
  | AckMessage
  | FileEndMessage
  | ImportResultMessage
  | BatchEndMessage
  | CancelMessage

/** canonical 投影：chunk 的 `dataBase64` 换成 `data:{bytes,sha256}`。 */
export type CanonicalMessage = Record<string, unknown>

/** 拒绝码：与 `WIRE_ERROR_CODES` 同源。 */
export type WireErrorCode = (typeof import('../protocol.js').WIRE_ERROR_CODES)[number]

/** 解码结果。 */
export type DecodeResult =
  | { readonly ok: true; readonly message: WireMessage; readonly canonical: CanonicalMessage }
  | { readonly ok: false; readonly code: WireErrorCode; readonly detail: string; readonly path: string }

const fail = (code: WireErrorCode, detail: string, path: string): DecodeResult => ({ ok: false, code, detail, path })

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

type FieldKind =
  | 'version'
  | 'int'
  | 'id'
  | 'text'
  | 'sha256'
  | 'base64'
  | 'enum'
  | 'enumArray'
  | 'idArray'
  | 'limits'
  | 'resultArray'

interface FieldSpec {
  readonly name: string
  readonly kind: FieldKind
  readonly required: boolean
  readonly min?: number
  readonly max?: number
  readonly minChars?: number
  readonly maxChars?: number
  readonly pattern?: RegExp
  readonly values?: readonly string[]
  readonly minItems?: number
  readonly maxItems?: number
}

const ID_FIELD = (name: string, required: boolean): FieldSpec => ({
  name,
  kind: 'id',
  required,
  minChars: 1,
  maxChars: MAX_ID_CHARS,
  pattern: ID_PATTERN
})

const EPOCH_FIELD = (name: string): FieldSpec => ({ name, kind: 'int', required: true, min: 0, max: MAX_EPOCH })

/** 冻结字段顺序 = 检查顺序 = canonical 键顺序。 */
const MESSAGE_FIELDS: Record<WireMessageType, readonly FieldSpec[]> = {
  hello: [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    { name: 'clientBuild', kind: 'text', required: true, minChars: 1, maxChars: 64, pattern: /^[A-Za-z0-9._/+:-]+$/ }
  ],
  capabilities: [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    { name: 'features', kind: 'enumArray', required: true, values: WIRE_FEATURES, maxItems: WIRE_FEATURES.length },
    { name: 'limits', kind: 'limits', required: true }
  ],
  context: [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', true),
    ID_FIELD('targetId', true),
    EPOCH_FIELD('documentEpoch'),
    EPOCH_FIELD('composerEpoch'),
    ID_FIELD('composerScope', true)
  ],
  'batch-begin': [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    ID_FIELD('targetId', true),
    EPOCH_FIELD('documentEpoch'),
    EPOCH_FIELD('composerEpoch'),
    { name: 'fileCount', kind: 'int', required: true, min: 1, max: DEFAULT_LIMITS.maxFilesPerBatch },
    { name: 'totalBytes', kind: 'int', required: true, min: 0, max: DEFAULT_LIMITS.maxBatchBytes }
  ],
  'file-begin': [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    ID_FIELD('fileId', true),
    {
      name: 'name',
      kind: 'text',
      required: true,
      minChars: 1,
      maxChars: MAX_NAME_CHARS,
      pattern: /^[^/\\\u0000]+$/
    },
    { name: 'byteLength', kind: 'int', required: true, min: 0, max: DEFAULT_LIMITS.maxFileBytes },
    {
      name: 'mime',
      kind: 'text',
      required: true,
      minChars: 1,
      maxChars: MAX_MIME_CHARS,
      pattern: /^[A-Za-z0-9.+-]+\/[A-Za-z0-9.+-]+$/
    },
    { name: 'sha256', kind: 'sha256', required: false }
  ],
  chunk: [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    ID_FIELD('fileId', true),
    { name: 'seq', kind: 'int', required: true, min: 0, max: MAX_SEQ },
    { name: 'offset', kind: 'int', required: true, min: 0, max: MAX_CHUNK_OFFSET },
    { name: 'byteLength', kind: 'int', required: true, min: 1, max: CHUNK_BYTES },
    { name: 'dataBase64', kind: 'base64', required: true, minChars: 4, maxChars: MAX_BASE64_CHARS, pattern: BASE64_PATTERN }
  ],
  ack: [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    ID_FIELD('fileId', true),
    { name: 'seq', kind: 'int', required: true, min: 0, max: MAX_SEQ },
    { name: 'offset', kind: 'int', required: true, min: 0, max: MAX_CHUNK_OFFSET },
    { name: 'byteLength', kind: 'int', required: true, min: 1, max: CHUNK_BYTES },
    {
      name: 'bufferedBytes',
      kind: 'int',
      required: true,
      min: 0,
      max: DEFAULT_LIMITS.maxStagingBytesPerTarget
    },
    { name: 'inFlight', kind: 'int', required: true, min: 0, max: 2 }
  ],
  'file-end': [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    ID_FIELD('fileId', true),
    { name: 'totalBytes', kind: 'int', required: true, min: 0, max: DEFAULT_LIMITS.maxFileBytes },
    { name: 'sha256', kind: 'sha256', required: true },
    { name: 'submittedItems', kind: 'int', required: false, min: 1, max: MAX_SUBMITTED_ITEMS }
  ],
  'import-result': [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    ID_FIELD('fileId', true),
    { name: 'status', kind: 'enum', required: true, values: RESULT_STATUSES },
    { name: 'attachmentIds', kind: 'idArray', required: true, maxItems: MAX_ATTACHMENT_IDS },
    { name: 'code', kind: 'enum', required: false, values: ERROR_CODES }
  ],
  'batch-end': [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    { name: 'status', kind: 'enum', required: true, values: RESULT_STATUSES },
    {
      name: 'results',
      kind: 'resultArray',
      required: true,
      minItems: 1,
      maxItems: DEFAULT_LIMITS.maxFilesPerBatch
    }
  ],
  cancel: [
    { name: 'v', kind: 'version', required: true },
    { name: 'type', kind: 'enum', required: true, values: WIRE_MESSAGE_TYPES },
    ID_FIELD('sessionId', false),
    ID_FIELD('batchId', true),
    { name: 'reason', kind: 'enum', required: false, values: ERROR_CODES },
    { name: 'stage', kind: 'enum', required: false, values: ERROR_STAGES }
  ]
}

const LIMIT_FIELDS: readonly FieldSpec[] = [
  { name: 'maxFileBytes', kind: 'int', required: true, min: 0, max: DEFAULT_LIMITS.maxFileBytes },
  { name: 'maxFilesPerBatch', kind: 'int', required: true, min: 1, max: DEFAULT_LIMITS.maxFilesPerBatch },
  { name: 'maxBatchBytes', kind: 'int', required: true, min: 0, max: DEFAULT_LIMITS.maxBatchBytes },
  { name: 'maxScreenshotPixels', kind: 'int', required: true, min: 0, max: DEFAULT_LIMITS.maxScreenshotPixels },
  {
    name: 'maxStagingBytesPerTarget',
    kind: 'int',
    required: true,
    min: 0,
    max: DEFAULT_LIMITS.maxStagingBytesPerTarget
  },
  { name: 'maxConcurrentTargets', kind: 'int', required: true, min: 1, max: DEFAULT_LIMITS.maxConcurrentTargets }
]

const RESULT_ITEM_FIELDS: readonly FieldSpec[] = [
  ID_FIELD('fileId', true),
  { name: 'status', kind: 'enum', required: true, values: RESULT_STATUSES },
  { name: 'attachmentIds', kind: 'idArray', required: false, maxItems: MAX_ATTACHMENT_IDS },
  { name: 'code', kind: 'enum', required: false, values: ERROR_CODES }
]

/** UTF-8 字节数（不依赖 Node Buffer，client 半区同样可用）。 */
export function utf8ByteLength(text: string): number {
  return new TextEncoder().encode(text).length
}

/** 递归按键名排序后序列化，供两端做结构比较。 */
export function canonicalJsonString(value: unknown): string {
  return JSON.stringify(sortKeys(value))
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys)
  if (isRecord(value)) {
    const out: Record<string, unknown> = {}
    for (const key of Object.keys(value).sort()) out[key] = sortKeys(value[key])
    return out
  }
  return value
}

interface FieldOutcome {
  readonly ok: boolean
  readonly value?: unknown
  readonly code?: WireErrorCode
  readonly detail?: string
  readonly path?: string
}

function checkField(spec: FieldSpec, value: unknown, path: string): FieldOutcome {
  switch (spec.kind) {
    case 'version': {
      if (typeof value !== 'number' || !Number.isInteger(value)) {
        return { ok: false, code: 'invalid-field-type', detail: 'v 必须是整数', path }
      }
      if (value !== PROTOCOL_VERSION) {
        return { ok: false, code: 'version-mismatch', detail: `协议版本必须为 ${PROTOCOL_VERSION}`, path }
      }
      return { ok: true, value }
    }
    case 'int': {
      if (typeof value !== 'number' || !Number.isInteger(value)) {
        return { ok: false, code: 'invalid-field-type', detail: '必须是整数', path }
      }
      if (value < 0) return { ok: false, code: 'negative-integer', detail: '不允许负数', path }
      const min = spec.min ?? 0
      const max = spec.max ?? Number.MAX_SAFE_INTEGER
      if (value < min || value > max) {
        return { ok: false, code: 'integer-out-of-range', detail: `必须在 ${min}..${max}`, path }
      }
      return { ok: true, value }
    }
    case 'id':
    case 'text': {
      if (typeof value !== 'string') return { ok: false, code: 'invalid-field-type', detail: '必须是字符串', path }
      const maxChars = spec.maxChars ?? Number.MAX_SAFE_INTEGER
      if (value.length > maxChars) {
        return { ok: false, code: 'field-too-large', detail: `长度超过 ${maxChars}`, path }
      }
      if (value.length < (spec.minChars ?? 0)) {
        return { ok: false, code: 'invalid-field-value', detail: `长度不足 ${spec.minChars ?? 0}`, path }
      }
      if (spec.pattern && !spec.pattern.test(value)) {
        return { ok: false, code: 'invalid-field-value', detail: '不符合冻结模式（禁止路径分隔符/空白/控制字符）', path }
      }
      return { ok: true, value }
    }
    case 'sha256': {
      if (typeof value !== 'string') return { ok: false, code: 'invalid-field-type', detail: '必须是字符串', path }
      if (!SHA256_PATTERN.test(value)) {
        return { ok: false, code: 'invalid-field-value', detail: '必须是 64 位小写十六进制', path }
      }
      return { ok: true, value }
    }
    case 'base64': {
      if (typeof value !== 'string') return { ok: false, code: 'invalid-field-type', detail: '必须是字符串', path }
      if (value.length > (spec.maxChars ?? Number.MAX_SAFE_INTEGER)) {
        return { ok: false, code: 'field-too-large', detail: `Base64 超过 ${spec.maxChars ?? 0} 字符`, path }
      }
      if (value.length < (spec.minChars ?? 0) || !isCanonicalBase64(value)) {
        return { ok: false, code: 'invalid-field-value', detail: '不是规范 Base64', path }
      }
      return { ok: true, value }
    }
    case 'enum': {
      if (typeof value !== 'string') return { ok: false, code: 'invalid-field-type', detail: '必须是字符串', path }
      if (!(spec.values ?? []).includes(value)) {
        return { ok: false, code: 'unknown-enum-value', detail: `不在冻结枚举 ${(spec.values ?? []).join('|')}`, path }
      }
      return { ok: true, value }
    }
    case 'enumArray': {
      if (!Array.isArray(value)) return { ok: false, code: 'invalid-field-type', detail: '必须是数组', path }
      if (value.length > (spec.maxItems ?? Number.MAX_SAFE_INTEGER)) {
        return { ok: false, code: 'field-too-large', detail: `数组超过 ${spec.maxItems ?? 0} 项`, path }
      }
      const seen = new Set<string>()
      for (let i = 0; i < value.length; i += 1) {
        const item = value[i]
        if (typeof item !== 'string' || !(spec.values ?? []).includes(item)) {
          return { ok: false, code: 'unknown-enum-value', detail: '未知枚举成员', path: `${path}[${i}]` }
        }
        if (seen.has(item)) {
          return { ok: false, code: 'invalid-field-value', detail: '枚举成员重复', path: `${path}[${i}]` }
        }
        seen.add(item)
      }
      return { ok: true, value: [...value] }
    }
    case 'idArray': {
      if (!Array.isArray(value)) return { ok: false, code: 'invalid-field-type', detail: '必须是数组', path }
      if (value.length > (spec.maxItems ?? Number.MAX_SAFE_INTEGER)) {
        return { ok: false, code: 'field-too-large', detail: `数组超过 ${spec.maxItems ?? 0} 项`, path }
      }
      const out: string[] = []
      for (let i = 0; i < value.length; i += 1) {
        const item = checkField({ ...ID_FIELD('item', true) }, value[i], `${path}[${i}]`)
        if (!item.ok) return item
        const id = String(item.value)
        if (out.includes(id)) {
          return { ok: false, code: 'invalid-field-value', detail: 'ID 重复', path: `${path}[${i}]` }
        }
        out.push(id)
      }
      return { ok: true, value: out }
    }
    case 'limits':
    case 'resultArray': {
      if (spec.kind === 'limits') {
        if (!isRecord(value)) return { ok: false, code: 'invalid-field-type', detail: '必须是对象', path }
        const keyOutcome = scanKeys(value, LIMIT_FIELDS, path)
        if (!keyOutcome.ok) return keyOutcome
        const limits: Record<string, number> = {}
        for (const field of LIMIT_FIELDS) {
          const raw = value[field.name]
          if (raw === undefined) return { ok: false, code: 'missing-field', detail: `缺少 ${field.name}`, path: `${path}.${field.name}` }
          const outcome = checkField(field, raw, `${path}.${field.name}`)
          if (!outcome.ok) return outcome
          limits[field.name] = outcome.value as number
        }
        return { ok: true, value: limits }
      }
      if (!Array.isArray(value)) return { ok: false, code: 'invalid-field-type', detail: '必须是数组', path }
      if (value.length < (spec.minItems ?? 0) || value.length > (spec.maxItems ?? Number.MAX_SAFE_INTEGER)) {
        return { ok: false, code: 'field-too-large', detail: `数组长度必须在 ${spec.minItems ?? 0}..${spec.maxItems ?? 0}`, path }
      }
      const items: Record<string, unknown>[] = []
      for (let i = 0; i < value.length; i += 1) {
        const raw = value[i]
        if (!isRecord(raw)) return { ok: false, code: 'invalid-field-type', detail: '数组项必须是对象', path: `${path}[${i}]` }
        const keyOutcome = scanKeys(raw, RESULT_ITEM_FIELDS, `${path}[${i}]`)
        if (!keyOutcome.ok) return keyOutcome
        const item: Record<string, unknown> = {}
        for (const field of RESULT_ITEM_FIELDS) {
          const fieldValue = raw[field.name]
          if (fieldValue === undefined) {
            if (field.required) return { ok: false, code: 'missing-field', detail: `缺少 ${field.name}`, path: `${path}[${i}].${field.name}` }
            continue
          }
          const outcome = checkField(field, fieldValue, `${path}[${i}].${field.name}`)
          if (!outcome.ok) return outcome
          item[field.name] = outcome.value
        }
        items.push(item)
      }
      return { ok: true, value: items }
    }
    default:
      return { ok: false, code: 'invalid-field-type', detail: '未支持字段类型', path }
  }
}

/** 字段名精确匹配；大小写变体与多余字段分开报告（按键名升序保证确定性）。 */
function scanKeys(
  value: Record<string, unknown>,
  fields: readonly FieldSpec[],
  path: string
): { ok: true } | { ok: false; code: WireErrorCode; detail: string; path: string } {
  const allowed = new Set(fields.map((field) => field.name))
  const lower = new Map<string, string>()
  for (const name of allowed) lower.set(name.toLowerCase(), name)
  for (const key of Object.keys(value).sort()) {
    if (allowed.has(key)) continue
    const prefix = path === '' ? '' : `${path}.`
    if (lower.has(key.toLowerCase())) {
      return { ok: false, code: 'field-name-invalid', detail: `字段名大小写变体：${key}`, path: `${prefix}${key}` }
    }
    return { ok: false, code: 'unknown-field', detail: `字段集固定，出现未知字段：${key}`, path: `${prefix}${key}` }
  }
  return { ok: true }
}

/**
 * 解码并校验一条消息（纯函数，不读取任何外部状态）。
 * @param text 报文 JSON 文本
 */
export function decodeMessage(text: string): DecodeResult {
  const byteLength = utf8ByteLength(text)
  if (byteLength > MAX_MESSAGE_BYTES) {
    return fail('message-too-large', `消息 ${byteLength} 字节 > ${MAX_MESSAGE_BYTES}`, '')
  }

  let raw: unknown
  try {
    raw = JSON.parse(text)
  } catch (error) {
    return fail('malformed-json', error instanceof Error ? error.message : 'JSON 解析失败', '')
  }
  if (!isRecord(raw)) return fail('invalid-field-type', '根节点必须是对象', '')

  const version = raw['v']
  if (version === undefined) return fail('missing-field', '缺少 v', 'v')
  const versionOutcome = checkField({ name: 'v', kind: 'version', required: true }, version, 'v')
  if (!versionOutcome.ok) {
    return fail(versionOutcome.code ?? 'invalid-field-type', versionOutcome.detail ?? '版本不合法', 'v')
  }

  const type = raw['type']
  if (type === undefined) return fail('missing-field', '缺少 type', 'type')
  if (typeof type !== 'string') return fail('invalid-field-type', 'type 必须是字符串', 'type')
  if (!WIRE_MESSAGE_TYPES.includes(type as WireMessageType)) {
    return fail('unknown-message-type', `未知消息类型：${type}`, 'type')
  }

  const wireType = type as WireMessageType
  const fields = MESSAGE_FIELDS[wireType]
  const keyOutcome = scanKeys(raw, fields, '')
  if (!keyOutcome.ok) return fail(keyOutcome.code, keyOutcome.detail, keyOutcome.path)

  const message: Record<string, unknown> = {}
  const canonical: Record<string, unknown> = {}
  for (const field of fields) {
    const value = raw[field.name]
    if (value === undefined) {
      if (field.required) return fail('missing-field', `缺少 ${field.name}`, field.name)
      continue
    }
    const outcome = checkField(field, value, field.name)
    if (!outcome.ok) return fail(outcome.code ?? 'invalid-field-type', outcome.detail ?? '字段不合法', outcome.path ?? field.name)
    message[field.name] = outcome.value
    canonical[field.name] = outcome.value
  }

  if (wireType === 'chunk') {
    let payload: Uint8Array
    try {
      payload = base64Decode(String(message['dataBase64']))
    } catch {
      return fail('invalid-field-value', 'Base64 解码失败', 'dataBase64')
    }
    const declared = message['byteLength'] as number
    if (payload.length !== declared) {
      return fail('size-mismatch', `声明 ${declared} 字节，载荷 ${payload.length} 字节`, 'byteLength')
    }
    delete canonical['dataBase64']
    canonical['data'] = { bytes: payload.length, sha256: sha256HexSync(payload) }
    message['dataBase64'] = base64Encode(payload)
  }

  return { ok: true, message: message as unknown as WireMessage, canonical }
}

/** 编码回 JSON 文本（字段顺序即冻结顺序）。 */
export function encodeMessage(message: WireMessage): string {
  return JSON.stringify(message)
}

/**
 * 计算 canonical 投影（对已解码消息同样可用）。
 * chunk 的载荷按规范 Base64 重新编码，未知/多余字段不会出现在结果里。
 */
export function toCanonical(message: WireMessage): CanonicalMessage {
  const canonical: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(message)) {
    if (key === 'dataBase64') continue
    canonical[key] = value
  }
  if (message.type === 'chunk') {
    const payload = base64Decode(message.dataBase64)
    canonical['data'] = { bytes: payload.length, sha256: sha256HexSync(payload) }
  }
  return canonical
}

/** 结构相等（按 canonical JSON 字符串比较），两端语义一致。 */
export function canonicalEquals(left: unknown, right: unknown): boolean {
  return canonicalJsonString(left) === canonicalJsonString(right)
}
