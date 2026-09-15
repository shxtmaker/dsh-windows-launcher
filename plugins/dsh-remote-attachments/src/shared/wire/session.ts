/**
 * 线协议 v1 状态机（D10 冻结面，TS 侧生产实现）。
 *
 * 负责 decode 之后的一切：身份绑定、批次准入、限额、逐文件 seq/offset 与在途窗口、
 * file-end 的累计字节与 SHA-256 校验、逐 fileId 的结果状态、重放/去重缓存、
 * 批次关闭后的迟到拒绝，以及**三种互不合并的状态**：
 *
 *   transport（接收缓冲）≠ draft（草稿 staged）≠ upload（上传归 Harness）
 *
 * 与 C# 侧 `DshLauncher.Core.Attachments.AttachmentSession` 的判定顺序逐条一致。
 * 这里 `apply` 是 async：完整性校验走 `createSha256()`，有 Web Crypto 就用它，
 * 没有才退回纯 JS 增量实现（HTTP 局域网不是 SecureContext）。
 */

import {
  CHUNK_BYTES,
  DEFAULT_LIMITS,
  MAX_CHUNKS_IN_FLIGHT,
  WIRE_TIMEOUTS
} from '../protocol.js'
import { base64Decode } from './base64.js'
import {
  canonicalJsonString,
  type AckMessage,
  type BatchBeginMessage,
  type BatchEndMessage,
  type CancelMessage,
  type ChunkMessage,
  type ContextMessage,
  type FileBeginMessage,
  type FileEndMessage,
  type ImportResultMessage,
  type WireErrorCode,
  type WireLimits,
  type WireMessage,
  type WireMessageType
} from './messages.js'
import { ReplayCache, makeReplayEntry, replayKey, type ReplayEntry } from './replay-cache.js'
import { createSha256, type Sha256Hasher } from './sha256.js'

/** 传输层状态：只由 chunk/ack 改变。 */
export type TransportState = 'idle' | 'buffering' | 'buffered'

/** 草稿状态：只由 import-result 改变。 */
export type DraftState = 'none' | 'staged' | 'failed' | 'partial'

/** 上传归属：线协议只承认"归 Harness 所有"，不存在 ready/uploaded。 */
export type UploadState = 'none' | 'harness-owned'

/** 单个 fileId 的对外记录。 */
export interface WireFileRecord {
  readonly fileId: string
  readonly name: string
  readonly byteLength: number
  readonly mime: string
  readonly declaredSha256: string | null
  readonly transport: TransportState
  readonly draft: DraftState
  readonly upload: UploadState
  readonly receivedBytes: number
  readonly ackedBytes: number
  readonly inFlight: number
  readonly ended: boolean
  readonly resolved: boolean
  readonly submittedItems: number
  readonly attachmentIds: readonly string[]
}

/** apply 的成功结果。 */
export interface ApplyAccepted {
  readonly ok: true
  readonly type: WireMessageType
  readonly batchId: string | null
  readonly fileId: string | null
  /** 是否判定为已完成操作的幂等重放。 */
  readonly duplicate: boolean
  /** 本次 apply 是否触发了一次草稿导入（重复到达必须为 false）。 */
  readonly importInvoked: boolean
  /** 批次当前是否已关闭。 */
  readonly closed: boolean
  readonly file: WireFileRecord | null
  readonly files: readonly WireFileRecord[]
}

/** apply 的拒绝结果。 */
export interface ApplyRejected {
  readonly ok: false
  readonly code: WireErrorCode
  readonly detail: string
  readonly path: string
}

export type ApplyResult = ApplyAccepted | ApplyRejected

/** 超时报告（由调用方按需驱动，不自行起定时器）。 */
export interface TimeoutReport {
  readonly stalledFileIds: readonly string[]
  readonly cancelledBatchId: string | null
}

export interface WireSessionOptions {
  /** 覆盖生效限额；不得超过 DEFAULT_LIMITS（由 capabilities 语义保证）。 */
  readonly limits?: Partial<WireLimits> | undefined
  /** 注入时钟（测试与证据脚本用）。 */
  readonly now?: (() => number) | undefined
  /** 注入 crypto（证明"没有 Web Crypto 时仍能校验"）。 */
  readonly crypto?: { subtle?: SubtleCrypto } | undefined
  readonly replayCapacity?: number | undefined
  readonly replayLifetimeMs?: number | undefined
}

