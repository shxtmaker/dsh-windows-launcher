#!/usr/bin/env node
/**
 * D18 生产互通驱动（Node 侧）。
 *
 * 目标：把**真实 C# Core**（`AttachmentTransferCoordinator` + `AttachmentCodec`）产出的
 * 线协议帧经 stdio 收下，原样送进**真实 Chromium 页面里的生产接收端**
 * （`__DSH_ATTACHMENTS_RECEIVER__`，D12 已构建产物），再把接收端产出的
 * `ack` / `import-result` 帧原样写回同一个 C# 进程。
 *
 * 边界与职责（"只替代 WebView2 的物理消息边界，绝不替代发送算法"）：
 *   - 发送算法在 C# 侧：分块、背压窗口、序号、SHA-256、批次状态机、去重缓存全部由生产
 *     `AttachmentTransferCoordinator` 决定；本驱动**只做搬运**，不生成、不改写、不补发任何
 *     业务帧（唯一的例外是显式开启的负向探针 `--probe wrong-batch-id`，它由真实
 *     import-result 克隆出**故意错误**的 batchId，用来证明 C# 会拒绝它）。
 *   - 接收端是生产代码：页面全局 `__DSH_ATTACHMENTS_RECEIVER__`（`create()` → D12 接收端），
 *     它自己组装 `File` 并经生产草稿路径 `__DSH_ATTACHMENTS_BRIDGE__.importFiles`
 *     （D08 草稿适配器）导入，产出 `ack` / `import-result`。
 *   - 本驱动不实现第二套线协议编解码：出站帧只做**校验**（用已构建插件的生产 codec
 *     `decodeMessage` 复核合法性并记账），文本原样透传。
 *
 * stdio 协议（NDJSON，一行一条记录）：
 *   C# → 驱动（stdin）：每条一行，就是生产 C# codec 编码出的报文 JSON 文本本身。
 *  驱动 → C#（stdout）：
 *   {"kind":"page", ...}              页面就绪且生产接收端可用的握手记录（恰好一条，最先发出）
 *   {"kind":"frame","frame":{...}}    生产接收端产出的线协议帧（ack / import-result / cancel）
 *   {"kind":"reject", ...}            生产接收端**拒绝**了一条入站帧（stage/code/detail）
 *   {"kind":"note","note":"..."}      诊断（不参与判据）
 *   {"kind":"done", ...}              退出前最后一条（含 trace 路径与 exitReason）
 * 人类可读日志一律走 stderr，stdout 只承载上述记录，避免污染协议流。
 *
 * 双向 trace：`--trace <path>` 指定的 JSONL 文件按发生顺序逐行落盘（appendFileSync，
 * 进程被 SIGKILL 也保留已写部分），每条含 dir（c2d=C#→驱动 / d2c=驱动→C#）、kind、
 * 报文类型、batchId/fileId、载荷摘要等，供 C# 侧独立复核哈希与 ID。
 *
 * 清理：正常退出（stdin EOF）时关闭页面与 Chromium、写出 done 记录；被强杀时由 C# 侧
 * 负责回收，并断言无残留（见 Core.Tests 的互通用例）。
 */

import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createInterface } from 'node:readline'

import { createClient } from '../fixtures/lib.mjs'
import { launchChrome, openPage } from '../fixtures/cdp.mjs'
import { decodeMessage } from '../../lib/shared/wire/index.js'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = process.env.DSH_HOME ?? join(fixtureRoot, 'dsh-home')
const fixtureProfile = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const headlessProfile = process.env.DSH_ATTACH_HEADLESS_PROFILE ?? 'dsh-attachments-headless'
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

/** 人类可读日志：只走 stderr。 */
function log(message) {
  process.stderr.write(`[d18-interop] ${message}\n`)
}

// ---------- 参数 ----------

function parseArguments(argv) {
  const options = {
    loopbackBase: 'http://127.0.0.1:3099',
    lanBase: null,
    trace: null,
    probe: 'none',
    handshakeOnly: false,
    readyTimeoutMs: 120_000
  }
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index]
    const next = () => {
      index += 1
      if (index >= argv.length) throw new Error(`参数 ${token} 缺少取值`)
      return argv[index]
    }
    switch (token) {
      case '--loopback-base':
        options.loopbackBase = next().replace(/\/$/, '')
        break
      case '--lan-base':
        options.lanBase = next().replace(/\/$/, '')
        break
      case '--trace':
        options.trace = next()
        break
      case '--probe':
        options.probe = next()
        break
      case '--handshake-only':
        options.handshakeOnly = true
        break
      case '--ready-timeout-ms':
        options.readyTimeoutMs = Number(next())
        break
      default:
        throw new Error(`未知参数：${token}`)
    }
  }
  if (options.lanBase === null) throw new Error('必须提供 --lan-base（夹具的非 loopback 访问地址）')
  if (options.trace === null) throw new Error('必须提供 --trace（双向消息 trace 落盘路径）')
  if (!['none', 'wrong-batch-id'].includes(options.probe)) {
    throw new Error(`未知探针：${options.probe}（只支持 none / wrong-batch-id）`)
  }
  return options
}

