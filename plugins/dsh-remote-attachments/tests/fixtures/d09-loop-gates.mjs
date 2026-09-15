#!/usr/bin/env node
/**
 * D09 最小端到端闭环判据（Linux，真实夹具）。
 *
 * 目标：证明薄插件能把合成 `File` 经**生产适配器**放进原生草稿，经真实 Harness 发送，
 * 再由真实 `read` 工具把内容读回，四层证据（draft / network / receipt / tool bytes）一致。
 *
 * 全部判据都跑在产品路径上，**不安装任何垫片**：
 *   合成 File → `__DSH_ATTACHMENTS_BRIDGE__.importFiles` → 生产 draft-adapter 用 DataTransfer
 *   写原生 file input 并只触发一次 change → 原生 `attachmentIds` → composer 主按钮
 *   （真实用户点击路径）→ `session/prompt` → 真实 Harness agent 回合 → provider stub
 *   下发真实 `read` 工具调用 → 真实工具读回字节。
 *
 * 三轮发送，每轮一个独立 stub 实例（manifest 只有一个步骤），避免"步骤期望无法满足时
 * stub 反复下发同一工具调用"：
 *   第 1 轮 PNG（图片不上传，直接内联进 prompt）；
 *   第 2 轮 文本附件（intake 时后台上传 → receipt → 发送 → 同轮 read 读回）；
 *   第 3 轮 删掉 `dsh_pair` cookie 后只用 `x-dsh-remote-device` 设备头再来一次。
 *
 * 可证伪性：negativeProbes 每条都是"故意改错/故意制造受限状态就必须失败"的反例，
 * 包括用 CDP Fetch 域把上传请求挂起，验证 uploadsPending 确实阻塞发送、放行后恢复。
 *
 * 只用 tests/fixtures 既有夹具：lib.mjs（进程/cookie 客户端）、cdp.mjs（真实 Chromium）、
 * setup.sh 准备的私有 DSH_HOME。不触碰 $HOME/.dsh，不杀用户自己的 Chrome。
 */

