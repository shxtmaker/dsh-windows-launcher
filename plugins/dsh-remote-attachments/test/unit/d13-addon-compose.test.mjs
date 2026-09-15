/**
 * D13 组合层单元测试（导入**构建产物** `lib/`，与既有单测同款）。
 *
 * 覆盖"组合附加插件桥"这条链路的**行为**，而不是只断言函数存在：
 *
 *  - 接收端 → 生产桥 → 生产 draft-adapter → 原生输入状态：组装出的 File 真的经草稿路径导入，
 *    新增的原生 attachmentId 由**状态读取**确认（不是 DOM 芯片、不是 dispatchEvent 返回值）；
 *  - 三层状态**不合并**：transport(buffered) / draft(staged) / upload(harness-owned) 分开报告，
 *    状态面里不存在 upload ready；
 *  - 部分失败按 fileId 逐条可见（哪个成功、哪个失败、码是什么），成功的草稿结果被保留；
 *  - 能力冲突（未知 `__DSH_FILE_UPLOAD__`）与 remote 降级 ⇒ 接收端拒绝整批、绝不导入，
 *    未知 hook 与 remote seat 一个字节都不动；
 *  - 生命周期撤销：自有全局被删除、保留引用也拒绝、ReloadRequired 如实上报；
 *  - 撤销/降级后的负向探针，证明判据不是恒真。
 *
 * 这里用**假原生状态**（假 sessions/conversation/document/input + DataTransfer 替身）是因为
 * Node 里没有真实 DOM；真实浏览器中的同类判据见 tests/fixtures/d13-bridge-gates.mjs。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import {
  ADDON_GLOBAL,
  BRIDGE_GLOBAL,
  RECEIVER_GLOBAL,
  composeAttachmentsAddon,
  hasRemoteSeat,
  probeCapability,
  probeUploadHook
} from '../../lib/client/index.js'
import { CARRIER_BRAND } from '../../lib/client/compose.js'
import {
  CAPABILITY_CODES,
  CAPABILITY_STATUS,
  CARRIER_BRAND as SHARED_CARRIER_BRAND,
  STATUS_GLOBAL,
  UPLOAD_HOOK_GLOBAL,
  classifyOrigin
} from '../../lib/shared/capabilities.js'
import { decodeMessage } from '../../lib/shared/wire/index.js'
import * as clientEntry from '../../lib/client/index.js'
import {
  SESSION_ID,
  batchBegin,
  batchEndMessage,
  cancelMessage,
  chunkMessages,
  contextMessage,
  feed,
  fileBegin,
  fileEnd,
  sourceBytes
} from './receiver-fixtures.mjs'
import { sha256HexSync } from '../../lib/shared/wire/index.js'

const LAN_ORIGIN = classifyOrigin({
  origin: 'http://192.168.3.190:3099',
  hostname: '192.168.3.190',
  protocol: 'http:',
  isSecureContext: false,
  hasWebCrypto: false
})

// ---------- 假原生状态（真实 DOM 的判据在浏览器闸门里） ----------

/** 最小 DataTransfer 替身：适配器只用 items.add 与 files。 */
class FakeDataTransfer {
  constructor() {
    this._files = []
  }
  get items() {
    const files = this._files
    return { add: (file) => files.push(file) }
  }
  get files() {
    return this._files
  }
}

// Node 里没有 DataTransfer，而生产适配器读的是**全局**构造器（浏览器内置）。
// 这里给一个最小替身，让"接收端 → 桥 → 生产 draft-adapter → 原生状态"这条链路在 Node
// 里也能真的跑通；真实浏览器里的同类判据见 tests/fixtures/d13-bridge-gates.mjs。
if (typeof globalThis.DataTransfer !== 'function') {
  globalThis.DataTransfer = FakeDataTransfer
}

