/**
 * D13 握手单元测试（导入**构建产物** `lib/`，与既有单测同款）。
 *
 * 覆盖 `hello` / `capabilities` / `context` 的真实检查：
 *  - 协议版本核对（本地 API 被绕过 codec 时也必须拒绝）；
 *  - 对端限额**钳到本端策略与冻结上限**（逐项 declared/effective/clamped 可断言），
 *    并拒绝高于冻结上限的声明；
 *  - 来源规则：页面来源变化（导航）后 `hello`/`capabilities`/`context` 全部被拒；
 *    局域网 HTTP 不是安全上下文，`hashBackend` 如实报告 pure-js；
 *  - 会话身份：没有当前会话、或 context 指向别的会话一律拒绝；
 *  - 能力不可用时 `context` 被拒（结果类代码进 detail 与拒绝结果）。
 *
 * 真实浏览器里的同类判据见 tests/fixtures/d13-bridge-gates.mjs。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import {
  CLIENT_FEATURES,
  HANDSHAKE_VERSION,
  AttachmentHandshake
} from '../../lib/client/index.js'
import {
  ADDON_BUILD,
  CAPABILITY_CODES,
  CAPABILITY_STATUS,
  PACKAGE_VERSION,
  classifyOrigin,
  resolveCapabilityStatus
} from '../../lib/shared/capabilities.js'
import { clampLimits } from '../../lib/shared/limits.js'
import { DEFAULT_LIMITS, PROTOCOL_VERSION, WIRE_FEATURES } from '../../lib/shared/protocol.js'
import { decodeMessage } from '../../lib/shared/wire/index.js'

const LAN = classifyOrigin({
  origin: 'http://192.168.3.190:3099',
  hostname: '192.168.3.190',
  protocol: 'http:',
  isSecureContext: false,
  hasWebCrypto: false
})

const LOOPBACK = classifyOrigin({
  origin: 'http://127.0.0.1:3099',
  hostname: '127.0.0.1',
  protocol: 'http:',
  isSecureContext: false,
  hasWebCrypto: false
})

/** 用生产 codec 构造一条合法报文（保证喂进去的一定是线协议合法形态）。 */
function legal(raw) {
  const decoded = decodeMessage(JSON.stringify(raw))
  assert.equal(decoded.ok, true, `测试报文必须合法：${decoded.ok ? '' : `${decoded.code} ${decoded.detail}`}`)
  return decoded.message
}

const contextOf = (overrides = {}) =>
  legal({
    v: 1,
    type: 'context',
    sessionId: 'session-a',
    targetId: 'target-1',
    documentEpoch: 3,
    composerEpoch: 4,
    composerScope: 'scope-1',
    ...overrides
  })

const helloOf = (clientBuild = 'peer/1.2.3') => legal({ v: 1, type: 'hello', clientBuild })

const capabilitiesOf = (limits = {}, features = [...WIRE_FEATURES]) =>
  legal({ v: 1, type: 'capabilities', features, limits: { ...DEFAULT_LIMITS, ...limits } })

/** 断言拒绝并给出确定拒绝码。 */
function expectReject(result, code, matcher) {
  assert.equal(result.ok, false, `期望拒绝，实际接受：${JSON.stringify(result)}`)
  assert.equal(result.code, code, `拒绝码应为 ${code}，实际 ${result.code}（${result.detail}）`)
  if (matcher !== undefined) assert.match(result.detail, matcher)
  return result
}

test('握手模块身份：版本常量可核对，广告的特性不含 screenshot 且全部来自冻结枚举', () => {
  assert.equal(HANDSHAKE_VERSION, 1)
  assert.ok(CLIENT_FEATURES.length >= 3)
  assert.ok(!CLIENT_FEATURES.includes('screenshot'), '浏览器接收端不得宣称截图能力')
  for (const feature of CLIENT_FEATURES) assert.ok(WIRE_FEATURES.includes(feature))
})

test('本端构建标识符合 schema 的 clientBuild 规则（含 64 字符上限与禁 @）', () => {
  // schema：minLength 1、maxLength 64、pattern ^[A-Za-z0-9._/+:-]+$
  assert.match(ADDON_BUILD, /^[A-Za-z0-9._/+:-]+$/)
  assert.ok(ADDON_BUILD.length >= 1 && ADDON_BUILD.length <= 64, `长度 ${ADDON_BUILD.length}`)
  assert.ok(ADDON_BUILD.includes(PACKAGE_VERSION))
  // 直接喂给生产 codec：本端广告必须真的能过线协议校验。
  const decoded = decodeMessage(JSON.stringify({ v: 1, type: 'hello', clientBuild: ADDON_BUILD }))
  assert.equal(decoded.ok, true, `ADDON_BUILD 必须是合法 clientBuild：${decoded.ok ? '' : decoded.detail}`)
})