import { spawn, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdir, readdir, readFile, writeFile, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { deflateSync } from 'node:zlib'

import { createClient, redact, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const workDir = join(fixtureRoot, 'd09')
const port = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const stubPort = Number(process.env.DSH_ATTACH_STUB_PORT ?? 3901)
const fixtureProfile = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const headlessProfile = process.env.DSH_ATTACH_HEADLESS_PROFILE ?? 'dsh-attachments-headless'
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')
// 夹具 profile 的 llm-deepseek 指向本地 stub，凭据是占位值（见 setup.sh）。
process.env.DEEPSEEK_API_KEY ??= 'stub-key'

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
const sha256 = (buffer) => createHash('sha256').update(buffer).digest('hex')

// ---------- 证据收集 ----------

const gates = []
const negativeProbes = []

function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 负向探针：ok=true 表示"反例确实失败了"，即被测判据不是恒真。 */
function negative(id, description, ok, detail = '') {
  negativeProbes.push({ id, description, ok, detail })
  console.log(`  [neg ${ok ? 'OK' : 'BAD'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

const observations = {
  shim: 'none（本脚本不安装任何垫片；全部判据走产品路径）',
  sessions: {},
  stubSummaries: {},
  providerRequests: {},
  networkUploads: [],
  images: {},
  text: {},
  carrier: {},
  turns: {},
  cleanup: {}
}

// ---------- 素材 ----------

const TEXT_MARKER = 'DSH-D09-TEXT-MARKER-7f3a91c4'
const COOKIEFREE_MARKER = 'DSH-D09-COOKIEFREE-MARKER-2b8e55d1'
const BLOCKED_MARKER = 'DSH-D09-BLOCKED-MARKER-5e2c7a90'
const NON_ASCII_LINE = '第二行：中文内容与 emoji 🧪'
const TEXT_BODY = `${TEXT_MARKER}\n${NON_ASCII_LINE}\n第三行：tail\n`
const COOKIEFREE_BODY = `${COOKIEFREE_MARKER}\n${NON_ASCII_LINE}\n第三行：tail\n`
const BLOCKED_BODY = `${BLOCKED_MARKER}\n${NON_ASCII_LINE}\n`
const STAGED_PATH_PATTERN = 'saved at "([^"\\n]*/attachments/v1/files/[^"\\n]*)"'

/** 页面侧：发送按钮的稳定句柄只有 aria-label（css module 类名是哈希）。 */
const SEND_LABELS = ['发送消息', 'Send message', '排队发送', 'Queue message', '插话发送', 'Steer message']
const STOP_LABELS = ['停止生成', 'Stop generating']

/** 生成一张最小合法 PNG（2x2，RGB）。 */
function makePng() {
  const width = 2
  const height = 2
  const raw = Buffer.alloc((width * 3 + 1) * height)
  for (let y = 0; y < height; y++) {
    const rowStart = y * (width * 3 + 1)
    for (let x = 0; x < width; x++) {
      const at = rowStart + 1 + x * 3
      raw[at] = 0x33
      raw[at + 1] = 0x99
      raw[at + 2] = 0xff
    }
  }
  const crcTable = []
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    crcTable[n] = c >>> 0
  }
  const chunk = (type, data) => {
    const length = Buffer.alloc(4)
    length.writeUInt32BE(data.length, 0)
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
    let crc = 0xffffffff
    for (const byte of body) crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8)
    const crcBuffer = Buffer.alloc(4)
    crcBuffer.writeUInt32BE((crc ^ 0xffffffff) >>> 0, 0)
    return Buffer.concat([length, body, crcBuffer])
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(width, 0)
  ihdr.writeUInt32BE(height, 4)
  ihdr[8] = 8
  ihdr[9] = 2
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0))
  ])
}

const PNG_BYTES = makePng()

/** 页面侧：读 composer 卡片状态（发送按钮、附件 chip、阶段、桥与承载形状）。 */
const READ_CARD = `(() => {
  const card = document.querySelector('[data-composer-card]')
  if (card === null) return JSON.stringify({ card: false })
  const buttons = Array.from(card.querySelectorAll('button')).map((button) => ({
    aria: button.getAttribute('aria-label') ?? '',
    type: button.type,
    disabled: button.disabled
  }))
  const send = buttons.find((button) => ${JSON.stringify(SEND_LABELS)}.includes(button.aria)) ?? null
  const stop = buttons.find((button) => ${JSON.stringify(STOP_LABELS)}.includes(button.aria)) ?? null
  return JSON.stringify({
    card: true,
    send,
    stop,
    retry: Array.from(card.querySelectorAll('[aria-label^="重试上传"], [aria-label^="Retry"]')).map((node) => node.getAttribute('aria-label')),
    chips: Array.from(card.querySelectorAll('[data-composer-card] [role="group"] [aria-label]')).map((node) => node.getAttribute('aria-label')),
    inputPhase: document.querySelector('[data-composer-input]')?.getAttribute('data-phase') ?? null,
    rootPhase: document.querySelector('[data-composer-seat]')?.closest('[data-phase]')?.getAttribute('data-phase') ?? null,
    stored: (() => { try { return JSON.parse(localStorage.getItem('dsh.sessions.current') ?? 'null') } catch { return null } })(),
    bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
    bridgeVersion: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.version ?? null,
    currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
    hook: typeof globalThis.__DSH_FILE_UPLOAD__,
    hookFetch: typeof globalThis.__DSH_FILE_UPLOAD__?.fetch
  })
})()`

/** 承载契约判定（锁定 harness 的 FileUploadRuntime 读的是 hook.fetch）。 */
function carrierContractOk(state) {
  return state?.hook === 'object' && state?.hookFetch === 'function'
}

/** 页面侧：用生产桥导入合成 File（base64 或文本）。 */
function importProbe(sessionId, specs) {
  const sessionJson = sessionId === null ? 'null' : JSON.stringify(sessionId)
  return `(() => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    const specs = ${JSON.stringify(specs)}
    const files = specs.map((spec) => {
      const bytes = spec.base64
        ? Uint8Array.from(atob(spec.base64), (c) => c.charCodeAt(0))
        : new TextEncoder().encode(spec.text ?? '')
      return new File([bytes], spec.name, { type: spec.type })
    })
    const request = ${sessionJson} === null ? { files } : { sessionId: ${sessionJson}, files }
    try {
      return JSON.stringify({ result: bridge.importFiles(request) })
    } catch (error) {
      return JSON.stringify({ threw: String(error && error.message ? error.message : error) })
    }
  })()`
}

/** 页面侧：点击 composer 主按钮（真实用户路径：onPrimary → keyboard.submit）。 */
const CLICK_SEND = `(() => {
  const card = document.querySelector('[data-composer-card]')
  if (card === null) return 'no-card'
  const send = Array.from(card.querySelectorAll('button'))
    .find((button) => ${JSON.stringify(SEND_LABELS)}.includes(button.getAttribute('aria-label') ?? ''))
  if (send === undefined) return 'no-send-button'
  if (send.disabled) return 'disabled'
  send.click()
  return 'clicked'
})()`

// ---------- 网络证据 ----------

const net = { phase: 'image', requests: [], uploads: [], paused: [] }
const isUploadUrl = (url) => url.includes('uploadFileBinary')

/** 只有"非 loopback + 改写后的 /remote/api 上传 URL"才算走对路由。 */
function isRemoteUploadUrl(url, lanBase) {
  return url.startsWith(`${lanBase}/remote/api/session/uploadFileBinary`)
}

function attachNetworkProbe(page) {
  page.onEvent((message) => {
    if (message.method === 'Fetch.requestPaused') {
      net.paused.push({ phase: net.phase, requestId: message.params.requestId, url: message.params.request.url })
      return
    }
    if (message.method === 'Network.requestWillBeSent') {
      const request = message.params.request
      const record = {
        phase: net.phase,
        kind: 'request',
        id: message.params.requestId,
        url: request.url,
        method: request.method,
        headers: request.headers ?? {},
        postDataLength: (request.postData ?? '').length,
        hasPostData: request.hasPostData === true
      }
      net.requests.push(record)
      if (isUploadUrl(request.url)) net.uploads.push(record)
      return
    }
    if (message.method === 'Network.requestWillBeSentExtraInfo') {
      const record = net.requests.find((entry) => entry.id === message.params.requestId)
      if (record !== undefined) record.extraHeaders = message.params.headers ?? {}
      return
    }
    if (message.method === 'Network.loadingFinished') {
      const record = net.requests.find((entry) => entry.id === message.params.requestId)
      if (record !== undefined) record.encodedDataLength = message.params.encodedDataLength
      return
    }
    if (message.method === 'Network.responseReceived') {
      const response = message.params.response
      const record = net.requests.find((entry) => entry.id === message.params.requestId)
      if (record !== undefined) record.status = response.status
      if (isUploadUrl(response.url)) {
        // 上传响应体就是 receipt：直接在网络层读回来，不依赖页面内部状态。
        void page
          .send('Network.getResponseBody', { requestId: message.params.requestId })
          .then((body) => {
            const upload = net.uploads.find((entry) => entry.id === message.params.requestId)
            if (upload !== undefined) {
              upload.responseStatus = response.status
              upload.responseBody = body.base64Encoded ? Buffer.from(body.body, 'base64').toString('utf8') : body.body
            }
          })
          .catch(() => {})
      }
    }
  })
}

/** 取某个阶段的上传请求；phase 为空时取全部。 */
function uploadEntries(phase) {
  return net.uploads.filter((entry) => (phase === undefined ? true : entry.phase === phase))
}

function uploadHeaders(entry) {
  return { ...(entry?.headers ?? {}), ...(entry?.extraHeaders ?? {}) }
}

function parseReceipt(entry) {
  try {
    return entry?.responseBody === undefined ? null : JSON.parse(entry.responseBody)
  } catch {
    return null
  }
}

// ---------- 服务与 stub 控制 ----------

async function startStub(manifest, recordDir) {
  await rm(recordDir, { recursive: true, force: true })
  await mkdir(recordDir, { recursive: true })
  const manifestPath = join(recordDir, 'manifest.json')
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n')
  const child = spawn(
    process.execPath,
    [join(here, 'provider/provider-stub.mjs'), '--port', String(stubPort), '--manifest', manifestPath, '--record', recordDir],
    { cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'] }
  )
  let output = ''
  child.stdout.on('data', (chunk) => { output += chunk })
  child.stderr.on('data', (chunk) => { output += chunk })
  const deadline = Date.now() + 20_000
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`provider stub 提前退出（${child.exitCode}）：${output}`)
    if (existsSync(join(recordDir, 'stub-ready.json'))) return { child, recordDir, manifestPath }
    await sleep(200)
  }
  throw new Error(`provider stub 未就绪：${output}`)
}

async function stopStub(stub) {
  if (stub === null || stub.child.exitCode !== null) return
  stub.child.kill('SIGTERM')
  const deadline = Date.now() + 5_000
  while (stub.child.exitCode === null && Date.now() < deadline) await sleep(200)
  if (stub.child.exitCode === null) stub.child.kill('SIGKILL')
}

async function stubSummary() {
  const response = await fetch(`http://127.0.0.1:${stubPort}/__stub/summary`)
  return response.json()
}

/** 轮询直到 accept 成立；返回最后一次读到的值。 */
async function poll(read, accept, { timeoutMs, intervalMs = 500 }) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await read()
    if (accept(value)) return { ok: true, value }
    if (Date.now() >= deadline) return { ok: false, value }
    await sleep(intervalMs)
  }
}

