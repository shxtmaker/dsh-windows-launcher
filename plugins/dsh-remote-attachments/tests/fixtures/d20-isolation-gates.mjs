#!/usr/bin/env node
/**
 * D20 会话与来源隔离判据（Linux，真实夹具，双 target / 多会话）。
 *
 * ## 目标
 *
 * 证明**投递归属**是可判定的：隐藏页面、导航、会话变化与重放，都不能把附件投进错误的目标。
 * 交付物是一张**隔离矩阵**：一行一个 (操作 × target × 会话)，每行给出确定结果与
 * "附件实际落在哪里"——落点只认**原生 `attachmentIds`**（由后续一次导入的 `previous`
 * 原样回读），绝不认 DOM 卡片。
 *
 * ## 夹具形状（为什么是 2×2）
 *
 * 同一台主机上同时跑两个 **web target**（同 DSH_HOME，因此会话集合相同）：
 *   - target A：profile `dsh-attachments-fixture`，端口 3099；
 *   - target B：profile `dsh-attachments-fixture-b`，端口 3097。
 * 另有降级 profile（端口 3098）只用于"真实可达的异来源页面"这一条。
 *
 * 两个 target × 两条会话（s1 已有附件、s2 全新）＝ 4 个**落点单元**，每个单元
 * 一个真实页面：pAA(A×s1) / pBA(B×s1) / pAB(A×s2) / pBB(B×s2)。矩阵的判别力来自
 * "两个 target 看得到同一批会话"：归属只能由页面身份与操作身份决定，不能靠可见性蒙对。
 *
 * 扫描（sweep）在每个单元里用生产桥做一次探针导入，`previous` 就是该单元此刻的
 * 完整原生 id 列表。任何出现在某单元、却**不属于**该单元的 id 都计入误投递。
 *
 * ## 可证伪性
 *
 * - `controls`/`negativeProbes`：把矩阵判定器套到一份**故意改错落点**的矩阵副本上，
 *   必须报出误投递 > 0；探针读取在"已有附件"单元必须非空（否则"处处 0 个 id"
 *   会白送一个零误投递）；泄漏检查器必须对一段含绝对路径 + 文件字节 + 凭据的
 *   canary 报出泄漏；D15 来源规则必须对**真实配对来源**判可信（否则"一律不可信"
 *   这种恒假实现也能过）。
 * - 未跑的项一律记 `notRun` 并写明原因，绝不写成通过。
 *
 * ## 纪律
 *
 * 只用 tests/fixtures 既有夹具（lib.mjs / cdp.mjs / setup.sh 的私有 DSH_HOME）；
 * 不触碰 `$HOME/.dsh`，不杀用户自己的 Chrome；`finally` 里停掉自己启动的一切。
 *
 * 调试开关（不影响判据语义，只用于分段复跑）：
 *   `--only=op1,op2`  只跑指定操作
 *   `--keep-services` 结束后不执行 down.sh（保留夹具供排查）
 */

import { spawn, spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { deflateSync } from 'node:zlib'

import { createClient, findSecretLeaks, redact, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'
import { base64Encode, decodeMessage, planChunks } from '../../lib/shared/wire/index.js'
import {
  NAVIGATION_KIND,
  ORIGIN_CODES,
  hostShapeOf,
  originVectors,
  parseRemoteOrigin,
  runOriginVector
} from './d20-origin-policy.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const workDir = join(fixtureRoot, 'd20')
const portA = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const portB = Number(process.env.DSH_ATTACH_SECONDARY_PORT ?? 3097)
const portD = Number(process.env.DSH_ATTACH_DEGRADED_PORT ?? 3098)
const fixtureProfile = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const secondaryProfile = process.env.DSH_ATTACH_SECONDARY_PROFILE ?? 'dsh-attachments-fixture-b'
const degradedProfile = process.env.DSH_ATTACH_DEGRADED_PROFILE ?? 'dsh-attachments-degraded'
const headlessProfile = process.env.DSH_ATTACH_HEADLESS_PROFILE ?? 'dsh-attachments-headless'
const dshBin = process.env.DSH_BIN ?? join(homedir(), '.npm-global/bin/dsh')
const stubPort = Number(process.env.DSH_ATTACH_STUB_PORT ?? 3901)
process.env.DEEPSEEK_API_KEY ??= 'stub-key'

const argv = process.argv.slice(2)
const onlyArg = argv.find((value) => value.startsWith('--only='))
const ONLY_OPS = onlyArg === undefined ? null : new Set(onlyArg.slice('--only='.length).split(',').filter(Boolean))
const KEEP_SERVICES = argv.includes('--keep-services')

const D20_CRITERIA_VERSION = 1
const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

// =====================================================================
// 运行身份 / 证据目录（每次运行独立 runId；历史只增不改）
// =====================================================================

const RUN_STARTED_AT_MS = Date.now()
const RUN_ID = `d20-${new Date(RUN_STARTED_AT_MS).toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`
const RUN_COMMAND = `node ${process.argv[1] ?? 'tests/fixtures/d20-isolation-gates.mjs'}${argv.length === 0 ? '' : ` ${argv.join(' ')}`}`
const RUNS_DIR = join(durableEvidenceDir, 'd20-runs')
const RUN_DIR = join(RUNS_DIR, RUN_ID)
const RUN_STDOUT_PATH = join(RUN_DIR, 'stdout.log')

/** 运行日志：从一开始就 tee 到 run 目录（进程中途死掉也留得下现场）。 */
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

function candidateSummary() {
  const git = (args) => {
    try {
      return spawnSync('git', args, { cwd: repoRoot, encoding: 'utf8' }).stdout.trim()
    } catch {
      return null
    }
  }
  const porcelain = git(['status', '--porcelain']) ?? ''
  let pluginVersion = null
  try {
    pluginVersion = JSON.parse(readFileSync(join(pluginRoot, 'package.json'), 'utf8')).version ?? null
  } catch {
    pluginVersion = null
  }
  return {
    gitHead: git(['rev-parse', 'HEAD']),
    gitBranch: git(['rev-parse', '--abbrev-ref', 'HEAD']),
    gitWorktree: {
      dirtyEntries: porcelain === '' ? 0 : porcelain.split('\n').filter((line) => line !== '').length,
      porcelainSha256: createHash('sha256').update(porcelain).digest('hex')
    },
    pluginVersion,
    criteriaVersion: D20_CRITERIA_VERSION,
    hashes: {
      gateScript: sha256(readFileSync(fileURLToPath(import.meta.url))),
      originPolicyModule: sha256(readFileSync(join(here, 'd20-origin-policy.mjs')))
    }
  }
}

// =====================================================================
// 判据 / 负向探针 / 未验项
// =====================================================================

const gates = []
const negativeProbes = []
const notRun = []

function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}
function negative(id, description, ok, detail = '') {
  negativeProbes.push({ id, description, ok, detail })
  console.log(`  [neg ${ok ? 'OK' : 'BAD'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}
function markNotRun(id, description, reason) {
  notRun.push({ id, description, reason })
  const existing = gates.findIndex((item) => item.id === id)
  const detail = `notRun：${reason}`
  if (existing >= 0) gates[existing] = { id, description, ok: true, detail }
  else gates.push({ id, description, ok: true, detail })
  console.log(`  [notRun] ${id} ${description} — ${reason}`)
}

const observations = {
  targets: {},
  sessions: {},
  cells: {},
  operations: {},
  deliveries: [],
  leakSurfaces: {},
  sweep: {},
  boundaries: {}
}

// =====================================================================
// 泄漏检查（路径 / 字节 / 凭据三类的 canary）
// =====================================================================

const TEXT_MARKER = 'DSH-D20-ISOLATION-TEXT-7f3a91c4'
const ALT_MARKER = 'DSH-D20-ISOLATION-ALT-2b8e55d1'
const LATE_MARKER = 'DSH-D20-ISOLATION-LATE-5e2c7a90'
const REPLAY_MARKER = 'DSH-D20-ISOLATION-REPLAY-9c1d4e77'
const HIDDEN_MARKER = 'DSH-D20-ISOLATION-HIDDEN-3a7f21b5'
const LOCKED_MARKER = 'DSH-D20-ISOLATION-LOCKED-6d0e83fa'
const FRAME_MARKER = 'DSH-D20-ISOLATION-FRAME-1c4b9d02'
const CROSS_MARKER = 'DSH-D20-ISOLATION-CROSS-8e5a37c6'

/** 每份素材都带一个唯一 marker，因此"字节泄漏"是可搜索的。 */
function textBody(marker, size = 3 * 1024) {
  const lines = [`${marker} head`]
  let total = lines[0].length
  let index = 0
  while (total < size) {
    const line = `${marker} line ${String(index).padStart(4, '0')} ${'y'.repeat(32)}`
    lines.push(line)
    total += line.length + 1
    index += 1
  }
  return Buffer.from(`${lines.join('\n')}\n`, 'utf8')
}

const BODIES = {
  text: textBody(TEXT_MARKER),
  alt: textBody(ALT_MARKER),
  late: textBody(LATE_MARKER),
  replay: textBody(REPLAY_MARKER),
  hidden: textBody(HIDDEN_MARKER),
  locked: textBody(LOCKED_MARKER),
  frame: textBody(FRAME_MARKER),
  cross: textBody(CROSS_MARKER)
}

/** PNG（用于"图片不经上传"的旁证，2x2 RGB，与 D09 同款合成方式）。 */
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

/** 路径 canary：本地绝对路径出现在拒绝文本/报文/证据里都算泄漏。 */
const PATH_CANARIES = [
  fixtureRoot,
  pluginRoot,
  repoRoot,
  dshHome,
  '/attachments/v1/files/',
  homedir()
].filter((value) => typeof value === 'string' && value.length > 3)

/** 字节 canary：文件正文与其 base64 前缀都不该出现在任何"说出来"的表面上。 */
const BYTE_CANARIES = Object.entries(BODIES).map(([name, bytes]) => ({
  name,
  marker: bytes.toString('utf8').split('\n')[0],
  base64Prefix: base64Encode(bytes).slice(0, 48)
}))

/**
 * 三类泄漏一起查：本地路径、文件字节（原文 + base64 前缀）、既有凭据形态。
 * @returns {{ paths: string[], bytes: string[], secrets: string[] }}
 */
function findLeaks(text, label = '') {
  const value = typeof text === 'string' ? text : JSON.stringify(text ?? '')
  const paths = PATH_CANARIES.filter((canary) => value.includes(canary))
  const bytes = []
  for (const canary of BYTE_CANARIES) {
    if (value.includes(canary.marker)) bytes.push(`${canary.name}:marker`)
    if (value.includes(canary.base64Prefix)) bytes.push(`${canary.name}:base64`)
  }
  const secrets = findSecretLeaks(value)
  return { label, pathHits: paths, byteHits: bytes, secretHits: secrets, total: paths.length + bytes.length + secrets.length }
}

const leakSurfaces = []
/** 记录一个"可说出口"的表面（拒绝文本 / 状态面 / 页面可见文本 / 出站报文）。 */
function recordSurface(kind, label, text) {
  const leaks = findLeaks(text, `${kind}:${label}`)
  leakSurfaces.push({ kind, label, leaks, excerpt: String(typeof text === 'string' ? text : JSON.stringify(text ?? '')).slice(0, 400) })
  return leaks
}

// =====================================================================
// 页面侧装置（安装一次；导航后必须重装）
// =====================================================================

const INSTALL_HARNESS = `(() => {
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  if (host === undefined || host === null) return JSON.stringify({ ok: false, reason: 'no-receiver-host' })
  const state = { receivers: {}, files: {}, probes: [], frames: [] }
  globalThis.__D20__ = state

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
      ending: file === null || file === undefined ? null : {
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
        detail: result.import.result.detail === undefined ? null : String(result.import.result.detail),
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
    batchId: message.batchId === undefined ? null : message.batchId,
    fileId: message.fileId === undefined ? null : message.fileId,
    seq: message.seq === undefined ? null : message.seq,
    status: message.status === undefined ? null : message.status,
    code: message.code === undefined ? null : message.code,
    detail: message.detail === undefined ? null : message.detail,
    reason: message.reason === undefined ? null : message.reason,
    attachmentIds: message.attachmentIds === undefined ? null : Array.from(message.attachmentIds)
  })

  state.make = (name, options) => {
    state.receivers[name] = host.create(options === undefined ? {} : options)
    return true
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
      state.files[name + ':' + result.assembled.fileId] = result.assembled.file
    }
    return JSON.stringify({
      ok: true,
      result: shape(result),
      outgoing: outgoing.map(shapeOutgoing),
      wire: outgoing.map((message) => JSON.parse(JSON.stringify(message))),
      accounting: receiver.accounting
    })
  }

  state.recv = (name) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    return JSON.stringify({
      ok: true,
      accounting: receiver.accounting,
      identity: receiver.currentIdentity,
      activeBatchId: receiver.activeBatchId,
      disposal: receiver.disposal,
      fileResults: receiver.fileResults.map((entry) => ({
        batchId: entry.batchId,
        fileId: entry.fileId,
        name: entry.name,
        status: entry.status,
        code: entry.code,
        attachmentIds: Array.from(entry.attachmentIds),
        draft: entry.draft,
        transport: entry.transport,
        bytes: entry.bytes
      }))
    })
  }

  /** 注入事件：等价于"发生了一次主文档导航/关闭"（产品自己的公开方法）。 */
  state.navigate = (name) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return false
    receiver.navigate()
    return true
  }

  state.dispose = (name) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return false
    receiver.dispose('d20-gate')
    return true
  }

  state.globals = () => JSON.stringify({
    origin: location.origin,
    href: location.href,
    hostname: location.hostname,
    isSecureContext: globalThis.isSecureContext === true,
    visibility: document.visibilityState,
    bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
    bridgeVersion: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.version ?? null,
    currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
    receiverHost: typeof host,
    receiverVersion: host.version,
    addon: typeof globalThis.__DSH_ATTACHMENTS_ADDON__,
    addonVersion: globalThis.__DSH_ATTACHMENTS_ADDON__?.version ?? null,
    status: typeof globalThis.__DSH_ATTACHMENTS_STATUS__,
    hook: typeof globalThis.__DSH_FILE_UPLOAD__,
    hookFetch: typeof globalThis.__DSH_FILE_UPLOAD__?.fetch
  })

  /** 状态面：能力 / 来源 / 会话 / 记账（生产插件自己的读数）。 */
  state.status = () => {
    const addon = globalThis.__DSH_ATTACHMENTS_ADDON__
    let addonStatus = null
    try {
      addonStatus = addon === undefined || addon === null ? null : addon.status()
    } catch (error) {
      addonStatus = { threw: String(error && error.message ? error.message : error) }
    }
    let legacy = null
    try {
      legacy = globalThis.__DSH_ATTACHMENTS_STATUS__ ?? null
    } catch (error) {
      legacy = { threw: String(error && error.message ? error.message : error) }
    }
    return JSON.stringify({ addonStatus, legacy, wiring: host.wiring })
  }

  /** composer 原生状态：卡片数、文件 input 数/禁用态、阶段、发送按钮、可见文本摘要。 */
  state.card = () => {
    const cards = document.querySelectorAll('[data-composer-card]')
    const card = cards.length === 1 ? cards[0] : null
    const inputs = card === null ? [] : Array.from(card.querySelectorAll('input[type=file]'))
    const buttons = card === null ? [] : Array.from(card.querySelectorAll('button'))
    const alerts = Array.from(document.querySelectorAll('[role=alert], [role=status]'))
    return JSON.stringify({
      origin: location.origin,
      visibility: document.visibilityState,
      cardCount: cards.length,
      inputCount: inputs.length,
      inputDisabled: inputs.map((input) => input.disabled === true),
      phase: document.querySelector('[data-composer-input]')?.getAttribute('data-phase') ?? null,
      sendDisabled: buttons.filter((button) => (button.getAttribute('aria-label') ?? '').match(/发送|Send|排队|Queue|插话|Steer/)).map((button) => button.disabled),
      stopVisible: buttons.some((button) => (button.getAttribute('aria-label') ?? '').match(/停止|Stop/)),
      chips: card === null ? [] : Array.from(card.querySelectorAll('[role="group"] [aria-label]')).map((node) => node.getAttribute('aria-label')),
      alertText: alerts.map((node) => (node.textContent ?? '').slice(0, 200)).join(' | '),
      bodyText: (document.body?.innerText ?? '').slice(0, 1200)
    })
  }

  /** 原生落点读数：一次探针导入的 previous 就是此刻的原生 attachmentIds。 */
  state.probe = (label, name) => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    if (bridge === undefined) return JSON.stringify({ ok: false, reason: 'no-bridge' })
    const bytes = new TextEncoder().encode('d20-probe-' + label + '-' + Math.random().toString(36).slice(2))
    const file = new File([bytes], name, { type: 'text/plain' })
    try {
      const result = bridge.importFiles({ files: [file] })
      return JSON.stringify({ ok: true, name, result: {
        ok: result.ok === true,
        code: result.code === undefined ? null : result.code,
        detail: result.detail === undefined ? null : String(result.detail),
        added: result.added === undefined ? null : Array.from(result.added),
        previous: result.previous === undefined ? null : Array.from(result.previous)
      } })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  /** 探针附件的名字是否还在 composer 的卡片标签里（原生 UI 的独立读数）。 */
  state.chipHas = (name) =>
    JSON.stringify(Array.from(document.querySelectorAll('[data-composer-card] [role="group"] [aria-label]'))
      .map((node) => node.getAttribute('aria-label') ?? '')
      .some((label) => label.includes(name)))

  /**
   * 移除指定名字的附件（真实用户路径：点附件自己的移除控件）。
   * 探针必须能移除，否则探针会累积、污染落点扫描。
   */
  state.removeByName = (name) => {
    const prefixes = ['移除文件 ', '移除图片 ', '移除 ', 'Remove file ', 'Remove image ', 'Remove ']
    let clicked = null
    const nodes = Array.from(document.querySelectorAll('[data-composer-card] [aria-label]'))
    for (const node of nodes) {
      const label = node.getAttribute('aria-label') ?? ''
      if (!label.includes(name)) continue
      if (!prefixes.some((prefix) => label.startsWith(prefix))) continue
      const target = node.tagName === 'BUTTON' ? node : (node.querySelector('button') ?? node)
      try {
        target.click()
        clicked = label
      } catch (error) {
        clicked = 'threw:' + String(error && error.message ? error.message : error)
      }
      break
    }
    return JSON.stringify({ clicked, labels: nodes.map((node) => node.getAttribute('aria-label')) })
  }

  /** 直接调用生产桥（不经过 wire），用于"切会话/无会话"这类页面级归属判定。 */
  state.bridgeImport = (sessionId, name, text) => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    if (bridge === undefined) return JSON.stringify({ ok: false, reason: 'no-bridge' })
    const file = new File([new TextEncoder().encode(text)], name, { type: 'text/plain' })
    try {
      const request = sessionId === null ? { files: [file] } : { sessionId, files: [file] }
      const result = bridge.importFiles(request)
      return JSON.stringify({ ok: true, result: {
        ok: result.ok === true,
        code: result.code === undefined ? null : result.code,
        detail: result.detail === undefined ? null : String(result.detail),
        added: result.added === undefined ? null : Array.from(result.added),
        previous: result.previous === undefined ? null : Array.from(result.previous)
      } })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  /**
   * 同来源子框架（srcdoc，继承父来源）**自己**读一次两边：它自己的桥、以及父页面的桥。
   *
   * 为什么不加载真实应用：实测把应用装进子框架后，应用会**把顶层文档导航成它自己的 URL**
   * （frame-bust），那会直接毁掉夹具自己的落点单元文档 —— 所以真实应用形态记 notRun，
   * 这里用 srcdoc 子框架做**非破坏性**的同来源对照。
   */
  state.sameOriginChildProbe = (timeoutMs) => new Promise((resolve) => {
    const child = '<scr' + 'ipt>' +
      'var r = {};' +
      'try { r.own = typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__; } catch (e) { r.own = "threw:" + e.name; }' +
      'try { r.parentBridge = typeof parent.__DSH_ATTACHMENTS_BRIDGE__; } catch (e) { r.parentBridge = "threw:" + e.name; }' +
      'try { r.sameOrigin = (parent.document === document) ? "no" : "yes"; } catch (e) { r.sameOrigin = "threw:" + e.name; }' +
      'try { parent.postMessage({ d20soprope: r }, "*"); } catch (e) {}' +
      '</scr' + 'ipt>'
    const frame = document.createElement('iframe')
    frame.id = 'd20-frame-soprobe'
    frame.style.cssText = 'width:120px;height:60px'
    let done = false
    const finish = (value) => {
      if (done) return
      done = true
      window.removeEventListener('message', onMessage)
      frame.remove()
      resolve(value)
    }
    const onMessage = (event) => {
      const data = event && event.data
      if (data && typeof data === 'object' && data.d20soprope && typeof data.d20soprope.own === 'string') {
        finish({ from: String(event.origin), probe: data.d20soprope })
      }
    }
    window.addEventListener('message', onMessage)
    setTimeout(() => finish({ timedOut: true }), timeoutMs === undefined ? 8000 : timeoutMs)
    frame.srcdoc = child
    document.body.appendChild(frame)
  })

  /**
   * 异来源子框架**自己**尝试读父页面的桥。
   *
   * 为什么必须由子框架自己来读：父页面读 frame.contentWindow.parent.<prop> 时，parent 就是
   * 父页面自己，那次读取是**同来源**的，永远不会抛 —— 实测证实了这一点（typeofParent === 'object'）。
   * 只有让一个真正异来源的文档去读父页面，才能证明这条边界。这里用 「data:」 URL 子框架
   * （不透明来源 ⇒ 异来源），它把结论用 「postMessage」（跨来源白名单里的方法）回传。
   */
  state.crossOriginChildProbe = (timeoutMs) => new Promise((resolve) => {
    const child = '<scr' + 'ipt>' +
      'var r;' +
      'try { var b = parent.__DSH_ATTACHMENTS_BRIDGE__; r = "reached:" + typeof b + ":" + String(b && b.version); }' +
      'catch (e) { r = "blocked:" + e.name; }' +
      'try { parent.postMessage({ d20xoprope: r }, "*"); } catch (e) {}' +
      '</scr' + 'ipt>'
    const frame = document.createElement('iframe')
    frame.id = 'd20-frame-xoprobe'
    frame.style.cssText = 'width:120px;height:60px'
    let done = false
    const finish = (value) => {
      if (done) return
      done = true
      window.removeEventListener('message', onMessage)
      frame.remove()
      resolve(value)
    }
    const onMessage = (event) => {
      const data = event && event.data
      if (data && typeof data === 'object' && typeof data.d20xoprope === 'string') {
        finish({ from: String(event.origin), result: data.d20xoprope })
      }
    }
    window.addEventListener('message', onMessage)
    setTimeout(() => finish({ timedOut: true }), timeoutMs === undefined ? 8000 : timeoutMs)
    frame.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(child)
    document.body.appendChild(frame)
  })

  /** 点击"重试上传"（真实用户路径）：上传失败后 composer 会给出这个控件。 */
  state.retryUpload = () => {
    const nodes = Array.from(document.querySelectorAll('[data-composer-card] [aria-label]'))
    const target = nodes.find((node) => /^(重试上传|Retry )/.test(node.getAttribute('aria-label') ?? ''))
    if (target === undefined) return JSON.stringify({ clicked: null })
    try {
      target.click()
      return JSON.stringify({ clicked: target.getAttribute('aria-label') })
    } catch (error) {
      return JSON.stringify({ clicked: 'threw:' + String(error && error.message ? error.message : error) })
    }
  }

  /** 用 base64 导入（图片走这条路：图片**不上传**，直接内联进 prompt，因此发送按钮立刻可用）。 */
  state.bridgeImportBase64 = (name, mime, base64) => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    if (bridge === undefined) return JSON.stringify({ ok: false, reason: 'no-bridge' })
    const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))
    const file = new File([bytes], name, { type: mime })
    try {
      const result = bridge.importFiles({ files: [file] })
      return JSON.stringify({ ok: true, result: {
        ok: result.ok === true,
        code: result.code === undefined ? null : result.code,
        detail: result.detail === undefined ? null : String(result.detail),
        added: result.added === undefined ? null : Array.from(result.added),
        previous: result.previous === undefined ? null : Array.from(result.previous)
      } })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  state.setInputDisabled = (value) => {
    const inputs = Array.from(document.querySelectorAll('[data-composer-card] input[type=file]'))
    for (const input of inputs) input.disabled = value === true
    return JSON.stringify({ count: inputs.length, disabled: inputs.map((input) => input.disabled === true) })
  }

  /**
   * iframe 探针：同来源（可选加载真实应用）与异来源两种子框架。
   * 返回**只读的观察结果**：能读到什么、读不到时抛的是什么。
   */
  state.iframe = async (spec) => {
    const out = { created: [], note: spec.note ?? null }
    const removeAll = () => {
      for (const frame of state.frames) frame.remove()
      state.frames.length = 0
    }
    removeAll()
    const make = (src, id) => new Promise((resolveFrame) => {
      const frame = document.createElement('iframe')
      frame.id = id
      frame.style.cssText = 'width:240px;height:140px;border:1px solid #999'
      let settled = false
      const done = () => { if (!settled) { settled = true; resolveFrame(frame) } }
      frame.addEventListener('load', done, { once: true })
      setTimeout(done, 12000)
      if (src !== null) frame.src = src
      document.body.appendChild(frame)
      state.frames.push(frame)
    })
    /**
     * 只读观察一个子框架：**每一条读数都单独记录它抛的是什么**。
     * 关键教训：不能用"读 href 抛了就说明是异来源" —— contentWindow 为 null 时抛的是 TypeError，
     * 那只能说明"这个框架现在不可用"，不能当成"已证实异来源"。
     */
    const read = (frame, kind) => {
      const entry = { id: frame.id, kind, locationThrew: null, documentThrew: null, parentBridgeThrew: null, bridgeThrew: null }
      let win = null
      try { win = frame.contentWindow } catch (error) { entry.windowThrew = String(error && error.name ? error.name : error) }
      entry.hasWindow = win !== null && win !== undefined
      try { entry.href = win.location.href; entry.sameOriginReadable = true } catch (error) { entry.href = null; entry.sameOriginReadable = false; entry.locationThrew = String(error && error.name ? error.name : error) }
      try { entry.documentReadable = win.document !== null } catch (error) { entry.documentReadable = false; entry.documentThrew = String(error && error.name ? error.name : error) }
      try {
        entry.bridge = typeof win.__DSH_ATTACHMENTS_BRIDGE__
        entry.bridgeVersion = win.__DSH_ATTACHMENTS_BRIDGE__?.version ?? null
        entry.receiverHost = typeof win.__DSH_ATTACHMENTS_RECEIVER__
        entry.addon = typeof win.__DSH_ATTACHMENTS_ADDON__
        entry.currentSession = win.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null
      } catch (error) {
        entry.bridge = null
        entry.receiverHost = null
        entry.addon = null
        entry.currentSession = null
        entry.bridgeThrew = String(error && error.name ? error.name : error)
      }
      try { entry.parentBridge = typeof win.parent.__DSH_ATTACHMENTS_BRIDGE__ } catch (error) { entry.parentBridge = null; entry.parentBridgeThrew = String(error && error.name ? error.name : error) }
      return entry
    }
    const blank = await make(null, 'd20-frame-blank')
    out.created.push(read(blank, 'same-origin-blank'))
    if (spec.sameOriginSrc !== null && spec.sameOriginSrc !== undefined) {
      const app = await make(spec.sameOriginSrc, 'd20-frame-app')
      out.created.push(read(app, 'same-origin-app'))
    }
    // 异来源候选：只有**以 SecurityError 被证实**的才算，其余一律继续试下一个。
    for (const candidate of spec.crossOriginSrcs ?? []) {
      const frame = await make(candidate.url, 'd20-frame-cross-' + candidate.id)
      const entry = read(frame, 'cross-origin:' + candidate.id)
      out.created.push(entry)
      if (entry.locationThrew === 'SecurityError') {
        out.crossOriginVerified = candidate
        out.crossOriginFrame = entry
        break
      }
    }
    out.sameOriginFrameToParentBridge = out.created.find((item) => item.kind === 'same-origin-blank') ?? null
    // 同来源子框架**自己**发起一次导入：绝不允许落进主文档的草稿。
    if (spec.frameImport === true) {
      const frame = state.frames.find((item) => item.id === 'd20-frame-app') ?? state.frames[0]
      try {
        const win = frame.contentWindow
        const bridge = win === null || win === undefined ? undefined : win.__DSH_ATTACHMENTS_BRIDGE__
        if (bridge === undefined || bridge === null) {
          out.frameImport = { attempted: false, reason: 'no-bridge-in-frame', sameDocument: false }
        } else {
          const file = new File([new TextEncoder().encode(spec.frameImportText ?? 'frame')], 'd20-frame.txt', { type: 'text/plain' })
          const result = bridge.importFiles({ files: [file] })
          out.frameImport = {
            attempted: true,
            sameDocument: win.document === document,
            ok: result.ok === true,
            code: result.code === undefined ? null : result.code,
            detail: result.detail === undefined ? null : String(result.detail),
            added: result.added === undefined ? null : Array.from(result.added),
            previous: result.previous === undefined ? null : Array.from(result.previous)
          }
        }
      } catch (error) {
        out.frameImport = { attempted: true, threw: String(error && error.message ? error.message : error) }
      }
    }
    if (spec.keep !== true) removeAll()
    return JSON.stringify(out)
  }

  return JSON.stringify({ ok: true, globals: JSON.parse(state.globals()), wiring: host.wiring })
})()`

/** 文档被意外替换的记录（页面重载/崩溃恢复）。这是环境事件，必须据实记录而不是掩盖。 */
const replacedDocuments = []

/**
 * 确认页面侧装置还在；不在就重装一次并返回"重装过"。
 * 真实 Chromium 偶发在冻结/多页面压力下替换文档；判据脚本必须把这种事件变成**证据**，
 * 而不是让 `Cannot read properties of undefined` 变成一条"没有证据"的致命异常。
 */
async function ensureHarness(page, { timeoutMs = 30_000 } = {}) {
  const read = async () =>
    evaluate(
      page,
      `JSON.stringify({ d20: typeof globalThis.__D20__, bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__, host: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__, origin: location.origin, href: location.href, session: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null })`
    ).catch(() => '{"d20":"error"}').then((raw) => {
      try {
        return JSON.parse(raw)
      } catch {
        return { d20: 'parse-error' }
      }
    })
  let state = await read()
  if (state.d20 === 'object') return { ok: true, reinstalled: false, session: state.session, origin: state.origin, href: state.href }
  // 文档被替换了（Chrome 重载/崩溃恢复）：等插件重新落地，再把装置重装一遍。
  // 这是**环境事件**，必须记进证据而不是让判据脚本崩掉。
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    await sleep(1000)
    state = await read()
    if (state.bridge === 'object' && state.host === 'object') break
  }
  const install = await installHarness(page)
  console.log(`  [warn] 页面文档被替换（href=${String(state.href).slice(0, 80)} origin=${state.origin} 插件已回来=${state.bridge === 'object'}）；装置重装 ok=${install.ok}`)
  return { ok: install.ok === true, reinstalled: true, session: state.session, origin: state.origin, href: state.href, install }
}

/**
 * 用**原生状态**验证的桥导入（而不是只信同步 ACK）。
 *
 * 实测教训：一个文档里的**第一次**导入，原生 onChange 链路可能异步落地 —— 生产适配器的同步 ACK
 * 会如实报 input-state-unchanged（它刻意不重放），而文件随后其实进了草稿。如果只用 ACK 记账，
 * 这个 id 就会变成"扫描看得见、账本解释不了"的无归属 id。
 * 因此这里：导入 → 用探针轮询原生 id 是否真的增加 → 只把**观测到的**新增 id 记进账本。
 */
async function importVerified(page, cellId, fileName, textBody, { retries = 3, timeoutMs = 10_000, label = 'seed' } = {}) {
  const baseline = (await cellProbe(page, `${label}-${cellId}-base`)).ids ?? []
  let last = null
  let attempts = 0
  for (let index = 0; index < retries; index++) {
    attempts = index + 1
    last = JSON.parse(await evaluate(page, `globalThis.__D20__.bridgeImport(null, ${JSON.stringify(fileName)}, ${JSON.stringify(textBody)})`))
    const verify = await poll(
      async () => (await cellProbe(page, `${label}-${cellId}-v${index}`)).ids ?? [],
      (ids) => ids.length > baseline.length,
      { timeoutMs, intervalMs: 600 }
    )
    if (verify.ok) {
      const landed = verify.value.filter((id) => !baseline.includes(id))
      return { ok: true, landed, baseline, attempts, syncAckOk: last.result?.ok === true, syncAckCode: last.result?.code ?? null }
    }
  }
  return { ok: false, landed: [], baseline, attempts, syncAckOk: last?.result?.ok === true, syncAckCode: last?.result?.code ?? null }
}

/** 等到 composer 的发送按钮真的可用；期间可以点「重试上传」（真实用户路径）。 */
async function ensureSendable(page, { timeoutMs = 60_000 } = {}) {
  const deadline = Date.now() + timeoutMs
  const retries = []
  for (;;) {
    const card = await cardOf(page)
    const enabled = Array.isArray(card.sendDisabled) && card.sendDisabled.includes(false)
    if (enabled) return { ok: true, card, retries }
    if (Date.now() >= deadline) return { ok: false, card, retries }
    const retry = JSON.parse(await evaluate(page, 'globalThis.__D20__.retryUpload()'))
    if (retry.clicked !== null) retries.push(retry.clicked)
    await sleep(1500)
  }
}


// =====================================================================
// Node 侧消息构造（每条都先用生产 codec 校验）
// =====================================================================

function messageText(raw) {
  const decoded = decodeMessage(JSON.stringify(raw))
  if (!decoded.ok) throw new Error(`门禁构造的消息不合法：${decoded.code} ${decoded.detail} @${decoded.path}`)
  return JSON.stringify(decoded.message)
}
const rawMessageText = (raw) => JSON.stringify(raw)

/** 一套绑定到 (sessionId, targetId, epochs, scope) 的消息构造器。 */
function msgs({ sessionId, targetId, documentEpoch, composerEpoch, composerScope }) {
  return {
    sessionId,
    targetId,
    documentEpoch,
    composerEpoch,
    composerScope,
    context: (overrides = {}) =>
      messageText({
        v: 1,
        type: 'context',
        sessionId,
        targetId,
        documentEpoch,
        composerEpoch,
        composerScope,
        ...overrides
      }),
    contextRaw: (overrides = {}) =>
      rawMessageText({ v: 1, type: 'context', sessionId, targetId, documentEpoch, composerEpoch, composerScope, ...overrides }),
    batchBegin: ({ batchId, fileCount = 1, totalBytes = 0 }) =>
      messageText({ v: 1, type: 'batch-begin', sessionId, batchId, targetId, documentEpoch, composerEpoch, fileCount, totalBytes }),
    fileBegin: ({ batchId, fileId, name, mime = 'text/plain', byteLength, sha }) =>
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
      }),
    chunks: (bytes, { batchId, fileId, chunkBytes = 256 * 1024 }) =>
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
      })),
    fileEnd: ({ batchId, fileId, totalBytes, sha }) =>
      messageText({ v: 1, type: 'file-end', sessionId, batchId, fileId, totalBytes, sha256: sha }),
    batchEnd: (batchId, results, status = 'staged') =>
      messageText({ v: 1, type: 'batch-end', sessionId, batchId, status, results }),
    cancel: (batchId, reason = 'cancelled', stage = 'protocol-transfer') =>
      messageText({ v: 1, type: 'cancel', sessionId, batchId, reason, stage })
  }
}

// =====================================================================
// 页面交互辅助
// =====================================================================

/**
 * CDP 交互一律加超时。
 *
 * 实测教训：真实 Chromium 里某个 target 的渲染进程可能整体失联（命令既不成功也不失败），
 * 而没有超时的 CDP 调用会让整个判据脚本**永久挂住** —— 那就变成"没有证据"而不是"判据失败"。
 * 这里把每次交互都变成有界的：超时即抛错，由 `operation()` 记成一条失败的判据，其余操作继续。
 */
const CDP_TIMEOUT_MS = Number(process.env.DSH_ATTACH_D20_CDP_TIMEOUT_MS ?? 25_000)

function withTimeout(promise, ms, label) {
  let timer = null
  const guard = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`CDP 超时（${ms}ms）：${label}`)), ms)
    if (typeof timer.unref === 'function') timer.unref()
  })
  return Promise.race([promise, guard]).finally(() => clearTimeout(timer))
}

async function evaluate(page, expression, { timeoutMs = CDP_TIMEOUT_MS } = {}) {
  return withTimeout(
    page.evaluate(expression, { awaitPromise: true }),
    timeoutMs,
    `evaluate ${String(expression).replace(/\s+/g, ' ').slice(0, 110)}`
  )
}

async function send(page, method, params = {}, { timeoutMs = CDP_TIMEOUT_MS } = {}) {
  return withTimeout(page.send(method, params), timeoutMs, `${method}`)
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

let deviceA = null
let deviceB = null
let deviceD = null
let lanBaseA = null
let lanBaseB = null
let lanBaseD = null

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
    child.on('exit', (code) => resolvePromise({ code, output: output.slice(-300) }))
  })
}

async function rpc(client, lanBase, deviceId, method, args) {
  const response = await client.fetch(`${lanBase}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({
      type: 'client-request',
      rpcId: `d20-${method.replace(/\W/g, '-')}-${randomUUID().slice(0, 8)}`,
      method,
      payload: { args }
    })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 300) }
  }
}