/** 假原生输入状态：attachmentIds + phase，以及"change 之后新增 ID"的真实链路。 */
function createNativeState({ current = SESSION_ID } = {}) {
  const state = {
    attachmentIds: [],
    phase: 'plain',
    current,
    changeCount: 0,
    /** 让下一次 change 不产生任何新增（模拟原生校验拒绝）。 */
    rejectNext: false,
    setCurrent(next) {
      state.current = next
    }
  }

  let sequence = 0
  const input = {
    type: 'file',
    disabled: false,
    files: [],
    listeners: [],
    addEventListener(type, listener) {
      if (type === 'change') input.listeners.push(listener)
    },
    dispatchEvent(event) {
      for (const listener of input.listeners) listener(event)
      return true
    }
  }
  input.addEventListener('change', () => {
    state.changeCount += 1
    if (state.rejectNext) {
      state.rejectNext = false
      return
    }
    for (const file of input.files) {
      sequence += 1
      state.attachmentIds.push(`native-att-${sequence}-${file.name}`)
    }
  })

  const card = {
    querySelectorAll: (selector) => (selector === 'input[type=file]' ? [input] : []),
    closest: () => card
  }
  const document = {
    querySelectorAll: (selector) => (selector === '[data-composer-card]' ? [card] : [])
  }
  const sessionInput = {
    state: {
      getSnapshot: () => ({ attachmentIds: [...state.attachmentIds], phase: state.phase })
    }
  }
  const blockStore = { getSnapshot: () => undefined }
  const conversation = {
    input: { for: () => sessionInput },
    blocks: { storeFor: () => blockStore }
  }
  const sessions = {
    scope: (sessionId) => (sessionId === state.current ? { get: (name) => (name === 'conversation' ? conversation : undefined) } : undefined),
    list: { getSnapshot: () => ({ current: state.current }) }
  }
  return { state, input, document, sessions }
}

/** 造一个组合层 scope（页面全局替身）。 */
function createScope({ hook, seat = true, carrier = true } = {}) {
  const scope = { DataTransfer: FakeDataTransfer, isSecureContext: false }
  if (seat) {
    scope['__DSH_REMOTE_CHANNEL_BOOT__'] = { restore: () => {}, onUnpaired: null, onPaired: null }
  }
  if (carrier) {
    scope[UPLOAD_HOOK_GLOBAL] = {
      fetch: (input) => Promise.resolve({ ok: true, status: 200, url: String(input) }),
      brand: SHARED_CARRIER_BRAND
    }
  }
  if (hook !== undefined) scope[UPLOAD_HOOK_GLOBAL] = hook
  return scope
}

function compose(native, scope, extra = {}) {
  return composeAttachmentsAddon({
    scope,
    ctx: { get: (name) => (name === 'sessions' ? native.sessions : undefined) },
    document: native.document,
    origin: () => LAN_ORIGIN,
    ...extra
  })
}

/** 走完一个文件的完整传输（含分块与 file-end），返回最后一次 feed 的结果。 */
async function transfer(receiver, { batchId, fileId, name, bytes, chunkBytes = 4096 }) {
  for (const chunk of chunkMessages(bytes, { chunkBytes, batchId, fileId })) {
    const { result } = await feed(receiver, chunk)
    assert.equal(result.ok, true, `块必须被接受：${result.ok ? '' : result.code}`)
  }
  return feed(receiver, fileEnd({ batchId, fileId, totalBytes: bytes.length, sha256: sha256HexSync(bytes) }))
}

// ---------- 用例 ----------

test('能力探测：健康局域网页面（有 seat + 本插件承载 + 宿主服务）判定为 available', () => {
  const native = createNativeState()
  const scope = createScope()
  const facts = probeCapability({
    scope,
    ctx: { get: (name) => (name === 'sessions' ? native.sessions : undefined) },
    origin: () => LAN_ORIGIN
  })
  assert.equal(facts.resolution.status, CAPABILITY_STATUS.available)
  assert.equal(facts.facts.remoteSeat, true)
  assert.equal(facts.facts.uploadHook, 'installed')
  assert.equal(facts.facts.uploadHookOwnership, 'ours')
  assert.equal(facts.facts.originClass, 'lan')
})

