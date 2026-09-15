#!/usr/bin/env node
/**
 * D12 真实 Chromium 判据：浏览器分块接收端。
 *
 * 目标：在**真实夹具页面**（LAN + 普通 HTTP，非 SecureContext）里用**已构建的插件**
 * 把 v1 分块消息组装成真实浏览器 `File`，并把组装结果交给**生产草稿路径**
 * （`__DSH_ATTACHMENTS_BRIDGE__.importFiles` → 生产 draft-adapter），而不是测试钩子。
 *
 * 判据（全部读真实浏览器里的真实对象）：
 *   G01 接收端宿主已安装、版本可核对，且桥与当前会话就绪
 *   G02 页面确实是普通 HTTP 非安全上下文（`crypto.subtle` 缺失），接收端报 pure-js 后端
 *   G03 分块（含 256 KiB 满块）组装成真实 File：名称/类型/长度正确，
 *       **在页面里把 File 的字节读回来**，由 Node 用 node:crypto 与源逐字节比对并比对 SHA-256
 *   G03b ack 只反映接收缓冲与在途窗口（buffer-only 语义）
 *   G04 该 File 走的是生产草稿路径：草稿真的新增了原生 attachmentId（用下一次导入的 previous 回读）
 *   G05 故意写坏的哈希不得导入：草稿不新增任何 id，也不构造 File
 *   G06 超限/越界的传输被**拒绝而不是缓冲**（codec 字段上限、生效批次限额、目标暂存上限、累计越界）
 *   G07 cancel 回收缓冲：页面内记账在取消后回到 0，迟到块不得再入缓冲
 *   G08 同会话新批次：不重发 context，新 batchId 被 batch-begin 承认并且完整导入；旧 batchId 与迟到 file-end 不得重建操作
 *   G09 强制关掉 crypto.subtle 后组装与哈希校验照常成功（坏哈希仍被拒）
 *   G10 重放幂等：重复块/重复 file-end 不产生第二个 File、不第二次导入
 *   G11 全程结束后接收缓冲为 0、峰值不超过上限、保留批次有界
 *   G12 cancel 之后同会话可开新批次并完成导入；复用被取消的 batchId 被确定拒绝（R15 回归）
 *
 * 负向探针见 negativeProbes：坏哈希、错长度、跨文件块、取消中途、无 WebCrypto 坏哈希、
 * 超限不缓冲、内容比对可证伪——都必须**失败**，否则判据是恒真的。
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
  session: null,
  page: {},
  files: {},
  accounting: {},
  draft: {},
  acks: {},
  limits: {},
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

/** 断言接收端接受一条消息；否则抛出（由外层转成 G00 失败）。 */
function assertOk(label, fed) {
  if (fed?.ok !== true || fed.result?.ok !== true) {
    throw new Error(`${label} 未被接受：${JSON.stringify(fed?.result ?? fed)}`)
  }
}

/** 内容比对判据：长度 + SHA-256 + 逐字节；负向探针会拿改坏的字节再跑一次，必须为 false。 */
function contentMatches(bytes, source) {
  if (bytes.length !== source.length) return { ok: false, detail: `长度 ${bytes.length} ≠ ${source.length}` }
  const readHash = sha256(bytes)
  const sourceHash = sha256(source)
  if (readHash !== sourceHash) return { ok: false, detail: `哈希 ${readHash.slice(0, 16)} ≠ ${sourceHash.slice(0, 16)}` }
  for (let index = 0; index < bytes.length; index += 1) {
    if (bytes[index] !== source[index]) return { ok: false, detail: `第 ${index} 字节不同` }
  }
  return { ok: true, detail: `${bytes.length}B sha256=${readHash.slice(0, 16)}…` }
}

// ---------- 素材 ----------

/** 生成恰好 size 字节的确定性 ASCII 文本（1 字符 = 1 字节）。 */
function makeTextBytes(size, marker) {
  const lines = []
  let total = 0
  let index = 0
  while (total < size) {
    const line = `${marker} line ${String(index).padStart(6, '0')} ${'x'.repeat(24)}`
    lines.push(line)
    total += line.length + 1
    index += 1
  }
  let text = lines.join('\n')
  if (text.length < size) text += 'x'.repeat(size - text.length)
  return Buffer.from(text.slice(0, size), 'ascii')
}

const MARKER = 'DSH-D12-RECEIVER-MARKER'
/** 520 KiB：两块满 256 KiB + 一块尾块，专门覆盖"满块 + 多块"。 */
const MAIN_BYTES = makeTextBytes(520 * 1024, MARKER)
const MAIN_SHA = sha256(MAIN_BYTES)
/** 13065 字节，用于"同会话新批次"与重放幂等。 */
const SMALL_BYTES = makeTextBytes(3 * 4096 + 777, `${MARKER}-SMALL`)
const SMALL_SHA = sha256(SMALL_BYTES)
/** 96 KiB，分 3 块，用于强制无 WebCrypto 路径。 */
const NO_SUBTLE_BYTES = makeTextBytes(3 * 32 * 1024, `${MARKER}-NOSUBTLE`)
const NO_SUBTLE_SHA = sha256(NO_SUBTLE_BYTES)
/** 16 KiB，用于坏哈希与取消。 */
const CORRUPT_BYTES = makeTextBytes(2 * 8192, `${MARKER}-CORRUPT`)

// ---------- 页面侧装置（安装一次） ----------

