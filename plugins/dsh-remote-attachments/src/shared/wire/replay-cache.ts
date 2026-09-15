/**
 * 重放/去重缓存（D10 冻结语义）。
 *
 * 键至少包含 `documentEpoch` + `composerEpoch` + `batchId` + `fileId`（批级操作用空 fileId）。
 * 有容量与生命周期两个上限：容量淘汰**绝不驱逐活动操作**（钉住），因此活动操作不会
 * 因淘汰而被重新执行；查询时丢弃已过期条目，等价于"过期身份在进入缓冲前被拒绝"。
 */

import { REPLAY_CACHE_CAPACITY, REPLAY_CACHE_LIFETIME_MS } from '../protocol.js'

/** 缓存条目种类：整批（batch-end 收尾）或单文件。 */
export type ReplayEntryKind = 'batch' | 'file'

/** 条目状态：活动操作被钉住，完成后方可被容量淘汰。 */
export type ReplayEntryState = 'active' | 'completed'

/** 重放时可直接复用的结论摘要。 */
export interface ReplaySummary {
  /** 是否触发过一次草稿导入（重复到达必须为 false）。 */
  readonly importInvoked: boolean
  /** 该 fileId 最终确认的新增附件 ID。 */
  readonly attachmentIds: readonly string[]
  /** 该 fileId 的草稿状态。 */
  readonly draft: string
  /** 状态机最终判定。 */
  readonly status: string | null
}

export interface ReplayEntry {
  readonly kind: ReplayEntryKind
  readonly key: string
  readonly documentEpoch: number
  readonly composerEpoch: number
  readonly batchId: string
  readonly fileId: string
  /** 产生该结论的报文指纹；内容不同的重复即冲突。 */
  payloadDigest: string
  state: ReplayEntryState
  readonly createdAtMs: number
  expiresAtMs: number
  summary: ReplaySummary
}

export interface ReplayCacheOptions {
  readonly capacity?: number | undefined
  readonly lifetimeMs?: number | undefined
  readonly now?: (() => number) | undefined
}

/** 键格式对两端一致：`docEpoch:composerEpoch:batchId:fileId`。 */
export function replayKey(
  documentEpoch: number,
  composerEpoch: number,
  batchId: string,
  fileId: string
): string {
  return `${documentEpoch}:${composerEpoch}:${batchId}:${fileId}`
}

export class ReplayCache {
  private readonly entries = new Map<string, ReplayEntry>()
  private readonly capacity: number
  private readonly lifetimeMs: number
  private readonly now: () => number
  private evicted = 0
  private refusedEvictions = 0

  constructor(options: ReplayCacheOptions = {}) {
    this.capacity = options.capacity ?? REPLAY_CACHE_CAPACITY
    this.lifetimeMs = options.lifetimeMs ?? REPLAY_CACHE_LIFETIME_MS
    this.now = options.now ?? (() => Date.now())
  }

  /** 未过期条目数；查询时顺带丢弃已过期条目（生命周期是硬上限）。 */
  get size(): number {
    this.purgeExpired()
    return this.entries.size
  }

  private purgeExpired(): void {
    const now = this.now()
    for (const [key, entry] of this.entries) if (entry.expiresAtMs <= now) this.entries.delete(key)
  }

  /** 被钉住（活动）的条目数。 */
  get pinnedCount(): number {
    let pinned = 0
    for (const entry of this.entries.values()) if (entry.state === 'active') pinned += 1
    return pinned
  }

  get evictedCount(): number {
    return this.evicted
  }

  /** 容量已满且无可淘汰条目时拒绝淘汰的次数（钉住条目撑住容量）。 */
  get refusedEvictionCount(): number {
    return this.refusedEvictions
  }

  /** 读取未过期条目；过期即删除。 */
  get(key: string): ReplayEntry | undefined {
    const entry = this.entries.get(key)
    if (!entry) return undefined
    if (entry.expiresAtMs <= this.now()) {
      this.entries.delete(key)
      return undefined
    }
    return entry
  }

  /** 写入/覆盖条目；必要时按容量淘汰最老的**已完成**条目。 */
  put(entry: ReplayEntry): ReplayEntry {
    if (!this.entries.has(entry.key) && this.entries.size >= this.capacity) this.evictOne()
    this.entries.set(entry.key, entry)
    return entry
  }

  /** 标记完成：解除钉住并顺延生命周期。 */
  complete(key: string, summary: ReplaySummary, payloadDigest: string): ReplayEntry | undefined {
    const entry = this.entries.get(key)
    if (!entry) return undefined
    entry.state = 'completed'
    entry.summary = summary
    entry.payloadDigest = payloadDigest
    entry.expiresAtMs = this.now() + this.lifetimeMs
    return entry
  }

  /** 删除单个键。 */
  delete(key: string): void {
    this.entries.delete(key)
  }

  /** 清空（导航/关闭）。 */
  clear(): void {
    this.entries.clear()
  }

  private evictOne(): void {
    for (const [key, entry] of this.entries) {
      if (entry.state === 'active') continue
      this.entries.delete(key)
      this.evicted += 1
      return
    }
    // 全部被钉住：宁可持续超出容量，也不让活动操作失去去重保护。
    this.refusedEvictions += 1
  }
}

/** 创建条目（活动状态，生命周期从创建时刻起算）。 */
export function makeReplayEntry(params: {
  readonly kind: ReplayEntryKind
  readonly documentEpoch: number
  readonly composerEpoch: number
  readonly batchId: string
  readonly fileId: string
  readonly payloadDigest: string
  readonly summary: ReplaySummary
  readonly now: number
  readonly lifetimeMs?: number | undefined
}): ReplayEntry {
  const lifetime = params.lifetimeMs ?? REPLAY_CACHE_LIFETIME_MS
  return {
    kind: params.kind,
    key: replayKey(params.documentEpoch, params.composerEpoch, params.batchId, params.fileId),
    documentEpoch: params.documentEpoch,
    composerEpoch: params.composerEpoch,
    batchId: params.batchId,
    fileId: params.fileId,
    payloadDigest: params.payloadDigest,
    state: 'active',
    createdAtMs: params.now,
    expiresAtMs: params.now + lifetime,
    summary: params.summary
  }
}
