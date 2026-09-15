/**
 * D21 判据：背压与资源清理边界的**冻结参数表 + 采样方式 + 纯判定器**。
 *
 * 存在理由（D21 验收："不靠无限队列追求速度；连续操作后保留资源无单调无界增长"）：
 * 判据必须先冻结，再采样。本模块是那个"先冻结"的产物：
 *
 *   1. `METRICS` 是**冻结表**：每条计量项写明 单位（unit）、采样点（samplingPoint）、
 *      通过阈值（threshold + comparison + thresholdValue）、场景（scenario）、层级（layer）。
 *      采样脚本只能按 id 填报实测值，不得在采样处改写阈值。
 *   2. 阈值**优先引用产品常量**（`DEFAULT_LIMITS` / `MAX_CHUNKS_IN_FLIGHT` /
 *      `REPLAY_CACHE_CAPACITY` / `REPLAY_CACHE_LIFETIME_MS`），不另抄一份数字；
 *      只有产品没有显式常量的地方（例如 30 轮序列的增长斜率）才在 `LIMITS` 里冻结新数字。
 *   3. 判定与采集分离：`compare` / `judgeSeries` / `falsifiabilitySelfTest` 都是纯函数，
 *      由 `test/unit/d21-resource-criteria.test.mjs` 单测覆盖（含反例）。
 *   4. `runDedupProbes()` 用**已构建产物** `lib/` 里的 `ReplayCache` + 注入时钟做确定性
 *      过期/容量探测——不需要 sleep 120 秒，也不依赖真实时间。
 *
 * 采样点写法约定：`<层级>:<可复现的读取位置>`。层级为 `browser`（真实 Chromium 里的
 * 已构建插件）、`core`（真实可移植 Core，C# 用例写出的指标）或 `unit`（Node 22 对
 * 已构建 `lib/` 的确定性探测）。
 */

import {
  CHUNK_BYTES,
  DEFAULT_LIMITS,
  MAX_CHUNKS_IN_FLIGHT,
  REPLAY_CACHE_CAPACITY,
  REPLAY_CACHE_LIFETIME_MS
} from '../../lib/shared/protocol.js'
import { ReplayCache, makeReplayEntry } from '../../lib/shared/wire/replay-cache.js'

/** 判据版本：任何阈值/采样点改动都必须提升它（证据里逐次记录）。 */
export const D21_CRITERIA_VERSION = 'd21-resources-v1'

// ---------- 产品常量（阈值的事实来源；这里只是取别名，不复制数值） ----------

/** 与产品常量对齐的冻结限制。 */
export const LIMITS = Object.freeze({
  /** 单块原始字节数。 */
  chunkBytes: CHUNK_BYTES,
  /** 每文件在途未确认块数上限（2 块窗口）。 */
  maxChunksInFlight: MAX_CHUNKS_IN_FLIGHT,
  /** 单文件最大字节数。 */
  maxFileBytes: DEFAULT_LIMITS.maxFileBytes,
  /** 单批最大文件数。 */
  maxFilesPerBatch: DEFAULT_LIMITS.maxFilesPerBatch,
  /** 单批最大字节数。 */
  maxBatchBytes: DEFAULT_LIMITS.maxBatchBytes,
  /** 单目标暂存上限。 */
  maxStagingBytesPerTarget: DEFAULT_LIMITS.maxStagingBytesPerTarget,
  /** 目标并发上限。 */
  maxConcurrentTargets: DEFAULT_LIMITS.maxConcurrentTargets,
  /** 浏览器接收端默认并发组装上限（`ReceiverOptions.maxConcurrentAssemblies ?? 2`）。 */
  maxConcurrentAssemblies: 2,
  /** 浏览器接收端默认保留批次数（`ReceiverOptions.maxRetainedBatches ?? 16`）。 */
  maxRetainedBatches: 16,
  /** 去重缓存容量。 */
  replayCapacity: REPLAY_CACHE_CAPACITY,
  /** 去重缓存生命周期（毫秒）。 */
  replayLifetimeMs: REPLAY_CACHE_LIFETIME_MS,
  /** Core 全局并发闸门等待队列容量（`AttachmentConcurrencyGate.DefaultQueueCapacity`）。 */
  gateQueueCapacity: 32,
  /** Core 全局并发上限。 */
  gateMaxConcurrent: DEFAULT_LIMITS.maxConcurrentTargets,
  /** Core 未确认字节上限（`AttachmentTransferCoordinator.MaxPendingBytes`）。 */
  maxPendingBytes: MAX_CHUNKS_IN_FLIGHT * CHUNK_BYTES,
  /** 连续 30 轮序列：增长斜率上界（字节/轮）。 */
  seriesSlopeMaxBytesPerRound: 64 * 1024,
  /** 连续 30 轮序列：包络（max-min）上界（字节）——独立采样点（专用页面/隔离进程）用。 */
  seriesEnvelopeMaxBytes: 8 * 1024 * 1024,
  /** 共享 MTP 进程里托管堆包络的粗界（字节）：按实测噪声（11.2 MB）留余量。 */
  seriesEnvelopeSharedProcessMaxBytes: 16 * 1024 * 1024,
  /** 30 轮序列长度（D21 卡片要求"连续 30 轮"）。 */
  seriesRounds: 30
})