test('握手广告：组合层与接收端都发出 hello + capabilities，且限额被钳到冻结上限', () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope, { limits: { maxFileBytes: 1_000_000_000 } })
  const advertised = composed.beginHandshake()
  assert.equal(advertised.length, 2)
  assert.equal(advertised[0].type, 'hello')
  assert.equal(advertised[1].type, 'capabilities')
  for (const message of advertised) {
    assert.equal(decodeMessage(JSON.stringify(message)).ok, true, '广告报文必须能过生产 codec')
  }
  assert.equal(advertised[1].limits.maxFileBytes, 20 * 1024 * 1024, '限额必须被钳到冻结上限')

  const receiver = composed.receiverHost.create()
  const fromReceiver = receiver.beginHandshake()
  assert.equal(fromReceiver.length, 2)
  assert.deepEqual(
    fromReceiver.map((message) => message.type),
    ['hello', 'capabilities']
  )
  assert.deepEqual(receiver.beginHandshake(), [], '握手广告是幂等的，不重复发')
  assert.equal(
    composed.handle.status().advertised.hello.clientBuild,
    fromReceiver[0].clientBuild,
    '状态面与接收端广告必须同源'
  )
})

test('端到端：分块组装 → 生产草稿路径 → 原生 attachmentIds 新增；三层状态不合并', async () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope)
  const receiver = composed.receiverHost.create()
  const bytes = sourceBytes(9000)

  const context = await feed(receiver, contextMessage())
  assert.equal(context.result.ok, true, `context 必须被接受：${context.result.ok ? '' : context.result.detail}`)
  const begin = await feed(receiver, batchBegin({ batchId: 'batch-1', totalBytes: bytes.length }))
  assert.equal(begin.result.ok, true)
  const file = await feed(
    receiver,
    fileBegin({ batchId: 'batch-1', fileId: 'file-1', name: 'd13-unit.bin', byteLength: bytes.length, sha256: sha256HexSync(bytes) })
  )
  assert.equal(file.result.ok, true)

  const end = await transfer(receiver, { batchId: 'batch-1', fileId: 'file-1', name: 'd13-unit.bin', bytes })
  assert.equal(end.result.ok, true, `file-end 必须被接受：${end.result.ok ? '' : end.result.detail}`)
  assert.equal(end.result.importInvoked, true)
  assert.equal(end.result.import.result.ok, true, JSON.stringify(end.result.import.result))

  // 三层状态：transport=buffered（接收缓冲已接受）≠ draft=staged ≠ upload=harness-owned。
  assert.equal(end.result.file.transport, 'buffered')
  assert.equal(end.result.file.draft, 'staged')
  assert.equal(end.result.file.upload, 'harness-owned')

  // 原生草稿真的新增了附件（读取原生状态，不是 DOM 芯片、不是 dispatchEvent 返回值）。
  assert.equal(native.state.attachmentIds.length, 1)
  assert.equal(native.state.changeCount, 1, '只触发一次 change')
  assert.deepEqual(end.result.import.result.added, native.state.attachmentIds)

  const status = composed.handle.status()
  assert.deepEqual(status.draft.staged, ['file-1'])
  assert.deepEqual(status.draft.failed, [])
  assert.deepEqual(status.partialFailures, [])
  assert.equal(status.uploadOwnership, 'harness-owned')
  assert.equal(status.files[0].status, 'staged')
  assert.equal(status.files[0].upload, 'harness-owned')
  assert.equal(status.transport.importsInvoked, 1)
  assert.equal(status.transport.importsSucceeded, 1)
  // 状态面**从不**出现 upload ready：只有 none / harness-owned。
  const serialized = JSON.stringify(status)
  assert.ok(!/"upload":"ready"/.test(serialized), '状态面不得宣称上传就绪')
  assert.ok(!/"ready"/.test(serialized) || serialized.includes('"phase"'), '状态面不得出现 ready 取值')

  // 关批：已 staged 的文件不得被整体重复导入。
  const closed = await feed(
    receiver,
    batchEndMessage('batch-1', [{ fileId: 'file-1', status: 'staged', attachmentIds: end.result.import.result.added }])
  )
  assert.equal(closed.result.ok, true)
  assert.equal(native.state.attachmentIds.length, 1, '关批不得再导入一次')
})

