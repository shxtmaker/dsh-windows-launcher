/**
 * D19 原生提示（Toast）观测窗**判定器** —— 纯函数模块：无 DOM、无网络、无真实时钟。
 *
 * 为什么要单独一个模块：R22c 的 `T02-stale-receipt-native-prompt-visible` 出现过
 * "detail 里每一条可见子条件都满足、整体却 FAIL" 的结果。根因不是隐藏的产品行为，而是
 * **判据写法本身**：整体是六个布尔量的 `&&`，其中两个（`toastExactText` 取自
 * MutationObserver 的**出现事件**、`staleToast.observedDisappear` 取自**采样**里
 * "最后一次命中之后是否还有安静采样点"）从不作为自身取值打印，而打印出来的字段取自
 * **另一个数据源**（`文案=` 取的是首个命中**采样**的节点文本、`消失=` 取的是观察者事件）。
 * 于是"打印全真、整体为假"在结构上可能发生。
 *
 * 本模块把"采集"与"判定"彻底分开：
 *
 *   - **采集**（`d19-failure-gates.mjs` 页面侧 + 驱动侧）只负责产出原始序列：
 *     事件序列（`events`，每次 DOM 快照，含节点身份 nodeId）、采样序列（`samples`）、
 *     观察窗参数、该次 prompt 的信封；
 *   - **判定**（本模块）只吃这个序列，输出**具名子断言记录**，每条恰好五个字段
 *     `{ id, actual, expected, pass, failureReason }`；
 *   - 整体结果由 `computeOverall(records)` **直接从这些记录**算出（`records.every(pass)`），
 *     而 gate 打印的 detail 也由 `renderSubAssertionDetail(records)` **从同一批记录**渲染。
 *     因此"所有明细为真、整体为假"在结构上不再可能：明细即记录，整体即记录的全称量化。
 *
 * 覆盖的边界情形（确定性单测见 `test/unit/d19-alert-window-judge.test.mjs`）：
 * 出现、消失、**节点替换**（同文案、新元素）、**重复挂载**（两个节点同时可见）、
 * **窗口边界**（事件正好落在采样边界上）、**观察提前结束**（窗口在提示仍可见时关闭）。
 *
 * 时间语义：所有时间都是**相对点击发送**的毫秒（调用方负责归一化；给了 `sendAtWall`
 * 也可以直接喂页面侧的绝对 `Date.now()` 时间戳）。观察窗是**闭区间**
 * `[0, windowMs]`：`t === windowMs` 的事件/采样**在窗内**，`t > windowMs` 一律忽略。
 */

/** 判据版本：任何会让"同一份原始序列得出不同结论"的改动都必须提升它。
 *
 * v3（R22d 修正）：把"最短可见时长"从**单个实例**下移到**并集**。
 * 现场证据（run d19-2026-09-14T17-26-21-385Z-82dc8b1d）：一次拒绝里 Toast 节点在
 * +157ms 挂载、+663ms 被**同文案的新节点**替换（+649–697ms 页面做了一次重连重引导：
 * session/list、modelCatalog、settings/describe… 一串列表请求），第二个节点活满 4002ms。
 * 用户看到的是**连续的一条提示**；"某一次挂载只活了 506ms"是上游 `key={toast.seq}`
 * 重挂载的渲染细节，不是功能缺陷。因此：
 *   - 单个实例只断言**上界**（证伪"永不消失的静态字符串/生命周期由别的机制掌管"）；
 *   - 下界 + 上界都断言在**并集总可见区间**上（证伪"一闪而过"与"卡住不走"）。
 * 上界一个也没有放宽（单实例 6000ms、并集 10000ms 均沿用 v2）。
 */
export const D19_ALERT_CRITERIA_VERSION = 'd19-alert-window/3'