/** 比较算子。 */
export const COMPARISONS = Object.freeze({ LTE: 'lte', EQ: 'eq', GTE: 'gte' })

const L = LIMITS

/**
 * 冻结的计量项表。
 *
 * 说明：`role` 为 `negative-control` 的行是"必须被拒绝"的反向探针（超限必须被拒），
 * 它们同样是阈值判定的一部分，不会被当成"正向通过"计数。
 */
export const METRICS = Object.freeze([
  // ---------------- 浏览器接收端：最大文件 / 最大批次 ----------------
  {
    id: 'B01-max-file-peak-buffered-bytes',
    layer: 'browser',
    scenario: 'max-file',
    metric: 'receiver accounting peakBufferedBytes（20 MiB 单文件组装期峰值）',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("maxfile").peakBufferedBytes（真实 Chromium 中已构建插件的接收端记账）',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxFileBytes,
    threshold: `<= ${L.maxFileBytes} bytes（maxFileBytes：峰值不得超过"单文件原始字节数"）`
  },
  {
    id: 'B02-max-file-buffered-after',
    layer: 'browser',
    scenario: 'max-file',
    metric: 'receiver accounting bufferedBytes（20 MiB 单文件导入完成后）',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("maxfile").bufferedBytes（同一场景结束时）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 bytes（File 构造后自有缓冲必须全部释放）'
  },
  {
    id: 'B03-max-file-ack-inflight-peak',
    layer: 'browser',
    scenario: 'max-file',
    metric: 'ack.inFlight 峰值（接收端对 20 MiB 文件逐块回报的在途窗口读数）',
    unit: 'chunks',
    samplingPoint: 'browser:每一次 ack 的 inFlight 字段（__D21__.drain() 返回的待发帧）',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxChunksInFlight,
    threshold: `<= ${L.maxChunksInFlight} chunks（maxChunksInFlight：2 块窗口）`
  },
  {
    id: 'B04-max-file-ack-per-chunk',
    layer: 'browser',
    scenario: 'max-file',
    metric: '每个被接受的块恰好一条 ack（acks / accepted chunks）',
    unit: 'acks/chunk',
    samplingPoint: 'browser:__D21__.drain() 的 ack 条数 ÷ 被接收端接受的 chunk 条数',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1 ack/chunk（不多不少：ack 只证明缓冲已接受）'
  },
  {
    id: 'B05-max-batch-peak-buffered-files',
    layer: 'browser',
    scenario: 'max-batch',
    metric: 'receiver accounting peakBufferedFiles（10 文件批次组装期峰值）',
    unit: 'files',
    samplingPoint: 'browser:page __D21__.accounting("maxbatch").peakBufferedFiles',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxConcurrentAssemblies,
    threshold: `<= ${L.maxConcurrentAssemblies} files（接收端并发组装上限，第二道防线）`
  },
  {
    id: 'B06-max-batch-peak-buffered-bytes',
    layer: 'browser',
    scenario: 'max-batch',
    metric: 'receiver accounting peakBufferedBytes（10 文件批次组装期峰值）',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("maxbatch").peakBufferedBytes',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxBatchBytes,
    threshold: `<= ${L.maxBatchBytes} bytes（maxBatchBytes：峰值不得超过单批上限）`
  },
  {
    id: 'B07-over-batch-rejected',
    layer: 'browser',
    scenario: 'max-batch',
    metric: '11 文件（超过 maxFilesPerBatch=10）的 batch-begin 被确定拒绝',
    unit: 'boolean(0/1)',
    samplingPoint: 'browser:page __D21__.overBatch() 的拒绝码（必须非 ok）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（超限必须被拒绝，不得进入缓冲）',
    role: 'negative-control'
  },
  {
    id: 'B08-over-file-rejected',
    layer: 'browser',
    scenario: 'max-file',
    metric: 'maxFileBytes+1 的 file-begin 被确定拒绝',
    unit: 'boolean(0/1)',
    samplingPoint: 'browser:page __D21__.overFile() 的拒绝码（必须非 ok）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（超限必须被拒绝，不得进入缓冲）',
    role: 'negative-control'
  },
  // ---------------- 浏览器接收端：取消 / 释放 / 暂停消费者 ----------------
  {
    id: 'B09-cancel-buffered-bytes',
    layer: 'browser',
    scenario: 'cancel',
    metric: 'receiver accounting bufferedBytes（传输中途 cancel 之后）',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("cancel").bufferedBytes（cancel 帧被接受之后）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 bytes（取消必须回收全部接收缓冲）'
  },
  {
    id: 'B10-cancel-released-bytes',
    layer: 'browser',
    scenario: 'cancel',
    metric: 'receiver accounting releasedBytes（取消时记账的已释放字节）',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("cancel").releasedBytes',
    comparison: COMPARISONS.GTE,
    thresholdValue: 2 * CHUNK_BYTES,
    threshold: `>= ${2 * CHUNK_BYTES} bytes（取消前确实缓冲了 2 块，释放必须被记账）`
  },
  {
    id: 'B11-dispose-retained-after',
    layer: 'browser',
    scenario: 'cancel',
    metric: 'receiver dispose() 之后 retainedBatches / bufferedFiles',
    unit: 'entries',
    samplingPoint: 'browser:page __D21__.accounting("cancel")（dispose 之后同一路径再读一次）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 entries（撤销后不得保留任何批次状态或缓冲引用）'
  },
  {
    id: 'B12-paused-consumer-outbox-entries',
    layer: 'browser',
    scenario: 'paused-consumer',
    metric: '消费者暂停（不 drain）期间接收端累积的待发 ack 条目数（恢复时一次 drain 取出的 ack 条数）',
    unit: 'entries',
    samplingPoint: 'browser:page __D21__.resumeAndCount("paused")（暂停期间喂 3 块、中途不 drain）',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxChunksInFlight,
    threshold: `<= ${L.maxChunksInFlight} entries（maxChunksInFlight：ack 只在 drain 时投递且同时解除窗口，因此暂停期间至多积压一个窗口）`
  },
  {
    id: 'B13-paused-consumer-buffered-bytes',
    layer: 'browser',
    scenario: 'paused-consumer',
    metric: '消费者暂停期间被接受的累计缓冲字节',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("paused").bufferedBytes（第 3 块被拒之后）',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxChunksInFlight * L.chunkBytes,
    threshold: `<= ${L.maxChunksInFlight * L.chunkBytes} bytes（2 块窗口：暂停不会把缓冲推向暂存上限）`
  },
  {
    id: 'B14-paused-consumer-overrun-rejected',
    layer: 'browser',
    scenario: 'paused-consumer',
    metric: '暂停期间第 3 块被拒绝且拒绝码为 window-overflow（不得缓冲）',
    unit: 'boolean(0/1)',
    samplingPoint: 'browser:page __D21__.pausedOverrun("paused") 的拒绝码',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（超过在途窗口必须拒绝）',
    role: 'negative-control'
  },
  // ---------------- 浏览器接收端：连续 30 轮（无单调无界增长） ----------------
  {
    id: 'B15-series-heap-slope',
    layer: 'browser',
    scenario: 'series-30',
    metric: 'JS 堆在用字节的逐轮序列（30 轮，强制 GC 后采样）最小二乘斜率',
    unit: 'bytes/round',
    samplingPoint: 'browser:每轮结束后 CDP HeapProfiler.collectGarbage → Runtime.getHeapUsage().usedSize',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.seriesSlopeMaxBytesPerRound,
    threshold: `<= ${L.seriesSlopeMaxBytesPerRound} bytes/round（30 轮累计 ≤ 1.9 MiB；泄漏路径见负向控制 N3）`
  },
  {
    id: 'B16-series-heap-envelope',
    layer: 'browser',
    scenario: 'series-30',
    metric: '同一 30 轮序列的包络 max-min',
    unit: 'bytes',
    samplingPoint: 'browser:同上序列',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.seriesEnvelopeMaxBytes,
    threshold: `<= ${L.seriesEnvelopeMaxBytes} bytes（有界抖动；无界增长会同时触发斜率与包络）`
  },
  {
    id: 'B17-series-buffered-bytes-after',
    layer: 'browser',
    scenario: 'series-30',
    metric: '30 轮结束后接收缓冲残留',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("series").bufferedBytes（第 30 轮导入完成后）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 bytes（连续操作不得留下缓冲）'
  },
  {
    id: 'B18-series-retained-batches',
    layer: 'browser',
    scenario: 'series-30',
    metric: '30 轮结束后保留批次数',
    unit: 'batches',
    samplingPoint: 'browser:page __D21__.accounting("series").retainedBatches',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxRetainedBatches,
    threshold: `<= ${L.maxRetainedBatches} batches（maxRetainedBatches：保留批次有界，不随轮数增长）`
  },
  {
    id: 'B20-series-receiver-only-slope',
    layer: 'browser',
    scenario: 'series-30-receiver-only',
    metric: '接收端独占 30 轮（每轮 2 块后 cancel、不进草稿）的 JS 内存逐轮序列斜率',
    unit: 'bytes/round',
    samplingPoint: 'browser:每轮 cancel+drain 后 CDP HeapProfiler.collectGarbage → Runtime.getHeapUsage()（usedSize + backingStorageSize）',
    comparison: COMPARISONS.LTE,
    // 设计上界：接收端独占路径 30 轮累计不得超过 1 MiB（真实导入序列另有一条含产品状态的阈值）。
    thresholdValue: 32 * 1024,
    threshold: `<= ${32 * 1024} bytes/round（30 轮累计 ≤ 0.94 MiB：接收端/传输层自身不得随轮数增长）`
  },
  {
    id: 'B19-series-released-bytes',
    layer: 'browser',
    scenario: 'series-30',
    metric: '30 轮累计 constructedBytes（每轮文件构造后接收侧缓冲即释放的字节）',
    unit: 'bytes',
    samplingPoint: 'browser:page __D21__.accounting("series").constructedBytes（30 轮 × 4 KiB）',
    comparison: COMPARISONS.GTE,
    thresholdValue: L.seriesRounds * 4096,
    threshold: `>= ${L.seriesRounds * 4096} bytes（记账非空：30 轮确实走完构造+释放，否则"归零"可能是空检查）`
  },
  // ---------------- 去重缓存（已构建 lib/ + 注入时钟，确定性） ----------------
  {
    id: 'U01-dedup-ttl-live-before',
    layer: 'unit',
    scenario: 'dedup',
    metric: 'TTL 前 1ms 的条目仍在缓存中',
    unit: 'boolean(0/1)',
    samplingPoint: 'unit:lib/shared/wire/replay-cache.js ReplayCache.get（注入时钟 now += lifetimeMs-1）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（未到期不得提前失效）'
  },
  {
    id: 'U02-dedup-ttl-gone-at',
    layer: 'unit',
    scenario: 'dedup',
    metric: 'TTL 到期的条目被丢弃（不再具备去重保护）',
    unit: 'boolean(0/1)',
    samplingPoint: 'unit:同上，注入时钟再 +1ms',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（到期必须失效）'
  },
  {
    id: 'U03-dedup-capacity-bound',
    layer: 'unit',
    scenario: 'dedup',
    metric: '写入 4×capacity 个已完成条目后的缓存大小',
    unit: 'entries',
    samplingPoint: 'unit:ReplayCache.size（capacity=8，写 32 个已完成条目）',
    comparison: COMPARISONS.LTE,
    thresholdValue: 8,
    threshold: '<= 8 entries（capacity：容量淘汰必须生效）'
  },
  {
    id: 'U04-dedup-capacity-evicted',
    layer: 'unit',
    scenario: 'dedup',
    metric: '容量淘汰确实发生（evictedCount）',
    unit: 'entries',
    samplingPoint: 'unit:ReplayCache.evictedCount',
    comparison: COMPARISONS.GTE,
    thresholdValue: 1,
    threshold: '>= 1 entries（否则"有界"是因为什么都没写进去）'
  },
  {
    id: 'U05-dedup-pinned-survives',
    layer: 'unit',
    scenario: 'dedup',
    metric: '活动（钉住）条目在容量压力下不被驱逐',
    unit: 'boolean(0/1)',
    samplingPoint: 'unit:ReplayCache.get(pinnedKey) 在写入超过容量之后仍非空',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（活动操作绝不因淘汰而失去去重保护）'
  },
  {
    id: 'U06-dedup-pinned-refusal-counted',
    layer: 'unit',
    scenario: 'dedup',
    metric: '全部钉住时的拒绝淘汰被计数（超额是显式且可观测的）',
    unit: 'boolean(0/1)',
    samplingPoint: 'unit:ReplayCache.refusedEvictionCount > 0',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（"宁可超出容量"这件事必须被记账，不是静默无界）'
  },
  // ---------------- Core：慢 ACK / 暂停消费者 / 取消 ----------------
  {
    id: 'C01-pending-bytes-peak',
    layer: 'core',
    scenario: 'slow-ack',
    metric: 'AttachmentTransferCoordinator.PendingBytes 峰值（对端慢 ACK）',
    unit: 'bytes',
    samplingPoint: 'core:每一次 Pump() 之后读 PendingBytes（C# 用例 ResourceBoundaryTests）',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxPendingBytes,
    threshold: `<= ${L.maxPendingBytes} bytes（MaxPendingBytes = maxChunksInFlight × chunkBytes）`
  },
  {
    id: 'C02-pending-chunks-peak',
    layer: 'core',
    scenario: 'slow-ack',
    metric: 'PendingChunks 峰值（在途未确认块数）',
    unit: 'chunks',
    samplingPoint: 'core:每一次 Pump() 之后读 PendingChunks',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.maxChunksInFlight,
    threshold: `<= ${L.maxChunksInFlight} chunks（2 块窗口）`
  },
  {
    id: 'C03-slow-ack-progress-per-ack',
    layer: 'core',
    scenario: 'slow-ack',
    metric: '每释放 1 条 ack 后新增发送的块数（慢 ACK 逐条放行）',
    unit: 'chunks/ack',
    samplingPoint: 'core:逐条投递 ack 前后 Sent 计数差的最大值',
    comparison: COMPARISONS.LTE,
    thresholdValue: 1,
    threshold: '<= 1 chunks/ack（一条 ack 只放行一块，窗口不会被绕过）'
  },
  {
    id: 'C04-gate-waiting-peak',
    layer: 'core',
    scenario: 'paused-consumer',
    metric: 'AttachmentConcurrencyGate.WaitingCount 峰值（消费者暂停）',
    unit: 'entries',
    samplingPoint: 'core:每次 TryAcquire/Release 之后读 WaitingCount（直接驱动闸门）',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.gateQueueCapacity,
    threshold: `<= ${L.gateQueueCapacity} entries（DefaultQueueCapacity：有界等待队列）`
  },
  {
    id: 'C05-gate-refused-on-overflow',
    layer: 'core',
    scenario: 'paused-consumer',
    metric: '队列满时的准入被拒绝（RefusedCount）',
    unit: 'entries',
    samplingPoint: 'core:闸门 RefusedCount（第 capacity+1 个等待者）',
    comparison: COMPARISONS.GTE,
    thresholdValue: 1,
    threshold: '>= 1 entries（满队列必须拒绝，不得无界排队）',
    role: 'negative-control'
  },
  {
    id: 'C06-gate-active-peak',
    layer: 'core',
    scenario: 'paused-consumer',
    metric: 'ActiveCount 峰值（全局并发）',
    unit: 'targets',
    samplingPoint: 'core:闸门 ActiveCount',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.gateMaxConcurrent,
    threshold: `<= ${L.gateMaxConcurrent} targets（maxConcurrentTargets）`
  },
  {
    id: 'C07-cancel-pending-bytes',
    layer: 'core',
    scenario: 'cancel',
    metric: 'Cancel() 之后的 PendingBytes',
    unit: 'bytes',
    samplingPoint: 'core:Cancel() 之后读 PendingBytes',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 bytes（取消必须清空在途窗口）'
  },
  {
    id: 'C08-cancel-pending-chunks',
    layer: 'core',
    scenario: 'cancel',
    metric: 'Cancel() 之后的 PendingChunks',
    unit: 'chunks',
    samplingPoint: 'core:Cancel() 之后读 PendingChunks',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 chunks（窗口内不得残留已发未确认块）'
  },
  {
    id: 'C09-cancel-sources-disposed',
    layer: 'core',
    scenario: 'cancel',
    metric: '取消后已释放的字节源数 = 已准入文件数',
    unit: 'files',
    samplingPoint: 'core:每个 ScriptedByteSource.Disposed 计数（所有权策略）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1 比例（每个被准入的源必须恰好被释放一次：不得泄漏、不得重复释放）',
    ratioOf: ['disposed', 'admitted']
  },
  {
    id: 'C10-dedup-ttl-live-before',
    layer: 'core',
    scenario: 'dedup',
    metric: 'Core 去重台账：TTL 前 1ms 条目仍在',
    unit: 'boolean(0/1)',
    samplingPoint: 'core:注入时钟推进 lifetimeMs-1 后 ReplayCacheSize/条目可读性',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（未到期不得提前失效）'
  },
  {
    id: 'C11-dedup-ttl-gone-at',
    layer: 'core',
    scenario: 'dedup',
    metric: 'Core 去重台账：TTL 到期后条目消失',
    unit: 'boolean(0/1)',
    samplingPoint: 'core:注入时钟再 +1ms 后 ReplayCacheSize',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（到期必须失效；过期判定不依赖真实时间）'
  },
  {
    id: 'C12-dedup-capacity-bound',
    layer: 'core',
    scenario: 'dedup',
    metric: '多批次后 ReplayCacheSize 上界',
    unit: 'entries',
    samplingPoint: 'core:30 轮之后的 ReplayCacheSize（capacity 由用例注入为小数以便观察）',
    comparison: COMPARISONS.LTE,
    thresholdValue: 8,
    threshold: '<= 8 entries（用例注入的容量：缓存大小不随批次数增长）'
  },
  // ---------------- Core：连续 30 轮 + 清理所有权 ----------------
  {
    id: 'C13-series-heap-slope',
    layer: 'core',
    scenario: 'series-30',
    metric: '托管堆在用字节的逐轮序列（30 轮，GC.GetTotalMemory(true) 后采样）斜率',
    unit: 'bytes/round',
    samplingPoint: 'core:隔离的 D21 用例集进程里，每轮批次完成后 GC.Collect + WaitForPendingFinalizers + GetTotalMemory(true) 连读 3 次取最小',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.seriesSlopeMaxBytesPerRound,
    threshold: `<= ${L.seriesSlopeMaxBytesPerRound} bytes/round（30 轮累计 ≤ 1.9 MiB）`
  },
  {
    id: 'C14-series-heap-envelope',
    layer: 'core',
    scenario: 'series-30',
    metric: '同一 30 轮托管堆序列的包络 max-min（共享 MTP 进程）',
    unit: 'bytes',
    samplingPoint: 'core:同上序列（隔离的 D21 用例集进程；共享全量跑会混入其余 540 例的活动对象，故紧判据只在隔离采样点成立）',
    comparison: COMPARISONS.LTE,
    // 共享进程噪声实测：隔离跑 0.75 MB、全量跑 11.2 MB（三次取最小后）。因此这一行按 16 MiB 粗界，
    // 紧包络（8 MiB）由夹具对**隔离进程**里同一条序列另判一次（控制 N13）。斜率 C13 才是泄漏敏感判据。
    thresholdValue: L.seriesEnvelopeSharedProcessMaxBytes,
    threshold: `<= ${L.seriesEnvelopeSharedProcessMaxBytes} bytes（共享测试进程的实测噪声上界；紧包络见控制 N13）`
  },
  {
    id: 'C15-series-sources-disposed',
    layer: 'core',
    scenario: 'series-30',
    metric: '30 轮中被释放的字节源数',
    unit: 'files',
    samplingPoint: 'core:每轮 ScriptedByteSource.Disposed 累计',
    comparison: COMPARISONS.EQ,
    thresholdValue: L.seriesRounds,
    threshold: `= ${L.seriesRounds} files（每轮的源都必须被释放，成功路径同样如此）`
  },
  {
    id: 'C16-series-replay-cache-size',
    layer: 'core',
    scenario: 'series-30',
    metric: '30 轮之后 ReplayCacheSize',
    unit: 'entries',
    samplingPoint: 'core:第 30 轮之后读 ReplayCacheSize',
    comparison: COMPARISONS.LTE,
    thresholdValue: L.replayCapacity,
    threshold: `<= ${L.replayCapacity} entries（去重缓存容量不随轮数增长）`
  },
  {
    id: 'C17-dispose-gate-slots',
    layer: 'core',
    scenario: 'dispose',
    metric: 'Dispose() 之后全局闸门的活动槽位数',
    unit: 'targets',
    samplingPoint: 'core:Dispose() 之后读 Gate.ActiveCount / WaitingCount',
    comparison: COMPARISONS.EQ,
    thresholdValue: 0,
    threshold: '= 0 targets（清理所有权：释放后不得继续占用全局并发槽位）'
  },
  {
    id: 'C18-dispose-idempotent',
    layer: 'core',
    scenario: 'dispose',
    metric: '重复 Dispose() 不产生第二次释放副作用（幂等）',
    unit: 'boolean(0/1)',
    samplingPoint: 'core:第二次 Dispose() 之后再次读 Gate.ActiveCount 与源释放次数',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1（幂等：重复释放不得改变已释放状态）'
  },
  {
    id: 'C19-dispose-queued-released',
    layer: 'core',
    scenario: 'dispose',
    metric: 'Dispose() 时仍在 pending/transferring 的文件全部被释放',
    unit: 'files',
    samplingPoint: 'core:Dispose() 后每个源 Disposed 计数（含排队中未开始的源）',
    comparison: COMPARISONS.EQ,
    thresholdValue: 1,
    threshold: '= 1 比例（排队中的源同样属协调器所有，必须释放）',
    ratioOf: ['disposed', 'admitted']
  }
])

