#!/usr/bin/env node
/**
 * D23 兼容、独立停用与更新后状态判据（真实夹具 + 真实 Chromium）。
 *
 * 目标：证明附加插件能被**独立停用**、能**重载/重启**，并且对**全家桶版本变化**给出
 * 诚实的结论——既有可核对的兼容矩阵，也有"不因恢复了一个全局变量就宣称 runtime 恢复"
 * 的硬判据。
 *
 * 判据分组（每条都必须能失败，负向探针见 negativeProbes）：
 *   G01 已验证组合（清单）：固定版本与**实际装出来的**版本逐项一致；组合配置里本插件行恰好一行
 *   G02 已验证组合（页面能力）：available + 无原因码 + 承载为 ours + 上传路由 remote-rewrite
 *   G03 已验证组合（附件通路）：多块 → 原生草稿真实 id → /remote 上传 200 + receipt 哈希一致
 *       → 主机内容寻址库逐字节相同
 *   G04 配对不受影响（停用前基线）：设备库集合不变 + 心跳 200 + /remote 读会话 200 + 停用调用计数为 0
 *   G05 运行时停用：自有全局与承载被撤、状态面 disposed、明确 ReloadRequired、保留引用一律被拒
 *   G06 不得谎报恢复：只恢复全局变量（承载 / 全部四个全局）后仍必须 ReloadRequired 且导入仍被拒
 *   G07 真实页面重载后能力真的恢复（同一判据谓词从 false 翻到 true，且真实批次能导入原生草稿）
 *   G08 Harness 重启后：同一设备仍可用（心跳 + /remote 读会话 + 设备库不变）且附件通路照常
 *   G09 未知既有 hook：unavailable + capability-conflict，未知 hook 对象原样保留（身份/标记/无品牌）
 *   G10 不支持组合（模拟上游契约改名）：unavailable + capability-disabled，承载拒绝且零请求，
 *       但内置路径仍可用（回退到内置行为），配对不受影响
 *   G11 独立停用覆盖面：组合配置差异只落在本插件行；LAN 页 unavailable、loopback 页 degraded；
 *       二者原因码都是 capability-disabled；内置附件行为（真实上传 + 主机库逐字节）照常
 *   G12 remote 降级：unavailable + capability-disabled，承载拒绝且零请求（不回退裸 /api），配对照常
 *   G13 版本策略产物：兼容矩阵与策略文档存在，且矩阵每行与本次实测结论逐字段一致
 *
 * 证据布局（D19/D20/D21/D22 同款，只增不改）：每次运行一个
 * artifacts/verify-portable/d23-runs/<runId>/，顶层 d23-compat-gates.json（两处双写）与
 * d23-compatibility-matrix.json 都是**最新一次**运行的镜像；历史只认 runId 目录，index.json 只追加。
 *
 * 明确不做（见 notRun）：对生产 $HOME/.dsh 或真实 npm 源执行任何更新；把夹具固定版本换成
 * 未经验证的 0.3.21/0.3.22；Windows 实机安装/更新与真实 WebView2 边界。
 *
 * 夹具自有的两处**局部改动**（只改夹具私有的 dsh-home 内已装包，绝不改仓库源码/tarball/生产）：
 *   1. 在已装 @linxin666/dsh-remote-web-ui 的 PairingService.stop() 里插一行调用记录，
 *      用来把"停用 addon 绝不调用 PairingService.stop"变成可直接观察的断言（负向控制会触发它）；
 *   2. 在**专用的一次服务运行**里把 remote 的 seat 全局名整体改名（host+client 两处一致），
 *      模拟"全家桶更新后内部契约改名"的不支持组合；该次运行结束后立即还原。
 */

import { spawn, spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { appendFile, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises'
import { dirname, join, relative, resolve, sep } from 'node:path'
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
const keepFixture = process.env.DSH_ATTACH_KEEP_FIXTURE === '1'
const lockPath = join(pluginRoot, 'tests/fixtures/compatibility-lock.json')
const matrixPath = join(durableEvidenceDir, 'd23-compatibility-matrix.json')
const policyDocPath = join(pluginRoot, 'docs/COMPATIBILITY.md')
const productionHome = join(process.env.HOME ?? '', '.dsh')

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
/**
 * 绝对路径脱敏：证据里绝不出现本机绝对家目录/仓库路径（eng 门禁会按
 * `/(home|Users)/<name>/` 硬检查，D22 同款）。redact() 只处理凭据形态，路径由这里兜住。
 */
function sanitize(text) {
  const home = process.env.HOME ?? ''
  let out = String(text)
  for (const [prefix, token] of [
    [repoRoot, '<REPO>'],
    [dshHome, '<DSH_HOME>'],
    [fixtureRoot, '<FIXTURE>'],
    [pluginRoot, '<PLUGIN>']
  ]) {
    if (prefix !== '') out = out.split(prefix).join(token)
  }
  if (home !== '') out = out.split(home).join('<HOME>')
  return out.replace(/\/(home|Users)\/[A-Za-z0-9._-]+\//g, '/<home>/')
}
const sha256hex = (bytes) => createHash('sha256').update(bytes).digest('hex')
const sha256of = (buffer) => `sha512-${createHash('sha512').update(buffer).digest('base64')}`
const rel = (value) => relative(repoRoot, value).split(sep).join('/')

// ---------- 运行身份 / 证据目录（每次运行独立 runId；历史只增不改） ----------

const RUN_STARTED_AT_MS = Date.now()
const RUN_ID = `d23-${new Date(RUN_STARTED_AT_MS).toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`
const RUN_COMMAND = `node ${process.argv[1] ?? 'tests/fixtures/d23-compat-gates.mjs'}${process.argv.slice(2).length === 0 ? '' : ` ${process.argv.slice(2).join(' ')}`}`
const RUNS_DIR = join(durableEvidenceDir, 'd23-runs')
const RUN_DIR = join(RUNS_DIR, RUN_ID)
const RUN_STDOUT_PATH = join(RUN_DIR, 'stdout.log')

function teeToRunLog(line) {
  try {
    mkdirSync(RUN_DIR, { recursive: true })
    appendFileSync(RUN_STDOUT_PATH, `${line}\n`)
  } catch {
    // tee 失败不能影响判据本身。
  }
}
for (const stream of ['log', 'error']) {
  const original = console[stream].bind(console)
  console[stream] = (...args) => {
    teeToRunLog(args.map((value) => (typeof value === 'string' ? value : String(value))).join(' '))
    original(...args)
  }
}
process.on('exit', (code) => teeToRunLog(`[run] exit code=${String(code)} runId=${RUN_ID}`))

// ---------- 证据收集 ----------

const gates = []
const negativeProbes = []
const notRun = []
const observations = {
  versions: {},
  scope: {},
  verified: {},
  disable: {},
  reload: {},
  restart: {},
  unknownHook: {},
  unsupported: {},
  disabledAddon: {},
  degraded: {},
  negative: {},
  cleanup: {}
}

function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok: ok === true, detail })
  console.log(`  [${ok === true ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 负向探针：ok=true 表示"反例确实失败了"，即被测判据不是恒真。 */
function negative(id, description, ok, detail = '') {
  negativeProbes.push({ id, description, ok: ok === true, detail })
  console.log(`  [neg ${ok === true ? 'OK' : 'BAD'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 未验项：如实登记，绝不当作通过。 */
function notRunItem(id, description, platform, reason) {
  notRun.push({ id, description, platform, reason })
}

// ---------- HTTP / 设备通道 ----------

async function rpc(client, base, deviceId, method, args) {
  const response = await client.fetch(`${base}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...(deviceId === null ? {} : { 'x-dsh-remote-device': deviceId }) },
    body: JSON.stringify({ type: 'client-request', rpcId: `d23-${method.replace(/\W/g, '-')}-${Date.now()}`, method, payload: { args: args ?? {} } })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

const heartbeat = (client, base) =>
  client.fetch(`${base}/api/pair/heartbeat`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' }).then((response) => response.status)

const pairStatus = (client, base) => client.fetch(`${base}/api/pair/status`).then(async (response) => ({ status: response.status, body: await response.json().catch(() => null) }))

/** 生成一次性配对令牌（只从 loopback 铸造）。 */
async function issueToken(basePort) {
  const response = await fetch(`http://127.0.0.1:${basePort}/api/pair/issue`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
  const body = await response.json()
  return body.token
}

/** Node 侧配对（用于探针客户端；与浏览器侧共用同一令牌窗口）。 */
async function pairNode(base, token) {
  const client = createClient()
  await client.follow(`${base}/pair-accept?pair=${token}`)
  return { client, deviceId: client.jar.get('dsh_pair') ?? '' }
}

/** 浏览器侧走真实扫码流：/pair-accept 设 HttpOnly cookie，再从 CDP 读出设备 id。 */
async function pairInChrome(page, base, token) {
  await page.navigate(`${base}/pair-accept?pair=${encodeURIComponent(token)}`)
  const cookies = await page.send('Network.getCookies', { urls: [base] })
  const cookie = (cookies.cookies ?? []).find((entry) => entry.name === 'dsh_pair')
  return cookie?.value ?? ''
}

/** 设备库（服务端持久化文件）里的设备集合指纹：只记数量与集合哈希，不落原始 id。 */
function deviceStoreFingerprint() {
  const path = join(dshHome, 'remote-web-ui-devices.json')
  if (!existsSync(path)) return { exists: false, count: 0, setHash: null }
  try {
    const parsed = JSON.parse(readFileSync(path, 'utf8'))
    const ids = Object.keys(parsed).sort()
    return { exists: true, count: ids.length, setHash: sha256hex(ids.join('\n')).slice(0, 16) }
  } catch (error) {
    return { exists: true, count: -1, setHash: null, error: String(error).slice(0, 80) }
  }
}

// ---------- 主机内容寻址附件库（与服务端实现无关的独立读数） ----------

/** 对象目录：`<DSH_HOME>/attachments/v1/files/<sha 前两位>/<sha>/`，原文件按原名放在目录里。 */
const storeObjectDir = (sha) => join(dshHome, 'attachments/v1/files', sha.slice(0, 2), sha)

/** 读出该哈希对应的对象字节（目录里取第一个普通文件）；不存在返回 null。 */
async function readStoreObject(sha) {
  const dir = storeObjectDir(sha)
  try {
    const entries = await readdir(dir, { withFileTypes: true })
    const file = entries.find((entry) => entry.isFile())
    if (file === undefined) return null
    return await readFile(join(dir, file.name))
  } catch {
    return null
  }
}

function bytesEqual(left, right) {
  if (left === null || left.length !== right.length) return false
  for (let index = 0; index < left.length; index += 1) if (left[index] !== right[index]) return false
  return true
}

async function poll(read, accept, { timeoutMs, intervalMs = 500 } = {}) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await read()
    if (accept(value)) return { ok: true, value }
    if (Date.now() >= deadline) return { ok: false, value }
    await sleep(intervalMs)
  }
}

const waitForStoreObject = (sha, { timeoutMs = 30_000 } = {}) => poll(() => readStoreObject(sha), (value) => value !== null, { timeoutMs, intervalMs: 600 })

// ---------- 生产 profile 未被触碰的守卫 ----------

/**
 * 只对 `$HOME/.dsh/profiles/*` 的**清单层文件**取值（不含 node_modules 海量文件）：
 * 更新/安装会改写这些文件，而普通会话流量不会，因此它既能发现"我们碰了生产"，
 * 也不会因为用户自己正在聊天而误报。
 */
function productionFingerprint() {
  const root = join(productionHome, 'profiles')
  const out = {}
  if (!existsSync(root)) return { root: '<absent>', files: out }
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (entry.isDirectory() === false) continue
    const dir = join(root, entry.name)
    for (const file of readdirSync(dir, { withFileTypes: true })) {
      if (file.isFile() === false) continue
      const path = join(dir, file.name)
      try {
        out[rel(path)] = sha256hex(readFileSync(path)).slice(0, 16)
      } catch {
        out[rel(path)] = '<unreadable>'
      }
    }
  }
  return { root: '<DSH_HOME>/profiles', files: out }
}

// ---------- 夹具已装 remote 包的局部改动（可逆、可核对、绝不入库） ----------

const remotePackageRel = 'node_modules/@linxin666/dsh-remote-web-ui'
const stopMarker = '/* D23-stop-instrumentation */'
const seatV2 = '__DSH_REMOTE_CHANNEL_BOOT_V2__'
const seatV1 = '__DSH_REMOTE_CHANNEL_BOOT__'

const profileDirOf = (profile) => join(dshHome, 'profiles', profile)

function patchCount(text, needle) {
  return text.split(needle).length - 1
}

/**
 * 把夹具私有 dsh-home 里的 remote 包还原到基线（幂等），并返回基线哈希。
 * 这样反复运行本门禁不会把上一次的局部改动当成"上游行为"。
 */
async function ensureRemoteBaseline(profile) {
  const dir = join(profileDirOf(profile), remotePackageRel)
  const hostPath = join(dir, 'lib/index.js')
  const clientPath = join(dir, 'lib/client.js')
  const record = { profile, dir: rel(dir), host: {}, client: {}, baselineSha256: {} }
  for (const path of [hostPath, clientPath]) {
    if (existsSync(path) === false) return { ok: false, detail: `缺少已装 remote 包文件：${rel(path)}` }
    let text = await readFile(path, 'utf8')
    if (text.includes(seatV2)) text = text.split(seatV2).join(seatV1)
    if (text.includes(stopMarker)) {
      const injected = text.split('\n').filter((line) => line.includes(stopMarker) === false).join('\n')
      text = injected
    }
    await writeFile(path, text)
    const buffer = await readFile(path)
    if (path === hostPath) {
      record.host = { path: rel(path), stopCalls: patchCount(text, 'stop() {'), occurrences: patchCount(text, seatV1) }
    } else {
      record.client = { path: rel(path), occurrences: patchCount(text, seatV1) }
    }
    record.baselineSha256[rel(path)] = sha256hex(buffer)
  }
  return { ok: true, record }
}

/**
 * 在 PairingService.stop() 顶部插一行调用记录（夹具私有副本）。
 *
 * `stop()` 的语义是"撤销所有设备会话并清空令牌"（remote 包自带注释），因此
 * "停用 addon 绝不调用它"既可以直接由这个调用记录观察，也可以由设备是否仍可用观察。
 */
async function instrumentPairingStop(profile, dshHomeAbs) {
  const path = join(profileDirOf(profile), remotePackageRel, 'lib/index.js')
  let text = await readFile(path, 'utf8')
  if (text.includes(stopMarker)) return { ok: true, alreadyPatched: true, path: rel(path) }
  const needle = '\tstop() {\n\t\tthis.tokens.clear();'
  const occurrences = patchCount(text, needle)
  if (occurrences !== 1) return { ok: false, detail: `stop() 片段出现 ${occurrences} 次，拒绝盲改` }
  const logPath = join(dshHomeAbs, 'd23-pairing-stop-calls.log')
  const injected =
    `\tstop() {\n` +
    `\t\ttry { writeFileSync(${JSON.stringify(logPath)}, JSON.stringify({ at: new Date().toISOString(), stack: String(new Error().stack).split("\\n").slice(0, 3).join("|") }) + "\\n", { flag: "a" }); } catch (e) {} ${stopMarker}\n` +
    `\t\tthis.tokens.clear();`
  const patched = text.replace(needle, injected)
  if (patchCount(patched, stopMarker) !== 1 || patchCount(patched, needle) !== 0) return { ok: false, detail: '写入后自检失败' }
  await writeFile(path, patched)
  const after = await readFile(path)
  return { ok: true, alreadyPatched: false, path: rel(path), sha256: sha256hex(after).slice(0, 16), bytesDelta: after.length - text.length }
}

/** 读取 stop() 调用记录（不存在 = 0 次调用）。 */
function pairingStopCalls() {
  const path = join(dshHome, 'd23-pairing-stop-calls.log')
  if (existsSync(path) === false) return { exists: false, calls: 0, tail: '' }
  const text = readFileSync(path, 'utf8')
  const lines = text.split('\n').filter((line) => line.trim().length > 0)
  return { exists: true, calls: lines.length, tail: lines.slice(-2).join(' | ').slice(0, 300) }
}

/**
 * 把 remote 的 seat 全局名在**已装包的两处**一致改名（host 内联脚本 + 浏览器半区），
 * 模拟"全家桶更新后内部契约改名"：改写层本身照常工作（新名字），而按旧契约探测的
 * 本插件必须 fail closed。这是夹具私有的确定性模拟，不是真的装了另一个版本。
 */
async function applySeatRename(profile) {
  const dir = join(profileDirOf(profile), remotePackageRel)
  const record = { from: seatV1, to: seatV2, files: [] }
  for (const name of ['lib/index.js', 'lib/client.js']) {
    const path = join(dir, name)
    const text = await readFile(path, 'utf8')
    // 打包产物里常量是双引号字面量（`const X = "__DSH_REMOTE_CHANNEL_BOOT__";`）。
    const quoted = `"${seatV1}"`
    const occurrences = patchCount(text, quoted)
    if (occurrences !== 1) return { ok: false, detail: `${name} 中 seat 字面量出现 ${occurrences} 次，拒绝盲改` }
    const patched = text.split(quoted).join(`"${seatV2}"`)
    await writeFile(path, patched)
    const after = await readFile(path)
    record.files.push({ path: rel(path), occurrences, sha256: sha256hex(after).slice(0, 16) })
  }
  return { ok: true, record }
}

// ---------- 组合配置（停用是"只动本插件行"的独立范围） ----------

function dumpConfig(profile, patchPath) {
  const args = patchPath === null ? ['--profile', profile, '--dump-config'] : ['--profile', profile, '--patch', patchPath, '--dump-config']
  const result = spawnSync(dshBin, args, { cwd: repoRoot, env: { ...process.env, DSH_HOME: dshHome }, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 })
  return { status: result.status, text: String(result.stdout ?? '') + String(result.stderr ?? '') }
}

/** 行级差异：返回只出现在其中一边的行（保持顺序）。 */
function lineDelta(before, after) {
  const countOf = (text) => {
    const map = new Map()
    for (const line of text.split('\n')) map.set(line, (map.get(line) ?? 0) + 1)
    return map
  }
  const left = countOf(before)
  const right = countOf(after)
  const onlyLeft = []
  const onlyRight = []
  for (const [line, count] of left) if ((right.get(line) ?? 0) < count) onlyLeft.push(line)
  for (const [line, count] of right) if ((left.get(line) ?? 0) < count) onlyRight.push(line)
  return { onlyLeft, onlyRight }
}

// ---------- 会话播种 ----------

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
    child.on('exit', (code) => resolvePromise({ code, output: output.slice(-200) }))
  })
}