function seedSession(task) {
  return new Promise((resolvePromise) => {
    const child = spawn(dshBin, ['--profile', headlessProfile, task], {
      cwd: repoRoot,
      env: { ...process.env, DSH_HOME: dshHome, DEEPSEEK_API_KEY: 'stub-key' },
      stdio: ['ignore', 'pipe', 'pipe']
    })
    let output = ''
    child.stdout.on('data', (chunk) => { output += chunk })
    child.stderr.on('data', (chunk) => { output += chunk })
    child.on('exit', (code) => resolvePromise({ code, output: output.slice(-200) }))
  })
}

async function rpc(client, lanBase, deviceId, method, args) {
  const response = await client.fetch(`${lanBase}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({ type: 'client-request', rpcId: `d09-${method.replace(/\W/g, '-')}-${Date.now()}`, method, payload: { args } })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

const readCard = (page) => page.evaluate(READ_CARD).then((text) => JSON.parse(text))

/** 打开页面并等到"桥认定的当前会话 == 目标会话"。 */
async function openAppPage(chrome, lanBase, deviceId, sessionId) {
  const page = await openPage(chrome.debugPort)
  attachNetworkProbe(page)
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
  })
  await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId ?? '')}`)
  // 就绪必须等到**桥认定的当前会话**等于目标会话：桥在 boot 期就装好了，而会话选择是
  // 客户端异步状态；过早导入会被适配器正确判为 context-changed（实测踩到过）。
  const ready = await poll(
    async () => readCard(page),
    (state) => state.card === true && state.bridge === 'object' && carrierContractOk(state) && state.currentSession === sessionId,
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  return { page, ready }
}

/** 等一轮 agent 回合结束：主按钮从"停止生成"回到"发送消息"。 */
async function waitTurnEnd(page, timeoutMs = 90_000) {
  return poll(async () => readCard(page), (state) => state.send !== null && state.stop === null, { timeoutMs, intervalMs: 1000 })
}

/** 读取浏览器 cookie 名（cookie-free 证据）。 */
async function cookiesOf(page, url) {
  try {
    const result = await page.send('Network.getCookies', { urls: [url] })
    return (result.cookies ?? []).map((cookie) => cookie.name).sort()
  } catch {
    return []
  }
}

// ---------- 判定函数（判据与负向探针共用，保证反例能真的失败） ----------

/** 工具结果里剥离行号后的正文；与导入字节逐行比对。 */
function reconstructReadText(resultText) {
  const content = /<content>\n([\s\S]*?)\n\(End of file[^)]*\)\n<\/content>/.exec(resultText)
  const body = content === null ? resultText : content[1]
  const lines = []
  for (const line of body.split('\n')) {
    const match = /^\s*(\d+):\s?(.*)$/.exec(line)
    if (match !== null) lines.push(match[2])
  }
  return lines.join('\n') + '\n'
}

/** 只认"工具真的把这份字节读回来了"：剥离行号后的正文必须与导入字节逐字节相同。 */
function containsImportedText(resultText, sourceText) {
  return reconstructReadText(resultText) === sourceText
}

/** receipt 一致性：字节数 + 内容寻址 id 都必须等于导入的那份字节。 */
function receiptConsistent(receipt, sourceBytes) {
  const value = receipt?.value
  if (receipt?.ok !== true || typeof value !== 'object' || value === null) return { ok: false, detail: 'receipt.ok !== true' }
  if (typeof value.receiptId !== 'string' || value.receiptId.length === 0) return { ok: false, detail: 'receiptId 缺失' }
  const file = value.file
  if (typeof file?.bytes !== 'number' || file.bytes !== sourceBytes.length) {
    return { ok: false, detail: `bytes=${file?.bytes} 期望=${sourceBytes.length}` }
  }
  const expected = `sha256:${sha256(sourceBytes)}`
  if (file.attachmentId !== expected) return { ok: false, detail: `attachmentId=${file.attachmentId} 期望=${expected}` }
  return { ok: true, detail: `receiptId=…${String(value.receiptId).slice(-8)} attachmentId=${file.attachmentId} bytes=${file.bytes}` }
}

/** 从请求体里取 image_url 的原始字节。 */
function imagePayloadBytes(requestBody) {
  const found = []
  for (const message of requestBody?.messages ?? []) {
    const content = message?.content
    if (!Array.isArray(content)) continue
    for (const part of content) {
      if (part?.type !== 'image_url') continue
      const url = typeof part.image_url === 'string' ? part.image_url : (part.image_url?.url ?? '')
      const comma = url.indexOf(',')
      if (comma < 0) continue
      found.push({ url, bytes: Buffer.from(url.slice(comma + 1), 'base64') })
    }
  }
  return found
}