interface SentChunk {
  readonly offset: number
  readonly byteLength: number
  acked: boolean
}

interface FileState {
  readonly fileId: string
  readonly name: string
  readonly byteLength: number
  readonly mime: string
  readonly declaredSha256: string | null
  readonly hasher: Sha256Hasher
  expectedSeq: number
  expectedOffset: number
  readonly sent: Map<number, SentChunk>
  receivedBytes: number
  ackedBytes: number
  ended: boolean
  resolved: boolean
  draft: DraftState
  upload: UploadState
  submittedItems: number
  attachmentIds: string[]
  endDigest: string | null
  resultDigest: string | null
  lastActivityMs: number
}

interface BatchState {
  readonly batchId: string
  readonly fileCount: number
  readonly totalBytes: number
  readonly targetId: string
  readonly documentEpoch: number
  readonly composerEpoch: number
  readonly sessionId: string
  readonly files: Map<string, FileState>
  activeFileId: string | null
  closed: boolean
  cancelled: boolean
  readonly startedAtMs: number
  lastActivityMs: number
}

interface Identity {
  readonly sessionId: string
  readonly targetId: string
  readonly documentEpoch: number
  readonly composerEpoch: number
  readonly composerScope: string
}

const reject = (code: WireErrorCode, detail: string, path = ''): ApplyRejected => ({ ok: false, code, detail, path })

/**
 * 一条 target 上的线协议会话：一次用户粘贴对应一个 batchId，
 * 批内每个文件独立 fileId、独立 seq 序列。
 */
export class WireSession {
  private readonly options: WireSessionOptions
  private readonly now: () => number
  private readonly cache: ReplayCache
  private limits: WireLimits
  private identity: Identity | null = null
  private identityExpired = false
  private batch: BatchState | null = null
  private lastHandshakeMs = 0

  constructor(options: WireSessionOptions = {}) {
    this.options = options
    this.now = options.now ?? (() => Date.now())
    this.limits = { ...DEFAULT_LIMITS, ...(options.limits ?? {}) }
    this.cache = new ReplayCache({
      capacity: options.replayCapacity,
      lifetimeMs: options.replayLifetimeMs,
      now: this.now
    })
  }

  /** 生效限额（capabilities 协商后的结果）。 */
  get effectiveLimits(): WireLimits {
    return { ...this.limits }
  }

  /** 当前身份；导航/关闭后为过期。 */
  get currentIdentity(): (Identity & { readonly expired: boolean }) | null {
    if (!this.identity) return null
    return { ...this.identity, expired: this.identityExpired }
  }

  get replayCacheSize(): number {
    return this.cache.size
  }

  get replayCachePinned(): number {
    return this.cache.pinnedCount
  }

  get replayCacheEvicted(): number {
    return this.cache.evictedCount
  }

  get replayCacheRefusedEvictions(): number {
    return this.cache.refusedEvictionCount
  }

  /** 当前批次是否已关闭。 */
  get batchClosed(): boolean {
    return this.batch?.closed ?? false
  }

  /** 当前批次 ID。 */
  get activeBatchId(): string | null {
    return this.batch?.batchId ?? null
  }

  /** 逐文件记录的只读快照。 */
  get files(): readonly WireFileRecord[] {
    if (!this.batch) return []
    return [...this.batch.files.values()].map((file) => this.recordOf(file))
  }

  /** 取单个文件记录。 */
  fileRecord(fileId: string): WireFileRecord | null {
    const file = this.batch?.files.get(fileId)
    return file ? this.recordOf(file) : null
  }

  /**
   * 导航/关闭：清空重放缓存、关闭当前批次、让当前身份过期。
   * 之后必须由新的 `context` 重新建立身份，迟到的 file-end 无法重建操作。
   */
  navigate(): void {
    this.cache.clear()
    if (this.batch) {
      this.batch.closed = true
      this.batch.cancelled = true
    }
    this.batch = null
    if (this.identity) this.identityExpired = true
  }

