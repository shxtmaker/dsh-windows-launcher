#!/usr/bin/env node
/**
 * D19 判据：远端上传网络故障与撤销语义（可重复的故障注入 + 脱敏 trace）。
 *
 * 目标：在**真实 Chromium + 真实夹具服务 + 真实上传路径**上，把网络故障按需注入，
 * 并证明四件事：
 *
 *   1. 错误**可见且确定**：每个故障都让 composer 的文件卡进入真实的 `error` 状态
 *      （上游 `FileCard` 的 `state === 'error'`，渲染出"上传失败，点击重试"+ 重试按钮），
 *      同时记录该手势观测到的 HTTP 状态 / 网络失败 / 业务失败码。
 *   2. **不自动重发**：一个手势的网络层逻辑请求数（CDP `Network.*`）保持 1，
 *      安静窗口后不增长；prompt 请求数为 0。
 *   3. **不误投**：上传失败的附件永远不进入 `ready`；跨会话导入被确定拒绝
 *      （`context-changed`）；部分批次里已成功的文件不被重放（按文件名精确计数）。
 *   4. **不同拒绝语义被分别记录**：重试复用原草稿条目、失效 receipt 被确定性拒绝、
 *      撤销后"新请求被拒"与"在途请求被终止"分开断言、客户端取消终止在途上传。
 *   5. **拒绝被原生呈现（阶段 7）**：失效 receipt 被拒后，上游 InputBar 会把该 `promptError`
 *      交给 `showToast`，渲染成 portal 到 `document.body` 的**原生 toast**（`[role="alert"]`，
 *      class 含 `_toast_*`，文案"文件尚未上传成功，请重新添加后再试"，存活 ~4000ms）。
 *      因此断言必须在**点击发送之前**装好观察者、按 ~150ms 采样，抓"出现 → 消失"的完整
 *      生命周期，并用页面自己那次 prompt 请求的公开响应信封（CDP Network：
 *      `session/attachment-invalid` / `FILE_NOT_STAGED`）把提示与**这一次**拒绝绑定；
 *      另配两条负向对照：发送成功不得出现该提示、注入同形 toast 必须被同一观察者看到。
 *
 *   **采集与判定分离（R22d）**：本文件只负责**采集**原始序列（观察者事件 + 采样 + 信封 +
 *   请求计数），判定交给纯模块 `d19-alert-window-judge.mjs`。T02 的整体结果由该模块产出的
 *   **具名子断言记录**（每条恰好 `{id, actual, expected, pass, failureReason}`）直接算出
 *   （`computeOverall`），gate 的 detail 也**从同一批记录**渲染（`renderSubAssertionDetail`）。
 *   因此 R22c 那种"detail 里每条可见子条件都满足、整体却 FAIL"不可能再发生：明细即记录，
 *   整体即记录的全称量化。
 *
 *   **观察窗语义（R22d）**：样本按"至少 `ALERT_MIN_WINDOW_MS`，且最后一次命中之后还要有
 *   `ALERT_QUIET_TAIL_MS` 的安静尾部"结束，硬上界 `ALERT_MAX_WINDOW_MS`。这样"窗口在提示
 *   仍可见时关闭"只会出现在真的异常（提示迟迟不消失）时，而它会被**具名**记录
 *   `T02-06-observation-complete` 指出来，不再是一条从不打印自身取值的隐藏合取项。
 *   上界不放宽：单实例上界见判定器（4000ms 常量的 1.5 倍），总可见区间仍是既有的 10000ms。
 *
 * 故障注入面见 `fault-proxy.mjs`：真实上传路径是
 * `页面(非 loopback origin) → 承载 __DSH_FILE_UPLOAD__ → remote 改写
 *  → /remote/api/session/uploadFileBinary`，代理就坐在页面 origin 上，
 * 因此注入点与生产路径完全重合（不是测试专用捷径）。
 *
 * 每个故障判据都带**对照**：同一手势在注入关闭时必须是 ready + 200；
 * 另有一条负向探针证明"error 断言"并非恒真。
 *
 * 只用 tests/fixtures 既有夹具（lib.mjs / cdp.mjs / setup.sh 的私有 DSH_HOME）；
 * 不触碰 $HOME/.dsh，不杀用户自己的 Chrome。
 */

import { spawn, execFileSync } from 'node:child_process'
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { createHash, randomUUID } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { release as osRelease } from 'node:os'

import { createClient, findSecretLeaks, redact, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'
import { startFaultProxy } from './fault-proxy.mjs'
import {
  D19_ALERT_CRITERIA_VERSION,
  DEFAULT_BOUNDS as ALERT_BOUNDS,
  computeOverall as computeAlertOverall,
  judgeAlertWindow,
  renderSubAssertionDetail
} from './d19-alert-window-judge.mjs'

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
const stubPort = Number(process.env.DSH_ATTACH_STUB_PORT ?? 3901)
const readyTimeoutMs = Number(process.env.DSH_ATTACH_READY_TIMEOUT_MS ?? 120_000)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

/** 安静窗口：故障落定后等这么久，再确认请求计数没有"自己"增长。 */
const QUIET_MS = Number(process.env.DSH_ATTACH_D19_QUIET_MS ?? 2500)

/**
 * 失效 receipt 的提示观测窗（阶段 7/7b）。
 *
 * toast 只活 holdMs(3000)+fade(1000)≈4000ms（锁定包常量），任何"发完再 sleep 一个固定时长
 * 再读一次"的读法都可能正好落在卸载之后；所以这里从**发送之前**开始按 ~150ms 采样。
 *
 * 窗口语义（R22d）：
 *   - `ALERT_MIN_WINDOW_MS`：最短观察时长（采样至少覆盖这么久）；
 *   - 命中之后还要再等 `ALERT_QUIET_TAIL_MS` 的安静尾部才停 —— 这样"窗口在提示仍可见时
 *     关闭"不会变成一条隐藏的失败合取项；真的出现时由具名记录
 *     `T02-06-observation-complete` 指出来（那是观察不足，不是"活得太久"）；
 *   - `ALERT_MAX_WINDOW_MS`：硬上界，到点必停（防止提示永不消失时把脚本拖死）。
 */
const ALERT_MIN_WINDOW_MS = Number(process.env.DSH_ATTACH_D19_ALERT_WINDOW_MS ?? 12_000)
const ALERT_MAX_WINDOW_MS = Number(process.env.DSH_ATTACH_D19_ALERT_MAX_WINDOW_MS ?? 20_000)
const ALERT_QUIET_TAIL_MS = Number(process.env.DSH_ATTACH_D19_ALERT_QUIET_TAIL_MS ?? 1_000)
const ALERT_SAMPLE_INTERVAL_MS = Number(process.env.DSH_ATTACH_D19_ALERT_INTERVAL_MS ?? 150)
/** 上游 zh 文案：`file.notStaged` 的映射结果（dsh-client-ui-conversation 的 copy 表）。 */
const ALERT_EXPECTED_TEXT = '文件尚未上传成功，请重新添加后再试'
/** 该提示的文案判据：认这一类（含近义），不把"整页含失败/错误"当成证据。 */
const ALERT_ATTACHMENT_RE = /文件尚未上传成功|重新添加|未上传|not uploaded|附件|attachment/i
/** 判定上界（单实例 / 总可见区间）与判据版本都由判定器模块拥有，见 d19-alert-window-judge.mjs。 */
const ALERT_TOTAL_MAX_MS = Number(process.env.DSH_ATTACH_D19_ALERT_TOTAL_MAX_MS ?? ALERT_BOUNDS.totalMaxMs)
const ALERT_INSTANCE_MAX_MS = Number(process.env.DSH_ATTACH_D19_ALERT_INSTANCE_MAX_MS ?? ALERT_BOUNDS.instanceMaxMs)
const ALERT_INSTANCE_MIN_MS = Number(process.env.DSH_ATTACH_D19_ALERT_INSTANCE_MIN_MS ?? ALERT_BOUNDS.instanceMinMs)

// ---------- 运行身份 / 证据目录（每次运行独立 runId，历史结果不覆写） ----------

const RUN_STARTED_AT_MS = Date.now()
const RUN_ID = `d19-${new Date(RUN_STARTED_AT_MS).toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`
const RUN_COMMAND = `node ${process.argv[1] ?? 'tests/fixtures/d19-failure-gates.mjs'}${process.argv.slice(2).length === 0 ? '' : ` ${process.argv.slice(2).join(' ')}`}`
const RUNS_DIR = join(durableEvidenceDir, 'd19-runs')
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

const sha256OfFile = (path) => {
  try {
    return createHash('sha256').update(readFileSync(path)).digest('hex')
  } catch {
    return null
  }
}

/** 插件源码树的指纹：该目录在 git 里是**未跟踪**的，所以候选身份不能只靠 commit
 *  （工作树里的改动不会体现在 HEAD 上）。这里对 `src/**` 逐个文件取哈希再聚合。 */
function sourceTreeSha256(root) {
  const files = []
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = join(dir, entry.name)
      if (entry.isDirectory()) walk(path)
      else if (entry.isFile()) files.push(path)
    }
  }
  try {
    walk(root)
  } catch {
    return null
  }
  const digest = createHash('sha256')
  for (const path of files.map((value) => value).sort()) {
    digest.update(path.slice(pluginRoot.length + 1))
    digest.update('\0')
    digest.update(readFileSync(path))
    digest.update('\0')
  }
  return { sha256: digest.digest('hex'), files: files.length }
}

/** 候选摘要：提交/工作树 + 插件版本 + 实际服务的 UI 包版本（据实记录，不猜）。 */
function candidateSummary() {
  const git = (args) => {
    try {
      return execFileSync('git', args, { cwd: repoRoot, encoding: 'utf8' }).trim()
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
  const uiVersions = {}
  const installedRoot = join(process.env.HOME ?? '', '.npm-global/lib/node_modules/@deepseek-ai/dsh/node_modules/@deepseek-ai')
  for (const name of ['dsh-client-ui-conversation', 'dsh-client-ui-attachment', 'dsh-api-session-controller', 'dsh-web-frontend', 'dsh-client-file-upload']) {
    try {
      uiVersions[name] = JSON.parse(readFileSync(join(installedRoot, name, 'package.json'), 'utf8')).version ?? null
    } catch {
      uiVersions[name] = null
    }
  }
  return {
    gitHead: git(['rev-parse', 'HEAD']),
    gitBranch: git(['rev-parse', '--abbrev-ref', 'HEAD']),
    gitWorktree: { dirtyEntries: porcelain === '' ? 0 : porcelain.split('\n').filter((line) => line !== '').length, porcelainSha256: createHash('sha256').update(porcelain).digest('hex') },
    pluginVersion,
    pluginSourceTree: sourceTreeSha256(join(pluginRoot, 'src')),
    installedUiVersions: uiVersions,
    criteriaVersion: D19_ALERT_CRITERIA_VERSION,
    hashes: {
      gateScript: sha256OfFile(fileURLToPath(import.meta.url)),
      judgeModule: sha256OfFile(join(here, 'd19-alert-window-judge.mjs'))
    }
  }
}

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))

// ---------- 证据 ----------

const gates = []
const negativeProbes = []
const notRun = []
const trace = []

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

// ---------- 页面侧装置 ----------

const CHIP_DIV = `[...document.querySelectorAll('[data-composer-card] [title]')].filter((element) => element.tagName === 'DIV')`

/**
 * 读真实 composer 文件卡状态。
 *
 * 状态判据来自**上游渲染代码本身**（`dsh-client-ui-attachment` 的 `FileCard`）：
 * `state === 'error'` 时卡片正文变成一个带 `aria-label=重试上传 <name>` 的按钮，
 * 且 meta 文本是该语言的 `file.uploadFailed`。这里按"渲染出来的 DOM"读，
 * 不碰 React 内部，也不新增任何产品侧探针。
 */
const READ_CHIPS = `JSON.stringify(${CHIP_DIV}.map((element) => {
  const text = element.textContent || ''
  const buttons = [...element.querySelectorAll('button')].map((button) => button.getAttribute('aria-label') || '')
  const meta = [...element.querySelectorAll('span')].map((span) => span.textContent || '').filter((value) => value !== '')
  const failed = /上传失败|Upload failed/.test(text)
  const uploading = /上传中|Uploading/.test(text)
  return {
    name: element.getAttribute('title'),
    state: failed ? 'error' : uploading ? 'uploading' : 'ready',
    meta: meta.length === 0 ? '' : meta[meta.length - 1],
    buttons,
    retryAffordance: failed && buttons.length >= 2
  }
}))`

