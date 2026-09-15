/**
 * 浏览器分块接收端（D12）。
 *
 * 目标：把**已解码**的线协议 v1 消息安全组装成一个真实的浏览器 `File`，在普通 HTTP
 * （非 SecureContext，`crypto.subtle` 可能整体缺失）下照常做内容完整性校验，并把
 * 接收缓冲的内存占用钉死在明确的边界内。
 *
 * ## 复用而不是重写
 *
 * 逐块 `seq`/`offset`/累计字节、在途窗口、file-end 的长度与 SHA-256 校验、重放/去重、
 * 批次关闭后的迟到拒绝——这些语义**全部**由 D10 的生产状态机 `WireSession` 执行，
 * 本模块只做它不做的事：
 *
 *   1. 持有**字节**（`WireSession` 只累积哈希，不保留载荷）；
 *   2. 把校验干净的文件构造成真实 `File`，并交给生产草稿路径**恰好一次**；
 *   3. 维护接收缓冲的记账，并在 cancel / 批次关闭 / 身份失效时真的把字节丢掉；
 *   4. 产出 `ack` 与 `import-result`（线协议里 native→client 的那两个方向）。
 *
 * 因此这里没有第二套消息模型，也没有第二个 SHA-256：哈希走 `createSha256()`（有 Web Crypto
 * 就用它，否则纯 JS 增量实现），去重走 `ReplayCache`，判定顺序与 `session.ts` 逐条一致。
 *
 * ## 每批次一个状态机
 *
 * 冻结方案（`implementation-plan.md` §4.2）要求：**批次关闭后，同一会话里必须靠新的
 * `batch-begin`（新的 `batchId`）重新建立操作，且不重发 `context`**。D10 的 `WireSession`
 * 是"一会话一实例"的写法：`batch-end`/`cancel` 之后实例里仍留着已关闭的批次，第二个
 * `batch-begin` 会被判为 `duplicate-operation`/`batch-in-progress`（C# 侧的
 * `AttachmentSession` 更严格，直接要求新的 `context`）。
 *
 * 本模块不改动 D10 的任何语义，而是把**身份**留在接收端：每个批次用一个**新的**
 * `WireSession`，创建时用接收端已绑定的身份在内部"预热"一次 context（不经过线路，
 * 不是重发），再把 `batch-begin` 交给它。于是：
 *
 *   - 同会话新批次：新 `batchId` → 被 `batch-begin` 承认（不重发 context）；
 *   - 旧 `batchId`：被**保留的**旧会话判为 `duplicate-operation`；
 *   - 关闭批次后的迟到 `file-end`/`chunk`：被保留的旧会话判为 `batch-closed`，无法重建操作。
 *
 * ## 三种状态不合并
 *
 * `ack` 只证明**接收缓冲已接受该块**（transport），既不等于草稿已 staged，也不等于上传就绪；
 * 草稿状态只由 `import-result` 改变（本模块在导入后自己产出并应用该消息），上传始终归 Harness。
 */

import {
  ADDON_BUILD,
  CAPABILITY_CODES,
  type CapabilityResolution,
  type OriginFacts
} from '../shared/capabilities.js'
import { clampLimits } from '../shared/limits.js'
import {
  WireSession,
  base64Decode,
  createSha256,
  decodeMessage,
  type AckMessage,
  type ApplyResult,
  type CancelMessage,
  type CapabilitiesMessage,
  type ContextMessage,
  type DraftState,
  type ImportResultMessage,
  type Sha256Backend,
  type TransportState,
  type UploadState,
  type WireErrorCode,
  type WireFileRecord,
  type WireLimits,
  type WireMessage,
  type WireMessageType
} from '../shared/wire/index.js'
import { AttachmentHandshake, type HandshakeSnapshot } from './handshake.js'
import type { DraftImportResult } from './draft-adapter.js'

/** 接收端公开面版本。调用方必须显式核对。 */
export const RECEIVER_VERSION = 1

/** 接收端安装在页面全局上的名字（与桥一样，供真实传输层接入）。 */
export const RECEIVER_GLOBAL = '__DSH_ATTACHMENTS_RECEIVER__'

/** 一次草稿导入请求：接收端只提交本次新构造的那个 `File`。 */
export interface ReceiverImportRequest {
  readonly sessionId: string
  readonly batchId: string
  readonly fileId: string
  readonly files: readonly File[]
}

/** 草稿导入回调。生产上就是 `bridge.importFiles`。 */
export type ReceiverImportFn = (
  request: ReceiverImportRequest
) => DraftImportResult | Promise<DraftImportResult>

/** 接收端配置。 */
export interface ReceiverOptions {
  /** 把组装好的 `File` 交给生产草稿路径（必须恰好调用一次/文件）。 */
  readonly importFiles: ReceiverImportFn
  /** 生效限额；不得超过 `DEFAULT_LIMITS`（由 capabilities 语义保证）。 */
  readonly limits?: Partial<WireLimits> | undefined
  /** 同时组装的文件数上限；协议本身每目标同时只处理一个文件，这里是第二道防线。 */
  readonly maxConcurrentAssemblies?: number | undefined
  /** 保留多少个已关闭批次的状态（用于迟到消息判定与去重），活动批次被钉住不淘汰。 */
  readonly maxRetainedBatches?: number | undefined
  /** 注入 crypto（证明"没有 Web Crypto 时仍能校验"）；缺省读 `globalThis.crypto`。 */
  readonly crypto?: { subtle?: SubtleCrypto } | undefined
  /** 注入时钟。 */
  readonly now?: (() => number) | undefined
  readonly replayCapacity?: number | undefined
  readonly replayLifetimeMs?: number | undefined
  /**
   * D13 握手：本端构建标识（`hello.clientBuild`）。缺省用共享的 `ADDON_BUILD`。
   * 只影响广告内容，不改变 D12 的既有行为。
   */
  readonly clientBuild?: string | undefined
  /** D13 握手：当前页面来源（来源变化 = 导航，迟到消息一律拒绝）。 */
  readonly origin?: (() => OriginFacts) | undefined
  /** D13 握手：宿主当前会话；**有值时** `context` 必须指向它，否则 `context-changed`。 */
  readonly currentSession?: (() => string | null) | undefined
  /** D13 能力判定；返回非 available 时接收端拒绝一切操作消息（不会导入任何文件）。 */
  readonly capability?: (() => CapabilityResolution) | undefined
}

/**
 * 单个 fileId 的对外结果（D13 状态面）。
 *
 * 三层状态**从不合并**：`transport`（接收缓冲）≠ `draft`（草稿 staged）≠ `upload`
 * （上传归 Harness，线协议里不存在 ready/uploaded）。部分失败按 fileId 逐条可见。
 */
