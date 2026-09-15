#!/usr/bin/env node
/**
 * D22 判据：**交付包**（pack/*.tgz）在**干净的全家桶环境**里能跑 —— 证明"不是 source link 在干活"。
 *
 * 交付判据（全部可证伪，反例见 negativeProbes）：
 *   G01/G02 候选包身份：本轮构建树的字节就是包内字节（陈旧 pack/*.tgz 会被抓住）；
 *   G03/G04 干净环境：在夹具私有 DSH_HOME 里**新建**一个此前不存在的 profile，只装固定的
 *            @linxin666/dsh-web-all + 候选 tarball；lockfile 记录到候选包的 integrity/tarball；
 *            profile 内实际文件与包内文件逐字节相同，且不存在任何 source link；
 *   G05     不依赖夹具 profile 状态：D22 profile 是独立目录，夹具 profile 的已装产物前后不变；
 *   G06/G07 无额外 remote 实例：组合配置里 remote 行恰好一次（F02 技法），运行期只有本 profile
 *            的一个 dsh 进程，夹具/第二 target 端口没有被本轮拉起；
 *   G08/G09 **跑着的就是本轮 tarball**：服务返回的 HTML 必须逐字含**已安装 host 模块**现场生成的
 *            预启动承载脚本；合并 client bundle 里本包的段落必须逐字节等于包内 lib/client.js；
 *   G10-G14 代表性闭环（裁剪但真实）：分块传输 → 真 File → 生产草稿导入 → 原生 attachment id 独立回读
 *            → 真实上传 receipt → 真实发送 → 真实 read 工具逐字节读回；
 *   G15/G16 包卫生（复用 scripts/pack-scan.mjs，与 D03 test:pack 同一条扫描）+ manifest 脱敏。
 *
 * 证据布局（D19/D20/D21 同款，只增不改）：每次运行一个 artifacts/verify-portable/d22-runs/<runId>/，
 * 顶层 artifacts/verify-portable/d22-tarball-gates.json 与 artifacts/fixture/evidence/d22-tarball-gates.json
 * 都是**最新一次**运行的镜像；历史只认 runId 目录，index.json 只追加。
 *
 * 收尾：只停本次自己起的服务/stub/Chrome，并删除本次新建的 D22 profile（默认；--keep-profile 保留），
 * **不**调用 down.sh —— 那会删掉整个夹具目录，而夹具是共享的（setup.sh 需要数分钟）。
 * 传给 verify-portable.ps1 时由编排层在检查前后各自 down.sh/setup.sh，与本脚本无关。
 *
 * 用法：
 *   node tests/fixtures/d22-tarball-gates.mjs [--keep-profile]
 * 环境：
 *   DSH_D22_TARBALL=pack/xxx.tgz  指定候选包（默认 pack/ 下唯一的 .tgz）
 *   DSH_D22_PORT / DSH_D22_STUB_PORT / DSH_D22_PROFILE / DSH_ATTACH_FIXTURE_ROOT / DSH_BIN
 */

import { spawn, spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, statSync, lstatSync, realpathSync, writeFileSync } from 'node:fs'
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { connect as netConnect } from 'node:net'
import { release as osRelease } from 'node:os'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { createClient, findSecretLeaks, redact, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'
import { readTarball, scanTarEntries } from '../../scripts/pack-scan.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const packDir = join(pluginRoot, 'pack')
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')
const homeDir = process.env.HOME ?? ''

// 服务侧 llm-deepseek 读的是环境变量（profile patch 写的是 apiKeyEnv: DEEPSEEK_API_KEY）。
// 缺它时真实 Harness 会在发请求**之前**就失败（no API key），provider stub 收不到任何 agent 请求，
// 闭环会以"点了发送但 agentRequests=0"的形式失败。stub 不校验 key，固定一个占位值即可。
process.env.DEEPSEEK_API_KEY ??= 'stub-key'

const fixtureProfile = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const headlessProfile = process.env.DSH_ATTACH_HEADLESS_PROFILE ?? 'dsh-attachments-headless'
const profileName = process.env.DSH_D22_PROFILE ?? 'dsh-attachments-d22'
const port = Number(process.env.DSH_D22_PORT ?? 3096)
const stubPort = Number(process.env.DSH_D22_STUB_PORT ?? 3902)
const fixturePort = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const secondaryPort = Number(process.env.DSH_ATTACH_SECONDARY_PORT ?? 3097)
const keepProfile = process.argv.includes('--keep-profile')

const profileDir = join(dshHome, 'profiles', profileName)
const fixtureProfileDir = join(dshHome, 'profiles', fixtureProfile)
const headlessProfileDir = join(dshHome, 'profiles', headlessProfile)
const installedDir = join(profileDir, 'node_modules/@shxtmaker/dsh-remote-attachments')
const servicePidFile = join(fixtureRoot, 'service-d22.pid')
const serviceLog = join(fixtureRoot, 'evidence/d22-service.log')
const installLog = join(fixtureRoot, 'evidence/d22-install.log')

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
const sha256 = (buffer) => createHash('sha256').update(buffer).digest('hex')
const sha512of = (buffer) => `sha512-${createHash('sha512').update(buffer).digest('base64')}`
const rel = (value) => relative(repoRoot, value).split(sep).join('/')

// ---------- 运行身份 / 证据目录（每次运行独立 runId；历史只增不改） ----------

const RUN_STARTED_AT_MS = Date.now()
const RUN_ID = `d22-${new Date(RUN_STARTED_AT_MS).toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`
const RUN_COMMAND = `node ${process.argv[1] ?? 'tests/fixtures/d22-tarball-gates.mjs'}${process.argv.slice(2).length === 0 ? '' : ` ${process.argv.slice(2).join(' ')}`}`
const RUNS_DIR = join(durableEvidenceDir, 'd22-runs')
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
const observations = {}

function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 负向探针：ok=true 表示"反例确实失败了"，即被测判据不是恒真。 */
function negative(id, description, ok, detail = '') {
  negativeProbes.push({ id, description, ok, detail })
  console.log(`  [neg ${ok ? 'OK' : 'BAD'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

function markNotRun(id, description, reason) {
  notRun.push({ id, description, reason })
  console.log(`  [n/r] ${id} ${description} — ${reason}`)
}

// ---------- 路径与泄漏卫生（证据里不得出现绝对家目录 / repo 绝对路径） ----------

function sanitize(text) {
  let out = String(text)
  if (homeDir !== '') out = out.split(homeDir).join('<HOME>')
  out = out.split(repoRoot).join('<REPO>')
  out = out.split(fixtureRoot).join('artifacts/fixture')
  return out
}

const HOME_PATH_LEAK = /(?:^|[^<\w])\/(?:home|Users)\/[A-Za-z0-9._-]+\//
function findHomePathLeaks(text) {
  return HOME_PATH_LEAK.test(text) ? ['absolute-home-path'] : []
}

function leakReport(text) {
  return [...findSecretLeaks(text), ...findHomePathLeaks(text)]
}

// ---------- 进程与端口 ----------

/** `pgrep -af <pattern>`；过滤掉探针自身所在的 bash 行。 */
function pgrep(pattern) {
  const out = spawnSync('bash', ['-lc', `pgrep -af ${JSON.stringify(pattern)} || true`], { encoding: 'utf8' }).stdout
  return String(out ?? '')
    .trim()
    .split('\n')
    .filter((line) => line.length > 0 && line.includes('pgrep -af') === false && line.includes('d22-tarball-gates') === false)
}

/** 该 pid 的 DSH_HOME 是否就是本夹具的 DSH_HOME。 */
function pidDshHome(pid) {
  try {
    const env = readFileSync(`/proc/${pid}/environ`, 'utf8')
    const match = /(?:^|\0)DSH_HOME=([^\0]*)/.exec(env)
    return match === null ? null : match[1]
  } catch {
    return null
  }
}

function d22ServicePids() {
  const rows = pgrep(`profile ${profileName}`)
  return rows
    .map((line) => ({ pid: Number(line.split(' ')[0]), line }))
    .filter((row) => Number.isFinite(row.pid) && pidDshHome(row.pid) === dshHome)
}

function portAccepts(targetPort) {
  return new Promise((resolvePromise) => {
    const socket = netConnect({ host: '127.0.0.1', port: targetPort })
    const done = (value) => {
      socket.destroy()
      resolvePromise(value)
    }
    socket.on('connect', () => done(true))
    socket.on('error', () => done(false))
    socket.setTimeout(1500, () => done(false))
  })
}

async function httpJson(url, init) {
  const response = await fetch(url, init)
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

// ---------- 候选包（tarball）身份 ----------

const tarballPath = (() => {
  if (typeof process.env.DSH_D22_TARBALL === 'string' && process.env.DSH_D22_TARBALL !== '') return resolve(process.env.DSH_D22_TARBALL)
  const candidates = existsSync(packDir) ? readdirSync(packDir).filter((name) => name.endsWith('.tgz')) : []
  return candidates.length === 1 ? join(packDir, candidates[0]) : join(packDir, 'shxtmaker-dsh-remote-attachments-0.1.0.tgz')
})()

/** 构建树（pluginRoot/lib）逐文件哈希；用于证明"包内字节就是本轮构建的字节"。 */
function buildTreeHashes() {
  const root = join(pluginRoot, 'lib')
  const map = new Map()
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.isFile()) map.set(relative(root, full).split(sep).join('/'), sha256(readFileSync(full)))
    }
  }
  if (existsSync(root)) walk(root)
  return map
}

/**
 * 包内 lib/** 与当前构建树是否逐文件相同（纯函数，便于反例复用）。
 *
 * 发布面以 package.json 的 `files` 为准：`lib/**\/*.js`、`lib/**\/*.js.map`、`lib/**\/*.d.ts`
 * 三类**必须**在包里；其余构建产物（`.d.ts.map`、`lib/cordis.patch.yml`）不强求发布，
 * 但一旦在包里就必须与磁盘逐字节相同。返回 { ok, checked, mismatched[], missingInTarball[], extraInTarball[] }。
 */
function compareTarballToBuildTree(entries, treeHashes) {
  const tarballLib = new Map()
  for (const entry of entries) {
    if (!entry.isFile || !entry.path.startsWith('package/lib/')) continue
    tarballLib.set(entry.path.slice('package/lib/'.length), sha256(entry.content))
  }
  const required = new Set([...treeHashes.keys()].filter((path) => /\.(?:js|js\.map|d\.ts)$/.test(path)))
  const mismatched = []
  const missingInTarball = []
  for (const path of required) {
    if (!tarballLib.has(path)) missingInTarball.push(path)
    else if (tarballLib.get(path) !== treeHashes.get(path)) mismatched.push(path)
  }
  // 包里出现了磁盘上不存在的文件，或非必发文件与磁盘不一致，同样算陈旧产物。
  const extraInTarball = [...tarballLib.keys()].filter((path) => !treeHashes.has(path))
  for (const path of tarballLib.keys()) {
    if (!required.has(path) && treeHashes.has(path) && tarballLib.get(path) !== treeHashes.get(path)) mismatched.push(path)
  }
  const unpublishedOptional = [...treeHashes.keys()].filter((path) => !required.has(path) && !tarballLib.has(path))
  return {
    ok: mismatched.length === 0 && missingInTarball.length === 0 && extraInTarball.length === 0,
    checked: required.size,
    mismatched,
    missingInTarball,
    extraInTarball,
    unpublishedOptional
  }
}

/** 候选身份比对（纯函数）：观测到的 sha256/integrity 必须等于候选。 */
function identityMatches(candidate, observed) {
  const reasons = []
  if (!observed || observed.sha256 !== candidate.sha256) reasons.push(`sha256=${observed?.sha256 ?? '(缺失)'} 期望=${candidate.sha256}`)
  if (!observed || observed.integrity !== candidate.integrity) reasons.push(`integrity=${observed?.integrity ?? '(缺失)'} 期望=${candidate.integrity}`)
  return { ok: reasons.length === 0, reasons }
}

/** 解析 pnpm-lock.yaml 里本插件的解析记录（integrity/tarball/version）与 importer specifier。 */
function parsePluginLockEntry(lockText) {
  const lines = lockText.split('\n')
  let entry = null
  let specifier = null
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index]
    const spec = /^\s+specifier:\s*(file:\S+)/.exec(line)
    if (spec !== null && specifier === null) specifier = spec[1]
    if (!/^\s{2}'?@shxtmaker\/dsh-remote-attachments@/.test(line)) continue
    const block = lines.slice(index, index + 4).join('\n')
    const integrity = /integrity:\s*([^,\s}]+)/.exec(block)?.[1] ?? null
    const tarball = /tarball:\s*([^,}\s]+)/.exec(block)?.[1] ?? null
    const version = /^\s+version:\s*(\S+)\s*$/m.exec(block)?.[1] ?? null
    if (integrity !== null && entry === null) entry = { key: line.trim(), integrity, tarball, version }
  }
  return { entry, specifier, hasLink: /@shxtmaker\/dsh-remote-attachments@link:/.test(lockText) }
}

