/**
 * 桥（`src/client/bridge.ts`）的归属确认判据。
 *
 * 被测对象是生产实现，假的是它的外部依赖（cordis 服务、scope、DOM）。这些用例要证明的是
 * **拒绝路径真的会拒绝**：方案 4.4 节要求"若无法建立可靠归属确认，该适配器不得进入正式
 * 支持矩阵"，所以每条不确定情形都必须返回确定失败码，而不是猜一个卡片继续。
 */

import assert from 'node:assert/strict'
import test from 'node:test'
import { createRequire } from 'node:module'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { BRIDGE_VERSION, createAttachmentBridge } from '../../lib/client/bridge.js'

// Node 没有 DataTransfer；适配器会用它写入 files。这里补一个最小实现——假的是浏览器 API，
// 不是被测的生产逻辑（真实浏览器路径由 tests/fixtures/d08-ui-gates.mjs 覆盖）。
if (typeof globalThis.DataTransfer === 'undefined') {
  globalThis.DataTransfer = class DataTransfer {
    #files = []
    items = { add: (file) => this.#files.push(file) }
    get files() {
      return this.#files
    }
  }
}

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const require = createRequire(import.meta.url)

/** 假 DOM：只需要 `querySelectorAll('[data-composer-card]')` 与 input 的三个动作。 */
function fakeDocument({ cards = 1, inputsPerCard = 1, disabled = false } = {}) {
  const events = []
  const inputs = Array.from({ length: inputsPerCard }, () => ({
    disabled,
    files: null,
    dispatchEvent(event) {
      events.push(event.type)
      return true
    }
  }))
  const card = {
    closest: (selector) => (selector === '[data-composer-card]' ? card : null),
    querySelectorAll: (selector) => (selector === 'input[type=file]' ? inputs : [])
  }
  return {
    events,
    inputs,
    document: {
      querySelectorAll: (selector) => (selector === '[data-composer-card]' ? Array.from({ length: cards }, () => card) : [])
    }
  }
}

/** 假会话输入状态：setFiles 后按 preconfigured 增量追加附件 ID。 */
function fakeSessionInput({ phase = 'plain', initial = [], onSet = () => ['att-1'] } = {}) {
  let ids = [...initial]
  const facade = {
    state: { getSnapshot: () => ({ attachmentIds: ids, phase }) },
    // 供假 setFiles 使用：模拟原生链路在 change 后同步写入草稿。
    __apply: (count) => {
      ids = [...ids, ...onSet(count)]
    }
  }
  return facade
}

function harness({
  current = 'session-a',
  requested = undefined,
  scopeResolves = true,
  conversation = 'ok',
  cards = 1,
  inputsPerCard = 1,
  disabled = false,
  phase = 'plain',
  initial = [],
  added = ['att-1']
} = {}) {
  const dom = fakeDocument({ cards, inputsPerCard, disabled })
  const sessionInput = fakeSessionInput({ phase, initial, onSet: () => added })
  const sessions = {
    scope: (id) => (scopeResolves ? { id, get: (name) => (name === 'conversation' && conversation === 'ok' ? conversationFacade : undefined) } : null),
    list: { getSnapshot: () => ({ current }) }
  }
  const conversationFacade = {
    input: { for: () => sessionInput },
    blocks: { storeFor: () => ({ getSnapshot: () => undefined }) }
  }
  // 让 setFiles 驱动假状态：桥不认识 input，只能通过 deps 闭包；这里直接从 DataTransfer 侧
  // 无从得知，所以改为在 dispatchChange 后由 input 自己的 dispatchEvent 触发状态更新。
  for (const input of dom.inputs) {
    const original = input.dispatchEvent.bind(input)
    input.dispatchEvent = (event) => {
      sessionInput.__apply(1)
      return original(event)
    }
  }
  const bridge = createAttachmentBridge({
    getSessions: () => (conversation === 'absent' ? undefined : sessions),
    conversationOf: (scope) => (conversation === 'ok' ? conversationFacade : conversation === 'null-service' ? undefined : { input: {} }),
    document: dom.document
  })
  return { bridge, dom, sessionInput, sessions, request: { sessionId: requested, files: [{ name: 'a.txt' }] } }
}

test('sessions 服务缺失时返回 unsupported（不假装成功）', () => {
  const { bridge, request } = harness({ conversation: 'absent' })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'unsupported')
})

test('没有当前会话（首页）时返回 no-session', () => {
  const { bridge, request } = harness({ current: null, requested: null })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'no-session')
  assert.match(result.detail, /没有可用会话/)
})

test('目标会话不是当前会话时返回 context-changed（切会话迟到请求）', () => {
  const { bridge, request, dom } = harness({ current: 'session-b', requested: 'session-a' })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'context-changed')
  // 拒绝必须发生在触碰 DOM 之前：不得对别的会话的卡片触发任何事件。
  assert.deepEqual(dom.events, [])
})

test('scope 解析不出来时返回 no-session', () => {
  const { bridge, request } = harness({ scopeResolves: false })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'no-session')
})

test('conversation 服务形状不符时返回 unsupported', () => {
  const { bridge, request } = harness({ conversation: 'null-service' })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'unsupported')
})

test('文档内没有 composer 卡片时返回 no-composer-card', () => {
  const { bridge, request } = harness({ cards: 0 })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'no-composer-card')
})

test('文档内有多个 composer 卡片时返回 no-composer-card（归属不确定，不猜）', () => {
  const { bridge, request, dom } = harness({ cards: 2 })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'no-composer-card')
  assert.match(result.detail, /2 个/)
  assert.deepEqual(dom.events, [])
})

test('卡片内 file input 不唯一时返回 no-file-input（不猜哪一个）', () => {
  const { bridge, request } = harness({ inputsPerCard: 2 })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'no-file-input')
})

test('input 禁用时返回 file-input-disabled 且不触发 change', () => {
  const { bridge, request, dom } = harness({ disabled: true })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'file-input-disabled')
  assert.deepEqual(dom.events, [])
})

test('phase 非 plain 时返回 busy-phase（disabled 不足以表达编辑许可）', () => {
  const { bridge, request, dom } = harness({ phase: 'submitting' })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'busy-phase')
  assert.deepEqual(dom.events, [])
})

test('成功路径：change 恰好一次，新增数等于文件数，旧 ID 逐位保留', () => {
  const { bridge, request, dom } = harness({ initial: ['old-1', 'old-2'], added: ['new-1'] })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, true, JSON.stringify(result))
  assert.deepEqual(result.previous, ['old-1', 'old-2'])
  assert.deepEqual(result.added, ['new-1'])
  assert.deepEqual(dom.events, ['change'])
})

test('缺省 sessionId 时使用当前会话', () => {
  const { bridge, request } = harness({ current: 'session-a', requested: undefined })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, true, JSON.stringify(result))
})

test('原生链路未同步新增时返回 input-state-unchanged，且不重放 change', () => {
  const { bridge, request, dom } = harness({ added: [] })
  const result = bridge.importFiles(request)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'input-state-unchanged')
  assert.deepEqual(dom.events, ['change'], '失败后不得再次派发 change')
})

test('桥版本号与常量一致，供调用方核对', () => {
  const { bridge } = harness()
  assert.equal(bridge.version, BRIDGE_VERSION)
  assert.equal(typeof bridge.currentSession(), 'string')
})

test('产物里的桥全局名与源码常量一致', async () => {
  const bundle = await require('node:fs').promises.readFile(join(projectRoot, 'lib/client.js'), 'utf8')
  assert.match(bundle, /__DSH_ATTACHMENTS_BRIDGE__/)
})
