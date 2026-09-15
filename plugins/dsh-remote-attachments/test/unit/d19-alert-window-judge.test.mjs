/**
 * D19 原生提示观测窗**判定器**的确定性单测（无浏览器、无真实时钟）。
 *
 * 被测对象是 `tests/fixtures/d19-alert-window-judge.mjs`：它只吃"事件序列 + 采样序列"
 * 这样的原始观测，产出**具名子断言记录**。这里用可注入的时钟（显式时间戳）复现
 * R22c 里出现过与可能出现过的每一种窗口形态，并钉住两条不变式：
 *
 *   1. 每条子断言记录**恰好**是 `{ id, actual, expected, pass, failureReason }`；
 *   2. 整体结果 === 这些记录的全称量化（`overall === records.every(r => r.pass)`）——
 *      "明细全真、整体为假"在结构上不可能。
 *
 * 覆盖：出现 / 消失 / 节点替换 / 重复挂载 / 窗口边界 / 观察提前结束，
 * 外加对 R22c 那次丢失运行的重建（用幸存 detail 里的数字做**排除法**，不伪造原始数据）。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import {
  D19_ALERT_CRITERIA_VERSION,
  DEFAULT_BOUNDS,
  computeOverall,
  judgeAlertWindow,
  renderSubAssertionDetail
} from '../../tests/fixtures/d19-alert-window-judge.mjs'

const EXPECTED_TEXT = '文件尚未上传成功，请重新添加后再试'
const MATCH_RE = /文件尚未上传成功|重新添加|未上传|not uploaded|附件|attachment/i
const SEND_AT = 1_700_000_000_000

const ALERT_CLASS = '_toast_e5v0f_6'

/** 造一个节点快照（形状与采集侧 `describeAlert` 一致）。 */
function alertNode({ nodeId, text = EXPECTED_TEXT, parentIsBody = true, inComposerCard = false, className = ALERT_CLASS }) {
  return { nodeId, tag: 'DIV', class: className, role: 'alert', parent: 'BODY', parentIsBody, inComposerCard, style: null, text }
}

/**
 * 用可注入时钟造一次观测：给定每个节点的**可见片段**，生成
 *   - 事件序列：基线 + 每次可见性状态变化的快照（与真实 MutationObserver 行为一致）；
 *   - 采样序列：每 `intervalMs` 一次，直到 `windowMs`（闭区间）。
 * `endMs === null` 表示"窗口结束时仍可见"（观察提前结束）。
 */
function buildObservation({
  segments,
  windowMs = 12_000,
  intervalMs = 152,
  observedUntilMs = null,
  promptRequestsInWindow = 1,
  envelope = { code: 'session/attachment-invalid', reason: 'FILE_NOT_STAGED' }
}) {
  const changes = new Set([0])
  for (const segment of segments) {
    changes.add(segment.startMs)
    if (segment.endMs !== null) changes.add(segment.endMs)
  }
  const changeTimes = [...changes].sort((a, b) => a - b)
  const present = (t) => segments.filter((segment) => t >= segment.startMs && (segment.endMs === null || t < segment.endMs))
  const events = changeTimes.map((t, index) => ({
    t: SEND_AT + t,
    kind: index === 0 ? 'baseline' : 'alert-mutation',
    alerts: present(t).map((segment) => alertNode(segment))
  }))
  const lastMs = observedUntilMs ?? windowMs
  const samples = []
  for (let t = 0; t <= lastMs; t += intervalMs) {
    samples.push({ t: SEND_AT + t, alerts: present(t).map((segment) => alertNode(segment)) })
  }
  return { events, samples, windowMs, expectedText: EXPECTED_TEXT, matchRe: MATCH_RE, sendAtWall: SEND_AT, promptRequestsInWindow, envelope }
}

/** 采集侧观察者事件与采样事件在 < 边界上取"半开"语义：t === endMs 时节点已经卸载。 */

const judge = (observation) => judgeAlertWindow(observation)

/** 每条记录恰好五个字段、类型正确。 */
function assertRecordShape(records) {
  for (const item of records) {
    assert.deepEqual(Object.keys(item), ['id', 'actual', 'expected', 'pass', 'failureReason'])
    assert.equal(typeof item.id, 'string')
    assert.ok(item.id.length > 0)
    assert.equal(typeof item.pass, 'boolean')
    assert.equal(typeof item.failureReason, 'string')
    assert.ok(item.actual !== undefined)
    assert.ok(item.expected !== undefined)
    if (item.pass) assert.equal(item.failureReason, '')
    else assert.notEqual(item.failureReason, '')
  }
}