// ---------- 页面侧装置 ----------

const INSTALL_HARNESS = `(() => {
  if (globalThis.__D23__ !== undefined) return
  const state = { receivers: {}, retained: {}, saved: {}, files: {} }
  globalThis.__D23__ = state
  const scope = globalThis
  const G = {
    addon: '__DSH_ATTACHMENTS_ADDON__', bridge: '__DSH_ATTACHMENTS_BRIDGE__', receiver: '__DSH_ATTACHMENTS_RECEIVER__',
    hook: '__DSH_FILE_UPLOAD__', status: '__DSH_ATTACHMENTS_STATUS__', seat: '__DSH_REMOTE_CHANNEL_BOOT__'
  }
  const snap = (value) => (value === undefined || value === null ? null : JSON.parse(JSON.stringify(value)))

  const shapeOutgoing = (message) => ({
    type: message.type, status: message.status === undefined ? null : message.status,
    code: message.code === undefined ? null : message.code, fileId: message.fileId === undefined ? null : message.fileId,
    batchId: message.batchId === undefined ? null : message.batchId, seq: message.seq === undefined ? null : message.seq,
    reason: message.reason === undefined ? null : message.reason
  })

  const shapeResult = (result) => {
    if (result.ok !== true) return { ok: false, stage: result.stage, code: result.code, detail: result.detail }
    const file = result.file
    return {
      ok: true, stage: result.stage, type: result.type, fileId: result.fileId, batchId: result.batchId,
      record: file === undefined || file === null ? null : {
        transport: file.transport, draft: file.draft, upload: file.upload, ended: file.ended,
        receivedBytes: file.receivedBytes, attachmentIds: Array.from(file.attachmentIds),
        sha256: file.sha256 === undefined ? null : file.sha256
      },
      assembled: result.assembled === undefined || result.assembled === null ? null : {
        fileId: result.assembled.fileId, name: result.assembled.name, mime: result.assembled.mime, size: result.assembled.size
      },
      import: result.import === undefined || result.import === null ? null : {
        ok: result.import.result.ok === true,
        code: result.import.result.code === undefined ? null : result.import.result.code,
        added: Array.from(result.import.result.added === undefined ? [] : result.import.result.added),
        previous: Array.from(result.import.result.previous === undefined ? [] : result.import.result.previous),
        detail: result.import.result.detail === undefined ? null : String(result.import.result.detail).slice(0, 120),
        status: result.import.message === undefined ? null : result.import.message.status,
        ids: Array.from(result.import.message === undefined ? [] : (result.import.message.attachmentIds === undefined ? [] : result.import.message.attachmentIds)),
        applied: result.import.applied === true,
        appliedCode: result.import.appliedCode === undefined ? null : result.import.appliedCode
      }
    }
  }

  state.globals = () => JSON.stringify({
    addon: typeof scope[G.addon], bridge: typeof scope[G.bridge], receiver: typeof scope[G.receiver],
    hook: typeof scope[G.hook], status: typeof scope[G.status], seat: typeof scope[G.seat]
  })

  state.install = () => {
    const addon = scope[G.addon]
    const host = scope[G.receiver]
    const hook = scope[G.hook]
    const seat = scope[G.seat]
    return JSON.stringify({
      ok: addon !== undefined && addon !== null,
      addonType: typeof addon,
      addonVersion: addon === undefined ? null : addon.version,
      addonPackage: addon === undefined ? null : addon.packageName,
      bridgeType: typeof scope[G.bridge],
      receiverType: typeof host,
      receiverVersion: host === undefined ? null : host.version,
      wiring: host === undefined ? null : host.wiring,
      hookType: typeof hook,
      hookFetch: typeof (hook || {}).fetch,
      hookBrand: hook !== undefined && hook !== null && hook.brand !== undefined ? String(hook.brand) : null,
      seatType: typeof seat,
      seatRestore: typeof (seat || {}).restore,
      statusSeat: snap(scope[G.status]),
      addonStatus: addon === undefined ? null : addon.status(),
      capability: addon === undefined ? null : addon.capability(),
      currentSession: scope[G.bridge] === undefined ? null : scope[G.bridge].currentSession()
    })
  }

  state.make = (name) => {
    state.receivers[name] = scope[G.receiver].create({})
    return true
  }

  state.feed = async (name, text) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    let result
    try { result = await receiver.acceptText(text) } catch (error) {
      return JSON.stringify({ ok: false, reason: 'accept-threw:' + String(error && error.message ? error.message : error) })
    }
    let outgoing = []
    try { outgoing = await receiver.drainOutgoing() } catch (error) {
      return JSON.stringify({ ok: false, reason: 'drain-threw:' + String(error && error.message ? error.message : error) })
    }
    return JSON.stringify({ ok: true, result: shapeResult(result), outgoing: outgoing.map(shapeOutgoing), accounting: receiver.accounting, capability: receiver.capability })
  }

  state.receiverAccounting = (name) => JSON.stringify(state.receivers[name] === undefined ? null : state.receivers[name].accounting)
  state.addonStatus = () => JSON.stringify(scope[G.addon] === undefined ? null : scope[G.addon].status())
  state.capability = () => JSON.stringify(scope[G.addon] === undefined ? null : scope[G.addon].capability())

  /** 保留引用：撤销之后仍要用它们发起导入（证明"行为撤销"而不只是删全局名）。 */
  state.retainAll = () => {
    state.saved = { addon: scope[G.addon], bridge: scope[G.bridge], receiver: scope[G.receiver], hook: scope[G.hook] }
    return JSON.stringify({ addon: typeof state.saved.addon, bridge: typeof state.saved.bridge, receiver: typeof state.saved.receiver, hook: typeof state.saved.hook })
  }

  state.dispose = (reason) => {
    const handle = scope[G.addon]
    if (handle === undefined) return JSON.stringify({ ok: false, reason: 'no-addon' })
    const after = handle.dispose(reason)
    return JSON.stringify({ ok: true, after: JSON.parse(JSON.stringify(after)), globals: JSON.parse(state.globals()) })
  }

  state.restoreCarrierOnly = () => {
    scope[G.hook] = state.saved.hook
    return JSON.stringify({ restored: typeof scope[G.hook], globals: JSON.parse(state.globals()) })
  }

  state.restoreAllGlobals = () => {
    scope[G.hook] = state.saved.hook
    scope[G.bridge] = state.saved.bridge
    scope[G.receiver] = state.saved.receiver
    scope[G.addon] = state.saved.addon
    return state.globals()
  }

  /**
   * 恢复判定器（两个方向都要用同一个谓词，避免"通过条件"被临时放宽）。
   * recovered = 有活的 addon + 不需要重载 + 能力 available + 承载在位 +（可选）真实导入成功。
   */
  state.recovery = (doProbe) => {
    const handle = scope[G.addon]
    const bridge = scope[G.bridge]
    const seat = scope[G.status] === undefined || scope[G.status] === null ? null : scope[G.status].addon
    const live = handle === undefined || handle === null ? null : handle.status()
    const capability = handle === undefined || handle === null ? null : handle.capability()
    let probe = { ok: false, code: 'not-probed', detail: '' }
    if (doProbe === true) {
      if (bridge === undefined || bridge === null) probe = { ok: false, code: 'no-bridge', detail: 'bridge 全局不存在' }
      else {
        try {
          const bytes = new TextEncoder().encode('d23-recovery-probe')
          const result = bridge.importFiles({ files: [new File([bytes], 'd23-recovery-probe.txt', { type: 'text/plain' })] })
          probe = { ok: result.ok === true, code: result.code === undefined || result.code === null ? null : String(result.code), detail: String(result.detail === undefined ? '' : result.detail).slice(0, 100) }
        } catch (error) {
          probe = { ok: false, code: 'threw', detail: String(error && error.message ? error.message : error).slice(0, 100) }
        }
      }
    }
    const reloadRequired = live === null ? (seat === null ? null : seat.reloadRequired) : live.reloadRequired
    const hookState = typeof scope[G.hook] === 'object' && scope[G.hook] !== null
    return JSON.stringify({
      globals: JSON.parse(state.globals()),
      addonState: live === null ? null : live.state,
      reloadRequired: reloadRequired,
      capabilityStatus: capability === null ? null : capability.status,
      capabilityCode: capability === null ? null : capability.code,
      capabilityReason: capability === null ? null : String(capability.reason === null || capability.reason === undefined ? '' : capability.reason).slice(0, 120),
      uploadRoute: live === null ? null : live.upload.route,
      bridgeDisposed: bridge === undefined || bridge === null ? null : bridge.disposed === true,
      diagnosticsSeat: seat === null ? null : { state: seat.state, reloadRequired: seat.reloadRequired, disposeReason: seat.disposeReason },
      probe: probe,
      carrierPresent: hookState,
      recovered: live !== null && live.state === 'installed' && live.reloadRequired.required === false && capability !== null && capability.status === 'available' && hookState === true && (doProbe !== true || probe.ok === true)
    })
  }

  state.unknownHookCheck = () => {
    const hook = scope[G.hook]
    let marker = null
    try { marker = hook === undefined || hook === null ? null : String(hook.marker()) } catch (error) { marker = 'threw:' + String(error && error.message ? error.message : error) }
    return JSON.stringify({
      type: typeof hook,
      marker: marker,
      brand: hook !== undefined && hook !== null && hook.brand !== undefined ? String(hook.brand) : null,
      isSameObject: hook === scope.__D23_UNKNOWN_HOOK__,
      hookFetch: typeof (hook || {}).fetch,
      globals: JSON.parse(state.globals())
    })
  }

  state.retainedBridgeProbe = (sessionId) => {
    const bridge = state.saved.bridge
    if (bridge === undefined) return JSON.stringify({ ok: false, code: 'no-retained-bridge' })
    const bytes = new TextEncoder().encode('d23-retained-probe')
    const file = new File([bytes], 'd23-retained-probe.txt', { type: 'text/plain' })
    try {
      const result = sessionId === null ? bridge.importFiles({ files: [file] }) : bridge.importFiles({ sessionId: sessionId, files: [file] })
      return JSON.stringify({ ok: result.ok === true, code: result.code === undefined ? null : result.code, detail: String(result.detail === undefined ? '' : result.detail).slice(0, 120) })
    } catch (error) {
      return JSON.stringify({ ok: false, code: 'threw', detail: String(error && error.message ? error.message : error).slice(0, 120) })
    }
  }

  /**
   * 承载探针：直接调用 __DSH_FILE_UPLOAD__.fetch 走一次真实上传路径。
   * 健康页面上它必须真的发出请求（对照组），降级/不支持页面上必须以 capability-disabled 拒绝且零请求。
   */
  state.carrierProbe = async (label) => {
    const hook = scope[G.hook]
    if (hook === undefined || hook === null || typeof hook.fetch !== 'function') return JSON.stringify({ thrown: false, reason: 'no-carrier', hookType: typeof hook })
    const bytes = new TextEncoder().encode('d23-carrier-probe-' + String(label))
    try {
      const response = await hook.fetch('/api/session/uploadFileBinary?sessionId=' + encodeURIComponent(String(label)) + '&name=d23-carrier-probe.txt', {
        method: 'POST', headers: { 'content-type': 'application/octet-stream' }, body: bytes
      })
      return JSON.stringify({ thrown: false, status: response.status })
    } catch (error) {
      return JSON.stringify({ thrown: true, code: error && error.code !== undefined ? String(error.code) : null, message: String(error && error.message ? error.message : error).slice(0, 160) })
    }
  }

  /** 内置入口：把真实 File 写进 composer 的原生 input 并只触发一次 change（不经过本插件任何代码）。 */
  state.nativeImport = (label, size) => {
    const card = document.querySelectorAll('[data-composer-card]')[0]
    if (card === undefined) return JSON.stringify({ ok: false, reason: 'no-card' })
    const input = card.querySelector('input[type=file]')
    if (input === undefined || input === null) return JSON.stringify({ ok: false, reason: 'no-input' })
    const sizeBytes = size === undefined ? 4096 : size
    const lines = []
    let total = 0
    let index = 0
    while (total < sizeBytes) {
      const line = 'D23-NATIVE-' + String(label) + ' line ' + String(index).padStart(5, '0') + ' ' + 'n'.repeat(24)
      lines.push(line)
      total += line.length + 1
      index += 1
    }
    const text = lines.join('\\n').slice(0, sizeBytes)
    const bytes = new TextEncoder().encode(text)
    const file = new File([bytes], 'd23-native-' + String(label) + '.txt', { type: 'text/plain' })
    const transfer = new DataTransfer()
    transfer.items.add(file)
    input.files = transfer.files
    const dispatched = input.dispatchEvent(new Event('change', { bubbles: true }))
    let base64 = ''
    const chunk = 8192
    for (let offset = 0; offset < bytes.length; offset += chunk) base64 += String.fromCharCode.apply(null, bytes.subarray(offset, offset + chunk))
    return JSON.stringify({ ok: true, dispatched: dispatched, name: file.name, bytes: bytes.length, base64: btoa(base64) })
  }

  /**
   * 原生草稿的附件位（与本插件无关的读数）——取 data-slot="conversation.input.attachments"。
   * 用 title 属性与可见文案判断"该文件真的进了草稿"和"这条卡片处于失败态"，不依赖 CSS 模块名。
   */
  state.builtInState = () => {
    const slot = document.querySelector('[data-slot="conversation.input.attachments"]')
    const html = slot === null ? '' : String(slot.innerHTML || '')
    const text = slot === null ? '' : String(slot.innerText || '')
    return JSON.stringify({
      present: slot !== null,
      html: html.slice(0, 600),
      text: text.slice(0, 300),
      titles: Array.from(slot === null ? [] : slot.querySelectorAll('[title]')).map((node) => String(node.getAttribute('title'))).slice(0, 5),
      failed: /failed|上传失败|重试/.test(html) || /上传失败|重试/.test(text),
      cards: document.querySelectorAll('[data-composer-card]').length
    })
  }

  state.cardText = () => {
    const card = document.querySelectorAll('[data-composer-card]')[0]
    return JSON.stringify({
      cards: document.querySelectorAll('[data-composer-card]').length,
      inputs: card === undefined ? null : card.querySelectorAll('input[type=file]').length,
      phase: document.querySelector('[data-composer-input]') === null ? null : document.querySelector('[data-composer-input]').getAttribute('data-phase'),
      conversation: document.querySelector('[data-conversation-scroll]') !== null,
      text: card === undefined ? null : card.innerText.slice(0, 300)
    })
  }
})()`

/** 页面求值（带超时）：把"挂住"变成一条可见失败，而不是让判据无声卡死。 */
const evaluate = (page, expression, { timeoutMs = 60_000 } = {}) =>
  Promise.race([
    page.evaluate(expression, { awaitPromise: true }),
    new Promise((_resolve, reject) => setTimeout(() => reject(new Error(`页面求值超时（${timeoutMs}ms）：${String(expression).slice(0, 80)}`)), timeoutMs))
  ])

const fed = (page, name, text) => evaluate(page, `globalThis.__D23__.feed(${JSON.stringify(name)}, ${JSON.stringify(text)})`).then(JSON.parse)

/** 页面上下文：把页面、网络事件、页面异常收在一个对象里。 */
async function openPageContext(chrome, { base, sessionId, pairToken, extraHeadScript, deviceQuery, navigateTo }) {
  const page = await openPage(chrome.debugPort)
  const requests = []
  const pageErrors = []
  const context = { page, requests, pageErrors, base, sessionId }
  page.onEvent((message) => {
    if (message.method === 'Network.requestWillBeSent') {
      requests.push({ kind: 'request', id: message.params.requestId, url: message.params.request.url, method: message.params.request.method })
    } else if (message.method === 'Network.responseReceived') {
      requests.push({ kind: 'response', id: message.params.requestId, url: message.params.response.url, status: message.params.response.status })
    } else if (message.method === 'Runtime.exceptionThrown') {
      pageErrors.push(String(message.params.exceptionDetails?.exception?.description ?? '').slice(0, 200))
    }
  })
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  if (extraHeadScript !== undefined) await page.send('Page.addScriptToEvaluateOnNewDocument', { source: extraHeadScript })
  await page.send('Page.addScriptToEvaluateOnNewDocument', { source: INSTALL_HARNESS })
  if (sessionId !== undefined && sessionId !== null && sessionId !== '') {
    await page.send('Page.addScriptToEvaluateOnNewDocument', {
      source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
    })
  }
  const deviceId = pairToken === undefined || pairToken === null ? '' : await pairInChrome(page, base, pairToken)
  await page.navigate(navigateTo ?? `${base}/pair-app${deviceQuery === undefined ? '' : deviceQuery}`)
  return { ...context, deviceId }
}