/**
 * 上界选择（写进报告，不藏在代码里）：
 *
 * - `instanceMinMs = 1000`：上游 Toast 的常量是 `holdMs(3000) + fadeMs(1000) = 4000`；
 *   下界只用来证伪"一闪而过的抖动/占位节点"，不把 4000 本身当功能标准。
 * - `instanceMaxMs = 6000`：单个 Toast 实例上界。锁定包在**重挂载时允许重放未解决的错误**
 *   （`@deepseek-ai/dsh-client-ui-conversation` 的 InputBar `showToast` 无去重，见
 *   `client.js:15824-15846`），所以上界给到常量 1.5 倍以吸收定时器抖动与 CI 负载，
 *   但仍能证伪"永不消失的静态字符串"与"生命周期由别的机制掌管"。
 * - `totalMaxMs = 10000`：**多个挂载合并后的总可见区间**上界，沿用修复前的既有上界
 *   （不放宽）；实测两形态为 4.0s 与 7.5s，均在其内。
 */
export const DEFAULT_BOUNDS = Object.freeze({
  instanceMinMs: 1000,
  instanceMaxMs: 6000,
  totalMaxMs: 10000
})

/** 一次观测的默认软窗（≥ 它且已安静才停）与硬上界（到点必停）。 */
export const DEFAULT_WINDOW = Object.freeze({
  minWindowMs: 12000,
  maxWindowMs: 20000,
  /** 判定器认为"尾部足够安静"所需的安静时长（不是采样点个数，避免依赖采样频率）。 */
  quietTailMs: 1000
})

/** 五字段记录（**恰好**这五个键，顺序固定）——子断言与整体结论的唯一载体。 */
function record(id, actual, expected, pass, failureReason = '') {
  return {
    id,
    actual,
    expected,
    pass: pass === true,
    failureReason: pass === true ? '' : failureReason === '' ? '条件不成立' : failureReason
  }
}

const asArray = (value) => (Array.isArray(value) ? value : [])

/** 命中 = 文本匹配该类提示（与采集侧同一套规则；不把"整页含失败/错误"当证据）。 */
function hitsOf(alerts, matchRe) {
  if (!(matchRe instanceof RegExp)) throw new TypeError('judgeAlertWindow: matchRe 必须是 RegExp')
  if (matchRe.global) matchRe.lastIndex = 0
  return asArray(alerts).filter((alert) => matchRe.test(String(alert?.text ?? '')))
}

/** 节点身份：优先用采集侧给的不透明 nodeId；缺失时退化为 (class, text) 形状键。 */
function nodeIdOf(alert) {
  const id = alert?.nodeId
  if (typeof id === 'string' && id !== '') return id
  if (typeof alert?.nodeId === 'number') return `n${String(alert.nodeId)}`
  return `shape:${String(alert?.class ?? '')}|${String(alert?.text ?? '')}`
}

function normalizeTimes(input) {
  const base = input.sendAtWall
  const rel = (t) => (typeof base === 'number' ? t - base : t)
  return {
    events: asArray(input.events)
      .filter((event) => Number.isFinite(event?.t))
      .map((event) => ({ tMs: rel(event.t), kind: String(event.kind ?? 'event'), alerts: asArray(event.alerts) }))
      .sort((a, b) => a.tMs - b.tMs),
    samples: asArray(input.samples)
      .filter((sample) => Number.isFinite(sample?.t))
      .map((sample) => ({ tMs: rel(sample.t), alerts: asArray(sample.alerts) }))
      .sort((a, b) => a.tMs - b.tMs)
  }
}

/**
 * 从事件序列里切出**挂载实例**（不是"命中片段"）。
 *
 * 实例的身份是**节点**（nodeId），因此：
 *   - 同文案的新元素 = **两个实例**（节点替换能被看见，而不是被合并成一段）；
 *   - 两个节点同时可见 = 两个实例且区间重叠（重复挂载能被看见）。
 * 事件的快照只在状态变化时产生，所以"某节点在 t 时刻不在快照里"等价于"该节点在 t 时刻
 * 已经卸载"；最后一个快照里仍存在的节点 = **未被观察到消失**（`endMs === null`）。
 */