/** 不变式：整体 === 记录的全称量化；detail 由同一批记录渲染。 */
function assertOverallIsDerived(result) {
  assertRecordShape(result.subAssertions)
  assert.equal(result.overall, result.subAssertions.every((item) => item.pass))
  assert.equal(result.overall, computeOverall(result.subAssertions))
  const detail = renderSubAssertionDetail(result.subAssertions, result.observations)
  for (const item of result.subAssertions) assert.ok(detail.includes(item.id), `detail 缺少 ${item.id}`)
  assert.equal(result.subAssertions.every((item) => detail.includes(`[ok] ${item.id}`)), result.overall)
}

test('出现 + 消失：单个实例落在上界内 → 全绿', () => {
  const result = judge(buildObservation({ segments: [{ nodeId: 'T1', startMs: 155, endMs: 4155 }] }))
  assertOverallIsDerived(result)
  assert.equal(result.overall, true)
  assert.equal(result.observations.mountCount, 1)
  assert.equal(result.observations.totalVisibleMs, 4000)
  assert.equal(result.instances[0].lifetimeMs, 4000)
  assert.deepEqual(result.observations.concurrentMounts, [])
  assert.deepEqual(result.observations.replacements, [])
})

test('节点替换：同文案、新元素 → 两个实例，各自有界，总区间仍有界', () => {
  const result = judge(
    buildObservation({
      segments: [
        { nodeId: 'T1', startMs: 155, endMs: 4155 },
        { nodeId: 'T2', startMs: 4207, endMs: 8207 }
      ]
    })
  )
  assertOverallIsDerived(result)
  assert.equal(result.overall, true, renderSubAssertionDetail(result.subAssertions, result.observations))
  assert.equal(result.observations.mountCount, 2)
  assert.deepEqual(result.instances.map((instance) => instance.lifetimeMs), [4000, 4000])
  assert.equal(result.observations.replacements.length, 1)
  assert.equal(result.observations.replacements[0].gapMs, 52)
  assert.equal(result.observations.totalVisibleMs, 8000)
  // 两个实例都满足单实例上界，总区间 8000 ≤ 10000（既有上界未被放宽）。
  assert.equal(result.subAssertions.find((item) => item.id === 'T02-07-total-visible-bounded').pass, true)
})

test('重复挂载：两个节点同时可见 → 如实报告并发，但不因上游重放语义判失败', () => {
  const result = judge(
    buildObservation({
      segments: [
        { nodeId: 'T1', startMs: 155, endMs: 4155 },
        { nodeId: 'T2', startMs: 1_000, endMs: 5_000 }
      ]
    })
  )
  assertOverallIsDerived(result)
  assert.equal(result.observations.concurrentMounts.length, 1)
  assert.equal(result.observations.concurrentMounts[0].overlapMs, 3_155)
  // 并发挂载本身不是本插件的验收项（锁定包的 showToast 无去重），因此不进整体；
  // 但它必须被**看见并打印**，不能消失。
  assert.equal(result.overall, true)
  assert.ok(renderSubAssertionDetail(result.subAssertions, result.observations).includes('concurrent=1'))
})

test('窗口边界：卸载事件正好落在 windowMs 上算窗内（闭区间）', () => {
  const observation = buildObservation({
    segments: [{ nodeId: 'T1', startMs: 7_900, endMs: 12_000 }],
    windowMs: 12_000
  })
  const result = judge(observation)
  assertOverallIsDerived(result)
  assert.equal(result.instances[0].endMs, 12_000)
  assert.equal(result.instances[0].lifetimeMs, 4_100)
  assert.equal(result.subAssertions.find((item) => item.id === 'T02-06-observation-complete').pass, true)
  assert.equal(result.overall, true)
})

test('窗口边界外一条：卸载事件在 windowMs+1 → 视为未观察到消失', () => {
  const observation = buildObservation({
    segments: [{ nodeId: 'T1', startMs: 7_900, endMs: 12_001 }],
    windowMs: 12_000,
    observedUntilMs: 12_000
  })
  const result = judge(observation)
  assertOverallIsDerived(result)
  const complete = result.subAssertions.find((item) => item.id === 'T02-06-observation-complete')
  assert.equal(complete.pass, false)
  assert.match(complete.failureReason, /观察不足/)
  assert.equal(result.overall, false)
})

