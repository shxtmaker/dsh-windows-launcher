#!/usr/bin/env node
/**
 * D13 真实 Chromium 判据：组合附加插件桥（握手 / 能力状态 / 草稿导入 / 生命周期）。
 *
 * 目标：在**真实夹具页面**（局域网 + 普通 HTTP，非 SecureContext）里，用**已构建的插件**
 * 证明下面这条通路是接上的、而且只有这一条：
 *
 * ```
 * 原生端 ──分块消息──▶ 接收端 ──组装 File──▶ 生产桥 ──▶ 生产 draft-adapter ──▶ 原生草稿
 * ```
 *
 * 判据（全部读真实浏览器里的真实对象与真实网络）：
 *   G01 组合层安装：addon/bridge/receiver 三个全局齐备，接收端宿主接线（来源/会话/能力）已挂上，
 *       健康局域网页面的能力判定为 available，上传路由为 remote-rewrite
 *   G02 握手：本端 hello + capabilities（生产 codec 校验、限额钳到冻结上限、特性不含 screenshot）；
 *       收到对端更低限额后**生效限额真的变小**（越限文件被 limit-file-bytes 拒绝）
 *   G03 端到端：多块（含 256 KiB 满块）→ 组装 File（页面回读字节 + SHA-256 与源一致）→
 *       经生产桥进入原生草稿（用下一次导入的 previous 回读原生 attachmentIds）
 *   G04 三层状态可分辨：transport=buffered / draft=staged / upload=harness-owned，
 *       且状态面**从不**出现 upload ready
 *   G05 部分失败逐文件可辨：同批一个 staged、一个 hash-mismatch，失败项与原因为 fileId 级别
 *   G06 未知 __DSH_FILE_UPLOAD__（boot 前注入）⇒ 能力冲突：我们的承载未安装、未知 hook 原样保留、
 *       整批被拒（拒绝 + cancel 带 capability-conflict）、没有任何导入，且**配对/心跳/设备通道照常**
 *   G07 远程降级（remote 客户端通道层未安装）⇒ 附件能力 unavailable，
 *       承载**拒绝**上传且一个请求都不发（不回退裸 /api；该页面裸 /api 其实是可达的，见下）
 *   G08 会话切换后：指向旧会话的迟到批次被拒（context-changed），新会话的批次照常导入
 *   G09 撤销：自有全局被删除、保留引用被拒、承载被撤、状态面明确报告 ReloadRequired
 *
 * 负向探针见 negativeProbes：版本不符、限额越界、坏哈希、跨会话错投、未知 hook、
 * 降级页面零请求、撤销后仍可传输——每一个都必须**失败**，否则判据是恒真的。
 *
 * 降级页面说明：夹具的降级 profile 把本机 LAN 地址列入 connection.trustedHosts，
 * 因此那个页面上的裸 `/api/...` **是可达的**。正因为它可达，"承载拒绝上传且不发请求"
 * 才是对本插件**主动拒绝回退**的判据，而不是被服务端 401 掩盖的假象。
 *
 * 只用 tests/fixtures 既有夹具（lib.mjs / cdp.mjs / setup.sh 的私有 DSH_HOME）；
 * 不触碰 $HOME/.dsh，不杀用户自己的 Chrome。
 */