  /**
   * 超时判定（纯计算，不自行起定时器）。
   * 在途超过 ackMs、或 file-end 后等待 import-result 超过 fileEndMs 的文件算停滞；
   * 批次空闲超过 batchIdleMs 直接取消。停滞/超时都会取消批次并释放自有资源。
   */
  checkTimeouts(nowMs?: number): TimeoutReport {
    const now = nowMs ?? this.now()
    const batch = this.batch
    if (!batch || batch.closed || batch.cancelled) return { stalledFileIds: [], cancelledBatchId: null }

    const stalled: string[] = []
    for (const file of batch.files.values()) {
      if (file.resolved) continue
      const idle = now - file.lastActivityMs
      if (file.ended && idle > WIRE_TIMEOUTS.fileEndMs) stalled.push(file.fileId)
      else if (!file.ended && this.inFlight(file) > 0 && idle > WIRE_TIMEOUTS.ackMs) stalled.push(file.fileId)
    }

    const batchIdle = now - batch.lastActivityMs > WIRE_TIMEOUTS.batchIdleMs
    if (stalled.length === 0 && !batchIdle) return { stalledFileIds: [], cancelledBatchId: null }

    batch.cancelled = true
    for (const file of batch.files.values()) file.sent.clear()
    for (const file of batch.files.values()) {
      this.pinCacheEntries(file, batch, 'completed')
    }
    return { stalledFileIds: stalled, cancelledBatchId: batch.batchId }
  }

  /**
   * 应用一条已解码消息。判定顺序与 README §7 / C# 实现一致。
   */
  async apply(message: WireMessage): Promise<ApplyResult> {
    const now = this.now()
    switch (message.type) {
      case 'hello':
        this.lastHandshakeMs = now
        return this.accepted(message.type, null, null, false, false)
      case 'capabilities':
        this.limits = { ...message.limits }
        this.lastHandshakeMs = now
        return this.accepted(message.type, null, null, false, false)
      case 'context':
        this.bindContext(message, now)
        return this.accepted(message.type, null, null, false, false)
      default:
        break
    }

    // —— 会话绑定：没有有效 sessionId 不接收任何操作消息 ——
    if (message.sessionId === undefined) return reject('no-session', '操作消息必须携带 sessionId')
    if (!this.identity) return reject('no-session', '尚未建立 context，拒绝操作消息')
    if (this.identityExpired) return reject('context-changed', '身份已因导航/关闭过期，需重新 context')
    if (message.sessionId !== this.identity.sessionId) {
      return reject('context-changed', `sessionId 与已绑定上下文不一致（${message.sessionId}）`)
    }

    switch (message.type) {
      case 'batch-begin':
        return this.applyBatchBegin(message, now)
      case 'cancel':
        return this.applyCancel(message, now)
      case 'file-begin':
        return this.applyFileBegin(message, now)
      case 'chunk':
        return this.applyChunk(message, now)
      case 'ack':
        return this.applyAck(message, now)
      case 'file-end':
        return this.applyFileEnd(message, now)
      case 'import-result':
        return this.applyImportResult(message, now)
      case 'batch-end':
        return this.applyBatchEnd(message, now)
      default:
        return reject('unknown-message-type', '未处理的消息类型')
    }
  }

  // ————————————————————————————————————————————————————————————
  // 握手
  // ————————————————————————————————————————————————————————————

  private bindContext(message: ContextMessage, now: number): void {
    const next: Identity = {
      sessionId: message.sessionId,
      targetId: message.targetId,
      documentEpoch: message.documentEpoch,
      composerEpoch: message.composerEpoch,
      composerScope: message.composerScope
    }
    const changed =
      this.identity === null ||
      this.identity.sessionId !== next.sessionId ||
      this.identity.targetId !== next.targetId ||
      this.identity.composerEpoch !== next.composerEpoch ||
      this.identity.composerScope !== next.composerScope ||
      this.identity.documentEpoch !== next.documentEpoch

    if (changed) {
      // composer 身份变化即失效：旧批次与旧缓存都不得继续复用。
      this.cache.clear()
      if (this.batch) {
        this.batch.closed = true
        this.batch.cancelled = true
      }
      this.batch = null
    }
    this.identity = next
    this.identityExpired = false
    this.lastHandshakeMs = now
  }