/** 按 id 取冻结定义；未知 id 直接抛（防止采样脚本写错计量项还"通过"）。 */
export function metricById(id) {
  const found = METRICS.find((item) => item.id === id)
  if (found === undefined) throw new Error(`未知的 D21 计量项 id：${id}`)
  return found
}

/** 比较算子实现（`measured` 必须是有穷数，否则一律不通过）。 */
export function compare(comparison, measured, thresholdValue) {
  if (typeof measured !== 'number' || !Number.isFinite(measured)) return false
  switch (comparison) {
    case COMPARISONS.LTE:
      return measured <= thresholdValue
    case COMPARISONS.GTE:
      return measured >= thresholdValue
    case COMPARISONS.EQ:
      return measured === thresholdValue
    default:
      throw new Error(`未知比较算子：${comparison}`)
  }
}

/**
 * 荒谬阈值：对任何有穷实测值都必然不成立。
 * 用途是"这条断言真的能失败吗"的自检（见 `falsifiabilitySelfTest`）。
 */
export function absurdThreshold(comparison, measured) {
  switch (comparison) {
    case COMPARISONS.LTE:
      return measured - 1
    case COMPARISONS.GTE:
      return measured + 1
    case COMPARISONS.EQ:
      return measured + 1
    default:
      throw new Error(`未知比较算子：${comparison}`)
  }
}