export interface ReceiverFileResult {
  readonly batchId: string
  readonly fileId: string
  readonly name: string
  readonly status: 'pending' | 'staged' | 'failed' | 'partial'
  /** 失败/部分失败的结果类代码；成功时为 null。 */
  readonly code: string | null
  readonly attachmentIds: readonly string[]
  readonly submittedItems: number
  readonly transport: TransportState
  readonly draft: DraftState
  /** 上传阶段恒为 Harness 所有：只有 `none` / `harness-owned`，绝无 ready。 */
  readonly upload: UploadState
  readonly bytes: number
  readonly updatedAtMs: number
}

/** 接收缓冲记账（只统计**本模块持有**的字节；File 一旦构造出来就由它自己持有）。 */
export interface ReceiverAccounting {
  /** 当前接收缓冲里的原始字节数。 */
  readonly bufferedBytes: number
  /** 当前持有字节的文件数（并发组装数）。 */
  readonly bufferedFiles: number
  readonly peakBufferedBytes: number
  readonly peakBufferedFiles: number
  /** 已构造进 `File` 的字节数（构造后接收缓冲即释放）。 */
  readonly constructedBytes: number
  /** 未构造就被丢弃的字节数（cancel / 批次关闭 / 身份失效 / 淘汰）。 */
  readonly releasedBytes: number
  readonly releasedFiles: number
  readonly filesConstructed: number
  readonly importsInvoked: number
  readonly importsSucceeded: number
  readonly importsFailed: number
  readonly rejectedMessages: number
  readonly retainedBatches: number
  readonly openBatchId: string | null
  /** 新会话将使用的哈希后端（HTTP 非安全上下文下应为 pure-js）。 */
  readonly hashBackend: Sha256Backend
  readonly stagingByteLimit: number
  readonly maxConcurrentAssemblies: number
  /** 生命周期是否已被撤销（撤销后所有入口都确定拒绝）。 */
  readonly disposed: boolean
}

/** 构造出来的真实 `File` 及其独立读数。 */
export interface AssembledFile {
  readonly fileId: string
  readonly name: string
  readonly mime: string
  readonly size: number
  /** file-end 声明并由接收端校验通过的摘要（十六进制小写）。 */
  readonly sha256: string
  readonly file: File
}

/** 一次导入的结果及其对应的线协议消息。 */
export interface ReceiverImportOutcome {
  readonly result: DraftImportResult
  readonly message: ImportResultMessage
  /** 该 import-result 是否被状态机接受（拒绝时不发给对端）。 */
  readonly applied: boolean
  readonly appliedCode: WireErrorCode | null
}

export type ReceiverStage =
  | 'decode'
  | 'handshake'
  | 'context'
  | 'batch-begin'
  | 'file-begin'
  | 'chunk'
  | 'ack'
  | 'file-end'
  | 'import-result'
  | 'batch-end'
  | 'cancel'

/** 接受结果。 */
export interface ReceiveAccepted {
  readonly ok: true
  readonly stage: ReceiverStage
  readonly type: WireMessageType
  readonly batchId: string | null
  readonly fileId: string | null
  /** 已完成操作的幂等重放（重复 file-end/batch-end/ack）。 */
  readonly duplicate: boolean
  /** 本次是否触发了一次草稿导入（重复到达必须为 false）。 */
  readonly importInvoked: boolean
  readonly file: WireFileRecord | null
  /** 本次构造出的 File（仅 file-end 那一次非空）。 */
  readonly assembled: AssembledFile | null
  readonly import: ReceiverImportOutcome | null
  readonly bufferedBytes: number
}

/** 拒绝结果。 */
export interface ReceiveRejected {
  readonly ok: false
  readonly stage: ReceiverStage
  readonly code: WireErrorCode
  readonly detail: string
  readonly path: string
  readonly bufferedBytes: number
}

export type ReceiveResult = ReceiveAccepted | ReceiveRejected

interface Assembly {
  readonly key: string
  readonly batchId: string
  readonly fileId: string
  readonly name: string
  readonly mime: string
  readonly byteLength: number
  readonly parts: Uint8Array[]
  bytes: number
}

interface BatchEntry {
  readonly batchId: string
  readonly session: WireSession
  closed: boolean
  cancelled: boolean
  lastActivityMs: number
}

type PendingOutgoing =
  | {
      readonly kind: 'ack'
      readonly batchId: string
      readonly fileId: string
      readonly seq: number
      readonly offset: number
      readonly byteLength: number
    }
  | { readonly kind: 'message'; readonly message: WireMessage }

const assemblyKey = (batchId: string, fileId: string): string => `${batchId}\u0000${fileId}`

const reject = (code: WireErrorCode, detail: string, path = ''): ReceiveRejected => ({
  ok: false,
  stage: 'decode',
  code,
  detail,
  path,
  bufferedBytes: 0
})

/**
 * 草稿导入失败码 → 线协议结果码（`import-result.code` 只接受 `ERROR_CODES`）。
 */
function draftFailureCode(code: string): string {
  // 只有这三个码在 ERROR_CODES 里有同名成员，其余一律归入 draft-import-failed。
  if (code === 'no-session') return 'no-session'
  if (code === 'context-changed') return 'context-changed'
  if (code === 'existing-attachments-lost') return 'partial-import'
  return 'draft-import-failed'
}

/** 浏览器分块接收端。 */
export class AttachmentReceiver {
  private readonly importFiles: ReceiverImportFn
  private readonly maxConcurrentAssemblies: number
  private readonly maxRetainedBatches: number
  private readonly cryptoOption: { subtle?: SubtleCrypto } | undefined
  private readonly now: () => number
  private readonly replayCapacity: number | undefined
  private readonly replayLifetimeMs: number | undefined
  private readonly handshakeState: AttachmentHandshake
  private readonly capabilityProvider: () => CapabilityResolution
  private limits: WireLimits
  private identity: ContextMessage | null = null
  private identityExpired = false
  private readonly batches = new Map<string, BatchEntry>()
  private readonly assemblies = new Map<string, Assembly>()
  private readonly outbox: PendingOutgoing[] = []
  /** 逐 fileId 的结果账本（含已关闭批次），供状态面报告部分失败。 */
  private readonly results = new Map<string, ReceiverFileResult>()
  /** 已经因能力不可用发出过 cancel 的 batchId（同批只发一次）。 */
  private readonly refusalCancels = new Set<string>()
  private openBatchId: string | null = null
  private queue: Promise<unknown> = Promise.resolve()
  private handshakeSent = false
  private disposed = false
  private disposeReason: string | null = null
  private disposedAtMs: number | null = null
  private stats = {
    peakBufferedBytes: 0,
    peakBufferedFiles: 0,
    constructedBytes: 0,
    releasedBytes: 0,
    releasedFiles: 0,
    filesConstructed: 0,
    importsInvoked: 0,
    importsSucceeded: 0,
    importsFailed: 0,
    rejectedMessages: 0
  }