/** 已安装的插件文件与包内文件逐字节比对（纯函数）。 */
function compareInstalledToTarball(entries, installedRoot) {
  const mismatched = []
  const missing = []
  let checked = 0
  for (const entry of entries) {
    if (!entry.isFile) continue
    const relativePath = entry.path.replace(/^package\//, '')
    const target = join(installedRoot, relativePath)
    if (!existsSync(target)) {
      missing.push(relativePath)
      continue
    }
    checked += 1
    if (sha256(readFileSync(target)) !== sha256(entry.content)) mismatched.push(relativePath)
  }
  const published = new Set(entries.filter((entry) => entry.isFile).map((entry) => entry.path.replace(/^package\//, '')))
  const extra = []
  const walk = (dir, prefix) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name)
      const path = prefix === '' ? entry.name : `${prefix}/${entry.name}`
      if (entry.isDirectory()) walk(full, path)
      else if (entry.isFile() && !published.has(path)) extra.push(path)
    }
  }
  if (existsSync(installedRoot)) walk(installedRoot, '')
  return { ok: mismatched.length === 0 && missing.length === 0 && extra.length === 0, checked, mismatched, missing, extra }
}

/** 目录树里是否存在指向仓库插件源码/构建目录的符号链接（source link 的形态）。 */
function findSourceLinks(root) {
  const hits = []
  if (!existsSync(root)) return hits
  const pluginReal = realpathSync(pluginRoot)
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name)
      let stat = null
      try {
        stat = lstatSync(full)
      } catch {
        continue
      }
      if (stat.isSymbolicLink()) {
        let target = null
        try {
          target = realpathSync(full)
        } catch {
          target = null
        }
        if (target !== null && (target === pluginReal || target.startsWith(pluginReal + sep))) hits.push(`${rel(full)} -> ${rel(target)}`)
        continue
      }
      if (stat.isDirectory()) walk(full)
    }
  }
  walk(root)
  return hits
}

// ---------- 页面侧装置 ----------

const SEND_LABELS = ['发送消息', 'Send message', '排队发送', 'Queue message', '插话发送', 'Steer message']
const STOP_LABELS = ['停止生成', 'Stop generating']

/** 页面侧：读 composer 卡片 + 桥/接收端/承载形状（与 D09/D21 同款句柄）。 */
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
    stored: (() => { try { return JSON.parse(localStorage.getItem('dsh.sessions.current') ?? 'null') } catch { return null } })(),
    bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
    bridgeVersion: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.version ?? null,
    currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
    hook: typeof globalThis.__DSH_FILE_UPLOAD__,
    hookFetch: typeof globalThis.__DSH_FILE_UPLOAD__?.fetch,
    receiver: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__,
    receiverVersion: globalThis.__DSH_ATTACHMENTS_RECEIVER__?.version ?? null,
    receiverClientBuild: globalThis.__DSH_ATTACHMENTS_RECEIVER__?.wiring?.clientBuild ?? null
  })
})()`

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

/** 页面侧：用生产桥导入合成 File（base64 或文本），返回 ok/added/previous/code。 */
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

/**
 * 页面侧：用**生产接收端**（__DSH_ATTACHMENTS_RECEIVER__.create()，草稿导入函数由组合层注入）
 * 跑一次真实分块批次：context → batch-begin → file-begin → chunk×N → file-end，
 * 每步 drain ack / import-result。返回逐块结果、fileRecord 与 import-result。
 */
function receiverBatchProbe(spec) {
  return `(async () => {
    const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
    if (host === undefined || host === null) return JSON.stringify({ ok: false, reason: 'no-receiver-host' })
    const spec = ${JSON.stringify(spec)}
    const bytes = Uint8Array.from(atob(spec.dataBase64), (c) => c.charCodeAt(0))
    const receiver = host.create()
    const stats = { ok: true, chunks: [], results: [], acks: 0, importResults: 0, importResult: null }
    const drain = async () => {
      for (const message of await receiver.drainOutgoing()) {
        if (message.type === 'ack') stats.acks += 1
        if (message.type === 'import-result') {
          stats.importResults += 1
          stats.importResult = { status: message.status, code: message.code ?? null, attachmentIds: Array.from(message.attachmentIds ?? []) }
        }
      }
    }
    const send = async (object) => {
      const result = await receiver.acceptText(JSON.stringify(object))
      stats.results.push({ type: object.type, ok: result.ok === true, code: result.ok === true ? null : result.code })
      return result
    }
    await send({ v: 1, type: 'context', sessionId: spec.sessionId, targetId: spec.targetId, documentEpoch: 1, composerEpoch: 1, composerScope: 'd22-clean-profile' })
    await drain()
    const begin = await send({ v: 1, type: 'batch-begin', sessionId: spec.sessionId, batchId: spec.batchId, targetId: spec.targetId, documentEpoch: 1, composerEpoch: 1, fileCount: 1, totalBytes: bytes.length })
    if (begin.ok !== true) return JSON.stringify({ ...stats, ok: false, reason: 'batch-begin:' + String(begin.code) })
    const started = await send({ v: 1, type: 'file-begin', sessionId: spec.sessionId, batchId: spec.batchId, fileId: spec.fileId, name: spec.name, mime: spec.mime, byteLength: bytes.length, sha256: spec.sha256 })
    if (started.ok !== true) return JSON.stringify({ ...stats, ok: false, reason: 'file-begin:' + String(started.code) })
    let offset = 0
    let seq = 0
    while (offset < bytes.length) {
      const size = Math.min(spec.chunkBytes, bytes.length - offset)
      const slice = bytes.subarray(offset, offset + size)
      let binary = ''
      for (let index = 0; index < slice.length; index += 0x8000) binary += String.fromCharCode.apply(null, slice.subarray(index, index + 0x8000))
      const result = await send({ v: 1, type: 'chunk', sessionId: spec.sessionId, batchId: spec.batchId, fileId: spec.fileId, seq, offset, byteLength: size, dataBase64: btoa(binary) })
      stats.chunks.push({ seq, offset, byteLength: size, ok: result.ok === true, code: result.ok === true ? null : result.code })
      await drain()
      offset += size
      seq += 1
    }
    const ended = await send({ v: 1, type: 'file-end', sessionId: spec.sessionId, batchId: spec.batchId, fileId: spec.fileId, totalBytes: bytes.length, sha256: spec.sha256 })
    await drain()
    const record = receiver.fileRecord(spec.fileId)
    stats.fileEnd = { ok: ended.ok === true, code: ended.ok === true ? null : ended.code }
    stats.record = record === null ? null : {
      transport: record.transport,
      draft: record.draft,
      upload: record.upload,
      receivedBytes: record.receivedBytes,
      ackedBytes: record.ackedBytes,
      ended: record.ended,
      submittedItems: record.submittedItems,
      attachmentIds: Array.from(record.attachmentIds)
    }
    stats.stagedFileIds = Array.from(receiver.stagedFileIds ?? [])
    stats.accounting = receiver.accounting
    stats.fileBytes = bytes.length
    return JSON.stringify(stats)
  })()`
}

// ---------- provider stub 控制（D09/D21 同款） ----------

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

const stubSummary = async () => (await fetch(`http://127.0.0.1:${stubPort}/__stub/summary`)).json()

async function poll(read, accept, { timeoutMs, intervalMs = 500 }) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await read()
    if (accept(value)) return { ok: true, value }
    if (Date.now() >= deadline) return { ok: false, value }
    await sleep(intervalMs)
  }
}

// ---------- 判定函数（判据与负向探针共用） ----------

/** 工具结果里剥离行号后的正文（D09 同款）。 */
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

function containsImportedText(resultText, sourceText) {
  return reconstructReadText(resultText) === sourceText
}

function receiptConsistent(receipt, sourceBytes) {
  const value = receipt?.value
  if (receipt?.ok !== true || typeof value !== 'object' || value === null) return { ok: false, detail: 'receipt.ok !== true' }
  if (typeof value.receiptId !== 'string' || value.receiptId.length === 0) return { ok: false, detail: 'receiptId 缺失' }
  const file = value.file
  if (typeof file?.bytes !== 'number' || file.bytes !== sourceBytes.length) return { ok: false, detail: `bytes=${file?.bytes} 期望=${sourceBytes.length}` }
  const expected = `sha256:${sha256(sourceBytes)}`
  if (file.attachmentId !== expected) return { ok: false, detail: `attachmentId=${file.attachmentId} 期望=${expected}` }
  return { ok: true, detail: `receiptId=…${String(value.receiptId).slice(-8)} attachmentId=${file.attachmentId} bytes=${file.bytes}` }
}

/** 与 harness dsh-client-modules 的 comboSource() 同式：去 sourceURL/sourceMappingURL 尾注，补齐尾换行。 */
function prepareServedSource(clientText) {
  const SOURCE_MAP_TRAILER = /(?:\r?\n)?\/\/# sourceMappingURL=[^\r\n]*(?:\r?\n)?$/
  const SOURCE_URL_TRAILER = /(?:\r?\n)?\/\/# sourceURL=([^\r\n]+)(?:\r?\n)?$/
  let source = clientText.replace(SOURCE_URL_TRAILER, '').replace(SOURCE_MAP_TRAILER, '')
  if (!source.endsWith('\n')) source += '\n'
  return source
}

/**
 * 合并 combo bundle 里是否存在**完整**的本包 client.js 字节。
 *
 * combo 的拼接单位就是 `${preparedSource};\n`（见 dsh-client-modules 的 comboSource/buildCombo），
 * 因此判据是：候选 client.js 的字节在 bundle 里出现**恰好一次**，且紧邻的边界正好是 combo 接缝
 * （前一个字符是换行，后两个字符是 ";\n"）。
 *
 * 为什么不用"取两个 `window.__ModuleLoader__.load({` 之间的段落"：本包 client.js 自己的注释里
 * 就出现过这个前缀（实测），按前缀切片会把段落截短 8.5 KB，从而把"字节相同"误判成不同。
 * 纯函数，供反例复用。
 */