/**
 * 生成一行报告记录。
 *
 * `measured` 为 `null` 表示**未采样**（脚本没跑到）；这时 pass=false 且 `notRun=true`，
 * 绝不允许把"没测"写成通过。
 */
export function buildRow(id, measured, extra = {}) {
  const definition = metricById(id)
  const notRun = measured === null || measured === undefined
  const pass = !notRun && compare(definition.comparison, measured, definition.thresholdValue)
  return {
    id: definition.id,
    layer: definition.layer,
    scenario: extra.scenario ?? definition.scenario,
    metric: definition.metric,
    unit: definition.unit,
    samplingPoint: definition.samplingPoint,
    comparison: definition.comparison,
    threshold: definition.threshold,
    thresholdValue: definition.thresholdValue,
    measured: notRun ? null : measured,
    pass,
    notRun,
    role: definition.role ?? 'bound',
    ...(extra.detail === undefined ? {} : { detail: extra.detail }),
    ...(extra.raw === undefined ? {} : { raw: extra.raw })
  }
}

/** 比例型计量项：`ratioOf: [分子, 分母]`，两者都由采样给出。 */
export function buildRatioRow(id, numerator, denominator) {
  const definition = metricById(id)
  if (typeof numerator !== 'number' || typeof denominator !== 'number' || denominator <= 0) {
    return { ...buildRow(id, null), notRun: true, detail: `比例不可计算：${numerator}/${denominator}` }
  }
  return buildRow(id, numerator / denominator, { raw: { numerator, denominator } })
}