/** 原厂基线页（本插件整行禁用、页面上没有 addon）的就绪判据：composer 与原生 input 真实渲染。 */
async function waitForComposer(page, { timeoutMs = 60_000 } = {}) {
  return poll(
    async () => {
      try {
        return JSON.parse(await evaluate(page, 'globalThis.__D23__ === undefined ? JSON.stringify({cards:0}) : globalThis.__D23__.cardText()'))
      } catch {
        return { cards: 0 }
      }
    },
    // 必须有真实的会话面（`[data-conversation-scroll]`，remote 的 boot watchdog 也用同一个信号），
    // 否则拿到的可能只是"选择工作区/新任务"首页 composer，导入会被丢掉（D23 实测）。
    (value) => value.cards === 1 && value.inputs === 1 && value.phase !== null && value.conversation === true,
    { timeoutMs, intervalMs: 1000 }
  )
}

/** 内置入口的一次真实导入之后，从原生草稿附件位读出的结果形状（不依赖本插件）。 */
function builtInOutcome(slot, name) {
  const html = String(slot?.html ?? '')
  const text = String(slot?.text ?? '')
  const titles = Array.isArray(slot?.titles) ? slot.titles : []
  return {
    attached: titles.includes(name) || html.includes(`title="${name}"`) || text.includes(name),
    uploadFailed: slot?.failed === true
  }
}

/**
 * 内置入口导入（带重试）：把真实 File 写进 composer 的原生 input 并触发一次 change，
 * 直到原生草稿附件位真的出现该文件名。重试只用于"页面还没准备好、change 被丢掉"的窗口，
 * 一旦附件位出现立即停止（因此不会重复附着）。
 */
async function nativeImportIntoDraft(page, label, size, { attempts = 3, waitMs = 8000 } = {}) {
  let native = null
  let slot = null
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    native = JSON.parse(await evaluate(page, `globalThis.__D23__.nativeImport(${JSON.stringify(label)}, ${size})`))
    slot = await readBuiltInSlot(page, native.name, { timeoutMs: waitMs })
    if (builtInOutcome(slot, native.name).attached) return { native, slot, attempts: attempt }
  }
  return { native, slot, attempts }
}

/** 读原生草稿附件位，并等到本次导入的文件真的出现（最多 timeoutMs）。 */
async function readBuiltInSlot(page, name, { timeoutMs = 15_000 } = {}) {
  const result = await poll(
    async () => JSON.parse(await evaluate(page, 'globalThis.__D23__.builtInState()')),
    (value) => builtInOutcome(value, name).attached,
    { timeoutMs, intervalMs: 500 }
  )
  return result.value
}

/** 等到页面上的 addon 装好并打开目标会话（重载/重启后复用它）。 */
async function waitForPageReady(context, { timeoutMs = 90_000 } = {}) {
  return poll(
    async () => {
      try {
        return JSON.parse(await evaluate(context.page, 'globalThis.__D23__ === undefined ? JSON.stringify({harness:false}) : globalThis.__D23__.install()'))
      } catch (error) {
        return { ok: false, harness: true, error: String(error && error.message ? error.message : error).slice(0, 120) }
      }
    },
    (value) => value.addonType === 'object' && value.receiverType === 'object' && value.currentSession === context.sessionId,
    { timeoutMs, intervalMs: 1000 }
  )
}

// ---------- 生产码构造的线消息（与服务端同一 codec 校验） ----------

const messageText = (raw) => {
  const decoded = decodeMessage(JSON.stringify(raw))
  if (decoded.ok !== true) throw new Error(`门禁构造的消息不合法：${decoded.code} ${decoded.detail} @${decoded.path}`)
  return JSON.stringify(decoded.message)
}

const contextText = (sessionId) => messageText({ v: 1, type: 'context', sessionId, targetId: 'target-d23', documentEpoch: 31, composerEpoch: 32, composerScope: 'composer-scope-d23' })
const batchBeginText = ({ sessionId, batchId, totalBytes }) => messageText({ v: 1, type: 'batch-begin', sessionId, batchId, targetId: 'target-d23', documentEpoch: 31, composerEpoch: 32, fileCount: 1, totalBytes })
const fileBeginText = ({ sessionId, batchId, fileId, name, byteLength, sha }) => messageText({ v: 1, type: 'file-begin', sessionId, batchId, fileId, name, byteLength, mime: 'text/plain', sha256: sha })
const fileEndText = ({ sessionId, batchId, fileId, totalBytes, sha }) => messageText({ v: 1, type: 'file-end', sessionId, batchId, fileId, totalBytes, sha256: sha })
const chunkText = ({ sessionId, batchId, fileId, plan, bytes }) => messageText({ v: 1, type: 'chunk', sessionId, batchId, fileId, seq: plan.seq, offset: plan.offset, byteLength: plan.byteLength, dataBase64: base64Encode(bytes.subarray(plan.offset, plan.offset + plan.byteLength)) })

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

const MARKER = 'DSH-D23-COMPAT-MARKER'
/** 300 KiB：跨过 256 KiB 满块边界，覆盖"满块 + 尾块"。 */
const MAIN_BYTES = makeTextBytes(300 * 1024, MARKER)
const MAIN_SHA = sha256hex(MAIN_BYTES)
const RELOAD_BYTES = makeTextBytes(70 * 1024, `${MARKER}-RELOAD`)
const RELOAD_SHA = sha256hex(RELOAD_BYTES)

/** 跑一条真实分块批次（生产接收端 → 生产桥 → 原生草稿），返回逐层结果。 */
async function runTransfer(context, { label, bytes, sha, sessionId, chunkBytes = 262144, fileId = null }) {
  const batchId = `batch-d23-${label}-${Date.now()}`
  const resolvedFileId = fileId ?? `file-d23-${label}`
  const name = `d23-${label}.txt`
  await evaluate(context.page, `globalThis.__D23__.make(${JSON.stringify(label)})`)
  const contextFed = await fed(context.page, label, contextText(sessionId))
  const begin = await fed(context.page, label, batchBeginText({ sessionId, batchId, totalBytes: bytes.length }))
  const fileBegin = await fed(context.page, label, fileBeginText({ sessionId, batchId, fileId: resolvedFileId, name, byteLength: bytes.length, sha }))
  const plans = planChunks(bytes.length, chunkBytes)
  const chunkResults = []
  for (const plan of plans) chunkResults.push(await fed(context.page, label, chunkText({ sessionId, batchId, fileId: resolvedFileId, plan, bytes })))
  const end = await fed(context.page, label, fileEndText({ sessionId, batchId, fileId: resolvedFileId, totalBytes: bytes.length, sha }))
  return { label, name, batchId, fileId: resolvedFileId, context: contextFed, begin, fileBegin, plans, chunkResults, end, record: end.result?.record ?? null, import: end.result?.import ?? null }
}

/** 等一次真实上传请求出现，并取回 receipt 正文（正文未必立刻可取，按 D22 的做法重试）。 */
async function collectUpload(context, { name, sha, timeoutMs = 30_000 }) {
  const uploadUrl = `uploadFileBinary`
  const seen = () => context.requests.filter((entry) => entry.kind === 'request' && entry.url.includes(uploadUrl) && entry.url.includes(encodeURIComponent(name)))
  const found = await poll(async () => seen(), (value) => value.length >= 1, { timeoutMs, intervalMs: 400 })
  const request = found.value[0] ?? null
  if (request === null) return { ok: false, detail: '没有任何上传请求', request: null, status: null, receipt: null, responseId: null }
  const response = await poll(
    async () => context.requests.find((entry) => entry.kind === 'response' && entry.id === request.id) ?? null,
    (value) => value !== null,
    { timeoutMs: 20_000, intervalMs: 300 }
  )
  const responseEntry = response.value
  let receipt = null
  let bodyError = null
  if (responseEntry !== null) {
    for (let attempt = 0; attempt < 12 && receipt === null; attempt += 1) {
      try {
        const body = await context.page.send('Network.getResponseBody', { requestId: request.id })
        receipt = JSON.parse(body.body)
      } catch (error) {
        bodyError = String(error && error.message ? error.message : error).slice(0, 120)
        await sleep(500)
      }
    }
  }
  const value = receipt?.value ?? null
  return {
    ok: responseEntry?.status === 200 && receipt?.ok === true && value?.file?.attachmentId === `sha256:${sha}`,
    request,
    responseId: responseEntry?.id ?? null,
    status: responseEntry?.status ?? null,
    receipt: value === null ? null : { receiptId: String(value.receiptId ?? '').slice(-8), attachmentId: value.file?.attachmentId ?? null, bytes: value.file?.bytes ?? null, name: value.file?.name ?? null },
    bodyError
  }
}

/** 内置（无本插件参与）上传：只认主机内容寻址库里出现的、逐字节相同的对象。 */
async function collectBuiltInUpload(sha, { expected = null, timeoutMs = 30_000 } = {}) {
  const stored = await waitForStoreObject(sha, { timeoutMs })
  if (stored.ok !== true) return { ok: false, detail: '主机库中未出现该对象', matches: false, bytes: 0 }
  return {
    ok: true,
    matches: expected === null ? null : bytesEqual(stored.value, expected),
    bytes: stored.value.length,
    sha256: sha256hex(stored.value)
  }
}

/** 配对读数快照：设备集合 + 心跳 + /remote 读会话 + PairingService.stop 调用计数。 */
async function capturePairing(client, base, deviceId, label) {
  const snapshot = {
    label,
    store: deviceStoreFingerprint(),
    heartbeat: await heartbeat(client, base),
    listStatus: (await rpc(client, base, deviceId, 'session/list', { _request: {} })).status,
    stopCalls: pairingStopCalls().calls
  }
  pairingTrend.push(snapshot)
  return snapshot
}

// =====================================================================
// 主流程
// =====================================================================

let service = null
let chrome = null
const openPages = []
let fatal = null
const serviceLog = join(evidenceDir, 'service-d23.log')
/** 配对读数的纵向趋势：设备集合只增不减、stop 调用恒为 0（addon 操作期间）。 */
const pairingTrend = []

const mainBase = () => `http://${lanAddress}:${port}`
let lanAddress = ''
/** 探针客户端：与浏览器共用同一个已配对设备（cookie jar 手动带上设备 id）。 */
let probeClient = null

/** 启动一个夹具服务；支持 --patch 覆盖层；返回启动 URL（loopback 上带浏览器凭据 token）。 */
async function startFixtureService({ profile, patchPath, logPath, basePort }) {
  const args = ['--profile', profile]
  if (patchPath !== undefined && patchPath !== null) args.push('--patch', patchPath)
  args.push('--no-open')
  const started = startService(dshBin, args, { cwd: repoRoot, dshHome, logPath })
  await writeFile(join(fixtureRoot, 'service.pid'), String(started.child.pid))
  const bootUrl = await waitForReady(started.child, logPath, 120_000)
  return { ...started, logPath, profile, basePort, bootUrl }
}