/** 从请求体里取附件 handle 文本（provider 侧看到的文本附件形态）。 */
function fileHandleTexts(requestBody) {
  const texts = []
  for (const message of requestBody?.messages ?? []) {
    const content = message?.content
    if (typeof content === 'string') {
      if (content.includes('attachments/v1/files')) texts.push(content)
      continue
    }
    if (!Array.isArray(content)) continue
    for (const part of content) {
      if (part?.type === 'text' && typeof part.text === 'string' && part.text.includes('attachments/v1/files')) texts.push(part.text)
    }
  }
  return texts
}

/** 读 stub 记录里的第 index 个 provider 请求体。 */
async function readStubRequest(recordDir, index = -1) {
  const names = existsSync(recordDir) ? (await readdir(recordDir)).filter((name) => name.startsWith('request-')).sort() : []
  const target = index < 0 ? names.at(index) : names[index]
  if (target === undefined) return null
  return JSON.parse(await readFile(join(recordDir, target), 'utf8'))
}

// =====================================================================
// 主流程
// =====================================================================

let service = null
let stub = null
let chrome = null
const openPages = []
let fatal = null
let deviceId = null
let client = null
let lanBase = null
let sessionA = null
let sessionB = null
const serviceLog = join(evidenceDir, 'service-d09-loop.log')

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await mkdir(workDir, { recursive: true })