test('观察提前结束：窗口在提示仍可见时关闭 → 显式判为"观察不足"而不是"活得太久"', () => {
  const result = judge(
    buildObservation({
      segments: [
        { nodeId: 'T1', startMs: 155, endMs: 4_158 },
        { nodeId: 'T2', startMs: 7_900, endMs: null }
      ]
    })
  )
  assertOverallIsDerived(result)
  assert.equal(result.overall, false)
  const failing = result.subAssertions.filter((item) => !item.pass).map((item) => item.id)
  assert.deepEqual(failing, ['T02-06-observation-complete'])
  // 第一个实例仍然拿到"出现→消失"的完整生命周期并满足单实例上界。
  assert.equal(result.instances[0].lifetimeMs, 4_003)
  assert.equal(result.subAssertions.find((item) => item.id === 'T02-05-instance-upper-bound').pass, true)
  // 关键：失败原因是**观察窗**，且 detail 里看得见（打印全真而整体为假不可能再发生）。
  assert.match(result.subAssertions[5].failureReason, /窗口在提示仍可见时结束/)
  assert.ok(renderSubAssertionDetail(result.subAssertions, result.observations).includes('[NO] T02-06-observation-complete'))
})

test('非同类节点（正则命中但文案不同）只作为事实报告，不污染逐字文案判据', () => {
  const result = judge(
    buildObservation({
      segments: [
        { nodeId: 'F1', text: '附件上传失败', startMs: 100, endMs: 900 },
        { nodeId: 'T1', startMs: 155, endMs: 4_155 }
      ]
    })
  )
  assertOverallIsDerived(result)
  assert.equal(result.subAssertions.find((item) => item.id === 'T02-02-text-exact').pass, true)
  assert.equal(result.observations.foreignAlertCount, 1)
  assert.equal(result.observations.foreignAlerts[0].texts[0], '附件上传失败')
  assert.equal(result.overall, true)
})

test('基线不干净：发送前已有同类提示 → 判失败并给出残留文案', () => {
  const observation = buildObservation({ segments: [{ nodeId: 'T0', startMs: 0, endMs: 400 }] })
  const result = judge(observation)
  assertOverallIsDerived(result)
  const baseline = result.subAssertions.find((item) => item.id === 'T02-04-baseline-clean')
  assert.equal(baseline.pass, false)
  assert.deepEqual(baseline.actual.baselineTexts, [EXPECTED_TEXT])
  assert.equal(result.overall, false)
})

test('请求计数与信封：重复 prompt 或信封不符都必须显式失败', () => {
  const observation = { ...buildObservation({ segments: [{ nodeId: 'T1', startMs: 155, endMs: 4_155 }] }), promptRequestsInWindow: 2, envelope: { code: 'session/attachment-invalid', reason: 'FILE_NOT_STAGED' } }
  const duplicated = judge(observation)
  assertOverallIsDerived(duplicated)
  assert.equal(duplicated.subAssertions.find((item) => item.id === 'T02-08-no-duplicate-prompt').pass, false)
  assert.equal(duplicated.overall, false)

  const wrongEnvelope = judge({ ...observation, promptRequestsInWindow: 1, envelope: { code: 'session/other', reason: null } })
  assertOverallIsDerived(wrongEnvelope)
  assert.deepEqual(wrongEnvelope.subAssertions.filter((item) => !item.pass).map((item) => item.id), ['T02-09-envelope-bound'])
})

test('现场形态：节点在 +663ms 被同文案新元素替换（首个实例仅 506ms）→ 仍然通过', () => {
  // 数字取自 R22d 的现场证据（run d19-2026-09-14T17-26-21-385Z-82dc8b1d）：
  // A1=[157,663]（506ms）、A2=[663,4665]（4002ms）；+649–697ms 页面做了一次重连重引导。
  // 用户看到的是**连续一条提示**（总 4508ms），因此下界只能落在并集上，
  // 单实例只卡上界——否则判据会把"上游重挂载"误判成产品缺陷（R22c 那类不稳定的来源）。
  const result = judge(
    buildObservation({
      segments: [
        { nodeId: 'A1', startMs: 157, endMs: 663 },
        { nodeId: 'A2', startMs: 663, endMs: 4_665 }
      ]
    })
  )
  assertOverallIsDerived(result)
  assert.equal(result.overall, true, renderSubAssertionDetail(result.subAssertions, result.observations))
  assert.equal(result.observations.mountCount, 2)
  assert.equal(result.observations.replacements.length, 1)
  assert.equal(result.observations.replacements[0].gapMs, 0)
  assert.deepEqual(result.instances.map((instance) => instance.lifetimeMs), [506, 4_002])
  assert.equal(result.observations.totalVisibleMs, 4_508)
  assert.equal(result.subAssertions.find((item) => item.id === 'T02-05-instance-upper-bound').pass, true)
  assert.equal(result.subAssertions.find((item) => item.id === 'T02-07-total-visible-bounded').pass, true)
})

