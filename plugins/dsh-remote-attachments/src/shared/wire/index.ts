/**
 * 线协议 v1 公共入口（D10）。
 *
 * 冻结常量**一律从 `../protocol.js` 再导出**，不在此重新定义：
 * 任何数值改动都必须发生在 protocol.ts + schema.json + expected.json 三处同步，
 * 并由 `scripts/wire-contract-gates.mjs` 与 C# 侧测试共同守住。
 */

export {
  CHUNK_BYTES,
  DEFAULT_LIMITS,
  ERROR_CODES,
  ERROR_STAGES,
  ID_PATTERN,
  MAX_ATTACHMENT_IDS,
  MAX_BASE64_CHARS,
  MAX_CHUNK_OFFSET,
  MAX_CHUNKS_IN_FLIGHT,
  MAX_CHUNKS_PER_FILE,
  MAX_EPOCH,
  MAX_ID_CHARS,
  MAX_MESSAGE_BYTES,
  MAX_MIME_CHARS,
  MAX_NAME_CHARS,
  MAX_SEQ,
  MAX_SUBMITTED_ITEMS,
  MESSAGE_TYPES,
  PROTOCOL_VERSION,
  REPLAY_CACHE_CAPACITY,
  REPLAY_CACHE_LIFETIME_MS,
  RESULT_STATUSES,
  SHA256_PATTERN,
  WIRE_ERROR_CODES,
  WIRE_FEATURES,
  WIRE_TIMEOUTS,
  checkBatchLimits,
  checkFileLimits,
  checkProtocolVersion,
  planChunks
} from '../protocol.js'

export { BASE64_PATTERN, base64Decode, base64Encode, isCanonicalBase64 } from './base64.js'
export {
  WIRE_MESSAGE_TYPES,
  canonicalEquals,
  canonicalJsonString,
  decodeMessage,
  encodeMessage,
  toCanonical,
  utf8ByteLength
} from './messages.js'
export type {
  AckMessage,
  BatchBeginMessage,
  BatchEndMessage,
  BatchEndResultItem,
  CancelMessage,
  CanonicalMessage,
  CapabilitiesMessage,
  ChunkMessage,
  ContextMessage,
  DecodeResult,
  FileBeginMessage,
  FileEndMessage,
  HelloMessage,
  ImportResultMessage,
  ResultStatus,
  WireErrorCode,
  WireLimits,
  WireMessage,
  WireMessageType
} from './messages.js'
export {
  PureJsSha256,
  createSha256,
  sha256Hex,
  sha256HexSync,
  toHex
} from './sha256.js'
export type { Sha256Backend, Sha256Hasher } from './sha256.js'
export { ReplayCache, makeReplayEntry, replayKey } from './replay-cache.js'
export type {
  ReplayCacheOptions,
  ReplayEntry,
  ReplayEntryKind,
  ReplayEntryState,
  ReplaySummary
} from './replay-cache.js'
export { WireSession } from './session.js'
export type {
  ApplyAccepted,
  ApplyRejected,
  ApplyResult,
  DraftState,
  TimeoutReport,
  TransportState,
  UploadState,
  WireFileRecord,
  WireSessionOptions
} from './session.js'