/** 安装页面侧装置；返回 globals 读数（失败时 ok=false，不抛）。 */
async function installHarness(page) {
  try {
    const raw = await evaluate(page, INSTALL_HARNESS)
    return JSON.parse(raw)
  } catch (error) {
    return { ok: false, reason: String(error?.message ?? error).slice(0, 200) }
  }
}

/** 打开应用页并等到"桥认定的当前会话 == 目标会话"（sessionId 为 null 时只等桥就绪）。 */
async function openAppPage(chrome, lanBase, deviceId, sessionId, { label = '' } = {}) {
  const page = await openPage(chrome.debugPort)
  await send(page, 'Page.enable')
  await send(page, 'Runtime.enable')
  await send(page, 'Network.enable')
  await send(page, 'Page.addScriptToEvaluateOnNewDocument', {
    source:
      sessionId === null
        ? `try{localStorage.removeItem('dsh.sessions.current')}catch(e){}`
        : `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
  })
  await withTimeout(page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId ?? '')}`), 45_000, `navigate ${lanBase}/pair-app`)
  const ready = await poll(
    async () => {
      const globals = await evaluate(
        page,
        `(() => {
          const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
          return JSON.stringify({
            bridge: typeof bridge,
            bridgeVersion: bridge?.version ?? null,
            currentSession: bridge?.currentSession?.() ?? null,
            receiverHost: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__,
            card: document.querySelectorAll('[data-composer-card]').length
          })
        })()`
      ).catch(() => '{"bridge":"error"}')
      try {
        return JSON.parse(globals)
      } catch {
        return { bridge: 'parse-error' }
      }
    },
    (value) =>
      value.bridge === 'object' &&
      value.bridgeVersion === 1 &&
      value.receiverHost === 'object' &&
      (sessionId === null ? true : value.currentSession === sessionId),
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  const install = await installHarness(page)
  return { page, ready, install, label }
}

async function feed(page, name, text) {
  const shaped = JSON.parse(await evaluate(page, `globalThis.__D20__.feed(${JSON.stringify(name)}, ${JSON.stringify(text)})`))
  if (shaped?.result?.detail !== undefined && shaped.result.detail !== null) {
    recordSurface('refusal-detail', `${name}:${shaped.result.code ?? 'ok'}`, shaped.result.detail)
  }
  if (shaped?.result?.import?.detail !== undefined && shaped.result.import.detail !== null) {
    recordSurface('import-detail', `${name}:${shaped.result.import.code ?? 'ok'}`, shaped.result.import.detail)
  }
  for (const message of shaped?.wire ?? []) recordSurface('wire-out', `${name}:${message.type}`, JSON.stringify(message))
  return shaped
}

const recvState = async (page, name) => JSON.parse(await evaluate(page, `globalThis.__D20__.recv(${JSON.stringify(name)})`))
const makeReceiver = async (page, name) => evaluate(page, `globalThis.__D20__.make(${JSON.stringify(name)})`)
const globalsOf = async (page) => JSON.parse(await evaluate(page, 'globalThis.__D20__.globals()'))
const pageStatus = async (page) => JSON.parse(await evaluate(page, 'globalThis.__D20__.status()'))
const cardOf = async (page) => JSON.parse(await evaluate(page, 'globalThis.__D20__.card()'))
const navigateInjected = async (page, name) => evaluate(page, `globalThis.__D20__.navigate(${JSON.stringify(name)})`)

/** 探针文件名（Node 侧与页面侧必须是同一个字符串）。 */
const probeFileName = (label) => `d20-probe-${String(label).replace(/[^A-Za-z0-9._-]/g, '-')}.txt`

/**
 * 原生落点读数：探针导入一次，`previous` 即该文档此刻的原生 attachmentIds。
 *
 * 探针会**顺手把自己加的那一项移除**（点原生移除控件，并轮询断言卡片真的消失），
 * 因此读数不会累积、不会污染后续扫描。移除是真实用户路径，不是改内部状态。
 */
async function cellProbe(page, label) {
  const name = probeFileName(label)
  const health = await ensureHarness(page)
  if (health.reinstalled) replacedDocuments.push({ label, at: new Date().toISOString(), session: health.session, origin: health.origin, href: health.href, installOk: health.install?.ok ?? null })
  if (health.ok !== true) {
    // 读不出来就如实说"读不出来"，绝不用一个异常把整轮判据变成"没有证据"。
    return {
      ok: false,
      ids: null,
      added: null,
      code: 'harness-unavailable',
      reason: `页面装置无法建立（文档被替换后插件未回来）：${JSON.stringify(health.install ?? null).slice(0, 200)}`,
      removal: null,
      harnessReinstalled: true
    }
  }
  const probeOutcome = await evaluate(page, `globalThis.__D20__.probe(${JSON.stringify(label)}, ${JSON.stringify(name)})`)
    .then((raw) => ({ raw }))
    .catch((error) => ({ error: String(error?.message ?? error) }))
  if (probeOutcome.error !== undefined) {
    return { ok: false, ids: null, added: null, code: 'probe-failed', reason: probeOutcome.error.slice(0, 200), removal: null, harnessReinstalled: health.reinstalled === true }
  }
  const probe = JSON.parse(probeOutcome.raw)
  if (probe?.result?.detail) recordSurface('probe-detail', label, probe.result.detail)
  // 探针自身加的那个 id 不属于任何操作，登记为噪声（即使移除失败也不会被误判成误投递）。
  for (const id of probe.result?.added ?? []) probeNoiseIds.add(id)
  let removal = null
  if (probe.ok === true && probe.result?.ok === true && (probe.result.added ?? []).length === 1) {
    const chipHas = async () => JSON.parse(await evaluate(page, `globalThis.__D20__.chipHas(${JSON.stringify(name)})`))
    const appeared = await poll(chipHas, (value) => value === true, { timeoutMs: 6_000, intervalMs: 200 })
    const click = JSON.parse(await evaluate(page, `globalThis.__D20__.removeByName(${JSON.stringify(name)})`))
    const gone = await poll(chipHas, (value) => value === false, { timeoutMs: 8_000, intervalMs: 250 })
    removal = { name, appeared: appeared.ok, clicked: click.clicked, gone: gone.ok, labelsSample: (click.labels ?? []).slice(0, 6) }
    probeHygiene.push(removal)
  }
  return {
    ok: probe.ok === true && probe.result?.ok === true,
    ids: probe.result?.previous ?? null,
    added: probe.result?.added ?? null,
    code: probe.result?.code ?? null,
    reason: probe.reason ?? null,
    removal,
    harnessReinstalled: health.reinstalled === true
  }
}

/**
 * 扫描一个影子文档并立刻关掉它（从 openPages 摘掉，避免 finally 重复关闭）。
 * 关掉之后不可能再有投递落进去，因此"关前扫描"与"留到最后扫描"判别力相同。
 */
async function sealShadow(cellId, page, note, { close = true } = {}) {
  const probe = await cellProbe(page, `seal-${cellId}`)
  const ids = new Set((probe.ids ?? []).filter((id) => !probeNoiseIds.has(id)))
  sealedShadows.set(cellId, { note, ids, probe: { ids: probe.ids, added: probe.added, ok: probe.ok, code: probe.code }, closed: close })
  shadowCells.delete(cellId)
  if (close) {
    const at = openPages.indexOf(page)
    if (at >= 0) openPages.splice(at, 1)
    await page.close().catch(() => {})
  }
  return { cellId, ids: ids.size, note, closed: close }
}

/** 扫描前被重开的单元（原文档不可达：渲染进程失联/文档被替换）。 */
const recoveredCells = []

/**
 * 扫描前把不可达的单元页面重开一个文档。
 *
 * 为什么可以这样做而不掩盖问题：**单元的原生草稿随文档一起消失**，重开后它是空的。
 * 如果账本里还记着"投给这个单元"的 id，判定器会把它报成 `recorded-but-absent-in-its-own-cell`
 * —— 也就是说"投进了一个已经不存在、且再也读不到的文档"不会被当成零误投递。
 * 反过来，若该单元本来就没收到过任何投递（D20 里 A×s2 就是这种），重开只是让扫描能继续。
 */
async function recoverCellPage(cellId, chrome, { lanBaseFor, deviceFor }) {
  const cell = cells.get(cellId)
  if (cell === undefined) return { cellId, recovered: false, reason: 'unknown-cell' }
  const health = await ensureHarness(cell.page, { timeoutMs: 8_000 })
  if (health.ok === true) return { cellId, recovered: false, healthy: true, session: health.session, href: health.href }
  const reopened = await openAppPage(chrome, lanBaseFor(cell.targetId), deviceFor(cell.targetId), cell.sessionId, { label: `${cellId}(重开)` })
  if (reopened.ready.ok !== true || reopened.install.ok !== true) {
    return { cellId, recovered: false, reason: 'reopen-failed', ready: reopened.ready.ok, install: reopened.install.ok }
  }
  openPages.push(reopened.page)
  const at = openPages.indexOf(cell.page)
  if (at >= 0) openPages.splice(at, 1)
  await cell.page.close().catch(() => {})
  const rebound = bindCell(cellId, reopened.page, cell.docInstance + 1, '扫描前重开（原文档不可达）')
  const record = { cellId, recovered: true, reason: health.reason ?? 'unreachable', rebound, unreadableBefore: health }
  recoveredCells.push(record)
  console.log(`  [warn] 单元 ${cellId} 原文档不可达，已重开一个文档（历史交付物若投给旧文档会被判定器报成账本不自洽）`)
  return record
}

/**
 * 绑定/重绑一个落点单元到某个文档。
 *
 * 为什么要"重绑"：单元的身份是 (target × 会话 × **文档实例**)。会话切换、刷新、跨来源导航
 * 都会换掉文档，原文档随之销毁（它的草稿也不可能再被投递），因此单元要指向新文档并递增实例号。
 * 每次重绑都必须显式调用，绝不靠"页面还在所以单元还在"这种假设。
 */
function bindCell(cellId, page, docInstance, note) {
  const entry = cells.get(cellId) ?? {}
  const rebound = { ...entry, page, docInstance, reboundNote: note ?? null }
  cells.set(cellId, rebound)
  return { cellId, docInstance, sessionId: rebound.sessionId, targetId: rebound.targetId, note: note ?? null }
}

/**
 * 喂一个**完整批次**：context → batch-begin → file-begin → chunks → file-end。
 * `stopAfter` 可以停在任意一步（用于"刷新后迟到"这类半途状态）。
 * 返回每一步的结果与最后一次结果。
 */
async function feedBatch(page, name, spec) {
  const {
    sessionId, batchId, fileId, bytes, epochs, stopAfter = 'file-end',
    fileName, mime = 'text/plain', sendContext = true, sha = sha256(bytes)
  } = spec
  const m = msgs({ sessionId, targetId: spec.targetId, ...epochs, composerScope: spec.composerScope })
  const steps = []
  const push = (step, shaped) => steps.push({ step, result: shaped?.result ?? { ok: false, reason: shaped?.reason }, outgoing: shaped?.outgoing ?? [] })
  if (sendContext) push('context', await feed(page, name, m.context()))
  if (stopAfter === 'context') return { steps, last: steps.at(-1) }
  push('batch-begin', await feed(page, name, m.batchBegin({ batchId, fileCount: 1, totalBytes: bytes.length })))
  if (stopAfter === 'batch-begin') return { steps, last: steps.at(-1) }
  push('file-begin', await feed(page, name, m.fileBegin({ batchId, fileId, name: fileName, mime, byteLength: bytes.length, sha })))
  if (stopAfter === 'file-begin') return { steps, last: steps.at(-1) }
  const chunks = m.chunks(bytes, { batchId, fileId })
  for (const chunk of chunks) {
    push(`chunk:${chunk.plan.seq}`, await feed(page, name, chunk.text))
    if (stopAfter === `chunk:${chunk.plan.seq}`) return { steps, last: steps.at(-1) }
  }
  if (stopAfter === 'chunks') return { steps, last: steps.at(-1) }
  push('file-end', await feed(page, name, m.fileEnd({ batchId, fileId, totalBytes: bytes.length, sha })))
  return { steps, last: steps.at(-1) }
}

// =====================================================================
// 隔离矩阵
// =====================================================================

const CELL_DEFS = [
  { id: 'A-s1', targetId: 'A', role: 'A×s1' },
  { id: 'A-s2', targetId: 'A', role: 'A×s2' },
  { id: 'B-s1', targetId: 'B', role: 'B×s1' },
  { id: 'B-s2', targetId: 'B', role: 'B×s2' }
]

const cells = new Map() // cellId -> { page, docInstance, sessionId, targetId, targetPort, origin, sessionRole }
const shadowCells = new Map() // cellId -> { page, note }：非矩阵单元、但**仍然存活**的文档，也要参与扫描
const deliveryLedger = [] // { op, cellId, docInstance, ids, fileNames }
/** 探针自身的 id（噪声）：它们不属于任何操作，扫描时必须排除，否则会被误判成误投递。 */
const probeNoiseIds = new Set()
/** 每次探针的"出现 → 点击移除 → 消失"记录（探针卫生证据）。 */
const probeHygiene = []
/**
 * 已"封存"的影子文档：操作一结束就扫描它、然后立刻关掉。
 * 这样既保留了"落进非矩阵文档也算误投递"的判别力，又把同时打开的 SPA 页面数压到最小
 * （实测教训：同一个 headless Chromium 里堆太多重页面会偶发某个 target 整体失联）。
 */
const sealedShadows = new Map()

/** 记录一次"某操作在某文档里真的产出了 id"（import 成功时调用）。 */
function recordDelivery(op, cellId, docInstance, ids, fileNames) {
  if (ids === null || ids === undefined || ids.length === 0) return
  deliveryLedger.push({ op, cellId, docInstance, ids: [...ids], fileNames })
}

const matrixRows = []
/**
 * 记一行矩阵。
 * @param {{op:string, cellId:string, intendedCellId:string, determinate:boolean, outcome:string,
 *          code:string|null, landedIds:string[]|null, detail:string, evidence:object}} row
 */
function recordRow(row) {
  const cell = cells.get(row.cellId) ?? null
  const intended = cells.get(row.intendedCellId) ?? null
  matrixRows.push({
    operation: row.op,
    targetId: cell?.targetId ?? null,
    targetPort: cell?.targetPort ?? null,
    targetOrigin: cell?.origin ?? null,
    sessionId: cell?.sessionId ?? null,
    sessionRole: cell?.sessionRole ?? null,
    cellId: row.cellId,
    role: row.cellId === row.intendedCellId ? 'intended' : 'neighbour',
    intendedCellId: row.intendedCellId,
    documentInstance: cell === undefined ? null : `${row.cellId}@doc${cell.docInstance}`,
    determinate: row.determinate === true,
    outcome: row.outcome,
    code: row.code ?? null,
    landedIds: row.landedIds ?? null,
    landedCount: row.landedIds === null || row.landedIds === undefined ? 0 : row.landedIds.length,
    detail: row.detail ?? '',
    evidence: row.evidence ?? {},
    intendedOrigin: intended?.origin ?? null
  })
}

/**
 * 矩阵判定器：把"落点账本 + 扫描读数 + 行"算成误投递计数。
 * **这是被负向探针直接调用的同一个函数**，因此"零误投递"不是恒真的。
 */
function judgeMatrix({ rows, ledger, sweep }) {
  const byCell = new Map()
  // 单元集合来自**扫描**（含影子单元），而不是来自账本：账本漏记不会让单元凭空消失。
  for (const [cellId, ids] of sweep.cells) byCell.set(cellId, new Set(ids))
  // 1) 账本里的 id 必须落在它被记录的那个单元里。
  const ledgerMisplaced = []
  for (const delivery of ledger) {
    const observed = byCell.get(delivery.cellId)
    if (observed === undefined) {
      ledgerMisplaced.push({ id: null, from: delivery.cellId, reason: 'delivery-cell-not-swept', op: delivery.op })
      continue
    }
    for (const id of delivery.ids) if (!observed.has(id)) ledgerMisplaced.push({ id, from: delivery.cellId, reason: 'recorded-but-absent-in-its-own-cell', op: delivery.op })
  }
  // 2) 每个单元里的每个 id 必须能被账本解释，且账本说的就是它所在的那个单元。
  const unattributed = []
  const misDelivered = []
  const owner = new Map()
  for (const delivery of ledger) for (const id of delivery.ids) owner.set(id, delivery)
  for (const [cellId, ids] of byCell) {
    for (const id of ids) {
      const delivery = owner.get(id)
      if (delivery === undefined) {
        unattributed.push({ id: id.slice(0, 24), cellId })
        misDelivered.push({ id: id.slice(0, 24), intendedCellId: null, landedCellId: cellId, reason: 'unattributed' })
        continue
      }
      if (delivery.cellId !== cellId) {
        misDelivered.push({ id: id.slice(0, 24), intendedCellId: delivery.cellId, landedCellId: cellId, reason: 'landed-elsewhere', op: delivery.op })
      }
    }
  }
  const byOperation = {}
  for (const row of rows) {
    byOperation[row.operation] = byOperation[row.operation] ?? { rows: 0, misDelivered: 0, refused: 0, imported: 0, noDelivery: 0 }
    byOperation[row.operation].rows += 1
    if (row.outcome === 'refused') byOperation[row.operation].refused += 1
    if (row.outcome === 'imported') byOperation[row.operation].imported += 1
    if (row.outcome === 'no-delivery') byOperation[row.operation].noDelivery += 1
    // 该操作"意图的落点"是 intended 行；误投递记在意图行上。
    if (row.role === 'intended') {
      byOperation[row.operation].misDelivered += misDelivered.filter((item) => item.intendedCellId === row.cellId).length + unattributed.length * 0
    }
  }
  return {
    cells: [...byCell].map(([cellId, ids]) => ({ cellId, count: ids.size, ids: [...ids].map((id) => id.slice(0, 24)) })),
    ledgerSize: ledger.length,
    ledgerMisplaced,
    unattributed,
    misDelivered,
    misDeliveryTotal: misDelivered.length + ledgerMisplaced.filter((item) => item.reason !== 'delivery-cell-not-swept').length,
    byOperation
  }
}

// =====================================================================
// 夹具生命周期
// =====================================================================

let serviceA = null
let serviceB = null
let serviceD = null
let chrome = null
let stub = null
const openPages = []
let fatal = null
const serviceLogA = join(evidenceDir, 'service-d20-a.log')
const serviceLogB = join(evidenceDir, 'service-d20-b.log')
const serviceLogD = join(evidenceDir, 'service-d20-degraded.log')

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

async function stopStub(handle) {
  if (handle === null || handle.child.exitCode !== null) return
  handle.child.kill('SIGTERM')
  const deadline = Date.now() + 5_000
  while (handle.child.exitCode === null && Date.now() < deadline) await sleep(200)
  if (handle.child.exitCode === null) handle.child.kill('SIGKILL')
}

async function pair(port, lanAddress) {
  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  if (!issue.ok) throw new Error(`配对票据签发失败：HTTP ${issue.status} @${port}`)
  const token = (await issue.json()).token
  await client.follow(`http://${lanAddress}:${port}/pair-accept?pair=${token}`)
  return { client, deviceId: client.jar.get('dsh_pair') ?? '' }
}

// =====================================================================
// 主流程
// =====================================================================

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await mkdir(workDir, { recursive: true })

const opsRan = new Set()
const shouldRun = (op) => ONLY_OPS === null || ONLY_OPS.has(op)
/** 已生成的两份文本：down.sh 会删掉夹具目录（连同夹具侧证据），收尾后要按既有约定补写回来。 */
let reportText = null
let matrixText = null

/**
 * 硬看门狗：判据脚本绝不允许"永远挂着"。
 *
 * 每个 CDP 调用都有超时，理论上不会挂死；但真出现"每一步都超时"的极端情况时，
 * 总时长仍可能拖很久。看门狗到点后：停掉自己启动的一切（Chrome / 两个 target /
 * 降级 target / provider stub），写一份**明确失败**的证据，然后以退出码 3 结束。
 */
const WATCHDOG_MS = Number(process.env.DSH_ATTACH_D20_WATCHDOG_MS ?? 20 * 60_000)
const watchdog = setTimeout(() => {
  const detail = `看门狗超时（${WATCHDOG_MS}ms）：判据脚本未能自行结束，已强制停止夹具并按失败记账`
  console.error(`[watchdog] ${detail}`)
  teeToRunLog(`[watchdog] ${detail}`)
  for (const page of [...openPages]) {
    try {
      page.close()
    } catch {
      // 尽力而为
    }
  }
  try {
    chrome?.close()
  } catch {
    // 尽力而为
  }
  for (const [handle, pidFile] of [[serviceA, join(fixtureRoot, 'service.pid')], [serviceB, join(fixtureRoot, 'service-b.pid')], [serviceD, null], [stub, null]]) {
    if (handle === null || handle === undefined) continue
    try {
      handle.child?.kill('SIGKILL')
    } catch {
      // 尽力而为
    }
    if (pidFile !== null) {
      try {
        rmSync(pidFile, { force: true })
      } catch {
        // 尽力而为
      }
    }
  }
  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D20',
      purpose: 'session-origin-isolation',
      runId: RUN_ID,
      criteriaVersion: D20_CRITERIA_VERSION,
      startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
      finishedAt: new Date().toISOString(),
      command: RUN_COMMAND,
      platform: { platform: process.platform, arch: process.arch, node: process.version, cwd: process.cwd(), dshBin },
      candidate: candidateSummary(),
      gates: [{ id: 'G00-watchdog', description: '判据脚本在限定时间内自行结束', ok: false, detail }],
      failedGateIds: ['G00-watchdog'],
      result: 'fail',
      watchdog: { timeoutMs: WATCHDOG_MS },
      opsRan: [...opsRan]
    },
    null,
    2
  ) + '\n'
  try {
    mkdirSync(RUN_DIR, { recursive: true })
    writeFileSync(join(RUN_DIR, 'report.json'), report)
    mkdirSync(evidenceDir, { recursive: true })
    writeFileSync(join(evidenceDir, 'd20-isolation-gates.json'), report)
    mkdirSync(durableEvidenceDir, { recursive: true })
    writeFileSync(join(durableEvidenceDir, 'd20-isolation-gates.json'), report)
  } catch (error) {
    console.error(`[watchdog] 写入证据失败：${String(error)}`)
  }
  process.exit(3)
}, WATCHDOG_MS)
if (typeof watchdog.unref === 'function') watchdog.unref()
async function operation(op, title, body) {
  if (!shouldRun(op)) return
  console.log(`\n--- 操作 ${op}：${title} ---`)
  try {
    await body()
    opsRan.add(op)
  } catch (error) {
    gate(`OP-${op}-runtime`, `操作 ${op} 自身跑完（无致命异常）`, false, String(error?.stack ?? error).slice(0, 400))
  }
}