test('部分失败按 fileId 逐条可见：坏哈希失败、已 staged 的成功项被保留', async () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope)
  const receiver = composed.receiverHost.create()
  const good = sourceBytes(5000, 11)
  const bad = sourceBytes(5000, 12)

  assert.equal((await feed(receiver, contextMessage())).result.ok, true)
  assert.equal((await feed(receiver, batchBegin({ batchId: 'batch-2', fileCount: 2, totalBytes: good.length + bad.length }))).result.ok, true)

  assert.equal(
    (await feed(receiver, fileBegin({ batchId: 'batch-2', fileId: 'file-good', name: 'good.bin', byteLength: good.length, sha256: sha256HexSync(good) }))).result.ok,
    true
  )
  const goodEnd = await transfer(receiver, { batchId: 'batch-2', fileId: 'file-good', name: 'good.bin', bytes: good })
  assert.equal(goodEnd.result.import.result.ok, true)

  assert.equal(
    (await feed(receiver, fileBegin({ batchId: 'batch-2', fileId: 'file-bad', name: 'bad.bin', byteLength: bad.length }))).result.ok,
    true
  )
  for (const chunk of chunkMessages(bad, { chunkBytes: 4096, batchId: 'batch-2', fileId: 'file-bad' })) {
    assert.equal((await feed(receiver, chunk)).result.ok, true)
  }
  const badEnd = await feed(
    receiver,
    fileEnd({ batchId: 'batch-2', fileId: 'file-bad', totalBytes: bad.length, sha256: 'f'.repeat(64) })
  )
  assert.equal(badEnd.result.ok, false)
  assert.equal(badEnd.result.code, 'hash-mismatch')

  const status = composed.handle.status()
  assert.deepEqual(status.draft.staged, ['file-good'])
  assert.deepEqual(status.draft.failed, ['file-bad'])
  assert.deepEqual(status.partialFailures.map((item) => `${item.fileId}:${item.code}`), ['file-bad:hash-mismatch'])
  // 成功的草稿结果被保留：原生状态里仍有 1 个附件，失败的文件没有新增。
  assert.equal(native.state.attachmentIds.length, 1)
  assert.equal(status.transport.importsFailed, 0, '校验失败发生在导入之前，不算导入失败')
})

test('草稿层失败（原生输入禁用）逐文件报告 draft-import-failed，且不谎报 staged', async () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope)
  const receiver = composed.receiverHost.create()
  const bytes = sourceBytes(2048, 21)

  assert.equal((await feed(receiver, contextMessage())).result.ok, true)
  assert.equal((await feed(receiver, batchBegin({ batchId: 'batch-3', totalBytes: bytes.length }))).result.ok, true)
  assert.equal(
    (await feed(receiver, fileBegin({ batchId: 'batch-3', fileId: 'file-3', name: 'blocked.bin', byteLength: bytes.length, sha256: sha256HexSync(bytes) }))).result.ok,
    true
  )
  native.input.disabled = true
  const end = await transfer(receiver, { batchId: 'batch-3', fileId: 'file-3', name: 'blocked.bin', bytes })
  assert.equal(end.result.ok, true, 'file-end 本身被接受（传输没问题）')
  assert.equal(end.result.import.result.ok, false)
  assert.equal(end.result.import.result.code, 'file-input-disabled')
  assert.equal(end.result.import.message.status, 'failed')
  assert.equal(end.result.import.message.code, 'draft-import-failed')

  const status = composed.handle.status()
  assert.deepEqual(status.draft.staged, [])
  assert.deepEqual(status.draft.failed, ['file-3'])
  assert.deepEqual(status.partialFailures.map((item) => `${item.fileId}:${item.code}`), ['file-3:draft-import-failed'])
  assert.equal(native.state.attachmentIds.length, 0, '原生草稿不得凭空多出附件')
  assert.equal(status.transport.importsFailed, 1)
})