  // ————————————————————————————————————————————————————————————
  // 批次
  // ————————————————————————————————————————————————————————————

  private applyBatchBegin(message: BatchBeginMessage, now: number): ApplyResult {
    const identity = this.identity
    if (!identity) return reject('no-session', '尚未建立 context')
    if (
      message.targetId !== identity.targetId ||
      message.documentEpoch !== identity.documentEpoch ||
      message.composerEpoch !== identity.composerEpoch
    ) {
      return reject('context-changed', '批次身份与当前上下文不一致（targetId/双 epoch）')
    }
    if (this.batch) {
      // 已取消的批次与已关闭的批次一样**不再占用会话**：cancel 的语义是"停止该操作"，
      // 若取消后仍挡住新批次，用户取消一次就再也贴不进来（D12 实测，R15 修复）。
      if (!this.batch.closed && !this.batch.cancelled) {
        return reject('batch-in-progress', '同一会话已有开放批次')
      }
      // 只有复用**同一个** batchId 才算重复操作；换了新 batchId 就应当被承认。
      if (this.batch.batchId === message.batchId) {
        return reject('duplicate-operation', '该 batchId 已关闭；重新导入必须生成新的 batchId')
      }
    }
    const existing = this.cache.get(
      replayKey(identity.documentEpoch, identity.composerEpoch, message.batchId, '')
    )
    if (existing) return reject('duplicate-operation', '该 batchId 已关闭；重新导入必须生成新的 batchId')

    if (message.fileCount > this.limits.maxFilesPerBatch) {
      return reject('limit-batch-files', `fileCount ${message.fileCount} > ${this.limits.maxFilesPerBatch}`)
    }
    if (message.totalBytes > this.limits.maxBatchBytes) {
      return reject('limit-batch-bytes', `totalBytes ${message.totalBytes} > ${this.limits.maxBatchBytes}`)
    }

    this.batch = {
      batchId: message.batchId,
      fileCount: message.fileCount,
      totalBytes: message.totalBytes,
      targetId: message.targetId,
      documentEpoch: message.documentEpoch,
      composerEpoch: message.composerEpoch,
      sessionId: identity.sessionId,
      files: new Map(),
      activeFileId: null,
      closed: false,
      cancelled: false,
      startedAtMs: now,
      lastActivityMs: now
    }
    this.cache.put(
      makeReplayEntry({
        kind: 'batch',
        documentEpoch: identity.documentEpoch,
        composerEpoch: identity.composerEpoch,
        batchId: message.batchId,
        fileId: '',
        payloadDigest: '',
        summary: { importInvoked: false, attachmentIds: [], draft: 'none', status: null },
        now,
        lifetimeMs: this.options.replayLifetimeMs
      })
    )
    return this.accepted(message.type, message.batchId, null, false, false)
  }

  private applyCancel(message: CancelMessage, now: number): ApplyResult {
    const batch = this.batch
    if (!batch || batch.batchId !== message.batchId) {
      const stale = this.closedBatchExists(message.batchId)
      return reject(
        stale ? 'batch-closed' : 'batch-not-open',
        stale ? '批次已关闭' : '未找到该 operation'
      )
    }
    const already = batch.cancelled
    batch.cancelled = true
    batch.lastActivityMs = now
    // 释放自有缓冲，但保留已 confirm 的草稿结果（部分成功必须可确认）。
    for (const file of batch.files.values()) file.sent.clear()
    for (const file of batch.files.values()) this.pinCacheEntries(file, batch, 'completed')
    return this.accepted(message.type, message.batchId, null, already, false)
  }