const INSTALL_D19 = `(() => {
  const addon = globalThis.__DSH_ATTACHMENTS_ADDON__
  const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  if (addon === undefined || bridge === undefined || host === undefined) {
    return JSON.stringify({ ok: false, reason: 'addon-not-installed' })
  }
  const state = { lastImports: {} }
  globalThis.__D19__ = state

  state.chips = () => ${READ_CHIPS}

  const bytes = (label, size) => {
    const out = new Uint8Array(size)
    for (let i = 0; i < out.length; i++) out[i] = (i + label.length) % 251
    return out
  }

  /** 真实手势：把文件塞进 composer 原生 file input（走生产桥 → 生产适配器 → 原生上传）。 */
  state.importFile = (name, sessionId, size) => {
    const file = new File([bytes(name, size === undefined ? 2048 : size)], name, { type: 'application/octet-stream' })
    const request = sessionId === null || sessionId === undefined ? { files: [file] } : { sessionId, files: [file] }
    try {
      const result = bridge.importFiles(request)
      state.lastImports[name] = result
      return JSON.stringify({ result })
    } catch (error) {
      return JSON.stringify({ threw: String(error && error.message ? error.message : error) })
    }
  }

  state.importBatch = (names) => {
    const files = names.map((name) => new File([bytes(name, 2048)], name, { type: 'application/octet-stream' }))
    try {
      const result = bridge.importFiles({ files })
      state.lastImports[names.join('+')] = result
      return JSON.stringify({ result })
    } catch (error) {
      return JSON.stringify({ threw: String(error && error.message ? error.message : error) })
    }
  }

  const findCard = (name) => ${CHIP_DIV}.find((element) => element.getAttribute('title') === name) || null
  /** 卡片内最后一个按钮是"移除"（上游 FileCard 的渲染顺序：重试在前、移除在后）。 */
  state.removeChip = (name) => {
    const card = findCard(name)
    if (card === null) return JSON.stringify({ ok: false, reason: 'no-card' })
    const buttons = [...card.querySelectorAll('button')]
    if (buttons.length === 0) return JSON.stringify({ ok: false, reason: 'no-button' })
    buttons[buttons.length - 1].click()
    return JSON.stringify({ ok: true, label: buttons[buttons.length - 1].getAttribute('aria-label') })
  }
  /** 应用内的重试入口：error 卡片里第一个按钮。 */
  state.retryChip = (name) => {
    const card = findCard(name)
    if (card === null) return JSON.stringify({ ok: false, reason: 'no-card' })
    const buttons = [...card.querySelectorAll('button')]
    if (buttons.length < 2) return JSON.stringify({ ok: false, reason: 'no-retry-affordance' })
    buttons[0].click()
    return JSON.stringify({ ok: true, label: buttons[0].getAttribute('aria-label') })
  }
  state.clickSend = () => {
    const card = document.querySelector('[data-composer-card]')
    if (card === null) return JSON.stringify({ ok: false, reason: 'no-card' })
    const send = [...card.querySelectorAll('button')].find((button) => /发送|Send/i.test(button.getAttribute('aria-label') || ''))
    if (send === undefined) return JSON.stringify({ ok: false, reason: 'no-send-button' })
    if (send.disabled) return JSON.stringify({ ok: false, reason: 'send-disabled' })
    send.click()
    return JSON.stringify({ ok: true })
  }
  state.status = () => JSON.stringify(addon.status())
  state.diagnostics = () => JSON.stringify(globalThis.__DSH_ATTACHMENTS_STATUS__ || {})

  // ---- 原生提示面（toast）观测装置，只读公开 DOM ----
  //
  // 上游 InputBar 收到 session/attachment-invalid 时把 promptError 交给 showToast，
  // 'Toast'（@deepseek-ai/dsh-web-frontend）把节点 **portal 到 document.body**，带
  // 'role="alert"' 与 CSS-module 类 '_toast_*'，并在 holdMs(3000)+fade(1000) 后被卸载。
  // 两条硬要求由此而来：观察者必须在**点击发送之前**就位；出现/消失用 MutationObserver
  // 记精确时刻，而不是"发完 sleep 固定时长再读一次"（那正好落在卸载瞬间）。
  // 注意：提示节点是 '[data-composer-card]' 的**兄弟之外**的 body 子节点，绝不用
  // '[data-composer-card] [class*="notice"]' 这类只覆盖 info 行的选择器去代表它。
  const ALERT_SELECTOR = '[role="alert"], [class*="toast"]'
  // 节点身份：只用 WeakMap 记账，**不改动产品 DOM**（不加属性、不动样式）。
  // 有了它，"同文案的新元素"（节点替换）与"两个节点同时可见"（重复挂载）才能被判据分开，
  // 而不是被合并成一段连续可见区间。
  const alertNodeIds = new WeakMap()
  let alertNodeSeq = 0
  const alertNodeId = (element) => {
    let id = alertNodeIds.get(element)
    if (id === undefined) {
      alertNodeSeq += 1
      id = 'A' + String(alertNodeSeq)
      alertNodeIds.set(element, id)
    }
    return id
  }
  const describeAlert = (element) => ({
    nodeId: alertNodeId(element),
    tag: element.tagName,
    class: element.getAttribute('class'),
    role: element.getAttribute('role'),
    parent: element.parentElement === null ? null : element.parentElement.tagName,
    parentIsBody: element.parentElement === document.body,
    inComposerCard: element.closest('[data-composer-card]') !== null,
    style: element.getAttribute('style'),
    text: (element.textContent || '').slice(0, 300)
  })
  const snapshotAlerts = () => [...document.querySelectorAll(ALERT_SELECTOR)].map(describeAlert)
  const alertState = { events: [], last: null, observer: null, installedAt: null, probe: null }
  state.alertState = alertState
  state.installAlertObserver = () => {
    if (alertState.observer !== null) alertState.observer.disconnect()
    alertState.events = []
    alertState.last = JSON.stringify(snapshotAlerts())
    alertState.installedAt = Date.now()
    alertState.events.push({ t: alertState.installedAt, kind: 'baseline', alerts: JSON.parse(alertState.last) })
    const record = (kind) => {
      const alerts = snapshotAlerts()
      const json = JSON.stringify(alerts)
      if (json === alertState.last) return
      alertState.last = json
      alertState.events.push({ t: Date.now(), kind, alerts })
    }
    alertState.observer = new MutationObserver((records) => {
      const relevant = records.some((record) => {
        for (const node of record.addedNodes) if (node.nodeType === 1 && (node.matches(ALERT_SELECTOR) || node.querySelector(ALERT_SELECTOR) !== null)) return true
        for (const node of record.removedNodes) if (node.nodeType === 1 && (node.matches(ALERT_SELECTOR) || node.querySelector(ALERT_SELECTOR) !== null)) return true
        return false
      })
      if (relevant) record('alert-mutation')
    })
    alertState.observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'role'] })
    return JSON.stringify({ ok: true, installedAt: alertState.installedAt, baseline: JSON.parse(alertState.last) })
  }
  state.sampleAlerts = () => JSON.stringify({ t: Date.now(), alerts: snapshotAlerts() })
  state.alertEvents = () => JSON.stringify(alertState.events)
  /** 检测器自检：注入/移除一个与上游同形的节点（只用于证明"没看到"不是检测器瞎）。 */
  state.injectProbeAlert = (text) => {
    const node = document.createElement('div')
    node.setAttribute('role', 'alert')
    node.setAttribute('class', '_toast_probe')
    node.textContent = text
    document.body.appendChild(node)
    alertState.probe = node
    return JSON.stringify({ ok: true, parentIsBody: node.parentElement === document.body })
  }
  state.removeProbeAlert = () => {
    if (alertState.probe === null) return JSON.stringify({ ok: false, reason: 'no-probe' })
    alertState.probe.remove()
    alertState.probe = null
    return JSON.stringify({ ok: true })
  }
  state.fenceVisible = () => {
    const text = document.body ? document.body.textContent || '' : ''
    return /未配对|Not paired|配对已失效|pairing/i.test(text)
  }
  return JSON.stringify({ ok: true })
})()`

/** 每个故障的观测记录（故障矩阵），在主流程里填充。 */
let observedFaults = {}

/** 阶段 7/7b 的原生提示观测（toast 生命周期 + 公开信封 + 负向对照 + 检测器自检），写进证据 JSON。 */
const observedPromptSurfaces = { negativeControl: null, staleReceipt: null, postRejectionDiagnostic: null, detectorSelfTest: null }

/** 从代理截留的响应正文里取信封错误码（业务失败 / 门控拒绝都会带 code）。 */
function proxyResponseCodes(entries) {
  const codes = []
  for (const entry of entries) {
    if (typeof entry.responseBody !== 'string') continue
    try {
      const parsed = JSON.parse(entry.responseBody)
      const code = parsed?.error?.code ?? parsed?.result?.error?.code ?? null
      if (typeof code === 'string') codes.push(code)
    } catch {
      // 非 JSON 正文（注入的纯文本状态码）不参与 code 提取。
    }
  }
  return codes
}

const evaluate = (page, expression, { timeoutMs = 60_000 } = {}) =>
  Promise.race([
    page.evaluate(expression, { awaitPromise: true }),
    new Promise((_resolve, reject) =>
      setTimeout(() => reject(new Error(`页面求值超时（${timeoutMs}ms）：${String(expression).slice(0, 80)}`)), timeoutMs)
    )
  ])

const readChips = async (page) => JSON.parse(await evaluate(page, `globalThis.__D19__.chips()`))

/**
 * 判定一次"发送 → 提示生命周期"观察。
 *
 * **采集与判定分离**：这里只把采集到的原始序列（事件 + 采样 + 信封 + 请求计数）交给纯模块
 * `d19-alert-window-judge.mjs`，本文件不自己写任何合取条件、不自己算整体。
 * 判定只用**公开 DOM**：`[role="alert"]`（以及 class 含 `toast` 的同一节点），文案按
 * ALERT_ATTACHMENT_RE 认到本故障这一类，逐字文案另算一条。**不使用**整页 innerText 的
 * `/失败|错误|…/` 过滤（那个面看不到"文件尚未上传成功，请重新添加后再试"），也不使用
 * `[data-composer-card] [class*="notice"]`（那是 composer 卡片的兄弟节点，且只用于 info 行）。
 */
function judgeSend(send, { envelope } = {}) {
  return judgeAlertWindow({
    events: send.events,
    samples: send.samples,
    sendAtWall: send.sendAtWall,
    windowMs: send.observedUntilMs,
    observedUntilMs: send.observedUntilMs,
    expectedText: ALERT_EXPECTED_TEXT,
    matchRe: ALERT_ATTACHMENT_RE,
    promptRequestsInWindow: send.promptRequestsInWindow,
    envelope: envelope ?? null,
    bounds: { instanceMinMs: ALERT_INSTANCE_MIN_MS, instanceMaxMs: ALERT_INSTANCE_MAX_MS, totalMaxMs: ALERT_TOTAL_MAX_MS }
  })
}

const alertNodeSummary = (alert) => ({ nodeId: alert.nodeId, role: alert.role, class: alert.class, parent: alert.parent, parentIsBody: alert.parentIsBody, inComposerCard: alert.inComposerCard, text: alert.text })

/**
 * 提示观测面的证据体（进 JSON 报告）：子断言记录 + 观测事实 + **原始事件/采样序列**。
 * 原始序列一律保留，保证下一次不会再出现"失败原因没被打印、且被下一次运行覆写"。
 */
function alertSurfaceEvidence(send, judged) {
  return {
    criteriaVersion: judged.criteriaVersion,
    selector: '[role="alert"]（portal 到 document.body；同一节点 class 含 _toast_*）',
    expectedText: ALERT_EXPECTED_TEXT,
    window: { minWindowMs: send.minWindowMs, maxWindowMs: send.maxWindowMs, observedUntilMs: send.observedUntilMs, quietTailMs: send.quietTailMs, sampleIntervalMs: send.sampleIntervalMs, sampleCount: send.samples.length },
    overall: judged.overall,
    subAssertions: judged.subAssertions,
    instances: judged.instances,
    observations: judged.observations,
    baseline: (send.baseline ?? []).map(alertNodeSummary),
    events: send.events.map((event) => ({ tMs: event.t - send.sendAtWall, kind: event.kind, alerts: (event.alerts ?? []).map(alertNodeSummary) })),
    samples: send.samples.map((sample) => ({ tMs: sample.t - send.sendAtWall, nodes: (sample.alerts ?? []).map((alert) => ({ nodeId: alert.nodeId, text: alert.text })) })),
    pageRequestsInWindow: send.pageRequestsInWindow
  }
}
const chipOf = (chips, name) => chips.find((chip) => chip.name === name) ?? null

/** 从代理截留的上传响应里取宿主开出的 receiptId（用来判断 prompt 里带的到底是不是这一张）。 */
function uploadReceipt(entries) {
  for (const entry of entries) {
    if (typeof entry.responseBody !== 'string') continue
    const match = entry.responseBody.match(/"receiptId"\s*:\s*"([^"]+)"/)
    if (match !== null) return match[1]
  }
  return null
}

/**
 * 从页面自己那次 prompt 请求体里提取"这次带了哪些文件凭证"。
 *
 * 上游形状（`dsh-client-ui-conversation` 的 `serializeAttachments`）是
 * `[{ type: 'file', receiptId }, { type: 'text', text }]`：文件只带 receiptId，不带草稿 ID。
 * 因此这里只留**结构**（文件部件数、receipt 数量、与已知 receipt 的相等关系、6 位前缀），
 * 绝不把请求正文本身写进任何产物。
 */
function summarizePromptPayload(entry, { knownReceipts = {}, currentSession = null } = {}) {
  const base = {
    available: false,
    queryNames: entry?.queryNames ?? [],
    sessionMatchesCurrent: entry?.sessionId === null || entry?.sessionId === undefined ? null : entry.sessionId === currentSession
  }
  if (entry === null || entry === undefined) return { ...base, reason: 'no-prompt-request' }
  if (typeof entry.postData !== 'string' || entry.postData === '') return { ...base, reason: 'no-post-data' }
  let parsed = null
  try {
    parsed = JSON.parse(entry.postData)
  } catch {
    return { ...base, reason: 'not-json' }
  }
  const flat = JSON.stringify(parsed)
  const receipts = [...new Set(flat.match(/"receiptId":"[^"]+"/g) ?? [])].map((match) => match.slice('"receiptId":"'.length, -1))
  const matched = (receipt) => Object.entries(knownReceipts).filter(([, value]) => typeof value === 'string' && value === receipt).map(([name]) => name)
  return {
    ...base,
    available: true,
    bytes: entry.postData.length,
    fileParts: (flat.match(/"type":"file"/g) ?? []).length,
    textParts: (flat.match(/"type":"text"/g) ?? []).length,
    receiptCount: receipts.length,
    receiptPreviews: receipts.map((receipt) => `${receipt.slice(0, 6)}…(${receipt.length})`),
    receiptMatches: receipts.map((receipt) => matched(receipt)),
    everyReceiptKnown: receipts.length > 0 && receipts.every((receipt) => matched(receipt).length > 0)
  }
}

async function poll(read, accept, { timeoutMs, intervalMs = 400 } = {}) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await read()
    if (accept(value)) return { ok: true, value }
    if (Date.now() >= deadline) return { ok: false, value }
    await sleep(intervalMs)
  }
}