// ---------- 协议输出与 trace ----------

let traceEntries = 0

function writeRecord(record) {
  process.stdout.write(`${JSON.stringify(record)}\n`)
}

class Trace {
  #path

  constructor(path) {
    this.#path = path
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, '')
  }

  get path() {
    return this.#path
  }

  get count() {
    return traceEntries
  }

  /** 追加一条 trace（同步落盘：进程被强杀也保留已发生的事实）。 */
  append(entry) {
    const line = JSON.stringify({ seq: traceEntries, at: Date.now(), ...entry })
    traceEntries += 1
    appendFileSync(this.#path, `${line}\n`)
    return entry
  }
}

/** 载荷摘要：只记摘要，不在 trace 里重复整段 Base64（chunk 原文另有 raw 字段）。 */
const payloadDigest = (base64) => sha256(Buffer.from(base64, 'base64'))

// ---------- 页面侧装置（只做转发与回读，不做任何协议判定） ----------

const INSTALL_HARNESS = `(() => {
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
  if (host === undefined || host === null) return JSON.stringify({ ok: false, reason: 'no-receiver-host' })
  if (bridge === undefined || bridge === null) return JSON.stringify({ ok: false, reason: 'no-bridge' })
  const state = { receiver: null, files: {} }
  globalThis.__D18__ = state

  const shapeResult = (result) => ({
    ok: result.ok === true,
    stage: result.stage === undefined ? null : result.stage,
    code: result.code === undefined ? null : result.code,
    detail: result.detail === undefined ? null : result.detail,
    path: result.path === undefined ? null : result.path,
    type: result.type === undefined ? null : result.type,
    batchId: result.batchId === undefined ? null : result.batchId,
    fileId: result.fileId === undefined ? null : result.fileId,
    duplicate: result.duplicate === true,
    importInvoked: result.importInvoked === true,
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
      applied: result.import.applied === true
    },
    record: result.file === null || result.file === undefined ? null : {
      transport: result.file.transport,
      draft: result.file.draft,
      upload: result.file.upload,
      ended: result.file.ended,
      receivedBytes: result.file.receivedBytes,
      attachmentIds: Array.from(result.file.attachmentIds)
    }
  })

  state.create = (options) => {
    state.receiver = host.create(options === undefined ? {} : options)
    return JSON.stringify({
      ok: true,
      version: host.version,
      wiring: host.wiring,
      hashBackend: state.receiver.accounting.hashBackend,
      limits: {
        maxStagingBytesPerTarget: state.receiver.accounting.stagingByteLimit,
        maxConcurrentAssemblies: state.receiver.accounting.maxConcurrentAssemblies,
        replayCapacity: null
      }
    })
  }

  /** 送一条入站帧并取走接收端产出的全部出站帧（原文与整形视图都给）。 */
  state.feed = async (text) => {
    if (state.receiver === null) return JSON.stringify({ ok: false, reason: 'no-receiver' })
    let result
    try {
      result = await state.receiver.acceptText(text)
    } catch (error) {
      return JSON.stringify({ ok: false, reason: 'accept-threw:' + String(error && error.message ? error.message : error) })
    }
    let outgoing = []
    try {
      outgoing = await state.receiver.drainOutgoing()
    } catch (error) {
      return JSON.stringify({ ok: false, reason: 'drain-threw:' + String(error && error.message ? error.message : error) })
    }
    if (result.ok === true && result.assembled !== null && result.assembled !== undefined) {
      state.files[result.assembled.fileId] = result.assembled.file
    }
    return JSON.stringify({
      ok: true,
      result: shapeResult(result),
      wire: outgoing.map((message) => JSON.parse(JSON.stringify(message))),
      accounting: {
        bufferedBytes: state.receiver.accounting.bufferedBytes,
        bufferedFiles: state.receiver.accounting.bufferedFiles,
        peakBufferedBytes: state.receiver.accounting.peakBufferedBytes,
        filesConstructed: state.receiver.accounting.filesConstructed,
        importsInvoked: state.receiver.accounting.importsInvoked,
        importsSucceeded: state.receiver.accounting.importsSucceeded,
        importsFailed: state.receiver.accounting.importsFailed,
        rejectedMessages: state.receiver.accounting.rejectedMessages,
        retainedBatches: state.receiver.accounting.retainedBatches,
        hashBackend: state.receiver.accounting.hashBackend,
        disposed: state.receiver.accounting.disposed
      }
    })
  }

  /** 在页面里读回组装出的真实 File 的字节（Node 侧独立算哈希判定）。 */
  state.readBack = async (fileId) => {
    const file = state.files[fileId]
    if (file === undefined) return JSON.stringify({ ok: false, reason: 'no-file:' + fileId })
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
      base64: btoa(binary)
    })
  }

  /** 用生产桥探一次原生草稿：previous = 本次探针之前的原生附件 ID。 */
  state.draftProbe = (label) => {
    const bytes = new TextEncoder().encode('d18-interop-probe-' + label + '-' + Date.now())
    const file = new File([bytes], 'd18-interop-probe.txt', { type: 'text/plain' })
    try {
      const result = bridge.importFiles({ files: [file] })
      return JSON.stringify({
        label,
        ok: result.ok === true,
        code: result.ok === true ? null : result.code,
        added: result.ok === true ? Array.from(result.added) : [],
        previous: Array.from(result.previous)
      })
    } catch (error) {
      return JSON.stringify({ label, ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  state.globals = () => JSON.stringify({
    isSecureContext: globalThis.isSecureContext === true,
    subtle: typeof globalThis.crypto?.subtle,
    bridge: typeof bridge,
    bridgeVersion: bridge?.version ?? null,
    currentSession: bridge?.currentSession?.() ?? null,
    receiverHost: typeof host,
    receiverVersion: host.version,
    wiring: host.wiring
  })

  return JSON.stringify({ ok: true, globals: JSON.parse(state.globals()) })
})()`