  private applyFileBegin(message: FileBeginMessage, now: number): ApplyResult {
    const guard = this.guardBatch(message.batchId)
    if ('code' in guard) return guard
    const { batch } = guard

    if (message.byteLength > this.limits.maxFileBytes) {
      return reject('limit-file-bytes', `byteLength ${message.byteLength} > ${this.limits.maxFileBytes}`)
    }
    if (batch.files.has(message.fileId)) {
      return reject('duplicate-operation', `fileId ${message.fileId} 已在本批次声明过`)
    }
    if (batch.files.size >= batch.fileCount) {
      return reject('limit-batch-files', `批内文件数已达 batch-begin 声明的 ${batch.fileCount}`)
    }
    if (batch.activeFileId !== null) {
      const active = batch.files.get(batch.activeFileId)
      if (active && !active.ended) {
        return reject('file-in-progress', `每目标同时只处理一个文件，当前活动文件是 ${active.fileId}`)
      }
    }

    batch.files.set(message.fileId, {
      fileId: message.fileId,
      name: message.name,
      byteLength: message.byteLength,
      mime: message.mime,
      declaredSha256: message.sha256 ?? null,
      hasher: createSha256(this.options.crypto),
      expectedSeq: 0,
      expectedOffset: 0,
      sent: new Map(),
      receivedBytes: 0,
      ackedBytes: 0,
      ended: false,
      resolved: false,
      draft: 'none',
      upload: 'none',
      submittedItems: 1,
      attachmentIds: [],
      endDigest: null,
      resultDigest: null,
      lastActivityMs: now
    })
    batch.activeFileId = message.fileId
    batch.lastActivityMs = now
    return this.accepted(message.type, message.batchId, message.fileId, false, false)
  }

  // ————————————————————————————————————————————————————————————
  // 传输
  // ————————————————————————————————————————————————————————————

  private applyChunk(message: ChunkMessage, now: number): ApplyResult {
    const guard = this.guardBatch(message.batchId)
    if ('code' in guard) return guard
    const { batch } = guard
    const file = batch.files.get(message.fileId)
    if (!file) return reject('file-id-mismatch', `fileId ${message.fileId} 不在本批次`)
    if (file.ended || file.resolved) {
      return reject('duplicate-operation', `fileId ${message.fileId} 的传输已结束，重复块不重新入缓冲`)
    }
    if (batch.activeFileId !== message.fileId) {
      return reject('file-id-mismatch', `当前活动文件是 ${batch.activeFileId ?? '（无）'}`)
    }
    if (this.inFlight(file) >= MAX_CHUNKS_IN_FLIGHT) {
      return reject('window-overflow', `在途块数已达 ${MAX_CHUNKS_IN_FLIGHT}`)
    }
    if (message.seq > file.expectedSeq) {
      return reject('sequence-gap', `期望 seq ${file.expectedSeq}，收到 ${message.seq}`)
    }
    if (message.seq < file.expectedSeq) {
      return reject('seq-overlap', `seq ${message.seq} 已接收过（期望 ${file.expectedSeq}）`)
    }
    if (message.offset > file.expectedOffset) {
      return reject('sequence-gap', `期望 offset ${file.expectedOffset}，收到 ${message.offset}`)
    }
    if (message.offset < file.expectedOffset) {
      return reject('seq-overlap', `offset ${message.offset} 回退（期望 ${file.expectedOffset}）`)
    }
    if (file.receivedBytes + message.byteLength > file.byteLength) {
      return reject(
        'size-mismatch',
        `块超出文件声明长度：${file.receivedBytes} + ${message.byteLength} > ${file.byteLength}`
      )
    }

    const payload = base64Decode(message.dataBase64)
    if (payload.length !== message.byteLength) {
      return reject('size-mismatch', `载荷 ${payload.length} 字节 ≠ 声明 ${message.byteLength}`)
    }
    file.hasher.update(payload)
    file.sent.set(message.seq, { offset: message.offset, byteLength: message.byteLength, acked: false })
    file.expectedSeq += 1
    file.expectedOffset += message.byteLength
    file.receivedBytes += message.byteLength
    file.lastActivityMs = now
    batch.lastActivityMs = now
    return this.accepted(message.type, message.batchId, message.fileId, false, false)
  }