  constructor(options: ReceiverOptions) {
    this.importFiles = options.importFiles
    this.maxConcurrentAssemblies = Math.max(1, options.maxConcurrentAssemblies ?? 2)
    this.maxRetainedBatches = Math.max(1, options.maxRetainedBatches ?? 16)
    this.cryptoOption = options.crypto
    this.now = options.now ?? (() => Date.now())
    this.replayCapacity = options.replayCapacity
    this.replayLifetimeMs = options.replayLifetimeMs
    this.capabilityProvider =
      options.capability ??
      (() => ({ status: 'available', code: null, missing: [], reason: null }) as CapabilityResolution)
    // 限额在这里被**钳到冻结上限**：本端策略不能大于线协议 v1 的冻结值，
    // 对端声明再由 handshake 钳到本策略（见 shared/limits.ts）。
    this.handshakeState = new AttachmentHandshake({
      clientBuild: options.clientBuild ?? ADDON_BUILD,
      limits: options.limits,
      origin: options.origin,
      currentSession: options.currentSession,
      capability: this.capabilityProvider,
      now: this.now
    })
    this.limits = this.handshakeState.limits
  }

  // ————————————————————————————————————————————————————————————
  // 公开读数
  // ————————————————————————————————————————————————————————————

  get accounting(): ReceiverAccounting {
    let bufferedBytes = 0
    for (const assembly of this.assemblies.values()) bufferedBytes += assembly.bytes
    return {
      bufferedBytes,
      bufferedFiles: this.assemblies.size,
      peakBufferedBytes: this.stats.peakBufferedBytes,
      peakBufferedFiles: this.stats.peakBufferedFiles,
      constructedBytes: this.stats.constructedBytes,
      releasedBytes: this.stats.releasedBytes,
      releasedFiles: this.stats.releasedFiles,
      filesConstructed: this.stats.filesConstructed,
      importsInvoked: this.stats.importsInvoked,
      importsSucceeded: this.stats.importsSucceeded,
      importsFailed: this.stats.importsFailed,
      rejectedMessages: this.stats.rejectedMessages,
      retainedBatches: this.batches.size,
      openBatchId: this.openBatchId,
      hashBackend: createSha256(this.cryptoOption).backend,
      stagingByteLimit: this.limits.maxStagingBytesPerTarget,
      maxConcurrentAssemblies: this.maxConcurrentAssemblies,
      disposed: this.disposed
    }
  }

  /** 当前接收缓冲字节数（内存边界的直接读数）。 */
  get bufferedBytes(): number {
    let total = 0
    for (const assembly of this.assemblies.values()) total += assembly.bytes
    return total
  }

  /** 绑定的身份（未绑定或已过期时给出相应标志）。 */
  get currentIdentity(): (ContextMessage & { readonly expired: boolean }) | null {
    if (this.identity === null) return null
    return { ...this.identity, expired: this.identityExpired }
  }

  /** 当前开放批次 ID。 */
  get activeBatchId(): string | null {
    return this.openBatchId
  }

  /** 单文件记录（先看开放批次，再按保留批次查找）。 */
  fileRecord(fileId: string): WireFileRecord | null {
    const open = this.openBatchId === null ? null : this.batches.get(this.openBatchId) ?? null
    const openRecord = open?.session.fileRecord(fileId) ?? null
    if (openRecord !== null) return openRecord
    for (const entry of this.batches.values()) {
      const record = entry.session.fileRecord(fileId)
      if (record !== null) return record
    }
    return null
  }

  /** 开放批次的逐文件记录。 */
  get files(): readonly WireFileRecord[] {
    const open = this.openBatchId === null ? null : this.batches.get(this.openBatchId) ?? null
    return open?.session.files ?? []
  }

  /** 握手状态（版本/限额/来源/会话身份的逐字段读数）。 */
  get handshake(): HandshakeSnapshot {
    return this.handshakeState.snapshot
  }

  /** 当前能力判定（每次读取都重新探测，不缓存过期结论）。 */
  get capability(): CapabilityResolution {
    return this.capabilityProvider()
  }

  /** 逐 fileId 的结果账本（含已关闭批次）：staged / failed / partial 各自可辨。 */
  get fileResults(): readonly ReceiverFileResult[] {
    return [...this.results.values()]
  }

  /** 已确认进入草稿的 fileId（部分成功时保留可确认的成功项）。 */
  get stagedFileIds(): readonly string[] {
    return this.fileResults.filter((item) => item.status === 'staged').map((item) => item.fileId)
  }

  /** 失败与部分失败的逐文件明细（fileId + 结果码）。 */
  get partialFailures(): readonly ReceiverFileResult[] {
    return this.fileResults.filter((item) => item.status === 'failed' || item.status === 'partial')
  }

  /** 是否已被生命周期撤销（撤销后不再接受任何消息）。 */
  get isDisposed(): boolean {
    return this.disposed
  }

  /**
   * 本端握手消息（`hello` + `capabilities`），由传输层显式取走并发出。
   *
   * 刻意**不**混进 `drainOutgoing()`：那条流里的每一帧都对应一次真实的
   * "接收缓冲已接受"或"草稿导入结果"，把握手塞进去会让逐块 ACK 判据失真。
   * 幂等：第二次调用返回空数组。
   */
  beginHandshake(): readonly WireMessage[] {
    if (this.disposed || this.handshakeSent) return []
    this.handshakeSent = true
    return this.handshakeState.outgoing()
  }

  // ————————————————————————————————————————————————————————————
  // 入口
  // ————————————————————————————————————————————————————————————

  /** 接受一条**已解码**的 v1 消息。并发调用按到达顺序串行化。 */
  async accept(message: WireMessage): Promise<ReceiveResult> {
    const run = (): Promise<ReceiveResult> => this.dispatch(message)
    const chained = this.queue.then(run, run)
    this.queue = chained.then(
      () => undefined,
      () => undefined
    )
    return chained
  }

  /** 接受一条报文文本：先用生产 codec 解码，再走 `accept`。 */
  async acceptText(text: string): Promise<ReceiveResult> {
    if (this.disposed) return this.rejectedDisposed()
    const decoded = decodeMessage(text)
    if (!decoded.ok) {
      this.stats.rejectedMessages += 1
      return { ...reject(decoded.code, decoded.detail, decoded.path), bufferedBytes: this.bufferedBytes }
    }
    return this.accept(decoded.message)
  }