// ---------- 会话、配对与页面 ----------

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
      rpcId: `d18-${method.replace(/\W/g, '-')}-${Date.now()}`,
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

const evaluate = (page, expression) => page.evaluate(expression, { awaitPromise: true })

async function feed(page, text) {
  return JSON.parse(await evaluate(page, `globalThis.__D18__.feed(${JSON.stringify(text)})`))
}

async function readBack(page, fileId) {
  return JSON.parse(await evaluate(page, `globalThis.__D18__.readBack(${JSON.stringify(fileId)})`))
}

async function draftProbe(page, label) {
  return JSON.parse(await evaluate(page, `globalThis.__D18__.draftProbe(${JSON.stringify(label)})`))
}

// =====================================================================
// 主流程
// =====================================================================

const options = parseArguments(process.argv.slice(2))
const trace = new Trace(options.trace)
let chrome = null
let page = null
let exitReason = 'unknown'
let fatal = null
let pageSessionId = null
let lastAccounting = null
let framesForwarded = 0

/** 出站帧：先按生产 codec 复核合法性，再原样透传给 C# 并记 trace。 */
function emitFrame(frame, origin) {
  const recheck = decodeMessage(JSON.stringify(frame))
  const summary = {
    dir: 'd2c',
    kind: 'frame',
    origin,
    type: typeof frame?.type === 'string' ? frame.type : null,
    batchId: typeof frame?.batchId === 'string' ? frame.batchId : null,
    fileId: typeof frame?.fileId === 'string' ? frame.fileId : null,
    seq: typeof frame?.seq === 'number' ? frame.seq : null,
    offset: typeof frame?.offset === 'number' ? frame.offset : null,
    byteLength: typeof frame?.byteLength === 'number' ? frame.byteLength : null,
    status: typeof frame?.status === 'string' ? frame.status : null,
    code: typeof frame?.code === 'string' ? frame.code : null,
    attachmentIds: Array.isArray(frame?.attachmentIds) ? [...frame.attachmentIds] : null,
    codecOk: recheck.ok === true,
    codecCode: recheck.ok === true ? null : recheck.code
  }
  trace.append({ ...summary, frame })
  writeRecord({ kind: 'frame', frame })
  return summary
}