try {
  console.log(`D23 兼容/停用/更新后状态判据 runId=${RUN_ID}\n`)
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })
  await mkdir(RUN_DIR, { recursive: true })

  if (existsSync(join(fixtureRoot, 'lan-address.txt')) === false) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  const base = mainBase()

  // ================= 阶段 0：清单/范围/组合配置（零成本，纯文件与 --dump-config） =================
  console.log('阶段 0：固定版本 vs 实际安装、停用范围、生产未触碰守卫\n')
  const lock = JSON.parse(await readFile(lockPath, 'utf8'))
  const profileDir = profileDirOf(fixtureProfile)
  const profileManifestPath = join(profileDir, 'package.json')
  const profileManifest = JSON.parse(await readFile(profileManifestPath, 'utf8'))
  const lockfilePath = join(profileDir, 'pnpm-lock.yaml')
  const lockfileText = await readFile(lockfilePath, 'utf8')
  const installedVersions = {}
  for (const [name, key] of [
    ['@linxin666/dsh-web-all', 'all'],
    ['@linxin666/dsh-remote-web-ui', 'remote'],
    ['@shxtmaker/dsh-remote-attachments', 'addon']
  ]) {
    const manifest = join(profileDir, 'node_modules', name, 'package.json')
    installedVersions[key] = existsSync(manifest) ? JSON.parse(await readFile(manifest, 'utf8')).version : null
  }
  const harnessVersion = spawnSync(dshBin, ['--version'], { encoding: 'utf8' }).stdout.trim().split('\n')[0]
  const lockVersions = {
    harness: lock.harness.version,
    all: lock.packages.find((entry) => entry.name === '@linxin666/dsh-web-all')?.version ?? null,
    remote: lock.packages.find((entry) => entry.name === '@linxin666/dsh-remote-web-ui')?.version ?? null,
    addon: lock.packages.find((entry) => entry.name === '@shxtmaker/dsh-remote-attachments')?.version ?? null
  }
  const bundles = profileManifest?.dsh?.profile?.bundles ?? []
  const composedBase = dumpConfig(fixtureProfile, null)
  const addonRows = composedBase.text.split('\n').filter((line) => line.trim() === "- id: remote-attachments").length
  const remoteRows = composedBase.text.split('\n').filter((line) => line.includes('@linxin666/dsh-remote-web-ui')).length
  observations.versions = {
    pinned: lockVersions,
    observed: { harness: harnessVersion, ...installedVersions },
    profileManifestDependencies: profileManifest.dependencies,
    bundles,
    lockfileMentions: {
      webAll: lockfileText.includes(`'@linxin666/dsh-web-all@${lockVersions.all}'`),
      remoteWebUi: lockfileText.includes(`'@linxin666/dsh-remote-web-ui@${lockVersions.remote}'`),
      addonTarball: /@shxtmaker\/dsh-remote-attachments@file:/.test(lockfileText)
    },
    composedAddonRows: addonRows,
    composedRemoteRows: remoteRows
  }
  gate(
    'G01-verified-combination-manifest',
    '已验证组合的清单层：Harness/all/remote/addon 的**实际安装版本**与 compatibility-lock.json 逐项一致，组合配置里本插件行恰好一行、remote 提供者恰好一份，profile 锁文件记录到 tarball',
    harnessVersion === lockVersions.harness &&
      installedVersions.all === lockVersions.all &&
      installedVersions.remote === lockVersions.remote &&
      installedVersions.addon === lockVersions.addon &&
      bundles.join(',') === '@deepseek-ai/dsh-base,@deepseek-ai/dsh-web-app,@linxin666/dsh-web-all,@shxtmaker/dsh-remote-attachments' &&
      addonRows === 1 &&
      remoteRows === 1 &&
      observations.versions.lockfileMentions.webAll &&
      observations.versions.lockfileMentions.remoteWebUi &&
      observations.versions.lockfileMentions.addonTarball,
    `harness=${harnessVersion} all=${installedVersions.all} remote=${installedVersions.remote} addon=${installedVersions.addon} 插件行=${addonRows} remote 行=${remoteRows}`
  )

  const overlayPath = join(fixtureRoot, 'd23-disable-overlay.yml')
  await writeFile(
    overlayPath,
    [
      '# D23 独立停用覆盖层（只针对本插件行；由 d23-compat-gates.mjs 生成）',
      '- id: remote-attachments',
      "  name: '@shxtmaker/dsh-remote-attachments'",
      '  config:',
      '    enabled: false',
      ''
    ].join('\n')
  )
  const manifestShaBefore = sha256hex(await readFile(profileManifestPath))
  const lockShaBefore = sha256hex(await readFile(lockfilePath))
  const composedDisabled = dumpConfig(fixtureProfile, overlayPath)
  const delta = lineDelta(composedBase.text, composedDisabled.text)
  const productionBefore = productionFingerprint()
  observations.scope = {
    overlay: rel(overlayPath),
    overlayContent: await readFile(overlayPath, 'utf8'),
    composedDiff: delta,
    profileManifestSha256: manifestShaBefore,
    lockfileSha256: lockShaBefore,
    exactSameLength: composedBase.text.length < composedDisabled.text.length
  }
  console.log(`  组合配置差异行：+${delta.onlyRight.length} / -${delta.onlyLeft.length}`)

  // ================= 阶段 1：会话与主服务 =================
  console.log('阶段 1：播种会话、启动主夹具服务（带 PairingService.stop 调用记录）\n')
  // 设备表默认上限 4（@linxin666/dsh-remote-web-ui 的 maxDevices），且每次 accept 挤掉最老设备。
  // 上次运行/侦察留下的设备会让本轮的关键设备被挤出，因此先清空夹具私有的配对表（不是生产）。
  const leftoverDevices = deviceStoreFingerprint()
  await rm(join(dshHome, 'remote-web-ui-devices.json'), { force: true })
  observations.pairingReset = { clearedLeftovers: leftoverDevices, maxDevicesNote: '上游默认 maxDevices=4：第 5 次配对会挤掉最老的设备，因此本门禁全程只保留一个主设备 + 一个负向控制设备。' }
  const seedA = await seedSession('d23 compatibility gate session A')
  const seedB = await seedSession('d23 compatibility gate session B')
  observations.seeds = { a: seedA.code, b: seedB.code }

  await rm(join(dshHome, 'd23-pairing-stop-calls.log'), { force: true })
  const baseline = await ensureRemoteBaseline(fixtureProfile)
  if (baseline.ok !== true) throw new Error(`remote 包基线还原失败：${baseline.detail}`)
  const instrumentation = await instrumentPairingStop(fixtureProfile, dshHome)
  if (instrumentation.ok !== true) throw new Error(`stop 调用记录插桩失败：${instrumentation.detail}`)
  observations.instrumentation = { baseline: baseline.record, stop: instrumentation }

  service = await startFixtureService({ profile: fixtureProfile, patchPath: null, logPath: serviceLog, basePort: port })
  const bootUrl = service.bootUrl

  const token = await issueToken(port)
  // 设备表默认上限 4（remote 的 maxDevices），且每次 accept 会挤掉最老的设备。
  // 因此本门禁只做**一次**真实配对（浏览器里走 /pair-accept），所有页面复用同一个
  // HttpOnly cookie，Node 侧探针也用同一个设备 id——否则探针自己会被后来的配对准出局。
  console.log('  已铸造一次性配对令牌；配对在浏览器里走真实 /pair-accept（阶段 2）\n')

  // ================= 阶段 2：已验证组合（能力 + 附件通路 + 配对不受影响） =================
  console.log('阶段 2：已验证组合（LAN 页面，浏览器内真实配对）\n')
  chrome = await launchChrome({})
  // 第一次进页面：只做真实配对（还不需要会话）。配对后再按设备凭据读共享会话列表，
  // 最后预置 localStorage 并重新进页面，让应用真的打开目标会话。
  const verified = await openPageContext(chrome, { base, sessionId: null, pairToken: token, deviceQuery: '' })
  openPages.push(verified.page)
  if (verified.deviceId === '') throw new Error('浏览器侧 /pair-accept 没有拿到设备凭据')
  probeClient = { client: createClient(), deviceId: verified.deviceId }
  probeClient.client.jar.set('dsh_pair', verified.deviceId)
  const unpaired = createClient()
  unpaired.jar.set('dsh_pair', 'not-a-paired-device')
  const unpairedProbe = await rpc(unpaired, base, 'not-a-paired-device', 'session/list', { _request: {} })
  const listProbe = await rpc(probeClient.client, base, probeClient.deviceId, 'session/list', { _request: {} })
  const items = listProbe.body?.result?.value?.items ?? []
  const sessionIds = [...new Set(items.map((item) => item.sessionId).filter((value) => typeof value === 'string'))]
  if (sessionIds.length < 1) throw new Error(`共享 DSH_HOME 里没有可用会话（items=${items.length}，listStatus=${listProbe.status}）`)
  const SESSION = sessionIds[0]
  verified.sessionId = SESSION
  await verified.page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(SESSION)}}))}catch(e){}`
  })
  await verified.page.navigate(`${base}/pair-app`)
  observations.sessions = { count: sessionIds.length, current: SESSION.slice(0, 12) + '…', listStatus: listProbe.status, unpairedStatus: unpairedProbe.status }
  const stopCallsAtStart = pairingStopCalls()
  const storeAtStart = deviceStoreFingerprint()
  const pairingAtStart = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase2-after-browser-pairing')
  const statusAtStart = await pairStatus(probeClient.client, base)
  observations.pairing = {
    devicesAfterOneBrowserPairing: storeAtStart,
    statusHttp: statusAtStart.status,
    deviceCount: statusAtStart.body?.deviceCount ?? null,
    onlineCount: statusAtStart.body?.onlineCount ?? null,
    atStart: pairingAtStart
  }
  console.log(`  会话 ${SESSION}\n  设备数=${storeAtStart.count} stop 调用记录=${stopCallsAtStart.calls}\n`)
  const pairingBeforeTransfer = pairingAtStart
  const verifiedReady = await waitForPageReady(verified)
  const verifiedInstall = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.install()'))
  const capability = verifiedInstall.capability
  const statusBefore = verifiedInstall.addonStatus
  const preRecovery = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.recovery(false)'))
  observations.verified = {
    ready: verifiedReady.ok,
    browserDeviceMatched: verified.deviceId === probeClient.deviceId,
    browserDevicePresent: verified.deviceId.length > 0,
    install: {
      addonVersion: verifiedInstall.addonVersion,
      addonPackage: verifiedInstall.addonPackage,
      receiverVersion: verifiedInstall.receiverVersion,
      wiring: verifiedInstall.wiring,
      hookType: verifiedInstall.hookType,
      hookFetch: verifiedInstall.hookFetch,
      hookBrand: verifiedInstall.hookBrand,
      hookBrandIsOurs: verifiedInstall.hookBrand === `@shxtmaker/dsh-remote-attachments/carrier@0.1.0`,
      seatType: verifiedInstall.seatType,
      seatRestore: verifiedInstall.seatRestore,
      build: statusBefore?.build ?? null,
      buildMatchesPackage: statusBefore?.build === ADDON_BUILD
    },
    capability,
    uploadRoute: statusBefore?.upload?.route ?? null,
    carrierCapturedAtBoot: statusBefore?.reloadRequired?.carrierCapturedAtBoot ?? null,
    preRecovery
  }
  gate(
    'G02-verified-capability-available',
    '已验证组合的页面能力：addon/bridge/receiver 三个全局齐备且构建标识等于包内构建；能力 available 且无原因码；承载是本插件装的（品牌一致、fetch 形状）；上传路由为 remote-rewrite；状态面记录"boot 阶段已捕获承载闭包"',
    verifiedReady.ok === true &&
      verifiedInstall.addonType === 'object' &&
      verifiedInstall.addonPackage === '@shxtmaker/dsh-remote-attachments' &&
      verifiedInstall.receiverType === 'object' &&
      statusBefore?.build === ADDON_BUILD &&
      capability?.status === 'available' &&
      capability?.code === null &&
      verifiedInstall.hookType === 'object' &&
      verifiedInstall.hookFetch === 'function' &&
      verifiedInstall.hookBrand === '@shxtmaker/dsh-remote-attachments/carrier@0.1.0' &&
      statusBefore?.upload?.route === 'remote-rewrite' &&
      statusBefore?.reloadRequired?.carrierCapturedAtBoot === true &&
      preRecovery.recovered === true,
    `能力=${capability?.status}/${capability?.code} 路由=${statusBefore?.upload?.route} 承载=${verifiedInstall.hookType}/${verifiedInstall.hookFetch}/${verifiedInstall.hookBrand} 恢复谓词基线=${preRecovery.recovered}`
  )
  negative(
    'N01-recovery-predicate-can-be-true',
    '对照（防恒假）：同一个"恢复"谓词在健康页面上必须为真，否则 G06/G07 的 false/true 对比没有判别力',
    preRecovery.recovered === true && preRecovery.addonState === 'installed' && preRecovery.reloadRequired?.required === false,
    `recovered=${preRecovery.recovered} state=${preRecovery.addonState} reloadRequired=${preRecovery.reloadRequired?.required}`
  )

  console.log('  真实分块批次（300 KiB，满块 + 尾块）→ 原生草稿 → /remote 上传 → 主机库\n')
  const main = await runTransfer(verified, { label: 'verified-main', bytes: MAIN_BYTES, sha: MAIN_SHA, sessionId: SESSION })
  const mainUpload = await collectUpload(verified, { name: main.name, sha: MAIN_SHA })
  const mainStore = await collectBuiltInUpload(MAIN_SHA, { expected: MAIN_BYTES })
  const nativeIds = main.record?.attachmentIds ?? []
  const draftProbe = main.import === null ? null : { previous: main.import.previous, added: main.import.added, applied: main.import.applied, ok: main.import.ok, code: main.import.code, detail: main.import.detail }
  observations.verified.transfer = {
    plans: main.plans.length,
    chunkSizes: main.plans.map((plan) => plan.byteLength),
    fileEnd: { ok: main.end?.result?.ok ?? null, code: main.end?.result?.code ?? null },
    record: main.record,
    draftProbe,
    upload: mainUpload,
    store: mainStore,
    accounting: main.end?.accounting ?? null
  }
  gate(
    'G03-verified-attachment-path',
    '已验证组合的附件通路四层一致：分块（含 256 KiB 满块）经生产接收端组装并校验哈希 → 生产桥进入原生草稿拿到真实 attachmentIds → /remote 门控上传 200 且 receipt 的 attachmentId/字节数与源一致 → 主机内容寻址库里同一哈希对象逐字节相同',
    main.plans.length >= 2 &&
      main.plans.some((plan) => plan.byteLength === 262144) &&
      main.end?.result?.ok === true &&
      main.record?.draft === 'staged' &&
      main.record?.upload === 'harness-owned' &&
      nativeIds.length >= 1 &&
      main.import?.ok === true &&
      main.import?.applied === true &&
      mainUpload.ok === true &&
      mainStore.ok === true &&
      mainStore.matches === true,
    `块=${main.plans.length} 最大块=${Math.max(...main.plans.map((plan) => plan.byteLength))} 原生 id=${nativeIds.length} receipt=${mainUpload.status}/${mainUpload.receipt?.attachmentId?.slice(0, 20) ?? '(无)'} 主机库=${mainStore.bytes}B 逐字节=${mainStore.matches}`
  )
  const badBytes = Buffer.from(MAIN_BYTES)
  badBytes[1024] = badBytes[1024] ^ 0xff
  const badSha = sha256hex(badBytes)
  const badTransfer = await runTransfer(verified, { label: 'verified-badhash', bytes: badBytes, sha: MAIN_SHA, sessionId: SESSION, chunkBytes: 262144, fileId: 'file-d23-verified-badhash' })
  const badStore = await readStoreObject(badSha)
  negative(
    'N02-bytes-are-what-is-claimed',
    '反例：把载荷改一个字节但仍宣称原哈希时，file-end 必须以哈希不符拒绝、不得产生原生 id、主机库里不得出现该载荷的对象（所以 G03 的"逐字节一致"不是恒真）',
    badTransfer.end?.result?.ok === false && (badTransfer.record?.attachmentIds ?? []).length === 0 && badStore === null,
    `file-end=${badTransfer.end?.result?.ok} code=${badTransfer.end?.result?.code ?? '(无)'} ids=${(badTransfer.record?.attachmentIds ?? []).length} 主机库对象=${badStore === null ? '无' : '竟然有'}`
  )
  const randomSha = sha256hex(Buffer.from(`never-uploaded-${RUN_ID}`))
  const randomObject = await readStoreObject(randomSha)
  negative(
    'N03-store-check-is-falsifiable',
    '反例：从未上传过的内容在主机库里必须查不到对象（否则"主机库逐字节一致"这条判据可能只是恒真）',
    randomObject === null,
    `随机哈希对象=${randomObject === null ? '无（正确）' : '竟然存在'}`
  )

  const pairingAfterTransfer = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase2-after-transfer')
  observations.verified.pairingAfter = { before: pairingBeforeTransfer, after: pairingAfterTransfer }
  gate(
    'G04-pairing-and-device-set-untouched',
    '附件通路跑通后配对与设备库不受影响：同一设备心跳 200、/remote 读会话 200，设备集合（数量 + 集合哈希）与本次操作**之前**逐字段一致，且 PairingService.stop 的调用记录为 0',
    pairingAfterTransfer.heartbeat === 200 &&
      pairingAfterTransfer.listStatus === 200 &&
      pairingAfterTransfer.store.count === pairingBeforeTransfer.store.count &&
      pairingAfterTransfer.store.setHash === pairingBeforeTransfer.store.setHash &&
      pairingAfterTransfer.stopCalls === 0,
    `心跳=${pairingAfterTransfer.heartbeat} 会话列表=${pairingAfterTransfer.listStatus} 设备 ${pairingBeforeTransfer.store.count}→${pairingAfterTransfer.store.count}（集合哈希 ${pairingBeforeTransfer.store.setHash}→${pairingAfterTransfer.store.setHash}） stop 调用=${pairingAfterTransfer.stopCalls}`
  )

  // ================= 阶段 3：运行时停用 + 不得谎报恢复 + 真实重载后恢复 =================
  console.log('\n阶段 3：运行时停用（不重启服务）→ 只恢复全局变量 → 真实页面重载\n')
  const pairingBeforeDisable = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase3-before-disable')
  const retained = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.retainAll()'))
  const disposed = JSON.parse(await evaluate(verified.page, `globalThis.__D23__.dispose('d23-runtime-disable')`))
  const afterDisposeStatus = disposed.after
  const retainedProbeAfterDispose = JSON.parse(await evaluate(verified.page, `globalThis.__D23__.retainedBridgeProbe(${JSON.stringify(SESSION)})`))
  const pairingAfterDisable = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase3-after-disable')
  observations.disable = {
    retained,
    after: {
      state: afterDisposeStatus?.state,
      disposeReason: afterDisposeStatus?.disposeReason,
      disposeCount: afterDisposeStatus?.lifecycle?.disposeCount,
      reloadRequired: afterDisposeStatus?.reloadRequired,
      uploadRoute: afterDisposeStatus?.upload?.route,
      capability: { status: afterDisposeStatus?.capability?.status, code: afterDisposeStatus?.capability?.code },
      globals: disposed.globals
    },
    retainedProbeAfterDispose,
    pairingBefore: pairingBeforeDisable,
    pairingAfter: pairingAfterDisable
  }
  gate(
    'G05-runtime-disable-revokes-addon-only',
    '运行时停用只撤自己的东西：addon/bridge/receiver 三个全局被删、本插件装的承载被撤、状态面 disposed 且明确 ReloadRequired（带 reload-required 原因码）、保留的桥引用再导入一律被拒；同时配对毫发无损（心跳 200、/remote 读会话 200、设备集合与**停用前**逐字段一致、PairingService.stop 调用记录仍为 0）',
    disposed.ok === true &&
      disposed.globals.addon === 'undefined' &&
      disposed.globals.bridge === 'undefined' &&
      disposed.globals.receiver === 'undefined' &&
      disposed.globals.hook === 'undefined' &&
      afterDisposeStatus?.state === 'disposed' &&
      afterDisposeStatus?.reloadRequired?.required === true &&
      afterDisposeStatus?.reloadRequired?.carrierCapturedAtBoot === true &&
      /reload-required/.test(String(afterDisposeStatus?.reloadRequired?.reason ?? '')) &&
      afterDisposeStatus?.upload?.route === 'disposed' &&
      retainedProbeAfterDispose.ok === false &&
      retainedProbeAfterDispose.code === 'capability-disabled' &&
      pairingAfterDisable.heartbeat === 200 &&
      pairingAfterDisable.listStatus === 200 &&
      pairingAfterDisable.store.count === pairingBeforeDisable.store.count &&
      pairingAfterDisable.store.setHash === pairingBeforeDisable.store.setHash &&
      pairingAfterDisable.stopCalls === 0,
    `全局=${JSON.stringify(disposed.globals)} 状态=${afterDisposeStatus?.state} reloadRequired=${afterDisposeStatus?.reloadRequired?.required} 保留桥=${retainedProbeAfterDispose.code} 心跳=${pairingAfterDisable.heartbeat} 设备集合=${pairingBeforeDisable.store.setHash}→${pairingAfterDisable.store.setHash} stop 调用=${pairingAfterDisable.stopCalls}`
  )

  // 只恢复承载全局
  const restoredCarrierOnly = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.restoreCarrierOnly()'))
  const recoveryCarrierOnly = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.recovery(true)'))
  // 再恢复全部四个全局（"恢复全局变量"的最强形式）
  const restoredAll = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.restoreAllGlobals()'))
  const recoveryAll = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.recovery(true)'))
  const pairingAfterRestore = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase3-after-globals-restore')
  observations.reload = {
    restoredCarrierOnly,
    recoveryCarrierOnly,
    restoredAll,
    recoveryAll,
    pairingAfterRestore
  }
  gate(
    'G06-no-false-recovery-claim',
    '不得谎报恢复：停用后**只把全局变量放回去**（先只恢复承载、再恢复全部四个全局）都不构成恢复——状态面仍是 disposed、仍报告 ReloadRequired，保留引用导入仍被 capability-disabled 拒绝，恢复谓词仍为 false；因此"恢复了全局"绝不能被当成"runtime 已恢复"',
    recoveryCarrierOnly.recovered === false &&
      recoveryAll.recovered === false &&
      recoveryAll.addonState === 'disposed' &&
      recoveryAll.reloadRequired?.required === true &&
      /reload-required/.test(String(recoveryAll.reloadRequired?.reason ?? '')) &&
      recoveryAll.uploadRoute === 'disposed' &&
      recoveryAll.capabilityStatus !== 'available' &&
      recoveryAll.probe.ok === false &&
      recoveryAll.probe.code === 'capability-disabled' &&
      recoveryAll.diagnosticsSeat?.state === 'disposed' &&
      recoveryAll.diagnosticsSeat?.reloadRequired?.required === true &&
      recoveryCarrierOnly.probe.ok === false,
    `只恢复承载 recovered=${recoveryCarrierOnly.recovered}；恢复全部全局 recovered=${recoveryAll.recovered} state=${recoveryAll.addonState} reloadRequired=${recoveryAll.reloadRequired?.required} 导入=${recoveryAll.probe.code} 诊断座=${recoveryAll.diagnosticsSeat?.state}`
  )
  negative(
    'N04-globals-restore-is-not-recovery',
    '反例（本任务的验收点）：把四个全局原样放回后，被撤销的桥/接收端仍然拒绝导入、状态面仍然要求重载——所以"恢复了全局变量"这条捷径不成立',
    recoveryAll.recovered === false && recoveryAll.probe.ok === false && recoveryAll.bridgeDisposed === true,
    `recovered=${recoveryAll.recovered} 导入=${recoveryAll.probe.code} 桥已撤销=${recoveryAll.bridgeDisposed}`
  )

  // ---- 重载方向 1：LAN 页面（先试裸 reload，再走配对页真实再入） ----
  //
  // 实测边界（本轮证据里如实记录，并有干净页面对照）：LAN 文档的**裸 reload** 会被 Harness 的
  // 浏览器凭据层以 401（`dsh web authentication required`）拒绝——这是上游 all-bundle/浏览器
  // 凭据交接的性质，与本插件无关（对照页面没有本插件参与、裸 reload 同样 401）。手机上的
  // 受支持再入路径是 `/pair-app`（配对后再入），因此这里两条都测：先裸 reload（记录事实），
  // 再走 `/pair-app` 再入作为恢复路径。
  const pairingBeforeLanReload = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase3-lan-before-reload')
  await verified.page.send('Page.reload', {})
  const lanBareReloadReady = await waitForPageReady(verified, { timeoutMs: 25_000 })
  const lanBareReloadState = JSON.parse(
    await evaluate(verified.page, 'JSON.stringify({ href: location.href, globals: globalThis.__D23__ === undefined ? null : JSON.parse(globalThis.__D23__.globals()), text: document.body ? document.body.innerText.slice(0, 120) : null })').catch(() => '{"href":null}')
  )

  // 干净对照页：本插件完全没被停用/触碰过，同样裸 reload —— 若它也 401，则上面的 401 与停用无关。
  const control = await openPageContext(chrome, { base, sessionId: SESSION, pairToken: null, deviceQuery: '' })
  openPages.push(control.page)
  const controlReady = await waitForPageReady(control, { timeoutMs: 60_000 })
  await control.page.send('Page.reload', {})
  const controlBareReloadReady = await waitForPageReady(control, { timeoutMs: 25_000 })
  const controlBareReloadState = JSON.parse(
    await evaluate(control.page, 'JSON.stringify({ href: location.href, addon: typeof globalThis.__DSH_ATTACHMENTS_ADDON__, text: document.body ? document.body.innerText.slice(0, 120) : null })').catch(() => '{"href":null}')
  )
  await control.page.close()

  // 恢复路径：走真实再入口 `/pair-app`（新文档），随后用同一谓词与真实批次验证。
  await verified.page.navigate(`${base}/pair-app`)
  const reloadReady = await waitForPageReady(verified)
  const reloadInstall = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.install()'))
  const reloadRecovery = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.recovery(false)'))
  const reloadTransfer = await runTransfer(verified, { label: 'verified-reload', bytes: RELOAD_BYTES, sha: RELOAD_SHA, sessionId: SESSION, chunkBytes: 131072 })
  const reloadUpload = await collectUpload(verified, { name: reloadTransfer.name, sha: RELOAD_SHA })
  const reloadStore = await collectBuiltInUpload(RELOAD_SHA, { expected: RELOAD_BYTES })
  const pairingAfterLanReload = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase3-lan-after-reentry')
  observations.reload.afterReload = {
    lanBareReload: { ready: lanBareReloadReady.ok, state: lanBareReloadState },
    controlBareReload: { ready: controlBareReloadReady.ok, state: controlBareReloadState },
    reentry: {
      ready: reloadReady.ok,
      capability: reloadInstall.capability,
      reloadRequired: reloadInstall.addonStatus?.reloadRequired,
      recovery: reloadRecovery,
      upload: reloadUpload,
      store: reloadStore,
      nativeIds: reloadTransfer.record?.attachmentIds ?? [],
      draft: reloadTransfer.record?.draft ?? null
    },
    pairingBefore: pairingBeforeLanReload,
    pairingAfter: pairingAfterLanReload
  }
  gate(
    'G07-page-reload-recovers-capability',
    '页面重载方向：停用+只恢复全局之后，走真实的配对页再入（新文档）——同一恢复谓词从 false 翻到 true（addon 重新装好、能力 available、不再要求重载、承载在位），真实批次再次拿到原生 attachmentIds、上传 200、主机库逐字节一致；配对与设备集合在这一步前后逐字段不变',
    reloadReady.ok === true &&
      reloadRecovery.recovered === true &&
      reloadInstall.addonStatus?.reloadRequired?.required === false &&
      reloadInstall.capability?.status === 'available' &&
      reloadInstall.hookType === 'object' &&
      (reloadTransfer.record?.attachmentIds ?? []).length >= 1 &&
      reloadTransfer.record?.draft === 'staged' &&
      reloadUpload.ok === true &&
      reloadStore.ok === true &&
      reloadStore.matches === true &&
      pairingAfterLanReload.heartbeat === 200 &&
      pairingAfterLanReload.listStatus === 200 &&
      pairingAfterLanReload.store.setHash === pairingBeforeLanReload.store.setHash,
    `再入后 recovered=${reloadRecovery.recovered} 能力=${reloadInstall.capability?.status} 需重载=${reloadInstall.addonStatus?.reloadRequired?.required} 原生 id=${(reloadTransfer.record?.attachmentIds ?? []).length} receipt=${reloadUpload.status} 主机库=${reloadStore.bytes}B 逐字节=${reloadStore.matches} 设备集合=${pairingBeforeLanReload.store.setHash}→${pairingAfterLanReload.store.setHash}`
  )
  negative(
    'N11-lan-bare-reload-401-is-upstream',
    '边界（不是本插件的锅，也不是恒真断言）：LAN 文档的裸 reload 被浏览器凭据层 401；对照页面（本插件完全未被触碰）裸 reload 行为必须一致——因此"裸 reload 401"不能记在本插件账上，也不能被当成"重载后能力恢复"的证据',
    lanBareReloadReady.ok === false &&
      controlBareReloadReady.ok === false &&
      controlReady.ok === true &&
      /authentication required|401|未授权/.test(String(controlBareReloadState.text ?? '')) === true &&
      controlBareReloadState.addon === 'undefined',
    `被测页裸 reload ready=${lanBareReloadReady.ok}（href=${String(lanBareReloadState.href).slice(0, 40)}）对照页裸 reload ready=${controlBareReloadReady.ok}（addon=${controlBareReloadState.addon}）对照页首屏="${String(controlBareReloadState.text).slice(0, 60).replace(/\n/g, ' ')}"`
  )

  // ---- 重载方向 2：loopback 页面上的**真实 Page.reload** ----
  //
  // loopback 上 Harness 自己服务 `/`（浏览器凭据由启动 URL 的 token 交给浏览器），因此这里是
  // 货真价实的文档重载：重载前装好并跑通一次传输，`Page.reload` 之后必须重新装好并再跑通一次。
  const loopbackContext = await openPageContext(chrome, { base: `http://127.0.0.1:${port}`, sessionId: SESSION, pairToken: null, deviceQuery: '', navigateTo: bootUrl })
  openPages.push(loopbackContext.page)
  const loopbackReadyBefore = await waitForPageReady(loopbackContext, { timeoutMs: 90_000 })
  const loopbackInstallBefore = JSON.parse(await evaluate(loopbackContext.page, 'globalThis.__D23__.install()'))
  const LOOPBACK_BYTES = makeTextBytes(24 * 1024, `${MARKER}-LOOPBACK`)
  const LOOPBACK_SHA = sha256hex(LOOPBACK_BYTES)
  const loopbackTransferBefore = await runTransfer(loopbackContext, { label: 'loopback-before-reload', bytes: LOOPBACK_BYTES, sha: LOOPBACK_SHA, sessionId: SESSION, chunkBytes: 16384 })
  const loopbackStoreBefore = await collectBuiltInUpload(LOOPBACK_SHA, { expected: LOOPBACK_BYTES })
  await loopbackContext.page.send('Page.reload', {})
  const loopbackReadyAfter = await waitForPageReady(loopbackContext, { timeoutMs: 90_000 })
  const loopbackInstallAfter = JSON.parse(await evaluate(loopbackContext.page, 'globalThis.__D23__.install()'))
  const loopbackRecoveryAfter = JSON.parse(await evaluate(loopbackContext.page, 'globalThis.__D23__.recovery(false)'))
  const LOOPBACK2_BYTES = makeTextBytes(12 * 1024, `${MARKER}-LOOPBACK-RELOAD`)
  const LOOPBACK2_SHA = sha256hex(LOOPBACK2_BYTES)
  const loopbackTransferAfter = await runTransfer(loopbackContext, { label: 'loopback-after-reload', bytes: LOOPBACK2_BYTES, sha: LOOPBACK2_SHA, sessionId: SESSION, chunkBytes: 16384 })
  const loopbackStoreAfter = await collectBuiltInUpload(LOOPBACK2_SHA, { expected: LOOPBACK2_BYTES })
  const pairingAfterLoopbackReload = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase3-after-loopback-reload')
  observations.reload.loopback = {
    origin: `http://127.0.0.1:${port}`,
    bootTokenUsed: String(bootUrl).includes('token='),
    before: {
      ready: loopbackReadyBefore.ok,
      capability: loopbackInstallBefore.capability?.status ?? null,
      route: loopbackInstallBefore.addonStatus?.upload?.route ?? null,
      nativeIds: (loopbackTransferBefore.record?.attachmentIds ?? []).length,
      store: loopbackStoreBefore
    },
    after: {
      ready: loopbackReadyAfter.ok,
      capability: loopbackInstallAfter.capability?.status ?? null,
      route: loopbackInstallAfter.addonStatus?.upload?.route ?? null,
      reloadRequired: loopbackInstallAfter.addonStatus?.reloadRequired?.required ?? null,
      recovery: loopbackRecoveryAfter.recovered,
      nativeIds: (loopbackTransferAfter.record?.attachmentIds ?? []).length,
      store: loopbackStoreAfter
    },
    pairingAfter: pairingAfterLoopbackReload
  }
  gate(
    'G07b-genuine-page-reload-on-loopback',
    '真实 `Page.reload`（loopback 上 Harness 自己服务文档）后能力真的恢复：重载前 capability=available、路由 loopback-direct、真实批次进原生草稿且主机库逐字节一致；重载后同一谓词从 false 翻到 true，并且再来一条真实批次同样进草稿 + 主机库逐字节一致（这是"重载确实能恢复"的硬证据，而不是"恢复了全局变量"）',
    loopbackReadyBefore.ok === true &&
      loopbackInstallBefore.capability?.status === 'available' &&
      (loopbackTransferBefore.record?.attachmentIds ?? []).length >= 1 &&
      loopbackStoreBefore.ok === true &&
      loopbackStoreBefore.matches === true &&
      loopbackReadyAfter.ok === true &&
      loopbackInstallAfter.capability?.status === 'available' &&
      loopbackInstallAfter.addonStatus?.reloadRequired?.required === false &&
      loopbackRecoveryAfter.recovered === true &&
      (loopbackTransferAfter.record?.attachmentIds ?? []).length >= 1 &&
      loopbackStoreAfter.ok === true &&
      loopbackStoreAfter.matches === true &&
      pairingAfterLoopbackReload.stopCalls === 0,
    `重载前 能力=${loopbackInstallBefore.capability?.status} 路由=${loopbackInstallBefore.addonStatus?.upload?.route} 原生 id=${(loopbackTransferBefore.record?.attachmentIds ?? []).length} 主机库=${loopbackStoreBefore.bytes}B/逐字节=${loopbackStoreBefore.matches}；重载后 能力=${loopbackInstallAfter.capability?.status} 需重载=${loopbackInstallAfter.addonStatus?.reloadRequired?.required} 原生 id=${(loopbackTransferAfter.record?.attachmentIds ?? []).length} 主机库=${loopbackStoreAfter.bytes}B/逐字节=${loopbackStoreAfter.matches}`
  )
  await loopbackContext.page.close()

  // ================= 阶段 4：Harness 重启 =================
  console.log('\n阶段 4：重启 Harness（同一 profile、同一 DSH_HOME）\n')
  const pairingBeforeRestart = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase4-before-restart')
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = null
  const restartLog = join(evidenceDir, 'service-d23-restart.log')
  service = await startFixtureService({ profile: fixtureProfile, patchPath: null, logPath: restartLog, basePort: port })
  const pairingAfterRestart = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase4-after-restart')
  // 与阶段 3 同一口径：LAN 上的**裸 reload** 被上游浏览器凭据层拒绝（见 N11 的干净对照），
  // 因此"重启后再进入页面"同样走受支持的配对页再入口；这仍是新文档、新 boot。
  await verified.page.navigate(`${base}/pair-app`)
  const restartReady = await waitForPageReady(verified)
  const restartInstall = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.install()'))
  const restartRecovery = JSON.parse(await evaluate(verified.page, 'globalThis.__D23__.recovery(false)'))
  const restartBytes = makeTextBytes(40 * 1024, `${MARKER}-RESTART`)
  const restartSha = sha256hex(restartBytes)
  const restartTransfer = await runTransfer(verified, { label: 'verified-restart', bytes: restartBytes, sha: restartSha, sessionId: SESSION, chunkBytes: 131072 })
  const restartUpload = await collectUpload(verified, { name: restartTransfer.name, sha: restartSha })
  const restartStore = await readStoreObject(restartSha)
  observations.restart = {
    before: pairingBeforeRestart,
    after: pairingAfterRestart,
    listItems: (await rpc(probeClient.client, base, probeClient.deviceId, 'session/list', { _request: {} })).body?.result?.value?.items?.length ?? null,
    ready: restartReady.ok,
    capability: restartInstall.capability?.status ?? null,
    recovery: restartRecovery.recovered,
    nativeIds: restartTransfer.record?.attachmentIds ?? [],
    upload: restartUpload,
    storeMatches: restartStore === null ? null : bytesEqual(restartStore, restartBytes)
  }
  gate(
    'G08-harness-restart-preserves-pairing-and-attachment',
    '重启 Harness 之后：同一设备（同一 deviceId、同一凭据）心跳 200、/remote 读会话 200、设备集合与重启前逐字段一致、PairingService.stop 调用记录仍为 0；页面重载后 addon 重新装好、能力 available、恢复谓词为 true，并且真实批次仍然拿到原生 id、上传 200、主机库逐字节一致',
    pairingAfterRestart.heartbeat === 200 &&
      pairingAfterRestart.listStatus === 200 &&
      pairingAfterRestart.store.count === pairingBeforeRestart.store.count &&
      pairingAfterRestart.store.setHash === pairingBeforeRestart.store.setHash &&
      pairingAfterRestart.stopCalls === 0 &&
      restartReady.ok === true &&
      restartInstall.capability?.status === 'available' &&
      restartRecovery.recovered === true &&
      (restartTransfer.record?.attachmentIds ?? []).length >= 1 &&
      restartUpload.ok === true &&
      restartStore !== null &&
      bytesEqual(restartStore, restartBytes),
    `心跳=${pairingAfterRestart.heartbeat} 会话列表=${pairingAfterRestart.listStatus} 设备 ${pairingBeforeRestart.store.count}→${pairingAfterRestart.store.count}（集合哈希 ${pairingBeforeRestart.store.setHash}→${pairingAfterRestart.store.setHash}） 能力=${restartInstall.capability?.status} 原生 id=${(restartTransfer.record?.attachmentIds ?? []).length} 主机库逐字节=${restartStore === null ? '无' : bytesEqual(restartStore, restartBytes)} stop 调用=${pairingAfterRestart.stopCalls}`
  )

  // ================= 阶段 5：未知既有 hook =================
  console.log('\n阶段 5：未知既有 __DSH_FILE_UPLOAD__（boot 前注入）\n')
  const unknownScript = `globalThis.__DSH_FILE_UPLOAD__ = { fetch: function () { return Promise.reject(new Error('unknown-hook-carrier')) }, marker: function () { return 'd23-unknown-hook-alive' } }; globalThis.__D23_UNKNOWN_HOOK__ = globalThis.__DSH_FILE_UPLOAD__;`
  const unknownContext = await openPageContext(chrome, { base, sessionId: SESSION, pairToken: null, extraHeadScript: unknownScript, deviceQuery: `?device=${encodeURIComponent(probeClient.deviceId)}` })
  openPages.push(unknownContext.page)
  await waitForPageReady(unknownContext, { timeoutMs: 60_000 })
  const unknownHook = JSON.parse(await evaluate(unknownContext.page, 'globalThis.__D23__.unknownHookCheck()'))
  const unknownCapability = JSON.parse(await evaluate(unknownContext.page, 'globalThis.__D23__.capability()'))
  const unknownStatus = JSON.parse(await evaluate(unknownContext.page, 'globalThis.__D23__.addonStatus()'))
  await evaluate(unknownContext.page, `globalThis.__D23__.make('unknown')`)
  const unknownFeed = await fed(unknownContext.page, 'unknown', contextText(SESSION))
  const unknownBegin = await fed(unknownContext.page, 'unknown', batchBeginText({ sessionId: SESSION, batchId: 'batch-d23-unknown', totalBytes: 4096 }))
  const unknownCancel = (unknownBegin.outgoing ?? []).find((message) => message.type === 'cancel') ?? null
  // 桥层必须给出确定的原因码（接收端的 context 准入在冲突时根本不放行，因此以桥为准）。
  const unknownRecovery = JSON.parse(await evaluate(unknownContext.page, 'globalThis.__D23__.recovery(true)'))
  const unknownHeartbeat = await heartbeat(probeClient.client, base)
  observations.unknownHook = {
    hook: unknownHook,
    capability: unknownCapability,
    status: { capability: unknownStatus?.capability, upload: unknownStatus?.upload },
    feed: { ok: unknownFeed.ok, code: unknownFeed.result?.code ?? null, detail: unknownFeed.result?.detail ?? null },
    begin: { ok: unknownBegin.ok, result: unknownBegin.result ?? null, accounting: unknownBegin.accounting ?? null },
    importsInvoked: unknownBegin.accounting?.importsInvoked ?? null,
    cancel: unknownCancel,
    recovery: unknownRecovery,
    heartbeat: unknownHeartbeat,
    pageErrors: unknownContext.pageErrors.slice(0, 3)
  }
  gate(
    'G09-unknown-hook-conflict-preserves-original',
    '未知既有上传承载：能力判定 unavailable + capability-conflict（保留原功能、绝不覆盖），承载没有被换成我们的（无品牌、不是同一对象、marker 仍可用），接收端以 capability-conflict 拒绝整批（cancel 带该码）、桥对导入也以同一码拒绝，配对/心跳照常',
    unknownHook.type === 'object' &&
      unknownHook.marker === 'd23-unknown-hook-alive' &&
      unknownHook.brand === null &&
      unknownHook.isSameObject === true &&
      unknownCapability?.status === 'unavailable' &&
      unknownCapability?.code === 'capability-conflict' &&
      unknownStatus?.upload?.hook === 'conflict' &&
      unknownStatus?.upload?.ownership === 'unknown' &&
      unknownFeed.result?.ok === false &&
      unknownFeed.result?.code === 'no-session' &&
      /capability-conflict/.test(String(unknownFeed.result?.detail ?? '')) &&
      unknownBegin.ok === true &&
      unknownBegin.result?.ok === false &&
      /capability-conflict/.test(String(unknownBegin.result?.detail ?? '')) &&
      unknownCancel !== null &&
      unknownCancel.reason === 'capability-conflict' &&
      unknownBegin.accounting?.importsInvoked === 0 &&
      unknownRecovery.recovered === false &&
      unknownRecovery.probe.code === 'capability-conflict' &&
      unknownHeartbeat === 200,
    `hook=${unknownHook.type}/marker=${unknownHook.marker}/brand=${unknownHook.brand} 能力=${unknownCapability?.status}/${unknownCapability?.code} 归属=${unknownStatus?.upload?.ownership} 批次拒绝码=${unknownFeed.result?.code} cancel.reason=${unknownCancel?.reason} 导入次数=${unknownBegin.accounting?.importsInvoked} 桥导入=${unknownRecovery.probe.code} 心跳=${unknownHeartbeat}`
  )
  negative(
    'N05-unknown-hook-not-adopted',
    '反例：未知 hook 必须是**同一个对象**且**没有**被贴上本插件品牌（若我们覆盖了它，这两条会立刻失败）',
    unknownHook.isSameObject === true && unknownHook.brand === null && unknownHook.marker === 'd23-unknown-hook-alive',
    `同一对象=${unknownHook.isSameObject} 品牌=${unknownHook.brand} marker=${unknownHook.marker}`
  )

  // 降级页面上"零请求"的对照组：健康页面的承载会真的发出请求
  const healthyCarrier = JSON.parse(await evaluate(verified.page, `globalThis.__D23__.carrierProbe(${JSON.stringify(SESSION)})`))
  await sleep(1500)
  const healthyCarrierRequests = verified.requests.filter((entry) => entry.kind === 'request' && entry.url.includes('uploadFileBinary') && entry.url.includes('d23-carrier-probe.txt'))
  negative(
    'N06-carrier-probe-control-group',
    '对照组（防恒真）：健康页面上同一个承载探针会真的发出上传请求，因此 G10/G12 的"零请求"是拒绝生效的结果，而不是探针本身不会发请求',
    healthyCarrier.status === 200 && healthyCarrierRequests.length >= 1,
    `承载探针=${JSON.stringify(healthyCarrier)} 请求数=${healthyCarrierRequests.length}`
  )

  // ================= 阶段 6：独立停用（覆盖层，仅本插件行） =================
  console.log('\n阶段 6：独立停用覆盖层（LAN 页 + loopback 页）\n')
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = null
  const disabledLog = join(evidenceDir, 'service-d23-disabled.log')
  service = await startFixtureService({ profile: fixtureProfile, patchPath: overlayPath, logPath: disabledLog, basePort: port })
  const disabledBootUrl = service.bootUrl
  const disabledContext = await openPageContext(chrome, { base, sessionId: SESSION, pairToken: null, deviceQuery: '' })
  openPages.push(disabledContext.page)
  const disabledReady = await waitForPageReady(disabledContext, { timeoutMs: 60_000 })
  const pairingBeforeBuiltIn = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase6-before-builtin-upload')
  const disabledInstall = JSON.parse(await evaluate(disabledContext.page, 'globalThis.__D23__.install()'))
  const disabledCapability = JSON.parse(await evaluate(disabledContext.page, 'globalThis.__D23__.capability()'))
  const disabledRecovery = JSON.parse(await evaluate(disabledContext.page, 'globalThis.__D23__.recovery(true)'))
  const disabledCardBefore = JSON.parse(await evaluate(disabledContext.page, 'globalThis.__D23__.cardText()'))
  const disabledImport = await nativeImportIntoDraft(disabledContext.page, 'disabled', 4096)
  const disabledNative = disabledImport.native
  const disabledNativeBytes = Buffer.from(disabledNative.base64 ?? '', 'base64')
  const disabledNativeSha = sha256hex(disabledNativeBytes)
  const disabledBuiltIn = await collectBuiltInUpload(disabledNativeSha, { expected: disabledNativeBytes, timeoutMs: 20_000 })
  const disabledCardAfter = JSON.parse(await evaluate(disabledContext.page, 'globalThis.__D23__.cardText()'))
  const disabledSlot = disabledImport.slot
  const pairingAfterBuiltIn = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase6-after-builtin-upload')

  // loopback 页面：同一次停用运行里的第二个来源。
  // 用该服务的启动 URL（带 token）访问一次，让 Harness 的浏览器凭据 cookie 落到浏览器里，
  // 之后裸 `/` 才是被服务的文档。
  const disabledLoopbackContext = await openPageContext(chrome, { base: `http://127.0.0.1:${port}`, sessionId: SESSION, pairToken: null, deviceQuery: '', navigateTo: disabledBootUrl })
  openPages.push(disabledLoopbackContext.page)
  const loopbackReady = await waitForPageReady(disabledLoopbackContext, { timeoutMs: 90_000 })
  const loopbackInstall = JSON.parse(await evaluate(disabledLoopbackContext.page, 'globalThis.__D23__.install()'))
  const loopbackCapability = JSON.parse(await evaluate(disabledLoopbackContext.page, 'globalThis.__D23__.capability()'))
  const loopbackImport = await nativeImportIntoDraft(disabledLoopbackContext.page, 'disabled-loopback', 4096)
  const loopbackNative = loopbackImport.native
  const loopbackNativeBytes = Buffer.from(loopbackNative.base64 ?? '', 'base64')
  const loopbackNativeSha = sha256hex(loopbackNativeBytes)
  const loopbackBuiltIn = await collectBuiltInUpload(loopbackNativeSha, { expected: loopbackNativeBytes, timeoutMs: 20_000 })
  const loopbackCardAfter = JSON.parse(await evaluate(disabledLoopbackContext.page, 'globalThis.__D23__.cardText()'))
  const loopbackSlot = loopbackImport.slot

  observations.disabledAddon = {
    ready: disabledReady.ok,
    install: {
      addonType: disabledInstall.addonType,
      bridgeType: disabledInstall.bridgeType,
      receiverType: disabledInstall.receiverType,
      hookType: disabledInstall.hookType,
      hookFetch: disabledInstall.hookFetch,
      statusSeat: disabledInstall.statusSeat
    },
    capability: disabledCapability,
    statusCapability: disabledInstall.addonStatus?.capability,
    reloadRequired: disabledInstall.addonStatus?.reloadRequired,
    recovery: disabledRecovery,
    cardBefore: disabledCardBefore,
    nativeImport: { dispatched: disabledNative.dispatched, name: disabledNative.name, bytes: disabledNative.bytes, sha256: disabledNativeSha },
    builtIn: disabledBuiltIn,
    cardAfter: disabledCardAfter,
    slot: disabledSlot,
    outcome: builtInOutcome(disabledSlot, disabledNative.name),
    importAttempts: disabledImport.attempts,
    pairingBefore: pairingBeforeBuiltIn,
    pairingAfter: pairingAfterBuiltIn,
    loopback: {
      ready: loopbackReady.ok,
      capability: loopbackCapability,
      statusCapability: loopbackInstall.addonStatus?.capability,
      hookType: loopbackInstall.hookType,
      facts: loopbackInstall.addonStatus?.capability?.facts ?? null,
      nativeImport: { dispatched: loopbackNative.dispatched, name: loopbackNative.name, bytes: loopbackNative.bytes, sha256: loopbackNativeSha },
      builtIn: loopbackBuiltIn,
      cardAfter: loopbackCardAfter,
      slot: loopbackSlot,
      outcome: builtInOutcome(loopbackSlot, loopbackNative.name),
      importAttempts: loopbackImport.attempts,
      origin: `http://127.0.0.1:${port}`
    }
  }
  gate(
    'G11-independent-disable-scope-and-builtin-fallback',
    '独立停用（覆盖层只改本插件行）：host 不再注入承载（页面 boot 时 __DSH_FILE_UPLOAD__ 就不存在，状态座无 uploadHook），LAN 页能力 unavailable + capability-disabled、loopback 页 degraded + capability-disabled（同一原因码，且 facts.enabled=false 证明禁用标记真的到了浏览器半区），桥对导入一律拒绝；**内置附件行为照常**：真实 File 经 composer 原生 input 进入原生草稿，loopback 上内置上传真的落进主机内容寻址库且逐字节一致；配对/心跳/设备集合不受影响',
    disabledReady.ok === true &&
      disabledInstall.addonType === 'object' &&
      disabledInstall.hookType === 'undefined' &&
      disabledInstall.hookFetch === 'undefined' &&
      disabledInstall.statusSeat?.uploadHook === undefined &&
      disabledCapability?.status === 'unavailable' &&
      disabledCapability?.code === 'capability-disabled' &&
      disabledCapability?.facts?.uploadHook === 'absent' &&
      disabledInstall.addonStatus?.reloadRequired?.carrierCapturedAtBoot === false &&
      disabledRecovery.recovered === false &&
      disabledRecovery.probe.ok === false &&
      disabledNative.dispatched === true &&
      builtInOutcome(disabledSlot, disabledNative.name).attached === true &&
      loopbackCapability?.status === 'degraded' &&
      loopbackCapability?.code === 'capability-disabled' &&
      loopbackInstall.hookType === 'undefined' &&
      loopbackInstall.addonStatus?.capability?.facts?.enabled === false &&
      loopbackNative.dispatched === true &&
      builtInOutcome(loopbackSlot, loopbackNative.name).attached === true &&
      builtInOutcome(loopbackSlot, loopbackNative.name).uploadFailed === false &&
      loopbackBuiltIn.ok === true &&
      loopbackBuiltIn.matches === true &&
      loopbackBuiltIn.bytes === loopbackNativeBytes.length &&
      pairingAfterBuiltIn.heartbeat === 200 &&
      pairingAfterBuiltIn.listStatus === 200 &&
      pairingAfterBuiltIn.store.count === pairingBeforeBuiltIn.store.count &&
      pairingAfterBuiltIn.store.setHash === pairingBeforeBuiltIn.store.setHash &&
      pairingAfterBuiltIn.stopCalls === 0,
    `hook=${disabledInstall.hookType} LAN 能力=${disabledCapability?.status}/${disabledCapability?.code} loopback=${loopbackCapability?.status}/${loopbackCapability?.code}/enabled=${String(loopbackInstall.addonStatus?.capability?.facts?.enabled)} LAN 内置上传落库=${disabledBuiltIn.ok}（上游拒绝后台裸 /api，见 G11b 对照）loopback 内置上传落库=${loopbackBuiltIn.ok}/${loopbackBuiltIn.matches} 草稿附件位 titles=${JSON.stringify(disabledSlot.titles)} 心跳=${pairingAfterBuiltIn.heartbeat} 设备集合=${pairingBeforeBuiltIn.store.setHash}→${pairingAfterBuiltIn.store.setHash}`
  )
  negative(
    'N07-disabled-page-has-no-carrier-at-boot',
    '反例：停用页面上的承载必须在 **boot 时就缺席**（否则"用了内置传输"只是我们撤销得晚，而不是停用真的生效）',
    disabledInstall.hookType === 'undefined' && disabledInstall.statusSeat?.uploadHook === undefined && disabledCapability?.facts?.uploadHook === 'absent',
    `hook=${disabledInstall.hookType} 状态座 uploadHook=${String(disabledInstall.statusSeat?.uploadHook)} facts=${disabledCapability?.facts?.uploadHook}`
  )
  const mutatedLoopback = Buffer.from(loopbackNativeBytes)
  if (mutatedLoopback.length > 0) mutatedLoopback[0] = mutatedLoopback[0] ^ 0xff
  const storedLoopback = await readStoreObject(loopbackNativeSha)
  negative(
    'N08-builtin-upload-is-byte-exact',
    '反例：内置路径的"逐字节一致"必须能失败——把源字节改一个字节后，同一读数必须报不一致（证明 G11 里 loopback 内置上传的 matches 不是恒真）',
    storedLoopback !== null && bytesEqual(storedLoopback, loopbackNativeBytes) === true && bytesEqual(storedLoopback, mutatedLoopback) === false,
    `原样比对=${storedLoopback === null ? '无对象' : bytesEqual(storedLoopback, loopbackNativeBytes)} 篡改后比对=${storedLoopback === null ? '无对象' : bytesEqual(storedLoopback, mutatedLoopback)}（应为 false）`
  )

  // ---- 原厂基线对照：把本插件整行 disabled（连 client 半区都不加载）----
  // 用于回答"关掉 addon 之后附件行为是不是**未修改的内置行为**"：如果这一页（根本没有本插件）
  // 与上面 enabled:false 页的附件行为一致，那么"回退到内置行为"就是实测结论，而不是推断。
  const stockOverlayPath = join(fixtureRoot, 'd23-stock-overlay.yml')
  await writeFile(
    stockOverlayPath,
    ['# D23 原厂基线覆盖层：本插件整行禁用（client 半区也不加载）', '- id: remote-attachments', '  disabled: true', ''].join('\n')
  )
  const stockDump = dumpConfig(fixtureProfile, stockOverlayPath)
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = null
  const stockLog = join(evidenceDir, 'service-d23-stock.log')
  service = await startFixtureService({ profile: fixtureProfile, patchPath: stockOverlayPath, logPath: stockLog, basePort: port })
  const stockContext = await openPageContext(chrome, { base, sessionId: SESSION, pairToken: null, deviceQuery: '' })
  openPages.push(stockContext.page)
  // 原厂页面上没有 addon 全局，因此不能用"addon 装好"判就绪；用 composer 与原生 input 就绪。
  const stockReady = await waitForComposer(stockContext.page, { timeoutMs: 90_000 })
  const stockGlobals = JSON.parse(await evaluate(stockContext.page, 'JSON.stringify({ addon: typeof globalThis.__DSH_ATTACHMENTS_ADDON__, bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__, receiver: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__, hook: typeof globalThis.__DSH_FILE_UPLOAD__, status: typeof globalThis.__DSH_ATTACHMENTS_STATUS__, cards: document.querySelectorAll("[data-composer-card]").length })'))
  const stockImport = await nativeImportIntoDraft(stockContext.page, 'stock', 4096)
  const stockNative = stockImport.native
  const stockNativeBytes = Buffer.from(stockNative.base64 ?? '', 'base64')
  const stockNativeSha = sha256hex(stockNativeBytes)
  const stockBuiltIn = await collectBuiltInUpload(stockNativeSha, { expected: stockNativeBytes, timeoutMs: 20_000 })
  const stockCardAfter = JSON.parse(await evaluate(stockContext.page, 'globalThis.__D23__.cardText()'))
  const stockSlot = stockImport.slot
  const stockPairing = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase6b-stock-baseline')
  observations.stockBaseline = {
    overlay: await readFile(stockOverlayPath, 'utf8'),
    composedDisablesRow: /remote-attachments/.test(stockDump.text) && /disabled: true/.test(stockDump.text),
    ready: stockReady.ok,
    globals: stockGlobals,
    nativeImport: { dispatched: stockNative.dispatched, name: stockNative.name, bytes: stockNative.bytes, sha256: stockNativeSha },
    builtIn: stockBuiltIn,
    cardAfter: stockCardAfter,
    slot: stockSlot,
    outcome: builtInOutcome(stockSlot, stockNative.name),
    importAttempts: stockImport.attempts,
    pairing: stockPairing
  }
  gate(
    'G11b-disabled-equals-stock-builtin',
    '原厂基线对照：把本插件整行禁用后页面上**根本没有本插件的任何全局**（addon/bridge/receiver/承载/状态座全缺），此时内置入口仍然把真实文件送进原生草稿，而上传腿的结果与 enabled:false 页面**完全一致**——因此"停用后回退到未修改的内置行为"是实测结论（本夹具的加固 LAN 部署下内置后台上传被上游拒绝，这正是本插件承载存在的理由；loopback 上内置上传则照常成功）',
    stockReady.ok === true &&
      stockGlobals.addon === 'undefined' &&
      stockGlobals.bridge === 'undefined' &&
      stockGlobals.receiver === 'undefined' &&
      stockGlobals.hook === 'undefined' &&
      stockGlobals.status === 'undefined' &&
      stockGlobals.cards === 1 &&
      stockNative.dispatched === true &&
      builtInOutcome(stockSlot, stockNative.name).attached === true &&
      builtInOutcome(stockSlot, stockNative.name).uploadFailed === builtInOutcome(disabledSlot, disabledNative.name).uploadFailed &&
      stockBuiltIn.ok === observations.disabledAddon.builtIn.ok &&
      stockPairing.heartbeat === 200 &&
      stockPairing.store.setHash === pairingAfterBuiltIn.store.setHash,
    `原厂页全局=${JSON.stringify(stockGlobals)} 草稿得文件=${builtInOutcome(stockSlot, stockNative.name).attached} 上传腿失败=${builtInOutcome(stockSlot, stockNative.name).uploadFailed}（停用页=${builtInOutcome(disabledSlot, disabledNative.name).uploadFailed}）主机库出现=${stockBuiltIn.ok}（停用页=${observations.disabledAddon.builtIn.ok}）附件位 titles=${JSON.stringify(stockSlot.titles)}`
  )

  // 停用范围：组合差异只落在本插件行；profile 清单与锁文件未被改写
  const deltaLines = [...observations.scope.composedDiff.onlyLeft, ...observations.scope.composedDiff.onlyRight]
  const allowedDeltaLine = (line) =>
    line.includes('remote-attachments') || line.trim() === 'config:' || line.trim() === 'enabled: false'
  const forbiddenDelta = deltaLines.filter((line) => /dsh-web-all|dsh-base|dsh-web-app|remote-web-ui|web-ui-/.test(line))
  const scopeOk =
    deltaLines.length > 0 &&
    deltaLines.every(allowedDeltaLine) &&
    forbiddenDelta.length === 0 &&
    deltaLines.some((line) => line.includes('patched by')) &&
    deltaLines.some((line) => line.trim() === 'enabled: false') &&
    sha256hex(await readFile(profileManifestPath)) === manifestShaBefore &&
    sha256hex(await readFile(lockfilePath)) === lockShaBefore
  gate(
    'G12a-disable-scope-is-addon-only',
    '停用范围独立：带覆盖层的组合配置与不带覆盖层逐行对比，差异只出现在本插件的行（外加 patch 来源注释与 enabled: false 两行），**没有任何一行涉及全家桶**；profile 的 package.json 与 pnpm-lock.yaml 逐字节未被改写——因此停用不需要、也没有动全家桶',
    scopeOk,
    `差异行=${JSON.stringify(deltaLines)} 涉及全家桶的行=${JSON.stringify(forbiddenDelta)} 清单未变=${sha256hex(await readFile(profileManifestPath)) === manifestShaBefore} 锁文件未变=${sha256hex(await readFile(lockfilePath)) === lockShaBefore}`
  )

  // ================= 阶段 7：remote 降级 =================
  console.log('\n阶段 7：remote 降级 profile（客户端通道层未安装）\n')
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = null
  const degradedInfo = existsSync(join(fixtureRoot, 'degraded-profile.json'))
    ? JSON.parse(await readFile(join(fixtureRoot, 'degraded-profile.json'), 'utf8'))
    : { profile: 'dsh-attachments-degraded', port: 3098 }
  const degradedBase = `http://${lanAddress}:${degradedInfo.port}`
  await ensureRemoteBaseline(degradedInfo.profile)
  const degradedInstrumentation = await instrumentPairingStop(degradedInfo.profile, dshHome)
  const degradedLog = join(evidenceDir, 'service-d23-degraded.log')
  service = await startFixtureService({ profile: degradedInfo.profile, patchPath: null, logPath: degradedLog, basePort: degradedInfo.port })
  const degradedContext = await openPageContext(chrome, { base: degradedBase, sessionId: SESSION, pairToken: null, deviceQuery: '' })
  openPages.push(degradedContext.page)
  await waitForPageReady(degradedContext, { timeoutMs: 60_000 })
  const pairingBeforeCarrier = await capturePairing(probeClient.client, degradedBase, probeClient.deviceId, 'phase7-before-degraded-carrier')
  const degradedInstall = JSON.parse(await evaluate(degradedContext.page, 'globalThis.__D23__.install()'))
  const degradedCapability = JSON.parse(await evaluate(degradedContext.page, 'globalThis.__D23__.capability()'))
  const degradedCarrier = JSON.parse(await evaluate(degradedContext.page, `globalThis.__D23__.carrierProbe(${JSON.stringify(SESSION)})`))
  await sleep(1500)
  const degradedCarrierRequests = degradedContext.requests.filter((entry) => entry.kind === 'request' && entry.url.includes('uploadFileBinary') && entry.url.includes('d23-carrier-probe.txt'))
  const pairingAfterCarrier = await capturePairing(probeClient.client, degradedBase, probeClient.deviceId, 'phase7-after-degraded-carrier')
  observations.degraded = {
    base: degradedBase,
    capability: degradedCapability,
    hookType: degradedInstall.hookType,
    seatType: degradedInstall.seatType,
    carrier: degradedCarrier,
    carrierRequests: degradedCarrierRequests.length,
    instrumentation: degradedInstrumentation,
    pairingBefore: pairingBeforeCarrier,
    pairingAfter: pairingAfterCarrier
  }
  gate(
    'G12b-remote-degraded-disables-without-fallback',
    'remote 降级（客户端通道层未安装）：能力 unavailable + capability-disabled（facts.remoteSeat=false），承载就地拒绝（capability-disabled）且**一个请求都不发**——不回退裸 /api；同一设备在降级服务上心跳 200、/remote 读会话 200，设备集合与本次操作前逐字段一致、PairingService.stop 调用为 0',
    degradedCapability?.status === 'unavailable' &&
      degradedCapability?.code === 'capability-disabled' &&
      degradedInstall.addonStatus?.capability?.facts?.remoteSeat === false &&
      degradedCarrier.thrown === true &&
      degradedCarrier.code === 'capability-disabled' &&
      degradedCarrierRequests.length === 0 &&
      pairingAfterCarrier.heartbeat === 200 &&
      pairingAfterCarrier.listStatus === 200 &&
      pairingAfterCarrier.store.count === pairingBeforeCarrier.store.count &&
      pairingAfterCarrier.store.setHash === pairingBeforeCarrier.store.setHash &&
      pairingAfterCarrier.stopCalls === 0,
    `能力=${degradedCapability?.status}/${degradedCapability?.code} 承载=${degradedCarrier.code} 请求数=${degradedCarrierRequests.length} 心跳=${pairingAfterCarrier.heartbeat} 会话列表=${pairingAfterCarrier.listStatus} 设备集合=${pairingBeforeCarrier.store.setHash}→${pairingAfterCarrier.store.setHash}`
  )

  // ================= 阶段 8：不支持的组合（模拟上游契约改名） =================
  console.log('\n阶段 8：不支持组合（夹具内模拟"全家桶更新后 seat 契约改名"，不安装任何新版本）\n')
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = null
  const rename = await applySeatRename(fixtureProfile)
  if (rename.ok !== true) throw new Error(`seat 改名模拟失败：${rename.detail}`)
  const unsupportedLog = join(evidenceDir, 'service-d23-unsupported.log')
  service = await startFixtureService({ profile: fixtureProfile, patchPath: null, logPath: unsupportedLog, basePort: port })
  const unsupportedContext = await openPageContext(chrome, { base, sessionId: SESSION, pairToken: null, deviceQuery: '' })
  openPages.push(unsupportedContext.page)
  await waitForPageReady(unsupportedContext, { timeoutMs: 60_000 })
  const pairingBeforeUnsupported = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase8-before-unsupported-carrier')
  const unsupportedInstall = JSON.parse(await evaluate(unsupportedContext.page, 'globalThis.__D23__.install()'))
  const unsupportedCapability = JSON.parse(await evaluate(unsupportedContext.page, 'globalThis.__D23__.capability()'))
  const unsupportedCarrier = JSON.parse(await evaluate(unsupportedContext.page, `globalThis.__D23__.carrierProbe(${JSON.stringify(SESSION)})`))
  await sleep(1500)
  const unsupportedCarrierRequests = unsupportedContext.requests.filter((entry) => entry.kind === 'request' && entry.url.includes('uploadFileBinary') && entry.url.includes('d23-carrier-probe.txt'))
  const unsupportedImport = await nativeImportIntoDraft(unsupportedContext.page, 'unsupported', 2048)
  const unsupportedNative = unsupportedImport.native
  const unsupportedNativeBytes = Buffer.from(unsupportedNative.base64 ?? '', 'base64')
  const unsupportedNativeSha = sha256hex(unsupportedNativeBytes)
  const unsupportedBuiltIn = await collectBuiltInUpload(unsupportedNativeSha, { expected: unsupportedNativeBytes, timeoutMs: 20_000 })
  const unsupportedCardAfter = JSON.parse(await evaluate(unsupportedContext.page, 'globalThis.__D23__.cardText()'))
  const unsupportedSlot = unsupportedImport.slot
  const pairingAfterUnsupported = await capturePairing(probeClient.client, base, probeClient.deviceId, 'phase8-after-unsupported-carrier')
  observations.unsupported = {
    simulation: rename.record,
    note: '夹具内确定性模拟：把已装 remote 包的 seat 全局名在 host 内联脚本与浏览器半区**一致改名**，模拟上游版本变更后内部契约改名；没有安装或更新任何包。',
    seat: { seatType: unsupportedInstall.seatType, seatRestore: unsupportedInstall.seatRestore, hookType: unsupportedInstall.hookType },
    capability: unsupportedCapability,
    carrier: unsupportedCarrier,
    carrierRequests: unsupportedCarrierRequests.length,
    builtIn: { dispatched: unsupportedNative.dispatched, bytes: unsupportedNative.bytes, sha256: unsupportedNativeSha, stored: unsupportedBuiltIn, cardAfter: unsupportedCardAfter, slot: unsupportedSlot, outcome: builtInOutcome(unsupportedSlot, unsupportedNative.name), importAttempts: unsupportedImport.attempts },
    pairingBefore: pairingBeforeUnsupported,
    pairingAfter: pairingAfterUnsupported
  }
  gate(
    'G10-unsupported-combination-fails-closed',
    '不支持的组合（上游契约改名）：本插件按旧契约探测不到改写层 ⇒ 能力 unavailable + capability-disabled，承载就地拒绝且零请求（绝不静默走裸 /api）；内置附件路径回到未修改的内置行为（文件进原生草稿、LAN 后台上传被上游拒绝，与原厂基线页同形）；配对/心跳/设备集合不受影响',
    unsupportedCapability?.status === 'unavailable' &&
      unsupportedCapability?.code === 'capability-disabled' &&
      unsupportedInstall.addonStatus?.capability?.facts?.remoteSeat === false &&
      unsupportedInstall.seatRestore === 'undefined' &&
      unsupportedCarrier.thrown === true &&
      unsupportedCarrier.code === 'capability-disabled' &&
      unsupportedCarrierRequests.length === 0 &&
      builtInOutcome(unsupportedSlot, unsupportedNative.name).attached === true &&
      builtInOutcome(unsupportedSlot, unsupportedNative.name).uploadFailed === true &&
      unsupportedBuiltIn.ok === false &&
      pairingAfterUnsupported.heartbeat === 200 &&
      pairingAfterUnsupported.listStatus === 200 &&
      pairingAfterUnsupported.store.count === pairingBeforeUnsupported.store.count &&
      pairingAfterUnsupported.store.setHash === pairingBeforeUnsupported.store.setHash,
    `能力=${unsupportedCapability?.status}/${unsupportedCapability?.code} seatRestore=${unsupportedInstall.seatRestore} 承载=${unsupportedCarrier.code} 请求数=${unsupportedCarrierRequests.length} 内置路径（LAN）：草稿得文件=${builtInOutcome(unsupportedSlot, unsupportedNative.name).attached} 上传腿被拒=${builtInOutcome(unsupportedSlot, unsupportedNative.name).uploadFailed}（与 G11b 原厂页同形）心跳=${pairingAfterUnsupported.heartbeat} 设备集合=${pairingBeforeUnsupported.store.setHash}→${pairingAfterUnsupported.store.setHash}`
  )
  // 还原 seat 契约（避免把模拟留给后续读数）
  await ensureRemoteBaseline(fixtureProfile)

  // ================= 阶段 9：配对设备丢失的负向控制（破坏性，放在最后） =================
  console.log('\n阶段 9：负向控制——真的丢设备时，上面的断言必须失败\n')
  // 负向控制需要第二个设备：现在才配对（设备表上限 4，主设备要留到最后）。
  const secondToken = await issueToken(port)
  const secondClient = await pairNode(base, secondToken)
  observations.negative.secondDevice = { paired: secondClient.deviceId !== '', distinct: secondClient.deviceId !== probeClient.deviceId }
  const storeBeforeRevoke = deviceStoreFingerprint()
  const revokeResponse = await fetch(`http://127.0.0.1:${port}/api/pair/revoke`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ deviceId: secondClient.deviceId })
  })
  const revokedHeartbeat = await heartbeat(secondClient.client, base)
  const revokedList = await rpc(secondClient.client, base, secondClient.deviceId, 'session/list', { _request: {} })
  const survivorHeartbeat = await heartbeat(probeClient.client, base)
  const storeAfterRevoke = deviceStoreFingerprint()
  observations.negative.revoke = {
    httpStatus: revokeResponse.status,
    revokedHeartbeat,
    revokedListStatus: revokedList.status,
    survivorHeartbeat,
    store: storeAfterRevoke
  }
  negative(
    'N09-device-probe-detects-a-lost-device',
    '反例（设备丢失可被判据发现）：撤销一个设备后，同一探针在同一服务上必须失败（心跳 ≠200、/remote 会话列表 ≠200），而另一个设备仍然 200；因此"配对设备还在"不是恒真断言',
    revokeResponse.status === 200 &&
      revokedHeartbeat !== 200 &&
      revokedList.status !== 200 &&
      survivorHeartbeat === 200 &&
      storeBeforeRevoke.count === 2 &&
      storeAfterRevoke.count === storeBeforeRevoke.count - 1,
    `撤销=${revokeResponse.status} 被撤设备 心跳=${revokedHeartbeat} 列表=${revokedList.status}；存活设备 心跳=${survivorHeartbeat}；设备数 ${storeBeforeRevoke.count}→${storeAfterRevoke.count}`
  )
  const stopBefore = pairingStopCalls()
  const pairStopResponse = await fetch(`http://127.0.0.1:${port}/api/pair/stop`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
  const stopAfter = pairingStopCalls()
  const afterStopHeartbeat = await heartbeat(probeClient.client, base)
  const afterStopList = await rpc(probeClient.client, base, probeClient.deviceId, 'session/list', { _request: {} })
  const storeAfterStop = deviceStoreFingerprint()
  observations.negative.pairingStop = {
    httpStatus: pairStopResponse.status,
    stopCallsBefore: stopBefore.calls,
    stopCallsAfter: stopAfter.calls,
    stopLogTail: stopAfter.tail,
    afterStopHeartbeat,
    afterStopListStatus: afterStopList.status,
    storeAfterStop
  }
  negative(
    'N10-pairing-service-stop-would-be-visible',
    '反例（本任务第二条验收点的负向控制）：真的调用 PairingService.stop 时，插桩记录必须出现、存活设备立刻失效（心跳 ≠200、/remote ≠200）、设备库清空——这正说明"停用 addon 时该调用为 0 且设备仍在"是有效判据，而不是插桩或探针不会响',
    pairStopResponse.status === 200 &&
      stopBefore.calls === 0 &&
      stopAfter.calls >= 1 &&
      afterStopHeartbeat !== 200 &&
      afterStopList.status !== 200 &&
      storeAfterStop.count === 0,
    `stop HTTP=${pairStopResponse.status} 调用记录 ${stopBefore.calls}→${stopAfter.calls} 之后心跳=${afterStopHeartbeat} 列表=${afterStopList.status} 设备数=${storeAfterStop.count}`
  )

  // ================= 阶段 10：版本策略产物与生产未触碰 =================
  console.log('\n阶段 10：兼容矩阵 / 版本策略文档 / 生产未触碰\n')
  const productionAfter = productionFingerprint()
  // 判据口径：**既有**清单文件一个字节都不能被我们改写（新增文件允许——用户自己的 DSH 进程
  // 在本轮期间会新建 sdk/sdk-minimal 等 profile，那不是本门禁的行为；本门禁所有 DSH_HOME
  // 都指向夹具私有目录）。新增文件如实记录，既有文件哈希漂移即失败。
  const changedExisting = Object.keys(productionBefore.files).filter(
    (key) => productionAfter.files[key] !== productionBefore.files[key]
  )
  const addedFiles = Object.keys(productionAfter.files).filter((key) => productionBefore.files[key] === undefined)
  const productionUntouched = changedExisting.length === 0
  observations.scope.production = {
    before: Object.keys(productionBefore.files).length,
    after: Object.keys(productionAfter.files).length,
    changedExisting,
    addedFiles: addedFiles.slice(0, 12),
    untouched: productionUntouched,
    note: '既有清单文件逐字节不变；新增文件来自用户自己的 DSH 运行时（本门禁只用夹具私有 DSH_HOME）。'
  }
  gate(
    'G13a-production-profile-untouched',
    '全程没有触碰生产 $HOME/.dsh 的 profile 清单层：开始时已存在的清单文件**逐字节哈希不变**（新增文件只如实记录，不算我们的行为）；本门禁的所有 DSH_HOME、pairing、安装与模拟都发生在夹具私有目录里',
    productionUntouched,
    `既有清单文件 ${Object.keys(productionBefore.files).length} 个，漂移 ${changedExisting.length} 个；本轮新增 ${addedFiles.length} 个（来自用户自己的 DSH 运行时）`
  )

  // 未验项：在写兼容矩阵之前登记（矩阵要引用它们）
  notRunItem('WindowsPackagingAndUpdate', 'Windows 实机上的 MSI/zip 安装、真实 WebView2 承载与 Launcher 更新流程', 'windows', 'WindowsPending：本机为 Linux，无 WebView2/Windows 安装器')
  notRunItem('RealAllBundleUpdate', '对真实 npm 源执行一次 @linxin666/dsh-web-all 更新并复跑本矩阵', 'all', '按要求不得对生产执行更新；夹具固定 0.3.20 且不引入未验证版本，故只从 lock/manifest 推理')
  notRunItem('UnverifiedUpstreamCombination', 'all/remote 0.3.21/0.3.22 + Harness 0.1.5-rc.2 组合的真实行为', 'all', '该组合未被选定（见 compatibility-lock.json 的 deviationFromResearchSnapshot），本机未安装')
  notRunItem('MarketplaceUpdateFlow', '客户端市场/插件管理器 UI 触发的更新路径', 'web', '夹具的 llm 基址为本地 stub，市场/更新入口不在夹具判据范围内')
  notRunItem('RealIndependentScopePluginUpdate', '通过 dsh plugin --profile 真实更新本插件（换一个 tarball 版本）', 'addon', '本轮不安装第二个插件版本；独立范围由组合配置差异 + profile 清单/锁文件未被改写证明')

  observations.pairingTrend = pairingTrend
  const trendCounts = pairingTrend.map((entry) => entry.store.count)
  const monotonic = trendCounts.every((count, index) => index === 0 || count >= trendCounts[index - 1])
  const trendOk =
    monotonic &&
    pairingTrend.length >= 6 &&
    pairingTrend.every((entry) => entry.heartbeat === 200 && entry.listStatus === 200 && entry.stopCalls === 0)
  gate(
    'G13b-pairing-trend-monotonic',
    '配对读数纵向一致：从"已验证组合"到"独立停用/降级/不支持组合"的每一次快照里，同一设备的心跳都是 200、/remote 读会话都是 200、PairingService.stop 调用记录恒为 0，且设备数从不减少（只在真实配对时增加）——停用与版本变化都不会丢设备',
    trendOk,
    `${pairingTrend.length} 次快照：设备数=${JSON.stringify(trendCounts)} 心跳=${JSON.stringify(pairingTrend.map((entry) => entry.heartbeat))} 列表=${JSON.stringify(pairingTrend.map((entry) => entry.listStatus))} stop=${JSON.stringify(pairingTrend.map((entry) => entry.stopCalls))}`
  )

  const matrix = {
    schemaVersion: 1,
    task: 'D23',
    purpose: 'compatibility-matrix',
    generatedAt: new Date().toISOString(),
    runId: RUN_ID,
    pinned: { source: rel(lockPath), ...lockVersions },
    observed: { source: `夹具 profile ${fixtureProfile} 的实际安装` + '（harness 由 dsh --version 读出）', harness: harnessVersion, all: installedVersions.all, remote: installedVersions.remote, addon: installedVersions.addon },
    rows: [
      {
        combination: 'verified',
        status: capabilityStatusOf(observations.verified.capability),
        code: observations.verified.capability?.code ?? null,
        reason: '依赖齐备（remote 改写层 + 草稿入口 + 承载为 ours）',
        pairingUnaffected: observations.verified.pairingAfter?.after?.heartbeat === 200 && observations.verified.pairingAfter?.after?.store?.setHash === observations.verified.pairingAfter?.before?.store?.setHash,
        heartbeatUnaffected: observations.verified.pairingAfter?.after?.heartbeat === 200,
        attachmentFallsBackToBuiltIn: false,
        attachmentWorksViaAddon: observations.verified.transfer?.upload?.ok === true,
        evidence: ['G01-verified-combination-manifest', 'G02-verified-capability-available', 'G03-verified-attachment-path', 'G04-pairing-and-device-set-untouched']
      },
      {
        combination: 'unknown-hook',
        status: capabilityStatusOf(observations.unknownHook.capability),
        code: observations.unknownHook.capability?.code ?? null,
        reason: '已存在未知 __DSH_FILE_UPLOAD__：保留原功能、绝不覆盖',
        pairingUnaffected: observations.unknownHook.heartbeat === 200,
        heartbeatUnaffected: observations.unknownHook.heartbeat === 200,
        attachmentFallsBackToBuiltIn: true,
        attachmentWorksViaAddon: false,
        builtInDetail: { note: '未知 hook 原样保留并继续承担内置上传；本插件不参与，也不覆盖它' },
        evidence: ['G09-unknown-hook-conflict-preserves-original', 'N05-unknown-hook-not-adopted']
      },
      {
        combination: 'remote-degraded',
        status: capabilityStatusOf(observations.degraded.capability),
        code: observations.degraded.capability?.code ?? null,
        reason: 'remote 客户端通道层不可用（facts.remoteSeat=false）',
        pairingUnaffected: observations.degraded.pairingAfter?.heartbeat === 200 && observations.degraded.pairingAfter?.store?.setHash === observations.degraded.pairingBefore?.store?.setHash,
        heartbeatUnaffected: observations.degraded.pairingAfter?.heartbeat === 200,
        attachmentFallsBackToBuiltIn: true,
        attachmentWorksViaAddon: false,
        builtInDetail: { note: '本插件承载就地拒绝且零请求；页面回到内置行为（不做裸 /api 的静默回退）' },
        evidence: ['G12b-remote-degraded-disables-without-fallback']
      },
      {
        combination: 'unsupported-version',
        status: capabilityStatusOf(observations.unsupported.capability),
        code: observations.unsupported.capability?.code ?? null,
        reason: `上游内部契约与固定版本不符（夹具内模拟 seat 改名：${seatV1} → ${seatV2}）；本插件 fail closed`,
        pairingUnaffected: observations.unsupported.pairingAfter?.heartbeat === 200 && observations.unsupported.pairingAfter?.store?.setHash === observations.unsupported.pairingBefore?.store?.setHash,
        heartbeatUnaffected: observations.unsupported.pairingAfter?.heartbeat === 200,
        attachmentFallsBackToBuiltIn: observations.unsupported.builtIn?.outcome?.attached === true,
        attachmentWorksViaAddon: false,
        builtInDetail: {
          draftAttached: observations.unsupported.builtIn?.outcome?.attached === true,
          uploadLegRefusedUpstream: observations.unsupported.builtIn?.outcome?.uploadFailed === true,
          note: '与原厂基线页同形：真实文件经 composer 原生入口进入草稿，LAN 后台裸 /api 上传被上游浏览器凭据/配对栅栏拒绝'
        },
        evidence: ['G10-unsupported-combination-fails-closed']
      },
      {
        combination: 'addon-disabled',
        status: capabilityStatusOf(observations.disabledAddon.capability),
        code: observations.disabledAddon.capability?.code ?? null,
        reason: `覆盖层只关本插件行（enabled=false）；LAN 页 unavailable（承载缺席）/ loopback 页 ${capabilityStatusOf(observations.disabledAddon.loopback?.capability)}，原因码同为 capability-disabled`,
        pairingUnaffected: observations.disabledAddon.pairingAfter?.heartbeat === 200 && observations.disabledAddon.pairingAfter?.store?.setHash === observations.disabledAddon.pairingBefore?.store?.setHash,
        heartbeatUnaffected: observations.disabledAddon.pairingAfter?.heartbeat === 200,
        attachmentFallsBackToBuiltIn: observations.disabledAddon.outcome?.attached === true,
        attachmentWorksViaAddon: false,
        builtInDetail: {
          draftAttached: observations.disabledAddon.outcome?.attached === true,
          uploadLegRefusedUpstream: observations.disabledAddon.outcome?.uploadFailed === true,
          loopbackUploadStored: observations.disabledAddon.loopback?.builtIn?.ok === true && observations.disabledAddon.loopback?.builtIn?.matches === true,
          stockBaselineMatches: observations.stockBaseline?.outcome?.attached === true && observations.stockBaseline?.outcome?.uploadFailed === observations.disabledAddon.outcome?.uploadFailed,
          note: '与原厂基线页（整行 disabled、页面上没有本插件任何全局）逐项一致；loopback 上内置上传照常落进主机内容寻址库且逐字节一致'
        },
        evidence: ['G11-independent-disable-scope-and-builtin-fallback', 'G12a-disable-scope-is-addon-only', 'N07-disabled-page-has-no-carrier-at-boot']
      }
    ],
    updatePolicy: {
      pinnedBy: rel(lockPath),
      userInitiatedAllUpdate: '用户主动更新全家桶只会改写 profile 的 @linxin666/dsh-web-all 解析（及其依赖），本插件的行、tarball 依赖与 cordis 行 id 都不在其中；更新后必须重新按本矩阵核对，未核对前一律按 unsupported-version 处理（fail closed）。本次未对任何来源执行更新（notRun）。',
      independentScopePluginUpdate: 'dsh plugin --profile <p> 转发给 profile 目录里的 pnpm，只影响被点名的那个依赖：更新本插件不会动全家桶；更新全家桶也不会动本插件行。停用/更新本插件都不需要改全家桶，反之亦然。',
      failureModeObserved: '当上游内部契约改名/缺失时，能力判定为 unavailable + capability-disabled，承载拒绝且零请求（不回退裸 /api），内置路径不受影响。'
    },
    notRun: notRun.map((entry) => entry.id),
    note: '每一行都来自本次运行的真实读数（页面能力判定、真实网络、主机内容寻址库、组合配置与设备库）；未执行的更新路径见 notRun。'
  }
  await writeFile(matrixPath, evidenceText(matrix))
  await writeFile(join(evidenceDir, 'd23-compatibility-matrix.json'), evidenceText(matrix))

  const policyDoc = await readFile(policyDocPath, 'utf8')
  const policyOk =
    policyDoc.includes(lockVersions.harness) &&
    policyDoc.includes(lockVersions.all) &&
    policyDoc.includes(lockVersions.remote) &&
    /WindowsPending/.test(policyDoc) &&
    /dsh plugin/.test(policyDoc)
  const matrixRows = matrix.rows
  const matrixConsistent =
    matrixRows.length === 5 &&
    matrixRows.every((row) => ['available', 'degraded', 'unavailable'].includes(row.status)) &&
    matrixRows.find((row) => row.combination === 'verified')?.status === 'available' &&
    matrixRows.find((row) => row.combination === 'unknown-hook')?.code === 'capability-conflict' &&
    matrixRows.filter((row) => ['remote-degraded', 'unsupported-version', 'addon-disabled'].includes(row.combination)).every((row) => row.code === 'capability-disabled') &&
    matrixRows.filter((row) => row.combination !== 'verified').every((row) => row.pairingUnaffected === true && row.heartbeatUnaffected === true)
  gate(
    'G13-version-policy-artifacts',
    '版本策略产物成立：兼容矩阵存在且矩阵每行的状态/原因码与本次实测结论一致（verified=available；unknown-hook=capability-conflict；其余=capability-disabled 且配对/心跳均不受影响），插件内策略文档存在并写明固定版本、WindowsPending 与独立 scoped 更新入口',
    policyOk && matrixConsistent && observations.scope.production.untouched === true,
    `文档=${rel(policyDocPath)} 命中固定版本=${policyOk} 矩阵 ${matrixRows.length} 行一致=${matrixConsistent} 矩阵=${rel(matrixPath)}`
  )

  // 未验项已在写矩阵前登记（见上）

  await writeFile(join(evidenceDir, 'd23-page-errors.json'), JSON.stringify({ verified: verified.pageErrors.slice(0, 5), unknownHook: unknownContext.pageErrors.slice(0, 5) }, null, 2))
} catch (error) {
  fatal = error
  gate('G00-runtime', '判据脚本自身跑完（无致命异常）', false, String(error?.stack ?? error).slice(0, 400))
} finally {
  for (const page of openPages) await page.close().catch(() => {})
  if (chrome !== null) await chrome.close().catch(() => {})
  if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid')).catch(() => {})
  // 夹具私有的局部改动一律还原（无论成败），避免把模拟/插桩留给后续读数。
  await ensureRemoteBaseline(fixtureProfile).catch(() => {})
}