  /**
   * 取出待发消息（`ack` 与 `import-result`）并在此刻把 ack 应用到状态机（释放背压窗口）。
   *
   * ack 的**产生**条件是"字节已被接收缓冲接受"；这里只负责投递。
   */
  async drainOutgoing(): Promise<readonly WireMessage[]> {
    const messages: WireMessage[] = []
    for (const pending of this.outbox.splice(0, this.outbox.length)) {
      if (pending.kind === 'message') {
        messages.push(pending.message)
        continue
      }
      const entry = this.batches.get(pending.batchId)
      if (entry === undefined) continue
      const record = entry.session.fileRecord(pending.fileId)
      if (record === null) continue
      const inFlight = Math.max(0, record.inFlight - 1)
      const ack: AckMessage = {
        v: 1,
        type: 'ack',
        sessionId: this.identity?.sessionId ?? '',
        batchId: pending.batchId,
        fileId: pending.fileId,
        seq: pending.seq,
        offset: pending.offset,
        byteLength: pending.byteLength,
        bufferedBytes: this.bufferedBytes,
        inFlight
      }
      const applied: ApplyResult = await entry.session.apply(ack)
      // 批次在投递前被取消时该 ack 已无意义：不发出，也不改状态。
      if (!applied.ok) continue
      messages.push(ack)
    }
    return messages
  }

  /**
   * 导航/关闭：释放全部缓冲、丢弃全部批次状态，并让身份过期。
   * 之后必须由新的 `context` 重新建立身份（迟到消息得 `context-changed`）。
   */
  navigate(): void {
    this.releaseAll()
    this.handshakeState.navigate()
    if (this.identity !== null) this.identityExpired = true
  }

  /**
   * 生命周期撤销（禁用/卸载）：释放自有资源、丢弃身份，并让之后再进来的任何消息
   * 都被确定拒绝。**保留引用**的调用方（原生层可能已经拿到 receiver）也无法再导入：
   * 撤销是"行为撤销"，不只是把全局名删掉。
   *
   * 幂等；`reason` 只用于诊断文本。
   */
  dispose(reason = 'disposed'): void {
    if (this.disposed) return
    this.disposed = true
    this.disposeReason = reason
    this.disposedAtMs = this.now()
    this.releaseAll()
    this.handshakeState.navigate()
    if (this.identity !== null) this.identityExpired = true
  }

  /** 撤销原因（诊断用；未撤销时为 null）。 */
  get disposal(): { readonly disposed: boolean; readonly reason: string | null; readonly atMs: number | null } {
    return { disposed: this.disposed, reason: this.disposeReason, atMs: this.disposedAtMs }
  }

  /** 主动取消当前批次（对端也会收到 cancel）。 */
  async cancelActiveBatch(reason = 'cancelled'): Promise<ApplyResult | null> {
    const entry = this.openBatchId === null ? null : this.batches.get(this.openBatchId) ?? null
    if (entry === null) return null
    const message: CancelMessage = {
      v: 1,
      type: 'cancel',
      sessionId: this.identity?.sessionId ?? '',
      batchId: entry.batchId,
      reason,
      stage: 'protocol-transfer'
    }
    const result = await entry.session.apply(message)
    if (!result.ok) return result
    entry.cancelled = true
    this.releaseBatch(entry.batchId)
    this.dropPendingAcks(entry.batchId)
    this.outbox.push({ kind: 'message', message })
    return result
  }

  /** 超时判定（纯计算，不自行起定时器）；超时批次的自有缓冲立即释放。 */
  checkTimeouts(nowMs?: number): readonly string[] {
    const cancelled: string[] = []
    for (const entry of this.batches.values()) {
      const report = entry.session.checkTimeouts(nowMs)
      if (report.cancelledBatchId === null) continue
      entry.cancelled = true
      cancelled.push(entry.batchId)
      this.releaseBatch(entry.batchId)
      this.dropPendingAcks(entry.batchId)
    }
    return cancelled
  }

  // ————————————————————————————————————————————————————————————
  // 分派
  // ————————————————————————————————————————————————————————————

  private async dispatch(message: WireMessage): Promise<ReceiveResult> {
    if (this.disposed) return this.rejectedDisposed(message.type)

    switch (message.type) {
      case 'context':
        return this.handleContext(message)
      case 'capabilities':
        return this.handleCapabilities(message)
      case 'hello':
        return this.handleHello(message)
      default:
        break
    }

    // 能力闸门先于身份闸门：能力不可用时**任何**操作消息都不进入状态机，
    // 因此不可能有任何文件被导入（detail 里带确定的结果类代码）。
    const capability = this.capabilityGate(message)
    if (capability !== null) return capability

    const gate = this.identityGate(message)
    if (gate !== null) {
      this.stats.rejectedMessages += 1
      return { ...gate, bufferedBytes: this.bufferedBytes }
    }

    switch (message.type) {
      case 'batch-begin':
        return this.handleBatchBegin(message)
      case 'file-begin':
        return this.handleFileBegin(message)
      case 'chunk':
        return this.handleChunk(message)
      case 'ack':
        return this.handleAck(message)
      case 'file-end':
        return this.handleFileEnd(message)
      case 'import-result':
        return this.handleImportResult(message)
      case 'batch-end':
        return this.handleBatchEnd(message)
      case 'cancel':
        return this.handleCancel(message)
      default:
        this.stats.rejectedMessages += 1
        return {
          ...reject('unknown-message-type', '未处理的消息类型'),
          bufferedBytes: this.bufferedBytes
        }
    }
  }

  /**
   * 能力闸门：remote 降级/禁用、或已存在未知上传承载时，接收端**拒绝整批**。
   *
   * 拒绝码用冻结线协议里的 `no-session`（"客户端不接受该操作"是它的语义边界），
   * 而根本原因用结果类代码表达：拒绝结果和 `detail` 都带上
   * `capability-disabled` / `capability-conflict`，并对发起批次的 `batch-begin`
   * 回一条 `cancel`（`reason` = 结果类代码），免得对端只能看到一个笼统的失败。
   */
  private capabilityGate(message: WireMessage): ReceiveRejected | null {
    const resolution = this.capabilityProvider()
    if (resolution.status === 'available') return null
    const code = resolution.code ?? CAPABILITY_CODES.capabilityDisabled
    const batchId = (message as { readonly batchId?: string }).batchId
    if (message.type === 'batch-begin' && typeof batchId === 'string' && !this.refusalCancels.has(batchId)) {
      this.refusalCancels.add(batchId)
      const sessionId = this.identity?.sessionId
      this.outbox.push({
        kind: 'message',
        message: {
          v: 1,
          type: 'cancel',
          ...(sessionId === undefined || sessionId === '' ? {} : { sessionId }),
          batchId,
          reason: code,
          stage: 'protocol-transfer'
        }
      })
    }
    this.stats.rejectedMessages += 1
    return {
      ok: false,
      stage: message.type as ReceiverStage,
      code: 'no-session',
      detail: `${code}：附件能力不可用（${resolution.reason ?? resolution.status}），拒绝 ${message.type}`,
      path: 'capability',
      bufferedBytes: this.bufferedBytes
    }
  }