async function handleInboundFrame(page, text) {
  let parsed
  try {
    parsed = JSON.parse(text)
  } catch (error) {
    throw new Error(`C# 侧发来的不是 JSON 行：${String(error?.message ?? error)}`)
  }
  const type = typeof parsed?.type === 'string' ? parsed.type : null
  const inbound = {
    dir: 'c2d',
    kind: 'frame',
    type,
    batchId: typeof parsed?.batchId === 'string' ? parsed.batchId : null,
    fileId: typeof parsed?.fileId === 'string' ? parsed.fileId : null,
    seq: typeof parsed?.seq === 'number' ? parsed.seq : null,
    offset: typeof parsed?.offset === 'number' ? parsed.offset : null,
    byteLength: typeof parsed?.byteLength === 'number' ? parsed.byteLength : null,
    sha256: typeof parsed?.sha256 === 'string' ? parsed.sha256 : null,
    payloadSha256: typeof parsed?.dataBase64 === 'string' ? payloadDigest(parsed.dataBase64) : null,
    frame: parsed
  }

  const fed = await feed(page, text)
  if (fed.ok !== true) throw new Error(`页面装置拒绝转发：${JSON.stringify(fed)}`)
  framesForwarded += 1
  lastAccounting = fed.accounting
  trace.append({ ...inbound, accepted: fed.result.ok === true, code: fed.result.code ?? null })

  if (fed.result.ok !== true) {
    // 生产接收端拒绝了一条入站帧：这是判据（不是异常），如实回报 C# 侧。
    trace.append({
      dir: 'd2c',
      kind: 'reject',
      type,
      batchId: inbound.batchId,
      fileId: inbound.fileId,
      stage: fed.result.stage ?? null,
      code: fed.result.code ?? null,
      detail: fed.result.detail ?? null,
      payloadSha256: inbound.payloadSha256
    })
    writeRecord({
      kind: 'reject',
      type,
      batchId: inbound.batchId,
      fileId: inbound.fileId,
      stage: fed.result.stage ?? null,
      code: fed.result.code ?? null,
      detail: fed.result.detail ?? null,
      payloadSha256: inbound.payloadSha256
    })
  }

  // 生产接收端产出的出站帧逐条回给 C#。
  for (const frame of fed.wire) {
    if (options.probe === 'wrong-batch-id' && frame.type === 'import-result' && typeof frame.batchId === 'string') {
      // 负向探针：克隆真实 import-result，把 batchId 换成合法但不属于本批次的值，
      // 并把 attachmentIds 换成可识别的假 ID（这样"是否被接受"是可观测的，而不是靠推断）。
      // 期望 C# 生产协调器以 batch-not-open 拒绝它，且假 ID 不出现在任何文件记录里。
      const wrong = { ...frame, batchId: `${frame.batchId}.probe-wrong`, attachmentIds: ['d18-probe-wrong-id'] }
      emitFrame(wrong, 'probe-wrong-batch-id')
    }
    emitFrame(frame, 'receiver')
  }

  // 组装出的真实 File：在页面里读回字节，由 Node 独立算 SHA-256 写进 trace。
  if (fed.result.assembled !== null && fed.result.assembled !== undefined) {
    const back = await readBack(page, fed.result.assembled.fileId)
    if (back.ok !== true) throw new Error(`读回组装 File 失败：${JSON.stringify(back)}`)
    const bytes = Buffer.from(back.base64, 'base64')
    trace.append({
      dir: 'd2c',
      kind: 'assembled',
      type: 'file',
      batchId: inbound.batchId,
      fileId: fed.result.assembled.fileId,
      name: back.name,
      mime: back.type,
      byteLength: back.size,
      isFile: back.isFile === true,
      isBlob: back.isBlob === true,
      sha256: sha256(bytes),
      // 接收端状态机在 file-end 里核对过的声明哈希（来自 C# 生产发送端）。
      declaredSha256: fed.result.assembled.sha256 ?? null,
      importStatus: fed.result.import?.status ?? null,
      attachmentIds: fed.result.import?.ids ?? [],
      draftAdded: fed.result.import?.added ?? [],
      draftPrevious: fed.result.import?.previous ?? [],
      receiverAccounting: fed.accounting
    })
  }

  // 批次关闭：用生产桥探一次原生草稿，记下"本批之后"的原生 attachmentIds 数量。
  if (type === 'batch-end') {
    const probe = await draftProbe(page, `after-batch-${inbound.batchId}`)
    trace.append({ dir: 'd2c', kind: 'draft', type: 'batch-end', batchId: inbound.batchId, probe })
  }

  return fed
}