function capabilityStatusOf(capability) {
  return capability === undefined || capability === null ? 'unknown' : String(capability.status)
}

// ---------- 汇总、写证据、清理 ----------

const failed = gates.filter((item) => item.ok !== true)
const failedNegative = negativeProbes.filter((item) => item.ok !== true)
const result = failed.length === 0 && failedNegative.length === 0 && fatal === null ? 'pass' : 'fail'

const gatesEvidence = {
  schemaVersion: 1,
  task: 'D23',
  purpose: 'compatibility-and-disable',
  runId: RUN_ID,
  startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
  finishedAt: new Date().toISOString(),
  durationMs: Date.now() - RUN_STARTED_AT_MS,
  command: RUN_COMMAND,
  platform: { platform: process.platform, arch: process.arch, node: process.version, dshBin: '<DSH_BIN>', cwd: '<REPO>' },
  gates,
  failedGateIds: failed.map((item) => item.id),
  negativeProbes,
  failedNegativeIds: failedNegative.map((item) => item.id),
  notRun,
  notRunIds: notRun.map((item) => item.id),
  compatibilityMatrix: `artifacts/verify-portable/d23-compatibility-matrix.json（runId 副本：artifacts/verify-portable/d23-runs/${RUN_ID}/d23-compatibility-matrix.json）`,
  policyDoc: rel(policyDocPath),
  evidenceLayout: {
    runDir: `artifacts/verify-portable/d23-runs/${RUN_ID}`,
    index: 'artifacts/verify-portable/d23-runs/index.json',
    report: `artifacts/verify-portable/d23-runs/${RUN_ID}/report.json`,
    latestMirror: 'artifacts/verify-portable/d23-compat-gates.json 与 artifacts/fixture/evidence/d23-compat-gates.json 都是**最新一次**运行的镜像；历史只认 runId 目录'
  },
  observations,
  result
}
const report = {
  ...gatesEvidence,
  method:
    '真实夹具（私有 DSH_HOME + 固定全家桶 + 真实 Chromium，LAN 普通 HTTP 非安全上下文）：能力判定读 __DSH_ATTACHMENTS_ADDON__.status()/capability()，附件通路走生产分块接收端 → 生产桥 → 原生草稿，上传走真实 /remote 门控与主机内容寻址库；停用/重载/重启分别实测；不支持组合用夹具内确定性模拟（seat 契约改名）并在结束后还原；PairingService.stop 用夹具私有插桩直接观察（负向控制会触发它）',
  note: '本检查证明"可独立停用 + 重载/重启后仍然正确 + 版本变化时诚实报告"；真实 Windows 安装/更新与未验证上游组合属 notRun。'
}