test('hello：合法构建标识被接受且记录对端构建；非法构建被拒', () => {
  const handshake = new AttachmentHandshake({ clientBuild: 'ours/0.1.0' })
  const ok = handshake.acceptHello(helloOf('remote-attachments/0.1.0'))
  assert.equal(ok.ok, true)
  assert.equal(handshake.snapshot.peerBuild, 'remote-attachments/0.1.0')
  assert.equal(handshake.snapshot.phase, 'hello')

  // 绕过 codec 直接构造非法构建标识：仍必须被拒（纵深防御）。
  const bad = handshake.acceptHello({ v: 1, type: 'hello', clientBuild: 'has space' })
  expectReject(bad, 'invalid-field-value', /clientBuild/)
  const empty = handshake.acceptHello({ v: 1, type: 'hello', clientBuild: '' })
  expectReject(empty, 'missing-field', /clientBuild/)
})

test('版本核对：v≠1 的报文在握手层也被拒绝（不依赖 codec 是否被绕过）', () => {
  const handshake = new AttachmentHandshake({ clientBuild: 'ours/0.1.0' })
  const hello = handshake.acceptHello({ v: 2, type: 'hello', clientBuild: 'peer@1' })
  expectReject(hello, 'version-mismatch', /hello\.v=2/)
  const context = handshake.acceptContext({ ...contextOf(), v: 2 })
  expectReject(context, 'version-mismatch', /context\.v=2/)
  assert.equal(handshake.snapshot.refusals, 2)
})

test('capabilities：对端限额被钳到本端策略，逐项 declared/effective/clamped 落账', () => {
  const handshake = new AttachmentHandshake({
    clientBuild: 'ours/0.1.0',
    limits: { maxFileBytes: 1024 * 1024, maxFilesPerBatch: 3 }
  })
  // 本端策略本身先钳到冻结上限。
  assert.equal(handshake.snapshot.policyLimits.maxFileBytes, 1024 * 1024)
  assert.equal(handshake.snapshot.policyLimits.maxBatchBytes, DEFAULT_LIMITS.maxBatchBytes)

  const result = handshake.acceptCapabilities(
    capabilitiesOf({ maxFileBytes: 8 * 1024 * 1024, maxBatchBytes: 4096, maxConcurrentTargets: 1 })
  )
  assert.equal(result.ok, true)
  const snapshot = handshake.snapshot
  // 对端声明 8 MiB 高于本端策略 1 MiB（但未超冻结）：必须被钳到 1 MiB。
  assert.equal(snapshot.effectiveLimits.maxFileBytes, 1024 * 1024)
  assert.equal(snapshot.declaredLimits.maxFileBytes, 8 * 1024 * 1024)
  assert.equal(snapshot.effectiveLimits.maxBatchBytes, 4096)
  assert.equal(snapshot.effectiveLimits.maxConcurrentTargets, 1)
  // 未声明的项沿用本端上限。
  assert.equal(snapshot.effectiveLimits.maxFilesPerBatch, 3)
  assert.equal(snapshot.phase, 'ready')

  const fileClamp = snapshot.clamps.find((item) => item.key === 'maxFileBytes')
  assert.deepEqual(
    { declared: fileClamp.declared, effective: fileClamp.effective, clamped: fileClamp.clamped },
    { declared: 8 * 1024 * 1024, effective: 1024 * 1024, clamped: true }
  )
  const batchClamp = snapshot.clamps.find((item) => item.key === 'maxBatchBytes')
  assert.equal(batchClamp.clamped, false, '声明低于上限时不算被钳')
})