async function main() {
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error(`缺少夹具 lan-address.txt：${join(fixtureRoot, 'lan-address.txt')}；请先运行 tests/interop/ensure-fixture.sh`)
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  const lanBase = options.lanBase ?? `http://${lanAddress}:3099`
  trace.append({ dir: 'note', kind: 'start', lanBase, loopbackBase: options.loopbackBase, probe: options.probe, dshHome })

  // 1) 真实会话（共享 DSH_HOME 内的真实 Harness 落盘），与 D12/D13 判据同款。
  const seed = await seedSession('d18 production interop seed')
  trace.append({ dir: 'note', kind: 'seed-session', exitCode: seed.code, output: seed.output })

  // 2) 用带 cookie jar 的客户端完成真实配对（loopback 签发 → LAN 接受）。
  const client = createClient()
  const issue = await fetch(`${options.loopbackBase}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  if (issue.ok !== true) throw new Error(`配对签发失败：HTTP ${issue.status}`)
  const token = (await issue.json()).token
  const accepted = await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair') ?? ''
  if (deviceId === '') throw new Error(`配对未落下 dsh_pair cookie（final=${accepted.finalUrl} status=${accepted.response?.status}）`)
  const list = await rpc(client, lanBase, deviceId, 'session/list', { _request: {} })
  const items = list.body?.result?.value?.items ?? []
  const sessionId = items.map((item) => item.sessionId).find((id) => typeof id === 'string') ?? null
  if (sessionId === null) throw new Error('共享 DSH_HOME 里没有可用会话（headless 落盘失败）')
  trace.append({ dir: 'note', kind: 'paired', deviceIdPresent: true, sessionCount: items.length, sessionId })

  // 3) 真实 Chromium + 真实夹具页面（LAN 普通 HTTP 非安全上下文）。
  chrome = await launchChrome({})
  page = await openPage(chrome.debugPort)
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
    { timeoutMs: options.readyTimeoutMs, intervalMs: 1000 }
  )
  if (ready.ok !== true) throw new Error(`真实页面未在超时内就绪：${JSON.stringify(ready.value)}`)

  const install = JSON.parse(await evaluate(page, INSTALL_HARNESS))
  if (install.ok !== true) throw new Error(`页面装置安装失败：${JSON.stringify(install)}`)
  const create = JSON.parse(await evaluate(page, 'globalThis.__D18__.create({})'))
  const globals = JSON.parse(await evaluate(page, 'globalThis.__D18__.globals()'))
  pageSessionId = globals.currentSession

  const initialProbe = await draftProbe(page, 'initial')
  trace.append({ dir: 'note', kind: 'page-ready', sessionId: pageSessionId, globals, create, initialProbe, pageErrors })
  writeRecord({
    kind: 'page',
    sessionId: pageSessionId,
    receiverVersion: globals.receiverVersion,
    receiverHost: globals.receiverHost,
    bridgeVersion: globals.bridgeVersion,
    wiring: globals.wiring,
    isSecureContext: globals.isSecureContext,
    subtle: globals.subtle,
    hashBackend: create.hashBackend ?? null,
    initialDraftCount: initialProbe.previous?.length ?? null
  })
  log(`页面就绪：session=${pageSessionId} receiver=v${globals.receiverVersion} 初始草稿=${initialProbe.previous?.length ?? 'n/a'}`)

  if (options.handshakeOnly) {
    exitReason = 'handshake-only'
    return
  }

  // 4) 逐帧转发：C# 生产协调器 → 真实接收端 → 回帧给同一 C#。
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
  let frames = 0
  for await (const line of lines) {
    const text = line.trim()
    if (text === '') continue
    await handleInboundFrame(page, text)
    frames += 1
  }
  exitReason = 'stdin-eof'
  trace.append({ dir: 'note', kind: 'eof', frames, pageErrors })

  const finalProbe = await draftProbe(page, 'final')
  trace.append({ dir: 'note', kind: 'draft-final', probe: finalProbe })
}

// ---------- 入口：无论成功失败都清理页面/Chromium 并落 trace ----------

let exitCode = 0
try {
  await main()
} catch (error) {
  fatal = String(error?.stack ?? error)
  exitReason = 'fatal'
  exitCode = 78
  log(`致命错误：${String(error?.stack ?? error)}`)
  trace.append({ dir: 'note', kind: 'fatal', error: String(error?.message ?? error) })
} finally {
  try {
    if (page !== null) await page.close().catch(() => {})
    if (chrome !== null) await chrome.close().catch(() => {})
  } catch (error) {
    log(`清理页面/Chromium 失败：${String(error?.message ?? error)}`)
  }
  trace.append({
    dir: 'note',
    kind: 'done',
    exitReason,
    exitCode,
    framesForwarded,
    traceEntries: trace.count,
    tracePath: trace.path,
    pageSessionId,
    lastAccounting,
    fatal
  })
  writeRecord({
    kind: 'done',
    exitReason,
    exitCode,
    trace: trace.path,
    traceEntries: trace.count,
    framesForwarded,
    sessionId: pageSessionId,
    error: fatal
  })
}

process.exit(exitCode)