// ---------- 序列分析（"无单调无界增长"的可判定形式） ----------

/** 最小二乘斜率（每个采样步的增量）。 */
export function leastSquaresSlope(series) {
  const n = series.length
  if (n < 2) return 0
  const meanX = (n - 1) / 2
  const meanY = series.reduce((sum, value) => sum + value, 0) / n
  let numerator = 0
  let denominator = 0
  for (let index = 0; index < n; index += 1) {
    numerator += (index - meanX) * (series[index] - meanY)
    denominator += (index - meanX) ** 2
  }
  return denominator === 0 ? 0 : numerator / denominator
}

/** 序列的完整读数：斜率、包络、极值、逐轮递增次数。 */
export function analyzeSeries(series) {
  const min = Math.min(...series)
  const max = Math.max(...series)
  let increases = 0
  for (let index = 1; index < series.length; index += 1) if (series[index] > series[index - 1]) increases += 1
  return {
    rounds: series.length,
    first: series[0],
    last: series[series.length - 1],
    min,
    max,
    envelope: max - min,
    slopeBytesPerRound: leastSquaresSlope(series),
    increases,
    decreases: series.length - 1 - increases,
    series: [...series]
  }
}

/** 判定一个"内存/资源序列"是否存在无界增长：斜率与包络都必须有界。 */
export function judgeSeries(series, options = {}) {
  const slopeMax = options.slopeMaxBytesPerRound ?? LIMITS.seriesSlopeMaxBytesPerRound
  const envelopeMax = options.envelopeMaxBytes ?? LIMITS.seriesEnvelopeMaxBytes
  const analysis = analyzeSeries(series)
  const reasons = []
  if (analysis.rounds < (options.minRounds ?? 2)) reasons.push(`轮数不足：${analysis.rounds}`)
  if (!(analysis.slopeBytesPerRound <= slopeMax)) {
    reasons.push(`斜率 ${analysis.slopeBytesPerRound.toFixed(1)} B/轮 > ${slopeMax}`)
  }
  if (!(analysis.envelope <= envelopeMax)) {
    reasons.push(`包络 ${analysis.envelope} B > ${envelopeMax}`)
  }
  return { pass: reasons.length === 0, reasons, analysis }
}