import { spawn, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createClient, redact, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'
import { base64Encode, decodeMessage, planChunks } from '../../lib/shared/wire/index.js'
import { ADDON_BUILD } from '../../lib/shared/capabilities.js'
import { DEFAULT_LIMITS } from '../../lib/shared/protocol.js'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const port = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const fixtureProfile = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const headlessProfile = process.env.DSH_ATTACH_HEADLESS_PROFILE ?? 'dsh-attachments-headless'
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

// ---------- 证据收集 ----------

const gates = []
const negativeProbes = []
const observations = {
  sessions: {},
  page: {},
  handshake: {},
  transfer: {},
  partial: {},
  conflict: {},
  degraded: {},
  staleSession: {},
  teardown: {},
  cleanup: {}
}

function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 负向探针：ok=true 表示"反例确实失败了"，即被测判据不是恒真。 */
function negative(id, description, ok, detail = '') {
  negativeProbes.push({ id, description, ok, detail })
  console.log(`  [neg ${ok ? 'OK' : 'BAD'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

function assertOk(label, fed) {
  if (fed?.ok !== true || fed.result?.ok !== true) {
    throw new Error(`${label} 未被接受：${JSON.stringify(fed?.result ?? fed)}`)
  }
}

/** 内容比对判据：长度 + SHA-256 + 逐字节（负向探针会拿改坏的字节再跑一次）。 */
function contentMatches(bytes, source) {
  if (bytes.length !== source.length) return { ok: false, detail: `长度 ${bytes.length} ≠ ${source.length}` }
  const readHash = sha256(bytes)
  if (readHash !== sha256(source)) return { ok: false, detail: `哈希 ${readHash.slice(0, 16)} ≠ ${sha256(source).slice(0, 16)}` }
  for (let index = 0; index < bytes.length; index += 1) {
    if (bytes[index] !== source[index]) return { ok: false, detail: `第 ${index} 字节不同` }
  }
  return { ok: true, detail: `${bytes.length}B sha256=${readHash.slice(0, 16)}…` }
}

// ---------- 素材 ----------

function makeTextBytes(size, marker) {
  const lines = []
  let total = 0
  let index = 0
  while (total < size) {
    const line = `${marker} line ${String(index).padStart(6, '0')} ${'y'.repeat(24)}`
    lines.push(line)
    total += line.length + 1
    index += 1
  }
  let text = lines.join('\n')
  if (text.length < size) text += 'y'.repeat(size - text.length)
  return Buffer.from(text.slice(0, size), 'ascii')
}

const MARKER = 'DSH-D13-BRIDGE-MARKER'
/** 520 KiB：两块满 256 KiB + 尾块，覆盖满块与多块。 */
const MAIN_BYTES = makeTextBytes(520 * 1024, MARKER)
const MAIN_SHA = sha256(MAIN_BYTES)
/** 16 KiB，用于部分失败与撤销后的负向探针。 */
const SMALL_BYTES = makeTextBytes(16 * 1024, `${MARKER}-SMALL`)
const SMALL_SHA = sha256(SMALL_BYTES)
/** 4 KiB，用于"限额被钳低之后越限"的判定。 */
const TINY_BYTES = makeTextBytes(4096, `${MARKER}-TINY`)

// ---------- 页面侧装置 ----------

const INSTALL_HARNESS = `(() => {
  const addon = globalThis.__DSH_ATTACHMENTS_ADDON__
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  const state = { receivers: {}, files: {}, retained: {}, disposed: false }
  globalThis.__D13__ = state
  if (addon === undefined || addon === null || host === undefined || host === null) {
    return JSON.stringify({ ok: false, reason: 'addon-not-installed' })
  }

  const shape = (result) => {
    if (result.ok !== true) {
      return { ok: false, stage: result.stage, code: result.code, detail: result.detail, path: result.path, bufferedBytes: result.bufferedBytes }
    }
    const file = result.file
    return {
      ok: true,
      stage: result.stage,
      type: result.type,
      batchId: result.batchId,
      fileId: result.fileId,
      duplicate: result.duplicate === true,
      importInvoked: result.importInvoked === true,
      bufferedBytes: result.bufferedBytes,
      record: file === null || file === undefined ? null : {
        transport: file.transport,
        draft: file.draft,
        upload: file.upload,
        ended: file.ended,
        receivedBytes: file.receivedBytes,
        attachmentIds: Array.from(file.attachmentIds)
      },
      assembled: result.assembled === null || result.assembled === undefined ? null : {
        fileId: result.assembled.fileId,
        name: result.assembled.name,
        mime: result.assembled.mime,
        size: result.assembled.size,
        sha256: result.assembled.sha256
      },
      import: result.import === null || result.import === undefined ? null : {
        ok: result.import.result.ok === true,
        code: result.import.result.code === undefined ? null : result.import.result.code,
        added: result.import.result.added === undefined ? null : Array.from(result.import.result.added),
        previous: result.import.result.previous === undefined ? null : Array.from(result.import.result.previous),
        status: result.import.message.status,
        ids: Array.from(result.import.message.attachmentIds),
        applied: result.import.applied === true,
        appliedCode: result.import.appliedCode === null ? null : result.import.appliedCode
      }
    }
  }

  const shapeOutgoing = (message) => ({
    v: message.v,
    type: message.type,
    sessionId: message.sessionId === undefined ? null : message.sessionId,
    seq: message.seq === undefined ? null : message.seq,
    offset: message.offset === undefined ? null : message.offset,
    byteLength: message.byteLength === undefined ? null : message.byteLength,
    bufferedBytes: message.bufferedBytes === undefined ? null : message.bufferedBytes,
    inFlight: message.inFlight === undefined ? null : message.inFlight,
    batchId: message.batchId === undefined ? null : message.batchId,
    fileId: message.fileId === undefined ? null : message.fileId,
    status: message.status === undefined ? null : message.status,
    code: message.code === undefined ? null : message.code,
    reason: message.reason === undefined ? null : message.reason,
    stage: message.stage === undefined ? null : message.stage,
    clientBuild: message.clientBuild === undefined ? null : message.clientBuild,
    features: message.features === undefined ? null : Array.from(message.features),
    limits: message.limits === undefined ? null : message.limits,
    attachmentIds: message.attachmentIds === undefined ? null : Array.from(message.attachmentIds)
  })

  state.install = () => {
    const seat = globalThis.__DSH_REMOTE_CHANNEL_BOOT__
    const hook = globalThis.__DSH_FILE_UPLOAD__
    const status = addon.status()
    return JSON.stringify({
      ok: true,
      currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
      isSecureContext: globalThis.isSecureContext === true,
      subtle: typeof globalThis.crypto?.subtle,
      addonType: typeof addon,
      addonVersion: addon.version,
      addonPackage: addon.packageName,
      bridgeType: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
      receiverType: typeof host,
      receiverVersion: host.version,
      wiring: host.wiring,
      remoteSeat: typeof seat === 'object' && seat !== null && typeof seat.restore === 'function',
      remoteSeatType: typeof seat,
      hookType: typeof hook,
      hookFetch: typeof (hook || {}).fetch,
      hookBrand: (hook || {}).brand === undefined ? null : String((hook || {}).brand),
      status: status
    })
  }

  state.make = (name, options) => {
    state.receivers[name] = host.create(options === undefined ? {} : options)
    return true
  }

  /** 保留一个引用：撤销之后仍要能用它发起传输（证明"行为撤销"而不只是删全局名）。 */
  state.retain = (name) => {
    state.retained[name] = state.receivers[name]
    return true
  }

  state.handshake = (name) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    const messages = receiver.beginHandshake()
    return JSON.stringify({
      ok: true,
      count: messages.length,
      messages: messages.map(shapeOutgoing),
      wire: messages.map((message) => JSON.parse(JSON.stringify(message))),
      again: receiver.beginHandshake().length
    })
  }

  state.feed = async (name, text) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    let result
    try {
      result = await receiver.acceptText(text)
    } catch (error) {
      return JSON.stringify({ ok: false, reason: 'accept-threw:' + String(error && error.message ? error.message : error) })
    }
    let outgoing = []
    try {
      outgoing = await receiver.drainOutgoing()
    } catch (error) {
      return JSON.stringify({ ok: false, reason: 'drain-threw:' + String(error && error.message ? error.message : error) })
    }
    if (result.ok === true && result.assembled !== null && result.assembled !== undefined) {
      state.files[name] = result.assembled.file
    }
    return JSON.stringify({
      ok: true,
      result: shape(result),
      outgoing: outgoing.map(shapeOutgoing),
      wire: outgoing.map((message) => JSON.parse(JSON.stringify(message))),
      accounting: receiver.accounting,
      handshake: receiver.handshake,
      capability: receiver.capability
    })
  }

  state.addonStatus = () => JSON.stringify(addon.status())
  /** 所有接收端导入次数之和：撤销后仍可读（装置自己持有引用），用于证明"全局没有新增导入"。 */
  state.sumImports = () => Object.keys(state.receivers).reduce((total, name) => total + state.receivers[name].accounting.importsInvoked, 0)
  state.receiverAccounting = (name) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    return JSON.stringify(receiver.accounting)
  }
  state.receiverHandshake = (name) => JSON.stringify(state.receivers[name].handshake)

  /** 用生产桥探一次草稿：previous 是本次导入前原生的 attachmentIds（探针自己会新增一项）。 */
  state.draftProbe = (label) => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    if (bridge === undefined) return JSON.stringify({ ok: false, reason: 'no-bridge' })
    const bytes = new TextEncoder().encode('d13-probe-' + label + '-' + Date.now())
    const file = new File([bytes], 'd13-probe.txt', { type: 'text/plain' })
    try {
      const result = bridge.importFiles({ files: [file] })
      return JSON.stringify({ ok: true, result })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  /** 撤销后：用保留的桥引用直接导入（必须被拒）。 */
  state.retainedBridgeProbe = (sessionId) => {
    const bridge = state.retainedBridge
    if (bridge === undefined) return JSON.stringify({ ok: false, reason: 'no-retained-bridge' })
    const bytes = new TextEncoder().encode('d13-retained-' + Date.now())
    const file = new File([bytes], 'd13-retained.txt', { type: 'text/plain' })
    try {
      return JSON.stringify({ ok: true, result: bridge.importFiles(sessionId === null ? { files: [file] } : { sessionId, files: [file] }) })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  state.retainBridge = () => {
    state.retainedBridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    return true
  }

  /** 撤销：真实触发点是 cordis fiber 的 dispose，这里直接调用同一个 teardown。 */
  state.dispose = (reason) => {
    addon.dispose(reason)
    state.disposed = true
    return JSON.stringify({
      addonType: typeof globalThis.__DSH_ATTACHMENTS_ADDON__,
      bridgeType: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
      receiverType: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__,
      hookType: typeof globalThis.__DSH_FILE_UPLOAD__,
      diagnostics: (globalThis.__DSH_ATTACHMENTS_STATUS__ || {}).addon ?? null
    })
  }

  /**
   * 承载调用探针：返回结果或确定的能力错误，并登记"是否真的发出了请求"。
   *
   * 请求形状按固定版本的消费者（@deepseek-ai/dsh-client-file-upload 的 runtime.upload）来：
   * 路径 /api/session/uploadFileBinary?sessionId=…&name=…，头部 content-type:
   * application/octet-stream，body 是原始字节。
   * 带 20 秒超时兜底：真上传会写 receipt，服务端耗时不该被误判成"没有发出请求"；
   * 而判据真正关心的是**请求是否发出**（网络事件），响应状态只作为观测记录。
   */
  state.carrierProbe = async () => {
    const hook = globalThis.__DSH_FILE_UPLOAD__
    if (hook === undefined || hook === null || typeof hook.fetch !== 'function') {
      return JSON.stringify({ ok: false, reason: 'no-carrier' })
    }
    const sessionId = globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null
    const query = new URLSearchParams(sessionId === null ? {} : { sessionId })
    query.set('name', 'd13-carrier-probe.bin')
    const bytes = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8])
    try {
      const outcome = await Promise.race([
        hook.fetch('/api/session/uploadFileBinary?' + query.toString(), {
          method: 'POST',
          headers: { 'content-type': 'application/octet-stream' },
          body: new Blob([bytes], { type: 'application/octet-stream' }),
          credentials: 'same-origin'
        }).then(async (response) => ({ ok: true, status: response.status, body: (await response.text()).slice(0, 120) })),
        new Promise((resolve) => setTimeout(() => resolve({ ok: false, timeout: true }), 20000))
      ])
      return JSON.stringify(outcome)
    } catch (error) {
      return JSON.stringify({
        ok: false,
        thrown: true,
        name: String(error && error.name ? error.name : 'Error'),
        code: String((error && error.code) || ''),
        message: String(error && error.message ? error.message : error).slice(0, 200)
      })
    }
  }

  /** 未知 hook 页面：确认它还在、还能用、且没有被贴上我们的品牌。 */
  state.unknownHookCheck = async () => {
    const hook = globalThis.__DSH_FILE_UPLOAD__
    let marker = null
    try {
      marker = hook.marker === undefined ? null : hook.marker()
    } catch (error) {
      marker = 'threw:' + String(error && error.message ? error.message : error)
    }
    return JSON.stringify({
      type: typeof hook,
      marker: marker,
      brand: hook && hook.brand !== undefined ? String(hook.brand) : null,
      isSameObject: hook === globalThis.__D13_UNKNOWN_HOOK__,
      hookFetch: typeof (hook || {}).fetch
    })
  }

  return JSON.stringify({ ok: true, install: JSON.parse(state.install()) })
})()`

// ---------- Node 侧消息构造（每条都先用生产 codec 校验） ----------

let SESSION = ''
let STALE_SESSION = ''

const messageText = (raw) => {
  const decoded = decodeMessage(JSON.stringify(raw))
  if (!decoded.ok) throw new Error(`门禁构造的消息不合法：${decoded.code} ${decoded.detail} @${decoded.path}`)
  return JSON.stringify(decoded.message)
}

/** 故意不合法的报文：不做 codec 预校验，让接收端的 codec 自己拒绝。 */
const rawMessageText = (raw) => JSON.stringify(raw)

const contextText = ({ sessionId = SESSION, targetId = 'target-d13', documentEpoch = 21, composerEpoch = 22, composerScope = 'composer-scope-d13' } = {}) =>
  messageText({ v: 1, type: 'context', sessionId, targetId, documentEpoch, composerEpoch, composerScope })

const batchBeginText = ({ sessionId = SESSION, batchId, fileCount = 1, totalBytes = 0 }) =>
  messageText({
    v: 1,
    type: 'batch-begin',
    sessionId,
    batchId,
    targetId: 'target-d13',
    documentEpoch: 21,
    composerEpoch: 22,
    fileCount,
    totalBytes
  })

const fileBeginText = ({ sessionId = SESSION, batchId, fileId, name, mime = 'application/octet-stream', byteLength, sha }) =>
  messageText({
    v: 1,
    type: 'file-begin',
    sessionId,
    batchId,
    fileId,
    name,
    byteLength,
    mime,
    ...(sha === undefined ? {} : { sha256: sha })
  })

const fileEndText = ({ sessionId = SESSION, batchId, fileId, totalBytes, sha, submittedItems }) =>
  messageText({
    v: 1,
    type: 'file-end',
    sessionId,
    batchId,
    fileId,
    totalBytes,
    sha256: sha,
    ...(submittedItems === undefined ? {} : { submittedItems })
  })

const batchEndText = (batchId, results, status = 'staged', sessionId = SESSION) =>
  messageText({ v: 1, type: 'batch-end', sessionId, batchId, status, results })

const helloText = (clientBuild = 'peer/9.9.9') => messageText({ v: 1, type: 'hello', clientBuild })

const capabilitiesText = (limits) =>
  messageText({
    v: 1,
    type: 'capabilities',
    features: ['chunked-transfer', 'file-end-hash', 'multi-file-batch', 'cancel'],
    limits: { ...DEFAULT_LIMITS, ...limits }
  })

const chunkPlan = (bytes, { sessionId = SESSION, batchId, fileId, chunkBytes }) =>
  planChunks(bytes.length, chunkBytes).map((plan) => ({
    plan,
    text: messageText({
      v: 1,
      type: 'chunk',
      sessionId,
      batchId,
      fileId,
      seq: plan.seq,
      offset: plan.offset,
      byteLength: plan.byteLength,
      dataBase64: base64Encode(bytes.subarray(plan.offset, plan.offset + plan.byteLength))
    })
  }))

// ---------- 页面交互 ----------

/**
 * 页面求值（带超时）。
 *
 * 页面侧探针若因为服务端慢/悬挂而永不 settle，`awaitPromise` 会让整轮判据无声挂住；
 * 这里给每次求值一个上限，把"挂住"变成一条可见的失败判据。
 */
const evaluate = (page, expression, { timeoutMs = 60_000 } = {}) =>
  Promise.race([
    page.evaluate(expression, { awaitPromise: true }),
    new Promise((_resolve, reject) =>
      setTimeout(() => reject(new Error(`页面求值超时（${timeoutMs}ms）：${String(expression).slice(0, 80)}`)), timeoutMs)
    )
  ])

const fed = (page, name, text) => evaluate(page, `globalThis.__D13__.feed(${JSON.stringify(name)}, ${JSON.stringify(text)})`).then(JSON.parse)

const addonStatus = (page) => evaluate(page, 'globalThis.__D13__.addonStatus()').then(JSON.parse)

const draftProbe = async (page, label, expectedCount) => {
  const probe = await evaluate(page, `globalThis.__D13__.draftProbe(${JSON.stringify(label)})`).then(JSON.parse)
  const previous = probe.result?.previous ?? []
  return { probe, previous, matches: probe.ok === true && previous.length === expectedCount }
}

function seedSession(task) {
  return new Promise((resolvePromise) => {
    const child = spawn(dshBin, ['--profile', headlessProfile, task], {
      cwd: repoRoot,
      env: { ...process.env, DSH_HOME: dshHome, DEEPSEEK_API_KEY: process.env.DEEPSEEK_API_KEY ?? 'stub-key' },
      stdio: ['ignore', 'pipe', 'pipe']
    })
    let output = ''
    child.stdout.on('data', (chunk) => { output += chunk })
    child.stderr.on('data', (chunk) => { output += chunk })
    child.on('exit', (code) => resolvePromise({ code, output: output.slice(-300) }))
  })
}

/** typert 网关要求 payload 恰好一个平对象 args；session/list 需要显式 `_request`。 */
async function rpc(client, lanBase, deviceId, method, args) {
  const response = await client.fetch(`${lanBase}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({
      type: 'client-request',
      rpcId: `d13-${method.replace(/\W/g, '-')}-${Date.now()}`,
      method,
      payload: { args: args ?? {} }
    })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

async function poll(read, accept, { timeoutMs, intervalMs = 500 }) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await read()
    if (accept(value)) return { ok: true, value }
    if (Date.now() >= deadline) return { ok: false, value }
    await sleep(intervalMs)
  }
}