test('能力冲突：已存在未知 __DSH_FILE_UPLOAD__ ⇒ 拒绝整批、发出 cancel、绝不触碰未知 hook', async () => {
  const native = createNativeState()
  const unknown = { fetch: () => Promise.resolve({ ok: true }), note: 'unknown-hook' }
  const scope = createScope({ hook: unknown })
  const composed = compose(native, scope)
  const receiver = composed.receiverHost.create()
  const bytes = sourceBytes(1024, 31)

  const status = composed.handle.status()
  assert.equal(status.capability.status, CAPABILITY_STATUS.unavailable)
  assert.equal(status.capability.code, CAPABILITY_CODES.capabilityConflict)
  assert.equal(status.upload.hook, 'conflict')
  assert.equal(status.upload.ownership, 'unknown')
  assert.equal(status.upload.route, 'unavailable')
  assert.equal(scope[STATUS_GLOBAL].addon.capability.code, CAPABILITY_CODES.capabilityConflict)

  // 拒绝发生在建立身份之前：context 被拒，批次连不上身份。
  const context = await feed(receiver, contextMessage())
  assert.equal(context.result.ok, false)
  assert.equal(context.result.code, 'no-session')
  assert.match(context.result.detail, /capability-conflict/)

  const begin = await feed(receiver, batchBegin({ batchId: 'batch-conflict', totalBytes: bytes.length }))
  assert.equal(begin.result.ok, false)
  assert.match(begin.result.detail, /capability-conflict/)
  const refusal = begin.outgoing.find((message) => message.type === 'cancel')
  assert.ok(refusal, `能力拒绝必须回一条 cancel：${JSON.stringify(begin.outgoing)}`)
  assert.equal(refusal.reason, CAPABILITY_CODES.capabilityConflict)
  assert.equal(refusal.stage, 'protocol-transfer')
  assert.equal(decodeMessage(JSON.stringify(refusal)).ok, true, 'cancel 必须是线协议合法报文')

  // 未导入任何东西；未知 hook 原样保留（同一个对象、同样的字段）。
  assert.equal(composed.handle.status().transport.importsInvoked, 0)
  assert.equal(native.state.attachmentIds.length, 0)
  assert.equal(scope[UPLOAD_HOOK_GLOBAL], unknown)
  assert.equal(scope[UPLOAD_HOOK_GLOBAL].note, 'unknown-hook')
  assert.equal(probeUploadHook(scope).state, 'conflict')
})

test('remote 降级：局域网页面没有改写层 ⇒ 能力不可用、上传路径不再声称 remote-rewrite', async () => {
  const native = createNativeState()
  const scope = createScope({ seat: false })
  const composed = compose(native, scope)
  const receiver = composed.receiverHost.create()

  const status = composed.handle.status()
  assert.equal(status.capability.status, CAPABILITY_STATUS.unavailable)
  assert.equal(status.capability.code, CAPABILITY_CODES.capabilityDisabled)
  assert.equal(status.capability.facts.remoteSeat, false)
  assert.equal(status.upload.route, 'refused-no-remote-channel')
  assert.match(status.capability.reason, /远程通道不可用/)

  const begin = await feed(receiver, batchBegin({ batchId: 'batch-degraded', totalBytes: 10 }))
  assert.equal(begin.result.ok, false)
  assert.equal(composed.handle.status().transport.importsInvoked, 0)
  assert.equal(native.state.attachmentIds.length, 0)
})

test('remote 只读探测：本插件不改写、不 restore 别人的 seat；loopback 视为本机直连', () => {
  const seat = { restore: () => { throw new Error('不得调用 remote 的 restore') }, marker: 1 }
  const scope = { __DSH_REMOTE_CHANNEL_BOOT__: seat }
  assert.equal(hasRemoteSeat(scope), true)
  assert.equal(scope['__DSH_REMOTE_CHANNEL_BOOT__'], seat, '探测不得替换 seat')

  const native = createNativeState()
  const loopback = classifyOrigin({ origin: 'http://127.0.0.1:3099', hostname: '127.0.0.1', protocol: 'http:' })
  const noSeat = { DataTransfer: FakeDataTransfer }
  const facts = probeCapability({
    scope: noSeat,
    origin: () => loopback,
    ctx: { get: (name) => (name === 'sessions' ? native.sessions : undefined) }
  })
  assert.equal(facts.facts.remoteChannel, true, 'loopback 上 remote 的 boot 脚本按设计跳过，不算降级')
  assert.equal(facts.facts.remoteSeat, false)
})