  /** 撤销后的统一拒绝：确定、无副作用、带 `capability-disabled` 根因。 */
  private rejectedDisposed(type: WireMessageType | null = null): ReceiveRejected {
    this.stats.rejectedMessages += 1
    return {
      ok: false,
      stage: 'decode',
      code: 'no-session',
      detail: `${CAPABILITY_CODES.capabilityDisabled}：接收端已被撤销（${this.disposeReason ?? 'disposed'}），${
        type === null ? '不再接受任何消息' : `拒绝 ${type}`
      }；需要页面重载才能重新启用`,
      path: 'lifecycle',
      bufferedBytes: this.bufferedBytes
    }
  }

  private async handleHello(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'hello') return this.rejectResult('handshake', 'unknown-message-type', '类型不符')
    const result = this.handshakeState.acceptHello(message)
    if (!result.ok) {
      this.stats.rejectedMessages += 1
      return {
        ok: false,
        stage: 'handshake',
        code: result.code,
        detail: result.detail,
        path: result.path,
        bufferedBytes: this.bufferedBytes
      }
    }
    return this.accepted({
      stage: 'handshake',
      type: message.type,
      batchId: null,
      fileId: null,
      duplicate: false,
      importInvoked: false,
      file: null,
      assembled: null,
      import: null
    })
  }

  private handleContext(message: ContextMessage): ReceiveResult {
    const result = this.handshakeState.acceptContext(message)
    if (!result.ok) {
      this.stats.rejectedMessages += 1
      return {
        ok: false,
        stage: 'context',
        code: result.code,
        detail: result.detail,
        path: result.path,
        bufferedBytes: this.bufferedBytes
      }
    }

    const previous = this.identity
    const changed =
      previous === null ||
      previous.sessionId !== message.sessionId ||
      previous.targetId !== message.targetId ||
      previous.documentEpoch !== message.documentEpoch ||
      previous.composerEpoch !== message.composerEpoch ||
      previous.composerScope !== message.composerScope

    if (changed) this.releaseAll()
    this.identity = message
    this.identityExpired = false

    return this.accepted({
      stage: 'context',
      type: message.type,
      batchId: null,
      fileId: null,
      duplicate: false,
      importInvoked: false,
      file: null,
      assembled: null,
      import: null
    })
  }

  private async handleCapabilities(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'capabilities') {
      return this.rejectResult('handshake', 'unknown-message-type', '类型不符')
    }
    const result = this.handshakeState.acceptCapabilities(message)
    if (!result.ok) {
      this.stats.rejectedMessages += 1
      return {
        ok: false,
        stage: 'handshake',
        code: result.code,
        detail: result.detail,
        path: result.path,
        bufferedBytes: this.bufferedBytes
      }
    }
    // 生效限额 = min(本端策略, 对端声明)，且恒不超过冻结上限。
    this.limits = result.limits
    const open = this.openBatchId === null ? null : this.batches.get(this.openBatchId) ?? null
    if (open !== null) {
      // 转发**重新钳过**的 capabilities，而不是对端原文：否则已开批次会把限额
      // 抬回对端声明的值（状态机的 apply('capabilities') 是整体替换）。
      const clamped: CapabilitiesMessage = {
        v: 1,
        type: 'capabilities',
        features: [...message.features],
        limits: { ...result.limits }
      }
      await open.session.apply(clamped)
    }
    return this.accepted({
      stage: 'handshake',
      type: message.type,
      batchId: null,
      fileId: null,
      duplicate: false,
      importInvoked: false,
      file: null,
      assembled: null,
      import: null
    })
  }

  // ————————————————————————————————————————————————————————————
  // 批次准入
  // ————————————————————————————————————————————————————————————

  private async handleBatchBegin(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'batch-begin') return this.rejectResult('batch-begin', 'unknown-message-type', '类型不符')

    const known = this.batches.get(message.batchId)
    if (known !== undefined) {
      // 已关闭/已取消/正在进行的同一 batchId：交给保留的会话按 D10 语义判定。
      return this.applyThrough(known, message, 'batch-begin')
    }
    const open = this.openBatchId === null ? null : this.batches.get(this.openBatchId) ?? null
    if (open !== null && !open.closed && !open.cancelled) {
      // 只有**仍在进行中**的批次才挡住新批次；已取消的批次视同已关闭，不再占用会话
      // （R15 修复：此前取消后同一会话永远开不了新批次，用户无法再次粘贴）。
      return this.applyThrough(open, message, 'batch-begin')
    }

    const identity = this.identity
    if (identity === null) return this.rejectResult('batch-begin', 'no-session', '尚未建立 context')

    // 新批次：新状态机 + 接收端已绑定身份的**内部**预热（不经过线路，不是重发 context）。
    const session = this.newSession()
    const primed = await session.apply(this.identityMessage(identity))
    if (!primed.ok) return this.rejectedFrom(primed, 'batch-begin')
    const result = await session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'batch-begin')

    this.batches.set(message.batchId, {
      batchId: message.batchId,
      session,
      closed: false,
      cancelled: false,
      lastActivityMs: this.now()
    })
    this.openBatchId = message.batchId
    this.evictRetained()
    return this.acceptedFrom(result, 'batch-begin')
  }

  private async handleBatchEnd(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'batch-end') return this.rejectResult('batch-end', 'unknown-message-type', '类型不符')
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('batch-end', 'batch-not-open', '未找到该 operation')
    const result = await entry.session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'batch-end')

    entry.closed = true
    entry.cancelled = false
    entry.lastActivityMs = this.now()
    if (this.openBatchId === message.batchId) this.openBatchId = null
    // 批次关闭：该批次的自有缓冲一律释放（迟到消息不得重建操作）。
    this.releaseBatch(message.batchId)
    this.dropPendingAcks(message.batchId)
    return this.acceptedFrom(result, 'batch-end')
  }

  private async handleCancel(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'cancel') return this.rejectResult('cancel', 'unknown-message-type', '类型不符')
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('cancel', 'batch-not-open', '未找到该 operation')
    const result = await entry.session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'cancel')

    entry.cancelled = true
    entry.lastActivityMs = this.now()
    // cancel 释放自有资源，但**不**丢弃已确认的草稿结果。
    this.releaseBatch(message.batchId)
    this.dropPendingAcks(message.batchId)
    return this.acceptedFrom(result, 'cancel')
  }

  // ————————————————————————————————————————————————————————————
  // 文件与分块
  // ————————————————————————————————————————————————————————————

  private async handleFileBegin(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'file-begin') return this.rejectResult('file-begin', 'unknown-message-type', '类型不符')
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('file-begin', 'batch-not-open', '未找到该 operation')

    // 并发组装上限：第二道防线（协议本身每目标同时只处理一个文件）。
    if (this.assemblies.size >= this.maxConcurrentAssemblies) {
      return {
        ...reject(
          'file-in-progress',
          `接收端并发组装上限 ${this.maxConcurrentAssemblies}，当前已有 ${this.assemblies.size} 个文件在组装`
        ),
        stage: 'file-begin',
        bufferedBytes: this.bufferedBytes
      }
    }

    const result = await entry.session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'file-begin')
    entry.lastActivityMs = this.now()

    const key = assemblyKey(message.batchId, message.fileId)
    if (!this.assemblies.has(key)) {
      this.assemblies.set(key, {
        key,
        batchId: message.batchId,
        fileId: message.fileId,
        name: message.name,
        mime: message.mime,
        byteLength: message.byteLength,
        parts: [],
        bytes: 0
      })
    }
    // 先记一条 pending：状态面因此能区分"传输中"与"已 staged/失败"。
    this.recordResult(message.batchId, message.fileId, 'pending', null, [])
    this.recordPeak()
    return this.acceptedFrom(result, 'file-begin')
  }

  private async handleChunk(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'chunk') return this.rejectResult('chunk', 'unknown-message-type', '类型不符')
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('chunk', 'batch-not-open', '未找到该 operation')

    // 内存边界先于状态机推进：超限的块**拒绝而不是缓冲**（拒绝了就不写任何状态）。
    const prospective = this.bufferedBytes + message.byteLength
    if (prospective > this.limits.maxStagingBytesPerTarget) {
      return {
        ...reject(
          'limit-staging-bytes',
          `接收缓冲 ${this.bufferedBytes} + ${message.byteLength} > 目标暂存上限 ${this.limits.maxStagingBytesPerTarget}`
        ),
        stage: 'chunk',
        bufferedBytes: this.bufferedBytes
      }
    }

    const result = await entry.session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'chunk')

    const assembly = this.assemblies.get(assemblyKey(message.batchId, message.fileId))
    if (assembly === undefined) {
      return this.rejectResult('chunk', 'file-id-mismatch', `fileId ${message.fileId} 没有接收缓冲`)
    }
    const payload = base64Decode(message.dataBase64)
    if (payload.length !== message.byteLength) {
      return this.rejectResult('chunk', 'size-mismatch', `载荷 ${payload.length} 字节 ≠ 声明 ${message.byteLength}`)
    }
    // 字节进入接收缓冲之后才产生 ACK：ack = 接收缓冲（transport），不是 staged，也不是上传就绪。
    assembly.parts.push(payload)
    assembly.bytes += payload.length
    entry.lastActivityMs = this.now()
    this.recordPeak()
    this.outbox.push({
      kind: 'ack',
      batchId: message.batchId,
      fileId: message.fileId,
      seq: message.seq,
      offset: message.offset,
      byteLength: message.byteLength
    })
    return this.acceptedFrom(result, 'chunk')
  }

  private async handleAck(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'ack') return this.rejectResult('ack', 'unknown-message-type', '类型不符')
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('ack', 'batch-not-open', '未找到该 operation')
    const result = await entry.session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'ack')
    entry.lastActivityMs = this.now()
    return this.acceptedFrom(result, 'ack')
  }

  private async handleImportResult(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'import-result') {
      return this.rejectResult('import-result', 'unknown-message-type', '类型不符')
    }
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('import-result', 'batch-not-open', '未找到该 operation')
    const result = await entry.session.apply(message)
    if (!result.ok) return this.rejectedFrom(result, 'import-result')
    entry.lastActivityMs = this.now()
    // 对端回报的草稿结果照实记账：staged/failed/partial 逐 fileId 入账。
    this.recordResult(message.batchId, message.fileId, message.status, message.code ?? null, message.attachmentIds)
    return this.acceptedFrom(result, 'import-result')
  }

  private async handleFileEnd(message: WireMessage): Promise<ReceiveResult> {
    if (message.type !== 'file-end') return this.rejectResult('file-end', 'unknown-message-type', '类型不符')
    const entry = this.batches.get(message.batchId)
    if (entry === undefined) return this.rejectResult('file-end', 'batch-not-open', '未找到该 operation')

    // 长度 + SHA-256 校验全部在状态机内完成（createSha256：有 Web Crypto 就用，否则纯 JS）。
    const result = await entry.session.apply(message)
    if (!result.ok) {
      // 校验失败的文件同样入账：状态面必须能说出"哪个 fileId 失败、码是什么"。
      this.recordResult(message.batchId, message.fileId, 'failed', result.code, [])
      return this.rejectedFrom(result, 'file-end')
    }
    entry.lastActivityMs = this.now()

    if (!result.importInvoked) {
      // 重复 file-end：幂等重放，绝不产生第二个 File，也绝不第二次导入。
      return this.acceptedFrom(result, 'file-end')
    }

    const assembly = this.assemblies.get(assemblyKey(message.batchId, message.fileId))
    if (assembly === undefined) {
      return this.rejectResult('file-end', 'file-id-mismatch', `fileId ${message.fileId} 没有接收缓冲`)
    }

    // 用收到的分块拼出真实 File：Blob 构造器会复制字节，因此随后即可释放自有缓冲。
    const file = new File(assembly.parts as unknown as BlobPart[], assembly.name, { type: assembly.mime })
    if (file.size !== assembly.byteLength) {
      this.releaseAssembly(assembly, 'released')
      return this.rejectResult(
        'file-end',
        'size-mismatch',
        `构造出的 File 大小 ${file.size} ≠ 声明 ${assembly.byteLength}`
      )
    }
    const assembled: AssembledFile = {
      fileId: assembly.fileId,
      name: file.name,
      mime: file.type,
      size: file.size,
      sha256: message.sha256,
      file
    }
    this.stats.filesConstructed += 1
    this.stats.constructedBytes += assembly.bytes
    this.releaseAssembly(assembly, 'constructed')

    const imported = await this.runImport(entry, message.batchId, message.fileId, message.submittedItems ?? 1, file)
    return this.accepted({
      stage: 'file-end',
      type: message.type,
      batchId: message.batchId,
      fileId: message.fileId,
      duplicate: false,
      importInvoked: true,
      file: entry.session.fileRecord(message.fileId),
      assembled,
      import: imported
    })
  }

  // ————————————————————————————————————————————————————————————
  // 草稿导入
  // ————————————————————————————————————————————————————————————

  /** 把构造好的 File 交给生产草稿路径，并把结果翻译成 import-result。 */
  private async runImport(
    entry: BatchEntry,
    batchId: string,
    fileId: string,
    submittedItems: number,
    file: File
  ): Promise<ReceiverImportOutcome> {
    const sessionId = this.identity?.sessionId ?? ''
    this.stats.importsInvoked += 1
    let result: DraftImportResult
    try {
      result = await this.importFiles({ sessionId, batchId, fileId, files: [file] })
    } catch (error) {
      result = {
        ok: false,
        code: 'unsupported',
        detail: `草稿导入抛错：${error instanceof Error ? error.message : String(error)}`,
        previous: []
      }
    }

    const message = this.toImportResult(batchId, fileId, submittedItems, result)
    if (result.ok) this.stats.importsSucceeded += 1
    else this.stats.importsFailed += 1

    // 先把 import-result 应用到状态机，再入账：账本里的 transport/draft/upload 必须反映
    // **这条 import-result 之后**的真实状态，而不是导入前的快照。
    const applied = await entry.session.apply(message)
    if (applied.ok) this.outbox.push({ kind: 'message', message })
    // 逐 fileId 入账：staged 的记新增 ID，failed/partial 的记确定的结果码。
    this.recordResult(batchId, fileId, message.status, message.code ?? null, message.attachmentIds, submittedItems)
    return {
      result,
      message,
      applied: applied.ok,
      appliedCode: applied.ok ? null : applied.code
    }
  }

  /**
   * 写入/更新逐 fileId 的结果账本。
   *
   * 三层状态分开保存：`transport`/`draft`/`upload` 来自状态机的文件记录，
   * `status`/`code`/`attachmentIds` 来自这次导入或对端回报。上传阶段只可能是
   * `none`/`harness-owned`——附件插件**永远不宣称** upload ready。
   */
  private recordResult(
    batchId: string,
    fileId: string,
    rawStatus: string,
    code: string | null,
    attachmentIds: readonly string[],
    submittedItems?: number
  ): void {
    // 线协议里 `status` 的 TS 类型是宽字符串（`RESULT_STATUSES` 未 as const），
    // 取值集合由 schema/枚举守住；这里再收敛一次，未知取值按失败记账（不谎报成功）。
    const status: ReceiverFileResult['status'] =
      rawStatus === 'staged' || rawStatus === 'failed' || rawStatus === 'partial' || rawStatus === 'pending'
        ? rawStatus
        : 'failed'
    const key = assemblyKey(batchId, fileId)
    const previous = this.results.get(key)
    const record = this.fileRecord(fileId)
    this.results.set(key, {
      batchId,
      fileId,
      name: record?.name ?? previous?.name ?? '',
      status,
      code,
      attachmentIds: [...attachmentIds],
      submittedItems: submittedItems ?? previous?.submittedItems ?? 1,
      transport: record?.transport ?? previous?.transport ?? 'idle',
      draft: record?.draft ?? (status === 'pending' ? 'none' : status),
      upload: record?.upload ?? previous?.upload ?? 'none',
      bytes: record?.receivedBytes ?? previous?.bytes ?? 0,
      updatedAtMs: this.now()
    })
  }

  /** 草稿结果 → `import-result`（状态与 ID 数必须自洽，否则状态机会拒绝我们自己）。 */
  private toImportResult(
    batchId: string,
    fileId: string,
    submittedItems: number,
    result: DraftImportResult
  ): ImportResultMessage {
    const sessionId = this.identity?.sessionId ?? ''
    if (result.ok) {
      const ids = [...result.added]
      if (ids.length === submittedItems) {
        return { v: 1, type: 'import-result', sessionId, batchId, fileId, status: 'staged', attachmentIds: ids }
      }
      if (ids.length === 0) {
        return {
          v: 1,
          type: 'import-result',
          sessionId,
          batchId,
          fileId,
          status: 'failed',
          attachmentIds: [],
          code: 'draft-import-failed'
        }
      }
      // 新增数与 file-end 声明的 submittedItems 不一致：只能按部分成功上报。
      return {
        v: 1,
        type: 'import-result',
        sessionId,
        batchId,
        fileId,
        status: 'partial',
        attachmentIds: ids,
        code: 'partial-import'
      }
    }
    return {
      v: 1,
      type: 'import-result',
      sessionId,
      batchId,
      fileId,
      status: 'failed',
      attachmentIds: [],
      code: draftFailureCode(result.code)
    }
  }

  // ————————————————————————————————————————————————————————————
  // 缓冲与身份
  // ————————————————————————————————————————————————————————————

  private newSession(): WireSession {
    return new WireSession({
      limits: this.limits,
      crypto: this.cryptoOption,
      now: this.now,
      replayCapacity: this.replayCapacity,
      replayLifetimeMs: this.replayLifetimeMs
    })
  }

  private identityMessage(identity: ContextMessage): ContextMessage {
    return {
      v: 1,
      type: 'context',
      sessionId: identity.sessionId,
      targetId: identity.targetId,
      documentEpoch: identity.documentEpoch,
      composerEpoch: identity.composerEpoch,
      composerScope: identity.composerScope
    }
  }

  private identityGate(message: WireMessage): ReceiveRejected | null {
    const sessionId = (message as { readonly sessionId?: string }).sessionId
    if (sessionId === undefined) return reject('no-session', '操作消息必须携带 sessionId')
    if (this.identity === null) return reject('no-session', '尚未建立 context，拒绝操作消息')
    if (this.identityExpired) return reject('context-changed', '身份已因导航/关闭过期，需重新 context')
    if (sessionId !== this.identity.sessionId) {
      return reject('context-changed', `sessionId 与已绑定上下文不一致（${sessionId}）`)
    }
    return null
  }

  /** 释放某个批次的全部接收缓冲（cancel / batch-end / 超时）。 */
  private releaseBatch(batchId: string): void {
    for (const assembly of [...this.assemblies.values()]) {
      if (assembly.batchId === batchId) this.releaseAssembly(assembly, 'released')
    }
  }

  private releaseAssembly(assembly: Assembly, kind: 'released' | 'constructed'): void {
    const bytes = assembly.bytes
    // 先丢引用，再改记账：丢引用才是真的释放。
    assembly.parts.length = 0
    assembly.bytes = 0
    this.assemblies.delete(assembly.key)
    if (kind === 'released') {
      this.stats.releasedBytes += bytes
      this.stats.releasedFiles += 1
    }
  }

  /** 导航/身份变化：释放一切并丢弃批次状态（重放缓存也随之失效）。 */
  private releaseAll(): void {
    for (const assembly of [...this.assemblies.values()]) this.releaseAssembly(assembly, 'released')
    for (const entry of [...this.batches.values()]) entry.session.navigate()
    this.batches.clear()
    this.outbox.length = 0
    this.openBatchId = null
  }

  private dropPendingAcks(batchId: string): void {
    for (let index = this.outbox.length - 1; index >= 0; index -= 1) {
      const pending = this.outbox[index]
      if (pending !== undefined && pending.kind === 'ack' && pending.batchId === batchId) {
        this.outbox.splice(index, 1)
      }
    }
  }

  /** 保留批次有界：活动/未关闭批次被钉住，宁可持续超出也不丢活动状态。 */
  private evictRetained(): void {
    while (this.batches.size > this.maxRetainedBatches) {
      let victim: BatchEntry | null = null
      for (const entry of this.batches.values()) {
        if (entry.batchId === this.openBatchId) continue
        if (!entry.closed) continue
        if (victim === null || entry.lastActivityMs < victim.lastActivityMs) victim = entry
      }
      if (victim === null) break
      this.releaseBatch(victim.batchId)
      this.dropPendingAcks(victim.batchId)
      this.batches.delete(victim.batchId)
    }
  }

  private recordPeak(): void {
    const bytes = this.bufferedBytes
    if (bytes > this.stats.peakBufferedBytes) this.stats.peakBufferedBytes = bytes
    if (this.assemblies.size > this.stats.peakBufferedFiles) {
      this.stats.peakBufferedFiles = this.assemblies.size
    }
  }

  // ————————————————————————————————————————————————————————————
  // 结果构造
  // ————————————————————————————————————————————————————————————

  private async applyThrough(
    entry: BatchEntry,
    message: WireMessage,
    stage: ReceiverStage
  ): Promise<ReceiveResult> {
    const result = await entry.session.apply(message)
    entry.lastActivityMs = this.now()
    if (!result.ok) {
      this.stats.rejectedMessages += 1
      return this.rejectedFrom(result, stage)
    }
    return this.acceptedFrom(result, stage)
  }

  private rejectedFrom(result: ApplyResult, stage: ReceiverStage): ReceiveRejected {
    if (result.ok) throw new Error('内部错误：期望拒绝结果')
    this.stats.rejectedMessages += 1
    return { ...result, stage, bufferedBytes: this.bufferedBytes }
  }

  private rejectResult(stage: ReceiverStage, code: WireErrorCode, detail: string): ReceiveRejected {
    this.stats.rejectedMessages += 1
    return { ...reject(code, detail), stage, bufferedBytes: this.bufferedBytes }
  }

  private acceptedFrom(result: ApplyResult, stage: ReceiverStage): ReceiveAccepted {
    if (!result.ok) throw new Error('内部错误：期望接受结果')
    return {
      ok: true,
      stage,
      type: result.type,
      batchId: result.batchId,
      fileId: result.fileId,
      duplicate: result.duplicate,
      importInvoked: result.importInvoked,
      file: result.file,
      assembled: null,
      import: null,
      bufferedBytes: this.bufferedBytes
    }
  }

  private accepted(fields: Omit<ReceiveAccepted, 'ok' | 'bufferedBytes'>): ReceiveAccepted {
    return { ok: true, ...fields, bufferedBytes: this.bufferedBytes }
  }
}