try {
  console.log('D09 最小端到端闭环判据（产品路径，无垫片）\n')
  if (existsSync(join(fixtureRoot, 'lan-address.txt')) === false) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  lanBase = `http://${lanAddress}:${port}`

  // ---- 造会话：必须在 stub 启动之前，这样 stub 的 agent 请求计数从 0 开始 ----
  console.log('阶段 0：在共享 DSH_HOME 里造两条真实会话（stub 未启动，传输失败不影响落盘）\n')
  const seedA = await seedSession('d09 loop seed A')
  const seedB = await seedSession('d09 loop seed B')
  observations.sessions.seedExitCodes = [seedA.code, seedB.code]

  // 第 1 轮（PNG）用无工具步骤的 stub：图片不上传，只会得到一句最终文本。
  stub = await startStub({ final: { text: 'D09_PNG_FINAL' }, steps: [] }, join(workDir, 'stub-1-png'))

  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))
  await waitForReady(service.child, serviceLog, 120_000)

  client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  deviceId = client.jar.get('dsh_pair') ?? ''
  const list = await rpc(client, lanBase, deviceId, 'session/list', { _request: {} })
  const items = list.body?.result?.value?.items ?? []
  const sessionIds = items.map((item) => item.sessionId).filter((id) => typeof id === 'string')
  sessionA = sessionIds[0] ?? null
  sessionB = sessionIds[1] ?? null
  observations.sessions.list = { status: list.status, count: sessionIds.length }
  if (sessionA === null) throw new Error('共享 DSH_HOME 里没有可用会话（headless 落盘失败）')
  console.log(`  会话 A=${sessionA}${sessionB === null ? '' : ` B=${sessionB}`}\n`)

  chrome = await launchChrome({})

  // ================= 页面 A（会话 A）：第 1 轮 PNG + 第 2 轮 文本 =================
  console.log('阶段 1：PNG 轮（生产适配器 → 主按钮发送 → provider 图像载荷）\n')
  net.phase = 'image'
  const first = await openAppPage(chrome, lanBase, deviceId, sessionA)
  openPages.push(first.page)
  const pageA = first.page
  observations.carrier.product = {
    hook: first.ready.value.hook,
    hookFetch: first.ready.value.hookFetch,
    inputPhase: first.ready.value.inputPhase,
    rootPhase: first.ready.value.rootPhase
  }
  gate(
    'G00-bridge',
    '版本化桥在真实页面安装、版本号为 1，且桥认定的当前会话就是目标会话（生产适配器入口可用）',
    first.ready.value.bridge === 'object' && first.ready.value.bridgeVersion === 1 && first.ready.value.currentSession === sessionA,
    `bridge=${first.ready.value.bridge} version=${first.ready.value.bridgeVersion} current=${first.ready.value.currentSession} 就绪=${first.ready.ok}`
  )
  gate(
    'G00b-carrier-contract',
    '插件安装的上传承载符合锁定 harness 的消费契约（hook.fetch 是函数），且页面未安装任何垫片',
    carrierContractOk(first.ready.value),
    `hook=${first.ready.value.hook} hookFetch=${first.ready.value.hookFetch}（消费者 dsh-client-file-upload/lib/client.js:161 读 hook.fetch）`
  )
  negative(
    'N6-carrier-shape',
    '反例：修复前的"裸函数"承载形状必须让 G00b 的判定失败（证明该判据可证伪）',
    carrierContractOk({ hook: 'function', hookFetch: 'undefined' }) === false,
    "carrierContractOk({hook:'function',hookFetch:'undefined'}) === false"
  )

  const pngImport = JSON.parse(
    await pageA.evaluate(importProbe(sessionA, [{ name: 'd09-shot.png', type: 'image/png', base64: PNG_BYTES.toString('base64') }]))
  )
  const pngAdded = pngImport.result?.added ?? []
  gate(
    'G01-draft-import',
    '合成 PNG 经生产适配器导入：ok、恰好 1 个新增 id、旧 id 为空',
    pngImport.result?.ok === true && pngAdded.length === 1 && typeof pngAdded[0] === 'string' && pngAdded[0].length > 0 && Array.isArray(pngImport.result?.previous) && pngImport.result.previous.length === 0,
    `ok=${pngImport.result?.ok} added=${JSON.stringify(pngAdded)} previous=${JSON.stringify(pngImport.result?.previous)} code=${pngImport.result?.code ?? pngImport.threw ?? ''}`
  )
  observations.draft = { pngId: pngAdded[0] ?? null }

  const beforeSend = await stubSummary()
  gate(
    'G02-quiet',
    '导入完成后、显式发送之前：provider stub 收到的 agent 请求为 0（导入本身从不发送）',
    beforeSend.agentRequests === 0,
    `agentRequests=${beforeSend.agentRequests} sideChannel=${beforeSend.sideChannelRequests} requests=${beforeSend.requests}`
  )
  observations.stubSummaries.beforeFirstSend = beforeSend

  const readyToSend = await poll(async () => readCard(pageA), (state) => state.send !== null && state.send.disabled === false, { timeoutMs: 20_000 })
  const firstClick = await pageA.evaluate(CLICK_SEND)
  const firstSend = await poll(async () => stubSummary(), (summary) => summary.agentRequests > beforeSend.agentRequests, { timeoutMs: 45_000, intervalMs: 1000 })
  gate(
    'G03-send',
    '点击 composer 主按钮（aria-label=input.send 的 <button type=button>）后，provider stub 收到真实 agent 请求',
    readyToSend.ok && firstClick === 'clicked' && firstSend.ok,
    `发送按钮可用=${readyToSend.ok} click=${firstClick} agentRequests ${beforeSend.agentRequests}→${firstSend.value.agentRequests}`
  )

  const pngRequest = await readStubRequest(join(workDir, 'stub-1-png'))
  const payloads = imagePayloadBytes(pngRequest)
  const pngPayloadHash = payloads.length > 0 ? sha256(payloads[0].bytes) : ''
  const pngSourceHash = sha256(PNG_BYTES)
  observations.images = {
    sourceBytes: PNG_BYTES.length,
    sourceSha256: pngSourceHash,
    payloadBytes: payloads[0]?.bytes.length ?? 0,
    payloadSha256: pngPayloadHash,
    urlPrefix: payloads[0]?.url.slice(0, 24) ?? null,
    normalizationNote: '2x2 合成 PNG 的 request-image 规范化是恒等变换；真实截图被重编码时载荷哈希会与源文件不同'
  }
  gate(
    'G04-image-payload',
    'PNG 在 provider 请求里是 image_url data URL，且解码后的字节与导入的 PNG 逐字节相同（哈希相等）',
    payloads.length === 1 && payloads[0].bytes.equals(PNG_BYTES) && pngPayloadHash === pngSourceHash,
    `payloads=${payloads.length} 源=${PNG_BYTES.length}B/${pngSourceHash.slice(0, 12)} 载荷=${payloads[0]?.bytes.length ?? 0}B/${pngPayloadHash.slice(0, 12)}`
  )
  observations.providerRequests.image = {
    tools: Array.isArray(pngRequest?.tools) ? pngRequest.tools.length : 0,
    imageUrlPrefix: payloads[0]?.url.slice(0, 24) ?? null
  }
  observations.turns.turn1Ended = (await waitTurnEnd(pageA)).ok

  // ---- 第 2 轮：文本附件（intake 上传 → receipt → 发送 → read 读回） ----
  console.log('\n阶段 2：文本附件轮（上传 → receipt → 发送 → 真实 read 读回）\n')
  await stopStub(stub)
  stub = await startStub(
    {
      final: { text: 'D09_TEXT_FINAL' },
      steps: [{
        kind: 'tool-call',
        tool: 'read',
        arguments: { file_path: `{{prompt-last:${STAGED_PATH_PATTERN}}}` },
        expect: { resultContainsAll: [TEXT_MARKER, NON_ASCII_LINE] }
      }]
    },
    join(workDir, 'stub-2-text')
  )
  net.phase = 'text'
  const beforeText = await stubSummary()
  const txtImport = JSON.parse(
    await pageA.evaluate(importProbe(sessionA, [{ name: 'd09-note.txt', type: 'text/plain', text: TEXT_BODY }]))
  )
  const txtAdded = txtImport.result?.added ?? []
  observations.draft.txtId = txtAdded[0] ?? null
  observations.draft.previousAfterTurn1 = Array.isArray(txtImport.result?.previous) ? txtImport.result.previous.length : -1
  // 原生 id 的独立回读：同一批再来一张 PNG，它的 previous 必须逐位等于文本附件的 added。
  const roundTrip = JSON.parse(
    await pageA.evaluate(importProbe(sessionA, [{ name: 'd09-batch.png', type: 'image/png', base64: PNG_BYTES.toString('base64') }]))
  )
  const nativeIds = roundTrip.result?.previous ?? []
  gate(
    'G01b-native-id',
    '适配器返回的新增 id 是真实原生 attachmentIds：下一次导入的 previous 逐位回读到同一值，且上一轮发送已清空草稿',
    roundTrip.result?.ok === true && nativeIds.length === 1 && nativeIds[0] === txtAdded[0] && observations.draft.previousAfterTurn1 === 0,
    `文本 added=${JSON.stringify(txtAdded)} 下一批 previous=${JSON.stringify(nativeIds)} 第 1 轮后残留=${observations.draft.previousAfterTurn1}`
  )

  const uploadReady = await poll(async () => readCard(pageA), (state) => state.retry.length > 0 || (state.send !== null && state.send.disabled === false), { timeoutMs: 30_000 })
  gate(
    'G05-file-upload',
    '文本附件导入后后台上传进入 ready（无"重试上传"态、发送按钮解锁）',
    uploadReady.ok && uploadReady.value.retry.length === 0 && uploadReady.value.send?.disabled === false,
    `retry=${JSON.stringify(uploadReady.value.retry)} 发送按钮 disabled=${uploadReady.value.send?.disabled ?? '(无发送按钮)'}`
  )

  const textUploadPoll = await poll(
    async () => uploadEntries('text').find((entry) => typeof entry.responseBody === 'string') ?? uploadEntries('text')[0] ?? null,
    (entry) => entry !== null && typeof entry.responseBody === 'string',
    { timeoutMs: 15_000 }
  )
  const textUpload = textUploadPoll.value
  const textHeaders = uploadHeaders(textUpload)
  const textReceipt = parseReceipt(textUpload)
  const textReceiptCheck = receiptConsistent(textReceipt, Buffer.from(TEXT_BODY, 'utf8'))
  observations.networkUploads.push({
    phase: 'text',
    url: textUpload?.url ?? null,
    method: textUpload?.method ?? null,
    status: textUpload?.responseStatus ?? textUpload?.status ?? null,
    deviceHeader: textHeaders['x-dsh-remote-device'] ?? null,
    cookieHeader: textHeaders.cookie ?? null,
    hasPostData: textUpload?.hasPostData ?? null,
    transferredBytes: textUpload?.encodedDataLength ?? null,
    receipt: textReceipt
  })
  gate(
    'G06-network-route',
    '上传字节走改写后的 remote 路由 POST <lanBase>/remote/api/session/uploadFileBinary 并带 x-dsh-remote-device',
    textUpload !== null &&
      textUpload.method === 'POST' &&
      isRemoteUploadUrl(textUpload.url, lanBase) &&
      typeof textHeaders['x-dsh-remote-device'] === 'string' &&
      (textUpload.responseStatus ?? textUpload.status) === 200,
    `url=${textUpload?.url ?? '(无)'} method=${textUpload?.method ?? '(无)'} status=${textUpload?.responseStatus ?? '(无)'} 设备头=${textHeaders['x-dsh-remote-device'] === undefined ? '(无)' : '有'} 传输字节=${textUpload?.encodedDataLength ?? '(未知)'}`
  )
  gate(
    'G07-receipt',
    '上传 receipt 存在且与导入字节一致（bytes 相同、attachmentId=sha256(字节)）',
    textReceiptCheck.ok,
    textReceiptCheck.detail
  )
  observations.receipt = { observed: textReceipt, check: textReceiptCheck }

  const textClick = await pageA.evaluate(CLICK_SEND)
  const textRead = await poll(
    async () => stubSummary(),
    (summary) => summary.agentRequests > beforeText.agentRequests && (summary.toolResultTexts ?? []).some((text) => containsImportedText(text, TEXT_BODY)),
    { timeoutMs: 120_000, intervalMs: 1000 }
  )
  const textSummary = textRead.value
  const textReadText = (textSummary.toolResultTexts ?? []).find((text) => containsImportedText(text, TEXT_BODY)) ?? ''
  const textFirstRequest = await readStubRequest(join(workDir, 'stub-2-text'), 0)
  const textHandles = fileHandleTexts(textFirstRequest)
  const textHash = sha256(Buffer.from(TEXT_BODY, 'utf8'))
  observations.text = {
    bodyBytes: Buffer.byteLength(TEXT_BODY, 'utf8'),
    sha256: textHash,
    handleTexts: textHandles.map((text) => text.slice(0, 500)),
    readResultExcerpt: textReadText.slice(0, 300),
    stubStages: textSummary.stages,
    placeholderResolutions: textSummary.placeholderResolutions
  }
  const readBackHash = textReadText === '' ? '' : sha256(Buffer.from(reconstructReadText(textReadText), 'utf8'))
  gate(
    'G08-toolbytes-text',
    '同一轮内真实 read 工具结果剥离行号后与导入文本逐字节一致（含 marker 与非 ASCII 行），且发送确由主按钮触发',
    textClick === 'clicked' &&
      textRead.ok &&
      textReadText !== '' &&
      textHandles.length > 0 &&
      (textSummary.placeholderResolutions ?? []).some((record) => record.resolved === true && String(record.arguments?.file_path ?? '').includes('/attachments/v1/files/')),
    `click=${textClick} agentRequests ${beforeText.agentRequests}→${textSummary.agentRequests} 读回=${textReadText === '' ? '(无)' : `${Buffer.byteLength(reconstructReadText(textReadText), 'utf8')}B/${readBackHash.slice(0, 12)}`} 导入=${Buffer.byteLength(TEXT_BODY, 'utf8')}B/${textHash.slice(0, 12)} handle 文本=${textHandles.length} 段`
  )
  observations.turns.turn2Ended = (await waitTurnEnd(pageA)).ok

  negative(
    'N1-draft-context',
    '反例：用非当前会话 id 导入，桥必须确定失败而不是无脑 ok',
    (await (async () => {
      const bogus = JSON.parse(await pageA.evaluate(importProbe('session-not-current-0000', [{ name: 'n1.txt', type: 'text/plain', text: 'n1' }])))
      observations.negativeDraftImport = bogus.result ?? bogus
      return bogus.result?.ok === false && typeof bogus.result?.code === 'string'
    })()),
    `code=${JSON.stringify(observations.negativeDraftImport?.code ?? null)}`
  )
  negative(
    'N2-empty-draft-no-send',
    '反例：草稿为空时发送按钮是禁用的，点击不产生任何 agent 请求（G03 的点击不是恒真）',
    (await (async () => {
      const state = await readCard(pageA)
      const before = await stubSummary()
      const click = await pageA.evaluate(CLICK_SEND)
      await sleep(5_000)
      const after = await stubSummary()
      observations.negativeEmptySend = { disabled: state.send?.disabled ?? null, click, before: before.agentRequests, after: after.agentRequests }
      return state.send !== null && state.send.disabled === true && click === 'disabled' && after.agentRequests === before.agentRequests
    })()),
    `disabled=${observations.negativeEmptySend?.disabled} click=${observations.negativeEmptySend?.click} agentRequests ${observations.negativeEmptySend?.before}→${observations.negativeEmptySend?.after}`
  )
  negative(
    'N3-receipt-bytes',
    '反例：把 receipt 的 bytes 改成 ±1，一致性判定必须为 false',
    textReceipt !== null && receiptConsistent({ ...textReceipt, value: { ...textReceipt.value, file: { ...textReceipt.value.file, bytes: textReceipt.value.file.bytes + 1 } } }, Buffer.from(TEXT_BODY, 'utf8')).ok === false,
    'receipt.bytes+1 → 判定 false'
  )
  negative(
    'N4-toolbytes-marker',
    '反例：把期望正文改一个字符，读回判定必须为 false',
    textReadText !== '' && containsImportedText(textReadText, TEXT_BODY.replace(TEXT_MARKER, `${TEXT_MARKER}-X`)) === false,
    '期望 marker 改一字 → 判定 false'
  )
  negative(
    'N5-network-url',
    '反例：同源未改写的 /api/session/uploadFileBinary 不算走对 remote 路由',
    isRemoteUploadUrl(`${lanBase}/api/session/uploadFileBinary?sessionId=x`, lanBase) === false,
    `未改写 URL 判定 false（期望前缀 ${lanBase}/remote/api/…）`
  )

  await pageA.close()
  openPages.splice(openPages.indexOf(pageA), 1)

  // ---- 第 3 轮：cookie-free（删掉 dsh_pair，只用设备头）----
  console.log('\n阶段 3：cookie-free 轮（无 dsh_pair cookie，仅 x-dsh-remote-device）\n')
  await stopStub(stub)
  stub = await startStub(
    {
      final: { text: 'D09_COOKIEFREE_FINAL' },
      steps: [{
        kind: 'tool-call',
        tool: 'read',
        arguments: { file_path: `{{prompt-last:${STAGED_PATH_PATTERN}}}` },
        expect: { resultContainsAll: [COOKIEFREE_MARKER, NON_ASCII_LINE] }
      }]
    },
    join(workDir, 'stub-3-cookiefree')
  )
  const second = await openAppPage(chrome, lanBase, deviceId, sessionB ?? sessionA)
  openPages.push(second.page)
  const pageB = second.page
  const cookiesBefore = await cookiesOf(pageB, lanBase)
  await pageB.send('Network.deleteCookies', { name: 'dsh_pair', url: lanBase })
  const cookiesAfter = await cookiesOf(pageB, lanBase)
  observations.cookies = { before: cookiesBefore, after: cookiesAfter }

  net.phase = 'cookie-free'
  const beforeCookieFree = await stubSummary()
  const cookieFreeImport = JSON.parse(
    await pageB.evaluate(importProbe(sessionB ?? sessionA, [{ name: 'd09-cookiefree.txt', type: 'text/plain', text: COOKIEFREE_BODY }]))
  )
  const cookieFreeReady = await poll(async () => readCard(pageB), (state) => state.retry.length > 0 || (state.send !== null && state.send.disabled === false), { timeoutMs: 30_000 })
  const cookieFreePoll = await poll(
    async () => uploadEntries('cookie-free').find((entry) => typeof entry.responseBody === 'string') ?? uploadEntries('cookie-free')[0] ?? null,
    (entry) => entry !== null && typeof entry.responseBody === 'string',
    { timeoutMs: 15_000 }
  )
  const cookieFreeUpload = cookieFreePoll.value
  const cookieFreeHeaders = uploadHeaders(cookieFreeUpload)
  const cookieFreeReceipt = parseReceipt(cookieFreeUpload)
  const cookieFreeReceiptCheck = receiptConsistent(cookieFreeReceipt, Buffer.from(COOKIEFREE_BODY, 'utf8'))
  const cookieHeaderValue = cookieFreeHeaders.cookie
  observations.networkUploads.push({
    phase: 'cookie-free',
    url: cookieFreeUpload?.url ?? null,
    method: cookieFreeUpload?.method ?? null,
    status: cookieFreeUpload?.responseStatus ?? cookieFreeUpload?.status ?? null,
    deviceHeader: cookieFreeHeaders['x-dsh-remote-device'] ?? null,
    cookieHeader: cookieHeaderValue ?? null,
    hasPostData: cookieFreeUpload?.hasPostData ?? null,
    transferredBytes: cookieFreeUpload?.encodedDataLength ?? null,
    receipt: cookieFreeReceipt
  })
  gate(
    'G09-cookie-free-upload',
    '删除 dsh_pair cookie 后，上传仅凭 x-dsh-remote-device 设备头走通 remote 路由并拿到一致 receipt',
    cookieFreeImport.result?.ok === true &&
      cookieFreeUpload !== null &&
      cookieFreeUpload.method === 'POST' &&
      isRemoteUploadUrl(cookieFreeUpload.url, lanBase) &&
      typeof cookieFreeHeaders['x-dsh-remote-device'] === 'string' &&
      (cookieHeaderValue === undefined || String(cookieHeaderValue).includes('dsh_pair') === false) &&
      (cookieFreeUpload.responseStatus ?? cookieFreeUpload.status) === 200 &&
      cookieFreeReceiptCheck.ok,
    `cookie 前=${JSON.stringify(cookiesBefore)} 后=${JSON.stringify(cookiesAfter)} url=${cookieFreeUpload?.url ?? '(无)'} cookie 头=${cookieHeaderValue === undefined ? '(无)' : '(有)'} 设备头=${cookieFreeHeaders['x-dsh-remote-device'] === undefined ? '(无)' : '有'} ${cookieFreeReceiptCheck.detail}`
  )

  const cookieFreeClick = cookieFreeReady.value.send?.disabled === false ? await pageB.evaluate(CLICK_SEND) : 'blocked'
  const cookieFreeRead = await poll(
    async () => stubSummary(),
    (summary) => summary.agentRequests > beforeCookieFree.agentRequests && (summary.toolResultTexts ?? []).some((text) => containsImportedText(text, COOKIEFREE_BODY)),
    { timeoutMs: 120_000, intervalMs: 1000 }
  )
  const cookieFreeSummary = cookieFreeRead.value
  const cookieFreeReadText = (cookieFreeSummary.toolResultTexts ?? []).find((text) => containsImportedText(text, COOKIEFREE_BODY)) ?? ''
  const cookieFreeHash = sha256(Buffer.from(COOKIEFREE_BODY, 'utf8'))
  observations.text.cookieFree = {
    bodyBytes: Buffer.byteLength(COOKIEFREE_BODY, 'utf8'),
    sha256: cookieFreeHash,
    readResultExcerpt: cookieFreeReadText.slice(0, 300),
    stubStages: cookieFreeSummary.stages,
    placeholderResolutions: cookieFreeSummary.placeholderResolutions
  }
  const cookieFreeReadHash = cookieFreeReadText === '' ? '' : sha256(Buffer.from(reconstructReadText(cookieFreeReadText), 'utf8'))
  gate(
    'G10-cookie-free-toolbytes',
    'cookie-free 上传的附件在同一轮里被真实 read 工具逐字节读回（无 dsh_pair cookie 的完整闭环）',
    cookieFreeClick === 'clicked' && cookieFreeRead.ok && cookieFreeReadText !== '',
    `click=${cookieFreeClick} agentRequests ${beforeCookieFree.agentRequests}→${cookieFreeSummary.agentRequests} 读回=${cookieFreeReadText === '' ? '(无)' : `${Buffer.byteLength(reconstructReadText(cookieFreeReadText), 'utf8')}B/${cookieFreeReadHash.slice(0, 12)}`} 导入=${Buffer.byteLength(COOKIEFREE_BODY, 'utf8')}B/${cookieFreeHash.slice(0, 12)}`
  )
  observations.turns.turn3Ended = (await waitTurnEnd(pageB)).ok

  // ---- N7/N8：可控 pending —— 用 CDP Fetch 域把上传请求挂起 ----
  console.log('\n阶段 4：受控阻塞探针（Fetch 域挂起上传请求）\n')
  net.phase = 'blocked'
  const pendingBefore = await stubSummary()
  await pageB.send('Fetch.enable', { patterns: [{ urlPattern: '*uploadFileBinary*', requestStage: 'Request' }] })
  const blockedImport = JSON.parse(
    await pageB.evaluate(importProbe(sessionB ?? sessionA, [{ name: 'd09-blocked.txt', type: 'text/plain', text: BLOCKED_BODY }]))
  )
  const paused = await poll(async () => net.paused.find((entry) => entry.phase === 'blocked') ?? null, (entry) => entry !== null, { timeoutMs: 15_000, intervalMs: 300 })
  await sleep(3_000)
  const blockedCard = await readCard(pageB)
  const blockedClick = await pageB.evaluate(CLICK_SEND)
  await sleep(5_000)
  const pendingAfter = await stubSummary()
  observations.blockedUpload = {
    importOk: blockedImport.result?.ok ?? null,
    pausedUrl: paused.value?.url ?? null,
    sendDisabled: blockedCard.send?.disabled ?? null,
    retry: blockedCard.retry,
    click: blockedClick,
    agentRequests: [pendingBefore.agentRequests, pendingAfter.agentRequests]
  }
  negative(
    'N7-upload-pending-blocks-send',
    '反例：把上传请求挂起（uploadsPending）时发送按钮禁用，点击不产生 agent 请求',
    paused.value !== null && blockedCard.send !== null && blockedCard.send.disabled === true && blockedClick === 'disabled' && pendingAfter.agentRequests === pendingBefore.agentRequests,
    `paused=${paused.value?.url ?? '(未挂起)'} send.disabled=${blockedCard.send?.disabled} click=${blockedClick} agentRequests ${pendingBefore.agentRequests}→${pendingAfter.agentRequests}`
  )
  if (paused.value !== null) await pageB.send('Fetch.continueRequest', { requestId: paused.value.requestId }).catch(() => {})
  await pageB.send('Fetch.disable').catch(() => {})
  const recovered = await poll(async () => readCard(pageB), (state) => state.retry.length === 0 && state.send !== null && state.send.disabled === false, { timeoutMs: 30_000 })
  negative(
    'N8-pending-recovery',
    '反例的恢复步：放行被挂起的上传请求后，附件进入 ready（发送按钮重新解锁）',
    recovered.ok,
    `retry=${JSON.stringify(recovered.value.retry)} send.disabled=${recovered.value.send?.disabled ?? null}`
  )

  await pageB.close()
  openPages.splice(openPages.indexOf(pageB), 1)
} catch (error) {
  fatal = error
  gate('G00-runtime', '判据脚本自身跑完（无致命异常）', false, String(error?.stack ?? error).slice(0, 400))
} finally {
  for (const page of openPages) await page.close().catch(() => {})
  if (chrome !== null) await chrome.close().catch(() => {})
  await stopStub(stub).catch(() => {})
  if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid')).catch(() => {})
}