  private applyAck(message: AckMessage, now: number): ApplyResult {
    const guard = this.guardBatch(message.batchId)
    if ('code' in guard) return guard
    const { batch } = guard
    const file = batch.files.get(message.fileId)
    if (!file) return reject('file-id-mismatch', `fileId ${message.fileId} 不在本批次`)
    if (message.bufferedBytes > this.limits.maxStagingBytesPerTarget) {
      return reject(
        'limit-staging-bytes',
        `bufferedBytes ${message.bufferedBytes} > ${this.limits.maxStagingBytesPerTarget}`
      )
    }
    if (message.seq > file.expectedSeq - 1) {
      return reject('sequence-gap', `ack 指向从未发送的块 seq ${message.seq}`)
    }
    const sent = file.sent.get(message.seq)
    if (!sent) return reject('sequence-gap', `ack 指向从未发送的块 seq ${message.seq}`)
    if (sent.acked) {
      // 重复 ack 是幂等重放：不改变已确认字节。
      return this.accepted(message.type, message.batchId, message.fileId, true, false)
    }
    if (sent.offset !== message.offset || sent.byteLength !== message.byteLength) {
      return reject('size-mismatch', `ack 的 offset/byteLength 与该块不符`)
    }
    sent.acked = true
    file.ackedBytes += sent.byteLength
    file.lastActivityMs = now
    batch.lastActivityMs = now
    return this.accepted(message.type, message.batchId, message.fileId, false, false)
  }

  // ————————————————————————————————————————————————————————————
  // 结束与结果
  // ————————————————————————————————————————————————————————————

  private async applyFileEnd(message: FileEndMessage, now: number): Promise<ApplyResult> {
    const guard = this.guardBatch(message.batchId)
    if ('code' in guard) return guard
    const { batch } = guard
    const file = batch.files.get(message.fileId)
    if (!file) return reject('file-id-mismatch', `fileId ${message.fileId} 不在本批次`)

    const incomingDigest = canonicalJsonString({ totalBytes: message.totalBytes, sha256: message.sha256 })

    if (file.ended) {
      // 重复 file-end 绝不产生第二次导入：内容一致即幂等，冲突则拒绝。
      if (file.endDigest === incomingDigest) {
        return this.accepted(message.type, message.batchId, message.fileId, true, false)
      }
      return reject('duplicate-file-end', `fileId ${message.fileId} 已结束且本次内容不同`)
    }
    if (batch.activeFileId !== message.fileId) {
      return reject('file-id-mismatch', `当前活动文件是 ${batch.activeFileId ?? '（无）'}`)
    }
    if (message.totalBytes !== file.byteLength) {
      return reject(
        'size-mismatch',
        `file-end 声明 ${message.totalBytes} 字节 ≠ file-begin 的 ${file.byteLength}`
      )
    }
    if (file.receivedBytes !== message.totalBytes) {
      return reject('size-mismatch', `已接收 ${file.receivedBytes} 字节 ≠ file-end 的 ${message.totalBytes}`)
    }
    if (file.declaredSha256 !== null && file.declaredSha256 !== message.sha256) {
      return reject('hash-mismatch', 'file-end 哈希与 file-begin 声明的哈希不符')
    }
    const digest = await file.hasher.digestHex()
    if (digest !== message.sha256) {
      return reject('hash-mismatch', `实际 SHA-256 ${digest} ≠ 声明 ${message.sha256}`)
    }

    file.ended = true
    file.submittedItems = message.submittedItems ?? 1
    file.endDigest = incomingDigest
    file.lastActivityMs = now
    batch.lastActivityMs = now
    // 活动操作被钉住：容量淘汰不得让它被重新执行。
    this.cache.put(
      makeReplayEntry({
        kind: 'file',
        documentEpoch: batch.documentEpoch,
        composerEpoch: batch.composerEpoch,
        batchId: batch.batchId,
        fileId: file.fileId,
        payloadDigest: incomingDigest,
        summary: { importInvoked: true, attachmentIds: [], draft: 'none', status: null },
        now,
        lifetimeMs: this.options.replayLifetimeMs
      })
    )
    // 校验干净：允许构造 File 并调用一次草稿导入。
    return this.accepted(message.type, message.batchId, message.fileId, false, true)
  }