const INSTALL_HARNESS = `(() => {
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  if (host === undefined || host === null) return JSON.stringify({ ok: false, reason: 'no-receiver-host' })
  const state = { receivers: {}, files: {} }
  globalThis.__D12__ = state

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
    attachmentIds: message.attachmentIds === undefined ? null : Array.from(message.attachmentIds),
    ids: message.attachmentIds === undefined ? null : Array.from(message.attachmentIds)
  })

  state.make = (name, options) => {
    state.receivers[name] = host.create(options === undefined ? {} : options)
    return state.receivers[name]
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
      // 原样（未经整形）的待发消息：Node 侧用生产 codec 复核线协议合法性。
      wire: outgoing.map((message) => JSON.parse(JSON.stringify(message))),
      accounting: receiver.accounting
    })
  }

  state.accounting = (name) => JSON.stringify(state.receivers[name].accounting)
  state.batchState = (name) => JSON.stringify({
    activeBatchId: state.receivers[name].activeBatchId,
    identity: state.receivers[name].currentIdentity,
    accounting: state.receivers[name].accounting
  })

  /** 在页面里把组装出的 File 的字节读回来（base64 交给 Node 判定）。 */
  state.readBack = async (name) => {
    const file = state.files[name]
    if (file === undefined) return JSON.stringify({ ok: false, reason: 'no-file' })
    const bytes = new Uint8Array(await file.arrayBuffer())
    let binary = ''
    const step = 0x8000
    for (let index = 0; index < bytes.length; index += step) {
      binary += String.fromCharCode.apply(null, bytes.subarray(index, index + step))
    }
    return JSON.stringify({
      ok: true,
      name: file.name,
      type: file.type,
      size: file.size,
      isFile: file instanceof File,
      isBlob: file instanceof Blob,
      constructorName: file.constructor === undefined ? null : file.constructor.name,
      base64: btoa(binary)
    })
  }

  /** 强制让 crypto.subtle 不可用（并记录它本来是什么）。 */
  state.forceNoSubtle = () => {
    const before = typeof globalThis.crypto?.subtle
    let forced = 'no-op'
    try {
      Object.defineProperty(globalThis.crypto, 'subtle', { value: undefined, configurable: true })
      forced = 'own-property'
    } catch (error) {
      forced = 'failed:' + String(error && error.message ? error.message : error)
    }
    return JSON.stringify({
      before,
      forced,
      after: typeof globalThis.crypto?.subtle,
      ownDescriptor: Object.getOwnPropertyDescriptor(globalThis.crypto, 'subtle') !== undefined,
      isSecureContext: globalThis.isSecureContext === true
    })
  }

  /** 用生产桥探一次草稿：previous 是本次导入前原生的 attachmentIds（本探针自己会新增一项）。 */
  state.draftProbe = (label) => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    if (bridge === undefined) return JSON.stringify({ ok: false, reason: 'no-bridge' })
    const bytes = new TextEncoder().encode('d12-probe-' + label + '-' + Date.now())
    const file = new File([bytes], 'd12-probe.txt', { type: 'text/plain' })
    try {
      return JSON.stringify({ ok: true, result: bridge.importFiles({ files: [file] }) })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  state.globals = () => JSON.stringify({
    isSecureContext: globalThis.isSecureContext === true,
    subtle: typeof globalThis.crypto?.subtle,
    bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
    bridgeVersion: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.version ?? null,
    currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
    receiverHost: typeof host,
    receiverVersion: host.version,
    create: typeof host.create
  })

  return JSON.stringify({ ok: true, globals: JSON.parse(state.globals()) })
})()`

// ---------- Node 侧消息构造（每条都先用生产 codec 校验） ----------

let SESSION = ''

function messageText(raw) {
  const decoded = decodeMessage(JSON.stringify(raw))
  if (!decoded.ok) throw new Error(`门禁构造的消息不合法：${decoded.code} ${decoded.detail} @${decoded.path}`)
  return JSON.stringify(decoded.message)
}

/** 故意越界的报文：不做 codec 预校验，让接收端的 codec 自己拒绝（门禁只做记录）。 */
const rawMessageText = (raw) => JSON.stringify(raw)

const contextText = () =>
  messageText({
    v: 1,
    type: 'context',
    sessionId: SESSION,
    targetId: 'target-d12',
    documentEpoch: 11,
    composerEpoch: 22,
    composerScope: 'composer-scope-d12'
  })

const batchBeginText = ({ batchId, fileCount = 1, totalBytes = 0 }) =>
  messageText({
    v: 1,
    type: 'batch-begin',
    sessionId: SESSION,
    batchId,
    targetId: 'target-d12',
    documentEpoch: 11,
    composerEpoch: 22,
    fileCount,
    totalBytes
  })

const fileBeginText = ({ batchId, fileId, name, mime, byteLength, sha }) =>
  messageText({
    v: 1,
    type: 'file-begin',
    sessionId: SESSION,
    batchId,
    fileId,
    name,
    byteLength,
    mime,
    ...(sha === undefined ? {} : { sha256: sha })
  })

const fileEndText = ({ batchId, fileId, totalBytes, sha }) =>
  messageText({ v: 1, type: 'file-end', sessionId: SESSION, batchId, fileId, totalBytes, sha256: sha })

const cancelText = (batchId, reason = 'cancelled') =>
  messageText({ v: 1, type: 'cancel', sessionId: SESSION, batchId, reason, stage: 'protocol-transfer' })

const batchEndText = (batchId, results, status = 'staged') =>
  messageText({ v: 1, type: 'batch-end', sessionId: SESSION, batchId, status, results })

/** 按固定块大小生成 chunk 消息；返回 {plan, text}，便于断言。 */
function chunkPlan(bytes, { batchId, fileId, chunkBytes }) {
  return planChunks(bytes.length, chunkBytes).map((plan) => ({
    plan,
    text: messageText({
      v: 1,
      type: 'chunk',
      sessionId: SESSION,
      batchId,
      fileId,
      seq: plan.seq,
      offset: plan.offset,
      byteLength: plan.byteLength,
      dataBase64: base64Encode(bytes.subarray(plan.offset, plan.offset + plan.byteLength))
    })
  }))
}

// ---------- 页面交互 ----------

const evaluate = (page, expression) => page.evaluate(expression, { awaitPromise: true })

async function feed(page, name, text) {
  return JSON.parse(await evaluate(page, `globalThis.__D12__.feed(${JSON.stringify(name)}, ${JSON.stringify(text)})`))
}

async function readBack(page, name) {
  return JSON.parse(await evaluate(page, `globalThis.__D12__.readBack(${JSON.stringify(name)})`))
}

async function accounting(page, name) {
  return JSON.parse(await evaluate(page, `globalThis.__D12__.accounting(${JSON.stringify(name)})`))
}

async function batchState(page, name) {
  return JSON.parse(await evaluate(page, `globalThis.__D12__.batchState(${JSON.stringify(name)})`))
}