function servedClientOccurrence(bundle, expectedSource) {
  const index = bundle.indexOf(expectedSource)
  if (index < 0) return { ok: false, reason: 'served-bundle-missing-candidate-client-bytes', occurrences: 0, index: -1 }
  const occurrences = bundle.split(expectedSource).length - 1
  const before = index === 0 ? '' : bundle[index - 1]
  const after = bundle.slice(index + expectedSource.length, index + expectedSource.length + 2)
  const atBoundary = (index === 0 || before === '\n') && after === ';\n'
  return {
    ok: occurrences === 1 && atBoundary,
    reason: occurrences !== 1 ? `occurrences=${occurrences}` : atBoundary ? 'exact' : `boundary before=${JSON.stringify(before)} after=${JSON.stringify(after)}`,
    occurrences,
    index,
    atBoundary
  }
}

/** 服务返回的 HTML 里是否逐字含已安装 host 模块现场生成的预启动承载脚本。 */
function servedHostScriptMatches(html, expectedScript) {
  if (html.includes(expectedScript)) return { ok: true, mode: 'raw' }
  const escaped = expectedScript
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
  if (html.includes(escaped)) return { ok: true, mode: 'html-escaped' }
  return { ok: false, mode: 'none' }
}

/** manifest 脱敏判定：文本必须零敏感形态，且同一判定对注入了 canary 的文本必须命中。 */
function redactionCheck(text) {
  const leaks = leakReport(text)
  const canaryLeaks = leakReport(`${text}\n{"pair":"pair=ABCDEFGH12345678"}\n{"path":"${homeDir}/.dsh/profiles/web"}`)
  return { ok: leaks.length === 0, leaks, canaryFires: canaryLeaks.length > 0, canaryLeaks }
}

// =====================================================================
// 主流程
// =====================================================================

let service = null
let stub = null
let chrome = null
const openPages = []
let fatal = null
let deviceId = ''
let client = null
let lanBase = ''
let sessionId = null
let candidate = null
let entries = []
let manifestText = ''
let manifestObject = null
let installReceipt = null
let fixtureProfileMarker = null

const fixtureMarker = (dir) => {
  try {
    return { packageJson: sha256(readFileSync(join(dir, 'package.json'))), client: sha256(readFileSync(join(dir, 'node_modules/@shxtmaker/dsh-remote-attachments/lib/client.js'))) }
  } catch {
    return null
  }
}

const productionProfileDir = join(homeDir, '.dsh/profiles/web')
const productionMarker = () => {
  try {
    return sha256(readFileSync(join(productionProfileDir, 'package.json')))
  } catch {
    return null
  }
}

const loopMarker = `D22-TARBALL-MARKER-${RUN_ID.slice(-8)}`
const NON_ASCII_LINE = '第二行：中文内容与 emoji 🧪'
const payloadLines = [loopMarker, NON_ASCII_LINE]
for (let index = 1; index <= 120; index += 1) payloadLines.push(`D22-LINE-${String(index).padStart(4, '0')} ${'x'.repeat(80)}`)
const payloadText = `${payloadLines.join('\n')}\n`
const payloadBytes = Buffer.from(payloadText, 'utf8')
const payloadSha256 = sha256(payloadBytes)
const payloadChunkBytes = 4096

/**
 * stub 的 read 步骤占位符：按**正文自己的内容寻址 digest** 定位 handle。
 *
 * 为什么锚 digest 而不是文件名：原生 id 回读探针会再导入一个小文件，两条 handle 可能出现在同一段
 * 文本里；只按文件名 + `[^\n]*saved at` 会把**探针**的暂存路径捕获进来（实测 stub 因此重放同一
 * 工具调用上千次，读回判定永远不满足）。digest 由正文字节决定，只可能命中正文那一条 handle。
 * 该占位符的语义（含"取最后一个匹配"）在阶段 A 用合成 handle 做正/反自检，不依赖夹具。
 */
const readStepPath = `{{prompt-last:sha256:${payloadSha256.slice(0, 8)}\\): verbatim read-only copy saved at "([^"\\n]*/attachments/v1/files/[^"\\n]*)"}}`

/** 复刻 provider stub 的占位符解析（整串占位符 + 取最后一个匹配 + 捕获组 1 优先）。 */
function placeholderSelfCheck(placeholder, handleText) {
  const match = /^\{\{prompt-last:([\s\S]*)\}\}$/.exec(placeholder)
  if (match === null) return { ok: false, reason: 'not-a-placeholder' }
  const pattern = new RegExp(match[1], 'g')
  let last = null
  for (let found = pattern.exec(handleText); found !== null; found = pattern.exec(handleText)) last = found
  return last === null ? { ok: false, reason: 'no-match' } : { ok: true, value: last[1] ?? last[0] }
}

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await mkdir(join(fixtureRoot, 'd22'), { recursive: true })

const productionBefore = productionMarker()