test('宿主禁用标记（client 拿不到行配置时）：按 host.enabled=false 判 degraded 并拒绝导入', async () => {
  // 真实 Harness 下 cordis 的浏览器半区加载器不带行配置，client 半区只能靠 host 写的标记
  // （见 host/upload-hook.ts 的 buildDisabledHookScript）。没有这条，loopback 页面上
  // 被禁用的插件会把自己报成 available 并且照旧导入——禁用失效（D23 实测）。
  const native = createNativeState()
  const scope = createScope()
  scope[STATUS_GLOBAL] = { host: { enabled: false, hookVersion: 2, brand: SHARED_CARRIER_BRAND, carrierShape: 'none' } }
  const composed = compose(native, scope)

  const status = composed.handle.status()
  assert.equal(status.capability.status, CAPABILITY_STATUS.degraded)
  assert.equal(status.capability.code, CAPABILITY_CODES.capabilityDisabled)
  assert.equal(status.capability.facts.enabled, false)
  const imported = composed.bridge.importFiles({ files: [{ name: 'x.bin', type: 'text/plain' }] })
  assert.equal(imported.ok, false)
  assert.equal(imported.code, 'capability-disabled')
  assert.equal(native.state.attachmentIds.length, 0, '被禁用时不得把文件塞进草稿')
})

test('显式禁用（enabled:false）：报告 degraded 且拒绝导入，但不动 remote seat 与承载', async () => {
  const native = createNativeState()
  const scope = createScope()
  const seat = scope['__DSH_REMOTE_CHANNEL_BOOT__']
  const carrier = scope[UPLOAD_HOOK_GLOBAL]
  const composed = compose(native, scope, { config: { enabled: false } })

  const status = composed.handle.status()
  assert.equal(status.capability.status, CAPABILITY_STATUS.degraded)
  assert.equal(status.capability.facts.enabled, false)
  assert.equal(scope['__DSH_REMOTE_CHANNEL_BOOT__'], seat, '禁用附件不得触碰 remote seat（配对/心跳不受影响）')
  assert.equal(scope[UPLOAD_HOOK_GLOBAL], carrier)

  const receiver = composed.receiverHost.create()
  const begin = await feed(receiver, batchBegin({ batchId: 'batch-off', totalBytes: 10 }))
  assert.equal(begin.result.ok, false)
  assert.equal(composed.handle.status().transport.importsInvoked, 0)
})

test('生命周期撤销：删除自有全局、保留引用也拒绝导入、如实上报 ReloadRequired', async () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope, { cordisEffect: true })
  const receiver = composed.receiverHost.create()
  const bytes = sourceBytes(3000, 41)

  assert.equal((await feed(receiver, contextMessage())).result.ok, true)
  assert.equal((await feed(receiver, batchBegin({ batchId: 'batch-teardown', totalBytes: bytes.length }))).result.ok, true)
  assert.equal(
    (await feed(receiver, fileBegin({ batchId: 'batch-teardown', fileId: 'file-t', name: 't.bin', byteLength: bytes.length, sha256: sha256HexSync(bytes) }))).result.ok,
    true
  )
  // 只发一块：缓冲里有字节，撤销必须释放它。
  const [firstChunk] = chunkMessages(bytes, { chunkBytes: 4096, batchId: 'batch-teardown', fileId: 'file-t' })
  assert.equal((await feed(receiver, firstChunk)).result.ok, true)
  assert.ok(receiver.accounting.bufferedBytes > 0)

  const before = receiver.accounting
  const status = composed.handle.dispose('fixture-disable')

  // 1) 自有全局被撤销（身份检查：只删自己的）。
  assert.equal(scope[BRIDGE_GLOBAL], undefined)
  assert.equal(scope[RECEIVER_GLOBAL], undefined)
  assert.equal(scope[ADDON_GLOBAL], undefined)
  assert.equal(scope[UPLOAD_HOOK_GLOBAL], undefined, '本插件安装的承载随撤销一起撤掉')
  // 2) 撤销后仍能读到"需要重载"。
  assert.equal(scope[STATUS_GLOBAL].addon.state, 'disposed')
  assert.equal(scope[STATUS_GLOBAL].addon.reloadRequired.required, true)
  assert.match(scope[STATUS_GLOBAL].addon.reloadRequired.reason, /reload-required/)
  assert.equal(status.state, 'disposed')
  assert.equal(status.lifecycle.cordisEffect, true)
  assert.equal(status.lifecycle.disposeCount, 1)
  // 3) 保留引用也无法再导入：缓冲被释放，后续消息一律拒绝。
  assert.equal(receiver.accounting.bufferedBytes, 0)
  assert.equal(receiver.isDisposed, true)
  const after = await feed(receiver, fileEnd({ batchId: 'batch-teardown', fileId: 'file-t', totalBytes: bytes.length, sha256: sha256HexSync(bytes) }))
  assert.equal(after.result.ok, false)
  assert.match(after.result.detail, /capability-disabled/)
  assert.equal(receiver.accounting.importsInvoked, before.importsInvoked)
  assert.equal(native.state.attachmentIds.length, 0)

  // 4) 幂等：重复撤销不再计数，也不改变结论。
  const second = composed.handle.dispose('again')
  assert.equal(second.lifecycle.disposeCount, 1)
  assert.equal(second.state, 'disposed')
})