function evidenceText(value) {
  return sanitize(redact(JSON.stringify(value, null, 2))) + '\n'
}

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await mkdir(RUN_DIR, { recursive: true })
await writeFile(join(RUN_DIR, 'report.json'), evidenceText(report))
await writeFile(join(RUN_DIR, 'gates.json'), evidenceText(gatesEvidence))
if (existsSync(matrixPath)) await writeFile(join(RUN_DIR, 'd23-compatibility-matrix.json'), await readFile(matrixPath))
await writeFile(
  join(RUN_DIR, 'run.json'),
  evidenceText({
    runId: RUN_ID,
    task: 'D23',
    startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
    finishedAt: new Date().toISOString(),
    durationMs: Date.now() - RUN_STARTED_AT_MS,
    command: RUN_COMMAND,
    result,
    exitCode: result === 'pass' ? 0 : 1,
    gates: { total: gates.length, passed: gates.length - failed.length, failedGateIds: failed.map((item) => item.id) },
    negativeProbes: { total: negativeProbes.length, passed: negativeProbes.length - failedNegative.length, failedIds: failedNegative.map((item) => item.id) },
    notRunIds: notRun.map((item) => item.id),
    fatal: fatal === null ? null : sanitize(redact(String(fatal?.stack ?? fatal))).slice(0, 600)
  })
)
await writeFile(join(durableEvidenceDir, 'd23-compat-gates.json'), evidenceText(gatesEvidence))
await writeFile(join(evidenceDir, 'd23-compat-gates.json'), evidenceText(gatesEvidence))