  private applyImportResult(message: ImportResultMessage, now: number): ApplyResult {
    const guard = this.guardBatch(message.batchId)
    if ('code' in guard) return guard
    const { batch } = guard
    const file = batch.files.get(message.fileId)
    if (!file) return reject('file-id-mismatch', `fileId ${message.fileId} 不在本批次`)
    if (!file.ended) return reject('file-not-ended', `fileId ${message.fileId} 尚未 file-end`)

    const incomingDigest = canonicalJsonString({
      status: message.status,
      attachmentIds: [...message.attachmentIds],
      code: message.code ?? null
    })

    if (file.resolved) {
      if (file.resultDigest === incomingDigest) {
        return this.accepted(message.type, message.batchId, message.fileId, true, false)
      }
      return reject('duplicate-operation', `fileId ${message.fileId} 的结果已确定，重复内容冲突`)
    }

    const ids = [...message.attachmentIds]
    const expectedIds = message.status === 'staged' ? file.submittedItems : message.status === 'failed' ? 0 : null
    if (expectedIds !== null && ids.length !== expectedIds) {
      return reject(
        'import-id-count-mismatch',
        `status=${message.status} 期望 ${expectedIds} 个新增 ID，实际 ${ids.length}`
      )
    }
    if (message.status === 'partial' && ids.length === 0) {
      return reject('import-id-count-mismatch', 'partial 必须至少包含 1 个已确认的新增 ID')
    }
    if ((message.status === 'failed' || message.status === 'partial') && message.code === undefined) {
      return reject('missing-field', `status=${message.status} 必须给出 code`)
    }
    const known = new Set<string>()
    for (const other of batch.files.values()) for (const id of other.attachmentIds) known.add(id)
    for (const id of ids) {
      if (known.has(id)) return reject('duplicate-operation', `附件 ID ${id} 已在本批次出现过`)
    }

    // ResultStatus（staged/failed/partial）是 DraftState 的子集，这里的赋值是精确的。
    file.draft = message.status as DraftState
    // staged 只代表草稿接收；上传与发送仍归 Harness，线协议里没有 ready。
    file.upload = message.status === 'staged' ? 'harness-owned' : 'none'
    file.attachmentIds = ids
    file.resolved = true
    file.resultDigest = incomingDigest
    file.lastActivityMs = now
    batch.lastActivityMs = now
    this.cache.complete(
      replayKey(batch.documentEpoch, batch.composerEpoch, batch.batchId, file.fileId),
      {
        importInvoked: true,
        attachmentIds: ids,
        draft: message.status,
        status: message.status
      },
      incomingDigest
    )
    return this.accepted(message.type, message.batchId, message.fileId, false, false)
  }

  private applyBatchEnd(message: BatchEndMessage, now: number): ApplyResult {
    const batch = this.batch

    if (!batch || batch.batchId !== message.batchId) {
      // 批次关闭后只再接受重复的 batch-end（幂等/冲突）与 cancel。
      const stale = this.cache.get(
        replayKey(
          this.identity?.documentEpoch ?? -1,
          this.identity?.composerEpoch ?? -1,
          message.batchId,
          ''
        )
      )
      if (!stale) return reject('batch-not-open', '未找到该 operation')
      const incomingDigest = canonicalJsonString({ status: message.status, results: message.results })
      if (stale.payloadDigest === incomingDigest) {
        return this.accepted(message.type, message.batchId, null, true, false)
      }
      return reject('duplicate-operation', '批次已关闭且本次 batch-end 内容冲突')
    }
    if (batch.cancelled) return reject('cancelled', '批次已取消，迟到结果被拒绝')

    const incomingDigest = canonicalJsonString({ status: message.status, results: message.results })
    if (batch.closed) {
      const stale = this.cache.get(
        replayKey(batch.documentEpoch, batch.composerEpoch, batch.batchId, '')
      )
      if (stale && stale.payloadDigest === incomingDigest) {
        return this.accepted(message.type, message.batchId, null, true, false)
      }
      return reject('duplicate-operation', '批次已关闭且本次 batch-end 内容冲突')
    }

    for (const file of batch.files.values()) {
      if (!file.resolved) {
        return reject('result-incomplete', `fileId ${file.fileId} 尚无确定结果，不能关闭批次`)
      }
    }
    const declared = new Set(message.results.map((item) => item.fileId))
    if (declared.size !== message.results.length) {
      return reject('invalid-field-value', 'batch-end 的 fileId 重复', 'results')
    }
    if (declared.size !== batch.files.size) {
      return reject('result-incomplete', 'batch-end 必须覆盖批内全部 fileId')
    }
    for (const item of message.results) {
      const file = batch.files.get(item.fileId)
      if (!file) return reject('file-id-mismatch', `fileId ${item.fileId} 不在本批次`, 'results')
      const ids = item.attachmentIds ?? []
      if (item.status !== file.draft) {
        return reject(
          'result-conflict',
          `fileId ${item.fileId} 的状态 ${item.status} 与记录的 ${file.draft} 冲突`,
          'results'
        )
      }
      if (canonicalJsonString([...ids]) !== canonicalJsonString([...file.attachmentIds])) {
        return reject('result-conflict', `fileId ${item.fileId} 的 attachmentIds 与记录冲突`, 'results')
      }
    }

    batch.closed = true
    batch.lastActivityMs = now
    for (const file of batch.files.values()) this.pinCacheEntries(file, batch, 'completed')
    this.cache.complete(
      replayKey(batch.documentEpoch, batch.composerEpoch, batch.batchId, ''),
      { importInvoked: false, attachmentIds: [], draft: message.status, status: message.status },
      incomingDigest
    )
    return this.accepted(message.type, message.batchId, null, false, false)
  }