// ---------- 反例控制 ----------

/**
 * 逐行自检：把阈值替换成"必然不成立"的荒谬值，判定必须翻转成 false。
 * 若某行在荒谬阈值下仍然通过，说明这条断言恒真（不是判据）。
 */
export function falsifiabilitySelfTest(rows) {
  const checked = rows.map((row) => {
    const finite = typeof row.measured === 'number' && Number.isFinite(row.measured)
    const absurd = finite ? absurdThreshold(row.comparison, row.measured) : null
    const passWithAbsurd = finite ? compare(row.comparison, row.measured, absurd) : true
    return {
      id: row.id,
      comparison: row.comparison,
      measured: row.measured,
      absurdThreshold: absurd,
      flippedToFail: finite && passWithAbsurd === false
    }
  })
  return { ok: checked.every((item) => item.flippedToFail), checked }
}

/**
 * 序列判定的反例控制：
 *   N3a 无界路径（每轮保留 +1 MiB）必须被判失败；
 *   N3b 缓慢泄漏（每轮 +80 KiB，低于包络但高于斜率阈值）必须被判失败；
 *   N3c 有界抖动必须被判通过（否则判定器恒假，判据没有信息量）。
 */
export function runSeriesNegativeControls(options = {}) {
  const slopeMax = options.slopeMaxBytesPerRound ?? LIMITS.seriesSlopeMaxBytesPerRound
  const envelopeMax = options.envelopeMaxBytes ?? LIMITS.seriesEnvelopeMaxBytes
  const base = 12 * 1024 * 1024
  const unbounded = Array.from({ length: 30 }, (_, index) => base + index * 1024 * 1024)
  const slowLeak = Array.from({ length: 30 }, (_, index) => base + index * 80 * 1024)
  const bounded = Array.from({ length: 30 }, (_, index) => base + (index % 2 === 0 ? 0 : 512 * 1024) + (index % 3) * 64 * 1024)
  const unboundedVerdict = judgeSeries(unbounded, { slopeMaxBytesPerRound: slopeMax, envelopeMaxBytes: envelopeMax })
  const slowLeakVerdict = judgeSeries(slowLeak, { slopeMaxBytesPerRound: slopeMax, envelopeMaxBytes: envelopeMax })
  const boundedVerdict = judgeSeries(bounded, { slopeMaxBytesPerRound: slopeMax, envelopeMaxBytes: envelopeMax })
  return {
    ok: unboundedVerdict.pass === false && slowLeakVerdict.pass === false && boundedVerdict.pass === true,
    controls: [
      {
        id: 'N3a-unbounded-series-rejected',
        description: `无界增长序列（每轮 +1 MiB，30 轮）必须被判失败（斜率阈值 ${slopeMax} B/轮）`,
        ok: unboundedVerdict.pass === false,
        detail: `slope=${unboundedVerdict.analysis.slopeBytesPerRound.toFixed(0)} B/轮 reasons=${unboundedVerdict.reasons.join('; ')}`
      },
      {
        id: 'N3b-slow-leak-rejected',
        description: `缓慢泄漏（每轮 +80 KiB，包络 2.3 MiB < ${envelopeMax}）必须被斜率判据抓住`,
        ok: slowLeakVerdict.pass === false,
        detail: `slope=${slowLeakVerdict.analysis.slopeBytesPerRound.toFixed(0)} B/轮 envelope=${slowLeakVerdict.analysis.envelope} reasons=${slowLeakVerdict.reasons.join('; ')}`
      },
      {
        id: 'N3c-bounded-jitter-accepted',
        description: '有界抖动序列必须被判通过（防止判定器恒假）',
        ok: boundedVerdict.pass === true,
        detail: `slope=${boundedVerdict.analysis.slopeBytesPerRound.toFixed(0)} B/轮 envelope=${boundedVerdict.analysis.envelope}`
      }
    ]
  }
}