const indexPath = join(RUNS_DIR, 'index.json')
const index = existsSync(indexPath) ? JSON.parse(await readFile(indexPath, 'utf8')) : { schemaVersion: 1, runs: [] }
if (index.runs.some((item) => item.runId === RUN_ID)) throw new Error(`runId 冲突（拒绝覆写历史）：${RUN_ID}`)
index.runs.push({
  runId: RUN_ID,
  startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
  finishedAt: new Date().toISOString(),
  durationMs: Date.now() - RUN_STARTED_AT_MS,
  result,
  gates: `${gates.length - failed.length}/${gates.length}`,
  failedGateIds: failed.map((item) => item.id),
  failedNegativeIds: failedNegative.map((item) => item.id),
  notRunIds: notRun.map((item) => item.id),
  command: RUN_COMMAND,
  report: `artifacts/verify-portable/d23-runs/${RUN_ID}/report.json`,
  matrix: 'artifacts/verify-portable/d23-compatibility-matrix.json',
  stdout: `artifacts/verify-portable/d23-runs/${RUN_ID}/stdout.log`
})
await writeFile(indexPath, evidenceText({ ...index, note: '只增不改：每次运行 push 一条；历史报告在各自的 runId 目录里，本文件与顶层 latest 镜像都不得作为历史依据。' }))