test('capabilities：声明高于冻结上限被拒；未知特性被拒；生效限额永不超冻结', () => {
  const handshake = new AttachmentHandshake({ clientBuild: 'ours/0.1.0' })
  // 越界声明与未知特性在正常路径上会先被生产 codec 拒绝（见下方负向断言）；
  // 这里刻意**绕过 codec** 直接喂给握手层，证明纵深防御存在。
  const over = handshake.acceptCapabilities({
    v: 1,
    type: 'capabilities',
    features: [...WIRE_FEATURES],
    limits: { ...DEFAULT_LIMITS, maxFileBytes: DEFAULT_LIMITS.maxFileBytes + 1 }
  })
  expectReject(over, 'integer-out-of-range', /maxFileBytes/)
  assert.equal(
    decodeMessage(
      JSON.stringify({
        v: 1,
        type: 'capabilities',
        features: [...WIRE_FEATURES],
        limits: { ...DEFAULT_LIMITS, maxFileBytes: DEFAULT_LIMITS.maxFileBytes + 1 }
      })
    ).ok,
    false,
    '同一份越界声明在生产 codec 上也必须被拒'
  )

  const unknown = handshake.acceptCapabilities({
    v: 1,
    type: 'capabilities',
    features: ['chunked-transfer', 'teleport'],
    limits: { ...DEFAULT_LIMITS }
  })
  expectReject(unknown, 'unknown-enum-value', /teleport/)
  assert.equal(
    decodeMessage(
      JSON.stringify({
        v: 1,
        type: 'capabilities',
        features: ['chunked-transfer', 'teleport'],
        limits: { ...DEFAULT_LIMITS }
      })
    ).ok,
    false,
    '同一份未知特性在生产 codec 上也必须被拒'
  )

  const good = handshake.acceptCapabilities(capabilitiesOf({ maxStagingBytesPerTarget: 1 }))
  assert.equal(good.ok, true)
  for (const [key, value] of Object.entries(handshake.snapshot.effectiveLimits)) {
    assert.ok(value <= DEFAULT_LIMITS[key], `${key} 生效值 ${value} 不得超过冻结上限 ${DEFAULT_LIMITS[key]}`)
  }
})

test('clampLimits：本端策略也被钳到冻结上限，非法声明在 violations 里逐项列出', () => {
  const clamped = clampLimits(
    { maxFileBytes: 4096, maxFilesPerBatch: 99, maxBatchBytes: -1 },
    { maxFileBytes: DEFAULT_LIMITS.maxFileBytes * 4 }
  )
  assert.equal(clamped.limits.maxFileBytes, 4096)
  assert.equal(clamped.limits.maxFilesPerBatch, DEFAULT_LIMITS.maxFilesPerBatch, '声明超冻结即被钳')
  const reasons = clamped.violations.map((item) => `${item.key}:${item.reason}`)
  assert.deepEqual(reasons.sort(), ['maxBatchBytes:below-minimum', 'maxFilesPerBatch:above-frozen'])
  assert.equal(clamped.ok, false)
})

test('来源规则：局域网 HTTP 不是安全上下文，hashBackend 如实报 pure-js；loopback 不当作局域网', () => {
  assert.equal(LAN.originClass, 'lan')
  assert.equal(LAN.isSecureContext, false)
  assert.equal(LAN.hasWebCrypto, false)
  assert.equal(LAN.hashBackend, 'pure-js')
  assert.equal(LOOPBACK.originClass, 'loopback')
  assert.equal(classifyOrigin({ origin: 'https://example.test', hostname: 'example.test' }).originClass, 'lan')
})

test('来源规则：导航后（来源变化）hello/capabilities/context 全部拒绝', () => {
  let current = LAN
  const handshake = new AttachmentHandshake({ clientBuild: 'ours/0.1.0', origin: () => current })
  assert.equal(handshake.acceptHello(helloOf()).ok, true)

  // 模拟页面导航到另一个来源：同一个握手对象（原生层可能还持有引用）必须拒绝后续一切。
  current = classifyOrigin({ origin: 'http://10.0.0.9:3099', hostname: '10.0.0.9', protocol: 'http:' })
  expectReject(handshake.acceptHello(helloOf()), 'context-changed', /来源已由/)
  expectReject(handshake.acceptCapabilities(capabilitiesOf()), 'context-changed')
  expectReject(handshake.acceptContext(contextOf()), 'context-changed')
  assert.equal(handshake.snapshot.originChanged, true)
})

test('会话身份：没有当前会话 / context 指向别的会话一律拒绝', () => {
  let current = 'session-b'
  const handshake = new AttachmentHandshake({
    clientBuild: 'ours/0.1.0',
    origin: () => LAN,
    currentSession: () => current
  })
  expectReject(handshake.acceptContext(contextOf({ sessionId: 'session-a' })), 'context-changed', /不是宿主当前会话/)
  assert.equal(handshake.acceptContext(contextOf({ sessionId: 'session-b' })).ok, true)

  current = null
  expectReject(handshake.acceptContext(contextOf({ sessionId: 'session-b' })), 'no-session', /没有可编辑会话/)
})