// ---------- 汇总、写证据、清理 ----------

const failed = gates.filter((item) => !item.ok)
const report = {
  schemaVersion: 1,
  task: 'D09',
  purpose: 'minimal-e2e-loop',
  platform: 'linux',
  method: '真实夹具（私有 DSH_HOME + 真实 web profile + 真实 Chromium）；草稿/网络/receipt/工具字节四层证据各自独立采集；全程产品路径，无垫片',
  gates,
  failedGateIds: failed.map((item) => item.id),
  notRunGateIds: gates.filter((item) => item.detail.startsWith('notRun')).map((item) => item.id),
  result: failed.length === 0 ? 'pass' : 'fail',
  negativeProbes,
  observations
}
if (fatal !== null) report.fatal = String(fatal?.stack ?? fatal).slice(0, 2000)

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd09-loop-gates.json'), redact(JSON.stringify(report, null, 2)) + '\n')
await writeFile(join(durableEvidenceDir, 'd09-loop-gates.json'), redact(JSON.stringify(report, null, 2)) + '\n')

console.log(`\nD09 gates: ${gates.length - failed.length}/${gates.length} 通过；负向探针 ${negativeProbes.filter((item) => item.ok).length}/${negativeProbes.length}`)
if (failed.length > 0) {
  console.error('未通过判据：')
  for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
}

// 清理：仅停夹具服务并删除夹具目录；证据已落盘，随后把夹具侧副本补回。
const down = spawnSync('bash', [join(here, 'down.sh')], { cwd: repoRoot, encoding: 'utf8' })
observations.cleanup.downSh = { status: down.status, tail: String(down.stdout ?? '').split('\n').slice(-3).join(' | ') }
const leftovers = spawnSync('bash', ['-lc', "pgrep -af 'dsh-attachments-fixture|dsh-attachments-headless|provider-stub|dsh-cdp-' || true"], { encoding: 'utf8' })
observations.cleanup.leftoverProcesses = String(leftovers.stdout ?? '')
  .trim()
  .split('\n')
  // pgrep -f 会匹配到本探针自己的命令行；只统计真实残留进程。
  .filter((line) => line.length > 0 && line.includes('pgrep') === false)
console.log(`清理：down.sh exit=${down.status}；夹具残留进程=${observations.cleanup.leftoverProcesses.length}`)

const finalReport = redact(JSON.stringify(report, null, 2)) + '\n'
await mkdir(evidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd09-loop-gates.json'), finalReport)
await writeFile(join(durableEvidenceDir, 'd09-loop-gates.json'), finalReport)

if (failed.length > 0) process.exitCode = 1