async function installHarness(page, { timeoutMs = 60_000 } = {}) {
  const deadline = Date.now() + timeoutMs
  let last = null
  for (;;) {
    try {
      last = JSON.parse(await evaluate(page, INSTALL_D19))
      if (last.ok === true) return last
    } catch (error) {
      last = { ok: false, reason: String(error?.message ?? error).slice(0, 200) }
    }
    if (Date.now() >= deadline) return last ?? { ok: false, reason: 'timeout' }
    await sleep(1000)
  }
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
    child.on('exit', (code) => resolvePromise({ code, output: output.slice(-200) }))
  })
}

async function rpc(client, lanBase, deviceId, method, args) {
  const response = await client.fetch(`${lanBase}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({ type: 'client-request', rpcId: `d19-${method.replace(/\W/g, '-')}-${Date.now()}`, method, payload: { args: args ?? {} } })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

/** 启动确定性 provider stub（发送路径需要真实 agent 回合）。 */
async function startStub() {
  const recordDir = join(evidenceDir, 'd19-stub')
  await rm(recordDir, { recursive: true, force: true })
  await mkdir(recordDir, { recursive: true })
  const manifestPath = join(recordDir, 'manifest.json')
  await writeFile(manifestPath, JSON.stringify({ responses: [{ text: 'd19 done' }] }, null, 2) + '\n')
  const child = spawn(
    process.execPath,
    [join(here, 'provider/provider-stub.mjs'), '--port', String(stubPort), '--manifest', manifestPath, '--record', recordDir],
    { cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'] }
  )
  let output = ''
  child.stdout.on('data', (chunk) => { output += chunk })
  child.stderr.on('data', (chunk) => { output += chunk })
  const ready = await poll(async () => existsSync(join(recordDir, 'stub-ready.json')), (value) => value === true, { timeoutMs: 20_000, intervalMs: 200 })
  if (!ready.ok) throw new Error(`provider stub 未就绪：${output.slice(-300)}`)
  return { child, recordDir }
}

async function stopStub(stub) {
  if (stub === null || stub.child.exitCode !== null) return
  stub.child.kill('SIGTERM')
  const deadline = Date.now() + 5_000
  while (stub.child.exitCode === null && Date.now() < deadline) await sleep(200)
  if (stub.child.exitCode === null) stub.child.kill('SIGKILL')
}

// ---------- 脱敏 ----------

/** trace 里绝不允许出现这些形态；任何一条命中都判失败。 */
const HOME_DIR = process.env.HOME ?? ''

function redactTraceText(text) {
  return redact(text)
    .split(HOME_DIR).join('<home-redacted>')
    .split(repoRoot).join('<repo-redacted>')
    .split(fixtureRoot).join('<fixture-redacted>')
    .replace(/\/(?:home|Users|tmp)\/[A-Za-z0-9._\-/]+/g, '<path-redacted>')
}

function findTraceLeaks(text) {
  const leaks = findSecretLeaks(text)
  const extra = [
    { name: 'home-path', re: /\/(?:home|Users)\/[A-Za-z0-9._-]+\// },
    { name: 'absolute-fixture-path', re: /artifacts\/fixture\/dsh-home/ },
    { name: 'query-device', re: /device=[A-Za-z0-9_-]{8,}/ },
    { name: 'raw-device-header', re: /"deviceHeaderValue"\s*:\s*"[^"]+"/ }
  ]
  for (const pattern of extra) if (pattern.re.test(text)) leaks.push(pattern.name)
  return leaks
}

/** 运行索引：只增不改（读不到或解析失败时**不**静默丢弃，而是报错停下）。 */
async function readRunIndex() {
  const indexPath = join(RUNS_DIR, 'index.json')
  if (!existsSync(indexPath)) return { schemaVersion: 1, runs: [] }
  const text = await readFile(indexPath, 'utf8')
  const parsed = JSON.parse(text)
  if (!Array.isArray(parsed.runs)) throw new Error(`d19-runs/index.json 结构异常（runs 不是数组）：${indexPath}`)
  return parsed
}

// =====================================================================
// 主流程
// =====================================================================

let service = null
let proxy = null
let chrome = null
let stub = null
let fatal = null
const serviceLog = join(evidenceDir, 'service-d19-failure.log')
const net = { uploads: [], responses: [], failures: [], prompts: [], promptResponses: [], requests: [] }

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })

try {
  console.log('D19 远端上传故障与撤销语义判据（真实 Chromium + 真实夹具 + 故障代理）\n')
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  const lanBase = `http://${lanAddress}:${port}`

  console.log(`阶段 0：造两个真实会话并启动夹具服务（${lanBase}）\n`)
  // provider stub 必须先起：无头 profile 的 llm-deepseek 指向它，否则播种回合会因为
  // 连不上模型端点而返回非零（会话仍会建立，但那会把"夹具没准备好"和"夹具坏了"混在一起）。
  stub = await startStub()
  const seedA = await seedSession('d19 failure gate session A')
  const seedB = await seedSession('d19 failure gate session B')

  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))
  await waitForReady(service.child, serviceLog, readyTimeoutMs)

  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair') ?? ''
  if (deviceId === '') throw new Error('配对后没有拿到 dsh_pair 设备 cookie')

  const list = await rpc(client, lanBase, deviceId, 'session/list', { _request: {} })
  const ids = [...new Set((list.body?.result?.value?.items ?? []).map((item) => item.sessionId).filter((value) => typeof value === 'string'))]
  if (ids.length < 2) {
    throw new Error(`共享 DSH_HOME 里只有 ${ids.length} 个会话（需要两个：当前会话与"另一个会话"）；播种退出码 A=${seedA.code} B=${seedB.code}，输出 A=${seedA.output} B=${seedB.output}`)
  }
  const SESSION = ids[0]
  const OTHER_SESSION = ids[1]
  console.log(`  当前会话=${SESSION}\n  另一个会话=${OTHER_SESSION}\n`)

  proxy = await startFaultProxy({ listenHost: '0.0.0.0', targetPort: port })
  const proxyBase = `http://${lanAddress}:${proxy.port}`
  console.log(`  故障代理由页面 origin 承载：${proxyBase} → 127.0.0.1:${port}\n`)

  chrome = await launchChrome({})
  const page = await openPage(chrome.debugPort)
  page.onEvent((message) => {
    if (message.method === 'Network.requestWillBeSent') {
      const url = message.params.request.url
      // 全量请求时间线（只留 pathname + 参数**名**）：用于给 T02 的"第二次挂载"
      // 找时间相关性（例如是否恰好落在一次 history/projection 重新拉取上）。
      try {
        const parsed = new URL(url)
        if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
          net.requests.push({ at: Date.now(), method: message.params.request.method, path: parsed.pathname, queryNames: [...new Set(parsed.searchParams.keys())].sort() })
        }
      } catch {
        // 非 URL（data: / blob:）不参与。
      }
      if (url.includes('uploadFileBinary')) {
        const parsedUrl = new URL(url)
        net.uploads.push({
          at: Date.now(),
          name: parsedUrl.searchParams.get('name'),
          path: parsedUrl.pathname,
          queryNames: [...parsedUrl.searchParams.keys()],
          sessionId: parsedUrl.searchParams.get('sessionId')
        })
      }
      // prompt 请求按 requestId 记下来：阶段 7 要用它做 CDP Network.getResponseBody，
      // 读页面**自己那次**请求的响应信封（公开面），把提示与这一次拒绝绑定。
      // postData 只用来提取"这次带了哪些 receipt"（见 summarizePromptPayload），正文不落盘。
      else if (url.includes('/session/prompt')) {
        const parsedUrl = new URL(url)
        net.prompts.push({
          at: Date.now(),
          requestId: message.params.requestId,
          path: parsedUrl.pathname,
          queryNames: [...parsedUrl.searchParams.keys()],
          sessionId: parsedUrl.searchParams.get('sessionId'),
          postData: typeof message.params.request.postData === 'string' ? message.params.request.postData : null
        })
      }
    }
    if (message.method === 'Network.responseReceived') {
      const url = message.params.response.url
      if (url.includes('uploadFileBinary')) net.responses.push({ at: Date.now(), status: message.params.response.status, name: url.match(/name=([^&]*)/)?.[1] ?? null })
      else if (url.includes('/session/prompt')) net.promptResponses.push({ at: Date.now(), requestId: message.params.requestId, status: message.params.response.status })
    }
    if (message.method === 'Network.loadingFailed') {
      net.failures.push({ at: Date.now(), error: message.params.errorText, canceled: message.params.canceled === true })
    }
  })
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(SESSION)}}))}catch(e){}`
  })
  await page.navigate(`${proxyBase}/pair-app?device=${encodeURIComponent(deviceId)}`)
  const ready = await poll(
    async () => JSON.parse(await evaluate(page, `JSON.stringify({
      addon: typeof globalThis.__DSH_ATTACHMENTS_ADDON__,
      bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
      receiver: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__,
      seat: typeof globalThis.__DSH_REMOTE_CHANNEL_BOOT__,
      hook: typeof globalThis.__DSH_FILE_UPLOAD__,
      route: (globalThis.__DSH_ATTACHMENTS_STATUS__ || {}).addon?.upload?.route ?? null,
      current: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null
    })`).catch(() => '{"addon":"undefined"}')),
    (value) => value.addon === 'object' && value.receiver === 'object' && value.current === SESSION,
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  if (!ready.ok) throw new Error(`页面未就绪：${JSON.stringify(ready.value)}`)
  const installed = await installHarness(page)
  if (installed.ok !== true) throw new Error(`页面侧装置未装上：${installed.reason}`)
  const wiring = ready.value
  gate(
    'D19-00-preconditions',
    '前置：页面在代理 origin（非 loopback）上 boot，remote 改写层生效且上传路由确定为 remote-rewrite，插件三全局齐备',
    wiring.seat === 'object' && wiring.hook === 'object' && wiring.route === 'remote-rewrite',
    `seat=${wiring.seat} hook=${wiring.hook} route=${wiring.route} current=${wiring.current === SESSION}`
  )

  // ---------- 手势工具 ----------

  /** 一个"手势"：注入故障 → 真实导入 → 观测 → 静默窗口内不许自增。 */
  async function gesture({ id, fault, name, sessionId = null, quietMs = QUIET_MS, settleTimeoutMs = 20_000 }) {
    proxy.setPhase(id)
    proxy.arm(fault)
    const uploadsBefore = net.uploads.length
    const promptsBefore = net.prompts.length
    const responsesBefore = net.responses.length
    const failuresBefore = net.failures.length
    const imported = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(name)}, ${JSON.stringify(sessionId)})`))
    const settled = await poll(
      async () => chipOf(await readChips(page), name),
      (chip) => chip !== null && chip.state !== 'uploading',
      { timeoutMs: settleTimeoutMs }
    )
    const immediate = {
      uploads: net.uploads.length - uploadsBefore,
      prompts: net.prompts.length - promptsBefore,
      responses: net.responses.slice(responsesBefore).map((entry) => entry.status),
      failures: net.failures.slice(failuresBefore).map((entry) => entry.error),
      chip: settled.value?.state ?? null,
      proxyUploads: proxy.uploads({ phase: id }).length,
      proxyOutcomes: proxy.uploads({ phase: id }).map((entry) => ({ outcome: entry.outcome ?? 'forwarded', status: entry.status ?? null, injected: entry.injected }))
    }
    await sleep(quietMs)
    const afterQuiet = {
      uploads: net.uploads.length - uploadsBefore,
      prompts: net.prompts.length - promptsBefore,
      proxyUploads: proxy.uploads({ phase: id }).length,
      chip: chipOf(await readChips(page), name)?.state ?? null
    }
    proxy.disarm()
    const detail = `chip=${immediate.chip} 逻辑上传=${immediate.uploads}→${afterQuiet.uploads} 代理匹配=${immediate.proxyUploads}→${afterQuiet.proxyUploads} 响应=${JSON.stringify(immediate.responses)} 失败=${JSON.stringify(immediate.failures)} 注入=${JSON.stringify(immediate.proxyOutcomes)}`
    // trace 只收白名单字段（计数、状态码、故障规格、状态面子集）——不整包 dump 状态，
    // 避免把设备 ID、cookie、绝对路径这类东西带进证据。
    const status = JSON.parse(await evaluate(page, `globalThis.__D19__.status()`))
    trace.push({
      phase: id,
      fault,
      file: name,
      importAck: { ok: imported.result?.ok ?? null, code: imported.result?.code ?? null, addedCount: imported.result?.added?.length ?? 0 },
      observed: {
        chip: immediate.chip,
        chipAfterQuiet: afterQuiet.chip,
        logicalUploads: immediate.uploads,
        logicalUploadsAfterQuiet: afterQuiet.uploads,
        logicalPrompts: immediate.prompts,
        proxyMatched: immediate.proxyUploads,
        proxyMatchedAfterQuiet: afterQuiet.proxyUploads,
        responseStatuses: immediate.responses,
        responseEnvelopeCodes: proxyResponseCodes(proxy.uploads({ phase: id })),
        injectedEnvelopeBodies: proxy.uploads({ phase: id }).map((item) => item.responseBody).filter((body) => typeof body === 'string' && body.startsWith('{')),
        networkFailures: immediate.failures,
        proxyOutcomes: immediate.proxyOutcomes
      },
      addonStatus: {
        state: status.state,
        capability: { status: status.capability?.status ?? null, code: status.capability?.code ?? null },
        upload: status.upload,
        transport: { importsInvoked: status.transport?.importsInvoked ?? null, importsFailed: status.transport?.importsFailed ?? null },
        uploadOwnership: status.uploadOwnership,
        reloadRequired: status.reloadRequired?.required ?? null
      }
    })
    return { imported, immediate, afterQuiet, detail, settled: settled.value }
  }

  async function removeChip(name) {
    const result = JSON.parse(await evaluate(page, `globalThis.__D19__.removeChip(${JSON.stringify(name)})`))
    await poll(async () => chipOf(await readChips(page), name), (chip) => chip === null, { timeoutMs: 6_000 })
    return result
  }

  /**
   * 读页面自己那次 prompt 请求的**响应信封**（CDP Network）。
   *
   * 这是公开面：请求由页面自己发出，信封形状是 remote 契约的
   * `{ result: { ok: false, error: { code, message, details: { reason } } } }`。
   * 用它把"用户看到的提示"与"这一次确定的拒绝"绑在一起（不依赖任何非公开句柄）。
   */
  async function readPromptEnvelope(response, { attempts = 12 } = {}) {
    for (let attempt = 0; attempt < attempts; attempt++) {
      try {
        const body = await page.send('Network.getResponseBody', { requestId: response.requestId })
        const text = body.base64Encoded === true ? Buffer.from(body.body, 'base64').toString('utf8') : body.body
        let parsed = null
        try {
          parsed = JSON.parse(text)
        } catch {
          parsed = null
        }
        const error = parsed?.result?.error ?? parsed?.error ?? null
        return {
          status: response.status,
          ok: parsed?.result?.ok ?? null,
          envelope: error === null || error === undefined ? null : { code: error.code ?? null, message: error.message ?? null, reason: error.details ? (error.details.reason ?? null) : null },
          rawSnippet: String(text).slice(0, 300)
        }
      } catch (error) {
        if (attempt === attempts - 1) return { status: response.status, ok: null, envelope: null, error: String(error?.message ?? error).slice(0, 200) }
        await sleep(120)
      }
    }
    return null
  }

  /**
   * 观测一次"点击发送"前后的提示生命周期（公开 DOM）+ 该次 prompt 的响应信封。
   *
   * 顺序很重要：`installAlertObserver` 必须在 `clickSend` **之前**；随后立刻开始按
   * ALERT_SAMPLE_INTERVAL_MS 采样，**不先等信封**（避免错过出现时刻），信封在同一循环里并行补齐。
   *
   * 停止条件（R22d）：至少覆盖 `minWindowMs`；若期间命中过，还要等最后一次命中之后
   * `ALERT_QUIET_TAIL_MS` 的安静尾部才停（这样"窗口在提示仍可见时关闭"不会成为隐藏条件）；
   * 硬上界 `maxWindowMs` 兜底。返回值里 `windowMs` = 实际观察到的时长。
   */
  async function observeSendWithAlerts({ id, promptsBefore, minWindowMs = ALERT_MIN_WINDOW_MS, maxWindowMs = ALERT_MAX_WINDOW_MS }) {
    const observer = JSON.parse(await evaluate(page, `globalThis.__D19__.installAlertObserver()`))
    const sendAtWall = Date.now()
    const clicked = JSON.parse(await evaluate(page, `globalThis.__D19__.clickSend()`))
    const samples = []
    let envelope = null
    let promptEntry = null
    let lastHitAt = null
    const startedAt = Date.now()
    for (;;) {
      const sample = JSON.parse(await evaluate(page, `globalThis.__D19__.sampleAlerts()`))
      samples.push({ t: sample.t, alerts: sample.alerts })
      if (sample.alerts.some((alert) => ALERT_ATTACHMENT_RE.test(alert.text ?? ''))) lastHitAt = sample.t
      if (envelope === null) {
        // requestId 集合必须**每轮重算**：prompt 是点击后由页面异步发出的，
        // 点击返回时 CDP 可能还没观测到这条请求。
        const requestIds = new Set(net.prompts.slice(promptsBefore).map((item) => item.requestId))
        const response = net.promptResponses.find((item) => requestIds.has(item.requestId)) ?? null
        if (response !== null) {
          envelope = await readPromptEnvelope(response)
          promptEntry = net.prompts.find((item) => item.requestId === response.requestId) ?? null
        }
      }
      const now = Date.now()
      const elapsed = now - startedAt
      const quietTailSatisfied = lastHitAt !== null && now - lastHitAt >= ALERT_QUIET_TAIL_MS
      if (elapsed >= minWindowMs && (lastHitAt === null || quietTailSatisfied)) break
      if (elapsed >= maxWindowMs) break
      await sleep(ALERT_SAMPLE_INTERVAL_MS)
    }
    const observedUntilMs = Date.now() - sendAtWall
    const events = JSON.parse(await evaluate(page, `globalThis.__D19__.alertEvents()`))
    return {
      id,
      installedAt: observer.installedAt,
      baseline: observer.baseline ?? [],
      sendAtWall,
      clicked,
      envelope,
      promptEntry,
      promptRequestsInWindow: net.prompts.slice(promptsBefore).length,
      samples,
      events,
      windowMs: observedUntilMs,
      minWindowMs,
      maxWindowMs,
      quietTailMs: ALERT_QUIET_TAIL_MS,
      sampleIntervalMs: ALERT_SAMPLE_INTERVAL_MS,
      observedUntilMs,
      hitInWindow: lastHitAt !== null,
      // 该窗内页面自己发出的请求（只留 pathname 与参数名，不留参数值/正文），
      // 用来给"第二次挂载"找时间上的相关性证据。
      pageRequestsInWindow: net.requests.filter((entry) => entry.at >= sendAtWall && entry.at <= sendAtWall + observedUntilMs).map((entry) => ({ atMs: entry.at - sendAtWall, method: entry.method, path: entry.path, queryNames: entry.queryNames }))
    }
  }

  // ================= 对照：注入关闭时同一手势必须成功 =================
  console.log('阶段 1：对照组（注入关闭）\n')
  const control = await gesture({ id: 'control-pass', fault: { kind: 'pass' }, name: 'd19-control.bin', expect: 'ready' })
  gate(
    'C01-control-pass-succeeds',
    '对照：同一上传手势在**注入关闭**时成功——composer 卡片进入 ready、恰好 1 个逻辑上传请求、HTTP 200',
    control.immediate.chip === 'ready' && control.immediate.uploads === 1 && control.immediate.responses.length === 1 && control.immediate.responses[0] === 200,
    control.detail
  )

  // 清掉 C01 的卡片：它的 receipt 在宿主重启后会失效，留在草稿里会让后面每一次 prompt
  // 都夹带一个失效凭证，从而掩盖"这一次拒绝到底由哪个附件引起"。清掉之后，
  // 阶段 1b 与阶段 7 的 prompt 载荷都只包含被测的那一个附件。
  await removeChip('d19-control.bin')

  // ================= 负向对照：发送成功时不得出现该提示 =================
  //
  // 这是 T02 的**负向对照**：同一设备、同一会话、同一页面、同一上传路径、同一 clickSend 手势、
  // 同一观察者与同一观察窗，唯一不同是 receipt 刚由宿主开出、宿主尚未重启 —— 宿主接受这次发送，
  // 因此同一判据必须**看不到**该附件提示。若这里也看到提示，T02 的"出现"就与本次拒绝无关。
  // 放在故障注入之前，是为了让"成功发送"这件事本身不被前面的拒绝污染。
  console.log('\n阶段 1b：负向对照（发送成功时不得出现该提示）\n')
  const ctlName = 'd19-alert-control.bin'
  proxy.setPhase('alert-control-upload')
  proxy.arm({ kind: 'pass' })
  const ctlImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(ctlName)})`))
  const ctlReady = await poll(async () => chipOf(await readChips(page), ctlName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  proxy.disarm()
  const ctlPromptsBefore = net.prompts.length
  const ctlSend = await observeSendWithAlerts({ id: 'alert-control-send', promptsBefore: ctlPromptsBefore })
  const ctlChipsAfter = await readChips(page)
  const ctlJudged = judgeSend(ctlSend)
  const ctlSurface = alertSurfaceEvidence(ctlSend, ctlJudged)
  const ctlBaselineClean = ctlSurface.baseline.every((alert) => !ALERT_ATTACHMENT_RE.test(alert.text ?? ''))
  const ctlChipConsumed = chipOf(ctlChipsAfter, ctlName) === null
  const ctlAccepted =
    ctlSend.clicked.ok === true && ctlSend.envelope !== null && ctlSend.envelope.ok === true && ctlSend.envelope.envelope === null
  const ctlReceipt = uploadReceipt(proxy.uploads({ phase: 'alert-control-upload' }))
  const ctlPayload = summarizePromptPayload(ctlSend.promptEntry, { knownReceipts: { freshUpload: ctlReceipt }, currentSession: SESSION })
  observedPromptSurfaces.negativeControl = {
    gesture: '同一会话、同一 clickSend 手势；文件刚上传（receipt 有效）、注入关闭、宿主未重启',
    imported: { ok: ctlImport.result?.ok ?? null, code: ctlImport.result?.code ?? null, addedCount: ctlImport.result?.added?.length ?? 0 },
    readyChip: ctlReady.value?.state ?? null,
    uploadReceiptPreview: ctlReceipt === null ? null : `${ctlReceipt.slice(0, 6)}…(${ctlReceipt.length})`,
    envelope: { status: ctlSend.envelope?.status ?? null, ok: ctlSend.envelope?.ok ?? null, code: ctlSend.envelope?.envelope?.code ?? null },
    promptRequestsInWindow: ctlSend.promptRequestsInWindow,
    promptPayload: ctlPayload,
    chipAfterSend: chipOf(ctlChipsAfter, ctlName)?.state ?? null,
    chipConsumedBySend: ctlChipConsumed,
    // 负向对照也走**同一判定器**：期望"该提示不可见"，即 mountCount===0。
    promptSurface: { criteriaVersion: ctlJudged.criteriaVersion, mountCount: ctlJudged.observations.mountCount, overall: ctlJudged.overall, subAssertions: ctlJudged.subAssertions, observations: ctlJudged.observations, window: ctlSurface.window }
  }
  trace.push({
    phase: 'alert-negative-control',
    imported: ctlImport,
    readyChip: ctlReady.value?.state ?? null,
    uploadReceiptPreview: observedPromptSurfaces.negativeControl.uploadReceiptPreview,
    clicked: ctlSend.clicked,
    envelope: observedPromptSurfaces.negativeControl.envelope,
    promptRequestsInWindow: ctlSend.promptRequestsInWindow,
    promptPayload: ctlPayload,
    chipAfterSend: observedPromptSurfaces.negativeControl.chipAfterSend,
    chipConsumedBySend: ctlChipConsumed,
    promptSurface: {
      criteriaVersion: ctlJudged.criteriaVersion,
      window: ctlSurface.window,
      mountCount: ctlJudged.observations.mountCount,
      samplesWithAttachmentText: ctlSend.samples.filter((sample) => sample.alerts.some((alert) => ALERT_ATTACHMENT_RE.test(alert.text ?? ''))).length,
      samples: ctlSend.samples.map((sample) => ({ tMs: sample.t - ctlSend.sendAtWall, texts: sample.alerts.map((alert) => alert.text) }))
    }
  })
  negative(
    'N7-successful-send-shows-no-attachment-prompt',
    `反例：同一设备、同一会话、同一发送手势在 receipt 有效（刚上传、注入关闭）时被宿主确定接受（信封 ok=true 且无 error），同一观察者 / 同一 ${Math.round(ctlSend.observedUntilMs / 1000)}s 窗口内**不得**出现该附件提示——否则 T02 的"出现"就不是由这次失效 receipt 引起的`,
    ctlAccepted && ctlBaselineClean && ctlJudged.observations.mountCount === 0,
    `信封 ok=${ctlSend.envelope?.ok ?? 'null'} 码=${ctlSend.envelope?.envelope?.code ?? 'null'} 发送后 chip=${chipOf(ctlChipsAfter, ctlName)?.state ?? 'null'}（草稿被消费=${ctlChipConsumed}）观察起点无残留=${ctlBaselineClean} prompt 内 receipt 命中=${JSON.stringify(ctlPayload.receiptMatches ?? null)} 窗口内挂载次数=${ctlJudged.observations.mountCount} 观察窗=${ctlSend.observedUntilMs}ms（至少 ${ctlSend.minWindowMs}ms）`
  )
  await removeChip(ctlName)
  await sleep(500)

  console.log('\n阶段 2：故障注入（每个故障一条"可见 + 不重发 + 不误投"三连判据）\n')
  const faults = [
    { id: 'fault-stream-break', label: 'stream-break', fault: { kind: 'stream-break', afterBytes: 512 } },
    { id: 'fault-stall', label: 'stall', fault: { kind: 'stall' } },
    { id: 'fault-http-413', label: 'http-413', fault: { kind: 'http', status: 413 } },
    { id: 'fault-business-failure', label: 'business-failure', fault: { kind: 'business-failure', code: 'ATTACHMENT_WRITE_FAILED' } },
    { id: 'fault-http-403', label: 'http-403', fault: { kind: 'http', status: 403 } },
    { id: 'fault-http-401', label: 'http-401', fault: { kind: 'http', status: 401 } }
  ]

  observedFaults = {}
  for (const entry of faults) {
    const name = `d19-${entry.label}.bin`
    const run = await gesture({ id: entry.id, fault: entry.fault, name, quietMs: entry.label === 'stall' ? 800 : QUIET_MS, settleTimeoutMs: entry.label === 'stall' ? 2500 : 20_000 })
    observedFaults[entry.label] = {
      fault: entry.fault,
      chip: run.immediate.chip,
      statuses: run.immediate.responses,
      envelopeCodes: proxyResponseCodes(proxy.uploads({ phase: entry.id })),
      networkFailures: run.immediate.failures,
      proxyOutcomes: run.immediate.proxyOutcomes
    }

    // stall 不是"错误"而是"在途"：判据要求它确定可辨（uploading），随后 release 才落定。
    if (entry.label === 'stall') {
      gate(
        'F-stall-inflight-visible',
        '故障 stall/timeout：请求被接受但永不应答时，卡片停在确定的 uploading 状态（既不假装成功也不静默消失），请求确实挂着，且挂起期间不自动重发（逻辑与代理计数都保持 1）',
        run.immediate.chip === 'uploading' &&
          run.afterQuiet.chip === 'uploading' &&
          proxy.heldCount() >= 1 &&
          run.immediate.uploads === 1 &&
          run.afterQuiet.uploads === 1 &&
          run.afterQuiet.proxyUploads === 1,
        `${run.detail} 代理挂起=${proxy.heldCount()} 安静后 chip=${run.afterQuiet.chip}`
      )
      const stallOther = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile('d19-misdelivery-stall.bin', ${JSON.stringify(OTHER_SESSION)})`))
      gate(
        'F-stall-no-misdelivery',
        '故障 stall/timeout：挂起期间该文件既不进入 ready 也不落到别的会话（跨会话导入被确定拒绝 context-changed）',
        chipOf(await readChips(page), name)?.state === 'uploading' &&
          stallOther.result?.ok === false &&
          stallOther.result?.code === 'context-changed',
        `chip=uploading 跨会话导入=${stallOther.result?.ok === true ? 'ok(竟然通过)' : stallOther.result?.code}`
      )
      const released = proxy.release()
      const recovered = await poll(async () => chipOf(await readChips(page), name), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
      gate(
        'F-stall-release-recovers',
        '故障 stall/timeout：解除挂起后同一个在途请求被真实服务处理并落到 ready（证明"停在 uploading"是通道挂起而不是功能坏了）',
        released === 1 && recovered.value?.state === 'ready',
        `release=${released} chip=${recovered.value?.state ?? 'null'}`
      )
      await removeChip(name)
      negative(
        'N-stall-not-error',
        '反例：挂起期间**不能**被误判成 error（否则"可见且确定"就退化成"一律失败"）',
        run.immediate.chip === 'uploading',
        `chip=${run.immediate.chip}`
      )
      continue
    }

    gate(
      `F-${entry.label}-error-visible`,
      `故障 ${entry.label}：错误在真实 composer 卡片上确定可见（state=error + 重试入口），并记录该手势的观测状态/失败码`,
      run.immediate.chip === 'error' && (run.settled?.retryAffordance === true) && run.immediate.uploads === 1,
      run.detail
    )
    gate(
      `F-${entry.label}-no-autoresend`,
      `故障 ${entry.label}：不自动重发——该手势的逻辑上传请求在安静窗口（${QUIET_MS}ms）后仍是 1，prompt 请求为 0（代理侧传输计数单独记录）`,
      run.afterQuiet.uploads === 1 && run.afterQuiet.prompts === 0 && run.afterQuiet.chip === 'error',
      `${run.detail}｜安静后 逻辑=${run.afterQuiet.uploads} 代理=${run.afterQuiet.proxyUploads} prompt=${run.afterQuiet.prompts} chip=${run.afterQuiet.chip}`
    )
    const other = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile('d19-misdelivery.bin', ${JSON.stringify(OTHER_SESSION)})`))
    const chipsNow = await readChips(page)
    gate(
      `F-${entry.label}-no-misdelivery`,
      `故障 ${entry.label}：不误投——失败文件永不进入 ready（卡片停在 error），且指向**另一个会话**的导入被确定拒绝（context-changed）`,
      chipOf(chipsNow, name)?.state === 'error' && other.result?.ok === false && other.result?.code === 'context-changed',
      `chip=${chipOf(chipsNow, name)?.state ?? 'null'} 跨会话导入=${other.result?.ok === true ? 'ok(竟然通过)' : other.result?.code}`
    )
    await removeChip(name)
  }

  // 负向探针：C01 的对照在手势层真的能区分两种结果。
  negative(
    'N1-error-assertion-distinguishes',
    '反例：把"卡片必须是 error"套到对照组（无故障）上必须为假——证明 F-*-error-visible 不是恒真判据',
    control.immediate.chip !== 'error',
    `无故障时 chip=${control.immediate.chip}`
  )

  // ================= 逐故障对照：同一批请求在注入关闭时必须成功 =================
  console.log('\n阶段 2b：逐故障对照（同一 URL / 同一文件名，注入关闭）\n')
  const controlRuns = []
  for (const entry of faults) {
    const name = `d19-${entry.label}.bin`
    proxy.setPhase(`control-${entry.label}`)
    proxy.arm({ kind: 'pass' })
    const uploadsBefore = net.uploads.length
    const promptsBefore = net.prompts.length
    const imported = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(name)})`))
    const settled = await poll(async () => chipOf(await readChips(page), name), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
    await sleep(1200)
    const run = {
      fault: entry.label,
      chip: settled.value?.state ?? null,
      uploads: net.uploads.length - uploadsBefore,
      uploadsAfterQuiet: net.uploads.length - uploadsBefore,
      prompts: net.prompts.length - promptsBefore,
      proxy: proxy.uploads({ phase: `control-${entry.label}` }).map((item) => item.status ?? null),
      ack: imported.result?.ok ?? null
    }
    proxy.disarm()
    controlRuns.push(run)
    trace.push({ phase: `control-${entry.label}`, fault: { kind: 'pass', note: '注入关闭（对照）' }, file: name, importAck: { ok: run.ack, addedCount: null }, observed: run })
    await removeChip(name)
  }
  gate(
    'C02-per-fault-control-injection-disabled',
    '逐故障对照：把上面 6 个故障对应的同一批请求在**注入关闭**时重跑一遍，全部 ready + HTTP 200 + 各 1 个逻辑上传——证明每个 F-*-* 判据确实由注入引起，而不是恒真',
    controlRuns.length === faults.length && controlRuns.every((run) => run.chip === 'ready' && run.uploads === 1 && run.prompts === 0 && run.proxy.length === 1 && run.proxy[0] === 200),
    controlRuns.map((run) => `${run.fault}:${run.chip}/${JSON.stringify(run.proxy)}/上传${run.uploads}`).join(' ')
  )
  negative(
    'N6-control-batch-not-always-error',
    '反例：逐故障对照里没有任何一个落在 error（若注入关闭仍失败，说明故障判据分不清注入与产品故障）',
    controlRuns.every((run) => run.chip !== 'error'),
    controlRuns.map((run) => `${run.fault}:${run.chip}`).join(' ')
  )

  // ================= 部分批次：成功项不被重放 =================
  console.log('\n阶段 3：部分批次（成功项不被重放）\n')
  const batchNameA = 'd19-batch-ok.bin'
  const batchNameB = 'd19-batch-fail.bin'
  proxy.setPhase('partial-batch')
  // 放过第 1 个匹配请求，只对第 2 个注入 413：同批一成一败。
  proxy.arm({ kind: 'http', status: 413, skip: 1, times: 1 })
  const uploadsBeforeBatch = net.uploads.length
  const batchImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importBatch([${JSON.stringify(batchNameA)}, ${JSON.stringify(batchNameB)}])`))
  const batchSettled = await poll(
    async () => await readChips(page),
    (chips) => [chipOf(chips, batchNameA), chipOf(chips, batchNameB)].every((chip) => chip !== null && chip.state !== 'uploading'),
    { timeoutMs: 30_000 }
  )
  const batchImmediate = {
    uploadsA: net.uploads.slice(uploadsBeforeBatch).filter((entry) => entry.name === batchNameA).length,
    uploadsB: net.uploads.slice(uploadsBeforeBatch).filter((entry) => entry.name === batchNameB).length,
    proxyA: proxy.uploads({ phase: 'partial-batch', name: batchNameA }).length,
    proxyB: proxy.uploads({ phase: 'partial-batch', name: batchNameB }).length
  }
  await sleep(QUIET_MS)
  const batchAfter = {
    uploadsA: net.uploads.slice(uploadsBeforeBatch).filter((entry) => entry.name === batchNameA).length,
    uploadsB: net.uploads.slice(uploadsBeforeBatch).filter((entry) => entry.name === batchNameB).length,
    proxyA: proxy.uploads({ phase: 'partial-batch', name: batchNameA }).length,
    proxyB: proxy.uploads({ phase: 'partial-batch', name: batchNameB }).length
  }
  const batchChips = batchSettled.value
  proxy.disarm()
  trace.push({ phase: 'partial-batch', fault: { kind: 'http', status: 413, skip: 1, times: 1 }, imported: batchImport, immediate: batchImmediate, afterQuiet: batchAfter, chips: batchChips })
  gate(
    'P01-partial-batch-success-not-replayed',
    '部分失败：一批两文件（第 1 个放行、第 2 个注入 413）后，已成功的文件既不被重传也不被重复导入——按文件名精确计数在安静窗口前后都是 1',
    chipOf(batchChips, batchNameA)?.state === 'ready' &&
      chipOf(batchChips, batchNameB)?.state === 'error' &&
      batchAfter.uploadsA === 1 &&
      batchAfter.proxyA === 1 &&
      batchAfter.uploadsB === 1 &&
      batchAfter.proxyB === 1 &&
      batchImmediate.uploadsA === batchAfter.uploadsA &&
      batchImmediate.uploadsB === batchAfter.uploadsB,
    `A=${chipOf(batchChips, batchNameA)?.state}/${batchAfter.uploadsA} 逻辑 ${batchAfter.proxyA} 代理｜B=${chipOf(batchChips, batchNameB)?.state}/${batchAfter.uploadsB} 逻辑 ${batchAfter.proxyB} 代理｜批次导入 added=${JSON.stringify(batchImport.result?.added ?? null)}`
  )
  negative(
    'N2-partial-batch-failure-is-real',
    '反例：同批第 2 个文件确实失败（不是"两个都成功"被误报成部分失败）',
    chipOf(batchChips, batchNameB)?.state === 'error' && chipOf(batchChips, batchNameA)?.state === 'ready',
    `A=${chipOf(batchChips, batchNameA)?.state} B=${chipOf(batchChips, batchNameB)?.state}`
  )
  await removeChip(batchNameB)
  await removeChip(batchNameA)
  await sleep(800)

  // ================= 重试复用原草稿 =================
  console.log('\n阶段 4：重试复用原草稿条目\n')
  const retryName = 'd19-retry.bin'
  proxy.setPhase('retry-first')
  proxy.arm({ kind: 'http', status: 413 })
  const retryImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(retryName)})`))
  const originalDraftId = retryImport.result?.added?.[0] ?? null
  await poll(async () => chipOf(await readChips(page), retryName), (chip) => chip !== null && chip.state === 'error', { timeoutMs: 20_000 })
  proxy.disarm()
  proxy.setPhase('retry-second')
  const chipsBeforeRetry = await readChips(page)
  const uploadsBeforeRetry = net.uploads.length
  const retryClicked = JSON.parse(await evaluate(page, `globalThis.__D19__.retryChip(${JSON.stringify(retryName)})`))
  const retrySettled = await poll(async () => chipOf(await readChips(page), retryName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  const retryUploads = net.uploads.slice(uploadsBeforeRetry).filter((entry) => entry.name === retryName).length
  await sleep(QUIET_MS)
  const retryUploadsAfterQuiet = net.uploads.slice(uploadsBeforeRetry).filter((entry) => entry.name === retryName).length
  const chipsAfterRetry = await readChips(page)
  // 见证导入：previous 是**导入前的原生草稿 ID 列表**，用它证明重试没有换掉草稿条目。
  const witness = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile('d19-witness.bin')`))
  const previousAfterRetry = witness.result?.previous ?? []
  proxy.disarm()
  trace.push({
    phase: 'retry-reuses-draft',
    fault: { kind: 'http', status: 413, note: '第一次注入 413，第二次解除后走应用内重试入口' },
    file: retryName,
    importAck: { ok: retryImport.result?.ok ?? null, code: retryImport.result?.code ?? null, addedCount: retryImport.result?.added?.length ?? 0 },
    originalDraftId: typeof originalDraftId === 'string' ? `${originalDraftId.slice(0, 8)}…` : null,
    retryClicked: retryClicked.ok === true,
    retryChip: retrySettled.value?.state ?? null,
    draftEntryCountBefore: chipsBeforeRetry.length,
    draftEntryCountAfter: chipsAfterRetry.length,
    originalIdStillPresent: previousAfterRetry.includes(originalDraftId),
    retryLogicalUploads: retryUploads,
    retryLogicalUploadsAfterQuiet: retryUploadsAfterQuiet
  })
  gate(
    'R01-retry-uses-original-draft',
    '重试走应用自身的重试入口并复用**同一个**草稿条目：重试后原生草稿 ID 列表里仍是导入时那一个（条目未被替换），composer 条目数前后不变（没有新增第二条），且只发出 1 个新上传请求、安静窗口内不增长',
    retryClicked.ok === true &&
      retrySettled.value?.state === 'ready' &&
      typeof originalDraftId === 'string' &&
      previousAfterRetry.includes(originalDraftId) &&
      chipsAfterRetry.length === chipsBeforeRetry.length &&
      retryUploads === 1 &&
      retryUploadsAfterQuiet === 1,
    `草稿ID=${String(originalDraftId).slice(0, 8)}… 条目数=${chipsBeforeRetry.length}→${chipsAfterRetry.length} 原ID仍在=${previousAfterRetry.includes(originalDraftId)} 重试上传=${retryUploads}→${retryUploadsAfterQuiet} chip=${retrySettled.value?.state ?? 'null'}`
  )
  negative(
    'N3-retry-not-a-second-draft',
    '反例：重试**没有**新增第二个草稿条目（composer 条目数与重试前相同，否则说明重试是"重新导入"而非"复用原草稿"）',
    chipsAfterRetry.length === chipsBeforeRetry.length,
    `重试前 ${chipsBeforeRetry.length} → 重试后 ${chipsAfterRetry.length}`
  )
  await removeChip('d19-witness.bin')
  await removeChip(retryName)

  // ================= 客户端取消 =================
  console.log('\n阶段 5：客户端取消在途上传\n')
  const cancelName = 'd19-cancel.bin'
  proxy.setPhase('client-cancel')
  proxy.arm({ kind: 'stall' })
  const cancelImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(cancelName)})`))
  const inFlight = await poll(async () => proxy.heldCount(), (value) => value >= 1, { timeoutMs: 10_000 })
  const uploadsBeforeCancel = net.uploads.length
  const promptsBeforeCancel = net.prompts.length
  const removed = await removeChip(cancelName)
  const aborted = await poll(async () => proxy.uploads({ phase: 'client-cancel' })[0]?.outcome ?? null, (value) => value === 'client-aborted', { timeoutMs: 10_000 })
  const releasedAfterCancel = proxy.release()
  await sleep(1500)
  const cancelAfter = {
    uploads: net.uploads.length - uploadsBeforeCancel,
    prompts: net.prompts.length - promptsBeforeCancel,
    held: proxy.heldCount(),
    outcome: proxy.uploads({ phase: 'client-cancel' })[0]?.outcome ?? null,
    chips: await readChips(page)
  }
  proxy.disarm()
  trace.push({ phase: 'client-cancel', imported: cancelImport, inFlight: inFlight.value, removed, releasedAfterCancel, after: cancelAfter })
  gate(
    'X01-client-cancel-terminates-inflight',
    '客户端取消：在途上传被真正终止（代理观测到 client-aborted、挂起表清空、release 无可放行），取消后该手势不再产生任何上传或 prompt 请求',
    inFlight.ok === true &&
      removed.ok === true &&
      aborted.ok === true &&
      releasedAfterCancel === 0 &&
      cancelAfter.uploads === 0 &&
      cancelAfter.prompts === 0 &&
      chipOf(cancelAfter.chips, cancelName) === null,
    `挂起=${inFlight.value} 取消=${JSON.stringify(removed)} 终止=${aborted.value} release=${releasedAfterCancel} 取消后上传=${cancelAfter.uploads} prompt=${cancelAfter.prompts}`
  )

  // ================= 宿主重启：连接被丢弃 + 恢复 =================
  console.log('\n阶段 6：宿主重启（连接丢弃 → 恢复）\n')
  const restartName = 'd19-host-restart.bin'
  proxy.setPhase('host-restart-down')
  proxy.arm({ kind: 'drop' })
  const dropped = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(restartName)})`))
  const dropSettled = await poll(async () => chipOf(await readChips(page), restartName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  proxy.disarm()
  const dropUploads = net.uploads.filter((entry) => entry.name === restartName).length
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await waitForReady(service.child, serviceLog, readyTimeoutMs)
  const backOnline = await poll(
    async () => (await client.fetch(`${lanBase}/remote/api/session/list`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
      body: JSON.stringify({ type: 'client-request', rpcId: `d19-restart-${Date.now()}`, method: 'session/list', payload: { args: { _request: {} } } })
    })).status,
    (status) => status === 200,
    { timeoutMs: 30_000, intervalMs: 1000 }
  )
  const recoveryName = 'd19-host-recovered.bin'
  proxy.setPhase('host-restart-recovered')
  proxy.arm({ kind: 'pass' })
  const recoveredImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(recoveryName)})`))
  const recoveredChip = await poll(async () => chipOf(await readChips(page), recoveryName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  const replayedAfterRestart = net.uploads.filter((entry) => entry.name === restartName).length
  proxy.disarm()
  trace.push({
    phase: 'host-restart',
    dropped,
    dropChip: dropSettled.value?.state ?? null,
    dropUploads,
    recoveredImport,
    recoveredChip: recoveredChip.value?.state ?? null,
    replayedAfterRestart
  })
  gate(
    'H01-host-restart-drops-connection',
    '宿主重启场景 A：上传进行中宿主连接被丢弃 ⇒ 卡片确定的 error（连接层失败被如实上报，不是静默成功）',
    dropSettled.value?.state === 'error' && dropped.result?.ok === true,
    `chip=${dropSettled.value?.state ?? 'null'} 逻辑上传=${dropUploads}`
  )
  gate(
    'H02-host-restart-recovers',
    '宿主重启场景 B：宿主重启回来并解除注入后，新的上传手势照常成功（恢复），且之前失败的文件没有被追加重传',
    backOnline.ok === true && recoveredChip.value?.state === 'ready' && replayedAfterRestart === dropUploads,
    `服务恢复=${backOnline.ok} 新文件 chip=${recoveredChip.value?.state ?? 'null'} 旧文件上传计数=${dropUploads}→${replayedAfterRestart}`
  )
  await removeChip(restartName)
  await removeChip(recoveryName)

  // ================= 失效 / 过期 ready receipt =================
  console.log('\n阶段 7：失效 receipt（宿主重启后 receipt 不再有效）\n')
  const staleName = 'd19-stale-receipt.bin'
  proxy.setPhase('stale-before')
  proxy.arm({ kind: 'pass' })
  const staleImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(staleName)})`))
  const staleReady = await poll(async () => chipOf(await readChips(page), staleName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  proxy.disarm()
  const staleUploadsBefore = net.uploads.filter((entry) => entry.name === staleName).length
  // 重启宿主：receipt 表在内存里，重启后该 receipt 失效。
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await waitForReady(service.child, serviceLog, readyTimeoutMs)
  await poll(
    async () => (await client.fetch(`${lanBase}/remote/api/session/list`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
      body: JSON.stringify({ type: 'client-request', rpcId: `d19-stale-${Date.now()}`, method: 'session/list', payload: { args: { _request: {} } } })
    })).status,
    (status) => status === 200,
    { timeoutMs: 30_000, intervalMs: 1000 }
  )
  proxy.setPhase('stale-send')
  const promptsBeforeStale = net.prompts.length
  const staleChipKept = chipOf(await readChips(page), staleName)?.state ?? null
  // 观测从**点击发送之前**开始（观察者先就位），窗口内按 150ms 采样，并抓住该次 prompt 的公开信封。
  const staleSend = await observeSendWithAlerts({ id: 'stale-send', promptsBefore: promptsBeforeStale })
  const staleUploadsAfter = net.uploads.filter((entry) => entry.name === staleName).length
  const stalePromptsAfter = net.prompts.length - promptsBeforeStale
  const chipsAfterStale = await readChips(page)
  proxy.disarm()

  const staleEnvelope = staleSend.envelope?.envelope ?? null
  const staleJudged = judgeSend(staleSend, { envelope: { code: staleEnvelope?.code ?? null, reason: staleEnvelope?.reason ?? null } })
  const staleSurface = alertSurfaceEvidence(staleSend, staleJudged)
  // 代理侧信封（同一条 prompt 请求；故障代理就坐在页面 origin 上）作为交叉验证，不作主判据。
  const promptEntries = proxy.log.filter((entry) => entry.path === '/remote/api/session/prompt')
  const lastPrompt = promptEntries[promptEntries.length - 1] ?? null
  let proxyEnvelope = null
  try {
    const parsed = lastPrompt?.responseBody === undefined ? null : JSON.parse(lastPrompt.responseBody)
    proxyEnvelope = parsed?.result?.error ?? parsed?.error ?? null
  } catch {
    proxyEnvelope = null
  }
  // 这次 prompt 到底带了哪张凭证：应为**宿主重启前**那次上传开出的那张（'stale-before' 阶段的代理响应）。
  const staleUploadReceipt = uploadReceipt(proxy.uploads({ phase: 'stale-before' }))
  const stalePromptPayload = summarizePromptPayload(staleSend.promptEntry, { knownReceipts: { staleUpload: staleUploadReceipt }, currentSession: SESSION })
  const stalePayloadCarriesStaleReceipt = (stalePromptPayload.receiptMatches ?? []).some((names) => names.includes('staleUpload'))
  // 第二次挂载的解释线索：把"每次挂载的起止"与"窗口内页面自己发出的请求"并排放在证据里。
  const secondMountEvidence = {
    mountTimeline: staleJudged.instances,
    replacements: staleJudged.observations.replacements,
    concurrentMounts: staleJudged.observations.concurrentMounts,
    pageRequestsInWindow: staleSend.pageRequestsInWindow,
    note: '锁定包 InputBar 的 showToast 无去重：useEffect 依赖 [promptError, showToast, t, imageLimits] 中任一换身份都会再次呈现同一个未解决的 promptError，且每次呈现都是新 key 的新挂载（dsh-client-ui-conversation/lib/client.js:15824-15846、:16054-16059）。'
  }
  const stalePromptSurface = {
    ...staleSurface,
    baselineClean: staleSurface.baseline.every((alert) => !ALERT_ATTACHMENT_RE.test(alert.text ?? '')),
    envelope: { status: staleSend.envelope?.status ?? null, ok: staleSend.envelope?.ok ?? null, code: staleEnvelope?.code ?? null, message: staleEnvelope?.message ?? null, reason: staleEnvelope?.reason ?? null },
    proxyEnvelope: { code: proxyEnvelope?.code ?? null, reason: proxyEnvelope?.details ? (proxyEnvelope.details.reason ?? null) : null },
    uploadReceiptPreview: staleUploadReceipt === null ? null : `${staleUploadReceipt.slice(0, 6)}…(${staleUploadReceipt.length})`,
    promptPayload: stalePromptPayload,
    secondMountEvidence
  }
  observedPromptSurfaces.staleReceipt = stalePromptSurface
  trace.push({
    phase: 'stale-receipt',
    staleImport,
    readyChip: staleReady.value?.state ?? null,
    staleUploadsBefore,
    staleChipKept,
    sendClicked: staleSend.clicked,
    staleErrorCode: staleEnvelope?.code ?? null,
    staleErrorReason: staleEnvelope?.reason ?? null,
    staleUploadsAfter,
    stalePromptsAfter,
    chipAfterSend: chipOf(chipsAfterStale, staleName)?.state ?? null,
    promptSurface: {
      criteriaVersion: staleSurface.criteriaVersion,
      selector: staleSurface.selector,
      expectedText: staleSurface.expectedText,
      window: staleSurface.window,
      overall: staleSurface.overall,
      subAssertions: staleSurface.subAssertions,
      instances: staleSurface.instances,
      observations: staleSurface.observations,
      mountTimeline: secondMountEvidence.mountTimeline,
      pageRequestsInWindow: secondMountEvidence.pageRequestsInWindow,
      envelope: stalePromptSurface.envelope,
      proxyEnvelope: stalePromptSurface.proxyEnvelope,
      uploadReceiptPreview: stalePromptSurface.uploadReceiptPreview,
      promptPayload: stalePromptSurface.promptPayload,
      samples: staleSurface.samples,
      observerEvents: staleSurface.events
    }
  })
  gate(
    'T01-stale-receipt-refused-without-replay',
    '失效 receipt：宿主重启后再发送，该 prompt 带的是**重启前**那次上传开出的 receipt（CDP 读请求体），服务端以确定错误码拒绝（读页面自己那次 prompt 请求的 CDP 响应信封：session/attachment-invalid / FILE_NOT_STAGED），既没有静默重放上传、也没有自动重发 prompt（各恰好 0 次新增）',
    staleReady.value?.state === 'ready' &&
      staleSend.clicked.ok === true &&
      stalePayloadCarriesStaleReceipt &&
      staleEnvelope?.code === 'session/attachment-invalid' &&
      staleEnvelope?.reason === 'FILE_NOT_STAGED' &&
      staleUploadsAfter === staleUploadsBefore &&
      stalePromptsAfter === 1,
    `发送前 chip=${staleChipKept} 信封码=${staleEnvelope?.code ?? 'null'}/${staleEnvelope?.reason ?? 'null'}（代理侧=${proxyEnvelope?.code ?? 'null'}）prompt 内 receipt=${JSON.stringify(stalePromptPayload.receiptPreviews ?? null)} 命中重启前凭证=${stalePayloadCarriesStaleReceipt} 上传重放=${staleUploadsBefore}→${staleUploadsAfter} prompt=${stalePromptsAfter}`
  )
  // ---- T02：整体**直接**由判定器产出的具名子断言记录算出；detail 由同一批记录渲染。----
  //
  // 三条被显式分开报告的量（R22d 要求）：
  //   (a) 单个 Toast **实例**的生命周期 —— `T02-05` 逐实例断言（事件流里按节点身份切分）；
  //   (b) 多次挂载形成的**总可见区间** —— `T02-07` 断言 + `observations.mergedSpans` 打印；
  //   (c) 同一次发送是否产生**重复请求** —— `T02-08` 断言 prompt 请求数恰好 1。
  // 第二次挂载本身（上游允许的重放）不进整体，但必须被看见并打印：见 detail 末尾的观测事实。
  gate(
    'T02-stale-receipt-native-prompt-visible',
    `失效 receipt 后原生提示**可见且是瞬态的**：观察者在发送前就位、按 ${ALERT_SAMPLE_INTERVAL_MS}ms 采样（至少 ${ALERT_MIN_WINDOW_MS}ms，命中后再等 ${ALERT_QUIET_TAIL_MS}ms 安静尾部，硬上界 ${ALERT_MAX_WINDOW_MS}ms），抓到 portal 到 document.body 的 [role="alert"]（class 含 _toast_*，不在 composer 卡片内），文案逐字等于 file.notStaged 的中文映射；单个实例存活 ∈[${ALERT_INSTANCE_MIN_MS},${ALERT_INSTANCE_MAX_MS}]ms、总可见区间 ≤${ALERT_TOTAL_MAX_MS}ms（均在窗口内消失，证明是完整生命周期而不是静态字符串）；该提示与本次 session/attachment-invalid / FILE_NOT_STAGED 信封绑定。判据版本 ${staleJudged.criteriaVersion}`,
    computeAlertOverall(staleJudged.subAssertions),
    renderSubAssertionDetail(staleJudged.subAssertions, staleJudged.observations)
  )
  await removeChip(staleName)

  // ================= 拒绝之后再走一次同手势（第二负向对照 N9 + 诊断记录） =================
  //
  // 先把"陈旧 receipt 被拒"这件事做完，再清空草稿、重传一个新文件、走同一 clickSend。
  // 这一步同时做两件事：
  //   1. N9（负向对照）：这次被宿主接受（信封 ok=true）且**不**出现该附件提示 —— 证明 T02 的
  //      提示绑定的是"这一次附件拒绝"，而不是"宿主重启过"或"页面出过错"；
  //   2. 诊断记录：草稿见证（残留条目会夹带失效 receipt）、prompt 载荷里到底带了哪张 receipt。
  console.log('\n阶段 7b：拒绝后的同手势（N9 负向对照 + 诊断记录）\n')
  const postRejName = 'd19-alert-control.bin'
  const chipsAtControlStart = await readChips(page)
  for (const chip of chipsAtControlStart) await removeChip(chip.name)
  // 见证导入：`previous` 是**导入前的原生草稿 ID 列表**，用它确认草稿里没有残留条目
  // （残留条目会把失效 receipt 一起带进 prompt，那样"对照"自己也会被拒）。
  proxy.setPhase('alert-witness')
  proxy.arm({ kind: 'pass' })
  const alertWitness = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile('d19-alert-witness.bin')`))
  const draftBeforeControl = Array.isArray(alertWitness.result?.previous) ? alertWitness.result.previous : []
  await removeChip('d19-alert-witness.bin')
  proxy.disarm()
  proxy.setPhase('alert-negative-control')
  proxy.arm({ kind: 'pass' })
  const postRejImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(postRejName)})`))
  const postRejReady = await poll(async () => chipOf(await readChips(page), postRejName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  proxy.disarm()
  const postRejPromptsBefore = net.prompts.length
  const postRejSend = await observeSendWithAlerts({ id: 'alert-negative-control', promptsBefore: postRejPromptsBefore })
  const postRejChipsAfter = await readChips(page)
  const postRejChipConsumed = chipOf(postRejChipsAfter, postRejName) === null
  const postRejJudged = judgeSend(postRejSend)
  const postRejSurface = alertSurfaceEvidence(postRejSend, postRejJudged)
  const postRejBaselineClean = postRejSurface.baseline.every((alert) => !ALERT_ATTACHMENT_RE.test(alert.text ?? ''))
  const postRejReceipt = uploadReceipt(proxy.uploads({ phase: 'alert-negative-control' }))
  const postRejectionDiagnostic = {
    gesture: '陈旧 receipt 被拒 → 清空草稿 → 重新上传新文件（注入关闭）→ 同一 clickSend',
    draftBeforeControl: { count: draftBeforeControl.length, staleIdStillPresent: draftBeforeControl.includes(staleImport.result?.added?.[0] ?? null) },
    imported: { ok: postRejImport.result?.ok ?? null, code: postRejImport.result?.code ?? null, addedCount: postRejImport.result?.added?.length ?? 0 },
    readyChip: postRejReady.value?.state ?? null,
    uploadReceiptPreview: postRejReceipt === null ? null : `${postRejReceipt.slice(0, 6)}…(${postRejReceipt.length})`,
    envelope: { status: postRejSend.envelope?.status ?? null, ok: postRejSend.envelope?.ok ?? null, code: postRejSend.envelope?.envelope?.code ?? null, reason: postRejSend.envelope?.envelope?.reason ?? null },
    promptRequestsInWindow: postRejSend.promptRequestsInWindow,
    promptPayload: summarizePromptPayload(postRejSend.promptEntry, {
      knownReceipts: { freshUpload: postRejReceipt, staleUpload: uploadReceipt(proxy.uploads({ phase: 'stale-before' })) },
      currentSession: SESSION
    }),
    chipAfterSend: chipOf(postRejChipsAfter, postRejName)?.state ?? null,
    chipConsumedBySend: postRejChipConsumed,
    baselineClean: postRejBaselineClean,
    // 与 T02 同一判定器：这里期望"该提示不可见"。
    promptSurface: { criteriaVersion: postRejJudged.criteriaVersion, mountCount: postRejJudged.observations.mountCount, overall: postRejJudged.overall, subAssertions: postRejJudged.subAssertions, observations: postRejJudged.observations, window: postRejSurface.window }
  }
  observedPromptSurfaces.postRejectionDiagnostic = postRejectionDiagnostic
  trace.push({
    phase: 'alert-post-rejection-diagnostic',
    imported: postRejImport,
    draftBeforeControl: postRejectionDiagnostic.draftBeforeControl,
    readyChip: postRejectionDiagnostic.readyChip,
    uploadReceiptPreview: postRejectionDiagnostic.uploadReceiptPreview,
    clicked: postRejSend.clicked,
    envelope: postRejectionDiagnostic.envelope,
    promptRequestsInWindow: postRejSend.promptRequestsInWindow,
    promptPayload: postRejectionDiagnostic.promptPayload,
    chipAfterSend: postRejectionDiagnostic.chipAfterSend,
    chipConsumedBySend: postRejChipConsumed,
    promptSurface: {
      criteriaVersion: postRejJudged.criteriaVersion,
      window: postRejSurface.window,
      mountCount: postRejJudged.observations.mountCount,
      samples: postRejSend.samples.map((sample) => ({ tMs: sample.t - postRejSend.sendAtWall, texts: sample.alerts.map((alert) => alert.text) }))
    }
  })
  console.log(
    `  [诊断] 拒绝后的同手势：草稿残留=${postRejectionDiagnostic.draftBeforeControl.count} 信封 ok=${postRejSend.envelope?.ok ?? 'null'} 码=${postRejSend.envelope?.envelope?.code ?? 'null'} prompt 内 receipt 命中=${JSON.stringify(postRejectionDiagnostic.promptPayload.receiptMatches ?? null)} 再被拒=${postRejSend.envelope?.ok === false} 窗口内挂载=${postRejJudged.observations.mountCount}\n`
  )
  // 第二条负向对照：把"陈旧 receipt 被拒"这件事**先做完**，再把草稿清干净、重传新文件，
  // 同一发送手势这一次被宿主接受 ⇒ 同一判据必须看不到提示。证明提示绑定的是"这次拒绝"，
  // 而不是"宿主重启过"或"这个页面出过任何错"。
  const acceptedAfterRejection =
    postRejSend.clicked.ok === true && postRejSend.envelope !== null && postRejSend.envelope.ok === true && postRejSend.envelope.envelope === null
  negative(
    'N9-post-rejection-fresh-receipt-send-accepted',
    '反例（拒绝之后）：把陈旧草稿完全清空（见证导入确认残留=0）并重传新文件后，同一发送手势被宿主确定接受（信封 ok=true）且同一观察者/同一窗口内不出现该附件提示——证明 T02 的提示绑定的是"这一次附件拒绝"，而不是"宿主重启过"或"页面出过错"',
    postRejectionDiagnostic.draftBeforeControl.count === 0 && acceptedAfterRejection && postRejBaselineClean && postRejJudged.observations.mountCount === 0,
    `信封 ok=${postRejSend.envelope?.ok ?? 'null'} 码=${postRejSend.envelope?.envelope?.code ?? 'null'} 草稿残留=${postRejectionDiagnostic.draftBeforeControl.count} prompt 内 receipt 命中=${JSON.stringify(postRejectionDiagnostic.promptPayload.receiptMatches ?? null)} 草稿被消费=${postRejChipConsumed} 观察起点无残留=${postRejBaselineClean} 窗口内挂载次数=${postRejJudged.observations.mountCount} 观察窗=${postRejSend.observedUntilMs}ms`
  )
  await removeChip(postRejName)

  // 检测器自检：注入一个与上游同形的 toast 必须被**同一观察者**看到并看到消失——
  // 否则 T02 的"出现"与 N7 的"没出现"都可能是检测器自身结构性失明。
  await evaluate(page, `globalThis.__D19__.installAlertObserver()`)
  const probeStartWall = Date.now()
  const probeInjected = JSON.parse(await evaluate(page, `globalThis.__D19__.injectProbeAlert(${JSON.stringify(ALERT_EXPECTED_TEXT)})`))
  const probeSeen = await poll(
    async () => JSON.parse(await evaluate(page, `globalThis.__D19__.sampleAlerts()`)),
    (value) => value.alerts.some((alert) => alert.text === ALERT_EXPECTED_TEXT),
    { timeoutMs: 3000, intervalMs: 100 }
  )
  const probeRemoved = JSON.parse(await evaluate(page, `globalThis.__D19__.removeProbeAlert()`))
  const probeGone = await poll(
    async () => JSON.parse(await evaluate(page, `globalThis.__D19__.sampleAlerts()`)),
    (value) => value.alerts.every((alert) => alert.text !== ALERT_EXPECTED_TEXT),
    { timeoutMs: 3000, intervalMs: 100 }
  )
  const probeEvents = JSON.parse(await evaluate(page, `globalThis.__D19__.alertEvents()`))
  // 自检也走**同一判定器**：注入节点的出现/消失必须在记录里成为一条完整实例。
  const probeJudged = judgeAlertWindow({
    events: probeEvents,
    samples: [],
    sendAtWall: probeStartWall,
    windowMs: Date.now() - probeStartWall,
    expectedText: ALERT_EXPECTED_TEXT,
    matchRe: ALERT_ATTACHMENT_RE,
    promptRequestsInWindow: 1,
    envelope: { code: 'session/attachment-invalid', reason: 'FILE_NOT_STAGED' },
    bounds: { instanceMinMs: 0, instanceMaxMs: Number.MAX_SAFE_INTEGER, totalMaxMs: Number.MAX_SAFE_INTEGER }
  })
  const probeInstance = probeJudged.instances[0] ?? null
  observedPromptSurfaces.detectorSelfTest = {
    injected: probeInjected.ok === true,
    seen: probeSeen.ok === true,
    removed: probeRemoved.ok === true,
    disappearSeen: probeGone.ok === true,
    criteriaVersion: probeJudged.criteriaVersion,
    observedInstance: probeInstance,
    subAssertions: probeJudged.subAssertions
  }
  negative(
    'N8-alert-detector-sees-a-toast',
    '反例：向 document.body 注入一个与上游同形的 [role="alert"] / class 含 toast 的节点时，同一观察者与同一判据必须记录到它的**出现**与**消失**——否则 T02 的"出现"和 N7 的"没出现"都可能是检测器瞎',
    probeInjected.ok === true &&
      probeSeen.ok === true &&
      probeRemoved.ok === true &&
      probeGone.ok === true &&
      probeInstance !== null &&
      probeInstance.startMs !== null &&
      probeInstance.endMs !== null,
    `注入=${probeInjected.ok} 被看到=${probeSeen.ok} 移除=${probeRemoved.ok} 消失被看到=${probeGone.ok} 判据版本=${probeJudged.criteriaVersion} 实例=${JSON.stringify(probeInstance)}`
  )
  // 自检节点已经移除，重新武装干净的观察者，避免影响后续阶段。
  await evaluate(page, `globalThis.__D19__.installAlertObserver()`)

  // ================= 撤销语义 =================
  console.log('\n阶段 8：撤销语义（新请求被拒 / 在途被终止 / 配对心跳不受影响）\n')
  // 先造一个"在途"的上传：stall 挂起，使请求停在门控之前。
  const revokeInflightName = 'd19-revoke-inflight.bin'
  proxy.setPhase('revoke-inflight')
  proxy.arm({ kind: 'stall' })
  const revokeInflightImport = JSON.parse(await evaluate(page, `globalThis.__D19__.importFile(${JSON.stringify(revokeInflightName)})`))
  const inflightHeld = await poll(async () => proxy.heldCount(), (value) => value >= 1, { timeoutMs: 10_000 })
  // 撤销设备：随后放行在途请求，它必须以确定拒绝结束，而不是悄悄成功。
  const revokeResponse = await fetch(`http://127.0.0.1:${port}/api/pair/revoke`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ deviceId })
  })
  const revokeBody = await revokeResponse.json().catch(() => null)
  const releasedInflight = proxy.release()
  const inflightSettled = await poll(async () => chipOf(await readChips(page), revokeInflightName), (chip) => chip !== null && chip.state !== 'uploading', { timeoutMs: 20_000 })
  const inflightEntry = proxy.uploads({ phase: 'revoke-inflight' })[0] ?? null
  proxy.disarm()

  // 新请求：撤销之后的手势必须被门控确定拒绝。
  const revokedNewName = 'd19-revoke-new.bin'
  const revokeNew = await gesture({ id: 'revoke-new', fault: { kind: 'pass' }, name: revokedNewName, quietMs: QUIET_MS })
  const revokedNewEntry = proxy.uploads({ phase: 'revoke-new' })[0] ?? null
  let revokedNewCode = null
  try {
    const parsed = revokedNewEntry?.responseBody === undefined ? null : JSON.parse(revokedNewEntry.responseBody)
    revokedNewCode = parsed?.result?.error?.code ?? null
  } catch {
    revokedNewCode = null
  }

  // 撤销设备的凭证必须被确定拒绝；配对通道本身必须仍然活着。
  const revokedHeartbeat = await client.fetch(`${lanBase}/api/pair/heartbeat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const revokedHeartbeatBody = await revokedHeartbeat.text()
  const reissue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const reissueToken = (await reissue.json().catch(() => null))?.token ?? null
  const freshClient = createClient()
  const freshAccept = await freshClient.follow(`${lanBase}/pair-accept?pair=${reissueToken ?? ''}`)
  const freshDevice = freshClient.jar.get('dsh_pair') ?? ''
  const freshHeartbeat = await freshClient.fetch(`${lanBase}/api/pair/heartbeat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  const freshHeartbeatBody = await freshHeartbeat.text()
  const fenceVisible = await evaluate(page, `globalThis.__D19__.fenceVisible()`)

  trace.push({
    phase: 'revocation',
    revoke: { status: revokeResponse.status, ok: revokeBody?.ok ?? null },
    inflight: { held: inflightHeld.value, released: releasedInflight, outcome: inflightEntry?.outcome ?? null, status: inflightEntry?.status ?? null, chip: inflightSettled.value?.state ?? null },
    newRequest: { chip: revokeNew.immediate.chip, status: revokedNewEntry?.status ?? null, code: revokedNewCode, uploads: revokeNew.immediate.uploads },
    revokedHeartbeat: { status: revokedHeartbeat.status, body: revokedHeartbeatBody.slice(0, 120) },
    freshPairing: { issueStatus: reissue.status, acceptStatus: freshAccept.response?.status ?? null, heartbeatStatus: freshHeartbeat.status, body: freshHeartbeatBody.slice(0, 80), distinctDevice: freshDevice !== deviceId }
  })

  gate(
    'V01-revocation-refuses-new-request',
    '撤销语义 A（新请求）：撤销后新的上传手势被门控**确定拒绝**——真实服务返回 403 + 信封码 unpaired，卡片停在 error，且只发出 1 个请求、不自动重发',
    revokeNew.immediate.chip === 'error' &&
      revokedNewEntry?.status === 403 &&
      revokedNewCode === 'unpaired' &&
      revokeNew.immediate.uploads === 1 &&
      revokeNew.afterQuiet.uploads === 1,
    `chip=${revokeNew.immediate.chip} 服务状态=${revokedNewEntry?.status ?? 'n/a'} 码=${revokedNewCode} 逻辑上传=${revokeNew.immediate.uploads}→${revokeNew.afterQuiet.uploads}`
  )
  gate(
    'V02-revocation-terminates-inflight',
    '撤销语义 B（在途请求）：撤销前发出、停在门控之前的在途上传在放行时被**终止**——以同一个 403/unpaired 确定拒绝结束，绝不悄悄完成',
    inflightHeld.ok === true &&
      releasedInflight === 1 &&
      inflightSettled.value?.state === 'error' &&
      inflightEntry?.status === 403,
    `在途挂起=${inflightHeld.value} release=${releasedInflight} 结局=${inflightEntry?.outcome ?? 'n/a'}/${inflightEntry?.status ?? 'n/a'} chip=${inflightSettled.value?.state ?? 'null'}`
  )
  gate(
    'V03-revocation-keeps-pairing-heartbeat',
    '撤销语义 C（不影响配对/心跳）：被撤销设备的凭证被确定拒绝（401/unpaired），而配对通道本身照常工作——重新签发 token、新设备配对成功、新设备心跳 200',
    revokedHeartbeat.status === 401 &&
      /unpaired/.test(revokedHeartbeatBody) &&
      reissue.status === 200 &&
      typeof reissueToken === 'string' &&
      freshHeartbeat.status === 200 &&
      freshDevice !== deviceId,
    `旧设备心跳=${revokedHeartbeat.status} 重新签发=${reissue.status} 新设备心跳=${freshHeartbeat.status} 设备不同=${freshDevice !== deviceId}`
  )
  await removeChip(revokedNewName)

  if (fenceVisible) {
    trace.push({ phase: 'revocation', fenceVisible: true })
  }

  // ---------- 负向探针：撤销判据不是恒真 ----------
  const controlHeartbeat = await client.fetch(`${lanBase}/api/pair/heartbeat`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  })
  negative(
    'N4-revoked-credential-really-dead',
    '反例：撤销之后旧设备**不能**再被当成有效凭证（否则"新请求被拒"就只是假象）',
    controlHeartbeat.status !== 200,
    `旧设备心跳=${controlHeartbeat.status}`
  )
} catch (error) {
  fatal = error
  gate('D19-99-runtime', '判据脚本自身跑完（无致命异常）', false, String(error?.stack ?? error).slice(0, 400))
} finally {
  try {
    if (chrome !== null) await chrome.close()
    if (proxy !== null) await proxy.close()
    if (stub !== null) await stopStub(stub)
    if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid'))
  } catch (error) {
    console.error(`清理阶段异常：${String(error?.message ?? error)}`)
  }
}