try {
  console.log(`D22 交付包干净安装判据 runId=${RUN_ID}\n`)

  // ================= A. 候选包身份 =================
  console.log('阶段 A：候选 tarball 身份（包内字节必须就是本轮构建树的字节）\n')
  if (!existsSync(tarballPath)) throw new Error(`缺少候选 tarball：${tarballPath}（请先 pnpm run build && pnpm run test:pack）`)
  const tarballBytes = readFileSync(tarballPath)
  entries = await readTarball(tarballPath)
  const packCandidates = readdirSync(packDir).filter((name) => name.endsWith('.tgz'))
  const manifestEntry = entries.find((entry) => entry.path === 'package/package.json')
  const tarballManifest = manifestEntry === undefined ? null : JSON.parse(manifestEntry.content.toString('utf8'))
  candidate = {
    tarball: rel(tarballPath),
    name: tarballManifest?.name ?? null,
    pluginVersion: tarballManifest?.version ?? null,
    bytes: tarballBytes.length,
    fileCount: entries.filter((entry) => entry.isFile).length,
    sha256: sha256(tarballBytes),
    integrity: sha512of(tarballBytes),
    builtAt: statSync(tarballPath).mtime.toISOString()
  }
  observations.candidate = candidate
  gate(
    'G01-candidate-tarball',
    '候选包唯一、身份正确、双面入口与类型声明都在包内',
    packCandidates.length === 1 &&
      candidate.name === '@shxtmaker/dsh-remote-attachments' &&
      candidate.pluginVersion === '0.1.0' &&
      entries.some((entry) => entry.path === 'package/lib/host.js' && entry.isFile && entry.size > 0) &&
      entries.some((entry) => entry.path === 'package/lib/client.js' && entry.isFile && entry.size > 0),
    `tgz=${packCandidates.join(',')} sha256=${candidate.sha256.slice(0, 16)} bytes=${candidate.bytes} files=${candidate.fileCount} version=${candidate.pluginVersion}`
  )

  const buildTree = buildTreeHashes()
  const buildCompare = compareTarballToBuildTree(entries, buildTree)
  observations.buildTree = { fileCount: buildTree.size, compare: buildCompare }
  gate(
    'G02-tarball-is-this-build',
    '包内 lib/** 与当前构建树逐文件相同（陈旧 pack/*.tgz 会被抓出）',
    buildCompare.ok,
    `checked=${buildCompare.checked} mismatched=${JSON.stringify(buildCompare.mismatched.slice(0, 3))} missing=${JSON.stringify(buildCompare.missingInTarball.slice(0, 3))} extra=${JSON.stringify(buildCompare.extraInTarball.slice(0, 3))}`
  )
  negative(
    'N01-stale-lib-is-caught',
    '反例：把包内 client.js 改一个字节（模拟陈旧 lib/ 打出的包），G02 的判定必须失败',
    (() => {
      const mutated = entries.filter((entry) => entry.isFile).map((entry) => ({
        path: entry.path,
        isFile: true,
        content: entry.path === 'package/lib/client.js' ? Buffer.concat([entry.content, Buffer.from('/*stale*/')]) : entry.content
      }))
      return compareTarballToBuildTree(mutated, buildTree).ok === false
    })(),
    '包内 client.js +"/*stale*/" → compareTarballToBuildTree().ok === false'
  )
  negative(
    'N02-stale-pack-identity-is-caught',
    '反例：观测到的 sha256/integrity 与候选不符（指向旧构建产物）时，身份判定必须失败',
    identityMatches(candidate, { sha256: 'deadbeef'.repeat(8), integrity: 'sha512-stale' }).ok === false &&
      identityMatches(candidate, { sha256: candidate.sha256, integrity: candidate.integrity }).ok === true,
    'identityMatches(stale) === false 且 identityMatches(candidate) === true'
  )

  // 闭环读回步骤的占位符自检（不依赖夹具）：必须命中**正文**那条 handle，绝不能命中探针那条。
  const syntheticPayloadHandle = `[File "d22-clean-profile-note.txt" (${payloadBytes.length} bytes, sha256:${payloadSha256.slice(0, 8)}): verbatim read-only copy saved at "<REPO>/artifacts/fixture/dsh-home/attachments/v1/files/58/5832082763/d22-clean-profile-note.txt". Read that path with your file tools when its contents are needed.]`
  const syntheticProbeHandle = '[File "d22-native-id-probe.txt" (19 bytes, sha256:aaaaaaa1): verbatim read-only copy saved at "<REPO>/artifacts/fixture/dsh-home/attachments/v1/files/aa/aaaaaaa1/d22-native-id-probe.txt". Read that path with your file tools when its contents are needed.]'
  const payloadPlaceholder = placeholderSelfCheck(readStepPath, `${syntheticProbeHandle}\n${syntheticPayloadHandle}`)
  const probeOnlyPlaceholder = placeholderSelfCheck(readStepPath, syntheticProbeHandle)
  observations.loopMechanism = { placeholderResolvedTo: payloadPlaceholder.value ?? null, probeOnlyResolved: probeOnlyPlaceholder.ok }
  gate(
    'G02b-readback-step-targets-payload',
    'stub 的 read 步骤占位符按内容寻址 digest 解析到**正文**的暂存路径（机制自检，不依赖夹具）',
    payloadPlaceholder.ok === true && String(payloadPlaceholder.value).endsWith('d22-clean-profile-note.txt'),
    `解析结果=${payloadPlaceholder.value ?? `(未命中：${payloadPlaceholder.reason})`}`
  )
  negative(
    'N02b-readback-step-ignores-probe',
    '反例：只有探针 handle 时同一占位符必须**不**命中（否则读回会读探针，expect 永不满足）',
    probeOnlyPlaceholder.ok === false,
    `只给探针 handle → ${probeOnlyPlaceholder.ok ? '命中了（错误）' : `未命中（${probeOnlyPlaceholder.reason}）`}`
  )

  // ================= B. 夹具前置条件 =================
  console.log('\n阶段 B：夹具与隔离前置条件\n')
  if (!existsSync(join(dshHome, 'profiles', fixtureProfile, 'package.json'))) {
    throw new Error(`夹具 profile 不存在：${fixtureProfileDir}（请先运行 tests/fixtures/setup.sh）`)
  }
  if (!existsSync(join(headlessProfileDir, 'package.json'))) {
    throw new Error(`无头 profile 不存在：${headlessProfileDir}（请先运行 tests/fixtures/setup.sh）`)
  }
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error('缺少夹具 lan-address.txt（请先运行 tests/fixtures/setup.sh）')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  lanBase = `http://${lanAddress}:${port}`
  const d22ExistedBefore = existsSync(profileDir)
  fixtureProfileMarker = fixtureMarker(fixtureProfileDir)
  observations.preconditions = {
    fixtureRoot: rel(fixtureRoot),
    dshHomeIsPrivate: dshHome.startsWith(join(repoRoot, 'artifacts') + sep),
    profileExistedBefore: d22ExistedBefore,
    fixtureProfileMarker,
    productionProfileTouchedBefore: productionBefore
  }
  gate(
    'G03-clean-slot',
    'D22 profile 槽位此前不存在，且夹具 DSH_HOME 位于仓库 artifacts/ 之下（不是生产 ~/.dsh）',
    d22ExistedBefore === false && observations.preconditions.dshHomeIsPrivate && !dshHome.startsWith(join(homeDir, '.dsh')),
    `profileDir=${rel(profileDir)} 已存在=${d22ExistedBefore} privateHome=${observations.preconditions.dshHomeIsPrivate}`
  )

  // ================= C. 干净安装 =================
  console.log('\n阶段 C：干净安装（新 profile + 固定全家桶 + 候选 tarball）\n')
  const install = spawnSync('bash', [join(here, 'd22-clean-install.sh')], {
    cwd: repoRoot,
    encoding: 'utf8',
    timeout: 900_000,
    env: {
      ...process.env,
      DSH_ATTACH_FIXTURE_ROOT: fixtureRoot,
      DSH_D22_PROFILE: profileName,
      DSH_D22_PORT: String(port),
      DSH_D22_STUB_PORT: String(stubPort),
      DSH_D22_TARBALL: tarballPath,
      DSH_BIN: dshBin
    }
  })
  await writeFile(installLog, sanitize(`${install.stdout ?? ''}${install.stderr ?? ''}`))
  if (install.status !== 0) {
    throw new Error(`d22-clean-install.sh 退出码 ${install.status ?? '(未退出)'}${install.error === undefined ? '' : `：${install.error.message}`}；日志见 ${rel(installLog)}`)
  }
  installReceipt = JSON.parse(await readFile(join(evidenceDir, 'd22-clean-install.json'), 'utf8'))
  observations.installReceipt = installReceipt

  const profileManifest = JSON.parse(await readFile(join(profileDir, 'package.json'), 'utf8'))
  const createdAfterRun = ['package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'cordis.patch.yml', 'node_modules']
    .filter((name) => existsSync(join(profileDir, name)))
    .every((name) => statSync(join(profileDir, name)).mtimeMs >= RUN_STARTED_AT_MS - 2000)
  const bundleVersions = {}
  const cliNodeModules = join(dirname(dirname(dshBin)), 'lib/node_modules/@deepseek-ai/dsh/node_modules/@deepseek-ai')
  for (const name of ['@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app', '@linxin666/dsh-web-all', '@linxin666/dsh-remote-web-ui', '@shxtmaker/dsh-remote-attachments']) {
    const profilePath = join(profileDir, 'node_modules', name, 'package.json')
    // CLI 的 node_modules 根已经带 @deepseek-ai 作用域，这里要按**去作用域后的目录名**拼接。
    const cliPath = join(cliNodeModules, name.replace(/^@[^/]+\//, ''), 'package.json')
    if (existsSync(profilePath)) bundleVersions[name] = { version: JSON.parse(await readFile(profilePath, 'utf8')).version, source: 'profile' }
    else if (existsSync(cliPath)) bundleVersions[name] = { version: JSON.parse(await readFile(cliPath, 'utf8')).version, source: 'cli' }
    else bundleVersions[name] = { version: null, source: null }
  }
  const compatibilityLock = JSON.parse(await readFile(join(pluginRoot, 'tests/fixtures/compatibility-lock.json'), 'utf8'))
  const lockedAll = compatibilityLock.packages.find((item) => item.name === '@linxin666/dsh-web-all')
  const declaredBundles = profileManifest?.dsh?.profile?.bundles ?? []
  observations.cleanProfile = { profileManifestBundles: declaredBundles, bundleVersions, createdAfterRun, specifier: profileManifest?.dependencies?.['@shxtmaker/dsh-remote-attachments'] ?? null }
  gate(
    'G04-fresh-profile-from-template',
    '第二 profile 本轮从 web 模板新建：目录此前不存在、本轮生成、bundles 为固定的四件套、全家桶版本等于锁定值',
    createdAfterRun &&
      Array.isArray(declaredBundles) &&
      ['@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app', '@linxin666/dsh-web-all', '@shxtmaker/dsh-remote-attachments'].every((name) => declaredBundles.includes(name)) &&
      bundleVersions['@linxin666/dsh-web-all']?.version === lockedAll.version,
    `bundles=${declaredBundles.join('+')} all=${bundleVersions['@linxin666/dsh-web-all']?.version}@${bundleVersions['@linxin666/dsh-web-all']?.source}（锁定 ${lockedAll.version}） fresh=${createdAfterRun}`
  )

  const lockText = await readFile(join(profileDir, 'pnpm-lock.yaml'), 'utf8')
  const lockEntry = parsePluginLockEntry(lockText)
  const installedCompare = compareInstalledToTarball(entries.filter((entry) => entry.isFile), installedDir)
  const sourceLinks = findSourceLinks(join(profileDir, 'node_modules/@shxtmaker'))
  const installedIsSymlink = existsSync(installedDir) ? lstatSync(installedDir).isSymbolicLink() : null
  const installedUnderProfile = existsSync(installedDir) && realpathSync(installedDir).startsWith(realpathSync(profileDir) + sep)
  const resolvedTarball = lockEntry.entry?.tarball === null || lockEntry.entry?.tarball === undefined
    ? null
    : resolve(profileDir, lockEntry.entry.tarball.replace(/^file:/, ''))
  // lockfile 里记录的是 integrity（sha512），与候选包字节必须一致；sha256 由包字节单独核对。
  const lockIntegrityMatches = lockEntry.entry?.integrity === candidate.integrity
  observations.installed = {
    dir: rel(installedDir),
    isSymlink: installedIsSymlink,
    underProfile: installedUnderProfile,
    sourceLinks,
    fileCompare: installedCompare,
    lockEntry: { ...lockEntry.entry, tarball: resolvedTarball === null ? null : rel(resolvedTarball) },
    lockSpecifier: lockEntry.specifier,
    resolvedTarball: resolvedTarball === null ? null : rel(resolvedTarball),
    lockIntegrityMatches,
    hasLinkDependency: lockEntry.hasLink
  }
  gate(
    'G05-installed-is-candidate',
    'profile 内实际安装的插件文件与候选包逐字节相同，lockfile 记录到候选包的 integrity/tarball，且不是 source link（无 link: 依赖、无符号链接、模块在 profile 目录内）',
    installedCompare.ok &&
      lockIntegrityMatches &&
      lockEntry.entry?.version === candidate.pluginVersion &&
      resolvedTarball !== null &&
      resolve(resolvedTarball) === resolve(tarballPath) &&
      lockEntry.hasLink === false &&
      installedIsSymlink === false &&
      installedUnderProfile === true &&
      sourceLinks.length === 0,
    `checked=${installedCompare.checked} mismatched=${JSON.stringify(installedCompare.mismatched.slice(0, 3))} missing=${JSON.stringify(installedCompare.missing.slice(0, 3))} extra=${JSON.stringify(installedCompare.extra.slice(0, 3))} integrity=${lockEntry.entry?.integrity === candidate.integrity ? '==候选' : `≠候选(${String(lockEntry.entry?.integrity).slice(0, 24)}…)`} link=${lockEntry.hasLink} symlink=${installedIsSymlink} underProfile=${installedUnderProfile} sourceLinks=${sourceLinks.length}`
  )
  negative(
    'N03-installed-drift-is-caught',
    '反例：把已安装文件当成比候选多一个字节的旧产物，G05 的文件比对必须失败',
    (() => {
      const fakeRoot = join(fixtureRoot, 'd22/fake-installed')
      mkdirSync(fakeRoot, { recursive: true })
      for (const entry of entries.filter((item) => item.isFile)) {
        const target = join(fakeRoot, entry.path.replace(/^package\//, ''))
        mkdirSync(dirname(target), { recursive: true })
        writeFileSync(target, entry.content)
      }
      writeFileSync(join(fakeRoot, 'lib/client.js'), Buffer.concat([readFileSync(join(fakeRoot, 'lib/client.js')), Buffer.from('//drift')]))
      const result = compareInstalledToTarball(entries.filter((entry) => entry.isFile), fakeRoot)
      return result.ok === false && result.mismatched.includes('lib/client.js')
    })(),
    '安装副本 client.js +"//drift" → compareInstalledToTarball().ok === false'
  )

  const fixtureMarkerAfterInstall = fixtureMarker(fixtureProfileDir)
  gate(
    'G06-no-reliance-on-fixture-profile',
    'D22 profile 与夹具 profile 是两个独立目录：D22 的模块在自身 profile 内，安装前后夹具 profile 的产物逐字节不变',
    installedUnderProfile === true &&
      realpathSync(installedDir) !== realpathSync(join(fixtureProfileDir, 'node_modules/@shxtmaker/dsh-remote-attachments')) &&
      fixtureProfileMarker !== null &&
      JSON.stringify(fixtureMarkerAfterInstall) === JSON.stringify(fixtureProfileMarker),
    `d22=${rel(installedDir)} fixture=${rel(join(fixtureProfileDir, 'node_modules/@shxtmaker/dsh-remote-attachments'))} fixtureUnchanged=${JSON.stringify(fixtureMarkerAfterInstall) === JSON.stringify(fixtureProfileMarker)}`
  )

  // ================= D. 组合配置（无额外 remote） =================
  console.log('\n阶段 D：组合配置（remote 恰好一次）\n')
  const composeRun = spawnSync(dshBin, ['--profile', profileName, '--dump-config'], {
    cwd: repoRoot,
    encoding: 'utf8',
    env: { ...process.env, DSH_HOME: dshHome }
  })
  if (composeRun.status !== 0) throw new Error(`--dump-config 退出码 ${composeRun.status}`)
  await writeFile(join(RUN_DIR, 'composed-config.yml'), sanitize(composeRun.stdout ?? ''))
  const composeText = composeRun.stdout ?? ''
  const addonRows = [...composeText.matchAll(/^- id: remote-attachments$/gm)].length
  const remoteAggregatedRows = [...composeText.matchAll(/name: '@linxin666\/dsh-web-all\/remote-web-ui'/g)].length
  const remoteDirectRows = [...composeText.matchAll(/^\s*name: '@linxin666\/dsh-remote-web-ui'$/gm)].length
  const webserverRows = [...composeText.matchAll(/^- id: webserver$/gm)].length
  observations.composedConfig = { addonRows, remoteAggregatedRows, remoteDirectRows, webserverRows }
  gate(
    'G07-single-remote-mount',
    '组合配置：本插件行恰好一次、全家桶 remote 行恰好一次、没有直接安装的 remote（F02 技法）',
    addonRows === 1 && remoteAggregatedRows === 1 && remoteDirectRows === 0 && webserverRows === 1,
    `addonRows=${addonRows} remoteAggregatedRows=${remoteAggregatedRows} remoteDirectRows=${remoteDirectRows} webserverRows=${webserverRows}`
  )

  // ================= E. 服务与"跑着的就是本轮 tarball" =================
  console.log('\n阶段 E：干净 profile 起真实服务并核对运行时身份\n')
  // 会话必须在 stub 启动前造好：stub 的 agentRequests 计数从 0 开始（D09 同款顺序）。
  const seed = spawnSync(dshBin, ['--profile', headlessProfile, 'd22 clean-profile loop seed'], {
    cwd: repoRoot,
    encoding: 'utf8',
    env: { ...process.env, DSH_HOME: dshHome, DEEPSEEK_API_KEY: 'stub-key' },
    timeout: 120_000
  })
  observations.sessions = { seedExitCode: seed.status }

  stub = await startStub(
    {
      final: { text: 'D22_CLEAN_PROFILE_FINAL' },
      steps: [{
        kind: 'tool-call',
        tool: 'read',
        // 占位符按正文 digest 锚定（见 readStepPath 的说明）；它的正/反语义在阶段 A 已自检。
        arguments: { file_path: readStepPath },
        expect: { resultContainsAll: [loopMarker, NON_ASCII_LINE] }
      }]
    },
    join(fixtureRoot, 'd22/stub')
  )

  service = startService(dshBin, ['--profile', profileName, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await writeFile(servicePidFile, String(service.child.pid))
  const readyUrl = await waitForReady(service.child, serviceLog, 120_000)
  const serviceText = existsSync(serviceLog) ? await readFile(serviceLog, 'utf8') : ''
  const d22Pids = d22ServicePids()
  const fixturePortOpen = await portAccepts(fixturePort)
  const secondaryPortOpen = await portAccepts(secondaryPort)
  observations.runtimeProcesses = {
    d22Pids: d22Pids.map((row) => row.line),
    fixturePortOpen,
    secondaryPortOpen,
    remoteProcesses: pgrep('dsh-remote-web-ui')
  }
  gate(
    'G08-service-identity',
    '服务真实就绪，且运行期只有本 profile 的一个 dsh 进程：夹具端口与第二 target 端口都没有被本轮拉起',
    readyUrl.startsWith('http://') && d22Pids.length === 1 && fixturePortOpen === false && secondaryPortOpen === false,
    `pid=${d22Pids.map((row) => row.pid).join(',')} 就绪=${readyUrl.replace(/token=[^&]+/, 'token=<redacted>')} fixture:${fixturePort}=${fixturePortOpen} secondary:${secondaryPort}=${secondaryPortOpen}`
  )
  const degradationPatterns = [/degraded/i, /failed to load/i, /cannot find module/i, /ERR_MODULE_NOT_FOUND/, /plugin .* not found/i, /capability conflict/i]
  const degradationHits = degradationPatterns.flatMap((pattern) => {
    const match = serviceText.match(pattern)
    return match ? [`${pattern} -> ${match[0]}`] : []
  })
  gate('G09-no-degradation', '启动日志无模块降级/缺失/能力冲突', degradationHits.length === 0, degradationHits.join('; '))

  client = createClient()
  const issue = await httpJson(`http://127.0.0.1:${port}/api/pair/issue`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
  if (typeof issue.body?.token !== 'string') throw new Error(`配对令牌签发失败：status=${issue.status}`)
  await client.follow(`${lanBase}/pair-accept?pair=${issue.body.token}`)
  deviceId = client.jar.get('dsh_pair') ?? ''
  // 与 D09 同款：LAN 上读取应用页（/pair-app），它才是浏览器里真正加载的页面（含 index-inject）。
  const pageLoad = await client.follow(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId)}`)
  if (pageLoad.response?.status !== 200) throw new Error(`LAN 应用页不可加载：status=${pageLoad.response?.status}`)
  const html = pageLoad.text
  await writeFile(join(RUN_DIR, 'served-index.html'), html)
  const rosterMatch = html.match(/href="\/plugins\/\?\?([^"]+)"/)
  const rosterQuery = rosterMatch ? rosterMatch[1].replace(/&amp;/g, '&') : ''
  const rosterSpec = rosterQuery.replace(/&rev=.*$/, '')
  const roster = rosterSpec === '' ? [] : rosterSpec.split(',').map((item) => decodeURIComponent(item.trim()))
  const addonInRoster = roster.filter((item) => item.includes('dsh-remote-attachments'))
  const remoteInRoster = roster.filter((item) => item.includes('dsh-remote-web-ui'))
  observations.roster = { entries: roster.length, addon: addonInRoster, remote: remoteInRoster }
  gate(
    'G10-roster-host-only-remote',
    'client roster 恰好一个本插件条目，且 remote 不在 client roster（host-only，未额外起第二份 remote）',
    roster.length > 0 && addonInRoster.length === 1 && remoteInRoster.length === 0,
    `roster=${roster.length} addon=${addonInRoster.length} remote=${remoteInRoster.length}`
  )

  // 运行中的 host 半区：已安装 host 模块现场生成的预启动脚本必须逐字出现在服务返回的 HTML 里。
  const hostModuleUrl = pathToFileURL(join(installedDir, 'lib/host/upload-hook.js')).href
  const installedHostHook = await import(hostModuleUrl)
  const expectedHostScript = installedHostHook.buildUploadHookScript()
  const hostScriptMatch = servedHostScriptMatches(html, expectedHostScript)
  const hostScriptMutated = servedHostScriptMatches(html, expectedHostScript.replace('__DSH_FILE_UPLOAD__', '__DSH_FILE_UPLOAD_MUTATED__'))
  observations.servedHostScript = { mode: hostScriptMatch.mode, length: expectedHostScript.length, mutatedMatches: hostScriptMutated.ok }
  gate(
    'G11-running-host-is-candidate',
    '服务返回的 HTML 逐字含**已安装 host 模块**现场生成的预启动承载脚本（跑着的 host 就是候选包里的那一份）',
    hostScriptMatch.ok,
    `匹配形态=${hostScriptMatch.mode} 脚本长度=${expectedHostScript.length} host.js=${sha256(readFileSync(join(installedDir, 'lib/host.js'))).slice(0, 12)}`
  )
  negative(
    'N04-mutated-host-script-is-caught',
    '反例：把期望的承载脚本改一处（模拟旧构建的 host），G11 的逐字判定必须失败',
    hostScriptMutated.ok === false,
    '承载脚本改 __DSH_FILE_UPLOAD__ → servedHostScriptMatches().ok === false'
  )

  // 运行中的 client 半区：合并 bundle 里本包段落必须逐字节等于包内 lib/client.js。
  const bundleResponse = await fetch(`http://127.0.0.1:${port}/plugins/??${rosterQuery}`, {
    headers: { ...client.cookieHeader(), 'x-dsh-remote-device': deviceId }
  })
  const bundle = await bundleResponse.text()
  const tarballClientText = entries.find((entry) => entry.path === 'package/lib/client.js').content.toString('utf8')
  const expectedServed = prepareServedSource(tarballClientText)
  const servedOccurrence = servedClientOccurrence(bundle, expectedServed)
  const mutatedClient = `${expectedServed}/*stale-build*/`
  observations.servedClientBundle = {
    status: bundleResponse.status,
    bytes: bundle.length,
    occurrences: servedOccurrence.occurrences,
    boundary: servedOccurrence.reason,
    servedClientSha256: sha256(Buffer.from(expectedServed, 'utf8')),
    tarballClientBytes: Buffer.byteLength(expectedServed, 'utf8'),
    tarballClientSha256: sha256(Buffer.from(expectedServed, 'utf8')),
    markerInBundle: bundle.includes('remote-attachments-client'),
    mutatedMatches: bundle.includes(mutatedClient)
  }
  gate(
    'G12-running-client-bundle-is-candidate',
    '合并 client bundle 里**完整**的本包 lib/client.js 字节恰好出现一次且落在 combo 接缝上（不是上一次构建的产物）',
    bundleResponse.status === 200 &&
      servedOccurrence.ok &&
      bundle.includes('remote-attachments-client'),
    `status=${bundleResponse.status} bytes=${bundle.length} 出现次数=${servedOccurrence.occurrences} 边界=${servedOccurrence.reason} 包内=${Buffer.byteLength(expectedServed, 'utf8')}B/${sha256(Buffer.from(expectedServed, 'utf8')).slice(0, 12)}`
  )
  negative(
    'N05-stale-client-bundle-is-caught',
    '反例：把候选 client.js 改一处（模拟陈旧 bundle），G12 的逐字节判定必须失败',
    bundle.includes(mutatedClient) === false && servedClientOccurrence(bundle, mutatedClient).ok === false,
    '服务端 bundle 不含改过的 client 文本 → 逐字节判定为 false'
  )

  // ================= F. 代表性闭环（干净 profile 内，真实 Chromium） =================
  console.log('\n阶段 F：干净 profile 内的代表性闭环（分块 → 真 File → 草稿 → 原生 id → 发送 → 工具读回）\n')
  const sessionsRpc = await client.fetch(`${lanBase}/remote/api/session/list`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({ type: 'client-request', rpcId: `d22-list-${Date.now()}`, method: 'session/list', payload: { args: { _request: {} } } })
  })
  const sessionsBody = await sessionsRpc.json().catch(() => null)
  const sessionItems = sessionsBody?.result?.value?.items ?? []
  sessionId = sessionItems.map((item) => item.sessionId).filter((value) => typeof value === 'string')[0] ?? null
  observations.sessions.list = { status: sessionsRpc.status, count: sessionItems.length, sessionId }
  if (sessionId === null) throw new Error('共享 DSH_HOME 里没有可用会话（headless 落盘失败）')

  chrome = await launchChrome({})
  const page = await openPage(chrome.debugPort)
  openPages.push(page)
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  // 网络探针必须在导航前挂上：附件一进草稿，Harness 就会开始后台上传，
  // 晚挂探针会漏掉真正的 upload 请求（实测踩到：uploads=0 而页面里其实已上传完成）。
  const net = { phase: 'upload', requests: [], uploads: [] }
  page.onEvent((message) => {
    if (message.method === 'Network.requestWillBeSent') {
      const request = message.params.request
      const record = { phase: net.phase, id: message.params.requestId, url: request.url, method: request.method, headers: request.headers ?? {}, hasPostData: request.hasPostData === true }
      net.requests.push(record)
      if (record.url.includes('uploadFileBinary')) net.uploads.push(record)
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
      if (response.url.includes('uploadFileBinary')) {
        const upload = net.uploads.find((entry) => entry.id === message.params.requestId)
        if (upload !== undefined) upload.responseStatus = response.status
        void fetchResponseBody(message.params.requestId).catch(() => {})
      }
    }
    if (message.method === 'Network.loadingFinished') {
      const upload = net.uploads.find((entry) => entry.id === message.params.requestId)
      if (upload !== undefined && upload.responseBody === undefined) void fetchResponseBody(message.params.requestId).catch(() => {})
    }
  })
  /**
   * 取上传响应的正文（receipt）。
   *
   * 为什么要有重试：responseReceived 时刻正文未必可取（实测 uploads=2 但两条都拿不到 receipt，
   * 于是"收据一致性"判据变成假失败）。这里在 responseReceived / loadingFinished 各试一次，
   * 并在轮询里继续重试，同时把失败原因记进条目（失败也要可诊断，不能静默吞掉）。
   */
  const fetchResponseBody = async (requestId) => {
    try {
      const body = await page.send('Network.getResponseBody', { requestId })
      const upload = net.uploads.find((entry) => entry.id === requestId)
      if (upload !== undefined) {
        upload.responseBody = body.base64Encoded ? Buffer.from(body.body, 'base64').toString('utf8') : body.body
        upload.bodyError = null
      }
    } catch (error) {
      const upload = net.uploads.find((entry) => entry.id === requestId)
      if (upload !== undefined) upload.bodyError = String(error?.message ?? error)
    }
  }
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
  })
  await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId)}`)
  const readCard = async () => JSON.parse(await page.evaluate(READ_CARD))
  /** 现场读一次插件自报的能力（ADDON.status() 是**实时**探测，不是安装期快照）。 */
  const readAddonStatus = async () =>
    JSON.parse(
      await page.evaluate(`(() => {
        const addon = globalThis.__DSH_ATTACHMENTS_ADDON__
        const snapshot = globalThis.__DSH_ATTACHMENTS_STATUS__?.addon?.capability ?? null
        let live = null
        try { live = addon?.status?.()?.capability ?? null } catch (error) { live = { threw: String(error && error.message ? error.message : error) } }
        return JSON.stringify({ live, snapshot })
      })()`)
    )
  const ready = await poll(
    readCard,
    (state) => state.card === true && state.bridge === 'object' && state.receiver === 'object' && state.hookFetch === 'function' && state.currentSession === sessionId,
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  observations.page = { ready: ready.ok, state: ready.value }
  gate(
    'G13-clean-profile-page-ready',
    '干净 profile 的页面里版本化桥与生产接收端都已安装，承载形状正确，且桥认定的当前会话是目标会话',
    ready.ok && ready.value.bridgeVersion === 1 && ready.value.receiverVersion === 1 && ready.value.currentSession === sessionId,
    `bridge=${ready.value.bridge} v${ready.value.bridgeVersion} receiver=${ready.value.receiver} v${ready.value.receiverVersion} hookFetch=${ready.value.hookFetch} current=${ready.value.currentSession}`
  )

  const addonStatus = await readAddonStatus()
  observations.addonStatus = addonStatus
  gate(
    'G13b-clean-profile-capability-available',
    '插件在干净 profile 里自报**实时**能力为 available（remote 通道 + 草稿导入依赖 + 承载齐备），不是"装上了但用不了"',
    addonStatus.live?.status === 'available' && (addonStatus.live?.missing ?? []).length === 0,
    `live=${addonStatus.live?.status} missing=${JSON.stringify(addonStatus.live?.missing ?? [])} 安装期快照=${addonStatus.snapshot?.status}（诊断面快照，见 G13b 说明）`
  )

  const batchId = `d22-batch-${RUN_ID.slice(-8)}`
  const fileId = `d22-file-${RUN_ID.slice(-8)}`
  const batchSpec = {
    sessionId,
    targetId: `d22-target-${RUN_ID.slice(-8)}`,
    batchId,
    fileId,
    name: 'd22-clean-profile-note.txt',
    mime: 'text/plain',
    chunkBytes: payloadChunkBytes,
    sha256: payloadSha256,
    dataBase64: payloadBytes.toString('base64')
  }
  const batch = JSON.parse(await page.evaluate(receiverBatchProbe(batchSpec), { awaitPromise: true }))
  const acceptedChunks = (batch.chunks ?? []).filter((chunk) => chunk.ok === true).length
  observations.chunkedTransfer = {
    fileBytes: payloadBytes.length,
    chunkBytes: payloadChunkBytes,
    plannedChunks: Math.ceil(payloadBytes.length / payloadChunkBytes),
    acceptedChunks,
    chunks: batch.chunks,
    fileEnd: batch.fileEnd,
    record: batch.record,
    importResult: batch.importResult,
    acks: batch.acks,
    importResults: batch.importResults
  }
  gate(
    'G14-chunked-transfer-commits-real-file',
    '生产接收端按 ≥3 块真实分块接收（逐块 ack），file-end 通过内容哈希校验，组装出的 File 被生产草稿导入为 staged',
    batch.ok === true &&
      acceptedChunks >= 3 &&
      acceptedChunks === Math.ceil(payloadBytes.length / payloadChunkBytes) &&
      batch.fileEnd?.ok === true &&
      batch.record?.receivedBytes === payloadBytes.length &&
      batch.record?.draft === 'staged' &&
      (batch.record?.attachmentIds ?? []).length >= 1 &&
      batch.importResult?.status === 'staged',
    `字节=${batch.fileBytes} 块=${acceptedChunks}/${Math.ceil(payloadBytes.length / payloadChunkBytes)}×(≤${payloadChunkBytes}B) fileEnd=${batch.fileEnd?.ok} received=${batch.record?.receivedBytes} draft=${batch.record?.draft} ids=${JSON.stringify(batch.record?.attachmentIds ?? [])} acks=${batch.acks}`
  )
  const receiverAttachmentIds = batch.record?.attachmentIds ?? []

  // 原生 id 的独立回读：同一会话再导一个小文件，它的 previous 必须逐位等于接收端导入的 id（D09 G01b 技法）。
  const nativeRoundTrip = JSON.parse(await page.evaluate(importProbe(sessionId, [{ name: 'd22-native-id-probe.txt', type: 'text/plain', text: 'D22 native id probe' }])))
  const nativePrevious = nativeRoundTrip.result?.previous ?? []
  observations.nativeAttachmentIds = { receiverIds: receiverAttachmentIds, nextImportPrevious: nativePrevious, probeResult: nativeRoundTrip.result ?? nativeRoundTrip }
  gate(
    'G15-native-attachment-id-readback',
    '接收端返回的 id 是真实原生 attachmentIds：下一次导入的 previous 逐位回读到同一值（不是自造字符串）',
    nativeRoundTrip.result?.ok === true &&
      receiverAttachmentIds.length >= 1 &&
      nativePrevious.length === receiverAttachmentIds.length &&
      nativePrevious.every((value, index) => value === receiverAttachmentIds[index]),
    `接收端 ids=${JSON.stringify(receiverAttachmentIds)} 下一批 previous=${JSON.stringify(nativePrevious)}`
  )
  negative(
    'N06-native-id-is-not-tautological',
    '反例：换一个会话 id 导入必须确定失败（桥/适配器不是无脑 ok）',
    (await (async () => {
      const bogus = JSON.parse(await page.evaluate(importProbe('session-not-current-0000', [{ name: 'n6.txt', type: 'text/plain', text: 'n6' }])))
      observations.negativeDraftImport = bogus.result ?? bogus
      return bogus.result?.ok === false && typeof bogus.result?.code === 'string'
    })()),
    `code=${JSON.stringify(observations.negativeDraftImport?.code ?? null)}`
  )

  // 分块哈希反例：声明错误的 sha256 必须让 file-end 失败且不产生原生 id。
  const badHashBatch = JSON.parse(await page.evaluate(receiverBatchProbe({ ...batchSpec, batchId: `${batchId}-bad`, fileId: `${fileId}-bad`, sha256: sha256(Buffer.from('not-the-payload')) }), { awaitPromise: true }))
  observations.chunkedTransfer.badHashControl = { fileEnd: badHashBatch.fileEnd, record: badHashBatch.record, importResult: badHashBatch.importResult }
  negative(
    'N07-chunked-hash-mismatch-is-caught',
    '反例：分块内容与声明的 sha256 不符时，file-end 必须失败、不得进入草稿、不得产生原生 id',
    badHashBatch.fileEnd?.ok === false && (badHashBatch.record?.attachmentIds ?? []).length === 0 && badHashBatch.record?.draft !== 'staged',
    `fileEnd=${badHashBatch.fileEnd?.ok} code=${badHashBatch.fileEnd?.code} draft=${badHashBatch.record?.draft} ids=${JSON.stringify(badHashBatch.record?.attachmentIds ?? [])}`
  )

  // 真实上传与 receipt：只认走 remote 改写路由 + 设备头 + 内容寻址 receipt。
  const uploadReady = await poll(readCard, (state) => state.retry.length > 0 || (state.send !== null && state.send.disabled === false), { timeoutMs: 60_000 })
  // 草稿里还有原生 id 回读用的小探针文件，因此必须挑**正文那一条**上传（按 receipt 字节数锚定）。
  const parseUploadReceipt = (entry) => {
    try {
      return entry?.responseBody === undefined ? null : JSON.parse(entry.responseBody)
    } catch {
      return null
    }
  }
  const uploadPoll = await poll(
    async () => {
      // 每轮把所有还没有正文的上传条目再取一次（响应已完成后再取通常能成功）。
      for (const entry of net.uploads) if (entry.responseBody === undefined) await fetchResponseBody(entry.id)
      return net.uploads.find((entry) => parseUploadReceipt(entry)?.value?.file?.bytes === payloadBytes.length) ?? null
    },
    (entry) => entry !== null,
    { timeoutMs: 45_000, intervalMs: 500 }
  )
  const upload = uploadPoll.value
  const uploadHeaders = { ...(upload?.headers ?? {}), ...(upload?.extraHeaders ?? {}) }
  const receipt = parseUploadReceipt(upload)
  const receiptCheck = receiptConsistent(receipt, payloadBytes)
  observations.upload = {
    uploadRequests: net.uploads.length,
    entries: net.uploads.map((entry) => ({
      url: entry.url,
      method: entry.method,
      status: entry.responseStatus ?? entry.status ?? null,
      deviceHeader: { ...(entry.headers ?? {}), ...(entry.extraHeaders ?? {}) }['x-dsh-remote-device'] !== undefined,
      hasBody: typeof entry.responseBody === 'string',
      bodyError: entry.bodyError ?? null,
      receiptBytes: parseUploadReceipt(entry)?.value?.file?.bytes ?? null
    })),
    url: upload?.url ?? null,
    method: upload?.method ?? null,
    status: upload?.responseStatus ?? upload?.status ?? null,
    deviceHeader: uploadHeaders['x-dsh-remote-device'] ?? null,
    transferredBytes: upload?.encodedDataLength ?? null,
    receipt,
    receiptCheck
  }
  gate(
    'G16-upload-remote-route-receipt',
    '文本附件经真实后台上传走改写后的 remote 路由（带设备头），receipt 的字节数与内容寻址 attachmentId 等于导入字节',
    uploadReady.ok &&
      uploadReady.value.retry.length === 0 &&
      upload !== null &&
      upload.method === 'POST' &&
      upload.url.startsWith(`${lanBase}/remote/api/session/uploadFileBinary`) &&
      typeof uploadHeaders['x-dsh-remote-device'] === 'string' &&
      (upload.responseStatus ?? upload.status) === 200 &&
      receiptCheck.ok,
    `url=${upload?.url ?? '(无)'} status=${upload?.responseStatus ?? '(无)'} 设备头=${uploadHeaders['x-dsh-remote-device'] === undefined ? '(无)' : '有'} 上传请求数=${net.uploads.length} ${receiptCheck.detail}`
  )
  negative(
    'N08-receipt-bytes-is-caught',
    '反例：把 receipt 的 bytes 改 ±1，一致性判定必须失败',
    receipt !== null && receiptConsistent({ ...receipt, value: { ...receipt.value, file: { ...receipt.value.file, bytes: receipt.value.file.bytes + 1 } } }, payloadBytes).ok === false,
    'receipt.bytes+1 → receiptConsistent().ok === false'
  )

  const beforeSend = await stubSummary()
  const click = await page.evaluate(CLICK_SEND)
  // 失控护栏：stub 的 step 只在工具结果满足 expect 时才推进；若读回判定永远不满足，
  // Harness 会重放同一工具调用（实测可达上千次）。这里在明显失控时提前收口并如实报失败，
  // 不把"跑不完"伪装成"还在等"。
  const runawayLimit = 25
  const readBack = await poll(
    async () => stubSummary(),
    (summary) =>
      (summary.toolResultTexts ?? []).some((text) => containsImportedText(text, payloadText)) ||
      (summary.toolCallRequests ?? 0) > runawayLimit,
    { timeoutMs: 150_000, intervalMs: 1000 }
  )
  const summary = readBack.value
  const readText = (summary.toolResultTexts ?? []).find((text) => containsImportedText(text, payloadText)) ?? ''
  observations.loop = {
    click,
    agentRequests: [beforeSend.agentRequests, summary.agentRequests],
    toolCallRequests: summary.toolCallRequests,
    runaway: (summary.toolCallRequests ?? 0) > runawayLimit,
    readBackBytes: readText === '' ? 0 : Buffer.byteLength(reconstructReadText(readText), 'utf8'),
    importedBytes: payloadBytes.length,
    importedSha256: payloadSha256,
    readBackSha256: readText === '' ? null : sha256(Buffer.from(reconstructReadText(readText), 'utf8')),
    placeholderResolutions: summary.placeholderResolutions,
    marker: loopMarker
  }
  gate(
    'G17-send-and-tool-readback',
    '点击真实 composer 主按钮后 provider 收到 agent 请求，同一轮内真实 read 工具剥离行号后与导入字节逐字节相同（marker + 非 ASCII 行）',
    click === 'clicked' &&
      readBack.ok &&
      readText !== '' &&
      (summary.toolCallRequests ?? 0) <= runawayLimit &&
      (summary.placeholderResolutions ?? []).some((record) => record.resolved === true && String(record.arguments?.file_path ?? '').includes('/attachments/v1/files/')),
    `click=${click} agentRequests ${beforeSend.agentRequests}→${summary.agentRequests} 工具调用=${summary.toolCallRequests} 读回=${readText === '' ? '(无)' : `${Buffer.byteLength(reconstructReadText(readText), 'utf8')}B/${sha256(Buffer.from(reconstructReadText(readText), 'utf8')).slice(0, 12)}`} 导入=${payloadBytes.length}B/${payloadSha256.slice(0, 12)}`
  )
  negative(
    'N09-readback-is-byte-exact',
    '反例：期望正文改一个字符，读回判定必须失败',
    readText !== '' && containsImportedText(readText, payloadText.replace(loopMarker, `${loopMarker}-X`)) === false,
    '期望 marker 改一字 → containsImportedText() === false'
  )

  // 主机侧独立证据：上传的字节真的落进了内容寻址的附件库（sha256 即 attachmentId）。
  // 它与"网络 receipt"是两条独立路径，任一条被伪造都过不了另一条。
  const stagedRoot = join(dshHome, 'attachments/v1/files')
  const stagedCopies = (() => {
    const hits = []
    const walk = (dir) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name)
        if (entry.isDirectory()) walk(full)
        else if (entry.isFile() && entry.name === 'd22-clean-profile-note.txt') {
          const content = readFileSync(full)
          hits.push({ path: rel(full), bytes: content.length, sha256: sha256(content) })
        }
      }
    }
    if (existsSync(stagedRoot)) walk(stagedRoot)
    return hits
  })()
  observations.stagedCopy = { root: rel(stagedRoot), hits: stagedCopies, expectedSha256: payloadSha256, expectedBytes: payloadBytes.length }
  gate(
    'G17b-uploaded-bytes-content-addressed-on-host',
    '主机附件库里存在与导入字节同名、同字节数、同 sha256 的暂存副本（内容寻址，attachmentId=sha256）',
    stagedCopies.some((item) => item.bytes === payloadBytes.length && item.sha256 === payloadSha256),
    `命中=${stagedCopies.length} 期望=${payloadBytes.length}B/${payloadSha256.slice(0, 12)} 实测=${JSON.stringify(stagedCopies.map((item) => `${item.bytes}B/${item.sha256.slice(0, 12)}`))}`
  )
  const d22PidsAfterLoop = d22ServicePids()
  const remoteRowsAfterLoop = pgrep('dsh-remote-web-ui')
  gate(
    'G18-no-second-remote-instance',
    '闭环期间仍只有本 profile 的一个 dsh 进程；没有额外的 remote 服务进程被拉起',
    d22PidsAfterLoop.length === 1 && remoteRowsAfterLoop.length === 0,
    `d22进程=${d22PidsAfterLoop.length} remote 进程=${remoteRowsAfterLoop.length}`
  )

  // ================= G. 包卫生与 manifest =================
  console.log('\n阶段 G：包卫生扫描与脱敏 manifest\n')
  const hygiene = scanTarEntries(entries)
  const hygieneCanaryContent = scanTarEntries([
    { path: 'package/lib/canary.js', isFile: true, content: Buffer.from('const cookie = "dsh_pair=0123456789abcdef0123456789abcdef"'), size: 0 }
  ])
  const hygieneCanaryPath = scanTarEntries([
    { path: 'package/lib/credentials.json', isFile: true, content: Buffer.from('{}'), size: 2 }
  ])
  const hygieneCanaryHome = scanTarEntries([
    { path: 'package/lib/leak.js', isFile: true, content: Buffer.from(`const dir = "${homeDir}/.dsh/profiles/web"`), size: 0 }
  ])
  observations.hygiene = {
    ok: hygiene.ok,
    offendingPaths: hygiene.offendingPaths,
    offendingContent: hygiene.offendingContent,
    canary: {
      cookie: hygieneCanaryContent.ok === false,
      credentialPath: hygieneCanaryPath.ok === false,
      homePath: hygieneCanaryHome.ok === false
    },
    scannedFiles: entries.filter((entry) => entry.isFile).length
  }
  gate(
    'G19-package-hygiene',
    '候选包不含凭据、测试私有数据或绝对家目录（与 D03 test:pack 同一条扫描），且禁止清单里的路径/正文形态为零',
    hygiene.ok && hygiene.offendingPaths.length === 0 && hygiene.offendingContent.length === 0,
    `扫描文件=${observations.hygiene.scannedFiles} 路径命中=${hygiene.offendingPaths.length} 正文命中=${hygiene.offendingContent.length}`
  )
  negative(
    'N10-hygiene-scan-fires',
    '反例 canary：把配对 cookie、credential 路径、绝对家目录路径分别塞进包条目，同一扫描必须命中',
    observations.hygiene.canary.cookie && observations.hygiene.canary.credentialPath && observations.hygiene.canary.homePath,
    `cookie=${observations.hygiene.canary.cookie} credentialPath=${observations.hygiene.canary.credentialPath} homePath=${observations.hygiene.canary.homePath}`
  )

  // ---- manifest：sha256 / integrity / 文件清单 / 声明入口 / 实际安装版本 / 插件版本 ----
  const cliUiVersions = {}
  for (const name of ['dsh-client-modules', 'dsh-client-file-upload', 'dsh-client-connection', 'dsh-web-app', 'dsh-base', 'dsh-attachment', 'dsh-agent-loop']) {
    const candidates = [join(profileDir, 'node_modules/@deepseek-ai', name, 'package.json'), join(cliNodeModules, name, 'package.json')]
    for (const manifestPath of candidates) {
      if (!existsSync(manifestPath)) continue
      cliUiVersions[name] = { version: JSON.parse(await readFile(manifestPath, 'utf8')).version, source: manifestPath.startsWith(profileDir) ? 'profile' : 'cli' }
      break
    }
  }
  const compatibilityTarball = compatibilityLock.packages.find((item) => item.name === '@shxtmaker/dsh-remote-attachments')
  manifestObject = {
    schemaVersion: 1,
    task: 'D22',
    purpose: 'final-tarball-install',
    runId: RUN_ID,
    generatedAt: new Date().toISOString(),
    candidate: {
      tarball: candidate.tarball,
      name: candidate.name,
      pluginVersion: candidate.pluginVersion,
      bytes: candidate.bytes,
      fileCount: candidate.fileCount,
      sha256: candidate.sha256,
      integrity: candidate.integrity,
      builtAt: candidate.builtAt
    },
    declaredEntryPoints: {
      main: tarballManifest?.main ?? null,
      exports: {
        '.': tarballManifest?.exports?.['.'] ?? null,
        './client': tarballManifest?.exports?.['./client'] ?? null
      },
      dsh: tarballManifest?.dsh ?? null,
      patch: tarballManifest?.dsh?.bundle?.patch ?? null
    },
    integrityRecordedInProfile: {
      lockfile: rel(join(profileDir, 'pnpm-lock.yaml')),
      key: lockEntry.entry?.key ?? null,
      specifier: lockEntry.specifier,
      integrity: lockEntry.entry?.integrity ?? null,
      tarball: resolvedTarball === null ? null : rel(resolvedTarball),
      matchesCandidate: lockIntegrityMatches
    },
    installedBundleVersions: bundleVersions,
    installedBundleVersionsNote: 'source=profile 表示装在 D22 profile 的 node_modules 里（全家桶与远程插件由 pnpm 解析，版本等于 compatibility-lock.json）；source=cli 表示该包由 CLI 自带（@deepseek-ai/* 的 shell/UI 包实测来自 CLI 的 node_modules，版本 0.1.5-rc.2），不在 profile 内重复安装；source=null 表示两处都没有。',
    servedUiPackageVersions: cliUiVersions,
    files: entries
      .filter((entry) => entry.isFile)
      .map((entry) => ({ path: entry.path.replace(/^package\//, ''), size: entry.size }))
      .sort((left, right) => (left.path < right.path ? -1 : 1)),
    installedProfile: {
      profile: profileName,
      dir: rel(profileDir),
      privateDshHome: rel(dshHome),
      createdFreshThisRun: true,
      tarballSpecifier: profileManifest?.dependencies?.['@shxtmaker/dsh-remote-attachments'] ?? null
    },
    compatibilityLockSnapshot: compatibilityTarball === undefined ? null : {
      declaredTarballSha256: compatibilityTarball.tarballSha256 ?? null,
      declaredIntegrity: compatibilityTarball.integrity ?? null,
      note: 'compatibility-lock.json 的插件条目是 D03 轮次快照，不随每次构建更新；本清单以实际候选包字节为准。'
    },
    runningBundleProof: {
      servedClientSha256: observations.servedClientBundle.servedClientSha256,
      servedClientOccurrences: observations.servedClientBundle.occurrences,
      servedClientBoundary: observations.servedClientBundle.boundary,
      tarballClientSha256: observations.servedClientBundle.tarballClientSha256,
      servedHostScriptMatchMode: observations.servedHostScript.mode,
      installedFileCompareOk: installedCompare.ok,
      installedFilesChecked: installedCompare.checked
    },
    windowsPending: [
      'Windows 正式打包/安装路径（eng/package.ps1、package-unsigned.ps1 产出的 MSI/zip 与 WebView2 承载）——不在 Linux 上尝试',
      '在 Windows 上以 pwsh 运行 eng/verify.ps1 的同一检查'
    ]
  }
  manifestText = sanitize(JSON.stringify(manifestObject, null, 2) + '\n')
  const redaction = redactionCheck(manifestText)
  observations.manifest = { bytes: manifestText.length, leaks: redaction.leaks, canaryFires: redaction.canaryFires, canaryLeaks: redaction.canaryLeaks }
  gate(
    'G20-manifest-redacted',
    'manifest（sha256/integrity/文件清单/声明入口/实际安装版本/插件版本）零敏感形态：无 pair/token/cookie/deviceId，也无绝对家目录路径',
    redaction.ok && manifestObject.files.length === candidate.fileCount && manifestObject.integrityRecordedInProfile.matchesCandidate === true,
    `文件条目=${manifestObject.files.length} 泄漏=${JSON.stringify(redaction.leaks)}`
  )
  negative(
    'N11-redaction-canary-fires',
    '反例 canary：往 manifest 文本里注入配对令牌与绝对家目录路径，同一脱敏判定必须命中',
    redaction.canaryFires && redaction.canaryLeaks.length >= 2,
    `canary 命中=${JSON.stringify(redaction.canaryLeaks)}`
  )

  // ================= H. 未验项（诚实标注） =================
  markNotRun('WindowsPackaging', 'Windows 正式打包与安装（MSI/zip + WebView2 承载）', 'WindowsPending：Linux 上不尝试，按任务卡口径保留')
  markNotRun('WindowsVerifyPs1', '在 Windows 上以 pwsh 运行 eng/verify.ps1 中的 final-tarball-install', 'WindowsPending：本检查只属 Linux Development 门禁')
  markNotRun('RealWebView2Boundary', '真实 WebView2 物理消息边界下的同一闭环', 'WindowsPending：Linux 夹具用真实 Chromium + 真实 /remote 通道替代，物理边界不同')
  markNotRun('LauncherByteSource', 'Windows Launcher 侧真实字节源（WindowsStagedByteSource）', 'WindowsPending：本轮用接收端分块协议直接驱动，等价于对端发送分块')
} catch (error) {
  fatal = error
  gate('G00-runtime', '判据脚本自身跑完（无致命异常）', false, sanitize(String(error?.stack ?? error)).slice(0, 500))
} finally {
  for (const page of openPages) await page.close().catch(() => {})
  if (chrome !== null) await chrome.close().catch(() => {})
  await stopStub(stub).catch(() => {})
  if (service !== null) await stopService(service.child, servicePidFile).catch(() => {})
  await rm(servicePidFile, { force: true })
  // 只删本轮自己新建的 D22 profile；夹具根目录保留给其它门禁复用（down.sh 由编排层调用）。
  if (!keepProfile && existsSync(profileDir)) {
    await rm(profileDir, { recursive: true, force: true }).catch(() => {})
  }
}

// ---------- 收尾核对：本轮起的进程必须全部结束 ----------
const leftoverPids = new Set([
  ...d22ServicePids().map((row) => String(row.pid)),
  ...pgrep(`provider-stub.mjs --port ${stubPort}`).map((line) => line.split(' ')[0]),
  ...(chrome === null ? [] : pgrep(`remote-debugging-port=${chrome.debugPort}`).map((line) => line.split(' ')[0]))
])
const otherFixtureRows = pgrep('dsh-attachments|headless-chrome|dsh-cdp').filter((line) => !leftoverPids.has(line.split(' ')[0]))
gate(
  'G21-no-leftovers',
  '本轮自己起的服务/provider stub/Chrome 全部结束（夹具内其它 profile 的进程只记录不误杀）',
  leftoverPids.size === 0,
  leftoverPids.size === 0 ? `零残留；其它夹具进程=${otherFixtureRows.length}` : `残留 pid=${[...leftoverPids].join(',')}`
)
observations.cleanup = {
  leftoverPids: [...leftoverPids],
  otherFixtureProcesses: otherFixtureRows,
  keptProfile: keepProfile,
  profileDirRemoved: !keepProfile && !existsSync(profileDir),
  downShInvoked: false,
  note: '不调用 down.sh：它会删除整个共享夹具目录（setup.sh 需要数分钟）。编排层（verify-portable.ps1）负责检查前后的 down.sh/setup.sh。'
}

// ---------- 汇总、写证据 ----------
const productionAfter = productionMarker()
gate(
  'G22-production-profile-untouched',
  '本轮未触碰生产 profile（$HOME/.dsh/profiles/web 的 package.json 哈希前后一致）',
  productionBefore === productionAfter,
  `before=${productionBefore === null ? '(不存在)' : productionBefore.slice(0, 12)} after=${productionAfter === null ? '(不存在)' : productionAfter.slice(0, 12)}`
)

const evidenceText = (value) => sanitize(redact(JSON.stringify(value, null, 2))) + '\n'

// 证据落盘前必须零敏感形态（pair=/cookie/token/deviceId/绝对家目录）；命中即判负。
// 与 manifest 的脱敏判定共用 leakReport()，并用同一批数据做 canary（证明判定会失败）。
const preWriteLeaks = leakReport(evidenceText({ candidate, installReceipt, observations }))
const preWriteCanary = leakReport(`${evidenceText({ candidate })}\n{"pair":"pair=ABCDEFGH12345678"}\n{"dir":"${homeDir}/.dsh/profiles/web"}`)
observations.evidenceRedaction = { leaks: preWriteLeaks, canaryLeaks: preWriteCanary }
gate(
  'G23-evidence-redacted',
  '证据落盘前零敏感形态：无 pairing token/cookie/JWT/deviceId，也无绝对家目录路径（redact() + findSecretLeaks）',
  preWriteLeaks.length === 0,
  `命中=${JSON.stringify(preWriteLeaks)}`
)
negative(
  'N12-evidence-redaction-canary-fires',
  '反例 canary：同一脱敏判定对注入的配对令牌与绝对家目录路径必须命中',
  preWriteCanary.length >= 2,
  `canary 命中=${JSON.stringify(preWriteCanary)}`
)

const failed = gates.filter((item) => !item.ok)
const failedNegative = negativeProbes.filter((item) => !item.ok)
const result = failed.length === 0 && failedNegative.length === 0 && fatal === null ? 'pass' : 'fail'

const reportText = evidenceText({
  schemaVersion: 1,
  task: 'D22',
  purpose: 'final-tarball-install',
  runId: RUN_ID,
  startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
  finishedAt: new Date().toISOString(),
  durationMs: Date.now() - RUN_STARTED_AT_MS,
  command: RUN_COMMAND,
  platform: { platform: process.platform, release: osRelease(), arch: process.arch, node: process.version, dshBin: '<DSH_BIN>', cwd: '<REPO>' },
  candidate,
  installReceipt,
  gates,
  negativeProbes,
  notRun,
  observations,
  manifest: `artifacts/verify-portable/d22-package-manifest.json（本轮 runId 副本：artifacts/verify-portable/d22-runs/${RUN_ID}/d22-package-manifest.json）`,
  evidenceLayout: {
    runDir: `artifacts/verify-portable/d22-runs/${RUN_ID}`,
    index: 'artifacts/verify-portable/d22-runs/index.json',
    report: `artifacts/verify-portable/d22-runs/${RUN_ID}/report.json`,
    latestMirror: 'artifacts/verify-portable/d22-tarball-gates.json 与 artifacts/fixture/evidence/d22-tarball-gates.json 都是**最新一次**运行的镜像；历史只认 runId 目录'
  },
  note: '本检查证明的是"交付包本身"：候选 tarball 在新建的干净 profile（固定全家桶）里装得上、装的就是候选包字节、跑着的 host/client 都是候选包字节，且代表性闭环真实走通。Linux+/remote 通道 + 真实 Chromium 是等价承载，真实 WebView2 物理边界属 WindowsPending。'
})
const gatesEvidenceText = evidenceText({
  schemaVersion: 1,
  task: 'D22',
  purpose: 'final-tarball-install',
  runId: RUN_ID,
  gates,
  failedGateIds: failed.map((item) => item.id),
  failedNegativeIds: failedNegative.map((item) => item.id),
  negativeProbes,
  notRun,
  candidate,
  result,
  report: `artifacts/verify-portable/d22-runs/${RUN_ID}/report.json`
})

await mkdir(RUN_DIR, { recursive: true })
await writeFile(join(RUN_DIR, 'report.json'), reportText)
await writeFile(join(RUN_DIR, 'gates.json'), gatesEvidenceText)
await writeFile(
  join(RUN_DIR, 'run.json'),
  evidenceText({
    runId: RUN_ID,
    task: 'D22',
    startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
    finishedAt: new Date().toISOString(),
    durationMs: Date.now() - RUN_STARTED_AT_MS,
    command: RUN_COMMAND,
    result,
    exitCode: result === 'pass' ? 0 : 1,
    candidate,
    gates: { total: gates.length, passed: gates.length - failed.length, failedGateIds: failed.map((item) => item.id) },
    negativeProbes: { total: negativeProbes.length, passed: negativeProbes.length - failedNegative.length, failedIds: failedNegative.map((item) => item.id) },
    notRunIds: notRun.map((item) => item.id),
    fatal: fatal === null ? null : sanitize(String(fatal?.stack ?? fatal)).slice(0, 600)
  })
)
if (manifestText !== '') await writeFile(join(RUN_DIR, 'd22-package-manifest.json'), manifestText)

await writeFile(join(durableEvidenceDir, 'd22-tarball-gates.json'), gatesEvidenceText)
await writeFile(join(evidenceDir, 'd22-tarball-gates.json'), gatesEvidenceText)
if (manifestText !== '') await writeFile(join(durableEvidenceDir, 'd22-package-manifest.json'), manifestText)

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
  candidate: { tarball: candidate?.tarball ?? null, sha256: candidate?.sha256 ?? null, integrity: candidate?.integrity ?? null, pluginVersion: candidate?.pluginVersion ?? null },
  command: RUN_COMMAND,
  report: `artifacts/verify-portable/d22-runs/${RUN_ID}/report.json`,
  manifest: `artifacts/verify-portable/d22-runs/${RUN_ID}/d22-package-manifest.json`,
  stdout: `artifacts/verify-portable/d22-runs/${RUN_ID}/stdout.log`
})
await writeFile(indexPath, evidenceText({ ...index, note: '只增不改：每次运行 push 一条；历史报告在各自 runId 目录里，本文件与顶层 latest 镜像都不得作为历史依据。' }))

console.log(`\nD22 判据：${gates.length - failed.length}/${gates.length} 通过；负向探针 ${negativeProbes.length - failedNegative.length}/${negativeProbes.length} 成立；未验=${notRun.length}`)
if (fatal !== null) console.error(`致命异常：${sanitize(String(fatal?.stack ?? fatal)).slice(0, 600)}`)
console.log(`[run] runId=${RUN_ID} 候选 sha256=${candidate?.sha256 ?? '(未知)'}`)
console.log(`[run] 报告=artifacts/verify-portable/d22-runs/${RUN_ID}/report.json（顶层镜像 d22-tarball-gates.json）`)
console.log(`[run] manifest=artifacts/verify-portable/d22-package-manifest.json`)
if (failed.length > 0 || failedNegative.length > 0) {
  console.error('失败项：')
  for (const item of [...failed, ...failedNegative]) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
  process.exitCode = 1
}