test('撤销的负向对照：未知 hook 不得被删除，且不谎报 ReloadRequired', () => {
  const native = createNativeState()
  const unknown = { fetch: () => Promise.resolve({ ok: true }), note: 'not-ours' }
  const scope = createScope({ hook: unknown })
  const composed = compose(native, scope)
  const status = composed.handle.dispose('fixture-disable-unknown')
  assert.equal(scope[UPLOAD_HOOK_GLOBAL], unknown, '未知 hook 必须原样留在页面上')
  assert.equal(scope[UPLOAD_HOOK_GLOBAL].note, 'not-ours')
  assert.equal(status.reloadRequired.required, false, '没有本插件承载被捕获时不得要求重载')
  assert.equal(status.reloadRequired.carrierCapturedAtBoot, false)
  assert.equal(scope[BRIDGE_GLOBAL], undefined, '自有全局仍必须撤销')
})

test('撤销后能力判定不得再回报 available（D23：恢复全局变量 ≠ 恢复 runtime）', () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope, { cordisEffect: true })

  // 撤销前：页面事实齐备 ⇒ available（对照，确保下面的结论不是恒假）。
  assert.equal(composed.handle.capability().status, CAPABILITY_STATUS.available)
  assert.equal(composed.handle.status().capability.status, CAPABILITY_STATUS.available)

  // 记下撤销时被删掉的全局，随后**原样放回**（模拟"只恢复全局变量"的恢复尝试）。
  const savedCarrier = scope[UPLOAD_HOOK_GLOBAL]
  const savedBridge = scope[BRIDGE_GLOBAL]
  const savedReceiver = scope[RECEIVER_GLOBAL]
  composed.handle.dispose('d23-restore-probe')
  scope[UPLOAD_HOOK_GLOBAL] = savedCarrier
  scope[BRIDGE_GLOBAL] = savedBridge
  scope[RECEIVER_GLOBAL] = savedReceiver
  scope[ADDON_GLOBAL] = composed.handle

  // 页面事实看起来完全"健康"（承载还是我们的、seat 还在、宿主服务还在）……
  assert.equal(probeUploadHook(scope).state, 'installed')
  assert.equal(
    probeCapability({ scope, ctx: { get: (name) => (name === 'sessions' ? native.sessions : undefined) }, document: native.document }).resolution.status,
    CAPABILITY_STATUS.available
  )

  // ……但这个实例已经撤销：能力判定与状态面都必须说不可用，并要求重载。
  const capability = composed.handle.capability()
  assert.equal(capability.status, CAPABILITY_STATUS.unavailable, '撤销后不得凭残留全局回报 available')
  assert.equal(capability.code, CAPABILITY_CODES.capabilityDisabled)
  assert.match(capability.reason, /reload-required/)
  const status = composed.handle.status()
  assert.equal(status.state, 'disposed')
  assert.equal(status.capability.status, CAPABILITY_STATUS.unavailable)
  assert.equal(status.capability.code, CAPABILITY_CODES.capabilityDisabled)
  assert.equal(status.reloadRequired.required, true)
  // 事实仍如实回带（诊断用），只是结论不再被"看起来还在"的全局带着跑。
  assert.equal(status.capability.facts.uploadHook, 'installed')
})