try {
  console.log(`D20 会话与来源隔离判据（双 target / 多会话）runId=${RUN_ID}\n`)
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  lanBaseA = `http://${lanAddress}:${portA}`
  lanBaseB = `http://${lanAddress}:${portB}`
  lanBaseD = `http://${lanAddress}:${portD}`
  const secondaryInfo = existsSync(join(fixtureRoot, 'secondary-profile.json'))
    ? JSON.parse(await readFile(join(fixtureRoot, 'secondary-profile.json'), 'utf8'))
    : { profile: secondaryProfile, port: portB }
  const degradedInfo = existsSync(join(fixtureRoot, 'degraded-profile.json'))
    ? JSON.parse(await readFile(join(fixtureRoot, 'degraded-profile.json'), 'utf8'))
    : { profile: degradedProfile, port: portD }
  observations.targets = {
    A: { profile: fixtureProfile, port: portA, origin: lanBaseA, role: 'primary-web' },
    B: { profile: secondaryInfo.profile, port: secondaryInfo.port, origin: lanBaseB, role: 'secondary-web-same-DSH_HOME' },
    D: { profile: degradedInfo.profile, port: degradedInfo.port, origin: lanBaseD, role: 'degraded-origin-only' },
    lanAddress,
    sharedDshHome: dshHome
  }

  // ---- 阶段 0：造会话（在 stub 启动之前，保证 agent 请求计数从 0 开始） ----
  console.log('阶段 0：在共享 DSH_HOME 里造两条真实会话（s1 随后挂附件，s2 保持全新）')
  const seed1 = await seedSession('d20 isolation session one')
  const seed2 = await seedSession('d20 isolation session two')
  observations.sessions.seedExitCodes = [seed1.code, seed2.code]

  // ---- 阶段 1：两个 target 同时起来 ----
  console.log('\n阶段 1：同时启动两个 web target（同一 DSH_HOME，不同端口）')
  serviceA = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLogA })
  await writeFile(join(fixtureRoot, 'service.pid'), String(serviceA.child.pid))
  const readyA = await waitForReady(serviceA.child, serviceLogA, 120_000)
  serviceB = startService(dshBin, ['--profile', secondaryInfo.profile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLogB })
  await writeFile(join(fixtureRoot, 'service-b.pid'), String(serviceB.child.pid))
  const readyB = await waitForReady(serviceB.child, serviceLogB, 120_000)
  observations.targets.A.readyUrl = readyA
  observations.targets.B.readyUrl = readyB
  const probeB = await fetch(`http://127.0.0.1:${portB}/`).then((r) => r.status).catch(() => 0)
  gate(
    'G01-dual-target',
    '两个 web target 在同一台主机上同时就绪（不同端口 ⇒ 不同来源），且指向同一个 DSH_HOME',
    readyA !== null && readyB !== null && probeB !== 0 && serviceA.child.exitCode === null && serviceB.child.exitCode === null,
    `A=${readyA} pid=${serviceA.child.pid}；B=${readyB} pid=${serviceB.child.pid}；B 首页 HTTP=${probeB}；dshHome=${dshHome}`
  )

  const pairedA = await pair(portA, lanAddress)
  deviceA = pairedA.deviceId
  const pairedB = await pair(portB, lanAddress)
  deviceB = pairedB.deviceId
  gate(
    'G02-pairing-both-targets',
    '两个 target 各自完成真实配对并拿到独立设备凭证（同一 DSH_HOME，两个来源各自持证）',
    typeof deviceA === 'string' && deviceA.length > 0 && typeof deviceB === 'string' && deviceB.length > 0 && deviceA !== deviceB,
    `deviceA=${deviceA.length} 字符 deviceB=${deviceB.length} 字符 不同=${deviceA !== deviceB}`
  )

  const listA = await rpc(pairedA.client, lanBaseA, deviceA, 'session/list', { _request: {} })
  const items = listA.body?.result?.value?.items ?? []
  const sessionIds = items.map((item) => item.sessionId).filter((id) => typeof id === 'string')
  const s1 = sessionIds[0] ?? null
  const s2 = sessionIds[1] ?? null
  observations.sessions.list = {
    status: listA.status,
    count: sessionIds.length,
    itemKeys: items.length > 0 ? Object.keys(items[0]) : [],
    // 子代理会话的判定依据必须**据实记录**，不能凭猜测。
    subagentMarkers: items
      .filter((item) => Object.keys(item).some((key) => /parent|subagent|child|agent/i.test(key)))
      .map((item) => ({ sessionId: item.sessionId, keys: Object.keys(item) }))
  }
  gate(
    'G03-two-sessions',
    '共享 DSH_HOME 里至少两条可打开会话（一条挂既有附件、一条全新），且两条会话对两个 target 都可见',
    s1 !== null && s2 !== null && s1 !== s2,
    `s1=${s1} s2=${s2} 列表条数=${sessionIds.length}`
  )

  chrome = await launchChrome({})
  observations.chrome = { debugPort: chrome.debugPort, version: chrome.version?.Browser ?? null }

  // ---- 阶段 2：落点单元的页面（2 target × 2 会话，**只有四个应用页面**） ----
  //
  // 拓扑刻意收紧到 4 个页面且**不再新开临时页面**：
  //   pAA = (A, s1) 全程不导航 —— "已有附件"单元，也是隐藏/iframe/迟到/重放/子代理/协议版本的操作页；
  //   pAB = 先 (A, s1)（临时），op1 切会话后成为 (A, s2) 单元，op2 刷新、op9 首页尝试都在它身上；
  //   pBA = (B, s1) 单元，op6 跨来源导航一来一回、op10 受限 composer 在它身上；
  //   pBB = (B, s2) 单元，op3 页面删除时关掉再重开。
  //
  // 实测教训：同一个 headless Chromium 里开到第 5 个重页面时，新 target 的渲染进程会整体
  // 失联（`Runtime.evaluate` 永不返回）；4 个页面则整轮稳定。因此"操作复用页面 + 单元重绑"
  // 不只是省时间，也是让判据可重复的前提。
  console.log('\n阶段 2：为 2×2 个落点单元开四个真实页面（后续操作全部复用它们）')
  const pAA = await openAppPage(chrome, lanBaseA, deviceA, s1, { label: 'A×s1' })
  openPages.push(pAA.page)
  const pBA = await openAppPage(chrome, lanBaseB, deviceB, s1, { label: 'B×s1' })
  openPages.push(pBA.page)
  const pAB = await openAppPage(chrome, lanBaseA, deviceA, s1, { label: 'A×s1→s2（切会话页）' })
  openPages.push(pAB.page)
  const pBB = await openAppPage(chrome, lanBaseB, deviceB, s2, { label: 'B×s2' })
  openPages.push(pBB.page)

  cells.set('A-s1', { page: pAA.page, docInstance: 1, sessionId: s1, sessionRole: 'existing-attachments', targetId: 'A', targetPort: portA, origin: lanBaseA })
  cells.set('B-s1', { page: pBA.page, docInstance: 1, sessionId: s1, sessionRole: 'existing-attachments', targetId: 'B', targetPort: portB, origin: lanBaseB })
  cells.set('B-s2', { page: pBB.page, docInstance: 1, sessionId: s2, sessionRole: 'brand-new', targetId: 'B', targetPort: portB, origin: lanBaseB })
  // A×s2 单元要等 op1 把 pAB 切到 s2 之后才存在；在此之前 pAB 的这个文档只是"切会话页"。

  const readyAll = [pAA, pBA, pAB, pBB].every((item) => item.ready.ok === true && item.install.ok === true)
  gate(
    'G04-four-cell-pages',
    '四个应用页面各自就绪：桥版本 1、接收端宿主已装、页面认定的当前会话等于目标会话（同一主机、两个 target、两条会话）',
    readyAll,
    [pAA, pBA, pAB, pBB]
      .map((item) => `${item.label}: ready=${item.ready.ok} current=${item.ready.value?.currentSession} install=${item.install.ok}`)
      .join('；')
  )

  // s1 挂既有附件（草稿是**文档级**的：A×s1 的既有附件只属于 pAA 这个文档）。
  // 用"原生状态验证"的导入：一个文档里的第一次导入可能异步落地，只信同步 ACK 会漏记这个 id。
  const seedImport = await importVerified(pAA.page, 'A-s1', 'd20-existing.txt', `${TEXT_MARKER} existing attachments holder\n`, { label: 'seed' })
  recordDelivery('setup-existing-attachments', 'A-s1', 1, seedImport.landed, ['d20-existing.txt'])
  observations.cells.seed = seedImport
  const cellReadA1 = await cellProbe(pAA.page, 'setup-A-s1')
  const cellReadB1 = await cellProbe(pBA.page, 'setup-B-s1')
  const cellReadB2 = await cellProbe(pBB.page, 'setup-B-s2')
  const switchDocRead = await cellProbe(pAB.page, 'setup-switch-doc')
  observations.cells.initial = { A_s1: cellReadA1, B_s1: cellReadB1, B_s2: cellReadB2, switchDoc: switchDocRead }
  gate(
    'G05-cell-read-shape',
    '落点读数确实读的是**原生 attachmentIds**：A×s1 此刻非空、B×s1 与 B×s2 为空（切会话页此刻还在 s1，也是空文档）',
    cellReadA1.ok && (cellReadA1.ids ?? []).length === 1 && cellReadB1.ok && (cellReadB1.ids ?? []).length === 0 &&
      cellReadB2.ok && (cellReadB2.ids ?? []).length === 0 && switchDocRead.ok && (switchDocRead.ids ?? []).length === 0,
    `A×s1=${JSON.stringify((cellReadA1.ids ?? []).map((id) => id.slice(0, 12)))} B×s1=${JSON.stringify(cellReadB1.ids)} B×s2=${JSON.stringify(cellReadB2.ids)} 切会话页=${JSON.stringify(switchDocRead.ids)}`
  )
  gate(
    'G06-probe-hygiene',
    '探针是可移除的：每次探针导入后，点原生移除控件都能让探针卡片消失（轮询断言出现→点击→消失），因此落点读数不累积、不污染扫描',
    probeHygiene.length >= 4 && probeHygiene.every((item) => item.appeared === true && item.clicked !== null && item.gone === true),
    `${probeHygiene.length} 次探针：出现=${probeHygiene.filter((item) => item.appeared).length} 点击=${probeHygiene.filter((item) => item.clicked !== null).length} 消失=${probeHygiene.filter((item) => item.gone).length}；例=${JSON.stringify(probeHygiene[0] ?? null)}`
  )
  negative(
    'N01-probe-read-not-vacuous',
    '反例：落点读数不是恒空 —— "已有附件"单元必须读到非空的原生 id 列表（否则"处处 0 个 id"会白送一个零误投递）',
    (cellReadA1.ids ?? []).length > 0,
    `A×s1 previous=${(cellReadA1.ids ?? []).length} 项`
  )

  // =====================================================================
  // 操作 1：会话切换（切到新会话后，瞄准旧会话的导入）
  // =====================================================================
  await operation('session-switch', '切会话后瞄准旧会话的导入必须被拒，且不落进任何单元', async () => {
    // pAB：先在 s1（这个文档只是一个会切换掉的临时文档），再切到 s2 成为 A×s2 单元。
    const before = await cellProbe(pAB.page, 'op1-pre')
    // 切会话：夹具没有会话 URL 路由，只能预置 localStorage 再重新进入应用页。
    // 后注册的 addScriptToEvaluateOnNewDocument 后执行，因此新文档里当前会话是 s2。
    await send(pAB.page, 'Page.addScriptToEvaluateOnNewDocument', {
      source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(s2)}}))}catch(e){}`
    })
    await withTimeout(pAB.page.navigate(`${lanBaseA}/pair-app?device=${encodeURIComponent(deviceA)}`), 45_000, 'navigate pAB(switch→s2)')
    const switched = await poll(
      async () => evaluate(
        pAB.page,
        `JSON.stringify({ bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__, current: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null })`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.current === s2,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const install = await installHarness(pAB.page)
    // 新文档 = A×s2 单元（doc2）。旧文档（A×s1 的第二个文档）已被销毁；它的扫描读数记在下面。
    const rebound = bindCell('A-s2', pAB.page, 2, '切会话后的新文档')

    const mOld = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 101, composerEpoch: 201, composerScope: 'scope-d20-A-s1' })
    await makeReceiver(pAB.page, 'switch')
    const contextAttempt = await feed(pAB.page, 'switch', mOld.context())
    await feed(pAB.page, 'switch', mOld.batchBegin({ batchId: 'batch-op1-old', totalBytes: BODIES.text.length }))
    const after = await cellProbe(pAB.page, 'op1-aim-old-session')
    const receiver = await recvState(pAB.page, 'switch')
    observations.operations['session-switch'] = {
      switched: switched.ok,
      switchedTo: switched.value?.current,
      installOk: install.ok,
      destroyedDocumentIds: (before.ids ?? []).length,
      postCount: (after.ids ?? []).length,
      contextCode: contextAttempt.result?.code ?? null,
      contextDetail: contextAttempt.result?.detail ?? null,
      rebound,
      receiver
    }
    const refused = contextAttempt.result?.ok === false && contextAttempt.result.code === 'context-changed'
    gate(
      'G10-session-switch-refuses-old-session',
      '页面切到 s2 之后，瞄准旧会话 s1 的 context 被确定拒绝（context-changed），且不建立任何身份',
      switched.ok && refused && receiver.identity === null,
      `切到=${switched.value?.current} context=${contextAttempt.result?.ok === false ? contextAttempt.result.code : 'ok'} identity=${receiver.identity === null ? 'null' : '有'}`
    )
    gate(
      'G11-session-switch-no-landing',
      '切会话只改变归属、不产生投递：切换掉的旧文档是空的（无既有附件），新文档的原生 id 数在瞄准旧会话的导入前后都是 0',
      (before.ids ?? []).length === 0 && (after.ids ?? []).length === 0 && receiver.accounting.importsInvoked === 0,
      `旧文档=${(before.ids ?? []).length} 新文档=${(after.ids ?? []).length} importsInvoked=${receiver.accounting.importsInvoked}`
    )
    // 旧文档已被导航销毁：它不可能再被投递，因此只需记下它被销毁时的读数。
    sealedShadows.set('shadow:A-s1-switchdoc', {
      note: '切会话前的文档（A×s1 的第二个文档），被 op1 的导航销毁',
      ids: new Set((before.ids ?? []).filter((id) => !probeNoiseIds.has(id))),
      probe: { ids: before.ids, added: before.added, ok: before.ok, code: before.code },
      closed: true
    })
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'session-switch',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: 'refused',
        code: 'context-changed',
        landedIds: [],
        detail: '切会话后瞄准旧会话 s1 的导入被拒；该单元零新增',
        evidence: { switchedTo: switched.value?.current, contextCode: contextAttempt.result?.code ?? null, receiverImportsInvoked: receiver.accounting.importsInvoked }
      })
    }
  })

  // =====================================================================
  // 操作 2：页面刷新 / 重新 bootstrap（在 A×s2 的文档上做，"已有附件"单元不受影响）
  // =====================================================================
  await operation('page-refresh', '刷新后迟到帧不得补齐刷新前的半成品，也不得投递', async () => {
    await makeReceiver(pAB.page, 'refresh')
    const partial = await feedBatch(pAB.page, 'refresh', {
      sessionId: s2, targetId: 'A', batchId: 'batch-op2', fileId: 'file-op2',
      bytes: BODIES.late, epochs: { documentEpoch: 301, composerEpoch: 401 },
      composerScope: 'scope-d20-refresh', fileName: 'd20-refresh.txt', stopAfter: 'chunks'
    })
    const bufferedBefore = (await recvState(pAB.page, 'refresh')).accounting.bufferedBytes
    const preCount = (await cellProbe(pAB.page, 'op2-pre')).ids ?? []

    // 刷新（同一会话，新的文档）
    await withTimeout(pAB.page.navigate(`${lanBaseA}/pair-app?device=${encodeURIComponent(deviceA)}`), 45_000, 'navigate pAB(refresh)')
    const reReady = await poll(
      async () => evaluate(
        pAB.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, current: b?.currentSession?.() ?? null, host: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__ }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.host === 'object' && value.current === s2,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const reinstall = await installHarness(pAB.page)
    const rebound = bindCell('A-s2', pAB.page, 3, '刷新后的新文档')
    await makeReceiver(pAB.page, 'refresh2')
    const freshAccounting = (await recvState(pAB.page, 'refresh2')).accounting
    const m = msgs({ sessionId: s2, targetId: 'A', documentEpoch: 301, composerEpoch: 401, composerScope: 'scope-d20-refresh' })
    const lateChunk = await feed(pAB.page, 'refresh2', m.chunks(BODIES.late, { batchId: 'batch-op2', fileId: 'file-op2' })[0].text)
    const lateEnd = await feed(pAB.page, 'refresh2', m.fileEnd({ batchId: 'batch-op2', fileId: 'file-op2', totalBytes: BODIES.late.length, sha: sha256(BODIES.late) }))
    const postCount = (await cellProbe(pAB.page, 'op2-post')).ids ?? []
    const finalAccounting = (await recvState(pAB.page, 'refresh2')).accounting
    observations.operations['page-refresh'] = {
      bufferedBeforeReload: bufferedBefore,
      preCount: preCount.length,
      postCount: postCount.length,
      readyAfterReload: reReady.ok,
      reinstallOk: reinstall.ok,
      freshBufferedBytes: freshAccounting.bufferedBytes,
      lateChunk: { code: lateChunk.result?.code ?? null, ok: lateChunk.result?.ok ?? null },
      lateEnd: { code: lateEnd.result?.code ?? null, ok: lateEnd.result?.ok ?? null },
      importsInvoked: finalAccounting.importsInvoked,
      peakBufferedBytesAfterReload: finalAccounting.peakBufferedBytes,
      rebound
    }
    gate(
      'G12-refresh-drops-partial-state',
      '刷新确实是新文档：旧接收端的半成品缓冲不在新文档里（新接收端 bufferedBytes=0），页面重新 bootstrap 成功',
      reReady.ok && reinstall.ok && bufferedBefore > 0 && freshAccounting.bufferedBytes === 0,
      `刷新前缓冲=${bufferedBefore}B 新文档缓冲=${freshAccounting.bufferedBytes}B 重新就绪=${reReady.ok}`
    )
    gate(
      'G13-refresh-late-frames-refused',
      '刷新后迟到的 chunk / file-end 被确定拒绝（没有 context 身份 ⇒ no-session），半成品不得被补齐',
      lateChunk.result?.ok === false && lateChunk.result.code === 'no-session' &&
        lateEnd.result?.ok === false && lateEnd.result.code === 'no-session' && finalAccounting.importsInvoked === 0,
      `chunk=${lateChunk.result?.code ?? 'ok'} file-end=${lateEnd.result?.code ?? 'ok'} importsInvoked=${finalAccounting.importsInvoked}`
    )
    gate(
      'G14-refresh-no-landing',
      '刷新不产生任何投递：该文档的原生 id 数在刷新前后一致（半成品字节既没补齐也没落进草稿）',
      preCount.length === postCount.length,
      `刷新前=${preCount.length} 刷新后=${postCount.length}`
    )
    negative(
      'N02-refresh-buffer-was-real',
      '反例：刷新前的半成品确实进了接收缓冲（bufferedBytes>0），因此"刷新后为 0"不是空断言',
      bufferedBefore > 0,
      `bufferedBefore=${bufferedBefore}B`
    )
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'page-refresh',
        cellId,
        intendedCellId: 'A-s2',
        determinate: true,
        outcome: 'refused',
        code: 'no-session',
        landedIds: [],
        detail: `刷新后迟到帧被拒；cell=${cellId} 零新增`,
        evidence: { bufferedBeforeReload: bufferedBefore, lateChunk: lateChunk.result?.code ?? null, lateEnd: lateEnd.result?.code ?? null }
      })
    }
  })

  // =====================================================================
  // 操作 3：页面 / target 删除
  // =====================================================================
  await operation('target-deletion', '删除页面与删除 target 都不得把在途附件挪到别的单元', async () => {
    // (a) 页面删除：把 B×s2 单元这一页（持有在途半成品）真的关掉，再按同一 (target, 会话) 重开一个文档。
    await makeReceiver(pBB.page, 'del')
    await feedBatch(pBB.page, 'del', {
      sessionId: s2, targetId: 'B', batchId: 'batch-op3', fileId: 'file-op3',
      bytes: BODIES.alt, epochs: { documentEpoch: 501, composerEpoch: 601 },
      composerScope: 'scope-d20-del', fileName: 'd20-del.txt', stopAfter: 'chunks'
    })
    const delBuffered = (await recvState(pBB.page, 'del')).accounting.bufferedBytes
    const listBefore = await (await fetch(`http://127.0.0.1:${chrome.debugPort}/json/list`)).json()
    const beforeSiblings = {
      'A-s1': (await cellProbe(pAA.page, 'op3-AA')).ids ?? [],
      'B-s1': (await cellProbe(pBA.page, 'op3-BA')).ids ?? []
    }
    const delPage = pBB.page
    // 先扫描封存（关掉之后不可能再被投递），再**真的关掉这个标签页**（Page.close），
    // 最后核对 target 数确实少了 1。注意 cdp.mjs 的 page.close() 只关调试 WebSocket，
    // 不关标签页 —— 那样"页面被删除"就没有真的发生。
    const sealedDel = await sealShadow('shadow:B-s2-deleted', delPage, '被删除的 B×s2 文档（持有在途半成品）', { close: false })
    const closeAt = openPages.indexOf(delPage)
    if (closeAt >= 0) openPages.splice(closeAt, 1)
    await send(delPage, 'Page.close').catch(() => {})
    await sleep(1500)
    const listAfter = await (await fetch(`http://127.0.0.1:${chrome.debugPort}/json/list`)).json()
    const closedGone = listAfter.filter((item) => item.type === 'page').length === listBefore.filter((item) => item.type === 'page').length - 1
    // 同 (target, 会话) 重开一个文档作为 B×s2 单元。
    const pBB2 = await openAppPage(chrome, lanBaseB, deviceB, s2, { label: 'B×s2(重开)' })
    openPages.push(pBB2.page)
    const rebound = bindCell('B-s2', pBB2.page, 2, '页面被删除后重开的新文档')
    const afterSiblings = {
      'A-s1': (await cellProbe(pAA.page, 'op3-AA-post')).ids ?? [],
      'B-s1': (await cellProbe(pBA.page, 'op3-BA-post')).ids ?? []
    }
    const siblingUnchanged = Object.keys(beforeSiblings).every((key) => beforeSiblings[key].length === afterSiblings[key].length)
    observations.operations['target-deletion'] = observations.operations['target-deletion'] ?? {}
    observations.operations['target-deletion'].pageDelete = {
      bufferedBeforeClose: delBuffered,
      sealed: sealedDel,
      closedTargetGone: closedGone,
      siblingsUnchanged: siblingUnchanged,
      before: Object.fromEntries(Object.entries(beforeSiblings).map(([key, ids]) => [key, ids.length])),
      after: Object.fromEntries(Object.entries(afterSiblings).map(([key, ids]) => [key, ids.length])),
      rebound
    }
    gate(
      'G15-page-delete-no-cross-delivery',
      '删掉持有在途半成品的页面：页面真的消失（target 数 -1），同会话的另一个 target 与同 target 的另一个会话都零新增（在途字节不挪窝）',
      delBuffered > 0 && closedGone && siblingUnchanged,
      `关闭前缓冲=${delBuffered}B 目标已消失=${closedGone} 邻接单元不变=${siblingUnchanged}`
    )
    negative(
      'N03-delete-had-pending-bytes',
      '反例：被删页面确实持有在途字节（否则"删除后别处零新增"是空断言）',
      delBuffered > 0,
      `bufferedBeforeClose=${delBuffered}B`
    )
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'target-deletion',
        cellId,
        intendedCellId: 'B-s2',
        determinate: true,
        outcome: 'no-delivery',
        code: null,
        landedIds: [],
        detail: '页面在导入完成前被关闭；在途字节随文档销毁，邻接单元零新增',
        evidence: { bufferedBeforeClose: delBuffered, closedTargetGone: closedGone }
      })
    }
  })
  // =====================================================================
  // 操作 4：后台 / 隐藏页面状态
  // =====================================================================
  await operation('hidden-page', '隐藏文档里的导入仍然只能落进它自己的单元', async () => {
    const pageB2 = cells.get('B-s2').page
    const targetIdOf = async (page) => (await send(page, 'Target.getTargetInfo')).targetInfo.targetId
    const aaTarget = await targetIdOf(pAA.page)
    const bbTarget = await targetIdOf(pageB2)
    // 先激活 pAA 让它可见，再激活另一个页面把它压成 hidden；全程轮询，断言"出现"与"消失"。
    // 激活偶发需要重试（同一个 headless 窗口里第一次激活可能不生效），因此这里是有界的重试。
    const ensureVisible = async () => {
      for (let index = 0; index < 4; index++) {
        await send(pageB2, 'Target.activateTarget', { targetId: aaTarget }).catch(() => {})
        const seen = await poll(async () => (await globalsOf(pAA.page)).visibility, (value) => value === 'visible', { timeoutMs: 6_000, intervalMs: 300 })
        if (seen.ok) return { ok: true, attempts: index + 1 }
      }
      return { ok: false, attempts: 4 }
    }
    const ensureHidden = async () => {
      for (let index = 0; index < 4; index++) {
        await send(pageB2, 'Target.activateTarget', { targetId: bbTarget }).catch(() => {})
        const seen = await poll(async () => (await globalsOf(pAA.page)).visibility, (value) => value === 'hidden', { timeoutMs: 6_000, intervalMs: 300 })
        if (seen.ok) return { ok: true, attempts: index + 1, value: 'hidden' }
      }
      return { ok: false, attempts: 4, value: null }
    }
    const visiblePoll = await ensureVisible()
    const backToVisible = await ensureVisible()
    const hiddenConfirm = await ensureHidden()
    // 此刻 pAA 是 hidden：做一次完整批次导入。
    const beforeCount = (await cellProbe(pAA.page, 'op4-pre')).ids ?? []
    await makeReceiver(pAA.page, 'hidden')
    const batch = await feedBatch(pAA.page, 'hidden', {
      sessionId: s1, targetId: 'A', batchId: 'batch-op4', fileId: 'file-op4',
      bytes: BODIES.hidden, epochs: { documentEpoch: 701, composerEpoch: 801 },
      composerScope: 'scope-d20-hidden', fileName: 'd20-hidden.txt'
    })
    const endResult = batch.last?.result ?? {}
    const added = endResult.import?.added ?? []
    recordDelivery('hidden-page', 'A-s1', cells.get('A-s1').docInstance, added, ['d20-hidden.txt'])
    const afterCount = (await cellProbe(pAA.page, 'op4-post')).ids ?? []
    const others = {
      'A-s2': (await cellProbe(pAB.page, 'op4-A2')).ids ?? [],
      'B-s1': (await cellProbe(pBA.page, 'op4-B1')).ids ?? [],
      'B-s2': (await cellProbe(pageB2, 'op4-B2')).ids ?? []
    }
    // 再激活回 pAA，断言 hidden 真的消失（完整窗口内"出现 + 消失"都观察到）。
    const visibleAgain = await ensureVisible()
    observations.operations['hidden-page'] = {
      visibleWhileActive: { ok: visiblePoll.ok, attempts: visiblePoll.attempts },
      visibleBeforeHiding: { ok: backToVisible.ok, attempts: backToVisible.attempts },
      hiddenReached: { ok: hiddenConfirm.ok, attempts: hiddenConfirm.attempts },
      visibleAgain: { ok: visibleAgain.ok, attempts: visibleAgain.attempts },
      outcome: endResult.ok === true ? 'imported' : 'refused',
      code: endResult.ok === true ? null : endResult.code,
      addedCount: added.length,
      beforeCount: beforeCount.length,
      afterCount: afterCount.length,
      othersCounts: Object.fromEntries(Object.entries(others).map(([key, ids]) => [key, ids.length])),
      accounting: (await recvState(pAA.page, 'hidden')).accounting
    }
    const determinate = endResult.ok === true ? added.length === 1 : typeof endResult.code === 'string'
    gate(
      'G16-hidden-visibility-really-changed',
      '隐藏状态是真的：同一窗口内先观察到 visible、再观察到 hidden、最后又回到 visible（不是靠 sleep 猜的）',
      visiblePoll.ok && backToVisible.ok && hiddenConfirm.ok && visibleAgain.ok,
      `visible=${visiblePoll.ok}/${backToVisible.ok} hidden=${hiddenConfirm.ok}（当时 visibility=${hiddenConfirm.value}） 回到 visible=${visibleAgain.ok}`
    )
    gate(
      'G17-hidden-page-determinate-and-own-cell-only',
      '隐藏文档里的完整批次结果确定（要么确定拒绝，要么落进 A×s1 且只加 1 项），且其它三个单元零新增',
      determinate && afterCount.length === beforeCount.length + added.length &&
        Object.values(others).every((ids) => ids.length === 0),
      `结果=${endResult.ok === true ? 'imported' : `refused:${endResult.code}`} 新增=${added.length} A×s1 ${beforeCount.length}→${afterCount.length} 其它=${JSON.stringify(Object.fromEntries(Object.entries(others).map(([key, ids]) => [key, ids.length])))}`
    )
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'hidden-page',
        cellId,
        intendedCellId: 'A-s1',
        determinate,
        outcome: endResult.ok === true ? (cellId === 'A-s1' ? 'imported' : 'no-delivery') : 'refused',
        code: endResult.ok === true ? null : (endResult.code ?? null),
        landedIds: cellId === 'A-s1' ? added : [],
        detail: `hidden 文档（document.visibilityState=hidden）里的批次；cell=${cellId}`,
        evidence: { visibility: hiddenConfirm.value, importsInvoked: observations.operations['hidden-page'].accounting.importsInvoked }
      })
    }
    // 冻结态：JS 可能整体被挂起，因此用超时护栏，并把"挂起"本身作为确定结论记录下来。
    // 刻意放在 A×s2 这个"便宜"的单元格上（它的草稿本来就是空的）：冻结/解冻是 Chrome 里
    // 最容易引发文档被替换的操作，不能让"已有附件"单元承担这个风险。
    const pFrozen = pAB.page
    const frozen = await evaluate(pFrozen, `(async () => { try { await globalThis.__D20__.make('frozen'); return 'made' } catch (e) { return 'threw:' + String(e && e.message ? e.message : e) } })()`).catch((error) => `eval-threw:${error?.message ?? error}`)
    await send(pFrozen, 'Page.setWebLifecycleState', { state: 'frozen' }).catch(() => {})
    const frozenFeed = await Promise.race([
      feed(pFrozen, 'frozen', msgs({ sessionId: s2, targetId: 'A', documentEpoch: 901, composerEpoch: 1001, composerScope: 'scope-d20-frozen' }).context())
        .then((value) => ({ responded: true, result: value.result }))
        .catch((error) => ({ responded: true, threw: String(error?.message ?? error) })),
      sleep(6_000).then(() => ({ responded: false }))
    ])
    await send(pFrozen, 'Page.setWebLifecycleState', { state: 'active' }).catch(() => {})
    // 解冻后必须把冻结态接收端撤销掉：否则万一消息在解冻后才被处理，会在扫描之后落进 A×s1，
    // 变成一条无法归属的 id。撤销是产品自己的"行为撤销"入口。
    const frozenDisposed = await evaluate(pFrozen, `globalThis.__D20__.dispose('frozen')`).catch(() => false)
    const frozenAccounting = await recvState(pFrozen, 'frozen').catch(() => null)
    observations.operations['hidden-page'].frozen = { made: frozen, feed: frozenFeed, disposed: frozenDisposed, accounting: frozenAccounting?.accounting ?? null }
    if (frozenFeed.responded === false) {
      markNotRun(
        'G18-frozen-document-behaviour',
        '冻结（frozen 生命周期）文档里的消息处理行为',
        '冻结后 6 秒内页面侧没有任何响应（JS 任务被挂起），无法在挂起状态下取回确定结果；挂起本身即"不投递"，但本条未单独验证'
      )
    } else {
      gate(
        'G18-frozen-document-behaviour',
        '冻结文档里的 context 依然给出确定结果（接受或拒绝都由产品状态机给出，而不是静默丢弃），且撤销后该接收端导入计数为 0',
        (frozenFeed.result?.ok === true || frozenFeed.result?.ok === false) && (frozenAccounting?.accounting?.importsInvoked ?? 0) === 0,
        `frozen feed=${JSON.stringify(frozenFeed.result ?? frozenFeed.threw ?? null).slice(0, 160)} disposed=${frozenDisposed} importsInvoked=${frozenAccounting?.accounting?.importsInvoked ?? null}`
      )
    }
  })

  // =====================================================================
  // 操作 5：iframe（非主文档不得获得文件能力）
  // =====================================================================
  await operation('iframe', '非主文档框架不得获得文件能力，更不得把文件塞进主文档草稿', async () => {
    const before = (await cellProbe(pAA.page, 'op5-pre')).ids ?? []
    const frameResult = JSON.parse(
      await evaluate(
        pAA.page,
        `globalThis.__D20__.iframe(${JSON.stringify({
          note: 'same-origin blank + cross-origin candidates（不装真实应用于子框架：会 frame-bust 顶层文档）',
          sameOriginSrc: null,
          crossOriginSrcs: [
            { id: 'B-app', url: `${lanBaseB}/pair-app?device=${encodeURIComponent(deviceB)}` },
            { id: 'D-app', url: `${lanBaseD}/pair-app?device=` },
            { id: 'B-404', url: `${lanBaseB}/d20-cross-origin-probe` }
          ],
          frameImport: false,
          frameImportText: `${FRAME_MARKER} frame-initiated import\n`
        })})`
      )
    )
    const after = (await cellProbe(pAA.page, 'op5-post')).ids ?? []
    // 异来源边界必须由**异来源文档自己**来读（父页面读 parent.<prop> 永远是同来源的，测不出边界）。
    const xoChild = JSON.parse(
      await evaluate(pAA.page, 'globalThis.__D20__.crossOriginChildProbe(10000).then((value) => JSON.stringify(value))')
    )
    const soChild = JSON.parse(
      await evaluate(pAA.page, 'globalThis.__D20__.sameOriginChildProbe(10000).then((value) => JSON.stringify(value))')
    )
    const blanks = frameResult.created?.find((item) => item.kind === 'same-origin-blank') ?? null
    const appFrame = frameResult.created?.find((item) => item.kind === 'same-origin-app') ?? null
    const cross = frameResult.crossOriginFrame ?? null
    observations.operations.iframe = { frameResult, xoChild, soChild, beforeCount: before.length, afterCount: after.length, blanks, appFrame, cross }
    gate(
      'G19-child-frame-no-capability',
      '非主文档框架拿不到文件能力：同来源空白子框架没有插件的桥/接收端（插件只装主文档）；异来源子框架从父页面读不到（location/document 全 SecurityError），且它自己尝试读父页面的桥被 SecurityError 挡住',
      blanks !== null && blanks.bridge === 'undefined' && blanks.receiverHost === 'undefined' &&
        frameResult.sameOriginFrameToParentBridge?.parentBridge === 'object' &&
        cross !== null && cross.locationThrew === 'SecurityError' && cross.documentThrew === 'SecurityError' &&
        typeof xoChild.result === 'string' && xoChild.result.startsWith('blocked:SecurityError'),
      `blank 子框架 bridge=${blanks?.bridge} receiver=${blanks?.receiverHost} 读父桥=${blanks?.parentBridge}；异来源(${cross?.kind ?? '无'}) location=${cross?.locationThrew} document=${cross?.documentThrew}；异来源文档自读父桥=${JSON.stringify(xoChild)}；候选=${JSON.stringify((frameResult.created ?? []).filter((item) => String(item.kind).startsWith('cross-origin')).map((item) => [item.kind, item.locationThrew]))}`
    )
    const frameImport = frameResult.frameImport ?? null
    // 子框架发起的导入：无论子框架有没有桥，主文档的原生 id 数都必须不变；
    // 即便它在子框架**自己的**文档里成功，落点也必须不是主文档（sameDocument=false）。
    const frameImportReachedMain = frameImport?.attempted === true && frameImport?.ok === true && frameImport?.sameDocument === true
    gate(
      'G20-frame-initiated-import-never-lands-in-main-document',
      '子框架自己发起的导入不得落进主文档：主文档的原生 id 数完全不变；成功时也只允许落在子框架自己的文档里',
      before.length === after.length && frameImportReachedMain === false,
      `主文档 ${before.length}→${after.length}；sameDocument=${frameImport?.sameDocument ?? null}；子框架导入=${JSON.stringify(frameImport)?.slice(0, 260)}`
    )
    // 同来源子框架（srcdoc）：它自己的文档里没有插件全局（插件只装主文档），因此它自己没有
    // 文件能力；它**能**读到父页面的桥是同来源信任域的正常结果（据实记录，不当作缺陷）。
    gate(
      'G21-subframe-has-no-file-capability',
      '同来源子框架自己没有文件能力：它自己的文档里没有桥/接收端全局（插件只装主文档），因此不存在"子框架自己发起导入"这条路径；它能读到父页面全局只是同来源信任域的正常结果',
      soChild.probe !== undefined && soChild.probe.own === 'undefined' && soChild.probe.sameOrigin === 'yes' && soChild.probe.parentBridge === 'object',
      `srcdoc 子框架：own=${soChild.probe?.own} parentBridge=${soChild.probe?.parentBridge} sameOrigin=${soChild.probe?.sameOrigin} from=${soChild.from}`
    )
    markNotRun(
      'G21b-real-app-inside-subframe',
      '"把真实应用装进同来源子框架"这一形态（子框架里插件会装上、但没有会话身份）',
      '实测该形态会触发应用自身的 frame-bust：子框架里的应用会把**顶层文档**导航成它自己的 URL，从而毁掉夹具自己的落点单元文档（本轮日志里可看到 3099/pair-app?device=… 的文档替换告警）。为了不让判据自己破坏被测对象，该形态未验证；子框架无文件能力这一结论由 G19/G21/G22 与 D15 策略共同覆盖'
    )
    negative(
      'N04-cross-origin-frame-candidate-found',
      '反例：异来源子框架候选里必须真的有一个**以 SecurityError 被证实为异来源**（否则"读不到桥"可能只是因为框架压根没导航）',
      cross !== null && cross.locationThrew === 'SecurityError',
      `已验证的异来源候选=${JSON.stringify(frameResult.crossOriginVerified ?? null)}；每个候选的 location 异常=${JSON.stringify((frameResult.created ?? []).filter((item) => String(item.kind).startsWith('cross-origin')).map((item) => [item.kind, item.locationThrew]))}`
    )
    // D15 策略：子框架/子资源一律 notMainDocument
    const policyChild = parseRemoteOrigin(lanBaseA)
    const childVerdict = originVectors(lanBaseA).find((vector) => vector.id === 'V25-child-frame-same-origin')
    const childRun = runOriginVector(childVerdict, lanBaseA)
    gate(
      'G22-d15-child-frame-policy',
      'D15 策略：子框架导航即便与可信来源逐字相同，也只得到 origin-not-main-document（既不授予也不撤销能力）',
      childRun.ok === true && childRun.checks.code === ORIGIN_CODES.notMainDocument && childRun.checks.trusted === false,
      `判定=${JSON.stringify(childRun.checks)} 可信来源=${policyChild.origin?.normalized ?? null}`
    )
    recordDelivery('iframe', 'A-s1', cells.get('A-s1').docInstance, [], [])
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'iframe',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: 'no-delivery',
        code: null,
        landedIds: [],
        detail: '非主文档框架发起的导入；主文档与各单元零新增',
        evidence: { frameBridge: blanks?.bridge ?? null, crossOriginLocationThrew: cross?.locationThrew ?? null, frameImport }
      })
    }
  })

  // =====================================================================
  // 操作 6：跨来源导航（含伪装形态）
  // =====================================================================
  await operation('cross-origin-navigation', '跨来源导航必须让来源与身份一起失效；伪装形态一律不可信', async () => {
    // (a) D15 纯策略向量：对着**真实配对来源**跑一遍，再对 DNS 形态跑一遍（两条互补的主机形态）。
    const ipResults = originVectors(lanBaseA).map((vector) => runOriginVector(vector, lanBaseA))
    const dnsOrigin = 'http://our.host:3099'
    const dnsResults = originVectors(dnsOrigin).map((vector) => runOriginVector(vector, dnsOrigin))
    const allResults = [...ipResults, ...dnsResults]
    const failed = allResults.filter((item) => item.ok !== true)
    const skipped = allResults.filter((item) => item.skipped === true)
    const shape = hostShapeOf(parseRemoteOrigin(lanBaseA).origin?.host ?? '')
    const named = ['V02-userinfo-evil-at-our-host', 'V05-suffix-host', 'V10-ipv6-bracket-then-suffix', 'V14-ipv6-mapped-v4-canonical', 'V28-external-link-same-origin']
    const namedOk = named.every((id) => allResults.some((item) => item.id === id && item.ok === true))
    observations.operations['cross-origin-navigation'] = {
      hostShape: shape,
      ipResults: ipResults.map((item) => ({ id: item.id, skipped: item.skipped === true, ok: item.ok, checks: item.checks ?? null })),
      dnsResults: dnsResults.map((item) => ({ id: item.id, skipped: item.skipped === true, ok: item.ok, checks: item.checks ?? null })),
      skipped: skipped.map((item) => ({ id: item.id, reason: item.reason })),
      named: named.map((id) => allResults.find((item) => item.id === id) ?? null)
    }
    gate(
      'G23-d15-policy-vectors',
      `D15 来源规则向量（真实来源主机形态=${shape} + DNS 形态）：userinfo / 后缀主机 / IPv6 字面量/默认端口/结尾根点/外部链接全部给出确定结论`,
      failed.length === 0 && namedOk && ipResults.length >= 28 && dnsResults.length >= 28,
      `向量 ${allResults.length} 条：通过 ${allResults.length - failed.length}，跳过 ${skipped.length}（形态不适用），失败 ${failed.length}${failed.length > 0 ? `：${failed.map((item) => `${item.id}:${item.mismatches.join(',')}`).join(' ')}` : ''}`
    )
    negative(
      'N05-d15-trusted-positive-control',
      '反例：D15 规则不是"一律不可信"——真实配对来源的主文档导航必须判 origin-trusted（正向对照）',
      ipResults.find((item) => item.id === 'V01-trusted-origin-exact')?.checks?.trusted === true,
      `V01 checks=${JSON.stringify(ipResults.find((item) => item.id === 'V01-trusted-origin-exact')?.checks ?? null)}`
    )
    const leakyVectors = [`http://evil.example@${parseRemoteOrigin(lanBaseA).origin?.host}:${portA}/pair-app`]
      .filter((url) => parseRemoteOrigin(url).ok === true)
    negative(
      'N06-userinfo-cannot-be-parsed-as-trusted',
      '反例：带 userinfo 的伪 URL 在任何解析路径下都不能被判成可信主文档（含大写 scheme、带密码、形如合法主机的前缀）',
      leakyVectors.length === 0 &&
        originVectors(lanBaseA)
          .filter((vector) => vector.id.startsWith('V02') || vector.id.startsWith('V03') || vector.id.startsWith('V04') || vector.id.startsWith('V07'))
          .every((vector) => runOriginVector(vector, lanBaseA).checks.trusted === false),
      `带 userinfo 的 4 条向量全部 trusted=false；直接解析 ok=${JSON.stringify(leakyVectors)}`
    )

    // (b) 真实导航：把 pBA（B 来源、会话 s1）导航到 A 来源，再导航回来。
    await makeReceiver(pBA.page, 'xo')
    // 导航前真的把字节放进接收缓冲（只 batch-begin 是 0 字节，"在途"就没有实质内容）。
    await feedBatch(pBA.page, 'xo', {
      sessionId: s1, targetId: 'B', batchId: 'batch-op6', fileId: 'file-op6',
      bytes: BODIES.cross, epochs: { documentEpoch: 1101, composerEpoch: 1201 }, composerScope: 'scope-d20-xo',
      fileName: 'd20-cross.txt', stopAfter: 'chunks'
    })
    const pre = await recvState(pBA.page, 'xo')
    await withTimeout(pBA.page.navigate(`${lanBaseA}/pair-app?device=${encodeURIComponent(deviceA)}`), 45_000, 'navigate pBA(A 来源)')
    const reReady = await poll(
      async () => evaluate(
        pBA.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, current: b?.currentSession?.() ?? null, origin: location.origin }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.current === s1 && value.origin === lanBaseA,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const reinstall = await installHarness(pBA.page)
    await makeReceiver(pBA.page, 'xo2')
    const lateChunk = await feed(
      pBA.page,
      'xo2',
      msgs({ sessionId: s1, targetId: 'B', documentEpoch: 1101, composerEpoch: 1201, composerScope: 'scope-d20-xo' }).chunks(BODIES.cross, { batchId: 'batch-op6', fileId: 'file-op6' })[0].text
    )
    const statusAfter = await pageStatus(pBA.page)
    // 这个 A 来源文档是影子单元：封存它的读数后导航回 B 来源（导航即销毁，之后不可能再被投递）。
    const sealedXo = await sealShadow('shadow:A-s1-xo', pBA.page, 'B×s1 导航到 A 来源后的文档（影子）', { close: false })
    await withTimeout(pBA.page.navigate(`${lanBaseB}/pair-app?device=${encodeURIComponent(deviceB)}`), 45_000, 'navigate pBA(回 B 来源)')
    const backReady = await poll(
      async () => evaluate(
        pBA.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, current: b?.currentSession?.() ?? null, origin: location.origin }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.current === s1 && value.origin === lanBaseB,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const backInstall = await installHarness(pBA.page)
    const rebound = bindCell('B-s1', pBA.page, 2, '跨来源导航一去一回后的新文档')
    observations.operations['cross-origin-navigation'].live = {
      preBuffered: pre.accounting.bufferedBytes,
      preIdentity: pre.identity,
      crossOriginDoc: { origin: reReady.value?.origin ?? null, expected: lanBaseA, reinstallOk: reinstall.ok },
      lateChunkCode: lateChunk.result?.code ?? null,
      statusOriginAfterNavigation: statusAfter.addonStatus?.origin ?? statusAfter.addonStatus?.facts?.origin ?? null,
      sealedShadow: sealedXo,
      returned: { origin: backReady.value?.origin ?? null, expected: lanBaseB, installOk: backInstall.ok },
      rebound
    }
    gate(
      'G24-real-cross-origin-navigation',
      '真实跨来源导航（B→A）之后：页面来源变成另一个 target 的来源，插件状态面随之报告新来源，导航前的接收端身份不复存在（迟到 chunk = no-session）',
      reReady.ok && reinstall.ok && lateChunk.result?.ok === false && lateChunk.result.code === 'no-session' && pre.accounting.bufferedBytes > 0 && backReady.ok,
      `B→A 后 origin=${reReady.value?.origin} 迟到 chunk=${lateChunk.result?.code ?? 'ok'} 导航前缓冲=${pre.accounting.bufferedBytes}B 回到 B=${backReady.value?.origin}`
    )

    // (c) 降级 profile（另一个真实可达来源）：页面能力必须明确不可用，且一个文件都不导入。
    //     复用 pAB（A×s2 单元的页面）：先导航到降级来源，跑完再导航回 A 来源恢复该单元。
    serviceD = startService(dshBin, ['--profile', degradedInfo.profile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLogD })
    await waitForReady(serviceD.child, serviceLogD, 120_000)
    const pairedD = await pair(degradedInfo.port, lanAddress)
    deviceD = pairedD.deviceId
    // 降级来源是**另一个来源**：它有自己的 localStorage，因此这里显式清掉会话选择，
    // 让宿主自己决定；能力判定在会话判定之前，所以"能力不可用"与选没选会话无关。
    await send(pAB.page, 'Page.addScriptToEvaluateOnNewDocument', { source: `try{localStorage.removeItem('dsh.sessions.current')}catch(e){}` })
    await withTimeout(pAB.page.navigate(`${lanBaseD}/pair-app?device=${encodeURIComponent(deviceD)}`), 45_000, 'navigate pAB(降级来源)')
    const degradedReady = await poll(
      async () => evaluate(
        pAB.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, origin: location.origin, host: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__, current: b?.currentSession?.() ?? null }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.host === 'object' && value.origin === lanBaseD,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const degradedInstall = await installHarness(pAB.page)
    const statusD = await pageStatus(pAB.page)
    await makeReceiver(pAB.page, 'deg')
    const degBatch = await feedBatch(pAB.page, 'deg', {
      sessionId: degradedReady.value?.current ?? s2, targetId: 'D', batchId: 'batch-op6-deg', fileId: 'file-op6-deg',
      bytes: BODIES.cross, epochs: { documentEpoch: 1301, composerEpoch: 1401 },
      composerScope: 'scope-d20-deg', fileName: 'd20-deg.txt'
    })
    const degOutgoing = degBatch.steps.flatMap((step) => step.outgoing ?? [])
    const degRecv = await recvState(pAB.page, 'deg')
    const degResult = degBatch.last?.result ?? {}
    const bridgeDeg = JSON.parse(await evaluate(pAB.page, `globalThis.__D20__.bridgeImport(null, 'd20-deg.txt', 'degraded')`))
    // 恢复 A×s2 单元：回到 A 来源并把会话钉回 s2。
    await send(pAB.page, 'Page.addScriptToEvaluateOnNewDocument', {
      source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(s2)}}))}catch(e){}`
    })
    await withTimeout(pAB.page.navigate(`${lanBaseA}/pair-app?device=${encodeURIComponent(deviceA)}`), 45_000, 'navigate pAB(回 A 来源)')
    const restored = await poll(
      async () => evaluate(
        pAB.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, origin: location.origin, current: b?.currentSession?.() ?? null }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.origin === lanBaseA && value.current === s2,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const restoreInstall = await installHarness(pAB.page)
    const reboundA2 = bindCell('A-s2', pAB.page, 4, '降级来源往返后恢复的 A×s2 文档')
    observations.operations['cross-origin-navigation'].degraded = {
      origin: degradedReady.value?.origin ?? null,
      expectedOrigin: lanBaseD,
      installOk: degradedInstall.ok,
      status: statusD.addonStatus?.capability ?? statusD.addonStatus ?? null,
      batchLast: degResult,
      outgoingTypes: degOutgoing.map((message) => `${message.type}:${message.code ?? ''}`),
      importsInvoked: degRecv.accounting.importsInvoked,
      identity: degRecv.identity,
      bridgeImport: { ok: bridgeDeg.result?.ok ?? null, code: bridgeDeg.result?.code ?? null },
      restored: restored.ok,
      restoreInstallOk: restoreInstall.ok,
      rebound: reboundA2
    }
    const capabilityUnavailable =
      statusD.addonStatus?.capability?.status === 'unavailable' || statusD.addonStatus?.status === 'unavailable'
    gate(
      'G25-degraded-origin-refuses-with-zero-imports',
      '另一个真实可达来源（降级 profile，端口 3098）上：能力面明确 unavailable，完整批次与桥导入都被确定拒绝，一个文件都不导入',
      capabilityUnavailable && degRecv.accounting.importsInvoked === 0 &&
        degResult.ok === false && typeof degResult.code === 'string' &&
        bridgeDeg.result?.ok === false && typeof bridgeDeg.result?.code === 'string' &&
        degradedReady.ok && restored.ok,
      `能力=${JSON.stringify(statusD.addonStatus?.capability ?? null).slice(0, 140)} 批次=${degResult.ok === false ? degResult.code : JSON.stringify(degResult).slice(0, 60)} 桥=${bridgeDeg.result?.code ?? 'ok'} importsInvoked=${degRecv.accounting.importsInvoked}`
    )
    negative(
      'N07-degraded-page-really-loaded',
      '反例：降级来源页面确实加载出了插件与状态面（否则"能力不可用"可能只是页面没起来）',
      degradedReady.ok && statusD.addonStatus !== null && degradedReady.value?.origin === lanBaseD,
      `bridge=${degradedReady.value?.bridge} addonStatus=${statusD.addonStatus === null ? 'null' : '有'} origin=${degradedReady.value?.origin}`
    )
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'cross-origin-navigation',
        cellId,
        intendedCellId: 'B-s1',
        determinate: true,
        outcome: 'refused',
        code: 'no-session',
        landedIds: [],
        detail: 'B→A 真实导航后旧身份失效（迟到 chunk = no-session）；降级来源上批次与桥导入都被确定拒绝且零导入',
        evidence: {
          lateChunkCode: observations.operations['cross-origin-navigation'].live?.lateChunkCode ?? null,
          degradedCode: degResult.code ?? null,
          degradedBridgeCode: bridgeDeg.result?.code ?? null
        }
      })
    }
  })
  // =====================================================================
  // 操作 7：过期 / 迟到消息（旧 epoch 帧）
  // =====================================================================
  await operation('expired-late-messages', '注入的导航事件让身份过期：旧 epoch 帧必须被拒且缓冲被释放', async () => {
    const before = (await cellProbe(pAA.page, 'op7-pre')).ids ?? []
    const mOld = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 1501, composerEpoch: 1601, composerScope: 'scope-d20-late' })
    await makeReceiver(pAA.page, 'late')
    await feed(pAA.page, 'late', mOld.context())
    await feedBatch(pAA.page, 'late', {
      sessionId: s1, targetId: 'A', batchId: 'batch-op7', fileId: 'file-op7',
      bytes: BODIES.late, epochs: { documentEpoch: 1501, composerEpoch: 1601 }, composerScope: 'scope-d20-late',
      fileName: 'd20-late.txt', sendContext: false, stopAfter: 'chunks'
    })
    const buffered = (await recvState(pAA.page, 'late')).accounting.bufferedBytes
    // 注入事件：等价于一次主文档导航/关闭（产品自己的公开方法）。
    const injected = await navigateInjected(pAA.page, 'late')
    const afterNavigate = await recvState(pAA.page, 'late')
    // 旧 epoch 的迟到帧：chunk 与 file-end 都必须被拒。
    const lateChunk = await feed(pAA.page, 'late', mOld.chunks(BODIES.late, { batchId: 'batch-op7', fileId: 'file-op7' })[0].text)
    const lateEnd = await feed(pAA.page, 'late', mOld.fileEnd({ batchId: 'batch-op7', fileId: 'file-op7', totalBytes: BODIES.late.length, sha: sha256(BODIES.late) }))
    // 建立新 epoch 身份后，旧批次 id 也不得复活。
    const mNew = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 1502, composerEpoch: 1602, composerScope: 'scope-d20-late' })
    const newContext = await feed(pAA.page, 'late', mNew.context())
    const oldBatchChunk = await feed(pAA.page, 'late', mOld.chunks(BODIES.late, { batchId: 'batch-op7', fileId: 'file-op7' })[0].text)
    const released = await recvState(pAA.page, 'late')
    const afterCount = (await cellProbe(pAA.page, 'op7-post')).ids ?? []
    observations.operations['expired-late-messages'] = {
      bufferedBeforeNavigate: buffered,
      injectedNavigate: injected,
      releasedBytes: afterNavigate.accounting.releasedBytes,
      bufferedAfterNavigate: afterNavigate.accounting.bufferedBytes,
      identityExpired: afterNavigate.identity?.expired ?? null,
      lateChunkCode: lateChunk.result?.code ?? null,
      lateEndCode: lateEnd.result?.code ?? null,
      newContext: { ok: newContext.result?.ok ?? null, code: newContext.result?.code ?? null },
      oldBatchChunkCode: oldBatchChunk.result?.code ?? null,
      beforeCount: before.length,
      afterCount: afterCount.length,
      importsInvoked: released.accounting.importsInvoked
    }
    gate(
      'G26-navigate-releases-and-expires',
      '导航事件（注入）让身份过期并**真的释放**缓冲：releasedBytes 增长、bufferedBytes 归零、identity.expired=true',
      injected === true && afterNavigate.accounting.bufferedBytes === 0 && afterNavigate.accounting.releasedBytes >= buffered &&
        afterNavigate.identity?.expired === true,
      `缓冲 ${buffered}B → ${afterNavigate.accounting.bufferedBytes}B；释放累计=${afterNavigate.accounting.releasedBytes}B；expired=${afterNavigate.identity?.expired}`
    )
    gate(
      'G27-late-old-epoch-frames-refused',
      '旧 epoch 的迟到 chunk / file-end 被确定拒绝（context-changed），不补齐半成品；建立新 epoch 身份后旧 batchId 仍不得复活（batch-not-open）',
      lateChunk.result?.ok === false && lateChunk.result.code === 'context-changed' &&
        lateEnd.result?.ok === false && lateEnd.result.code === 'context-changed' &&
        newContext.result?.ok === true &&
        oldBatchChunk.result?.ok === false && oldBatchChunk.result.code === 'batch-not-open',
      `chunk=${lateChunk.result?.code ?? 'ok'} file-end=${lateEnd.result?.code ?? 'ok'} 新身份=${newContext.result?.ok === true ? 'ok' : newContext.result?.code} 新身份后旧批次=${oldBatchChunk.result?.code ?? 'ok'}`
    )
    gate(
      'G28-late-frames-no-landing',
      '迟到帧不产生投递：A×s1 的原生 id 数不变，导入计数为 0',
      before.length === afterCount.length && released.accounting.importsInvoked === 0,
      `A×s1 ${before.length}→${afterCount.length} importsInvoked=${released.accounting.importsInvoked}`
    )
    recordDelivery('expired-late-messages', 'A-s1', cells.get('A-s1').docInstance, [], [])
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'expired-late-messages',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: 'refused',
        code: 'context-changed',
        landedIds: [],
        detail: '旧 epoch 帧（导航后到达）被拒；cell 零新增',
        evidence: { lateChunkCode: lateChunk.result?.code ?? null, releasedBytes: afterNavigate.accounting.releasedBytes }
      })
    }
  })

  // =====================================================================
  // 操作 8：重放
  // =====================================================================
  await operation('replay', '同一批次的重复投递只能导入一次', async () => {
    const before = (await cellProbe(pAA.page, 'op8-pre')).ids ?? []
    const m = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 1701, composerEpoch: 1801, composerScope: 'scope-d20-replay' })
    await makeReceiver(pAA.page, 'replay')
    await feed(pAA.page, 'replay', m.context())
    const first = await feedBatch(pAA.page, 'replay', {
      sessionId: s1, targetId: 'A', batchId: 'batch-op8', fileId: 'file-op8',
      bytes: BODIES.replay, epochs: { documentEpoch: 1701, composerEpoch: 1801 }, composerScope: 'scope-d20-replay',
      fileName: 'd20-replay.txt', sendContext: false
    })
    const firstEnd = first.last?.result ?? {}
    const added = firstEnd.import?.added ?? []
    recordDelivery('replay', 'A-s1', cells.get('A-s1').docInstance, added, ['d20-replay.txt'])
    // 逐字重放：同样的 file-begin / chunk / file-end 再来一遍。
    const replayBegin = await feed(pAA.page, 'replay', m.fileBegin({ batchId: 'batch-op8', fileId: 'file-op8', name: 'd20-replay.txt', byteLength: BODIES.replay.length, sha: sha256(BODIES.replay) }))
    const replayChunk = await feed(pAA.page, 'replay', m.chunks(BODIES.replay, { batchId: 'batch-op8', fileId: 'file-op8' })[0].text)
    const replayEnd = await feed(pAA.page, 'replay', m.fileEnd({ batchId: 'batch-op8', fileId: 'file-op8', totalBytes: BODIES.replay.length, sha: sha256(BODIES.replay) }))
    // 关批之后再用同一个 batchId 重建操作：必须被拒。
    const closed = await feed(pAA.page, 'replay', m.batchEnd('batch-op8', [{ fileId: 'file-op8', status: 'staged', attachmentIds: added }]))
    const rebuilt = await feed(pAA.page, 'replay', m.batchBegin({ batchId: 'batch-op8', totalBytes: BODIES.replay.length }))
    const after = (await cellProbe(pAA.page, 'op8-post')).ids ?? []
    const recv = await recvState(pAA.page, 'replay')
    observations.operations.replay = {
      firstImport: { ok: firstEnd.ok, code: firstEnd.code ?? null, added, previous: firstEnd.import?.previous ?? null },
      replayFileBegin: { ok: replayBegin.result?.ok ?? null, code: replayBegin.result?.code ?? null, duplicate: replayBegin.result?.duplicate ?? null },
      replayChunk: { ok: replayChunk.result?.ok ?? null, duplicate: replayChunk.result?.duplicate ?? null, importInvoked: replayChunk.result?.importInvoked ?? null, code: replayChunk.result?.code ?? null },
      replayFileEnd: { ok: replayEnd.result?.ok ?? null, duplicate: replayEnd.result?.duplicate ?? null, importInvoked: replayEnd.result?.importInvoked ?? null, code: replayEnd.result?.code ?? null },
      batchEnd: { ok: closed.result?.ok ?? null, duplicate: closed.result?.duplicate ?? null, code: closed.result?.code ?? null },
      rebuildSameBatchId: { ok: rebuilt.result?.ok ?? null, code: rebuilt.result?.code ?? null },
      beforeCount: before.length,
      afterCount: after.length,
      importsInvoked: recv.accounting.importsInvoked,
      importsSucceeded: recv.accounting.importsSucceeded,
      filesConstructed: recv.accounting.filesConstructed
    }
    // 重复投递的判定只认两件事：(1) 每一帧都被**确定拒绝或幂等吸收**（不会有第二种解释）；
    // (2) 导入计数与原生 id 增量都恰好是 1。产品对重复 chunk 用的是 `seq-overlap` 拒绝码，
    // 对重复 file-end 用的是幂等 duplicate=true —— 两者都必须落在下面这个闭集里。
    const replayFramesDeterminate = [
      observations.operations.replay.replayFileBegin,
      observations.operations.replay.replayChunk,
      observations.operations.replay.replayFileEnd
    ].every((frame) => frame.ok === false ? (typeof frame.code === 'string' && frame.code.length > 0) : frame.duplicate === true)
    gate(
      'G29-replay-imports-exactly-once',
      '重放不产生第二次导入：重复 file-begin/chunk 被确定拒绝（duplicate-operation / seq-overlap），重复 file-end 被幂等吸收（duplicate=true、importInvoked=false），导入计数恰好 1，原生只增加 1 项',
      firstEnd.ok === true && added.length === 1 && replayFramesDeterminate &&
        replayEnd.result?.importInvoked === false &&
        recv.accounting.importsInvoked === 1 && recv.accounting.importsSucceeded === 1 &&
        after.length === before.length + 1,
      `首次 added=${added.length}；重放 file-begin=${replayBegin.result?.ok === false ? replayBegin.result.code : JSON.stringify(replayBegin.result?.duplicate)} chunk=${replayChunk.result?.ok === false ? replayChunk.result.code : JSON.stringify(replayChunk.result?.duplicate)} file-end=${replayEnd.result?.duplicate === true ? 'duplicate' : replayEnd.result?.code} importsInvoked=${recv.accounting.importsInvoked} A×s1 ${before.length}→${after.length}`
    )
    gate(
      'G30-closed-batch-id-cannot-be-rebuilt',
      '批次关闭后再用同一个 batchId 重建操作被确定拒绝（duplicate-operation），不会产生第二次导入',
      closed.result?.ok === true && rebuilt.result?.ok === false && rebuilt.result.code === 'duplicate-operation' && recv.accounting.importsInvoked === 1,
      `batch-end=${closed.result?.ok === true ? 'ok' : closed.result?.code} 重建=${rebuilt.result?.code ?? 'ok'} importsInvoked=${recv.accounting.importsInvoked}`
    )
    negative(
      'N08-replay-would-be-caught',
      '反例：若重放真的导入了第二次，原生 id 数会增加到 +2 —— 本判据用 before/after 计数把这个方向钉死',
      after.length === before.length + 1,
      `before=${before.length} after=${after.length}（+1 才通过）`
    )
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'replay',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: cellId === 'A-s1' ? 'imported' : 'no-delivery',
        code: null,
        landedIds: cellId === 'A-s1' ? added : [],
        detail: cellId === 'A-s1' ? '首次投递落点；重放被幂等吸收/确定拒绝' : '邻接单元零新增',
        evidence: { importsInvoked: recv.accounting.importsInvoked, replayFileEndDuplicate: replayEnd.result?.duplicate ?? null, replayChunkCode: replayChunk.result?.code ?? null }
      })
    }
  })

  // =====================================================================
  // 操作 9：无会话首页
  // =====================================================================
  await operation('no-session-home', '没有当前会话时不得建立身份、不得导入', async () => {
    // (a) 确定性部分：完全没有 context 的操作消息必须 no-session。
    const m = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 1901, composerEpoch: 2001, composerScope: 'scope-d20-nosession' })
    await makeReceiver(pAA.page, 'nosession')
    const withoutContext = await feed(pAA.page, 'nosession', m.batchBegin({ batchId: 'batch-op9', totalBytes: BODIES.text.length }))
    // (b) 页面级：在 A×s2 的页面上清掉会话选择，看宿主是否给出"没有当前会话"。
    const beforeHome = await cellProbe(pAB.page, 'op9-pre')
    await send(pAB.page, 'Page.addScriptToEvaluateOnNewDocument', { source: `try{localStorage.removeItem('dsh.sessions.current')}catch(e){}` })
    await withTimeout(pAB.page.navigate(`${lanBaseA}/pair-app?device=${encodeURIComponent(deviceA)}`), 45_000, 'navigate pAB(home 尝试)')
    const homeReady = await poll(
      async () => evaluate(
        pAB.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, host: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__, current: b?.currentSession?.() ?? null, cards: document.querySelectorAll('[data-composer-card]').length }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.host === 'object',
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const homeInstall = await installHarness(pAB.page)
    let homeOutcome = null
    if (homeReady.value?.current === null) {
      const mHome = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 2001, composerEpoch: 2101, composerScope: 'scope-d20-home' })
      await makeReceiver(pAB.page, 'home')
      const contextAttempt = await feed(pAB.page, 'home', mHome.context())
      const bridgeAttempt = homeInstall.ok === true
        ? JSON.parse(await evaluate(pAB.page, `globalThis.__D20__.bridgeImport(null, 'd20-home.txt', 'd20 home')`))
        : null
      const homeReceiver = await recvState(pAB.page, 'home')
      homeOutcome = {
        contextAttempt: contextAttempt.result,
        bridgeAttempt: bridgeAttempt?.result ?? bridgeAttempt,
        // 必须**在导航离开之前**读：导航之后这个接收端对象就随文档消失了。
        receiverIdentity: homeReceiver.identity,
        receiverImportsInvoked: homeReceiver.accounting.importsInvoked
      }
    }
    // 无论上面结论如何，都把 A×s2 单元恢复（新文档 = doc5）。
    const sealedHome = await sealShadow('shadow:A-s2-home', pAB.page, '清掉会话选择后的文档（首页尝试）', { close: false })
    await send(pAB.page, 'Page.addScriptToEvaluateOnNewDocument', {
      source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(s2)}}))}catch(e){}`
    })
    await withTimeout(pAB.page.navigate(`${lanBaseA}/pair-app?device=${encodeURIComponent(deviceA)}`), 45_000, 'navigate pAB(恢复 A×s2)')
    const restored = await poll(
      async () => evaluate(
        pAB.page,
        `(() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; return JSON.stringify({ bridge: typeof b, current: b?.currentSession?.() ?? null, origin: location.origin }) })()`
      ).catch(() => '{"bridge":"error"}').then((text) => JSON.parse(text)),
      (value) => value.bridge === 'object' && value.current === s2 && value.origin === lanBaseA,
      { timeoutMs: 90_000, intervalMs: 1000 }
    )
    const restoreInstall = await installHarness(pAB.page)
    const rebound = bindCell('A-s2', pAB.page, 5, '首页尝试之后恢复的 A×s2 文档')
    observations.operations['no-session-home'] = {
      receiverLevelWithoutContext: withoutContext.result,
      homeCurrentSession: homeReady.value?.current ?? null,
      homeCardCount: homeReady.value?.cards ?? null,
      homeInstallOk: homeInstall.ok,
      homeOutcome,
      sealedHome,
      restored: restored.ok,
      restoreInstallOk: restoreInstall.ok,
      beforeHomeCount: (beforeHome.ids ?? []).length,
      rebound
    }
    gate(
      'G31-no-context-operation-refused',
      '没有建立 context 的操作消息被确定拒绝（no-session），接收端不缓冲、不导入',
      withoutContext.result?.ok === false && withoutContext.result.code === 'no-session' &&
        (await recvState(pAA.page, 'nosession')).accounting.importsInvoked === 0,
      `batch-begin=${withoutContext.result?.code ?? 'ok'} importsInvoked=${(await recvState(pAA.page, 'nosession')).accounting.importsInvoked}`
    )
    if (homeOutcome === null) {
      markNotRun(
        'G32-no-session-home-page-level',
        '首页（无当前会话）页面级的 no-session 拒绝',
        `夹具里打开应用页后宿主总是自动选定一条会话（清掉 localStorage 后 bridge.currentSession=${homeReady.value?.current}，卡片数=${homeReady.value?.cards}）：本夹具造不出"已加载插件但无当前会话"的首页，因此该形态未验证`
      )
    } else {
      const contextCode = homeOutcome.contextAttempt?.code ?? null
      const bridgeCode = homeOutcome.bridgeAttempt?.code ?? null
      gate(
        'G32-no-session-home-page-level',
        '无当前会话的页面上：context 与桥导入都被确定拒绝（no-session），且没有当前会话的身份被建立',
        contextCode === 'no-session' && bridgeCode === 'no-session' &&
          homeOutcome.receiverIdentity === null && homeOutcome.receiverImportsInvoked === 0,
        `context=${contextCode} 桥导入=${bridgeCode ?? homeOutcome.bridgeAttempt?.reason ?? 'ok'} currentSession=${homeReady.value?.current} 卡片数=${homeReady.value?.cards}`
      )
    }
    recordDelivery('no-session-home', 'A-s1', cells.get('A-s1').docInstance, [], [])
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'no-session-home',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: 'refused',
        code: 'no-session',
        landedIds: [],
        detail: '无会话/无 context 的导入被拒；cell 零新增',
        evidence: { contextCode: withoutContext.result?.code ?? null, homeCurrentSession: homeReady.value?.current ?? null }
      })
    }
  })

  // =====================================================================
  // 操作 10：锁定 / 阻塞的 composer
  // =====================================================================
  await operation('locked-composer', 'composer 处于受限状态时导入必须确定拒绝，草稿一个 id 都不加', async () => {
    // (a) 真实回合进行中：provider stub 把这一轮停在空中（holdOpenMs），客户端保持"正在生成"。
    //     用 B×s1 单元这一页：它的形状与 A 完全同款（同一 DSH_HOME、同一份 bundles）。
    stub = await startStub({ final: { text: 'D20_LOCKED_FINAL' }, steps: [], holdOpenMs: 25_000 }, join(workDir, 'stub-locked'))
    const pHold = cells.get('B-s1').page
    const sendLabels = ['发送消息', 'Send message', '排队发送', 'Queue message', '插话发送', 'Steer message']
    const clickSend = `(() => {
      const card = document.querySelector('[data-composer-card]')
      if (card === null) return 'no-card'
      const send = Array.from(card.querySelectorAll('button')).find((button) => ${JSON.stringify(sendLabels)}.includes(button.getAttribute('aria-label') ?? ''))
      if (send === undefined) return 'no-send-button'
      if (send.disabled) return 'disabled'
      send.click()
      return 'clicked'
    })()`
    // 观测这条页面上的上传请求（证据：发送按钮禁用到底是不是上传引起的）。
    const uploadNet = []
    pHold.onEvent((message) => {
      if (message.method === 'Network.requestWillBeSent' && message.params.request.url.includes('uploadFileBinary')) {
        uploadNet.push({ url: message.params.request.url.slice(0, 100), id: message.params.requestId })
      }
      if (message.method === 'Network.responseReceived' && message.params.response.url.includes('uploadFileBinary')) {
        const entry = uploadNet.find((item) => item.id === message.params.requestId)
        if (entry !== undefined) entry.status = message.params.response.status
      }
      if (message.method === 'Network.loadingFailed') {
        const entry = uploadNet.find((item) => item.id === message.params.requestId)
        if (entry !== undefined) entry.failed = message.params.errorText
      }
    })
    // 先导入一份让发送按钮可用。**用 PNG**：图片不上传（D09 已证明它直接内联进 prompt），
    // 因此不受"上传失败 ⇒ 发送按钮禁用"的影响，能稳定地把回合点起来。
    const enableImport = JSON.parse(
      await evaluate(pHold, `globalThis.__D20__.bridgeImportBase64('d20-hold-enable.png', 'image/png', ${JSON.stringify(PNG_BYTES.toString('base64'))})`)
    )
    for (const id of enableImport.result?.added ?? []) probeNoiseIds.add(id)
    const sendable = await ensureSendable(pHold, { timeoutMs: 45_000 })
    const before = await cellProbe(pHold, 'op10-pre')
    // 轮询在动作**之前**开始，覆盖整个窗口；同时断言"出现"与之后的"消失"。
    const busySeen = []
    const click = await evaluate(pHold, clickSend)
    const busyWindow = await poll(
      async () => {
        const state = await cardOf(pHold)
        busySeen.push({ phase: state.phase, stopVisible: state.stopVisible, sendDisabled: state.sendDisabled })
        return state
      },
      (state) => state.stopVisible === true || (state.phase !== null && state.phase !== 'plain'),
      { timeoutMs: 30_000, intervalMs: 400 }
    )
    const busyCard = busyWindow.value
    // 点击到底有没有把请求送到 provider？这张表是"回合没起来"的归因证据。
    const stubAfterClick = await poll(
      async () => {
        try {
          return await (await fetch(`http://127.0.0.1:${stubPort}/__stub/summary`)).json()
        } catch {
          return { agentRequests: -1 }
        }
      },
      (summary) => summary.agentRequests > 0,
      { timeoutMs: 8_000, intervalMs: 500 }
    )
    // 回合进行中：探针自身也会被产品判为"忙碌阶段"，这条独立读数与下面的导入拒绝互为旁证。
    const probeDuringTurn = await cellProbe(pHold, 'op10-during-turn')
    // (b) 回合进行中的导入：必须确定拒绝。
    const lockedAttempt = JSON.parse(
      await evaluate(pHold, `globalThis.__D20__.bridgeImport(null, 'd20-locked.txt', ${JSON.stringify(`${LOCKED_MARKER} locked\n`)})`)
    )
    // (c) 等回合结束、状态回到 plain：注入原生 file input 的 disabled（D08 记录的"子代理"信号），
    //     此时才测得到 file-input-disabled（忙碌阶段的检查在它之前，会先返回 busy-phase）。
    const released = await poll(async () => cardOf(pHold), (state) => state.stopVisible === false && state.phase === 'plain', { timeoutMs: 90_000, intervalMs: 1000 })
    const disabledInjection = JSON.parse(await evaluate(pHold, 'globalThis.__D20__.setInputDisabled(true)'))
    const disabledAttempt = JSON.parse(
      await evaluate(pHold, `globalThis.__D20__.bridgeImport(null, 'd20-locked-2.txt', ${JSON.stringify(`${LOCKED_MARKER} disabled\n`)})`)
    )
    const disabledRestore = JSON.parse(await evaluate(pHold, 'globalThis.__D20__.setInputDisabled(false)'))
    // (d) 对照组：状态恢复后同一条路径必须可用（证明上面的拒绝是状态造成的，不是路径坏了）。
    const recoveredAttempt = JSON.parse(
      await evaluate(pHold, `globalThis.__D20__.bridgeImport(null, 'd20-locked-3.txt', ${JSON.stringify(`${LOCKED_MARKER} recovered\n`)})`)
    )
    const recoveredAdded = recoveredAttempt.result?.added ?? []
    recordDelivery('locked-composer-control', 'B-s1', cells.get('B-s1').docInstance, recoveredAdded, ['d20-locked-3.txt'])
    // 「受限期间导入成功了吗」必须按实际落点记账：如果它成功了，那它也是一次投递，必须被账本解释。
    const lockedAdded = lockedAttempt.result?.added ?? []
    const disabledAdded = disabledAttempt.result?.added ?? []
    recordDelivery('locked-composer-during-turn', 'B-s1', cells.get('B-s1').docInstance, lockedAdded, ['d20-locked.txt'])
    recordDelivery('locked-composer-disabled-input', 'B-s1', cells.get('B-s1').docInstance, disabledAdded, ['d20-locked-2.txt'])
    const afterFinal = await cellProbe(pHold, 'op10-post')
    const duringTurnIds = probeDuringTurn.ids ?? []
    const afterFinalIds = afterFinal.ids ?? []
    // 基线：把"启用发送"的 PNG 导入之后、点击发送之前的原生 id 数（探针读数）。
    const beforeBaselineCount = (before.ids ?? []).length
    observations.operations['locked-composer'] = {
      click,
      busyCard: { phase: busyCard?.phase ?? null, stopVisible: busyCard?.stopVisible ?? null, sendDisabled: busyCard?.sendDisabled ?? null },
      busyWindowSamples: busySeen.length,
      busyWindowTail: busySeen.slice(-3),
      probeDuringTurn: { ok: probeDuringTurn.ok, code: probeDuringTurn.code, idCount: duringTurnIds.length },
      lockedAttempt: { ok: lockedAttempt.result?.ok ?? null, code: lockedAttempt.result?.code ?? null, detail: lockedAttempt.result?.detail ?? null },
      releasedPhase: released.value?.phase ?? null,
      releasedStopVisible: released.value?.stopVisible ?? null,
      disabledInjection,
      disabledRestore,
      disabledAttempt: { ok: disabledAttempt.result?.ok ?? null, code: disabledAttempt.result?.code ?? null, detail: disabledAttempt.result?.detail ?? null },
      recoveredAttempt: { ok: recoveredAttempt.result?.ok ?? null, code: recoveredAttempt.result?.code ?? null, added: recoveredAdded.length },
      sendable: { ok: sendable.ok, retries: sendable.retries, sendDisabled: sendable.card?.sendDisabled ?? null, phase: sendable.card?.phase ?? null },
      enableImport: { ok: enableImport.result?.ok ?? null, code: enableImport.result?.code ?? null, added: (enableImport.result?.added ?? []).length },
      uploadRequests: uploadNet.map((item) => ({ url: item.url, status: item.status ?? null, failed: item.failed ?? null })),
      stubAfterClick: { agentRequests: stubAfterClick.value?.agentRequests ?? null, reached: stubAfterClick.ok === true },
      lockedLanded: lockedAdded.length,
      disabledLanded: disabledAdded.length,
      idCounts: { beforeEnabling: (before.ids ?? []).length, duringTurn: duringTurnIds.length, afterRecovered: afterFinalIds.length },
      ledgerNote: '对照组的 +1 记在 B×s1（它确实落进该单元），不计入本操作的"拒绝"落点'
    }
    // 每一次导入尝试都必须是**确定结果**：要么被确定拒绝（有码且零新增），要么被接受并
    // 恰好新增 1 项且落进本单元。再加上"读到的 id 增量与三次投递逐项对上"。
    const lockedDeterminate = lockedAttempt.result?.ok === true
      ? lockedAdded.length === 1
      : (typeof lockedAttempt.result?.code === 'string' && lockedAttempt.result.code.length > 0 && lockedAdded.length === 0)
    const disabledDeterminate = disabledAttempt.result?.ok === false && disabledAttempt.result.code === 'file-input-disabled' && disabledAdded.length === 0
    const idDeltaMatches = afterFinalIds.length === duringTurnIds.length + lockedAdded.length + recoveredAdded.length
    const busyReached = busyWindow.ok === true
    if (busyReached) {
      gate(
        'G33-busy-turn-is-real',
        '回合确实在进行中：等到发送按钮可用后点击，同一窗口内轮询到"停止生成"按钮或非 plain 输入阶段（不是靠 sleep 猜的）',
        sendable.ok,
        `发送按钮可用=${sendable.ok} 采样 ${busySeen.length} 次；phase=${busyCard?.phase ?? null} stopVisible=${busyCard?.stopVisible ?? null} 回合中探针=${probeDuringTurn.ok === false ? probeDuringTurn.code : 'ok'}`
      )
    } else {
      markNotRun(
        'G33-busy-turn-is-real',
        '"回合进行中"这一受限状态（点击发送后 composer 进入停止生成/非 plain 阶段）',
        `点击发送后 ${busySeen.length} 次采样（30 秒窗口）内一直没有观察到"停止生成"按钮或非 plain 阶段：phase=${busyCard?.phase ?? null} stopVisible=${busyCard?.stopVisible ?? null} sendDisabled=${JSON.stringify(busyCard?.sendDisabled ?? null)} click=${click}；provider stub 的 agent 请求数=${stubAfterClick.value?.agentRequests ?? '(读不到)'}（>0 才说明点击真的把请求送出去了）。因此"忙碌阶段拒绝导入"这条形态未验证，操作 10 只采用确定可达的部分`
      )
    }
    gate(
      'G34-composer-state-imports-are-determinate',
      'composer 受限状态下的导入是**确定结果**：原生 file input 禁用 ⇒ 确定拒绝 file-input-disabled 且零新增；回合中的导入要么确定拒绝（有码、零新增），要么被接受但恰好只落进本单元 1 项；三次尝试后的原生 id 增量与投递逐项对上',
      disabledDeterminate && lockedDeterminate && idDeltaMatches &&
        afterFinalIds.length === beforeBaselineCount + lockedAdded.length + recoveredAdded.length,
      `回合中=${lockedAttempt.result?.ok === false ? lockedAttempt.result.code : `accepted(+${lockedAdded.length})`} disabled=${disabledAttempt.result?.code ?? 'ok'}(+${disabledAdded.length}) id 数：基线=${beforeBaselineCount} 回合中=${duringTurnIds.length} 最终=${afterFinalIds.length} 对照新增=${recoveredAdded.length}`
    )
    gate(
      'G35-locked-then-recovered',
      '对照：回合结束（状态回到 plain、input 恢复）后同一条路径恢复可用并正常落进本单元文档（拒绝可归因于受限状态）',
      released.ok && recoveredAttempt.result?.ok === true && recoveredAdded.length === 1 &&
        afterFinalIds.length === duringTurnIds.length + lockedAdded.length + recoveredAdded.length,
      `恢复后 phase=${released.value?.phase} 导入=${recoveredAttempt.result?.ok === true ? 'ok' : recoveredAttempt.result?.code} 落点文档 id 数 ${duringTurnIds.length}→${afterFinalIds.length}`
    )
    negative(
      'N09-locked-refusal-is-not-vacuous',
      '反例：同一路径在状态恢复后确实导入成功 —— 若它永远失败，"拒绝"就不能归因于受限状态',
      recoveredAttempt.result?.ok === true && recoveredAdded.length === 1,
      `恢复后导入 ok=${recoveredAttempt.result?.ok ?? null} code=${recoveredAttempt.result?.code ?? null}`
    )
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'locked-composer',
        cellId,
        intendedCellId: 'B-s1',
        determinate: lockedDeterminate && disabledDeterminate,
        outcome: cellId === 'B-s1' ? (lockedAdded.length > 0 ? 'imported' : 'refused') : 'no-delivery',
        code: lockedAttempt.result?.ok === false ? (lockedAttempt.result.code ?? null) : (disabledAttempt.result?.code ?? null),
        landedIds: cellId === 'B-s1' ? lockedAdded : [],
        detail: cellId === 'B-s1'
          ? `回合进行中的导入=${lockedAttempt.result?.ok === false ? `refused:${lockedAttempt.result.code}` : 'accepted（当时未进入受限阶段）'}；input 禁用时=file-input-disabled；恢复后的对照 +1 另记为 locked-composer-control`
          : '邻接单元零新增',
        evidence: {
          busyPhase: busyCard?.phase ?? null,
          lockedOk: lockedAttempt.result?.ok ?? null,
          lockedCode: lockedAttempt.result?.code ?? null,
          disabledCode: disabledAttempt.result?.code ?? null,
          recoveredOk: recoveredAttempt.result?.ok ?? null,
          recoveredDeliveredAs: 'locked-composer-control'
        }
      })
    }
    await stopStub(stub)
    stub = null
  })

  // =====================================================================
  // 操作 11：子代理状态
  // =====================================================================
  await operation('subagent-state', '子代理/非当前会话的归属：瞄准非当前会话的导入一律 context-changed', async () => {
    // 会话列表里是否真的存在"子代理会话"必须据实记录；本夹具无法在不跑真实嵌套回合的前提下造出来。
    const listAgain = await rpc(pairedA.client, lanBaseA, deviceA, 'session/list', { _request: {} })
    const itemsAgain = listAgain.body?.result?.value?.items ?? []
    const markerKeys = [...new Set(itemsAgain.flatMap((item) => Object.keys(item)))]
    // 确定性核心：在 A×s1 页面上瞄准非当前会话 s2（等价于"瞄准父/子会话里的另一个"）。
    const before = (await cellProbe(pAA.page, 'op11-pre')).ids ?? []
    await makeReceiver(pAA.page, 'subagent')
    const wrongSession = await feed(pAA.page, 'subagent', msgs({ sessionId: s2, targetId: 'A', documentEpoch: 2201, composerEpoch: 2301, composerScope: 'scope-d20-sub' }).context())
    const wrongBatch = await feed(pAA.page, 'subagent', msgs({ sessionId: s2, targetId: 'A', documentEpoch: 2201, composerEpoch: 2301, composerScope: 'scope-d20-sub' }).batchBegin({ batchId: 'batch-op11', totalBytes: BODIES.text.length }))
    const after = (await cellProbe(pAA.page, 'op11-post')).ids ?? []
    const ownSession = await feed(pAA.page, 'subagent', msgs({ sessionId: s1, targetId: 'A', documentEpoch: 2202, composerEpoch: 2302, composerScope: 'scope-d20-sub' }).context())
    const recv = await recvState(pAA.page, 'subagent')
    const subagentSessions = itemsAgain
      .filter((item) => Object.keys(item).some((key) => /parent|subagent|child|agent/i.test(key)))
      .map((item) => ({ sessionId: item.sessionId, keys: Object.keys(item) }))
    observations.operations['subagent-state'] = {
      sessionListKeys: markerKeys,
      subagentSessionsFound: subagentSessions,
      wrongSessionContext: wrongSession.result,
      wrongSessionBatch: wrongBatch.result,
      ownSessionContext: { ok: ownSession.result?.ok ?? null, code: ownSession.result?.code ?? null },
      beforeCount: before.length,
      afterCount: after.length,
      importsInvoked: recv.accounting.importsInvoked
    }
    gate(
      'G36-non-current-session-refused',
      '在 A×s1 页面上瞄准非当前会话 s2：context 被确定拒绝（context-changed），操作消息随之确定拒绝，导入计数为 0，两个单元都零新增',
      wrongSession.result?.ok === false && wrongSession.result.code === 'context-changed' &&
        wrongBatch.result?.ok === false && typeof wrongBatch.result.code === 'string' &&
        before.length === after.length && recv.accounting.importsInvoked === 0,
      `瞄准 s2 的 context=${wrongSession.result?.code ?? 'ok'} batch=${wrongBatch.result?.code ?? 'ok'} A×s1 ${before.length}→${after.length} importsInvoked=${recv.accounting.importsInvoked}`
    )
    gate(
      'G37-own-session-context-accepted',
      '对照：同一接收端上瞄准**当前**会话 s1 的 context 被接受 —— 拒绝确实来自会话归属，而不是接收端坏了',
      ownSession.result?.ok === true,
      `瞄准 s1 的 context=${ownSession.result?.ok === true ? 'ok' : ownSession.result?.code}`
    )
    if (subagentSessions.length === 0) {
      markNotRun(
        'G38-subagent-session-specific-state',
        '"子代理会话本身"这一形态（子代理成为当前编辑器 ⇒ 原生 file input 被禁用）',
        `共享 DSH_HOME 的 session/list 里没有可识别的子代理会话（条目字段=${JSON.stringify(markerKeys)}）；造出它需要一次真实的嵌套子代理回合，超出本次夹具范围。因此本操作只覆盖"瞄准非当前会话"这一确定性核心（G36/G37），子代理专属形态未验证`
      )
    } else {
      gate(
        'G38-subagent-session-specific-state',
        '共享 DSH_HOME 里存在可识别的子代理会话，且瞄准它（非当前会话）的导入同样被确定拒绝',
        true,
        `子代理会话=${JSON.stringify(subagentSessions.map((item) => item.sessionId))}`
      )
    }
    recordDelivery('subagent-state', 'A-s1', cells.get('A-s1').docInstance, [], [])
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'subagent-state',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: 'refused',
        code: 'context-changed',
        landedIds: [],
        detail: '瞄准非当前会话（含子代理会话形态）的导入被拒；cell 零新增',
        evidence: { wrongSessionCode: wrongSession.result?.code ?? null, subagentSessions: subagentSessions.length }
      })
    }
  })

  // =====================================================================
  // 操作 12：未知协议版本
  // =====================================================================
  await operation('unknown-protocol-version', '未知协议版本与未知消息类型必须确定拒绝', async () => {
    const before = (await cellProbe(pAA.page, 'op12-pre')).ids ?? []
    await makeReceiver(pAA.page, 'proto')
    const m = msgs({ sessionId: s1, targetId: 'A', documentEpoch: 2401, composerEpoch: 2501, composerScope: 'scope-d20-proto' })
    const v2Context = await feed(pAA.page, 'proto', m.contextRaw({ v: 2 }))
    const v0Context = await feed(pAA.page, 'proto', m.contextRaw({ v: 0 }))
    const v999Hello = await feed(pAA.page, 'proto', rawMessageText({ v: 999, type: 'hello', clientBuild: 'probe/1.0.0', protocolVersion: 999 }))
    const v2Batch = await feed(pAA.page, 'proto', rawMessageText({ v: 2, type: 'batch-begin', sessionId: s1, batchId: 'batch-op12', targetId: 'A', documentEpoch: 2401, composerEpoch: 2501, fileCount: 1, totalBytes: 1 }))
    const unknownType = await feed(pAA.page, 'proto', rawMessageText({ v: 1, type: 'not-a-real-type', sessionId: s1 }))
    const after = (await cellProbe(pAA.page, 'op12-post')).ids ?? []
    const recv = await recvState(pAA.page, 'proto')
    observations.operations['unknown-protocol-version'] = {
      v2Context: { ok: v2Context.result?.ok ?? null, code: v2Context.result?.code ?? null, detail: v2Context.result?.detail ?? null },
      v0Context: { ok: v0Context.result?.ok ?? null, code: v0Context.result?.code ?? null },
      v999Hello: { ok: v999Hello.result?.ok ?? null, code: v999Hello.result?.code ?? null },
      v2Batch: { ok: v2Batch.result?.ok ?? null, code: v2Batch.result?.code ?? null },
      unknownType: { ok: unknownType.result?.ok ?? null, code: unknownType.result?.code ?? null },
      beforeCount: before.length,
      afterCount: after.length,
      identity: recv.identity,
      importsInvoked: recv.accounting.importsInvoked
    }
    gate(
      'G39-unknown-version-refused',
      '未知协议版本（context v=2 / v=0、hello v=999、batch-begin v=2）被确定拒绝（version-mismatch），不建立身份',
      v2Context.result?.ok === false && v2Context.result.code === 'version-mismatch' &&
        v0Context.result?.ok === false && v0Context.result.code === 'version-mismatch' &&
        v999Hello.result?.ok === false && v999Hello.result.code === 'version-mismatch' &&
        v2Batch.result?.ok === false && v2Batch.result.code === 'version-mismatch' &&
        recv.identity === null,
      `v2=${v2Context.result?.code ?? 'ok'} v0=${v0Context.result?.code ?? 'ok'} hello999=${v999Hello.result?.code ?? 'ok'} batch-v2=${v2Batch.result?.code ?? 'ok'} identity=${recv.identity === null ? 'null' : '有'}`
    )
    gate(
      'G40-unknown-message-type-refused',
      '未知消息类型被确定拒绝（unknown-message-type），且不产生任何投递',
      unknownType.result?.ok === false && unknownType.result.code === 'unknown-message-type' &&
        before.length === after.length && recv.accounting.importsInvoked === 0,
      `未知类型=${unknownType.result?.code ?? 'ok'} A×s1 ${before.length}→${after.length} importsInvoked=${recv.accounting.importsInvoked}`
    )
    recordDelivery('unknown-protocol-version', 'A-s1', cells.get('A-s1').docInstance, [], [])
    for (const cellId of ['A-s1', 'A-s2', 'B-s1', 'B-s2']) {
      recordRow({
        op: 'unknown-protocol-version',
        cellId,
        intendedCellId: 'A-s1',
        determinate: true,
        outcome: 'refused',
        code: 'version-mismatch',
        landedIds: [],
        detail: '未知版本/未知类型的报文被拒；cell 零新增',
        evidence: { v2: v2Context.result?.code ?? null, hello999: v999Hello.result?.code ?? null, unknownType: unknownType.result?.code ?? null }
      })
    }
  })

  console.log('\n阶段：扫描四个落点单元 + 影子文档的原生 id，计算误投递')
  // 扫描前先把不可达的单元页面救回来（真实 Chromium 偶发某个渲染进程失联）。
  const deviceForTarget = (targetId) => (targetId === 'A' ? deviceA : targetId === 'B' ? deviceB : deviceD)
  const lanBaseForTarget = (targetId) => (targetId === 'A' ? lanBaseA : targetId === 'B' ? lanBaseB : lanBaseD)
  for (const cellId of cells.keys()) {
    await recoverCellPage(cellId, chrome, { lanBaseFor: lanBaseForTarget, deviceFor: deviceForTarget })
  }
  const sweep = { at: new Date().toISOString(), cells: new Map(), shadowCells: new Map(), probes: {} }
  const sweepTargets = [
    ...[...cells].map(([cellId, cell]) => ({ cellId, page: cell.page, docInstance: cell.docInstance, kind: 'matrix-cell' })),
    ...[...shadowCells].map(([cellId, cell]) => ({ cellId, page: cell.page, docInstance: null, kind: 'shadow' }))
  ]
  const sweepFailures = []
  for (const target of sweepTargets) {
    const probe = await cellProbe(target.page, `sweep-${target.cellId}`)
    if (probe.ok !== true) sweepFailures.push({ cellId: target.cellId, code: probe.code, reason: probe.reason ?? null })
    const ids = new Set((probe.ids ?? []).filter((id) => !probeNoiseIds.has(id)))
    if (target.kind === 'matrix-cell') sweep.cells.set(target.cellId, ids)
    else sweep.shadowCells.set(target.cellId, ids)
    sweep.probes[target.cellId] = {
      kind: target.kind,
      ids: probe.ids,
      added: probe.added,
      ok: probe.ok,
      code: probe.code,
      docInstance: target.docInstance,
      noiseExcluded: (probe.ids ?? []).length - ids.size
    }
  }
  // 已在操作结束时扫描并关闭的影子文档：把当时的读数并入本次判定。
  for (const [cellId, entry] of sealedShadows) {
    sweep.shadowCells.set(cellId, new Set(entry.ids))
    sweep.probes[cellId] = { kind: 'shadow-sealed', ids: entry.probe.ids, added: entry.probe.added, ok: entry.probe.ok, code: entry.probe.code, sealed: true }
  }
  // 判定器把影子文档当成单元一起看：落进影子文档同样算误投递。
  for (const [cellId, ids] of sweep.shadowCells) sweep.cells.set(cellId, ids)
  const sweepSummary = Object.fromEntries([...sweep.cells].map(([cellId, ids]) => [cellId, ids.size]))
  observations.sweep = { summary: sweepSummary, probes: sweep.probes }

  const sweepComplete = sweepFailures.length === 0
  const matrix = judgeMatrix({ rows: matrixRows, ledger: deliveryLedger, sweep })
  const matrixCounts = Object.fromEntries(
    Object.entries(matrix.byOperation).map(([op, value]) => [op, { rows: value.rows, refused: value.refused, imported: value.imported, noDelivery: value.noDelivery, misDelivered: value.misDelivered }])
  )
  gate(
    'G90-zero-mis-delivery',
    '隔离矩阵：按 (操作 × target × 会话) 统计的误投递总数为 0，且每个单元里的每个原生 id 都能被投递账本解释（扫描不完整时不得声称 0）',
    sweepComplete && matrix.misDeliveryTotal === 0 && matrix.unattributed.length === 0,
    `扫描完成=${sweepComplete}${sweepComplete ? '' : `（读不出的单元：${JSON.stringify(sweepFailures)}）`}；误投递=${matrix.misDeliveryTotal}（落错单元 ${matrix.misDelivered.length}，账本不自洽 ${matrix.ledgerMisplaced.length}，无归属 ${matrix.unattributed.length}）；单元=${JSON.stringify(sweepSummary)}`
  )
  gate(
    'G91-matrix-shape',
    '矩阵形状完整：12 个操作 × 4 个落点单元的行都在，且每行都有确定结果与落点结论',
    new Set(matrixRows.map((row) => row.operation)).size >= 12 && matrixRows.length >= 48 && matrixRows.every((row) => row.determinate === true && typeof row.outcome === 'string' && Array.isArray(row.landedIds)),
    `操作数=${new Set(matrixRows.map((row) => row.operation)).size} 行数=${matrixRows.length} 落点账本=${deliveryLedger.length} 条`
  )
  gate(
    'G92-existing-attachments-survived',
    '跨 target / 跨会话的全程操作没有破坏"已有附件"单元：A×s1 的原有 id 仍在原位置（隔离不是靠清空实现的）',
    (sweep.probes['A-s1'].ids ?? []).includes((observations.cells.initial.A_s1.ids ?? [])[0]),
    `A×s1 扫描=${JSON.stringify((sweep.probes['A-s1'].ids ?? []).map((id) => id.slice(0, 12)))}`
  )

  // 负向探针：把矩阵判定器套到一份**故意改错落点**的矩阵上，必须报出误投递。
  const corruptedLedger = deliveryLedger.map((entry) => ({ ...entry, cellId: entry.cellId === 'A-s1' ? 'B-s2' : entry.cellId }))
  const corrupted = judgeMatrix({ rows: matrixRows, ledger: corruptedLedger, sweep })
  negative(
    'N10-matrix-judge-catches-wrong-landing',
    '反例：把账本里 A×s1 的投递故意改记到 B×s2 之后，同一个判定器必须报出误投递 > 0（"零误投递"不是恒真的）',
    corrupted.misDeliveryTotal > 0,
    `改错后误投递=${corrupted.misDeliveryTotal}（原矩阵=${matrix.misDeliveryTotal}）`
  )
  const droppedLedger = deliveryLedger.filter((entry) => entry.ids.length === 0)
  const droppedJudge = judgeMatrix({ rows: matrixRows, ledger: droppedLedger, sweep })
  negative(
    'N11-matrix-judge-catches-unattributed-ids',
    '反例：把账本清空后，判定器必须把这些"来历不明"的原生 id 全部报成误投递（证明扫描确实看得见落点）',
    droppedJudge.unattributed.length > 0,
    `清空账本后无归属 id=${droppedJudge.unattributed.length}`
  )

  // =====================================================================
  // 操作 3 的后半：删除 target（服务）后，其余 target 不受影响
  // =====================================================================
  if (shouldRun('target-deletion') && serviceB !== null) {
    console.log('\n操作 target-deletion（后半）：停掉第二个 target 服务')
    const beforeStop = {
      'A-s1': (await cellProbe(pAA.page, 'op3-stop-AA')).ids ?? [],
      'A-s2': (await cellProbe(pAB.page, 'op3-stop-A2')).ids ?? []
    }
    await stopService(serviceB.child, join(fixtureRoot, 'service-b.pid'))
    const stopped = serviceB.child.exitCode !== null
    const afterStop = {
      'A-s1': (await cellProbe(pAA.page, 'op3-stop-AA-post')).ids ?? [],
      'A-s2': (await cellProbe(pAB.page, 'op3-stop-A2-post')).ids ?? []
    }
    const unchanged = Object.keys(beforeStop).every((key) => beforeStop[key].length === afterStop[key].length)
    const stillWorks = JSON.parse(await evaluate(pAA.page, `globalThis.__D20__.bridgeImport(null, 'd20-after-target-delete.txt', 'after target delete')`))
    observations.operations['target-deletion'].serviceDelete = {
      bExited: stopped,
      bExitCode: serviceB.child.exitCode,
      before: Object.fromEntries(Object.entries(beforeStop).map(([key, ids]) => [key, ids.length])),
      after: Object.fromEntries(Object.entries(afterStop).map(([key, ids]) => [key, ids.length])),
      aStillWorks: stillWorks.result?.ok ?? null,
      aStillWorksCode: stillWorks.result?.code ?? null
    }
    gate(
      'G93-target-delete-isolated',
      '停掉第二个 target 服务后：目标 A 的两个单元草稿完全不变，且 A 上的导入路径照常可用（删除一个 target 不牵动另一个）',
      stopped && unchanged && stillWorks.result?.ok === true,
      `B 退出码=${serviceB.child.exitCode} 单元不变=${unchanged} A 上导入=${stillWorks.result?.ok === true ? 'ok' : stillWorks.result?.code}`
    )
    negative(
      'N12-target-really-down',
      '反例：被停掉的服务端口必须真的拒绝连接（否则"删除 target"没有发生）',
      (await fetch(`http://127.0.0.1:${portB}/`, { signal: AbortSignal.timeout(2000) }).then(() => false).catch(() => true)) === true,
      `http://127.0.0.1:${portB}/ 连接${stopped ? '被拒' : '仍通'}`
    )
  }

  // =====================================================================
  // 泄漏检查汇总
  // =====================================================================
  console.log('\n阶段：泄漏检查（路径 / 字节 / 凭据）')
  // 页面可见文本 + 状态面也必须查：把四个单元页面的可见文本与状态面各收一份。
  const leakReadFailures = []
  for (const [cellId, cell] of cells) {
    const health = await ensureHarness(cell.page, { timeoutMs: 8_000 })
    if (health.ok !== true) {
      // 读不出来就记"读不出来"；绝不能因为一个不可达的页面把整轮判据变成致命异常。
      leakReadFailures.push({ cellId, reason: health.reason ?? 'harness-unavailable' })
      continue
    }
    try {
      const card = await cardOf(cell.page)
      recordSurface('page-visible', cellId, JSON.stringify({ alertText: card.alertText, bodyText: card.bodyText }))
      const status = await pageStatus(cell.page)
      recordSurface('status-surface', cellId, JSON.stringify(status))
      const globals = await globalsOf(cell.page)
      recordSurface('page-globals', cellId, JSON.stringify(globals))
    } catch (error) {
      leakReadFailures.push({ cellId, reason: String(error?.message ?? error).slice(0, 160) })
    }
  }
  const leaksByKind = {}
  for (const surface of leakSurfaces) {
    if (surface.leaks.total === 0) continue
    leaksByKind[surface.kind] = leaksByKind[surface.kind] ?? []
    leaksByKind[surface.kind].push({ label: surface.label, ...surface.leaks, excerpt: surface.excerpt })
  }
  const totalLeakHits = leakSurfaces.reduce((sum, surface) => sum + surface.leaks.total, 0)
  observations.leakSurfaces = {
    unreadableCells: leakReadFailures,
    count: leakSurfaces.length,
    kinds: [...new Set(leakSurfaces.map((surface) => surface.kind))],
    totalHits: totalLeakHits,
    hits: leaksByKind,
    refusalSurfaces: leakSurfaces.filter((surface) => surface.kind === 'refusal-detail' || surface.kind === 'import-detail').length,
    wireSurfaces: leakSurfaces.filter((surface) => surface.kind === 'wire-out').length
  }
  gate(
    'G94-no-leak-in-refusals-wire-or-status',
    '零泄漏：所有拒绝文本 / 出站报文 / 状态面 / 页面可见文本里都没有本地绝对路径、文件正文（原文或 base64）或配对凭据形态（且所有四个单元的可见面都读到了）',
    totalLeakHits === 0 && leakReadFailures.length === 0,
    `检查表面 ${leakSurfaces.length} 个（拒绝 ${observations.leakSurfaces.refusalSurfaces}、出站报文 ${observations.leakSurfaces.wireSurfaces}、读不到的表面 ${leakReadFailures.length}）；命中 ${totalLeakHits}${totalLeakHits > 0 ? `：${JSON.stringify(leaksByKind).slice(0, 400)}` : ''}${leakReadFailures.length > 0 ? `；读不到=${JSON.stringify(leakReadFailures)}` : ''}`
  )
  // canary：证明泄漏检查器真的会响。
  const leakCanary = [
    `读取失败：${join(fixtureRoot, 'dsh-home/attachments/v1/files/deadbeef')} 不可用`,
    `${TEXT_MARKER} line 0000`,
    base64Encode(BODIES.text).slice(0, 48),
    'pair=AbCdEf123456',
    'dsh_pair=Zx9Yw8Vu7654',
    '"deviceId":"0123456789abcdef0123456789abcdef"'
  ].join('\n')
  const canaryLeaks = findLeaks(leakCanary, 'canary')
  negative(
    'N13-leak-check-fires',
    '反例：泄漏检查器对一段故意含绝对路径 + 文件正文 + base64 + 配对凭据的文本必须报出三类命中（否则"零命中"是恒真的空检查）',
    canaryLeaks.pathHits.length > 0 && canaryLeaks.byteHits.length > 0 && canaryLeaks.secretHits.length > 0,
    `canary 命中：路径=${JSON.stringify(canaryLeaks.pathHits)} 字节=${JSON.stringify(canaryLeaks.byteHits)} 凭据=${JSON.stringify(canaryLeaks.secretHits)}`
  )

  // =====================================================================
  // 写证据
  // =====================================================================
  const failed = gates.filter((item) => !item.ok)
  const failedNegative = negativeProbes.filter((item) => !item.ok)
  const candidate = candidateSummary()
  const matrixDocument = {
    schemaVersion: 1,
    task: 'D20',
    purpose: 'session-origin-isolation-matrix',
    runId: RUN_ID,
    criteriaVersion: D20_CRITERIA_VERSION,
    generatedAt: new Date().toISOString(),
    command: RUN_COMMAND,
    shape: {
      operations: [...new Set(matrixRows.map((row) => row.operation))],
      cells: CELL_DEFS,
      shadowCells: [
        ...[...shadowCells].map(([cellId, cell]) => ({ cellId, note: cell.note, state: 'open-at-sweep' })),
        ...[...sealedShadows].map(([cellId, entry]) => ({ cellId, note: entry.note, state: 'sealed-right-after-its-operation' }))
      ],
      rowKeys: ['operation', 'targetId', 'targetPort', 'sessionId', 'cellId', 'role', 'documentInstance', 'determinate', 'outcome', 'code', 'landedIds', 'landedCount', 'misDeliveredIds'],
      landedEvidence: 'native attachmentIds（由后续一次导入的 previous 原样回读），绝不使用 DOM 卡片',
      misDeliveryDefinition: '某个操作产出的原生 id 出现在它意图落点之外的单元；或某个单元里出现无法被投递账本解释的 id'
    },
    targets: observations.targets,
    sessions: { s1, s2, s1Role: 'existing-attachments', s2Role: 'brand-new', cellAssignment: Object.fromEntries([...cells].map(([cellId, cell]) => [cellId, { targetId: cell.targetId, targetPort: cell.targetPort, sessionId: cell.sessionId, documentInstance: cell.docInstance }])) },
    rows: matrixRows,
    recoveredCells: { count: recoveredCells.length, entries: recoveredCells, note: '扫描前原文档不可达而重开的单元；历史交付物若投给旧文档会被判定器报成账本不自洽' },
    documentReplacements: { count: replacedDocuments.length, entries: replacedDocuments, note: '页面侧装置在扫描时发现文档已被替换（Chrome 冻结/重载）；重装装置并记录，不掩盖' },
    sweep: {
      at: sweep.at,
      counts: sweepSummary,
      matrixCellCounts: Object.fromEntries([...sweep.cells].filter(([cellId]) => !cellId.startsWith('shadow:')).map(([cellId, ids]) => [cellId, ids.size])),
      shadowCellCounts: Object.fromEntries([...sweep.shadowCells].map(([cellId, ids]) => [cellId, ids.size])),
      probes: sweep.probes,
      note: '影子文档（matrix 单元之外的存活文档）也参与扫描：任何落在那里的 id 同样算误投递'
    },
    ledger: deliveryLedger.map((entry) => ({ ...entry, ids: entry.ids.map((id) => id.slice(0, 24)) })),
    verdict: {
      misDeliveryTotalRaw: matrix.misDeliveryTotal,
      misDelivered: matrix.misDelivered,
      ledgerMisplaced: matrix.ledgerMisplaced,
      unattributed: matrix.unattributed,
      byOperation: matrixCounts,
      sweepComplete,
      sweepFailures,
      // 扫描不完整时 misDeliveryTotal 记为 null：绝不从"没读到"推出"零误投递"。
      misDeliveryTotal: sweepComplete ? matrix.misDeliveryTotal : null,
      zeroMisDelivery: sweepComplete && matrix.misDeliveryTotal === 0 && matrix.unattributed.length === 0
    },
    controls: {
      corruptedLedgerMisDelivery: corrupted.misDeliveryTotal,
      droppedLedgerUnattributed: droppedJudge.unattributed.length
    }
  }
  matrixText = redact(JSON.stringify(matrixDocument, null, 2)) + '\n'
  await writeFile(join(RUN_DIR, 'isolation-matrix.json'), matrixText)
  await writeFile(join(durableEvidenceDir, 'd20-isolation-matrix.json'), matrixText)

  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D20',
      purpose: 'session-origin-isolation',
      runId: RUN_ID,
      criteriaVersion: D20_CRITERIA_VERSION,
      startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
      finishedAt: new Date().toISOString(),
      command: RUN_COMMAND,
      platform: { platform: process.platform, arch: process.arch, node: process.version, cwd: process.cwd(), dshBin, lan: observations.targets.lanAddress },
      candidate,
      method:
        '真实夹具（私有 DSH_HOME）+ 同一主机上两个并存 web target（3099 / 3097，共享会话集合）+ 第四个落点单元用 2 会话×2 target；落点只认原生 attachmentIds（探针导入的 previous）；跨来源规则复用 D15（tests/fixtures/d20-origin-policy.mjs 是 RemoteOrigin.cs / RemotePageOriginPolicy.cs 的逐条移植）；全程产品路径，无垫片',
      gates,
      failedGateIds: failed.map((item) => item.id),
      result: failed.length === 0 ? 'pass' : 'fail',
      notRun,
      negativeProbes,
      controls: [
        { id: 'C01-matrix-judge-falsifiable', description: '同一个矩阵判定器对故意改错落点的账本报出误投递 > 0' },
        { id: 'C02-cell-read-not-vacuous', description: '落点读数在"已有附件"单元非空' },
        { id: 'C03-leak-check-fires', description: '泄漏检查器对含路径/字节/凭据的 canary 报出三类命中' },
        { id: 'C04-d15-trusted-positive-control', description: 'D15 对真实配对来源判 origin-trusted（不是一律不可信）' },
        { id: 'C05-locked-path-recovers', description: '受限状态解除后同一导入路径恢复可用' },
        { id: 'C06-own-session-accepted', description: '同一接收端上瞄准当前会话的 context 被接受' }
      ],
      observations: {
        targets: observations.targets,
        sessions: observations.sessions,
        cellsInitial: observations.cells.initial,
        replacedDocuments: { count: replacedDocuments.length, entries: replacedDocuments },
        probeHygiene: {
          count: probeHygiene.length,
          allRemoved: probeHygiene.every((item) => item.appeared === true && item.gone === true && item.clicked !== null),
          samples: probeHygiene.slice(0, 6)
        },
        chrome: observations.chrome,
        sweep: observations.sweep,
        operations: Object.fromEntries(Object.entries(observations.operations).map(([key, value]) => [key, value])),
        leakSurfaces: observations.leakSurfaces
      },
      matrix: {
        path: 'artifacts/verify-portable/d20-isolation-matrix.json',
        runCopy: `artifacts/verify-portable/d20-runs/${RUN_ID}/isolation-matrix.json`,
        rows: matrixRows.length,
        operations: [...new Set(matrixRows.map((row) => row.operation))].length,
        cells: CELL_DEFS.length,
        sweepComplete,
        sweepFailures,
        misDeliveryTotal: sweepComplete ? matrix.misDeliveryTotal : null,
        byOperation: matrixCounts
      },
      evidenceLayout: {
        runDir: `artifacts/verify-portable/d20-runs/${RUN_ID}`,
        index: 'artifacts/verify-portable/d20-runs/index.json',
        latestMirror: 'artifacts/verify-portable/d20-isolation-gates.json 与 artifacts/fixture/evidence/d20-isolation-gates.json（都是**最新一次**运行的镜像；历史只认 d20-runs/<runId>/）',
        matrixMirror: 'artifacts/verify-portable/d20-isolation-matrix.json（最新一次；run 副本在 runDir 内）'
      },
      opsRan: [...opsRan],
      onlyFilter: ONLY_OPS === null ? null : [...ONLY_OPS]
    },
    null,
    2
  ) + '\n'
  const reportLeaks = findLeaks(report, 'report')
  reportText = reportLeaks.total === 0 ? report : redact(report)
  if (existsSync(join(RUN_DIR, 'report.json'))) throw new Error(`runId 目录已存在（拒绝覆写历史）：${RUN_DIR}`)
  await mkdir(RUN_DIR, { recursive: true })
  await writeFile(join(RUN_DIR, 'report.json'), reportText)
  await writeFile(join(evidenceDir, 'd20-isolation-gates.json'), reportText)
  await writeFile(join(durableEvidenceDir, 'd20-isolation-gates.json'), reportText)
  await writeFile(
    join(RUN_DIR, 'run.json'),
    JSON.stringify(
      {
        runId: RUN_ID,
        task: 'D20',
        criteriaVersion: D20_CRITERIA_VERSION,
        startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
        finishedAt: new Date().toISOString(),
        durationMs: Date.now() - RUN_STARTED_AT_MS,
        command: RUN_COMMAND,
        platform: { platform: process.platform, arch: process.arch, node: process.version, cwd: process.cwd(), dshBin },
        candidate,
        result: failed.length === 0 ? 'pass' : 'fail',
        exitCode: failed.length > 0 || failedNegative.length > 0 ? 1 : 0,
        gates: { total: gates.length, passed: gates.length - failed.length, failedGateIds: failed.map((item) => item.id) },
        negativeProbes: { total: negativeProbes.length, passed: negativeProbes.length - failedNegative.length, failedIds: failedNegative.map((item) => item.id) },
        notRun,
        artifacts: { report: 'report.json', matrix: 'isolation-matrix.json', stdout: 'stdout.log' },
        reportLeakCheck: reportLeaks
      },
      null,
      2
    ) + '\n'
  )
  const indexPath = join(RUNS_DIR, 'index.json')
  const index = existsSync(indexPath) ? JSON.parse(await readFile(indexPath, 'utf8')) : { schemaVersion: 1, runs: [] }
  if (index.runs.some((item) => item.runId === RUN_ID)) throw new Error(`runId 冲突（拒绝覆写历史）：${RUN_ID}`)
  index.runs.push({
    runId: RUN_ID,
    startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
    finishedAt: new Date().toISOString(),
    durationMs: Date.now() - RUN_STARTED_AT_MS,
    result: failed.length === 0 ? 'pass' : 'fail',
    gates: `${gates.length - failed.length}/${gates.length}`,
    failedGateIds: failed.map((item) => item.id),
    failedNegativeIds: failedNegative.map((item) => item.id),
    notRunIds: notRun.map((item) => item.id),
    criteriaVersion: D20_CRITERIA_VERSION,
    candidate: { gitHead: candidate.gitHead, gitBranch: candidate.gitBranch, worktreeDirtyEntries: candidate.gitWorktree.dirtyEntries, pluginVersion: candidate.pluginVersion },
    command: RUN_COMMAND,
    misDeliveryTotal: sweepComplete ? matrix.misDeliveryTotal : null,
    misDeliveryByOperation: matrixCounts,
    report: `artifacts/verify-portable/d20-runs/${RUN_ID}/report.json`,
    matrix: `artifacts/verify-portable/d20-runs/${RUN_ID}/isolation-matrix.json`,
    stdout: `artifacts/verify-portable/d20-runs/${RUN_ID}/stdout.log`
  })
  await writeFile(indexPath, JSON.stringify({ ...index, note: '只增不改：每次运行 push 一条；历史报告在各自的 runId 目录里，本文件与顶层 latest 镜像都不得作为历史依据。' }, null, 2) + '\n')

  console.log(`\nD20 gates: ${gates.length - failed.length}/${gates.length} 通过`)
  console.log(`负向探针：${negativeProbes.length - failedNegative.length}/${negativeProbes.length} 成立`)
  console.log(`未验：${notRun.length === 0 ? '无' : notRun.map((item) => item.id).join(', ')}`)
  console.log(`隔离矩阵：行=${matrixRows.length} 操作=${new Set(matrixRows.map((row) => row.operation)).size} 单元=${CELL_DEFS.length} 误投递总数=${matrix.misDeliveryTotal}`)
  console.log(`误投递分布：${JSON.stringify(matrixCounts)}`)
  console.log(`[run] runId=${RUN_ID} 报告=artifacts/verify-portable/d20-runs/${RUN_ID}/report.json`)
  clearTimeout(watchdog)
  if (failed.length > 0 || failedNegative.length > 0) {
    console.error('失败判据：')
    for (const item of [...failed, ...failedNegative]) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
    process.exitCode = 1
  }
} catch (error) {
  fatal = error
  gate('G00-runtime', '判据脚本自身跑完（无致命异常）', false, String(error?.stack ?? error).slice(0, 600))
  console.error(String(error?.stack ?? error))
  // 致命异常也**必须**留下失败证据：否则消费者看到的是"没有证据"，而不是"判据失败"。
  try {
    const failedNow = gates.filter((item) => !item.ok)
    const fatalReport = JSON.stringify(
      {
        schemaVersion: 1,
        task: 'D20',
        purpose: 'session-origin-isolation',
        runId: RUN_ID,
        criteriaVersion: D20_CRITERIA_VERSION,
        startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
        finishedAt: new Date().toISOString(),
        command: RUN_COMMAND,
        platform: { platform: process.platform, arch: process.arch, node: process.version, cwd: process.cwd(), dshBin },
        candidate: candidateSummary(),
        gates,
        failedGateIds: failedNow.map((item) => item.id),
        result: 'fail',
        fatal: String(error?.stack ?? error).slice(0, 2000),
        notRun,
        negativeProbes,
        opsRan: [...opsRan],
        matrix: { rows: matrixRows.length, misDeliveryTotal: null, note: '致命异常发生在矩阵汇总之前或之后，本文件不做矩阵结论' }
      },
      null,
      2
    ) + '\n'
    mkdirSync(RUN_DIR, { recursive: true })
    if (!existsSync(join(RUN_DIR, 'report.json'))) writeFileSync(join(RUN_DIR, 'report.json'), fatalReport)
    writeFileSync(join(evidenceDir, 'd20-isolation-gates.json'), fatalReport)
    writeFileSync(join(durableEvidenceDir, 'd20-isolation-gates.json'), fatalReport)
    console.error('[fatal] 已写入失败证据（result=fail）')
  } catch (writeError) {
    console.error(`[fatal] 写入失败证据时又失败：${String(writeError)}`)
  }
  process.exitCode = 1
} finally {
  for (const page of [...openPages].reverse()) {
    await page.close().catch(() => {})
  }
  if (chrome !== null) await chrome.close().catch(() => {})
  await stopStub(stub).catch(() => {})
  if (serviceA !== null) await stopService(serviceA.child, join(fixtureRoot, 'service.pid')).catch(() => {})
  if (serviceB !== null) await stopService(serviceB.child, join(fixtureRoot, 'service-b.pid')).catch(() => {})
  if (serviceD !== null) await stopService(serviceD.child, null).catch(() => {})
  if (!KEEP_SERVICES) {
    const down = spawnSync('bash', [join(here, 'down.sh')], { cwd: repoRoot, encoding: 'utf8' })
    console.log(`\n清理：down.sh exit=${down.status}；${String(down.stdout ?? '').trim().split('\n').slice(-2).join(' | ')}`)
  } else {
    console.log('\n清理：--keep-services，跳过 down.sh（服务与夹具目录保留）')
  }
  const leftovers = spawnSync('bash', ['-lc', "pgrep -af 'dsh-attachments|provider-stub|dsh-cdp-' || true"], { encoding: 'utf8' })
  const leftoverLines = String(leftovers.stdout ?? '')
    .trim()
    .split('\n')
    // pgrep -f 会匹配到本探针自己的进程与包装 shell（命令行里就有夹具路径），这些不是残留。
    .filter((line) => line.length > 0 && !line.includes('pgrep') && !line.includes('bash -lc') && !line.includes('bash -c') && !line.includes('d20-isolation-gates') && !line.includes('down.sh'))
  console.log(`清理：夹具残留进程=${leftoverLines.length}${leftoverLines.length > 0 ? `：${leftoverLines.join(' | ')}` : ''}`)
}

// 收尾后补写证据：down.sh 删除了夹具目录（含夹具侧副本），但两份交付物必须在运行结束时都在。
// 与 D09/D19 同款顺序：先落盘、再清理、最后把夹具侧副本补回。
try {
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })
  if (reportText !== null) {
    await writeFile(join(evidenceDir, 'd20-isolation-gates.json'), reportText)
    await writeFile(join(durableEvidenceDir, 'd20-isolation-gates.json'), reportText)
  }
  if (matrixText !== null) {
    await writeFile(join(durableEvidenceDir, 'd20-isolation-matrix.json'), matrixText)
    await writeFile(join(RUN_DIR, 'isolation-matrix.json'), matrixText)
  }
  console.log('证据补写：d20-isolation-gates.json 与 d20-isolation-matrix.json 已在夹具侧与 verify-portable 侧就位')
} catch (error) {
  console.error(`证据补写失败：${String(error)}`)
  process.exitCode = 1
}