/**
 * 页面全局承载（与 `__DSH_ATTACHMENTS_BRIDGE__` 同款）：
 * 传输层用 `create()` 造一个把组装结果直接喂给生产草稿路径的接收端。
 *
 * `create()` 的调用方只能覆盖"单次接收端"的选项（限额/时钟/crypto）；
 * 身份与能力相关的依赖（当前会话、来源、能力判定、构建标识）由组合层固定注入，
 * 传输层无法用参数把能力闸门绕过去。
 */
export interface ReceiverHost {
  version: number
  /** 已安装的组合依赖（只读诊断，便于证据断言"闸门真的接上了"）。 */
  readonly wiring: {
    readonly clientBuild: string
    readonly hasOrigin: boolean
    readonly hasCurrentSession: boolean
    readonly hasCapability: boolean
  }
  create(options?: Omit<ReceiverOptions, 'importFiles'> | undefined): AttachmentReceiver
}

/** 组合层注入的、传输层不可覆盖的依赖。 */
export interface ReceiverHostDeps {
  readonly importFiles: ReceiverImportFn
  readonly clientBuild?: string | undefined
  readonly limits?: Partial<WireLimits> | undefined
  readonly origin?: (() => OriginFacts) | undefined
  readonly currentSession?: (() => string | null) | undefined
  readonly capability?: (() => CapabilityResolution) | undefined
  readonly now?: (() => number) | undefined
}

/** 用给定的草稿导入函数构造宿主面。 */
export function createReceiverHost(deps: ReceiverHostDeps): ReceiverHost {
  return {
    version: RECEIVER_VERSION,
    wiring: {
      clientBuild: deps.clientBuild ?? ADDON_BUILD,
      hasOrigin: typeof deps.origin === 'function',
      hasCurrentSession: typeof deps.currentSession === 'function',
      hasCapability: typeof deps.capability === 'function'
    },
    create(options = {}) {
      return new AttachmentReceiver({
        importFiles: deps.importFiles,
        limits: options.limits ?? deps.limits,
        maxConcurrentAssemblies: options.maxConcurrentAssemblies,
        maxRetainedBatches: options.maxRetainedBatches,
        crypto: options.crypto,
        now: options.now ?? deps.now,
        replayCapacity: options.replayCapacity,
        replayLifetimeMs: options.replayLifetimeMs,
        clientBuild: deps.clientBuild,
        origin: deps.origin,
        currentSession: deps.currentSession,
        capability: deps.capability
      })
    }
  }
}

/** 直接构造接收端。 */
export function createAttachmentReceiver(options: ReceiverOptions): AttachmentReceiver {
  return new AttachmentReceiver(options)
}