console.log(`\nD23 判据：${gates.length - failed.length}/${gates.length} 通过；负向探针 ${negativeProbes.length - failedNegative.length}/${negativeProbes.length} 成立；未验=${notRun.length}`)
if (fatal !== null) console.error(`致命异常：${sanitize(redact(String(fatal?.stack ?? fatal))).slice(0, 600)}`)
console.log(`[run] runId=${RUN_ID}`)
console.log(`[run] 报告=artifacts/verify-portable/d23-runs/${RUN_ID}/report.json（顶层镜像 d23-compat-gates.json）`)
console.log(`[run] 兼容矩阵=artifacts/verify-portable/d23-compatibility-matrix.json`)
if (failed.length > 0 || failedNegative.length > 0) {
  console.error('失败项：')
  for (const item of [...failed, ...failedNegative]) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
}

// 清理：只停夹具服务并删除夹具目录；证据已落盘，随后把夹具侧副本补回。
if (keepFixture === false) {
  const down = spawnSync('bash', [join(here, 'down.sh')], { cwd: repoRoot, encoding: 'utf8' })
  observations.cleanup.downSh = { status: down.status, tail: String(down.stdout ?? '').split('\n').slice(-3).join(' | ') }
} else {
  observations.cleanup.downSh = { status: null, tail: 'DSH_ATTACH_KEEP_FIXTURE=1：开发迭代时保留夹具' }
}
const leftovers = spawnSync('bash', ['-lc', "pgrep -af 'dsh-attachments-fixture|dsh-attachments-degraded|dsh-attachments-headless|dsh-cdp-' || true"], { encoding: 'utf8' })
observations.cleanup.leftoverProcesses = String(leftovers.stdout ?? '')
  .trim()
  .split('\n')
  .filter((line) => line.length > 0 && line.includes('pgrep') === false)
console.log(`清理：down.sh=${keepFixture ? 'skipped' : `exit=${observations.cleanup.downSh.status}`}；夹具残留进程=${observations.cleanup.leftoverProcesses.length}`)

const finalGates = evidenceText(gatesEvidence)
await mkdir(evidenceDir, { recursive: true })
await writeFile(join(evidenceDir, 'd23-compat-gates.json'), finalGates)
await writeFile(join(durableEvidenceDir, 'd23-compat-gates.json'), finalGates)

if (failed.length > 0 || failedNegative.length > 0 || fatal !== null) process.exitCode = 1