function extractInstances({ events, samples, matchRe, windowEndMs }) {
  const open = new Map()
  const instances = []
  const consider = (tMs, alerts, source, kind) => {
    const ids = new Set()
    for (const alert of hitsOf(alerts, matchRe)) {
      const id = nodeIdOf(alert)
      ids.add(id)
      let instance = open.get(id)
      if (instance === undefined) {
        instance = {
          nodeId: id,
          startMs: tMs,
          startSource: source,
          startKind: kind,
          endMs: null,
          endSource: null,
          texts: [],
          node: alert,
          observations: 0
        }
        open.set(id, instance)
        instances.push(instance)
      }
      instance.observations += 1
      const text = String(alert?.text ?? '')
      if (!instance.texts.includes(text)) instance.texts.push(text)
      instance.lastSeenMs = tMs
      instance.lastNode = alert
    }
    for (const [id, instance] of [...open]) {
      if (ids.has(id)) continue
      instance.endMs = tMs
      instance.endSource = source
      open.delete(id)
    }
  }
  for (const event of events) {
    if (event.tMs > windowEndMs) break
    consider(event.tMs, event.alerts, 'events', event.kind)
  }
  // 只出现在采样里的节点（事件流没记到）单独补齐：它们的时间分辨率是采样间隔，
  // 因此标 `precision: 'sample'`，判定器不会把它们当成"精确边界"。
  for (const sample of samples) {
    if (sample.tMs > windowEndMs) continue
    for (const alert of hitsOf(sample.alerts, matchRe)) {
      const id = nodeIdOf(alert)
      if (instances.some((instance) => instance.nodeId === id)) continue
      const text = String(alert?.text ?? '')
      instances.push({
        nodeId: id,
        startMs: sample.tMs,
        startSource: 'samples',
        startKind: 'sample',
        endMs: null,
        endSource: null,
        texts: [text],
        node: alert,
        observations: 1,
        lastSeenMs: sample.tMs,
        lastNode: alert,
        precision: 'sample'
      })
    }
  }
  // 采样只用来补"没被事件流看到"的节点；已被事件流看到的节点，其结束时刻以事件为准。
  // 采样分辨率实例的结束时刻用**最后一次命中采样**兜底（若之后有安静采样）。
  for (const instance of instances) {
    if (instance.precision !== 'sample') continue
    const lastHit = samples.filter((sample) => sample.tMs <= windowEndMs && hitsOf(sample.alerts, matchRe).some((alert) => nodeIdOf(alert) === instance.nodeId)).at(-1)
    if (lastHit === undefined) continue
    const quiet = samples.find((sample) => sample.tMs > lastHit.tMs && !hitsOf(sample.alerts, matchRe).some((alert) => nodeIdOf(alert) === instance.nodeId))
    if (quiet !== undefined) {
      instance.endMs = lastHit.tMs
      instance.endSource = 'samples'
    }
  }
  return instances
}

/** 合并后的总可见区间（多个挂载的并集），以及挂载之间的关系。 */
function summarizeVisible(instances, observationEndMs) {
  const spans = instances
    .map((instance) => ({ startMs: instance.startMs, endMs: instance.endMs === null ? observationEndMs : instance.endMs, nodeId: instance.nodeId }))
    .sort((a, b) => a.startMs - b.startMs)
  const merged = []
  for (const span of spans) {
    const last = merged.at(-1)
    if (last !== undefined && span.startMs <= last.endMs) last.endMs = Math.max(last.endMs, span.endMs)
    else merged.push({ ...span })
  }
  const totalVisibleMs = merged.reduce((sum, span) => sum + Math.max(0, span.endMs - span.startMs), 0)
  const concurrent = []
  for (let i = 0; i < spans.length; i += 1) {
    for (let j = i + 1; j < spans.length; j += 1) {
      const overlap = Math.min(spans[i].endMs, spans[j].endMs) - Math.max(spans[i].startMs, spans[j].startMs)
      if (overlap > 0) concurrent.push({ a: spans[i].nodeId, b: spans[j].nodeId, overlapMs: overlap })
    }
  }
  // 节点替换：前一个已结束、后一个在它结束后 1s 内以**同一文案**接上。
  const replacements = []
  for (const earlier of instances) {
    if (earlier.endMs === null) continue
    for (const later of instances) {
      if (later === earlier) continue
      if (later.startMs < earlier.endMs || later.startMs - earlier.endMs > 1000) continue
      if (!later.texts.some((text) => earlier.texts.includes(text))) continue
      replacements.push({ ended: earlier.nodeId, started: later.nodeId, gapMs: later.startMs - earlier.endMs })
    }
  }
  return { merged, totalVisibleMs, concurrent, replacements }
}