test('会话身份：没有有效 sessionId / 非法 epoch 一律拒绝（绕过 codec 也要拒绝）', () => {
  const handshake = new AttachmentHandshake({ clientBuild: 'ours/0.1.0', origin: () => LAN })
  // 这些报文**故意**不合法（codec 会先拒），因此不经 decodeStrict，直接喂给握手层，
  // 证明纵深防御真的存在，而不是"反正 codec 会拦"。
  expectReject(handshake.acceptContext({ ...contextOf(), sessionId: '' }), 'missing-field', /sessionId/)
  expectReject(handshake.acceptContext({ ...contextOf(), sessionId: 'has space' }), 'invalid-field-value', /sessionId/)
  expectReject(handshake.acceptContext({ ...contextOf(), targetId: '' }), 'missing-field', /targetId/)
  expectReject(handshake.acceptContext({ ...contextOf(), documentEpoch: -1 }), 'negative-integer', /documentEpoch/)
  expectReject(
    handshake.acceptContext({ ...contextOf(), composerEpoch: 2_147_483_648 }),
    'integer-out-of-range',
    /composerEpoch/
  )
  expectReject(handshake.acceptContext({ ...contextOf(), composerScope: 'x/y' }), 'invalid-field-value', /composerScope/)
})

test('能力闸门：能力不可用时 context 被拒，且拒绝结果带确定的结果类代码', () => {
  const conflict = resolveCapabilityStatus({
    remoteChannel: true,
    harnessDraftImport: true,
    uploadHook: 'conflict',
    origin: LAN
  })
  assert.equal(conflict.status, CAPABILITY_STATUS.unavailable)
  assert.equal(conflict.code, CAPABILITY_CODES.capabilityConflict)

  const handshake = new AttachmentHandshake({
    clientBuild: 'ours/0.1.0',
    origin: () => LAN,
    capability: () => conflict
  })
  const rejected = expectReject(handshake.acceptContext(contextOf()), 'no-session', /capability-conflict/)
  assert.equal(rejected.capabilityCode, CAPABILITY_CODES.capabilityConflict)
  assert.equal(handshake.snapshot.phase, 'refused')
  assert.equal(handshake.snapshot.context, null, '被拒的 context 不得建立身份')

  // 反例（非空洞性）：同一份 context 在能力可用时**必须**被接受。
  const healthy = new AttachmentHandshake({ clientBuild: 'ours/0.1.0', origin: () => LAN })
  assert.equal(healthy.acceptContext(contextOf()).ok, true)
})

test('本端广告：hello + capabilities 顺序固定，且都能被生产 codec 解码', () => {
  const handshake = new AttachmentHandshake({
    clientBuild: 'ours/0.1.0',
    limits: { maxFileBytes: DEFAULT_LIMITS.maxFileBytes * 2 }
  })
  const outgoing = handshake.outgoing()
  assert.equal(outgoing.length, 2)
  assert.equal(outgoing[0].type, 'hello')
  assert.equal(outgoing[1].type, 'capabilities')
  for (const message of outgoing) {
    const decoded = decodeMessage(JSON.stringify(message))
    assert.equal(decoded.ok, true, `广告报文必须合法：${decoded.ok ? '' : decoded.code}`)
  }
  assert.equal(outgoing[1].limits.maxFileBytes, DEFAULT_LIMITS.maxFileBytes, '广告的限额不得超过冻结上限')
  assert.equal(PROTOCOL_VERSION, 1)
})

test('握手快照可 JSON 序列化（状态面不会因为函数/循环引用而炸）', () => {
  const handshake = new AttachmentHandshake({ clientBuild: 'ours/0.1.0', origin: () => LAN })
  handshake.acceptHello(helloOf())
  handshake.acceptCapabilities(capabilitiesOf({ maxFileBytes: 2048 }))
  const text = JSON.stringify(handshake.snapshot)
  assert.ok(text.includes('"phase":"ready"'))
  assert.ok(text.includes('"effectiveLimits"'))
  assert.ok(!text.includes('"ready"') || !text.includes('upload'), '快照里不得出现 upload ready')
})