test('一闪而过：并集总可见短于下界 → 判失败（下界落在并集上，而不是被丢掉）', () => {
  const result = judge(buildObservation({ segments: [{ nodeId: 'T1', startMs: 1_000, endMs: 1_300 }] }))
  assertOverallIsDerived(result)
  const total = result.subAssertions.find((item) => item.id === 'T02-07-total-visible-bounded')
  assert.equal(total.pass, false)
  assert.match(total.failureReason, /短于下界/)
  assert.equal(result.overall, false)
})

test('实例超过上界：一个实例活过 6000ms → 判失败（证伪"静态字符串/卡住不走"）', () => {
  const result = judge(buildObservation({ segments: [{ nodeId: 'T1', startMs: 200, endMs: 8_200 }] }))
  assertOverallIsDerived(result)
  const upper = result.subAssertions.find((item) => item.id === 'T02-05-instance-upper-bound')
  assert.equal(upper.pass, false)
  assert.equal(result.overall, false)
})

test('判据版本与上界是显式常量（改动会被看见）', () => {
  assert.equal(D19_ALERT_CRITERIA_VERSION, 'd19-alert-window/3')
  assert.deepEqual(DEFAULT_BOUNDS, { instanceMinMs: 1_000, instanceMaxMs: 6_000, totalMaxMs: 10_000 })
})

/**
 * R22c 丢失运行的**排除法重建**（不伪造原始数据）：
 *
 * 幸存的只有 R22-D19 §附二 里那条 detail 字符串：文案逐字正确、portal=true、观察起点无残留、
 * 出现=+155ms、消失=+4158ms、存活=4003ms、命中样本=52/79、信封=session/attachment-invalid/
 * FILE_NOT_STAGED，**却整体 FAIL**。原始序列已被后一次运行覆写，无法恢复。
 *
 * 这里复现修复前的六合一判据（代码见 R22c 的 `d19-failure-gates.mjs:1329`），
 * 把它套到两组与幸存数字一致的世界里，看哪一组能重现那次 FAIL：
 *   - 世界 A：两次挂载都在窗口内结束（连续/小幅间隙）；
 *   - 世界 B：第二次挂载在采样窗口关闭时仍可见。
 */
function legacySummary({ events, samples, sendAtWall, windowMs }) {
  const hits = (alerts) => (alerts ?? []).filter((alert) => MATCH_RE.test(alert.text ?? ''))
  const sampleHits = samples.filter((sample) => hits(sample.alerts).length > 0)
  const appearEvent = events.find((event) => hits(event.alerts).length > 0) ?? null
  const disappearEvent = appearEvent === null ? null : events.find((event) => event.t > appearEvent.t && hits(event.alerts).length === 0) ?? null
  const lastSampleHit = sampleHits.at(-1) ?? null
  const quietSample = lastSampleHit === null ? null : samples.find((sample) => sample.t > lastSampleHit.t && hits(sample.alerts).length === 0) ?? null
  const firstHitNode = sampleHits.length === 0 ? null : hits(sampleHits[0].alerts)[0]
  return {
    expectedTextOk: firstHitNode !== null && firstHitNode.text === EXPECTED_TEXT,
    portalAnchored: firstHitNode !== null && firstHitNode.parentIsBody === true && firstHitNode.inComposerCard === false,
    baselineClean: hits(events.find((event) => event.kind === 'baseline')?.alerts ?? []).length === 0,
    observedDisappear: quietSample !== null,
    appearMs: appearEvent === null ? null : appearEvent.t - sendAtWall,
    disappearMs: disappearEvent === null ? null : disappearEvent.t - sendAtWall,
    lifetimeMs: appearEvent === null || disappearEvent === null ? null : disappearEvent.t - appearEvent.t,
    samplesWithAlert: sampleHits.length,
    sampleCount: samples.length,
    windowMs
  }
}

/** 修复前的整体 = 六合一 `&&`（其中两条从不打印自身取值）。 */
const legacyOverall = (summary) => summary.expectedTextOk && summary.portalAnchored && summary.baselineClean && summary.observedDisappear && summary.lifetimeMs !== null && summary.lifetimeMs >= 1_000 && summary.lifetimeMs <= 10_000 && summary.disappearMs !== null

