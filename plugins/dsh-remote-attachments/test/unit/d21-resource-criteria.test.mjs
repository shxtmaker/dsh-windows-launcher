/**
 * D21 判据模块单测：冻结表自洽、比较算子语义、序列判定、去重缓存确定性探测与反例。
 *
 * 这里守的不是"产品行为"，而是"判据本身能不能失败"：任何一条 D21 阈值如果恒真，
 * 单测就必须红。产品行为的判定在 tests/fixtures/d21-resource-gates.mjs 里。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import {
  COMPARISONS,
  D21_CRITERIA_VERSION,
  LIMITS,
  METRICS,
  absurdThreshold,
  analyzeSeries,
  buildRatioRow,
  buildRow,
  compare,
  computeVerdicts,
  falsifiabilitySelfTest,
  judgeSeries,
  leastSquaresSlope,
  metricById,
  runDedupProbes,
  runSeriesNegativeControls
} from '../../tests/fixtures/d21-resource-criteria.mjs'

test('冻结表：id 唯一、单位/采样点/阈值齐备，且阈值引用产品常量', () => {
  assert.equal(typeof D21_CRITERIA_VERSION, 'string')
  const ids = METRICS.map((item) => item.id)
  assert.equal(new Set(ids).size, ids.length, `计量项 id 必须唯一：${ids.length} 条`)
  for (const item of METRICS) {
    assert.ok(item.unit.length > 0, `${item.id} 缺 unit`)
    assert.ok(item.samplingPoint.length > 0, `${item.id} 缺 samplingPoint`)
    assert.ok(item.threshold.length > 0, `${item.id} 缺 threshold`)
    assert.ok(Number.isInteger(item.thresholdValue), `${item.id} 的 thresholdValue 必须是整数`)
    assert.ok([COMPARISONS.LTE, COMPARISONS.EQ, COMPARISONS.GTE].includes(item.comparison), `${item.id} 比较算子非法`)
    assert.ok(['browser', 'core', 'unit'].includes(item.layer), `${item.id} 层级非法`)
    assert.ok(['bound', 'negative-control'].includes(item.role ?? 'bound'), `${item.id} role 非法`)
  }
  // 阈值确实来自产品常量（改动产品常量必须同时影响判据，不允许抄一份数字）
  assert.equal(metricById('C01-pending-bytes-peak').thresholdValue, LIMITS.maxChunksInFlight * LIMITS.chunkBytes)
  assert.equal(metricById('C02-pending-chunks-peak').thresholdValue, LIMITS.maxChunksInFlight)
  assert.equal(metricById('B01-max-file-peak-buffered-bytes').thresholdValue, LIMITS.maxFileBytes)
  assert.equal(metricById('B06-max-batch-peak-buffered-bytes').thresholdValue, LIMITS.maxBatchBytes)
  assert.equal(metricById('B13-paused-consumer-buffered-bytes').thresholdValue, LIMITS.maxChunksInFlight * LIMITS.chunkBytes)
  assert.equal(metricById('B18-series-retained-batches').thresholdValue, LIMITS.maxRetainedBatches)
  assert.equal(metricById('B20-series-receiver-only-slope').thresholdValue, 32 * 1024)
  assert.equal(metricById('C16-series-replay-cache-size').thresholdValue, LIMITS.replayCapacity)
  assert.equal(metricById('C04-gate-waiting-peak').thresholdValue, LIMITS.gateQueueCapacity)
})

test('比较算子：lte/eq/gte 语义与"未采样即不通过"', () => {
  assert.equal(compare(COMPARISONS.LTE, 10, 10), true)
  assert.equal(compare(COMPARISONS.LTE, 11, 10), false)
  assert.equal(compare(COMPARISONS.EQ, 0, 0), true)
  assert.equal(compare(COMPARISONS.EQ, 1, 0), false)
  assert.equal(compare(COMPARISONS.GTE, 1, 1), true)
  assert.equal(compare(COMPARISONS.GTE, 0, 1), false)
  assert.equal(compare(COMPARISONS.LTE, Number.NaN, 1), false)
  assert.equal(compare(COMPARISONS.LTE, null, 1), false)
})

test('未采样的计量项一律 notRun 且 pass=false（不得把"没测"写成通过）', () => {
  const row = buildRow('C01-pending-bytes-peak', null)
  assert.equal(row.notRun, true)
  assert.equal(row.pass, false)
  assert.deepEqual(computeVerdicts([row]).failedIds, ['C01-pending-bytes-peak'])
})

test('比例型计量项：分子/分母都来自采样，分母为 0 即未采样', () => {
  const good = buildRatioRow('C09-cancel-sources-disposed', 3, 3)
  assert.equal(good.measured, 1)
  assert.equal(good.pass, true)
  const bad = buildRatioRow('C09-cancel-sources-disposed', 2, 3)
  assert.equal(bad.pass, false)
  const empty = buildRatioRow('C09-cancel-sources-disposed', 0, 0)
  assert.equal(empty.notRun, true)
})

test('已知 id 之外的计量项直接抛错（防止采样脚本写错 id 还通过）', () => {
  assert.throws(() => metricById('X99-not-a-metric'), /未知的 D21 计量项 id/)
})

test('最小二乘斜率与包络：手算样例', () => {
  assert.equal(leastSquaresSlope([0, 1, 2, 3]), 1)
  assert.equal(leastSquaresSlope([10, 10, 10]), 0)
  assert.equal(leastSquaresSlope([5]), 0)
  const analysis = analyzeSeries([100, 200, 150, 400])
  assert.equal(analysis.min, 100)
  assert.equal(analysis.max, 400)
  assert.equal(analysis.envelope, 300)
  assert.equal(analysis.increases, 2)
  assert.equal(analysis.decreases, 1)
  assert.equal(analysis.rounds, 4)
})

test('序列判定：无界增长必须失败、有界抖动必须通过', () => {
  const unbounded = Array.from({ length: 30 }, (_, index) => index * 1024 * 1024)
  const verdict = judgeSeries(unbounded)
  assert.equal(verdict.pass, false, `无界序列必须失败：${verdict.reasons.join('; ')}`)
  const bounded = Array.from({ length: 30 }, (_, index) => 1024 * 1024 + (index % 4) * 4096)
  assert.equal(judgeSeries(bounded).pass, true, `有界抖动必须通过：${judgeSeries(bounded).reasons.join('; ')}`)
})

test('反例控制：无界序列、缓慢泄漏被抓、有界抖动不被误杀', () => {
  const controls = runSeriesNegativeControls()
  assert.equal(controls.ok, true, JSON.stringify(controls.controls))
  for (const control of controls.controls) assert.equal(control.ok, true, `${control.id}：${control.detail}`)
})

test('荒谬阈值自检：每一行的阈值都能真的失败（否则该行恒真）', () => {
  const synthetic = [
    buildRow('B02-max-file-buffered-after', 0),
    buildRow('C01-pending-bytes-peak', LIMITS.maxPendingBytes),
    buildRow('B10-cancel-released-bytes', 2 * LIMITS.chunkBytes)
  ]
  const selfTest = falsifiabilitySelfTest(synthetic)
  assert.equal(selfTest.ok, true, JSON.stringify(selfTest.checked))
  // 让判据自检本身可失败：给它一条"measured 不是数"的行，它必须报 flippedToFail=false
  const broken = falsifiabilitySelfTest([{ id: 'broken', comparison: COMPARISONS.LTE, measured: null }])
  assert.equal(broken.ok, false, '非数实测值必须让自检失败（自检不是恒真）')
})

test('去重缓存确定性探测：TTL 前后、容量淘汰、钉住不驱逐', () => {
  const probe = runDedupProbes({ capacity: 8 })
  const byId = Object.fromEntries(probe.rows.map((row) => [row.id, row]))
  assert.equal(byId['U01-dedup-ttl-live-before'].pass, true, JSON.stringify(byId['U01-dedup-ttl-live-before']))
  assert.equal(byId['U02-dedup-ttl-gone-at'].pass, true, JSON.stringify(byId['U02-dedup-ttl-gone-at']))
  assert.equal(byId['U03-dedup-capacity-bound'].pass, true, JSON.stringify(byId['U03-dedup-capacity-bound']))
  assert.equal(byId['U04-dedup-capacity-evicted'].pass, true, JSON.stringify(byId['U04-dedup-capacity-evicted']))
  assert.equal(byId['U05-dedup-pinned-survives'].pass, true, JSON.stringify(byId['U05-dedup-pinned-survives']))
  assert.equal(byId['U06-dedup-pinned-refusal-counted'].pass, true, JSON.stringify(byId['U06-dedup-pinned-refusal-counted']))
  assert.equal(probe.counterProbe.ok, true, probe.counterProbe.detail)
  assert.equal(probe.observations.capacityProbe.size <= 8, true)
  assert.equal(computeVerdicts(probe.rows).result, 'pass')
})

test('去重缓存 TTL 探测的时钟是注入的：把寿命改成 1ms 也必须确定', () => {
  const probe = runDedupProbes({ capacity: 4, lifetimeMs: 1 })
  const byId = Object.fromEntries(probe.rows.map((row) => [row.id, row]))
  assert.equal(byId['U01-dedup-ttl-live-before'].pass, true)
  assert.equal(byId['U02-dedup-ttl-gone-at'].pass, true)
  assert.equal(probe.observations.ttlProbe.goneAtMs, 1)
})