test('保留的桥引用在撤销后拒绝导入（行为撤销，不只是删全局名）', () => {
  const native = createNativeState()
  const scope = createScope()
  const composed = compose(native, scope)
  const retained = composed.bridge
  assert.equal(composed.bridge.disposed, false)
  composed.handle.dispose('retained-reference')
  assert.equal(retained.disposed, true)
  assert.equal(retained.currentSession(), null)
  const result = retained.importFiles({
    sessionId: SESSION_ID,
    files: [{ name: 'x.bin', type: 'application/octet-stream' }]
  })
  assert.equal(result.ok, false)
  assert.equal(result.code, 'capability-disabled')
  assert.equal(native.state.attachmentIds.length, 0)
})

test('构建品牌：CARRIER_BRAND 在两个模块间同源（否则撤销会误判归属）', () => {
  assert.equal(CARRIER_BRAND, SHARED_CARRIER_BRAND)
})

test('client 入口把 teardown 接到 cordis effect：fiber 撤销时撤销自有全局并如实报告 ReloadRequired', async () => {
  const native = createNativeState()
  // client 入口固定装到"页面全局"（Node 里就是 globalThis），因此这里预置 boot 阶段该有的东西：
  // remote 的改写 seat + 本插件品牌的启动前承载。
  const seat = { restore: () => {}, onUnpaired: null, onPaired: null }
  globalThis.__DSH_REMOTE_CHANNEL_BOOT__ = seat
  const carrier = { fetch: () => Promise.resolve({ ok: true, status: 200 }), brand: SHARED_CARRIER_BRAND }
  globalThis[UPLOAD_HOOK_GLOBAL] = carrier

  const effects = []
  const ctx = {
    logger: { info: () => {}, warn: () => {} },
    get: (name) => (name === 'sessions' ? native.sessions : undefined),
    // cordis 的 ctx.effect：立即执行回调并把它的返回值登记为撤销器。
    effect: (callback, label) => {
      effects.push({ label, disposer: callback() })
      return () => {}
    }
  }

  const snapshot = clientEntry.apply(ctx, {})
  // apply 的**返回值**刻意保持 D03 的保守快照（缺页面/宿主上下文时不假装可用，
  // 且它必须与 client-bundle 回归判据里的形状稳定）；真实探测走状态面座位。
  assert.equal(snapshot.status, CAPABILITY_STATUS.unavailable)
  assert.equal(typeof globalThis[ADDON_GLOBAL], 'object')
  assert.equal(
    globalThis[ADDON_GLOBAL].status().capability.status,
    CAPABILITY_STATUS.available,
    '状态面必须给出真实能力判定（seat + 承载 + 宿主服务都在）'
  )
  assert.equal(typeof globalThis[BRIDGE_GLOBAL], 'object')
  assert.equal(typeof globalThis[RECEIVER_GLOBAL], 'object')
  assert.equal(globalThis[ADDON_GLOBAL].status().lifecycle.cordisEffect, true, '状态面必须承认 cordis effect 已注册')
  assert.equal(effects.length, 1, 'apply 必须恰好注册一个生命周期 effect')
  assert.match(effects[0].label, /lifecycle/)

  // 真实禁用路径：cordis 在 fiber 撤销时调用 effect 回调返回的 disposer。
  effects[0].disposer()

  assert.equal(globalThis[ADDON_GLOBAL], undefined)
  assert.equal(globalThis[BRIDGE_GLOBAL], undefined)
  assert.equal(globalThis[RECEIVER_GLOBAL], undefined)
  assert.equal(globalThis[UPLOAD_HOOK_GLOBAL], undefined, '本插件安装的承载随撤销一起撤掉')
  assert.equal(globalThis.__DSH_REMOTE_CHANNEL_BOOT__, seat, '撤销附件不得触碰 remote 的 seat')
  assert.equal(globalThis[STATUS_GLOBAL].addon.state, 'disposed')
  assert.equal(globalThis[STATUS_GLOBAL].addon.reloadRequired.required, true, 'Harness 已捕获承载闭包 ⇒ 必须报告需要重载')
  assert.match(globalThis[STATUS_GLOBAL].addon.reloadRequired.reason, /reload-required/)
  assert.equal(native.state.attachmentIds.length, 0)
  delete globalThis.__DSH_REMOTE_CHANNEL_BOOT__
})