test('重建：只有"最后一次挂载活到窗口关闭"的世界能重现那次 FAIL（且判据已把它显式化）', () => {
  // 世界 A：第二次挂载紧接着第一次（真实观察到的 4.0s / 7.5s 两形态之一），窗口内有安静尾部。
  const worldA = buildObservation({
    segments: [
      { nodeId: 'T1', startMs: 155, endMs: 4_158 },
      { nodeId: 'T2', startMs: 4_210, endMs: 8_210 }
    ]
  })
  const legacyA = legacySummary(worldA)
  assert.equal(legacyA.appearMs, 155)
  assert.equal(legacyA.disappearMs, 4_003 + 155)
  assert.equal(legacyA.lifetimeMs, 4_003)
  assert.equal(legacyA.observedDisappear, true)
  // 修复前判据在世界 A 下是**绿的**：因此 A 不可能是那次 FAIL 的世界。
  assert.equal(legacyOverall(legacyA), true)

  // 世界 B：第二次挂载在窗口关闭时仍可见 —— 修复前正是这条（从不打印自身取值）
  // `observedDisappear` 变假，于是"detail 全真、整体 FAIL"。
  const worldB = buildObservation({
    segments: [
      { nodeId: 'T1', startMs: 155, endMs: 4_158 },
      { nodeId: 'T2', startMs: 8_000, endMs: null }
    ]
  })
  const legacyB = legacySummary(worldB)
  assert.equal(legacyB.appearMs, 155)
  assert.equal(legacyB.disappearMs, 4_158)
  assert.equal(legacyB.lifetimeMs, 4_003)
  assert.equal(legacyB.observedDisappear, false)
  assert.equal(legacyB.samplesWithAlert, 52)
  assert.equal(legacyB.sampleCount, 79)
  assert.equal(legacyOverall(legacyB), false)
  // 新判据在同一世界下把"窗口在提示仍可见时关闭"变成**具名且有原因**的记录。
  const judged = judge(worldB)
  assertOverallIsDerived(judged)
  assert.equal(judged.overall, false)
  assert.deepEqual(judged.subAssertions.filter((item) => !item.pass).map((item) => item.id), ['T02-06-observation-complete'])

  // 世界 B 与幸存 detail 的每个可读字段一致：文案/portal/基线/出现/消失/存活/命中样本。
  assert.equal(legacyB.expectedTextOk, true)
  assert.equal(legacyB.portalAnchored, true)
  assert.equal(legacyB.baselineClean, true)
})

test('重建（穷举）：在所有与 52/79 命中一致的世界里，只有"第二次挂载活到窗口关闭"能重现 FAIL', () => {
  // 约束来自幸存 detail：出现=+155、首次零命中事件=+4158（存活 4003）、命中样本 52/79。
  // 穷举 = 第二次可见片段的起点 a2 ∈ (4158, 12000] 与终点 b2（null = 窗口关闭时仍可见），
  // 采样栅格与实测一致（0..12000，步长 152）。
  const windowMs = 12_000
  const step = 152
  const grid = []
  for (let t = 0; t <= windowMs; t += step) grid.push(t)
  const visible = (spans, t) => spans.some((span) => t >= span.startMs && (span.endMs === null || t < span.endMs))
  const consistent = []
  for (let a2 = 4_159; a2 <= windowMs; a2 += 1) {
    for (const b2 of [null, ...grid.filter((t) => t > a2)]) {
      const spans = [{ startMs: 155, endMs: 4_158 }, { startMs: a2, endMs: b2 }]
      if (grid.filter((t) => visible(spans, t)).length !== 52) continue
      const lastHit = [...grid].reverse().find((t) => visible(spans, t))
      const quietTail = grid.some((t) => t > lastHit && !visible(spans, t))
      consistent.push({ a2, b2, quietTail })
    }
  }
  const reproducesFail = consistent.filter((world) => world.quietTail === false)
  assert.ok(consistent.length > 3_000, `与 52/79 一致的世界应当很多，实际 ${consistent.length}`)
  assert.ok(reproducesFail.length > 0, '必须存在能重现那次 FAIL 的世界')
  // 关键结论：能重现 FAIL 的世界**全部**是"第二次挂载在窗口关闭时仍可见"，
  // 且起点只能落在 [7905, 8056]（即第一次卸载之后约 3.75–3.9 s，而不是紧随其后）。
  assert.equal(reproducesFail.every((world) => world.b2 === null || world.b2 >= 11_856), true)
  assert.equal(Math.min(...reproducesFail.map((world) => world.a2)), 7_905)
  assert.equal(Math.max(...reproducesFail.map((world) => world.a2)), 8_056)
  // 反证：紧随第一次卸载的第二次挂载（a2=4210，连续可见）在 52/79 约束下不成立——
  // 那时命中会涨到 77/79，与幸存 detail 不符。
  const backToBack = [{ startMs: 155, endMs: 4_158 }, { startMs: 4_210, endMs: null }]
  assert.equal(grid.filter((t) => visible(backToBack, t)).length, 77)
})