// ---------- 脱敏 trace ----------

const tracePayload = {
  schemaVersion: 1,
  task: 'D19',
  purpose: 'upload-failure-and-revocation',
  generatedAtMs: Date.now(),
  redaction: 'pair=/cookie/设备ID/绝对家目录路径全部脱敏（redact() + 本地路径规则）',
  entries: trace
}
let traceText = JSON.stringify(tracePayload, null, 2) + '\n'
const rawLeaks = findTraceLeaks(traceText)
traceText = redactTraceText(traceText)
const postLeaks = findTraceLeaks(traceText)
await mkdir(RUN_DIR, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await writeFile(join(RUN_DIR, 'trace.json'), traceText)
await writeFile(join(durableEvidenceDir, 'd19-failure-trace.json'), traceText)
gate(
  'D19-98-trace-redacted',
  '脱敏 trace：落盘文本经泄漏检查零命中（pair=/token/deviceId/cookie/JWT/绝对家目录路径），且脱敏确实生效（落盘前命中项被消除或本就不含敏感形态）',
  postLeaks.length === 0,
  `落盘前命中=${JSON.stringify(rawLeaks)} 落盘后命中=${JSON.stringify(postLeaks)} 字节=${Buffer.byteLength(traceText)}`
)
// 泄漏检查自身的反例：构造一段必然含敏感形态的文本，检查器必须报出来。
const canary = ['pair=abcdefghijklmnop', 'dsh_pair=abcdefghijklmnop', '/home/someuser/AI-Project/x', '"token":"abcdefghijklmnop"'].join('\n')
const canaryLeaks = findTraceLeaks(canary)
negative(
  'N5-leak-check-fires',
  '反例：泄漏检查器对一段故意含 pair=/cookie/绝对家目录/token 的文本必须报出泄漏（否则"零命中"是恒真的空检查）',
  canaryLeaks.length >= 3,
  `canary 命中=${JSON.stringify(canaryLeaks)}`
)

// ---------- 证据（每次运行独立 runId；历史结果只增不改） ----------

const failed = gates.filter((item) => !item.ok)
const failedNegative = negativeProbes.filter((item) => !item.ok)
const candidate = candidateSummary()
const report = JSON.stringify(
  {
    schemaVersion: 2,
    task: 'D19',
    purpose: 'upload-failure-and-revocation',
    runId: RUN_ID,
    criteriaVersion: D19_ALERT_CRITERIA_VERSION,
    startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
    finishedAt: new Date().toISOString(),
    command: RUN_COMMAND,
    platform: { platform: process.platform, release: osRelease(), arch: process.arch, node: process.version, cwd: process.cwd(), dshBin },
    candidate,
    gates,
    failedGateIds: failed.map((item) => item.id),
    result: failed.length === 0 ? 'pass' : 'fail',
    controls: [
      { id: 'C01-control-pass-succeeds', description: '同一上传手势在注入关闭时成功（chip=ready、1 个逻辑请求、HTTP 200）' },
      { id: 'N1-error-assertion-distinguishes', description: '把"必须是 error"套到对照组上为假：error 断言能区分两种结果' },
      { id: 'N2-partial-batch-failure-is-real', description: '部分批次里第 2 个文件确实失败、第 1 个确实成功' },
      { id: 'N3-retry-not-a-second-draft', description: '重试后草稿条目数仍为 1（复用而非重新导入）' },
      { id: 'N4-revoked-credential-really-dead', description: '撤销后旧设备凭证不再被接受' },
      { id: 'N7-successful-send-shows-no-attachment-prompt', description: 'T02 的负向对照：同一发送手势在 receipt 有效、信封 ok=true 时，同一观察者/同一窗口内不出现该附件提示' },
      { id: 'N8-alert-detector-sees-a-toast', description: 'T02/N7 的检测器自检：注入同形 [role="alert"]/_toast_* 节点必须被同一观察者记录到出现与消失' },
      { id: 'N9-post-rejection-fresh-receipt-send-accepted', description: 'T02 的第二条负向对照：陈旧拒绝之后再清空草稿、重传新文件，同一手势被接受且不出现该提示（证明提示绑定本次拒绝）' }
    ],
    negativeProbes,
    notRun,
    staleReceiptPrompt: observedPromptSurfaces,
    faultMatrix: observedFaults,
    trace: {
      path: `artifacts/verify-portable/d19-runs/${RUN_ID}/trace.json`,
      latestMirror: 'artifacts/verify-portable/d19-failure-trace.json',
      entries: trace.length,
      leakCheck: { beforeRedaction: rawLeaks, afterRedaction: postLeaks }
    },
    // 诚实标注：顶层 `d19-failure-gates.json` 是**最新一次**运行的镜像（供既有消费者读取），
    // 历史只认 d19-runs/<runId>/。R22c 那次失败运行的原始 JSON 已被上一次运行覆写，无法恢复。
    evidenceLayout: {
      runDir: `artifacts/verify-portable/d19-runs/${RUN_ID}`,
      index: 'artifacts/verify-portable/d19-runs/index.json',
      latestMirror: 'artifacts/verify-portable/d19-failure-gates.json（可被后续运行更新；历史不在此文件）'
    },
    note: '真实 Chromium + 真实夹具服务 + 故障反向代理；上传手势走生产桥 → 生产草稿适配器 → Harness 原生上传 → remote 改写 → /remote/api/session/uploadFileBinary。T02 的整体结果由 tests/fixtures/d19-alert-window-judge.mjs 的具名子断言记录直接算出。'
  },
  null,
  2
) + '\n'
// 报告本身也过一遍泄漏检查（它带着 trace 里同源的信息），命中就写进索引并在控制台点名。
const reportLeaksBefore = findTraceLeaks(report)
const reportText = reportLeaksBefore.length === 0 ? report : redactTraceText(report)
const reportLeaksAfter = findTraceLeaks(reportText)
if (existsSync(join(RUN_DIR, 'report.json'))) throw new Error(`runId 目录已存在（拒绝覆写历史）：${RUN_DIR}`)
await writeFile(join(RUN_DIR, 'report.json'), reportText)
await writeFile(join(evidenceDir, 'd19-failure-gates.json'), reportText)
await writeFile(join(durableEvidenceDir, 'd19-failure-gates.json'), reportText)
await writeFile(
  join(RUN_DIR, 'run.json'),
  JSON.stringify(
    {
      runId: RUN_ID,
      task: 'D19',
      criteriaVersion: D19_ALERT_CRITERIA_VERSION,
      startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
      finishedAt: new Date().toISOString(),
      durationMs: Date.now() - RUN_STARTED_AT_MS,
      command: RUN_COMMAND,
      platform: { platform: process.platform, release: osRelease(), arch: process.arch, node: process.version, cwd: process.cwd(), dshBin },
      candidate,
      result: failed.length === 0 ? 'pass' : 'fail',
      exitCode: failed.length > 0 || failedNegative.length > 0 ? 1 : 0,
      gates: { total: gates.length, passed: gates.length - failed.length, failedGateIds: failed.map((item) => item.id) },
      negativeProbes: { total: negativeProbes.length, passed: negativeProbes.length - failedNegative.length, failedIds: failedNegative.map((item) => item.id) },
      artifacts: { report: 'report.json', trace: 'trace.json', stdout: 'stdout.log' },
      reportLeakCheck: { beforeRedaction: reportLeaksBefore, afterRedaction: reportLeaksAfter }
    },
    null,
    2
  ) + '\n'
)

const indexEntry = {
  runId: RUN_ID,
  startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
  finishedAt: new Date().toISOString(),
  durationMs: Date.now() - RUN_STARTED_AT_MS,
  result: failed.length === 0 ? 'pass' : 'fail',
  gates: `${gates.length - failed.length}/${gates.length}`,
  failedGateIds: failed.map((item) => item.id),
  failedNegativeIds: failedNegative.map((item) => item.id),
  criteriaVersion: D19_ALERT_CRITERIA_VERSION,
  candidate: { gitHead: candidate.gitHead, gitBranch: candidate.gitBranch, worktreeDirtyEntries: candidate.gitWorktree.dirtyEntries, pluginVersion: candidate.pluginVersion },
  command: RUN_COMMAND,
  report: `artifacts/verify-portable/d19-runs/${RUN_ID}/report.json`,
  stdout: `artifacts/verify-portable/d19-runs/${RUN_ID}/stdout.log`,
  trace: `artifacts/verify-portable/d19-runs/${RUN_ID}/trace.json`,
  reportLeakCheck: { beforeRedaction: reportLeaksBefore, afterRedaction: reportLeaksAfter }
}
const index = await readRunIndex()
if (index.runs.some((item) => item.runId === RUN_ID)) throw new Error(`runId 冲突（拒绝覆写历史）：${RUN_ID}`)
index.runs.push(indexEntry)
await writeFile(join(RUNS_DIR, 'index.json'), JSON.stringify({ ...index, note: '只增不改：每次运行 push 一条；历史报告在各自的 runId 目录里，本文件与顶层 latest 镜像都不得作为历史依据。' }, null, 2) + '\n')
console.log(`\n[run] runId=${RUN_ID} 判据版本=${D19_ALERT_CRITERIA_VERSION} 报告=artifacts/verify-portable/d19-runs/${RUN_ID}/report.json`)
console.log(`[run] 已登记到 d19-runs/index.json（累计 ${index.runs.length} 次，只增不改）`)

console.log(`\nD19 gates: ${gates.length - failed.length}/${gates.length} 通过`)
console.log(`负向探针：${negativeProbes.length - failedNegative.length}/${negativeProbes.length} 成立`)
if (notRun.length > 0) console.log(`未验：${notRun.map((item) => item.id).join(', ')}`)
if (fatal !== null) console.error(`致命异常：${String(fatal?.stack ?? fatal).slice(0, 600)}`)
if (failed.length > 0 || failedNegative.length > 0) {
  console.error('失败判据：')
  for (const item of [...failed, ...failedNegative]) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
  process.exitCode = 1
} else {
  console.log('d19-failure-gates: PASS')
}