/**
 * 判定一次观测。
 *
 * @param {object} input
 * @param {Array<{t:number, kind?:string, alerts:Array<object>}>} input.events 观察者事件（含基线那条）
 * @param {Array<{t:number, alerts:Array<object>}>} input.samples 驱动侧采样
 * @param {number} [input.sendAtWall] 点击发送的绝对时刻（给了就做归一化）
 * @param {number} input.windowMs 请求的观察窗（闭区间上界）
 * @param {number} [input.observedUntilMs] 实际观察到的最后时刻（相对；缺省取序列末点）
 * @param {string} input.expectedText 该提示的逐字文案
 * @param {RegExp} input.matchRe 文案归类规则
 * @param {number} [input.promptRequestsInWindow] 本窗内该次发送产生的 prompt 请求数
 * @param {{code?:string|null, reason?:string|null}|null} [input.envelope] 该次 prompt 的响应信封
 * @param {object} [input.bounds] 覆盖默认上界
 * @returns {{criteriaVersion:string, subAssertions:Array<object>, overall:boolean, observations:object, instances:Array<object>}}
 */
export function judgeAlertWindow(input) {
  const bounds = { ...DEFAULT_BOUNDS, ...(input.bounds ?? {}) }
  const { events, samples } = normalizeTimes(input)
  const windowMs = Number.isFinite(input.windowMs) ? input.windowMs : DEFAULT_WINDOW.minWindowMs
  const windowEndMs = windowMs
  const lastEventMs = events.length === 0 ? 0 : events.at(-1).tMs
  const lastSampleMs = samples.length === 0 ? 0 : samples.at(-1).tMs
  const observationEndMs = Number.isFinite(input.observedUntilMs) ? input.observedUntilMs : Math.min(windowEndMs, Math.max(lastEventMs, lastSampleMs))

  const instances = extractInstances({ events, samples, matchRe: input.matchRe, windowEndMs })
  const { merged, totalVisibleMs, concurrent, replacements } = summarizeVisible(instances, observationEndMs)

  const baselineTexts = hitsOf(events.find((event) => event.kind === 'baseline')?.alerts ?? [], input.matchRe).map((alert) => String(alert?.text ?? ''))
  const expectedText = String(input.expectedText ?? '')
  const primary = instances.filter((instance) => instance.texts.includes(expectedText))
  const foreign = instances.filter((instance) => !instance.texts.includes(expectedText))
  const closed = primary.filter((instance) => instance.endMs !== null)
  const openInstances = primary.filter((instance) => instance.endMs === null)

  const textsFromEvents = [...new Set(primary.flatMap((instance) => instance.texts))].sort()
  const textsFromSamples = [
    ...new Set(
      samples
        .filter((sample) => sample.tMs <= windowEndMs)
        .flatMap((sample) => hitsOf(sample.alerts, input.matchRe))
        .filter((alert) => String(alert?.text ?? '') === expectedText)
        .map((alert) => String(alert.text))
    )
  ].sort()
  const exactSeen = textsFromEvents.includes(expectedText) || textsFromSamples.includes(expectedText)
  const exactNode = instances.find((instance) => instance.texts.includes(expectedText))?.node ?? null

  const envelope = input.envelope ?? null
  const promptRequests = Number.isFinite(input.promptRequestsInWindow) ? input.promptRequestsInWindow : null

  const subAssertions = [
    record(
      'T02-01-prompt-visible',
      { mountCount: primary.length, firstSeenMs: primary.length === 0 ? null : Math.min(...primary.map((instance) => instance.startMs)), channels: ['events', 'samples'] },
      '观察窗内至少出现一次该原生恢复提示（事件流或采样任一路径看到都算，且两者都会被打印）',
      primary.length > 0,
      primary.length === 0
        ? `观察窗 ${windowMs}ms 内事件流与采样都没有看到文案为「${expectedText}」的节点（事件 ${events.length} 条 / 采样 ${samples.length} 条）`
        : ''
    ),
    record(
      'T02-02-text-exact',
      { exactSeenFromEvents: textsFromEvents.includes(expectedText), exactSeenFromSamples: textsFromSamples.includes(expectedText), textsFromEvents, textsFromSamples },
      `观测到的文案里逐字包含「${expectedText}」`,
      exactSeen,
      exactSeen ? '' : `事件流看到 ${JSON.stringify(textsFromEvents)}、采样看到 ${JSON.stringify(textsFromSamples)}，都不含逐字文案`
    ),
    record(
      'T02-03-portal-anchored',
      exactNode === null
        ? { node: null }
        : { nodeId: nodeIdOf(exactNode), parent: exactNode.parent ?? null, parentIsBody: exactNode.parentIsBody === true, inComposerCard: exactNode.inComposerCard === true, class: exactNode.class ?? null },
      '承载逐字文案的节点 portal 到 document.body（parentIsBody=true）且不在 [data-composer-card] 内',
      exactNode !== null && exactNode.parentIsBody === true && exactNode.inComposerCard !== true,
      exactNode === null ? '没有可用于锚定判断的逐字文案节点' : `parentIsBody=${String(exactNode.parentIsBody)} inComposerCard=${String(exactNode.inComposerCard)}`
    ),
    record(
      'T02-04-baseline-clean',
      { baselineMatches: baselineTexts.length, baselineTexts },
      '观察起点（点击发送之前）没有同类提示残留',
      baselineTexts.length === 0,
      baselineTexts.length === 0 ? '' : `观察起点已有同类节点：${JSON.stringify(baselineTexts)}`
    ),
    record(
      'T02-05-instance-upper-bound',
      {
        closedInstances: closed.map((instance) => ({ nodeId: instance.nodeId, startMs: instance.startMs, endMs: instance.endMs, lifetimeMs: instance.endMs - instance.startMs, precision: instance.precision ?? 'event' })),
        openInstances: openInstances.length,
        upperBoundMs: bounds.instanceMaxMs
      },
      `每一个**单个 Toast 实例**的可见时长 ≤ ${bounds.instanceMaxMs}ms（上游常量 4000ms 的 1.5 倍；只证伪"静态字符串/非瞬态"）。下界不放在单实例上：节点被同文案的新元素替换时首个实例可以很短（现场实测 506ms，见判据版本注记），用户看到的是并集`,
      closed.length > 0 && closed.every((instance) => instance.endMs - instance.startMs <= bounds.instanceMaxMs),
      closed.length === 0
        ? '没有任何实例被观察到"出现→消失"的完整生命周期（无从判断瞬态性）'
        : `实例时长 ${JSON.stringify(closed.map((instance) => instance.endMs - instance.startMs))} 超过上界 ${bounds.instanceMaxMs}ms`
    ),
    record(
      'T02-06-observation-complete',
      { openInstances: openInstances.map((instance) => ({ nodeId: instance.nodeId, startMs: instance.startMs, lastSeenMs: instance.lastSeenMs ?? null })), windowMs, observedUntilMs: observationEndMs, lastEventMs, lastSampleMs },
      '观察窗在提示消失之后才结束（每个实例都被看到消失；窗口不得在提示仍可见时关闭）',
      openInstances.length === 0,
      openInstances.length === 0
        ? ''
        : `窗口在提示仍可见时结束：实例 ${JSON.stringify(openInstances.map((instance) => instance.nodeId))} 起始于 +${String(openInstances[0].startMs)}ms，最后观测点 +${String(observationEndMs)}ms（窗口 ${windowMs}ms）——这是**观察不足**，不是"活得太久"`
    ),
    record(
      'T02-07-total-visible-bounded',
      { totalVisibleMs, mergedSpans: merged, mounts: instances.length, boundsMs: [bounds.instanceMinMs, bounds.totalMaxMs] },
      `多次挂载合并后的总可见区间 ∈ [${bounds.instanceMinMs}, ${bounds.totalMaxMs}]ms（下界证伪"一闪而过"，上界沿用既有 10000ms，不放宽）`,
      totalVisibleMs >= bounds.instanceMinMs && totalVisibleMs <= bounds.totalMaxMs,
      totalVisibleMs < bounds.instanceMinMs
        ? `总可见仅 ${totalVisibleMs}ms，短于下界 ${bounds.instanceMinMs}ms（不是一次可读的提示）`
        : totalVisibleMs > bounds.totalMaxMs
          ? `总可见 ${totalVisibleMs}ms 超过上界 ${bounds.totalMaxMs}ms`
          : ''
    ),
    record(
      'T02-08-no-duplicate-prompt',
      { promptRequestsInWindow: promptRequests },
      '这一次发送只产生 1 个 prompt 请求（不自动重发）',
      promptRequests === 1,
      promptRequests === null ? '没有采集到 prompt 请求计数' : `本窗内 prompt 请求数 = ${promptRequests}（期望恰好 1）`
    ),
    record(
      'T02-09-envelope-bound',
      { code: envelope?.code ?? null, reason: envelope?.reason ?? null },
      '该次 prompt 的响应信封是 session/attachment-invalid 且 reason=FILE_NOT_STAGED（把提示绑定到"这一次"拒绝）',
      envelope?.code === 'session/attachment-invalid' && envelope?.reason === 'FILE_NOT_STAGED',
      `信封 code=${String(envelope?.code ?? null)} reason=${String(envelope?.reason ?? null)}`
    )
  ]

  return {
    criteriaVersion: D19_ALERT_CRITERIA_VERSION,
    subAssertions,
    overall: computeOverall(subAssertions),
    instances: primary.map((instance) => ({
      nodeId: instance.nodeId,
      startMs: instance.startMs,
      endMs: instance.endMs,
      lifetimeMs: instance.endMs === null ? null : instance.endMs - instance.startMs,
      lastSeenMs: instance.lastSeenMs ?? null,
      texts: instance.texts,
      precision: instance.precision ?? 'event'
    })),
    observations: {
      mountCount: primary.length,
      foreignAlertCount: foreign.length,
      foreignAlerts: foreign.map((instance) => ({ nodeId: instance.nodeId, startMs: instance.startMs, endMs: instance.endMs, texts: instance.texts })),
      concurrentMounts: concurrent,
      replacements,
      totalVisibleMs,
      mergedSpans: merged,
      lastEventMs,
      lastSampleMs,
      observedUntilMs: observationEndMs,
      truncated: openInstances.length > 0,
      quietTailMs:
        instances.length === 0
          ? null
          : observationEndMs - Math.max(...instances.map((instance) => instance.lastSeenMs ?? instance.startMs))
    }
  }
}