// ---------- 去重缓存确定性探测（已构建 lib/ + 注入时钟） ----------

/**
 * 用**已构建产物**里的 `ReplayCache` 做确定性过期/容量探测。
 *
 * 采样点：Node 22 进程内直接调用 `lib/shared/wire/replay-cache.js`；
 * 时钟完全注入（`now` 由探测自己推进），因此不需要 sleep，也不受真实 TTL 影响。
 */
export function runDedupProbes(options = {}) {
  const capacity = options.capacity ?? 8
  const lifetimeMs = options.lifetimeMs ?? REPLAY_CACHE_LIFETIME_MS
  let now = options.startMs ?? 1_700_000_000_000
  const clock = () => now
  const put = (cache, fileId, { complete = true } = {}) => {
    const summary = { importInvoked: true, attachmentIds: [`att-${fileId}`], draft: 'staged', status: 'staged' }
    const entry = makeReplayEntry({
      kind: 'file',
      documentEpoch: 1,
      composerEpoch: 1,
      batchId: 'batch-dedup',
      fileId,
      payloadDigest: `digest-${fileId}`,
      summary,
      now,
      lifetimeMs
    })
    cache.put(entry)
    if (complete) cache.complete(entry.key, summary, `digest-${fileId}`)
    return entry
  }

  const ttlCache = new ReplayCache({ capacity: 64, lifetimeMs, now: clock })
  const first = put(ttlCache, 'file-ttl')
  now += lifetimeMs - 1
  const liveBefore = ttlCache.get(first.key) !== undefined
  now += 1
  const goneAt = ttlCache.get(first.key) === undefined
  // 反例：不推进时钟时，同一个读取必须仍然命中——证明"到期消失"不是恒真。
  const frozenNow = now
  const frozenCache = new ReplayCache({ capacity: 64, lifetimeMs, now: () => frozenNow })
  const frozenEntry = put(frozenCache, 'file-frozen')
  const frozenStillPresent = frozenCache.get(frozenEntry.key) !== undefined

  const capacityCache = new ReplayCache({ capacity, lifetimeMs, now: clock })
  for (let index = 0; index < capacity * 4; index += 1) put(capacityCache, `file-cap-${index}`)

  const pinnedCache = new ReplayCache({ capacity, lifetimeMs, now: clock })
  const pinnedKeys = []
  for (let index = 0; index < capacity; index += 1) {
    const entry = put(pinnedCache, `file-pinned-${index}`, { complete: false })
    pinnedKeys.push(entry.key)
  }
  // 全部钉住：再写入一个新条目会拒绝淘汰（继续超出容量，但被计数）
  put(pinnedCache, 'file-pinned-extra', { complete: false })
  const pinnedSurvives = pinnedKeys.every((key) => pinnedCache.get(key) !== undefined)

  const rows = [
    buildRow('U01-dedup-ttl-live-before', liveBefore ? 1 : 0, { detail: `now += ${lifetimeMs - 1}ms 时条目仍在` }),
    buildRow('U02-dedup-ttl-gone-at', goneAt ? 1 : 0, { detail: `now += ${lifetimeMs}ms 时条目被丢弃` }),
    buildRow('U03-dedup-capacity-bound', capacityCache.size, { detail: `capacity=${capacity} 写入 ${capacity * 4} 个已完成条目后 size=${capacityCache.size}` }),
    buildRow('U04-dedup-capacity-evicted', capacityCache.evictedCount, { detail: `evicted=${capacityCache.evictedCount}` }),
    buildRow('U05-dedup-pinned-survives', pinnedSurvives ? 1 : 0, { detail: `钉住 ${pinnedKeys.length} 条 + 超额写入后仍全部可读` }),
    buildRow('U06-dedup-pinned-refusal-counted', pinnedCache.refusedEvictionCount > 0 ? 1 : 0, {
      detail: `refusedEvictionCount=${pinnedCache.refusedEvictionCount}，size=${pinnedCache.size}（设计允许短暂超出容量，但必须记账）`
    })
  ]
  return {
    rows,
    observations: {
      lifetimeMs,
      capacity,
      ttlProbe: { liveBeforeMs: lifetimeMs - 1, goneAtMs: lifetimeMs, liveBefore, goneAt },
      capacityProbe: { size: capacityCache.size, evicted: capacityCache.evictedCount, capacity },
      pinnedProbe: {
        size: pinnedCache.size,
        pinned: pinnedCache.pinnedCount,
        refusedEvictions: pinnedCache.refusedEvictionCount,
        survives: pinnedSurvives
      },
    },
    // 反例读数（不进报告行，只作为"检查能失败"的证据）
    counterProbe: {
      id: 'N4-frozen-clock-does-not-expire',
      description: '反例：注入时钟不推进时同一条目的 TTL 读取必须仍然命中（证明 U02 的"到期消失"不是恒真）',
      ok: frozenStillPresent === true,
      detail: `frozenClock 下命中=${frozenStillPresent}（若为 false 说明该探测与时钟无关）`
    }
  }
}

/** 汇总判定：所有行（含未采样）都必须 pass。 */
export function computeVerdicts(rows) {
  const failed = rows.filter((row) => row.pass !== true)
  const notRun = rows.filter((row) => row.notRun === true)
  return {
    total: rows.length,
    passed: rows.length - failed.length,
    failedIds: failed.map((row) => row.id),
    notRunIds: notRun.map((row) => row.id),
    result: failed.length === 0 ? 'pass' : 'fail',
    verdict: failed.length === 0 ? '每个计量项都在冻结阈值之内' : `${failed.length} 项越界：${failed.map((row) => row.id).join(', ')}`
  }
}