/**
 * 安装页面侧装置（带重试）。
 *
 * 会话切换会触发一次页面重载；重载期间 `Runtime.evaluate` 可能落在"新文档还没 boot"的
 * 窗口里，那时三个全局都还不存在。这里重试到装置真的装上为止，而不是把瞬时状态当结论。
 */
async function installHarness(page, { timeoutMs = 60_000 } = {}) {
  const deadline = Date.now() + timeoutMs
  let last = null
  for (;;) {
    try {
      last = JSON.parse(await evaluate(page, INSTALL_HARNESS))
      if (last.ok === true) return last
    } catch (error) {
      last = { ok: false, reason: String(error && error.message ? error.message : error).slice(0, 160) }
    }
    if (Date.now() >= deadline) return last ?? { ok: false, reason: 'timeout' }
    await sleep(1000)
  }
}

/** 把一个页面开到"插件就绪且当前会话符合预期"的状态。 */
async function openAppPage({ chrome, lanBase, originBase, deviceId, sessionId, extraHeadScript, device }) {
  const page = await openPage(chrome.debugPort)
  if (extraHeadScript !== undefined) {
    await page.send('Page.addScriptToEvaluateOnNewDocument', { source: extraHeadScript })
  }
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
  })
  const pageErrors = []
  page.onEvent((message) => {
    if (message.method === 'Runtime.exceptionThrown') {
      pageErrors.push(String(message.params.exceptionDetails?.exception?.description ?? '').slice(0, 200))
    }
  })
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  const query = device === undefined ? '' : `?device=${encodeURIComponent(device)}`
  await page.navigate(`${originBase ?? lanBase}/pair-app${query}`)
  const ready = await poll(
    async () =>
      JSON.parse(
        await evaluate(
          page,
          `JSON.stringify({
            addon: typeof globalThis.__DSH_ATTACHMENTS_ADDON__,
            bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
            receiver: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__,
            currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null
          })`
        ).catch(() => '{"addon":"undefined"}')
      ),
    (value) => value.addon === 'object' && value.receiver === 'object' && value.currentSession === sessionId,
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  return { page, ready, pageErrors }
}

// =====================================================================
// 主流程
// =====================================================================

let service = null
let degradedService = null
let chrome = null
const openPages = []
let fatal = null
const serviceLog = join(evidenceDir, 'service-d13-bridge.log')
const degradedLog = join(evidenceDir, 'service-d13-degraded.log')

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })

try {
  console.log('D13 组合附加插件桥判据（真实 Chromium + 真实夹具）\n')
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  const lanBase = `http://${lanAddress}:${port}`
  const degradedInfo = existsSync(join(fixtureRoot, 'degraded-profile.json'))
    ? JSON.parse(await readFile(join(fixtureRoot, 'degraded-profile.json'), 'utf8'))
    : { profile: 'dsh-attachments-degraded', port: 3098 }

  console.log(`阶段 0：造两个真实会话并启动夹具服务（${lanBase}）\n`)
  const seedA = await seedSession('d13 bridge gate session A')
  const seedB = await seedSession('d13 bridge gate session B')
  observations.sessions.seeds = { a: seedA.code, b: seedB.code }

  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))
  await waitForReady(service.child, serviceLog, 120_000)

  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair') ?? ''
  const list = await rpc(client, lanBase, deviceId, 'session/list', { _request: {} })
  const items = list.body?.result?.value?.items ?? []
  const ids = [...new Set(items.map((item) => item.sessionId).filter((value) => typeof value === 'string'))]
  if (ids.length < 2) throw new Error(`共享 DSH_HOME 里只有 ${ids.length} 个会话（需要两个：当前会话与切换后的会话）`)
  SESSION = ids[0]
  STALE_SESSION = ids[1]
  observations.sessions.current = SESSION
  observations.sessions.other = STALE_SESSION
  observations.sessions.count = ids.length
  console.log(`  会话 A=${SESSION}\n  会话 B=${STALE_SESSION}\n`)

  chrome = await launchChrome({})
  const app = await openAppPage({ chrome, lanBase, deviceId, sessionId: SESSION, device: deviceId })
  openPages.push(app.page)
  const page = app.page
  const installed = await installHarness(page)
  if (installed.ok !== true) throw new Error(`页面侧装置未装上：${installed.reason}`)
  observations.page.ready = app.ready.ok
  observations.page.install = installed.install

  // ================= G01：组合层安装 =================
  const install = installed.install
  const wiring = install.wiring ?? {}
  observations.page.wiring = wiring
  gate(
    'G01-composed-install',
    '组合层把 addon/bridge/receiver 三个全局装进真实页面，接收端宿主接线（来源/当前会话/能力）齐备，健康局域网页面判定为 available 且上传路由为 remote-rewrite',
    app.ready.ok === true &&
      install.addonType === 'object' &&
      install.addonVersion === 1 &&
      install.bridgeType === 'object' &&
      install.receiverType === 'object' &&
      install.receiverVersion === 1 &&
      wiring.hasOrigin === true &&
      wiring.hasCurrentSession === true &&
      wiring.hasCapability === true &&
      wiring.clientBuild === ADDON_BUILD &&
      install.remoteSeat === true &&
      install.hookType === 'object' &&
      install.hookFetch === 'function' &&
      install.status.capability.status === 'available' &&
      install.status.capability.code === null &&
      install.status.upload.route === 'remote-rewrite' &&
      install.status.upload.ownership === 'ours' &&
      install.status.origin.originClass === 'lan' &&
      install.status.origin.isSecureContext === false &&
      install.status.origin.hashBackend === 'pure-js',
    `ready=${app.ready.ok} addon=${install.addonType}/v${install.addonVersion} bridge=${install.bridgeType} receiver=${install.receiverType}/v${install.receiverVersion} wiring=${JSON.stringify(wiring)} seat=${install.remoteSeat} hook=${install.hookType}.fetch=${install.hookFetch} cap=${install.status.capability.status}/${install.status.capability.code} route=${install.status.upload.route} origin=${install.status.origin.originClass}/secure=${install.status.origin.isSecureContext}/hash=${install.status.origin.hashBackend}`
  )

  await evaluate(page, 'globalThis.__D13__.make("main")')

  // ================= G02：握手 =================
  console.log('\n阶段 1：握手（hello / capabilities / context）\n')
  const handshake = JSON.parse(await evaluate(page, 'globalThis.__D13__.handshake("main")'))
  const hello = handshake.messages[0]
  const capabilities = handshake.messages[1]
  const helloLegal = hello === undefined ? { ok: false } : decodeMessage(JSON.stringify(handshake.wire[0]))
  const capsLegal = capabilities === undefined ? { ok: false } : decodeMessage(JSON.stringify(handshake.wire[1]))
  observations.handshake.advertised = { hello, capabilities, again: handshake.again }
  gate(
    'G02-handshake-advertisement',
    '接收端发出 hello + capabilities（顺序固定、幂等）：构建标识与共享常量一致，限额已钳到冻结上限，特性不含 screenshot，且两条都能被生产 codec 解码',
    handshake.ok === true &&
      handshake.count === 2 &&
      handshake.again === 0 &&
      hello?.type === 'hello' &&
      hello.clientBuild === ADDON_BUILD &&
      capsLegal.ok === true &&
      helloLegal.ok === true &&
      capabilities?.type === 'capabilities' &&
      Array.isArray(capabilities.features) &&
      !capabilities.features.includes('screenshot') &&
      capabilities.features.includes('chunked-transfer') &&
      capabilities.limits.maxFileBytes === DEFAULT_LIMITS.maxFileBytes &&
      capabilities.limits.maxBatchBytes === DEFAULT_LIMITS.maxBatchBytes,
    `hello=${hello?.clientBuild} 条数=${handshake.count} 幂等重发=${handshake.again} features=${JSON.stringify(capabilities?.features)} limits.maxFileBytes=${capabilities?.limits?.maxFileBytes}`
  )

  // 对端声明更低的限额：生效限额必须真的变小，且越限文件被拒。
  const clampBatch = 'batch-clamp'
  assertOk('context(clamp)', await fed(page, 'main', contextText()))
  assertOk('hello(clamp)', await fed(page, 'main', helloText('peer/1.0.0')))
  assertOk('capabilities(clamp)', await fed(page, 'main', capabilitiesText({ maxFileBytes: 2048 })))
  const clampState = await evaluate(page, 'globalThis.__D13__.receiverHandshake("main")').then(JSON.parse)
  const clampBegin = await fed(page, 'main', batchBeginText({ batchId: clampBatch, totalBytes: TINY_BYTES.length }))
  const clampedFile = await fed(
    page,
    'main',
    fileBeginText({ batchId: clampBatch, fileId: 'file-clamped', name: 'clamped.bin', byteLength: TINY_BYTES.length })
  )
  await fed(page, 'main', messageText({ v: 1, type: 'cancel', sessionId: SESSION, batchId: clampBatch, reason: 'cancelled', stage: 'protocol-transfer' }))

  // 高于冻结上限的声明：生产 codec 必须直接拒绝（同一路径上 2048 那份是合法的，见上）。
  const overFrozen = await fed(
    page,
    'main',
    rawMessageText({
      v: 1,
      type: 'capabilities',
      features: ['chunked-transfer'],
      limits: { ...DEFAULT_LIMITS, maxFileBytes: DEFAULT_LIMITS.maxFileBytes + 1 }
    })
  )

  // 对照：恢复冻结限额后，**同一个** 4 KiB 文件必须被接受——证明上一条拒绝确实来自被钳低的限额。
  assertOk('capabilities(restore)', await fed(page, 'main', capabilitiesText({})))
  assertOk('batch-begin(restore)', await fed(page, 'main', batchBeginText({ batchId: 'batch-restore', totalBytes: TINY_BYTES.length })))
  const restoredFile = await fed(
    page,
    'main',
    fileBeginText({ batchId: 'batch-restore', fileId: 'file-restored', name: 'restored.bin', byteLength: TINY_BYTES.length })
  )
  await fed(page, 'main', messageText({ v: 1, type: 'cancel', sessionId: SESSION, batchId: 'batch-restore', reason: 'cancelled', stage: 'protocol-transfer' }))

  observations.handshake.clamp = {
    declared: clampState.declaredLimits,
    effective: clampState.effectiveLimits,
    policy: clampState.policyLimits,
    clamps: clampState.clamps,
    begin: clampBegin.result,
    file: clampedFile.result,
    overFrozen: overFrozen.result,
    restored: restoredFile.result
  }
  gate(
    'G02b-limits-clamped-and-enforced',
    '对端声明 2048 字节的单文件限额后：生效限额真的变成 2048（逐项 declared/effective 落账），4 KiB 文件被 limit-file-bytes 拒绝；恢复冻结限额后同一个文件又被接受（证明拒绝确实来自钳制）；高于冻结上限的声明被 codec 直接拒绝',
    clampState.declaredLimits?.maxFileBytes === 2048 &&
      clampState.effectiveLimits?.maxFileBytes === 2048 &&
      clampState.policyLimits?.maxFileBytes === DEFAULT_LIMITS.maxFileBytes &&
      clampState.clamps?.some((item) => item.key === 'maxFileBytes' && item.declared === 2048 && item.effective === 2048) === true &&
      clampBegin.result?.ok === true &&
      clampedFile.result?.ok === false &&
      clampedFile.result.code === 'limit-file-bytes' &&
      restoredFile.result?.ok === true &&
      overFrozen.result?.ok === false &&
      overFrozen.result.code === 'integer-out-of-range',
    `declared=${clampState.declaredLimits?.maxFileBytes} effective=${clampState.effectiveLimits?.maxFileBytes} policy=${clampState.policyLimits?.maxFileBytes} 越限文件=${clampedFile.result?.code ?? '(竟然通过)'} 恢复后同一文件=${restoredFile.result?.ok} 超冻结声明=${overFrozen.result?.code ?? '(竟然通过)'}`
  )
  negative(
    'N1-limit-clamp-enforced',
    '反例：把生效限额钳到 2048 之后 4 KiB 文件必须被拒，恢复冻结限额后同一个文件必须被接受（两次结果不同 ⇒ 判定不是恒真）',
    clampedFile.result?.ok === false &&
      clampedFile.result.code === 'limit-file-bytes' &&
      restoredFile.result?.ok === true,
    `钳制时=${clampedFile.result?.code ?? '(竟然通过)'} 恢复后=${restoredFile.result?.ok === true ? 'accepted' : restoredFile.result?.code}`
  )

  // ================= G03 / G04：端到端与三层状态 =================
  console.log('\n阶段 2：分块传输 → 生产草稿路径 → 原生 attachmentIds\n')
  let expectedNative = 0

  const mainBatch = 'batch-main'
  const mainFile = 'file-main'
  const mainName = 'd13-bridge-main.bin'
  assertOk('context(main)', await fed(page, 'main', contextText()))
  assertOk('batch-begin(main)', await fed(page, 'main', batchBeginText({ batchId: mainBatch, totalBytes: MAIN_BYTES.length })))
  assertOk(
    'file-begin(main)',
    await fed(page, 'main', fileBeginText({ batchId: mainBatch, fileId: mainFile, name: mainName, byteLength: MAIN_BYTES.length, sha: MAIN_SHA }))
  )
  const mainChunks = chunkPlan(MAIN_BYTES, { batchId: mainBatch, fileId: mainFile, chunkBytes: 256 * 1024 })
  let lastAckBuffered = 0
  for (const entry of mainChunks) {
    const chunkFed = await fed(page, 'main', entry.text)
    assertOk(`chunk seq=${entry.plan.seq}`, chunkFed)
    if (chunkFed.outgoing.length !== 1) throw new Error(`每接受一块应恰好一条 ack，实际 ${chunkFed.outgoing.length}`)
    const ack = chunkFed.outgoing[0]
    if (decodeMessage(JSON.stringify(chunkFed.wire[0])).ok !== true) throw new Error('ack 不是线协议合法报文')
    lastAckBuffered = ack.bufferedBytes
  }
  const mainEnd = await fed(page, 'main', fileEndText({ batchId: mainBatch, fileId: mainFile, totalBytes: MAIN_BYTES.length, sha: MAIN_SHA }))
  assertOk('file-end(main)', mainEnd)
  if (mainEnd.result.import?.ok === true) expectedNative += mainEnd.result.import.added.length

  const mainRead = JSON.parse(
    await evaluate(
      page,
      `(async () => {
        const file = globalThis.__D13__.files.main
        if (file === undefined) return JSON.stringify({ ok: false, reason: 'no-file' })
        const bytes = new Uint8Array(await file.arrayBuffer())
        let binary = ''
        const step = 0x8000
        for (let index = 0; index < bytes.length; index += step) {
          binary += String.fromCharCode.apply(null, bytes.subarray(index, index + step))
        }
        return JSON.stringify({ ok: true, name: file.name, type: file.type, size: file.size, isFile: file instanceof File, base64: btoa(binary) })
      })()`
    )
  )
  const readBytes = mainRead.ok === true ? Buffer.from(mainRead.base64, 'base64') : Buffer.alloc(0)
  const content = mainRead.ok === true ? contentMatches(readBytes, MAIN_BYTES) : { ok: false, detail: mainRead.reason }
  const statusAfterMain = await addonStatus(page)
  observations.transfer = {
    sourceBytes: MAIN_BYTES.length,
    sourceSha256: MAIN_SHA,
    chunks: mainChunks.map((entry) => entry.plan.byteLength),
    lastAckBuffered,
    end: mainEnd.result,
    readBackBytes: readBytes.length,
    readBackSha256: mainRead.ok === true ? sha256(readBytes) : null,
    content,
    fileFacts: { name: mainRead.name, type: mainRead.type, size: mainRead.size, isFile: mainRead.isFile },
    status: {
      staged: statusAfterMain.draft.staged,
      files: statusAfterMain.files,
      transport: statusAfterMain.transport,
      uploadOwnership: statusAfterMain.uploadOwnership
    }
  }
  gate(
    'G03-transfer-to-native-draft',
    '多块（含 256 KiB 满块）经组合层组装成真实 File：页面回读字节与源逐字节相同，且该 File 经生产桥进入原生草稿（状态面报告的新增 ID 与下一批导入读到的原生 attachmentIds 一致）',
    mainChunks.length >= 3 &&
      mainEnd.result.importInvoked === true &&
      mainEnd.result.import?.ok === true &&
      content.ok === true &&
      mainRead.isFile === true &&
      mainRead.size === MAIN_BYTES.length &&
      mainEnd.result.assembled?.sha256 === MAIN_SHA &&
      statusAfterMain.draft.staged.includes(mainFile) &&
      lastAckBuffered === MAIN_BYTES.length,
    `块=${mainChunks.length} 尺寸=${mainChunks.map((entry) => entry.plan.byteLength).join(',')} 回读=${readBytes.length}B ${content.detail} added=${JSON.stringify(mainEnd.result.import?.added)} staged=${JSON.stringify(statusAfterMain.draft.staged)}`
  )
  negative(
    'N2-content-compare-falsifiable',
    '反例：把源字节改一位后，内容比对判据必须为 false（证明 G03 的逐字节比对不是恒真）',
    readBytes.length > 0 && contentMatches(readBytes, (() => { const copy = Buffer.from(MAIN_BYTES); copy[Math.floor(copy.length / 2)] ^= 0xff; return copy })()).ok === false,
    `改一位 → ${contentMatches(readBytes, (() => { const copy = Buffer.from(MAIN_BYTES); copy[Math.floor(copy.length / 2)] ^= 0xff; return copy })()).detail}`
  )

  gate(
    'G04-three-states-separable',
    '三层状态互不合并：transport=buffered、draft=staged、upload=harness-owned；状态面里没有任何 "ready" 上传取值（上传归 Harness 所有）',
    mainEnd.result.record?.transport === 'buffered' &&
      mainEnd.result.record?.draft === 'staged' &&
      mainEnd.result.record?.upload === 'harness-owned' &&
      statusAfterMain.uploadOwnership === 'harness-owned' &&
      statusAfterMain.files.some((file) => file.fileId === mainFile && file.status === 'staged' && file.upload === 'harness-owned') === true &&
      !JSON.stringify(statusAfterMain).includes('"upload":"ready"') &&
      !JSON.stringify(statusAfterMain).includes('uploaded'),
    `三态=${mainEnd.result.record?.transport}/${mainEnd.result.record?.draft}/${mainEnd.result.record?.upload} uploadOwnership=${statusAfterMain.uploadOwnership} files=${JSON.stringify(statusAfterMain.files.map((file) => [file.fileId, file.status, file.transport, file.draft, file.upload]))}`
  )
  negative(
    'N3-no-upload-ready-claim',
    '反例：状态面与线协议结果里都不得出现 upload ready/uploaded（一旦有人宣称上传就绪，这条会失败）',
    !JSON.stringify(statusAfterMain).includes('"upload":"ready"') &&
      !JSON.stringify(statusAfterMain).includes('uploaded') &&
      mainEnd.result.import?.status === 'staged',
    `import.status=${mainEnd.result.import?.status} 状态面含 ready=${JSON.stringify(statusAfterMain).includes('"upload":"ready"')}`
  )

  // 关批 + 用"下一次导入的 previous"回读原生 attachmentIds（不是 DOM 芯片）。
  assertOk(
    'batch-end(main)',
    await fed(page, 'main', batchEndText(mainBatch, [{ fileId: mainFile, status: 'staged', attachmentIds: mainEnd.result.import?.added ?? [] }]))
  )
  const mainDraftProbe = await draftProbe(page, 'after-main', expectedNative)
  const mainAdded = mainEnd.result.import?.added ?? []
  observations.transfer.nativeProbe = { expected: expectedNative, previous: mainDraftProbe.previous, added: mainAdded }
  gate(
    'G03b-native-attachment-ids',
    '原生草稿真的多了这一项：接收端报告的新增 ID 出现在下一次导入读到的原生 attachmentIds 里（读原生状态，不是 DOM 芯片）',
    mainAdded.length === 1 &&
      mainDraftProbe.matches === true &&
      mainDraftProbe.previous.includes(mainAdded[0]),
    `added=${JSON.stringify(mainAdded)} previous=${JSON.stringify(mainDraftProbe.previous)} 期望条数=${expectedNative}`
  )
  expectedNative += (mainDraftProbe.probe.result?.added ?? []).length

  // ================= G05：部分失败逐文件可辨 =================
  console.log('\n阶段 3：部分失败逐文件可辨\n')
  const partialBatch = 'batch-partial'
  assertOk('batch-begin(partial)', await fed(page, 'main', batchBeginText({ batchId: partialBatch, fileCount: 2, totalBytes: SMALL_BYTES.length * 2 })))
  const goodFile = 'file-partial-good'
  const badFile = 'file-partial-bad'
  assertOk(
    'file-begin(good)',
    await fed(page, 'main', fileBeginText({ batchId: partialBatch, fileId: goodFile, name: 'partial-good.bin', byteLength: SMALL_BYTES.length, sha: SMALL_SHA }))
  )
  for (const entry of chunkPlan(SMALL_BYTES, { batchId: partialBatch, fileId: goodFile, chunkBytes: 4096 })) {
    assertOk('good chunk', await fed(page, 'main', entry.text))
  }
  const goodEnd = await fed(page, 'main', fileEndText({ batchId: partialBatch, fileId: goodFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  assertOk('file-end(good)', goodEnd)
  assertOk(
    'file-begin(bad)',
    await fed(page, 'main', fileBeginText({ batchId: partialBatch, fileId: badFile, name: 'partial-bad.bin', byteLength: SMALL_BYTES.length }))
  )
  for (const entry of chunkPlan(SMALL_BYTES, { batchId: partialBatch, fileId: badFile, chunkBytes: 4096 })) {
    assertOk('bad chunk', await fed(page, 'main', entry.text))
  }
  const badEnd = await fed(page, 'main', fileEndText({ batchId: partialBatch, fileId: badFile, totalBytes: SMALL_BYTES.length, sha: 'f'.repeat(64) }))
  const statusAfterPartial = await addonStatus(page)
  observations.partial = {
    goodEnd: goodEnd.result,
    badEnd: badEnd.result,
    staged: statusAfterPartial.draft.staged,
    failed: statusAfterPartial.draft.failed,
    partialFailures: statusAfterPartial.partialFailures,
    nativeProbeExpected: expectedNative
  }
  gate(
    'G05-partial-failure-per-file',
    '同一批次内部分失败按 fileId 逐条可辨：成功项留在 staged（草稿保住了），失败项带确定的结果码（hash-mismatch），且失败没有产生任何原生附件',
    goodEnd.result.import?.ok === true &&
      badEnd.result.ok === false &&
      badEnd.result.code === 'hash-mismatch' &&
      statusAfterPartial.draft.staged.includes(goodFile) &&
      statusAfterPartial.draft.failed.includes(badFile) &&
      statusAfterPartial.partialFailures.some((item) => item.fileId === badFile && item.code === 'hash-mismatch') === true &&
      statusAfterPartial.files.some((file) => file.fileId === badFile && file.status === 'failed' && file.code === 'hash-mismatch') === true,
    `good=${goodEnd.result.import?.added?.length ?? 0} 项 bad=${badEnd.result.code} staged=${JSON.stringify(statusAfterPartial.draft.staged)} failed=${JSON.stringify(statusAfterPartial.draft.failed)} partialFailures=${JSON.stringify(statusAfterPartial.partialFailures)}`
  )
  if (goodEnd.result.import?.ok === true) expectedNative += goodEnd.result.import.added.length
  // 生产语义（C# AttachmentTransferCoordinator.CancelBatchAfterFailure 的原话）：
  // "本地失败无法在线上单独中止一个文件：发 cancel 结束整批，但已 staged 的逐文件结果全部保留"。
  const partialCancel = await fed(
    page,
    'main',
    messageText({ v: 1, type: 'cancel', sessionId: SESSION, batchId: partialBatch, reason: 'hash-mismatch', stage: 'protocol-transfer' })
  )
  const statusAfterCancel = await addonStatus(page)
  const partialDraftProbe = await draftProbe(page, 'after-partial', expectedNative)
  observations.partial.cancel = partialCancel.result
  observations.partial.stagedAfterCancel = statusAfterCancel.draft.staged
  gate(
    'G05b-partial-native-count',
    '部分失败后按生产语义用 cancel 结束整批：已 staged 的成功项保留在状态面与原生命中，失败项绝不在原生状态里出现新增 ID',
    partialCancel.result?.ok === true &&
      statusAfterCancel.draft.staged.includes(goodFile) &&
      statusAfterCancel.draft.failed.includes(badFile) &&
      partialDraftProbe.matches === true,
    `cancel=${partialCancel.result?.ok} staged=${JSON.stringify(statusAfterCancel.draft.staged)} failed=${JSON.stringify(statusAfterCancel.draft.failed)} previous=${JSON.stringify(partialDraftProbe.previous)} 期望条数=${expectedNative}`
  )
  expectedNative += (partialDraftProbe.probe.result?.added ?? []).length

  // 版本不符（负向 + 正向对照）
  const versionMismatch = await fed(page, 'main', rawMessageText({ v: 2, type: 'hello', clientBuild: 'peer/1.0.0' }))
  negative(
    'N4-version-mismatch-rejected',
    '反例：协议版本不是 1 的报文被确定拒绝（version-mismatch），而同形状的 v1 hello 被接受',
    versionMismatch.result?.ok === false &&
      versionMismatch.result.code === 'version-mismatch',
    `v=2 → ${versionMismatch.result?.code ?? '(竟然通过)'}；v=1 见 G02`
  )

  // ================= G06：未知 hook ⇒ 能力冲突 =================
  console.log('\n阶段 4：未知 __DSH_FILE_UPLOAD__（boot 前注入）⇒ 能力冲突\n')
  const conflictApp = await openAppPage({
    chrome,
    lanBase,
    deviceId,
    sessionId: SESSION,
    device: deviceId,
    extraHeadScript: `globalThis.__DSH_FILE_UPLOAD__ = { fetch: function () { return Promise.reject(new Error('unknown-hook-carrier')) }, marker: function () { return 'unknown-hook-alive' } }; globalThis.__D13_UNKNOWN_HOOK__ = globalThis.__DSH_FILE_UPLOAD__;`
  })
  openPages.push(conflictApp.page)
  const conflictPage = conflictApp.page
  const conflictInstall = await installHarness(conflictPage)
  if (conflictInstall.ok !== true) throw new Error(`冲突页面侧装置未装上：${conflictInstall.reason}`)
  const conflictHook = JSON.parse(await evaluate(conflictPage, 'globalThis.__D13__.unknownHookCheck()'))
  await evaluate(conflictPage, 'globalThis.__D13__.make("conflict")')
  // 能力冲突时 context 也会被拒（没有有效会话身份 ⇒ 不接收批次）；这正是判据的一部分。
  const conflictContext = await fed(conflictPage, 'conflict', contextText())
  const conflictBegin = await fed(conflictPage, 'conflict', batchBeginText({ batchId: 'batch-conflict', totalBytes: SMALL_BYTES.length }))
  const conflictCancel = conflictBegin.outgoing.find((message) => message.type === 'cancel')
  const conflictStatus = await addonStatus(conflictPage)
  const conflictProbe = await draftProbe(conflictPage, 'conflict', 0)
  // 配对/心跳/设备通道必须照常：用同一个 deviceId 走心跳与 /remote 读会话列表。
  const heartbeat = await client.fetch(`${lanBase}/api/pair/heartbeat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: '{}'
  })
  const conflictRpc = await rpc(client, lanBase, deviceId, 'session/list', { _request: {} })
  observations.conflict = {
    install: { hookType: conflictInstall.install.hookType, hookBrand: conflictInstall.install.hookBrand },
    hook: conflictHook,
    context: conflictContext.result,
    begin: conflictBegin.result,
    cancel: conflictCancel ?? null,
    status: {
      capability: conflictStatus.capability,
      upload: conflictStatus.upload,
      importsInvoked: conflictStatus.transport.importsInvoked
    },
    draftProbe: conflictProbe.probe,
    pairing: { heartbeatStatus: heartbeat.status, sessionListStatus: conflictRpc.status, sessionCount: (conflictRpc.body?.result?.value?.items ?? []).length }
  }
  gate(
    'G06-unknown-hook-capability-conflict',
    'boot 前注入的未知 __DSH_FILE_UPLOAD__ 让能力确定为 conflict：我们的承载没有被安装（未知 hook 原样保留、可调用、未被贴品牌），整批被拒且回一条 capability-conflict 的 cancel，没有任何导入',
    conflictInstall.install.hookType === 'object' &&
      conflictInstall.install.hookBrand === null &&
      conflictHook.isSameObject === true &&
      conflictHook.marker === 'unknown-hook-alive' &&
      conflictHook.hookFetch === 'function' &&
      conflictStatus.capability.status === 'unavailable' &&
      conflictStatus.capability.code === 'capability-conflict' &&
      conflictStatus.upload.hook === 'conflict' &&
      conflictStatus.upload.ownership === 'unknown' &&
      conflictBegin.result?.ok === false &&
      /capability-conflict/.test(conflictBegin.result?.detail ?? '') &&
      conflictCancel?.reason === 'capability-conflict' &&
      conflictCancel?.stage === 'protocol-transfer' &&
      conflictStatus.transport.importsInvoked === 0 &&
      conflictProbe.probe.result?.ok === false &&
      conflictProbe.probe.result?.code === 'capability-conflict',
    `hook=${conflictHook.type} 保留原对象=${conflictHook.isSameObject} marker=${conflictHook.marker} brand=${conflictInstall.install.hookBrand} cap=${conflictStatus.capability.status}/${conflictStatus.capability.code} 整批=${conflictBegin.result?.code} cancel.reason=${conflictCancel?.reason} 导入次数=${conflictStatus.transport.importsInvoked} 桥探针=${conflictProbe.probe.result?.code ?? conflictProbe.probe.reason}`
  )
  gate(
    'G06b-pairing-untouched',
    '能力冲突的同时：配对/心跳/设备通道完全不受影响（心跳 200、/remote 读会话列表成功、设备数不变）',
    heartbeat.status === 200 &&
      conflictRpc.status === 200 &&
      (conflictRpc.body?.result?.value?.items ?? []).length === ids.length,
    `heartbeat=${heartbeat.status} session/list=${conflictRpc.status} 会话数=${(conflictRpc.body?.result?.value?.items ?? []).length}（期望 ${ids.length}）`
  )
  negative(
    'N5-unknown-hook-not-overwritten',
    '反例：未知 hook 必须既不被覆盖也不被删除（还是同一个对象、marker 仍可调用、没有我们的品牌）',
    conflictHook.isSameObject === true && conflictHook.marker === 'unknown-hook-alive' && conflictHook.brand === null,
    `same=${conflictHook.isSameObject} marker=${conflictHook.marker} brand=${conflictHook.brand}`
  )
  await conflictPage.close()
  openPages.splice(openPages.indexOf(conflictPage), 1)

  // ================= G07：会话切换 ⇒ 陈旧批次被拒 =================
  console.log('\n阶段 5：会话切换与陈旧批次\n')
  // 切换会话：本夹具没有会话 URL 路由，只能预置 localStorage 再重新进入应用页。
  // 两个要点：
  //   1) `addScriptToEvaluateOnNewDocument` 的脚本在**每次**新文档时执行，后注册的后执行
  //      ——因此再注册一条写 B 的脚本，新文档里当前会话才是 B；
  //   2) 应用落地后会把 URL 收敛成 `/`（`/` 不带模块图），所以这里必须**重新导航到
  //      `/pair-app?device=…`**，而不是 `location.reload()`（后者会停在 `/`，插件根本不会加载）。
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(STALE_SESSION)}}))}catch(e){}`
  })
  await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId)}`)
  await sleep(1500)
  const switched = await poll(
    async () =>
      JSON.parse(
        await evaluate(
          page,
          `JSON.stringify({
            addon: typeof globalThis.__DSH_ATTACHMENTS_ADDON__,
            currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null
          })`
        ).catch(() => '{"addon":"undefined"}')
      ),
    (value) => value.addon === 'object' && value.currentSession === STALE_SESSION,
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  const switchedInstall = await installHarness(page)
  if (switchedInstall.ok !== true) throw new Error(`切换会话后页面侧装置未装上：${switchedInstall.reason}`)
  if (switchedInstall.install.currentSession !== STALE_SESSION) {
    throw new Error(`切换会话后当前会话仍是 ${switchedInstall.install.currentSession}，期望 ${STALE_SESSION}`)
  }
  await evaluate(page, 'globalThis.__D13__.make("switched")')
  assertOk('context(switched)', await fed(page, 'switched', contextText({ sessionId: STALE_SESSION })))
  // 陈旧批次：身份已绑到 B，却拿 A 的 sessionId 来开批。
  const staleBegin = await fed(page, 'switched', batchBeginText({ sessionId: SESSION, batchId: 'batch-stale', totalBytes: TINY_BYTES.length }))
  const staleFile = await fed(
    page,
    'switched',
    fileBeginText({ sessionId: SESSION, batchId: 'batch-stale', fileId: 'file-stale', name: 'stale.bin', byteLength: TINY_BYTES.length })
  )
  const staleChunk = await fed(page, 'switched', chunkPlan(TINY_BYTES.subarray(0, 1024), { sessionId: SESSION, batchId: 'batch-stale', fileId: 'file-stale', chunkBytes: 1024 })[0].text)
  const staleStatus = await addonStatus(page)
  // 切到 B 之后，原生草稿是 **B 的**草稿（会话 A 的计数不再适用）：它必须还是 0 项。
  const staleProbe = await draftProbe(page, 'after-stale', 0)
  observations.staleSession = {
    current: STALE_SESSION,
    stale: SESSION,
    begin: staleBegin.result,
    file: staleFile.result,
    chunk: staleChunk.result,
    importsInvoked: staleStatus.transport.importsInvoked,
    nativeProbe: { expected: 0, previous: staleProbe.previous, matches: staleProbe.matches }
  }
  gate(
    'G07-stale-session-refused',
    '切到会话 B 之后，指向会话 A 的迟到批次在进入缓冲前就被拒（context-changed）：batch-begin/file-begin/chunk 全拒，导入次数为 0，原生草稿没有多出任何附件',
    switched.ok === true &&
      staleBegin.result?.ok === false &&
      staleBegin.result.code === 'context-changed' &&
      staleFile.result?.ok === false &&
      staleFile.result.code === 'context-changed' &&
      staleChunk.result?.ok === false &&
      staleChunk.result.code === 'context-changed' &&
      staleStatus.transport.importsInvoked === 0 &&
      staleProbe.matches === true,
    `当前=${STALE_SESSION} 陈旧=${SESSION} begin=${staleBegin.result?.code} file=${staleFile.result?.code} chunk=${staleChunk.result?.code} importsInvoked=${staleStatus.transport.importsInvoked} 原生 previous=${JSON.stringify(staleProbe.previous)} 期望=0`
  )
  negative(
    'N6-stale-session-not-imported',
    '反例：陈旧批次绝不允许落到新会话的草稿里（探测时新会话草稿里必须一项都没有；探针自身随后新增 1 项，下面用它给新会话的正向导入做基线）',
    staleProbe.probe.ok === true && staleProbe.previous.length === 0,
    `previous=${staleProbe.previous.length} 期望=0`
  )

  // 正向对照：新会话自己的批次照常导入（证明拒绝不是"什么都干不了"）。
  const freshBatch = 'batch-fresh-b'
  const freshFile = 'file-fresh-b'
  assertOk('batch-begin(fresh-b)', await fed(page, 'switched', batchBeginText({ sessionId: STALE_SESSION, batchId: freshBatch, totalBytes: TINY_BYTES.length })))
  assertOk(
    'file-begin(fresh-b)',
    await fed(page, 'switched', fileBeginText({ sessionId: STALE_SESSION, batchId: freshBatch, fileId: freshFile, name: 'fresh-b.bin', byteLength: TINY_BYTES.length, sha: sha256(TINY_BYTES) }))
  )
  for (const entry of chunkPlan(TINY_BYTES, { sessionId: STALE_SESSION, batchId: freshBatch, fileId: freshFile, chunkBytes: 4096 })) {
    assertOk('fresh-b chunk', await fed(page, 'switched', entry.text))
  }
  const freshEnd = await fed(page, 'switched', fileEndText({ sessionId: STALE_SESSION, batchId: freshBatch, fileId: freshFile, totalBytes: TINY_BYTES.length, sha: sha256(TINY_BYTES) }))
  // 基线：陈旧探针给自己加了 1 项（阅读顺序：陈旧探针 → 本批导入 → 本次探针）。
  const freshProbe = await draftProbe(page, 'after-fresh', 2)
  observations.staleSession.freshEnd = freshEnd.result
  observations.staleSession.freshProbe = { expected: 2, previous: freshProbe.previous, matches: freshProbe.matches }
  gate(
    'G07b-fresh-session-imports',
    '同一页面里新会话自己的批次照常完成导入：新会话的原生草稿确实多了一项（说明上一条拒绝不是"什么都导不进去"）',
    freshEnd.result?.ok === true &&
      freshEnd.result.importInvoked === true &&
      freshEnd.result.import?.ok === true &&
      freshProbe.matches === true &&
      freshProbe.previous.includes(freshEnd.result.import.added[0]),
    `import=${freshEnd.result.import?.ok} added=${JSON.stringify(freshEnd.result.import?.added)} 原生 previous=${JSON.stringify(freshProbe.previous)} 期望=2`
  )
  assertOk(
    'batch-end(fresh-b)',
    await fed(
      page,
      'switched',
      batchEndText(freshBatch, [{ fileId: freshFile, status: 'staged', attachmentIds: freshEnd.result.import?.added ?? [] }], 'staged', STALE_SESSION)
    )
  )

  // ================= G09：撤销 =================
  console.log('\n阶段 6：生命周期撤销\n')
  await evaluate(page, 'globalThis.__D13__.retainBridge()')
  await evaluate(page, 'globalThis.__D13__.make("teardown")')
  await evaluate(page, 'globalThis.__D13__.retain("teardown")')
  assertOk('context(teardown)', await fed(page, 'teardown', contextText({ sessionId: STALE_SESSION })))
  assertOk('batch-begin(teardown)', await fed(page, 'teardown', batchBeginText({ sessionId: STALE_SESSION, batchId: 'batch-teardown', totalBytes: SMALL_BYTES.length })))
  assertOk(
    'file-begin(teardown)',
    await fed(page, 'teardown', fileBeginText({ sessionId: STALE_SESSION, batchId: 'batch-teardown', fileId: 'file-teardown', name: 'teardown.bin', byteLength: SMALL_BYTES.length, sha: SMALL_SHA }))
  )
  const teardownChunk = chunkPlan(SMALL_BYTES, { sessionId: STALE_SESSION, batchId: 'batch-teardown', fileId: 'file-teardown', chunkBytes: 4096 })[0]
  assertOk('teardown chunk', await fed(page, 'teardown', teardownChunk.text))
  const beforeDispose = await addonStatus(page)
  // 同一个接收端的前后对比：状态面里 transport.importsInvoked 是所有接收端的求和，
  // 而撤销后的拒绝发生在**这一个**接收端上，因此这里读它自己的记账。
  const teardownBefore = JSON.parse(await evaluate(page, 'globalThis.__D13__.receiverAccounting("teardown")'))
  const sumImportsBefore = await evaluate(page, 'globalThis.__D13__.sumImports()')
  const dispose = JSON.parse(await evaluate(page, 'globalThis.__D13__.dispose("d13-fixture-disable")'))
  const postEnd = await fed(page, 'teardown', fileEndText({ sessionId: STALE_SESSION, batchId: 'batch-teardown', fileId: 'file-teardown', totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  const postStatus = JSON.parse(await evaluate(page, 'JSON.stringify(globalThis.__DSH_ATTACHMENTS_STATUS__ || {})'))
  const retainedBridge = JSON.parse(await evaluate(page, `globalThis.__D13__.retainedBridgeProbe(${JSON.stringify(STALE_SESSION)})`))
  const postProbe = await draftProbe(page, 'after-teardown', expectedNative)
  const postAddon = await evaluate(page, 'JSON.stringify((globalThis.__DSH_ATTACHMENTS_STATUS__ || {}).addon || null)').then(JSON.parse)
  const sumImportsAfter = await evaluate(page, 'globalThis.__D13__.sumImports()')
  observations.teardown = {
    beforeDisposeImports: beforeDispose.transport.importsInvoked,
    teardownReceiverImportsBefore: teardownBefore.importsInvoked,
    sumImportsBefore,
    sumImportsAfter,
    postAddonDiagnostics: postAddon,
    dispose,
    postEnd: postEnd.result,
    postAccounting: postEnd.accounting,
    retainedBridge: retainedBridge.result ?? retainedBridge,
    diagnostics: postStatus.addon ?? null,
    nativeProbe: { expected: expectedNative, previous: postProbe.previous }
  }
  gate(
    'G09-teardown-revokes',
    '撤销后：自有三个全局被删除、本插件安装的承载被撤掉、保留的接收端/桥引用一律拒绝、且状态面明确报告 ReloadRequired（不假装改动已生效）',
    dispose.addonType === 'undefined' &&
      dispose.bridgeType === 'undefined' &&
      dispose.receiverType === 'undefined' &&
      dispose.hookType === 'undefined' &&
      dispose.diagnostics?.state === 'disposed' &&
      dispose.diagnostics?.reloadRequired?.required === true &&
      /reload-required/.test(dispose.diagnostics?.reloadRequired?.reason ?? '') &&
      postEnd.result?.ok === false &&
      /capability-disabled/.test(postEnd.result?.detail ?? '') &&
      postEnd.accounting?.disposed === true &&
      postEnd.accounting?.importsInvoked === teardownBefore.importsInvoked &&
      sumImportsAfter === sumImportsBefore &&
      retainedBridge.result?.ok === false &&
      retainedBridge.result?.code === 'capability-disabled' &&
      postProbe.probe.ok === false,
    `全局 addon/bridge/receiver/hook=${dispose.addonType}/${dispose.bridgeType}/${dispose.receiverType}/${dispose.hookType} 诊断 state=${dispose.diagnostics?.state} reloadRequired=${dispose.diagnostics?.reloadRequired?.required} 传输=${postEnd.result?.code}/${postEnd.result?.detail?.slice(0, 40)} 保留桥=${retainedBridge.result?.code} 本接收端导入次数=${teardownBefore.importsInvoked}→${postEnd.accounting?.importsInvoked} 全部接收端导入求和=${sumImportsBefore}→${sumImportsAfter}`
  )
  negative(
    'N7-post-teardown-transfer-refused',
    '反例：撤销之后同一条传输不得再导入任何东西（保留的引用也必须被拒；撤销前同款传输是成功的，见 G05）',
    postEnd.result?.ok === false &&
      postEnd.accounting?.importsInvoked === teardownBefore.importsInvoked &&
      sumImportsAfter === sumImportsBefore &&
      postProbe.probe.ok === false,
    `post=${postEnd.result?.code ?? '(竟然通过)'} 本接收端 importsInvoked ${teardownBefore.importsInvoked}→${postEnd.accounting?.importsInvoked} 原生探针=${postProbe.probe.result?.code ?? postProbe.probe.reason}`
  )

  await writeFile(join(evidenceDir, 'd13-page-errors.json'), JSON.stringify({ main: app.pageErrors }, null, 2))
  await page.close()
  openPages.splice(openPages.indexOf(page), 1)

  // ================= 对照组：健康页面的承载会真的发请求 =================
  console.log('\n阶段 7：健康页面承载会真的发请求（降级对照组的正向基线）\n')
  const healthyApp = await openAppPage({ chrome, lanBase, deviceId, sessionId: SESSION, device: deviceId })
  openPages.push(healthyApp.page)
  const healthyPage = healthyApp.page
  const healthyRequests = []
  healthyPage.onEvent((message) => {
    if (message.method === 'Network.requestWillBeSent') {
      const url = String(message.params?.request?.url ?? '')
      if (url.includes('uploadFileBinary')) healthyRequests.push(url)
    }
  })
  const healthyInstall = await installHarness(healthyPage)
  if (healthyInstall.ok !== true) throw new Error(`对照页面侧装置未装上：${healthyInstall.reason}`)
  const healthyCarrier = JSON.parse(await evaluate(healthyPage, 'globalThis.__D13__.carrierProbe()'))
  await sleep(1500)
  observations.healthyCarrier = { carrier: healthyCarrier, requests: healthyRequests }
  negative(
    'N9-carrier-control-group',
    '对照组（非恒真证明）：同一个承载探针在健康页面上会真的发出上传请求、且 URL 被 remote 改写为 /remote/api/…；因此 N8 的"零请求"是降级判定生效的结果，不是探针本身不会发请求',
    healthyRequests.length >= 1 && healthyRequests.every((url) => url.includes('/remote/api/session/uploadFileBinary')),
    `健康页面 承载结果=${JSON.stringify(healthyCarrier)} 请求数=${healthyRequests.length} 首个=${healthyRequests[0] ?? '(无)'}`
  )
  // 对照组用完即关：后面的降级判据要停掉健康服务再启降级 profile。
  await healthyPage.close()
  openPages.splice(openPages.indexOf(healthyPage), 1)

  // ================= G08：远程降级 =================
  console.log('\n阶段 8：远程降级（remote 客户端通道层未安装）\n')
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = null
  degradedService = startService(dshBin, ['--profile', degradedInfo.profile, '--no-open'], { cwd: repoRoot, dshHome, logPath: degradedLog })
  await writeFile(join(fixtureRoot, 'service.pid'), String(degradedService.child.pid))
  await waitForReady(degradedService.child, degradedLog, 120_000)
  const degradedBase = `http://${lanAddress}:${degradedInfo.port}`

  const degradedClient = createClient()
  const degradedIssue = await fetch(`http://127.0.0.1:${degradedInfo.port}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const degradedToken = (await degradedIssue.json()).token
  await degradedClient.follow(`${degradedBase}/pair-accept?pair=${degradedToken}`)
  const degradedDevice = degradedClient.jar.get('dsh_pair') ?? ''

  const degradedApp = await openAppPage({
    chrome,
    lanBase: degradedBase,
    originBase: degradedBase,
    deviceId: degradedDevice,
    sessionId: SESSION,
    device: degradedDevice
  })
  openPages.push(degradedApp.page)
  const degradedPage = degradedApp.page
  const degradedInstall = await installHarness(degradedPage)
  if (degradedInstall.ok !== true) throw new Error(`降级页面侧装置未装上：${degradedInstall.reason}`)

  // 网络观测：统计发往上传端点的请求（含被 remote 改写的路径）。
  const uploadRequests = []
  degradedPage.onEvent((message) => {
    if (message.method === 'Network.requestWillBeSent') {
      const url = String(message.params?.request?.url ?? '')
      if (url.includes('uploadFileBinary')) uploadRequests.push(url)
    }
  })

  await evaluate(degradedPage, 'globalThis.__D13__.make("degraded")')
  const degradedContext = await fed(degradedPage, 'degraded', contextText())
  const degradedBegin = await fed(degradedPage, 'degraded', batchBeginText({ batchId: 'batch-degraded', totalBytes: SMALL_BYTES.length }))
  const degradedCancel = degradedBegin.outgoing.find((message) => message.type === 'cancel')
  const degradedStatus = await addonStatus(degradedPage)
  const degradedCarrier = JSON.parse(await evaluate(degradedPage, 'globalThis.__D13__.carrierProbe()'))
  await sleep(1500)
  const degradedProbe = await draftProbe(degradedPage, 'degraded', 0)
  // 配对/心跳/设备通道照常。
  const degradedHeartbeat = await degradedClient.fetch(`${degradedBase}/api/pair/heartbeat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': degradedDevice },
    body: '{}'
  })
  const degradedRpc = await rpc(degradedClient, degradedBase, degradedDevice, 'session/list', { _request: {} })

  observations.degraded = {
    profile: degradedInfo.profile,
    port: degradedInfo.port,
    install: degradedInstall.install,
    capability: degradedStatus.capability,
    upload: degradedStatus.upload,
    context: degradedContext.result,
    begin: degradedBegin.result,
    cancel: degradedCancel ?? null,
    carrier: degradedCarrier,
    uploadRequests,
    importsInvoked: degradedStatus.transport.importsInvoked,
    pairing: { heartbeatStatus: degradedHeartbeat.status, sessionListStatus: degradedRpc.status }
  }
  gate(
    'G08-remote-degraded-unavailable',
    '远程降级页面（局域网、无 /remote 改写层）上：remote seat 确实不在，附件能力明确 unavailable（capability-disabled），上传路由不再是 remote-rewrite，整批被拒且导入次数为 0',
    degradedInstall.install.remoteSeat === false &&
      degradedStatus.capability.status === 'unavailable' &&
      degradedStatus.capability.code === 'capability-disabled' &&
      degradedStatus.capability.facts.remoteSeat === false &&
      degradedStatus.capability.facts.originClass === 'lan' &&
      degradedStatus.upload.route === 'refused-no-remote-channel' &&
      degradedBegin.result?.ok === false &&
      /capability-disabled/.test(degradedBegin.result?.detail ?? '') &&
      degradedCancel?.reason === 'capability-disabled' &&
      degradedStatus.transport.importsInvoked === 0 &&
      degradedProbe.probe.ok === true &&
      degradedProbe.probe.result?.ok === false &&
      degradedProbe.probe.result?.code === 'capability-disabled',
    `seat=${degradedInstall.install.remoteSeat} cap=${degradedStatus.capability.status}/${degradedStatus.capability.code} route=${degradedStatus.upload.route} 整批=${degradedBegin.result?.code} cancel=${degradedCancel?.reason} 桥探针=${degradedProbe.probe.result?.code ?? degradedProbe.probe.reason} 承载探针=${degradedCarrier.thrown === true ? degradedCarrier.code : JSON.stringify(degradedCarrier)}`
  )
  gate(
    'G08b-no-bare-api-fallback',
    '承载在降级页面上**主动拒绝**上传且一个请求都不发：该页面的裸 /api 其实是可达的（夹具 trustedHosts 放行），因此"零请求"是对本插件拒绝回退的真实判据',
    degradedCarrier.thrown === true &&
      degradedCarrier.code === 'capability-disabled' &&
      uploadRequests.length === 0,
    `承载结果=${degradedCarrier.thrown === true ? degradedCarrier.code : JSON.stringify(degradedCarrier)} 上传请求数=${uploadRequests.length}`
  )
  gate(
    'G08c-pairing-still-works',
    '降级页面上配对/心跳/设备通道照常工作（心跳 200、/remote 读会话列表成功）：附件能力停用不会关闭配对路径',
    degradedHeartbeat.status === 200 &&
      degradedRpc.status === 200 &&
      (degradedRpc.body?.result?.value?.items ?? []).length === ids.length,
    `heartbeat=${degradedHeartbeat.status} session/list=${degradedRpc.status} 会话数=${(degradedRpc.body?.result?.value?.items ?? []).length}`
  )
  negative(
    'N8-degraded-refuses-without-request',
    '反例：降级页面上的承载调用必须以 capability-disabled 拒绝，且不得产生任何上传请求（对比健康页面：那里的承载会真的发出请求）',
    degradedCarrier.thrown === true && degradedCarrier.code === 'capability-disabled' && uploadRequests.length === 0,
    `thrown=${degradedCarrier.thrown} code=${degradedCarrier.code} 请求数=${uploadRequests.length}`
  )

  await degradedPage.close()
  openPages.splice(openPages.indexOf(degradedPage), 1)
  await chrome.close()
  chrome = null
} catch (error) {
  fatal = error
  gate('G00-runtime', '判据脚本自身跑完（无致命异常）', false, String(error?.stack ?? error).slice(0, 400))
} finally {
  for (const page of openPages) await page.close().catch(() => {})
  if (chrome !== null) await chrome.close().catch(() => {})
  if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid')).catch(() => {})
  if (degradedService !== null) await stopService(degradedService.child, join(fixtureRoot, 'service.pid')).catch(() => {})
}