/** 探一次草稿并把 previous 与期望条数比对（previous 是本次探针之前的原生 id 列表）。 */
async function draftProbe(page, label, expectedCount) {
  const probe = JSON.parse(await evaluate(page, `globalThis.__D12__.draftProbe(${JSON.stringify(label)})`))
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

async function rpc(client, lanBase, deviceId, method, args) {
  const response = await client.fetch(`${lanBase}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({
      type: 'client-request',
      rpcId: `d12-${method.replace(/\W/g, '-')}-${Date.now()}`,
      method,
      payload: { args }
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

// =====================================================================
// 主流程
// =====================================================================

let service = null
let chrome = null
const openPages = []
let fatal = null
const serviceLog = join(evidenceDir, 'service-d12-receiver.log')

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })

try {
  console.log('D12 浏览器分块接收端判据（真实 Chromium + 真实夹具）\n')
  if (existsSync(join(fixtureRoot, 'lan-address.txt')) === false) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  const lanBase = `http://${lanAddress}:${port}`

  console.log(`阶段 0：造真实会话并启动夹具服务（${lanBase}）\n`)
  const seed = await seedSession('d12 receiver gate seed')
  observations.session = { seedExitCode: seed.code }

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
  const sessionId = items.map((item) => item.sessionId).find((id) => typeof id === 'string') ?? null
  if (sessionId === null) throw new Error('共享 DSH_HOME 里没有可用会话（headless 落盘失败）')
  SESSION = sessionId
  observations.session.id = sessionId
  console.log(`  会话=${sessionId}\n`)

  chrome = await launchChrome({})
  const page = await openPage(chrome.debugPort)
  openPages.push(page)
  const pageErrors = []
  page.onEvent((message) => {
    if (message.method === 'Runtime.exceptionThrown') {
      pageErrors.push(String(message.params.exceptionDetails?.exception?.description ?? '').slice(0, 200))
    }
  })
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
  })
  await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId)}`)

  const ready = await poll(
    async () =>
      JSON.parse(
        await evaluate(
          page,
          `JSON.stringify({
            bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
            currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
            receiverHost: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__,
            receiverVersion: globalThis.__DSH_ATTACHMENTS_RECEIVER__?.version ?? null
          })`
        ).catch(() => '{"bridge":"undefined"}')
      ),
    (value) => value.bridge === 'object' && value.currentSession === sessionId && value.receiverHost === 'object',
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  const install = JSON.parse(await evaluate(page, INSTALL_HARNESS))
  const globals = JSON.parse(await evaluate(page, 'globalThis.__D12__.globals()'))
  observations.page.globals = globals
  observations.page.ready = ready.ok

  gate(
    'G01-receiver-installed',
    '已构建插件在真实页面安装了接收端宿主（版本 1、create 可用），且桥与当前会话就绪',
    install.ok === true &&
      globals.receiverHost === 'object' &&
      globals.receiverVersion === 1 &&
      globals.create === 'function' &&
      globals.bridge === 'object' &&
      globals.currentSession === sessionId,
    `ready=${ready.ok} host=${globals.receiverHost} version=${globals.receiverVersion} create=${globals.create} bridge=${globals.bridge}/${globals.bridgeVersion} current=${globals.currentSession}`
  )

  // ================= G02：HTTP 无 WebCrypto =================
  await evaluate(page, 'globalThis.__D12__.make("basic")')
  const basicAccounting = await accounting(page, 'basic')
  observations.accounting.basicInitial = basicAccounting
  gate(
    'G02-http-no-webcrypto',
    '夹具页面是普通 HTTP 非安全上下文（crypto.subtle 缺失），接收端因此选择纯 JS 增量哈希后端',
    globals.isSecureContext === false && globals.subtle === 'undefined' && basicAccounting.hashBackend === 'pure-js',
    `isSecureContext=${globals.isSecureContext} subtle=${globals.subtle} hashBackend=${basicAccounting.hashBackend}`
  )

  // ================= G03：多块组装成真实 File =================
  console.log('\n阶段 1：多块组装（含 256 KiB 满块）、字节回读与生产草稿路径\n')
  let expectedDraftCount = 0

  const mainBatch = 'batch-main'
  const mainFile = 'file-main'
  const mainName = 'd12-receiver-main.txt'
  assertOk('context', await feed(page, 'basic', contextText()))
  assertOk('batch-begin(main)', await feed(page, 'basic', batchBeginText({ batchId: mainBatch, totalBytes: MAIN_BYTES.length })))
  assertOk(
    'file-begin(main)',
    await feed(
      page,
      'basic',
      fileBeginText({ batchId: mainBatch, fileId: mainFile, name: mainName, mime: 'text/plain', byteLength: MAIN_BYTES.length, sha: MAIN_SHA })
    )
  )

  const mainChunks = chunkPlan(MAIN_BYTES, { batchId: mainBatch, fileId: mainFile, chunkBytes: 256 * 1024 })
  const acks = []
  for (const entry of mainChunks) {
    const fed = await feed(page, 'basic', entry.text)
    assertOk(`chunk seq=${entry.plan.seq}`, fed)
    if (fed.outgoing.length !== 1) throw new Error(`每接受一块应恰好一条 ack，实际 ${fed.outgoing.length}`)
    acks.push(fed.outgoing[0])
    // 产出的 ack 必须是线协议合法报文（用生产 codec 复核原样消息，不是整形后的视图）。
    const recheck = decodeMessage(JSON.stringify(fed.wire[0]))
    if (recheck.ok !== true) throw new Error(`ack 不是线协议合法报文：${recheck.code} ${recheck.detail}`)
  }
  const mainEnd = await feed(page, 'basic', fileEndText({ batchId: mainBatch, fileId: mainFile, totalBytes: MAIN_BYTES.length, sha: MAIN_SHA }))
  assertOk('file-end(main)', mainEnd)
  if (mainEnd.result.import?.ok === true) expectedDraftCount += mainEnd.result.import.added.length

  const mainRead = await readBack(page, 'basic')
  const readBytes = mainRead.ok === true ? Buffer.from(mainRead.base64, 'base64') : Buffer.alloc(0)
  const content = mainRead.ok === true ? contentMatches(readBytes, MAIN_BYTES) : { ok: false, detail: mainRead.reason }
  observations.files.main = {
    sourceBytes: MAIN_BYTES.length,
    sourceSha256: MAIN_SHA,
    assembled: mainEnd.result.assembled,
    fileFacts: {
      name: mainRead.name,
      type: mainRead.type,
      size: mainRead.size,
      isFile: mainRead.isFile,
      isBlob: mainRead.isBlob,
      constructorName: mainRead.constructorName
    },
    readBackBytes: readBytes.length,
    readBackSha256: mainRead.ok === true ? sha256(readBytes) : null,
    content,
    chunkCount: mainChunks.length,
    chunkSizes: mainChunks.map((entry) => entry.plan.byteLength),
    acks
  }
  gate(
    'G03-multichunk-real-file',
    '分块（3 块，其中两块为 256 KiB 满块）组装成真实浏览器 File；名称/类型/长度正确，页面读回的字节与源逐字节相同且 SHA-256 相等',
    mainChunks.length >= 3 &&
      content.ok === true &&
      mainRead.isFile === true &&
      mainRead.isBlob === true &&
      mainRead.name === mainName &&
      mainRead.type === 'text/plain' &&
      mainRead.size === MAIN_BYTES.length &&
      mainEnd.result.assembled?.sha256 === MAIN_SHA &&
      mainEnd.result.record?.transport === 'buffered' &&
      mainEnd.result.record?.draft === 'staged' &&
      mainEnd.result.record?.upload === 'harness-owned' &&
      acks.length === mainChunks.length &&
      acks.every(
        (ack, index) =>
          ack.type === 'ack' &&
          ack.seq === index &&
          ack.offset === mainChunks[index].plan.offset &&
          ack.byteLength === mainChunks[index].plan.byteLength &&
          ack.bufferedBytes === mainChunks[index].plan.offset + mainChunks[index].plan.byteLength
      ),
    `块=${mainChunks.length} 尺寸=${observations.files.main.chunkSizes.join(',')} File=${mainRead.size}B type=${mainRead.type} isFile=${mainRead.isFile} 回读=${readBytes.length}B ${content.detail} 三态=${mainEnd.result.record?.transport}/${mainEnd.result.record?.draft}/${mainEnd.result.record?.upload}`
  )
  gate(
    'G03b-ack-buffer-only',
    'ack 只证明接收缓冲已接受该块：bufferedBytes 等于接收缓冲累计字节，inFlight 落在 0..2，且 ack 全部线协议合法',
    acks.every((ack) => ack.type === 'ack' && ack.inFlight >= 0 && ack.inFlight <= 2) &&
      acks[0].bufferedBytes === mainChunks[0].plan.byteLength &&
      acks[acks.length - 1].bufferedBytes === MAIN_BYTES.length,
    `ack[0].bufferedBytes=${acks[0]?.bufferedBytes} ack[0].byteLength=${acks[0]?.byteLength} inFlight=${acks.map((ack) => ack.inFlight).join(',')}`
  )

  // ================= G04：生产草稿路径 =================
  const draftAfterMain = await draftProbe(page, 'after-main', expectedDraftCount)
  const importedIds = mainEnd.result.import?.added ?? []
  observations.draft.afterMain = { expectedCount: expectedDraftCount, previous: draftAfterMain.previous, importedIds }
  gate(
    'G04-production-draft-path',
    '组装出的 File 经生产桥进入原生草稿：接收端报告的新增 ID 出现在下一次导入读到的原生 attachmentIds 里',
    mainEnd.result.import?.ok === true &&
      importedIds.length === 1 &&
      draftAfterMain.matches === true &&
      draftAfterMain.previous.includes(importedIds[0]),
    `import.ok=${mainEnd.result.import?.ok} added=${JSON.stringify(importedIds)} previous=${JSON.stringify(draftAfterMain.previous)} 期望条数=${expectedDraftCount}`
  )
  expectedDraftCount += (draftAfterMain.probe.result?.added ?? []).length

  // 关掉主批次（后续"同会话新批次"才有意义）。
  const closeMain = await feed(page, 'basic', batchEndText(mainBatch, [{ fileId: mainFile, status: 'staged', attachmentIds: importedIds }]))
  assertOk('batch-end(main)', closeMain)

  // ================= G05：坏哈希不得导入 =================
  console.log('\n阶段 2：坏哈希、错长度与跨文件块\n')
  const corruptBatch = 'batch-corrupt'
  const corruptFile = 'file-corrupt'
  assertOk('batch-begin(corrupt)', await feed(page, 'basic', batchBeginText({ batchId: corruptBatch, totalBytes: CORRUPT_BYTES.length })))
  assertOk(
    'file-begin(corrupt)',
    await feed(
      page,
      'basic',
      fileBeginText({ batchId: corruptBatch, fileId: corruptFile, name: 'd12-corrupt.txt', mime: 'text/plain', byteLength: CORRUPT_BYTES.length })
    )
  )
  for (const entry of chunkPlan(CORRUPT_BYTES, { batchId: corruptBatch, fileId: corruptFile, chunkBytes: 8192 })) {
    assertOk(`corrupt chunk seq=${entry.plan.seq}`, await feed(page, 'basic', entry.text))
  }
  const corruptBefore = await accounting(page, 'basic')
  const corruptEnd = await feed(
    page,
    'basic',
    fileEndText({ batchId: corruptBatch, fileId: corruptFile, totalBytes: CORRUPT_BYTES.length, sha: 'f'.repeat(64) })
  )
  const corruptAfter = await accounting(page, 'basic')
  const draftAfterCorrupt = await draftProbe(page, 'after-corrupt', expectedDraftCount)
  observations.files.corrupt = { sourceBytes: CORRUPT_BYTES.length, sourceSha256: sha256(CORRUPT_BYTES), end: corruptEnd.result, before: corruptBefore, after: corruptAfter }
  gate(
    'G05-corrupt-hash-not-imported',
    '故意写坏的 SHA-256 被拒绝（hash-mismatch）；不构造 File、不调用导入，原生草稿也不新增任何 attachmentId',
    corruptEnd.result?.ok === false &&
      corruptEnd.result.code === 'hash-mismatch' &&
      corruptAfter.filesConstructed === corruptBefore.filesConstructed &&
      corruptAfter.importsInvoked === corruptBefore.importsInvoked &&
      draftAfterCorrupt.matches === true,
    `code=${corruptEnd.result?.code} filesConstructed ${corruptBefore.filesConstructed}→${corruptAfter.filesConstructed} importsInvoked ${corruptBefore.importsInvoked}→${corruptAfter.importsInvoked} 草稿 previous=${JSON.stringify(draftAfterCorrupt.previous)} 期望条数=${expectedDraftCount}`
  )
  expectedDraftCount += (draftAfterCorrupt.probe.result?.added ?? []).length

  negative(
    'N1-corrupt-hash-rejected',
    '反例：同长度但错误的哈希必须失败（hash-mismatch）；同款传输用正确哈希时成功（见 G03），故判定不是恒真',
    corruptEnd.result?.ok === false && corruptEnd.result.code === 'hash-mismatch',
    `code=${corruptEnd.result?.code ?? '(竟然通过)'} 正确哈希成功见 G03（ok=${mainEnd.result.ok}）`
  )

  const wrongLength = await feed(
    page,
    'basic',
    fileEndText({ batchId: corruptBatch, fileId: corruptFile, totalBytes: CORRUPT_BYTES.length + 1, sha: sha256(CORRUPT_BYTES) })
  )
  negative(
    'N2-wrong-length-rejected',
    '反例：file-end 声明长度与 file-begin 不符必须失败（size-mismatch）',
    wrongLength.result?.ok === false && wrongLength.result.code === 'size-mismatch',
    `code=${wrongLength.result?.code ?? '(竟然通过)'}`
  )

  const strayChunk = chunkPlan(CORRUPT_BYTES.subarray(0, 4096), { batchId: corruptBatch, fileId: 'file-elsewhere', chunkBytes: 4096 })[0]
  const beforeStray = await accounting(page, 'basic')
  const stray = await feed(page, 'basic', strayChunk.text)
  const afterStray = await accounting(page, 'basic')
  negative(
    'N3-chunk-other-file-rejected',
    '反例：给另一个 fileId 的块必须被拒绝（file-id-mismatch），且不得落进任何接收缓冲',
    stray.result?.ok === false && stray.result.code === 'file-id-mismatch' && afterStray.bufferedBytes === beforeStray.bufferedBytes,
    `code=${stray.result?.code ?? '(竟然通过)'} bufferedBytes ${beforeStray.bufferedBytes}→${afterStray.bufferedBytes}`
  )

  const tampered = Buffer.from(MAIN_BYTES)
  tampered[Math.floor(tampered.length / 2)] ^= 0xff
  negative(
    'N4-content-compare-falsifiable',
    '反例：把源字节改一位后，内容比对判据必须为 false（证明 G03 的字节比对不是恒真）',
    readBytes.length > 0 && contentMatches(readBytes, tampered).ok === false && contentMatches(readBytes, MAIN_BYTES).ok === true,
    `改一位 → ${contentMatches(readBytes, tampered).detail}`
  )

  // ================= G06：超限拒绝而不是缓冲 =================
  console.log('\n阶段 3：超限与越界（拒绝而不是缓冲）\n')
  const beforeOversized = await accounting(page, 'basic')
  const oversizedBegin = await feed(
    page,
    'basic',
    rawMessageText({
      v: 1,
      type: 'file-begin',
      sessionId: SESSION,
      batchId: corruptBatch,
      fileId: 'file-oversized',
      name: 'too-big.bin',
      byteLength: 20 * 1024 * 1024 + 1,
      mime: 'application/octet-stream'
    })
  )
  const oversizedState = await accounting(page, 'basic')

  await evaluate(page, 'globalThis.__D12__.make("limited", { limits: { maxBatchBytes: 16384, maxStagingBytesPerTarget: 8192 } })')
  assertOk('context(limited)', await feed(page, 'limited', contextText()))
  const overBatch = await feed(page, 'limited', batchBeginText({ batchId: 'batch-over', totalBytes: 16385 }))
  const limitedState = await batchState(page, 'limited')
  const limitedChunks = chunkPlan(CORRUPT_BYTES.subarray(0, 2 * 8192), { batchId: 'batch-small', fileId: 'file-small', chunkBytes: 8192 })
  assertOk('batch-begin(small)', await feed(page, 'limited', batchBeginText({ batchId: 'batch-small', totalBytes: 2 * 8192 })))
  assertOk(
    'file-begin(small)',
    await feed(page, 'limited', fileBeginText({ batchId: 'batch-small', fileId: 'file-small', name: 'small.txt', mime: 'text/plain', byteLength: 2 * 8192 }))
  )
  const stagedFirst = await feed(page, 'limited', limitedChunks[0].text)
  const stagedOver = await feed(page, 'limited', limitedChunks[1].text)
  const limitedAccounting = await accounting(page, 'limited')

  const overrunChunk = chunkPlan(CORRUPT_BYTES, { batchId: corruptBatch, fileId: corruptFile, chunkBytes: 8192 })[1]
  const overrun = await feed(
    page,
    'basic',
    messageText({ ...JSON.parse(overrunChunk.text), seq: 2, offset: 2 * 8192, byteLength: 8192 })
  )
  observations.limits = {
    oversizedBegin: oversizedBegin.result,
    oversizedState,
    overBatch: overBatch.result,
    limitedState,
    stagedFirst: stagedFirst.result,
    stagedOver: stagedOver.result,
    limitedAccounting,
    overrun: overrun.result
  }
  gate(
    'G06-limits-refused-not-buffered',
    '四类超限都在写入缓冲前被拒绝：codec 字段上限（20 MiB+1）、生效批次限额（16 KiB）、目标暂存上限（8 KiB）、累计越界',
    oversizedBegin.result?.ok === false &&
      oversizedBegin.result.code === 'integer-out-of-range' &&
      oversizedState.bufferedBytes === beforeOversized.bufferedBytes &&
      oversizedState.filesConstructed === beforeOversized.filesConstructed &&
      overBatch.result?.ok === false &&
      overBatch.result.code === 'limit-batch-bytes' &&
      limitedState.activeBatchId === null &&
      stagedFirst.result?.ok === true &&
      stagedOver.result?.ok === false &&
      stagedOver.result.code === 'limit-staging-bytes' &&
      limitedAccounting.bufferedBytes === 8192 &&
      overrun.result?.ok === false &&
      overrun.result.code === 'size-mismatch',
    `oversized=${oversizedBegin.result?.code}@${oversizedBegin.result?.bufferedBytes}B overBatch=${overBatch.result?.code} active=${limitedState.activeBatchId} 暂存 ${stagedFirst.result?.bufferedBytes}→${stagedOver.result?.code}@${limitedAccounting.bufferedBytes}B overrun=${overrun.result?.code}`
  )
  negative(
    'N5-oversized-not-buffered',
    '反例：超限传输必须被拒绝而不是缓冲（字段上限 20 MiB+1 被拒后接收缓冲与已构造文件数都不得变化）',
    oversizedBegin.result?.ok === false &&
      oversizedBegin.result.code === 'integer-out-of-range' &&
      oversizedState.bufferedBytes === beforeOversized.bufferedBytes &&
      oversizedState.filesConstructed === corruptBefore.filesConstructed,
    `code=${oversizedBegin.result?.code} bufferedBytes ${beforeOversized.bufferedBytes}→${oversizedState.bufferedBytes} filesConstructed=${oversizedState.filesConstructed}`
  )

  // ================= G07：cancel 回收缓冲 =================
  const beforeCancel = await accounting(page, 'basic')
  const cancelAck = await feed(page, 'basic', cancelText(corruptBatch))
  const afterCancel = await accounting(page, 'basic')
  const lateAfterCancel = await feed(page, 'basic', chunkPlan(CORRUPT_BYTES, { batchId: corruptBatch, fileId: corruptFile, chunkBytes: 8192 })[0].text)
  const afterLate = await accounting(page, 'basic')
  observations.accounting.cancel = { before: beforeCancel, afterCancel, afterLate, late: lateAfterCancel.result, cancel: cancelAck.result }
  gate(
    'G07-cancel-reclaims-buffers',
    'cancel 回收缓冲：取消前确有字节占用，取消后页面内记账回到 0（并记录释放字节数），迟到块不得再入缓冲',
    cancelAck.result?.ok === true &&
      beforeCancel.bufferedBytes === CORRUPT_BYTES.length &&
      beforeCancel.bufferedFiles === 1 &&
      afterCancel.bufferedBytes === 0 &&
      afterCancel.bufferedFiles === 0 &&
      afterCancel.releasedBytes >= CORRUPT_BYTES.length &&
      lateAfterCancel.result?.ok === false &&
      lateAfterCancel.result.code === 'cancelled' &&
      afterLate.bufferedBytes === 0,
    `取消前=${beforeCancel.bufferedBytes}B/${beforeCancel.bufferedFiles}文件 取消后=${afterCancel.bufferedBytes}B released=${afterCancel.releasedBytes} 迟到=${lateAfterCancel.result?.code} 最终=${afterLate.bufferedBytes}B`
  )
  negative(
    'N6-cancel-nonvacuous',
    '反例/非空洞性：取消前必须真的有 ≥16 KiB 在缓冲里（否则"取消后归零"是空判据），取消后必须为 0',
    beforeCancel.bufferedBytes > 0 && afterCancel.bufferedBytes === 0 && afterCancel.peakBufferedBytes > 0,
    `before=${beforeCancel.bufferedBytes} after=${afterCancel.bufferedBytes} peak=${afterCancel.peakBufferedBytes}`
  )

  // ================= G08：同会话新批次（不重发 context） =================
  console.log('\n阶段 4：同会话新批次与重放幂等\n')
  await evaluate(page, 'globalThis.__D12__.make("fresh")')
  assertOk('context(fresh)', await feed(page, 'fresh', contextText()))
  const freshFirst = 'batch-fresh-1'
  const freshFirstFile = 'file-fresh-1'
  assertOk('batch-begin(fresh-1)', await feed(page, 'fresh', batchBeginText({ batchId: freshFirst, totalBytes: SMALL_BYTES.length })))
  assertOk(
    'file-begin(fresh-1)',
    await feed(
      page,
      'fresh',
      fileBeginText({ batchId: freshFirst, fileId: freshFirstFile, name: 'd12-fresh-1.txt', mime: 'text/plain', byteLength: SMALL_BYTES.length, sha: SMALL_SHA })
    )
  )
  for (const entry of chunkPlan(SMALL_BYTES, { batchId: freshFirst, fileId: freshFirstFile, chunkBytes: 4096 })) {
    assertOk('fresh-1 chunk', await feed(page, 'fresh', entry.text))
  }
  const freshFirstEnd = await feed(page, 'fresh', fileEndText({ batchId: freshFirst, fileId: freshFirstFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  assertOk('file-end(fresh-1)', freshFirstEnd)
  const freshFirstIds = freshFirstEnd.result.import?.added ?? []
  const freshIdentityBefore = (await batchState(page, 'fresh')).identity
  const freshClose = await feed(page, 'fresh', batchEndText(freshFirst, [{ fileId: freshFirstFile, status: 'staged', attachmentIds: freshFirstIds }]))
  assertOk('batch-end(fresh-1)', freshClose)
  const freshBefore = await accounting(page, 'fresh')

  // 关键：**不重发 context**，新 batchId 直接开新批。
  const freshSecond = 'batch-fresh-2'
  const freshSecondFile = 'file-fresh-2'
  const freshBegin2 = await feed(page, 'fresh', batchBeginText({ batchId: freshSecond, totalBytes: SMALL_BYTES.length }))
  const freshFileBegin2 = await feed(
    page,
    'fresh',
    fileBeginText({ batchId: freshSecond, fileId: freshSecondFile, name: 'd12-fresh-2.txt', mime: 'text/plain', byteLength: SMALL_BYTES.length, sha: SMALL_SHA })
  )
  for (const entry of chunkPlan(SMALL_BYTES, { batchId: freshSecond, fileId: freshSecondFile, chunkBytes: 4096 })) {
    assertOk('fresh-2 chunk', await feed(page, 'fresh', entry.text))
  }
  const freshSecondEnd = await feed(page, 'fresh', fileEndText({ batchId: freshSecond, fileId: freshSecondFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  const freshAfter = await accounting(page, 'fresh')
  const freshIdentityAfter = (await batchState(page, 'fresh')).identity
  const freshRead = await readBack(page, 'fresh')
  const freshBytes = freshRead.ok === true ? Buffer.from(freshRead.base64, 'base64') : Buffer.alloc(0)
  const freshContent = freshRead.ok === true ? contentMatches(freshBytes, SMALL_BYTES) : { ok: false, detail: freshRead.reason }
  const reuseClosed = await feed(page, 'fresh', batchBeginText({ batchId: freshFirst, totalBytes: 0 }))
  const lateToClosed = await feed(page, 'fresh', fileEndText({ batchId: freshFirst, fileId: freshFirstFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  observations.newBatch = {
    identityBefore: freshIdentityBefore,
    identityAfter: freshIdentityAfter,
    accountingBefore: freshBefore,
    accountingAfter: freshAfter,
    secondBegin: freshBegin2.result,
    secondFileBegin: freshFileBegin2.result,
    secondEnd: freshSecondEnd.result,
    readBackBytes: freshBytes.length,
    readBackSha256: freshBytes.length > 0 ? sha256(freshBytes) : null,
    content: freshContent,
    reuseClosed: reuseClosed.result,
    lateToClosed: lateToClosed.result
  }
  gate(
    'G08-new-batch-without-context',
    '同会话新批次：不重发 context，新 batchId 被 batch-begin 承认并完整导入（身份未被替换）；复用已关闭 batchId 被拒，迟到 file-end 不得重建操作',
    freshBegin2.result?.ok === true &&
      freshFileBegin2.result?.ok === true &&
      freshSecondEnd.result?.ok === true &&
      freshSecondEnd.result.importInvoked === true &&
      freshSecondEnd.result.import?.ok === true &&
      freshContent.ok === true &&
      freshAfter.importsInvoked === freshBefore.importsInvoked + 1 &&
      freshIdentityAfter?.sessionId === SESSION &&
      freshIdentityAfter?.composerEpoch === freshIdentityBefore?.composerEpoch &&
      freshIdentityAfter?.expired === false &&
      reuseClosed.result?.ok === false &&
      reuseClosed.result.code === 'duplicate-operation' &&
      lateToClosed.result?.ok === false &&
      lateToClosed.result.code === 'batch-closed',
    `新批=${freshBegin2.result?.ok} 文件=${freshFileBegin2.result?.ok} 完成=${freshSecondEnd.result?.ok} 导入 ${freshBefore.importsInvoked}→${freshAfter.importsInvoked} 回读=${freshBytes.length}B ${freshContent.detail} 复用旧批=${reuseClosed.result?.code} 迟到=${lateToClosed.result?.code} 身份 epoch=${freshIdentityBefore?.composerEpoch}→${freshIdentityAfter?.composerEpoch} expired=${freshIdentityAfter?.expired}`
  )

  // ================= G12：cancel 之后同会话必须能重开新批次（R15 回归） =================
  // 旧症状：cancelled 批次 closed 仍为 false，接收端与状态机都据此挡住新批次，
  // batch-begin 被永久拒为 batch-in-progress——用户取消一次就再也贴不进来。
  await evaluate(page, 'globalThis.__D12__.make("reopen")')
  assertOk('context(reopen)', await feed(page, 'reopen', contextText()))
  const reopenCancelled = 'batch-reopen-cancel'
  assertOk(
    'batch-begin(reopen-cancel)',
    await feed(page, 'reopen', batchBeginText({ batchId: reopenCancelled, totalBytes: SMALL_BYTES.length }))
  )
  assertOk('cancel(reopen-cancel)', await feed(page, 'reopen', cancelText(reopenCancelled)))

  // 新 batchId 必须被承认（不重发 context）。
  const reopenFresh = 'batch-reopen-fresh'
  const reopenFile = 'file-reopen-fresh'
  const reopenBegin = await feed(page, 'reopen', batchBeginText({ batchId: reopenFresh, totalBytes: SMALL_BYTES.length }))
  const reopenFileBegin = await feed(
    page,
    'reopen',
    fileBeginText({
      batchId: reopenFresh,
      fileId: reopenFile,
      name: 'd12-reopen.txt',
      mime: 'text/plain',
      byteLength: SMALL_BYTES.length,
      sha: SMALL_SHA
    })
  )
  for (const entry of chunkPlan(SMALL_BYTES, { batchId: reopenFresh, fileId: reopenFile, chunkBytes: 4096 })) {
    assertOk('reopen chunk', await feed(page, 'reopen', entry.text))
  }
  const reopenEnd = await feed(page, 'reopen', fileEndText({ batchId: reopenFresh, fileId: reopenFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  const reopenRead = await readBack(page, 'reopen')
  const reopenBytes = reopenRead.ok === true ? Buffer.from(reopenRead.base64, 'base64') : Buffer.alloc(0)
  const reopenContent = reopenRead.ok === true ? contentMatches(reopenBytes, SMALL_BYTES) : { ok: false, detail: reopenRead.reason }
  // 复用被取消的 batchId：必须是确定拒绝，且不得重建操作。
  const reopenReuse = await feed(page, 'reopen', batchBeginText({ batchId: reopenCancelled, totalBytes: SMALL_BYTES.length }))
  observations.cancelReopen = {
    cancelledBatch: reopenCancelled,
    newBegin: reopenBegin.result,
    newFileBegin: reopenFileBegin.result,
    newEnd: reopenEnd.result,
    reuseCancelled: reopenReuse.result,
    readBackBytes: reopenBytes.length,
    content: reopenContent
  }
  gate(
    'G12-cancel-then-new-batch',
    'cancel 之后同会话能开新批次：新 batchId 被承认、完成导入且字节一致；复用被取消的 batchId 被确定拒绝',
    reopenBegin.result?.ok === true &&
      reopenBegin.result.code !== 'batch-in-progress' &&
      reopenFileBegin.result?.ok === true &&
      reopenEnd.result?.ok === true &&
      reopenEnd.result.importInvoked === true &&
      reopenContent.ok === true &&
      reopenReuse.result?.ok === false,
    `新批=${reopenBegin.result?.ok}/${reopenBegin.result?.code ?? ''} 文件=${reopenFileBegin.result?.ok}/${reopenFileBegin.result?.code ?? ''} 完成=${reopenEnd.result?.ok} 回读=${reopenBytes.length}B ${reopenContent.detail} 复用被取消批=${reopenReuse.result?.ok === false ? reopenReuse.result.code : '竟然成功'}`
  )

  // 关掉第二批，之后才能在同会话里开第三个批次（重放幂等用例）。
  const freshSecondIds = freshSecondEnd.result.import?.added ?? []
  assertOk(
    'batch-end(fresh-2)',
    await feed(page, 'fresh', batchEndText(freshSecond, [{ fileId: freshSecondFile, status: 'staged', attachmentIds: freshSecondIds }]))
  )

  // 重放幂等（用 fresh 的第三个批次）。
  const replayBatch = 'batch-replay'
  const replayFile = 'file-replay'
  assertOk('batch-begin(replay)', await feed(page, 'fresh', batchBeginText({ batchId: replayBatch, totalBytes: SMALL_BYTES.length })))
  assertOk(
    'file-begin(replay)',
    await feed(page, 'fresh', fileBeginText({ batchId: replayBatch, fileId: replayFile, name: 'd12-replay.txt', mime: 'text/plain', byteLength: SMALL_BYTES.length, sha: SMALL_SHA }))
  )
  const replayChunks = chunkPlan(SMALL_BYTES, { batchId: replayBatch, fileId: replayFile, chunkBytes: 4096 })
  for (const entry of replayChunks) assertOk('replay chunk', await feed(page, 'fresh', entry.text))
  const replayBeforeChunk = await accounting(page, 'fresh')
  // 传输结束之前的重复块：D10 语义是 seq-overlap（回退），绝不重新入缓冲。
  const repeatChunk = await feed(page, 'fresh', replayChunks[0].text)
  const replayAfterChunk = await accounting(page, 'fresh')
  assertOk('replay file-end', await feed(page, 'fresh', fileEndText({ batchId: replayBatch, fileId: replayFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA })))
  const replayBefore = await accounting(page, 'fresh')
  const repeatEnd = await feed(page, 'fresh', fileEndText({ batchId: replayBatch, fileId: replayFile, totalBytes: SMALL_BYTES.length, sha: SMALL_SHA }))
  // 文件已结束之后的迟到块：duplicate-operation，同样不得重新入缓冲。
  const repeatChunkAfterEnd = await feed(page, 'fresh', replayChunks[0].text)
  const replayAfter = await accounting(page, 'fresh')
  observations.replay = {
    repeatChunk: repeatChunk.result,
    repeatChunkAfterEnd: repeatChunkAfterEnd.result,
    repeatEnd: repeatEnd.result,
    beforeChunk: replayBeforeChunk,
    afterChunk: replayAfterChunk,
    before: replayBefore,
    after: replayAfter
  }
  gate(
    'G10-replay-idempotent',
    '重放幂等：传输中的重复块是 seq-overlap、结束后的迟到块是 duplicate-operation（都不重新入缓冲），重复 file-end 是幂等重放且不产生第二个 File、不第二次导入',
    repeatChunk.result?.ok === false &&
      repeatChunk.result.code === 'seq-overlap' &&
      replayAfterChunk.bufferedBytes === replayBeforeChunk.bufferedBytes &&
      repeatChunkAfterEnd.result?.ok === false &&
      repeatChunkAfterEnd.result.code === 'duplicate-operation' &&
      repeatEnd.result?.ok === true &&
      repeatEnd.result.duplicate === true &&
      repeatEnd.result.importInvoked === false &&
      replayAfter.filesConstructed === replayBefore.filesConstructed &&
      replayAfter.importsInvoked === replayBefore.importsInvoked &&
      replayAfter.bufferedBytes === 0,
    `重复块=${repeatChunk.result?.code} 结束后迟到块=${repeatChunkAfterEnd.result?.code} 重复 file-end duplicate=${repeatEnd.result?.duplicate} importInvoked=${repeatEnd.result?.importInvoked} filesConstructed ${replayBefore.filesConstructed}→${replayAfter.filesConstructed} importsInvoked ${replayBefore.importsInvoked}→${replayAfter.importsInvoked} buffered=${replayAfter.bufferedBytes}`
  )

  // ================= G09：强制关掉 WebCrypto =================
  console.log('\n阶段 5：强制无 WebCrypto 的组装与校验\n')
  const forced = JSON.parse(await evaluate(page, 'globalThis.__D12__.forceNoSubtle()'))
  await evaluate(page, 'globalThis.__D12__.make("nosubtle")')
  const noSubtleAccounting = await accounting(page, 'nosubtle')
  assertOk('context(nosubtle)', await feed(page, 'nosubtle', contextText()))
  const nsBatch = 'batch-nosubtle'
  const nsFile = 'file-nosubtle'
  assertOk(
    'batch-begin(nosubtle)',
    await feed(page, 'nosubtle', batchBeginText({ batchId: nsBatch, fileCount: 2, totalBytes: 2 * NO_SUBTLE_BYTES.length }))
  )
  assertOk(
    'file-begin(nosubtle)',
    await feed(
      page,
      'nosubtle',
      fileBeginText({ batchId: nsBatch, fileId: nsFile, name: 'd12-receiver-nosubtle.txt', mime: 'text/plain', byteLength: NO_SUBTLE_BYTES.length, sha: NO_SUBTLE_SHA })
    )
  )
  for (const entry of chunkPlan(NO_SUBTLE_BYTES, { batchId: nsBatch, fileId: nsFile, chunkBytes: 32 * 1024 })) {
    assertOk('nosubtle chunk', await feed(page, 'nosubtle', entry.text))
  }
  const nsEnd = await feed(page, 'nosubtle', fileEndText({ batchId: nsBatch, fileId: nsFile, totalBytes: NO_SUBTLE_BYTES.length, sha: NO_SUBTLE_SHA }))
  const nsRead = await readBack(page, 'nosubtle')
  const nsBytes = nsRead.ok === true ? Buffer.from(nsRead.base64, 'base64') : Buffer.alloc(0)
  const nsContent = nsRead.ok === true ? contentMatches(nsBytes, NO_SUBTLE_BYTES) : { ok: false, detail: nsRead.reason }
  // 同一个文件第二次 file-end 是 duplicate-file-end，因此坏哈希必须换一个文件来证明"校验仍生效"。
  const nsBadFile = 'file-nosubtle-bad'
  assertOk(
    'file-begin(nosubtle bad)',
    await feed(
      page,
      'nosubtle',
      fileBeginText({ batchId: nsBatch, fileId: nsBadFile, name: 'd12-receiver-nosubtle-bad.txt', mime: 'text/plain', byteLength: NO_SUBTLE_BYTES.length })
    )
  )
  for (const entry of chunkPlan(NO_SUBTLE_BYTES, { batchId: nsBatch, fileId: nsBadFile, chunkBytes: 32 * 1024 })) {
    assertOk('nosubtle bad chunk', await feed(page, 'nosubtle', entry.text))
  }
  const nsBadEnd = await feed(page, 'nosubtle', fileEndText({ batchId: nsBatch, fileId: nsBadFile, totalBytes: NO_SUBTLE_BYTES.length, sha: '0'.repeat(64) }))
  observations.noSubtle = {
    forced,
    accounting: noSubtleAccounting,
    end: nsEnd.result,
    readBackBytes: nsBytes.length,
    readBackSha256: nsBytes.length > 0 ? sha256(nsBytes) : null,
    content: nsContent,
    badHash: nsBadEnd.result
  }
  gate(
    'G09-forced-no-webcrypto',
    '强制 crypto.subtle 不可用后：接收端仍报 pure-js 后端，分块组装与 SHA-256 校验照常成功，页面读回字节与源一致',
    forced.after === 'undefined' &&
      noSubtleAccounting.hashBackend === 'pure-js' &&
      nsEnd.result?.ok === true &&
      nsEnd.result.importInvoked === true &&
      nsEnd.result.import?.ok === true &&
      nsContent.ok === true &&
      nsBytes.length === NO_SUBTLE_BYTES.length,
    `subtle ${forced.before}→${forced.after}（${forced.forced}） hashBackend=${noSubtleAccounting.hashBackend} 组装=${nsEnd.result?.ok} 回读=${nsBytes.length}B ${nsContent.detail}`
  )
  negative(
    'N7-no-subtle-bad-hash-still-rejected',
    '反例：关掉 WebCrypto 之后坏哈希仍必须被拒绝（证明 HTTP 路径没有"跳过校验"分支）',
    nsBadEnd.result?.ok === false && nsBadEnd.result.code === 'hash-mismatch',
    `code=${nsBadEnd.result?.code ?? '(竟然通过)'} 同接收端正确哈希已成功（ok=${nsEnd.result?.ok}）`
  )

  // ================= G11：无残留状态 =================
  const finalAccounting = await accounting(page, 'basic')
  observations.accounting.final = finalAccounting
  gate(
    'G11-accounting-bounded',
    '全程结束后：无任何接收缓冲残留（bufferedBytes=0），峰值不超过目标暂存上限，保留批次有界',
    finalAccounting.bufferedBytes === 0 &&
      finalAccounting.bufferedFiles === 0 &&
      finalAccounting.peakBufferedBytes <= finalAccounting.stagingByteLimit &&
      finalAccounting.peakBufferedBytes === MAIN_BYTES.length &&
      finalAccounting.retainedBatches <= 16 &&
      finalAccounting.filesConstructed === 1,
    `bufferedBytes=${finalAccounting.bufferedBytes} peak=${finalAccounting.peakBufferedBytes}≤${finalAccounting.stagingByteLimit} retainedBatches=${finalAccounting.retainedBatches} constructed=${finalAccounting.filesConstructed} importsInvoked=${finalAccounting.importsInvoked} released=${finalAccounting.releasedBytes}`
  )

  await writeFile(join(evidenceDir, 'd12-page-errors.json'), JSON.stringify(pageErrors, null, 2))
  await page.close()
  openPages.splice(openPages.indexOf(page), 1)
  await chrome.close()
  chrome = null
} catch (error) {
  fatal = error
  gate('G00-runtime', '判据脚本自身跑完（无致命异常）', false, String(error?.stack ?? error).slice(0, 400))
} finally {
  for (const page of openPages) await page.close().catch(() => {})
  if (chrome !== null) await chrome.close().catch(() => {})
  if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid')).catch(() => {})
}

// ---------- 汇总、写证据、清理 ----------

const failed = gates.filter((item) => !item.ok)
const report = {
  schemaVersion: 1,
  task: 'D12',
  purpose: 'browser-receiver',
  platform: 'linux',
  method:
    '真实夹具（私有 DSH_HOME + 真实 web profile + 真实 Chromium，LAN 普通 HTTP 非安全上下文）；消息由生产 codec 构造，接收端是已构建插件的页面全局，File 经生产桥进入原生草稿；字节回读在页面内完成，比较在 Node 用 node:crypto 独立完成',
  gates,
  failedGateIds: failed.map((item) => item.id),
  result: failed.length === 0 ? 'pass' : 'fail',
  negativeProbes,
  observations
}
if (fatal !== null) report.fatal = String(fatal?.stack ?? fatal).slice(0, 2000)

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd12-receiver-gates.json'), redact(JSON.stringify(report, null, 2)) + '\n')
await writeFile(join(durableEvidenceDir, 'd12-receiver-gates.json'), redact(JSON.stringify(report, null, 2)) + '\n')

console.log(`\nD12 gates: ${gates.length - failed.length}/${gates.length} 通过；负向探针 ${negativeProbes.filter((item) => item.ok).length}/${negativeProbes.length}`)
if (failed.length > 0) {
  console.error('未通过判据：')
  for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
}

// 清理：只停夹具服务并删除夹具目录；证据已落盘，随后把夹具侧副本补回。
const down = spawnSync('bash', [join(here, 'down.sh')], { cwd: repoRoot, encoding: 'utf8' })
observations.cleanup.downSh = { status: down.status, tail: String(down.stdout ?? '').split('\n').slice(-3).join(' | ') }
const leftovers = spawnSync('bash', ['-lc', "pgrep -af 'dsh-attachments-fixture|dsh-attachments-headless|dsh-cdp-' || true"], { encoding: 'utf8' })
observations.cleanup.leftoverProcesses = String(leftovers.stdout ?? '')
  .trim()
  .split('\n')
  .filter((line) => line.length > 0 && line.includes('pgrep') === false)
console.log(`清理：down.sh exit=${down.status}；夹具残留进程=${observations.cleanup.leftoverProcesses.length}`)

const finalReport = redact(JSON.stringify(report, null, 2)) + '\n'
await mkdir(evidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd12-receiver-gates.json'), finalReport)
await writeFile(join(durableEvidenceDir, 'd12-receiver-gates.json'), finalReport)

if (failed.length > 0) process.exitCode = 1