  // ————————————————————————————————————————————————————————————
  // 内部工具
  // ————————————————————————————————————————————————————————————

  private guardBatch(batchId: string): { readonly batch: BatchState } | ApplyRejected {
    const batch = this.batch
    if (batch && batch.batchId === batchId) {
      if (batch.cancelled) return reject('cancelled', '批次已取消，迟到消息被拒绝')
      if (batch.closed) return reject('batch-closed', '批次已关闭；新批次必须经 batch-begin 承认')
      return { batch }
    }
    if (this.closedBatchExists(batchId)) {
      return reject('batch-closed', '批次已关闭；新批次必须经 batch-begin 承认')
    }
    return reject('batch-not-open', '未找到该 operation')
  }

  private closedBatchExists(batchId: string): boolean {
    if (!this.identity) return false
    const entry = this.cache.get(
      replayKey(this.identity.documentEpoch, this.identity.composerEpoch, batchId, '')
    )
    return entry !== undefined
  }

  private inFlight(file: FileState): number {
    let count = 0
    for (const sent of file.sent.values()) if (!sent.acked) count += 1
    return count
  }

  private transportOf(file: FileState): TransportState {
    if (file.sent.size === 0) return file.ended ? 'buffered' : 'idle'
    return this.inFlight(file) === 0 ? 'buffered' : 'buffering'
  }

  private recordOf(file: FileState): WireFileRecord {
    return {
      fileId: file.fileId,
      name: file.name,
      byteLength: file.byteLength,
      mime: file.mime,
      declaredSha256: file.declaredSha256,
      transport: this.transportOf(file),
      draft: file.draft,
      upload: file.upload,
      receivedBytes: file.receivedBytes,
      ackedBytes: file.ackedBytes,
      inFlight: this.inFlight(file),
      ended: file.ended,
      resolved: file.resolved,
      submittedItems: file.submittedItems,
      attachmentIds: [...file.attachmentIds]
    }
  }

  private pinCacheEntries(file: FileState, batch: BatchState, state: 'completed'): void {
    const entry = this.cache.get(replayKey(batch.documentEpoch, batch.composerEpoch, batch.batchId, file.fileId))
    if (entry && state === 'completed') entry.state = 'completed'
  }

  private accepted(
    type: WireMessageType,
    batchId: string | null,
    fileId: string | null,
    duplicate: boolean,
    importInvoked: boolean
  ): ApplyAccepted {
    const file = fileId !== null ? this.fileRecord(fileId) : null
    return {
      ok: true,
      type,
      batchId,
      fileId,
      duplicate,
      importInvoked,
      closed: this.batchClosed,
      file,
      files: this.files
    }
  }
}

/** 供证据脚本/测试复用的通道常量。 */
export const WIRE_CHUNK_BYTES = CHUNK_BYTES
export const WIRE_MAX_CHUNKS_IN_FLIGHT = MAX_CHUNKS_IN_FLIGHT
export type { ReplayEntry }