// ---------- 汇总、写证据、清理 ----------

const failed = gates.filter((item) => !item.ok)
const report = {
  schemaVersion: 1,
  task: 'D13',
  purpose: 'addon-bridge',
  platform: 'linux',
  method:
    '真实夹具（私有 DSH_HOME + 真实 web profile + 真实 Chromium，LAN 普通 HTTP 非安全上下文）；消息由生产 codec 构造，接收端/桥/草稿适配器都是已构建插件的真实代码；原生草稿以 attachmentIds 回读（不是 DOM 芯片）；降级判定用独立 profile（remote 客户端通道层未安装）验证，并对照健康页面的网络行为',
  gates,
  failedGateIds: failed.map((item) => item.id),
  result: failed.length === 0 ? 'pass' : 'fail',
  negativeProbes,
  observations
}
if (fatal !== null) report.fatal = String(fatal?.stack ?? fatal).slice(0, 2000)

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd13-bridge-gates.json'), redact(JSON.stringify(report, null, 2)) + '\n')
await writeFile(join(durableEvidenceDir, 'd13-bridge-gates.json'), redact(JSON.stringify(report, null, 2)) + '\n')

console.log(`\nD13 gates: ${gates.length - failed.length}/${gates.length} 通过；负向探针 ${negativeProbes.filter((item) => item.ok).length}/${negativeProbes.length}`)
if (failed.length > 0) {
  console.error('未通过判据：')
  for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
}

// 清理：只停夹具服务并删除夹具目录；证据已落盘，随后把夹具侧副本补回。
const down = spawnSync('bash', [join(here, 'down.sh')], { cwd: repoRoot, encoding: 'utf8' })
observations.cleanup.downSh = { status: down.status, tail: String(down.stdout ?? '').split('\n').slice(-3).join(' | ') }
const leftovers = spawnSync('bash', ['-lc', "pgrep -af 'dsh-attachments-fixture|dsh-attachments-headless|dsh-attachments-degraded|dsh-cdp-' || true"], { encoding: 'utf8' })
observations.cleanup.leftoverProcesses = String(leftovers.stdout ?? '')
  .trim()
  .split('\n')
  .filter((line) => line.length > 0 && line.includes('pgrep') === false)
console.log(`清理：down.sh exit=${down.status}；夹具残留进程=${observations.cleanup.leftoverProcesses.length}`)

const finalReport = redact(JSON.stringify(report, null, 2)) + '\n'
await mkdir(evidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd13-bridge-gates.json'), finalReport)
await writeFile(join(durableEvidenceDir, 'd13-bridge-gates.json'), finalReport)

if (failed.length > 0) process.exitCode = 1