/**
 * 整体结果 = 具名子断言记录的**全称量化**。gate 必须用这个函数算整体，
 * 不得另写一份聚合逻辑——否则又会分叉出"明细真、整体假"的可能。
 */
export function computeOverall(records) {
  return asArray(records).length > 0 && asArray(records).every((item) => item.pass === true)
}

/** 五字段记录 → 一行人类可读文本（gate 的 detail 只从这里来）。 */
export function renderSubAssertion(item) {
  return `[${item.pass ? 'ok' : 'NO'}] ${item.id} actual=${JSON.stringify(item.actual)} expected=${JSON.stringify(item.expected)}${item.pass ? '' : ` why=${item.failureReason}`}`
}

/**
 * gate detail：**逐条**渲染同一批记录，再把观测事实（挂载次数/替换/并发/总区间）附在后面。
 * 明细与整体出自同一批记录，因此"打印全真而整体为假"不可能再发生。
 */
export function renderSubAssertionDetail(records, observations = {}) {
  const lines = asArray(records).map((item) => renderSubAssertion(item))
  const facts = [
    `mounts=${String(observations.mountCount ?? 'n/a')}`,
    `replacements=${String((observations.replacements ?? []).length)}`,
    `concurrent=${String((observations.concurrentMounts ?? []).length)}`,
    `foreignAlerts=${String(observations.foreignAlertCount ?? 'n/a')}`,
    `totalVisibleMs=${String(observations.totalVisibleMs ?? 'n/a')}`,
    `observedUntilMs=${String(observations.observedUntilMs ?? 'n/a')}`,
    `truncated=${String(observations.truncated ?? 'n/a')}`
  ]
  return `${lines.join(' | ')} || 观测事实：${facts.join(' ')}`
}
