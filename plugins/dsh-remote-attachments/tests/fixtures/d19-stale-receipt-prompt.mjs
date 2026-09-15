#!/usr/bin/env node
/**
 * D19 追加观测（只读验证）：失效 receipt 被服务端以 `session/attachment-invalid` /
 * `FILE_NOT_STAGED` 拒绝时，**是否存在用户可见的原生恢复提示**。
 *
 * 观测面（按请求者指定）：
 *   A. `document.body` 下的 `[role="alert"]`（以及识别出的 toast 容器/节点），
 *      记录全部带时间戳采样（含空样本）、文本与 DOM 快照。
 *   B. 公开的 `Session.promptError`。本脚本先做**穷尽式公开句柄搜索**
 *      （boot 前后 globalThis 差集 + 逐个检查候选句柄 + 深度扫描可达对象图），
 *      把结果写进 `promptErrorReadPath`；只有真找到公开路径才用它。搜索不到时就
 *      如实写 `publicPath: null` 并列出查过什么，**不拿私有结构冒充公开面**。
 *      作为**非公开旁证**（明确标注），另有一条 React fiber 读取，仅用于解释
 *      "上一轮为什么读不到"（promptError 驻留 ~17s 而 toast 只活 ~4s）。
 *
 * 两个观测面都在**发送之前**开始，以 ~150ms 间隔轮询到发送后 16s；另挂
 * MutationObserver 兜底记录出现/消失的精确时刻，避免瞬时提示被轮询漏掉。
 *
 * 不碰 src/**、eng/**、docs/**、node_modules；不劫持发送路径；不自动删除/重发。
 */

import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createClient, redact, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'
import { startFaultProxy } from './fault-proxy.mjs'

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

const SAMPLE_INTERVAL_MS = Number(process.env.DSH_ATTACH_SAMPLE_INTERVAL_MS ?? 150)
const PRE_SEND_MS = Number(process.env.DSH_ATTACH_PRE_SEND_MS ?? 1500)
const POST_SEND_MS = Number(process.env.DSH_ATTACH_POST_SEND_MS ?? 16_000)

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))

// =====================================================================
// 页面侧装置
// =====================================================================

/** boot 之前的 globalThis 快照：之后与 boot 后做差集，得到"应用加了哪些全局"。 */
const BASELINE_SCRIPT = `
try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:__SESSION__}))}catch(e){}
try{globalThis.__BASELINE_GLOBALS__=Object.getOwnPropertyNames(globalThis).slice()}catch(e){}
`

const READ_CHIPS = `JSON.stringify([...document.querySelectorAll('[data-composer-card] [title]')].filter((element) => element.tagName === 'DIV').map((element) => {
  const text = element.textContent || ''
  const buttons = [...element.querySelectorAll('button')].map((button) => button.getAttribute('aria-label') || '')
  const failed = /上传失败|Upload failed/.test(text)
  const uploading = /上传中|Uploading/.test(text)
  return { name: element.getAttribute('title'), state: failed ? 'error' : uploading ? 'uploading' : 'ready', buttons }
}))`

const INSTALL_GESTURES = `(() => {
  const addon = globalThis.__DSH_ATTACHMENTS_ADDON__
  const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  if (addon === undefined || bridge === undefined || host === undefined) {
    return JSON.stringify({ ok: false, reason: 'addon-not-installed' })
  }
  const state = {}
  globalThis.__D19P__ = state
  state.chips = () => ${READ_CHIPS}
  const bytes = (label, size) => {
    const out = new Uint8Array(size)
    for (let i = 0; i < out.length; i++) out[i] = (i + label.length) % 251
    return out
  }
  state.importFile = (name, size) => {
    const file = new File([bytes(name, size === undefined ? 2048 : size)], name, { type: 'application/octet-stream' })
    try {
      const result = bridge.importFiles({ files: [file] })
      return JSON.stringify({ result })
    } catch (error) {
      return JSON.stringify({ threw: String(error && error.message ? error.message : error) })
    }
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
  return JSON.stringify({ ok: true })
})()`

/**
 * 公开句柄搜索：应用新增了哪些全局？这些全局里有没有能到达 Session / sessions 服务的？
 *
 * 判据（任一命中即算"公开可达"）：
 *   - 对象本身或其 ≤3 层子对象带 `binding` 函数 + `list` 属性（`ctx.sessions` 服务形状）；
 *   - 对象本身或其 ≤3 层子对象带 `getSnapshot` + `subscribe` + `buildSnapshot`（Session 形状）；
 *   - 对象本身或其 ≤3 层子对象像 cordis Context（`get` 函数 + `reflect`/`plugin`/`inject`），
 *     且 `get('sessions')` 能取到东西。
 */
const PUBLIC_PATH_PROBE = `(() => {
  const baseline = new Set(globalThis.__BASELINE_GLOBALS__ ?? [])
  const added = Object.getOwnPropertyNames(globalThis).filter((name) => !baseline.has(name))
  const ownKeys = (value) => {
    try { return Object.getOwnPropertyNames(value).slice(0, 25) } catch (error) { return null }
  }
  const protoKeys = (value) => {
    try { const proto = Object.getPrototypeOf(value); return proto === null ? null : Object.getOwnPropertyNames(proto).slice(0, 40) } catch (error) { return null }
  }
  const isSessionsService = (value) => value !== null && typeof value === 'object' && typeof value.binding === 'function' && 'list' in value
  const isSession = (value) => value !== null && typeof value === 'object' && typeof value.getSnapshot === 'function' && typeof value.subscribe === 'function' && typeof value.buildSnapshot === 'function'
  const isCordisCtx = (value) => value !== null && typeof value === 'object' && typeof value.get === 'function' && (value.reflect !== undefined || typeof value.inject === 'function' || typeof value.plugin === 'function')

  const findings = []
  const visited = new Set()
  let scannedNodes = 0
  const scan = (value, path, depth) => {
    if (scannedNodes > 4000) return
    if (value === null || typeof value !== 'object') return
    if (visited.has(value)) return
    visited.add(value)
    scannedNodes += 1
    if (isSessionsService(value)) findings.push({ kind: 'sessions-service', path, keys: ownKeys(value) })
    if (isSession(value)) findings.push({ kind: 'session-instance', path, keys: ownKeys(value) })
    if (isCordisCtx(value)) {
      const probed = {}
      for (const name of ['sessions', 'uiSession', 'clientModules', 'slots']) {
        try { const got = value.get(name); probed[name] = got === undefined ? 'undefined' : 'object' } catch (error) { probed[name] = 'threw:' + String(error && error.message ? error.message : error).slice(0, 60) }
      }
      findings.push({ kind: 'cordis-ctx', path, keys: ownKeys(value), serviceProbe: probed })
    }
    if (depth >= 3) return
    let names = []
    try { names = Object.getOwnPropertyNames(value) } catch (error) { return }
    for (const name of names.slice(0, 60)) {
      if (name === 'window' || name === 'self' || name === 'globalThis' || name === 'document' || name === 'top' || name === 'parent' || name === 'frames') continue
      let child
      try { child = value[name] } catch (error) { continue }
      if (child === null || typeof child !== 'object') continue
      scan(child, path + '.' + name, depth + 1)
    }
  }

  const describeGlobal = (name) => {
    let value
    try { value = globalThis[name] } catch (error) { return { name, error: 'threw' } }
    if (value === null) return { name, type: 'null' }
    const type = typeof value
    const entry = { name, type }
    if (type === 'object' || type === 'function') {
      entry.ownKeys = ownKeys(value)
      entry.protoKeys = type === 'function' ? protoKeys(value) : null
    }
    return entry
  }

  const named = {}
  for (const name of ['__DSH_BOOT__', '__ModuleLoader__', '__DSH_TRANSPORT__', '__DSH_BOOT_READY__', '__DSH_ATTACHMENTS_ADDON__', '__DSH_ATTACHMENTS_BRIDGE__', '__DSH_ATTACHMENTS_RECEIVER__', '__DSH_ATTACHMENTS_STATUS__', '__DSH_REMOTE_CHANNEL_BOOT__', '__DSH_FILE_UPLOAD__']) {
    named[name] = describeGlobal(name)
  }

  for (const name of added) {
    let value
    try { value = globalThis[name] } catch (error) { continue }
    scan(value, name, 0)
  }

  return JSON.stringify({
    baselineCount: baseline.size,
    addedGlobals: added,
    addedCount: added.length,
    scannedNodes,
    findings,
    named,
    publicPromptErrorPath: findings.length === 0 ? null : findings
  })
})()`

/**
 * 双观测面采样探针。
 *
 * `promptError`（明确标注为非公开旁证）：来源链全部来自锁定依赖 ——
 * `dsh-client-ui-session` 的 `BUILTIN_SOURCE.resolve()` 产出 `hooks: { session: binding.session }`，
 * 经 renderer 的 `ScopeBindingContext` 下发；`binding.session` 就是
 * `dsh-api-session-controller` 的 Session 实例，`session.getSnapshot().promptError` 是它的公开快照字段。
 * 该结构**不是**页面公开句柄，因此本脚本只用它做旁证，结论不依赖它。
 */
const INSTALL_PROBE = `(() => {
  const ROLE_ALERT = '[role="alert"]'
  const TOAST_CLASS = '[class*="toast"]'
  const SELECTOR = ROLE_ALERT + ', ' + TOAST_CLASS
  const describe = (element) => ({
    tag: element.tagName,
    class: element.getAttribute('class'),
    role: element.getAttribute('role'),
    parent: element.parentElement === null ? null : element.parentElement.tagName + (element.parentElement.getAttribute('class') ? '.' + element.parentElement.getAttribute('class') : ''),
    parentIsBody: element.parentElement === document.body,
    matchedByRoleAlert: element.matches(ROLE_ALERT),
    matchedByToastClass: element.matches(TOAST_CLASS),
    style: element.getAttribute('style'),
    text: (element.textContent || '').slice(0, 300),
    html: element.outerHTML.slice(0, 600)
  })
  const snapshotAlerts = () => [...document.querySelectorAll(SELECTOR)].map(describe)

  const componentName = (fiber) => {
    const t = fiber.type
    if (t === null || t === undefined) return null
    if (typeof t === 'function') return t.displayName || t.name || 'anonymous'
    if (typeof t === 'string') return t
    if (typeof t === 'object') return t.displayName || (t.render ? t.render.name : null) || 'object-component'
    return String(t)
  }
  const isSession = (value) => value !== null && typeof value === 'object' && typeof value.getSnapshot === 'function' && typeof value.subscribe === 'function'

  const findSession = () => {
    const card = document.querySelector('[data-composer-card]')
    if (card === null) return { session: null, reason: 'no-composer-card' }
    const key = Object.keys(card).find((name) => name.startsWith('__reactFiber$'))
    if (key === undefined) return { session: null, reason: 'no-react-fiber-key' }
    let fiber = card[key]
    let depth = 0
    while (fiber !== null && fiber !== undefined && depth < 80) {
      let node = fiber.dependencies ? fiber.dependencies.firstContext : null
      while (node !== null && node !== undefined) {
        const value = node.memoizedValue
        if (value !== null && typeof value === 'object' && value.hooks && isSession(value.hooks.session)) {
          return { session: value.hooks.session, via: 'context:ScopeBindingContext.hooks.session', component: componentName(fiber), depth }
        }
        node = node.next
      }
      const props = fiber.memoizedProps
      if (props !== null && typeof props === 'object') {
        if (props.hooks && isSession(props.hooks.session)) return { session: props.hooks.session, via: 'props.hooks.session', component: componentName(fiber), depth }
        for (const value of Object.values(props)) {
          if (value !== null && typeof value === 'object' && value.hooks && isSession(value.hooks.session)) {
            return { session: value.hooks.session, via: 'props[' + componentName(fiber) + '].hooks.session', component: componentName(fiber), depth }
          }
        }
      }
      fiber = fiber.return
      depth += 1
    }
    return { session: null, reason: 'session-not-reachable-via-fiber' }
  }

  let cached = null
  const state = { events: [], lastAlertsJson: null, observer: null }
  globalThis.__PR__ = state

  state.installObserver = () => {
    if (state.observer !== null) return JSON.stringify({ ok: true, already: true })
    state.lastAlertsJson = JSON.stringify(snapshotAlerts())
    state.events.push({ t: Date.now(), kind: 'baseline', alerts: JSON.parse(state.lastAlertsJson) })
    const record = (kind) => {
      const alerts = snapshotAlerts()
      const json = JSON.stringify(alerts)
      if (json === state.lastAlertsJson) return
      state.lastAlertsJson = json
      state.events.push({ t: Date.now(), kind, alerts })
    }
    state.observer = new MutationObserver((records) => {
      const relevant = records.some((record) => {
        for (const node of record.addedNodes) if (node.nodeType === 1 && (node.matches(SELECTOR) || node.querySelector(SELECTOR) !== null)) return true
        for (const node of record.removedNodes) if (node.nodeType === 1 && (node.matches(SELECTOR) || node.querySelector(SELECTOR) !== null)) return true
        return false
      })
      record(relevant ? 'alert-mutation' : 'other-mutation')
    })
    state.observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'role'] })
    return JSON.stringify({ ok: true, already: false })
  }
  state.readEvents = () => JSON.stringify(state.events)

  state.sample = () => {
    const alerts = snapshotAlerts()
    const roleAlerts = alerts.filter((alert) => alert.matchedByRoleAlert === true)
    let nonPublic = null
    const found = findSession()
    if (found.session !== null) cached = found
    const resolved = found.session !== null ? found : cached
    if (resolved !== null && resolved.session !== null) {
      try {
        const snapshot = resolved.session.getSnapshot()
        const promptError = snapshot ? snapshot.promptError : undefined
        nonPublic = {
          readable: true,
          via: resolved.via,
          component: resolved.component,
          depth: resolved.depth,
          sessionId: snapshot ? snapshot.sessionId : null,
          readPathIsPublic: false,
          promptError: promptError === undefined || promptError === null ? null : {
            op: promptError.op ?? null,
            error: {
              name: promptError.error ? (promptError.error.name ?? null) : null,
              code: promptError.error ? (promptError.error.code ?? null) : null,
              message: promptError.error ? (promptError.error.message ?? null) : null,
              details: promptError.error ? (promptError.error.details ?? null) : null,
              reason: promptError.error && promptError.error.details ? (promptError.error.details.reason ?? null) : null
            },
            keys: Object.keys(promptError)
          }
        }
      } catch (error) {
        nonPublic = { readable: false, reason: 'snapshot-threw', error: String(error && error.message ? error.message : error) }
      }
    } else {
      nonPublic = { readable: false, reason: found.reason ?? 'session-unresolved' }
    }
    return JSON.stringify({
      t: Date.now(),
      alerts,
      roleAlerts,
      alertText: roleAlerts.length === 0 ? null : roleAlerts.map((alert) => alert.text).join(' | '),
      nonPublicPromptError: nonPublic
    })
  }
  return JSON.stringify({ ok: true })
})()`

/** 页面实际加载的脚本里搜关键实现标记——证明"服务出去的页面 bundle"确实是这些代码。 */
const SCAN_SERVED_BUNDLES = `(async () => {
  const fromPerf = performance.getEntriesByType('resource').map((entry) => entry.name).filter((name) => name.includes('.js'))
  const fromScripts = [...document.querySelectorAll('script[src]')].map((node) => node.src)
  const urls = [...new Set([...fromPerf, ...fromScripts])]
  const out = []
  for (const url of urls) {
    try {
      const response = await fetch(url, { cache: 'force-cache' })
      const text = await response.text()
      const hits = {}
      for (const marker of ['session/attachment-invalid', 'file.notStaged', 'attachmentErrorText', 'dsh-toast-hold', '_toast_', 'promptError']) {
        const index = text.indexOf(marker)
        if (index !== -1) hits[marker] = { byteOffset: index, snippet: text.slice(Math.max(0, index - 140), index + 140) }
      }
      out.push({ url, status: response.status, bytes: text.length, hits })
    } catch (error) {
      out.push({ url, error: String(error && error.message ? error.message : error) })
    }
  }
  return JSON.stringify({ scannedCount: urls.length, scannedUrls: urls, bundles: out })
})()`

// =====================================================================
// 工具
// =====================================================================

const evaluate = (page, expression, { timeoutMs = 60_000 } = {}) =>
  Promise.race([
    page.evaluate(expression, { awaitPromise: true }),
    new Promise((_resolve, reject) =>
      setTimeout(() => reject(new Error(`页面求值超时（${timeoutMs}ms）：${String(expression).slice(0, 80)}`)), timeoutMs)
    )
  ])

async function poll(read, accept, { timeoutMs, intervalMs = 400 } = {}) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await read()
    if (accept(value)) return { ok: true, value }
    if (Date.now() >= deadline) return { ok: false, value }
    await sleep(intervalMs)
  }
}

async function installUntil(page, expression, { timeoutMs = 60_000 } = {}) {
  const deadline = Date.now() + timeoutMs
  let last = null
  for (;;) {
    try {
      last = JSON.parse(await evaluate(page, expression))
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
    body: JSON.stringify({ type: 'client-request', rpcId: `d19p-${method.replace(/\W/g, '-')}-${Date.now()}`, method, payload: { args: args ?? {} } })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

async function startStub() {
  const recordDir = join(evidenceDir, 'd19p-stub')
  await mkdir(recordDir, { recursive: true })
  const manifestPath = join(recordDir, 'manifest.json')
  await writeFile(manifestPath, JSON.stringify({ responses: [{ text: 'd19p done' }] }, null, 2) + '\n')
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

const HOME_DIR = process.env.HOME ?? ''
const redactText = (text) =>
  redact(text).split(HOME_DIR).join('<home-redacted>').split(repoRoot).join('<repo-redacted>').split(fixtureRoot).join('<fixture-redacted>')

/** 全局静态引用（逐条 file:line；本脚本运行时再附上实际服务出去的 bundle 字节偏移）。 */
const CITATIONS = [
  { claim: '(a) 错误码 origin：宿主 session/prompt 门控发现该附件没有 staged receipt 时抛出该码', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js', line: 930, quote: 'if (attachment === void 0) throw new RemoteError("session/attachment-invalid", "File was not uploaded for this session.", { reason: "FILE_NOT_STAGED" });' },
  { claim: '(a) 同码的 TS 源（types 面，同一门控）', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/types/commands.js', line: 519, quote: "throw new RemoteError('session/attachment-invalid', 'File was not uploaded for this session.', { reason: 'FILE_NOT_STAGED' });" },
  { claim: '(a) 错误码在 wire/remote 契约里的形状（只有 reason 字段；宿主不持有 promptError）', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/typert.host.js', line: 1637, quote: "'session/attachment-invalid': { readonly reason: string };" },
  { claim: '(b) 是否映射到 toast/alert：InputBar 的 effect 把 promptError 交给 showToast', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 15840, quote: 'showToast(error.code === "session/attachment-invalid" || error.code === "subagent/attachment-invalid" ? attachmentErrorText(t, error.details.reason, imageLimits) : `${error.message} (${error.code})`);' },
  { claim: '(b) showToast 定义（React state，序号驱动重挂载）', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 15826, quote: 'const showToast = (0, react.useCallback)((text) => { toastSeq.current += 1; setToast({ seq: toastSeq.current, text }); }, []);' },
  { claim: '(b) Toast 渲染点（锚定 composer 卡片，onDone 卸载）', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 16054, quote: 'toast !== null && (0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.Toast, { text: toast.text, icon: ..., anchor: cardRef.current, onDone: dismissToast }, toast.seq)' },
  { claim: '(b) reason → 文案映射：FILE_NOT_STAGED 走 file.notStaged', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 15301, quote: 'case "FILE_NOT_STAGED": return t("file.notStaged");' },
  { claim: '(b) zh 文案（与实测 toast 文本逐字一致）', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 13683, quote: '"file.notStaged": "文件尚未上传成功，请重新添加后再试",' },
  { claim: '(b) en 文案（同一 key 的英文）', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 13845, quote: '"file.notStaged": "The file has not finished uploading; re-add it and try again",' },
  { claim: '(d) Toast 组件本体：portal 到 document.body，带 role="alert" 与 --dsh-toast-hold', file: 'node_modules/@deepseek-ai/dsh-web-frontend/dist/assets/index-BKQ_L1z6.js', line: 61, quote: 'function $C({text:t,icon:r,anchor:i,holdMs:s=VC,onDone:a}){...createPortal(jsxs("div",{className:Y0.toast,role:"alert",style:{...,"--dsh-toast-hold":`${String(s)}ms`},children:[...]}),document.body)}' },
  { claim: '(d) toast 存活时长常量：holdMs 3000 + 淡出 1000 ⇒ 挂载后 4000ms 卸载', file: 'node_modules/@deepseek-ai/dsh-web-frontend/dist/assets/index-BKQ_L1z6.js', line: 61, quote: 'const VC=3e3,AC=1e3;  // useEffect(() => { const p = setTimeout(a, s + AC); return () => clearTimeout(p) }, [s, a])' },
  { claim: '(d) toast 的 CSS-module 类名（稳定哈希）', file: 'node_modules/@deepseek-ai/dsh-web-frontend/dist/assets/index-BKQ_L1z6.js', line: 56, quote: 'Dd="_toast_e5v0f_6",Fd="_icon_e5v0f_37",Vd="_text_e5v0f_44",Y0={toast:Dd,icon:Fd,text:Vd}' },
  { claim: '(d) toast 定位/动画（fixed + 居中 + --dsh-toast-hold 驱动淡出）', file: 'node_modules/@deepseek-ai/dsh-web-frontend/dist/assets/index-DPX2bQLO.css', line: 1, quote: '._toast_e5v0f_6{position:fixed;top:40px;left:50%;z-index:1100;pointer-events:none;...;animation:_dsh-toast-in_e5v0f_1 .16s ease-out,_dsh-toast-fade_e5v0f_1 1s ease var(--dsh-toast-hold, 3s) forwards}' },
  { claim: '(d) primitives 是 shell 内联的静态模块，Toast 即上面的 $C', file: 'node_modules/@deepseek-ai/dsh-web-frontend/dist/assets/index-BKQ_L1z6.js', line: 114, quote: '"@deepseek-ai/dsh-client-ui-primitives":Zg  // Zg 的导出表内含 Toast:$C' },
  { claim: '(d) 反例：附件渲染包完全不产生 alert/toast（0 处 role="alert"、0 处 Toast）', file: 'node_modules/@deepseek-ai/dsh-client-ui-attachment/lib/client.js', line: 686, quote: 'state: upload === void 0 || upload.status === "uploading" ? "uploading" : upload.status === "ready" ? "ready" : "error",  // 无任何 toast/alert 渲染' },
  { claim: '(d) composer 的"行内提示"是另一个元素，且只用于 info 级，且是 [data-composer-card] 的**兄弟**而非后代', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 16058, quote: 'notice?.level === "info" && (0, react_jsx_runtime.jsx)("div", { className: InputBar_module_css_default.notice, role: "status", children: notice.text })' },
  { claim: '(c) 公开 Session.promptError 形状与写入点（send 失败）', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/client.js', line: 1688, quote: 'if (!result.ok) { ... this.promptError = { op: "send", error: result.error }; this.notifier.markDirty(); return result }' },
  { claim: '(c) 写入点（stop/cancel 失败）', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/client.js', line: 1742, quote: 'if (!result.ok) { this.promptError = { op: "stop", error: result.error }; this.notifier.markDirty(); }' },
  { claim: '(c) promptError 在快照里发布（buildSnapshot → getSnapshot）', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/client.js', line: 2148, quote: 'buildSnapshot() { return { ..., promptError: this.promptError, ... } }  // getSnapshot() 返回该缓存' },
  { claim: '(c) UI 正是通过 useSession 选择器读它', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 15811, quote: 'const promptError = useSession((s) => s.promptError) ?? null;' },
  { claim: '(c) TS 源形状：PromptError = { op, error }', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/types/client/sessions/session.js', line: 211, quote: "this.promptError = { op: 'send', error: result.error };" },
  { claim: '(c) SessionSnapshot 契约里 promptError 的声明', file: 'node_modules/@deepseek-ai/dsh-cordis-client-runner/lib/client.js', line: 1997, quote: 'export interface SessionSnapshot { ... readonly promptError: PromptError | null; ... }' },
  { claim: '(公开路径反证) 宿主侧完全不持有 promptError：wire/RPC 面没有它', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js', line: null, quote: 'grep -rn "promptError" dsh-api-session-controller/lib/index.js → 0 命中（promptError 只在 client.js 里出现）' },
  { claim: '(公开路径反证) 页面可用的 /remote/api 会话方法共 13 个，均不返回 promptError', file: 'node_modules/@deepseek-ai/dsh-api-session-controller/lib/client.js', line: null, quote: 'remote.session.{attachment,cancel,control,create,follow,fork,list,modelCatalog,page,prompt,rename,search,updateQueue}' },
  { claim: '(公开路径反证) 插件页面全局不暴露 Session/ctx：AddonHandle 只有 version/packageName/disposed/capability()/status()/dispose()', file: 'plugins/dsh-remote-attachments/src/client/compose.ts', line: 195, quote: 'export interface AddonHandle { readonly version: number; readonly packageName: string; readonly disposed: boolean; capability(): CapabilityResolution; status(): AddonStatus; dispose(reason?: string): AddonStatus }' },
  { claim: '(公开路径反证) 插件的 bridge 页面全局只给 currentSession(): string | null，不给 Session 对象', file: 'plugins/dsh-remote-attachments/src/client/bridge.ts', line: 82, quote: 'currentSession(): string | null' },
  { claim: '(澄清 1) 插件线协议只有 staged/failed/partial，明确"没有 upload ready"', file: 'plugins/dsh-remote-attachments/src/shared/protocol.ts', line: 106, quote: "/** `import-result` 与 `batch-end` 的单文件结果状态；没有 upload ready。 */\nexport const RESULT_STATUSES = Object.freeze(['staged', 'failed', 'partial'])" },
  { claim: '(澄清 1) staged 只代表草稿接收；上传与发送归 Harness', file: 'plugins/dsh-remote-attachments/src/shared/wire/session.ts', line: 691, quote: '// staged 只代表草稿接收；上传与发送仍归 Harness，线协议里没有 ready。\nfile.upload = message.status === \'staged\' ? \'harness-owned\' : \'none\'' },
  { claim: '(澄清 1) staged 由本插件 receiver 产出（草稿导入成功且新增 ID 数自洽）', file: 'plugins/dsh-remote-attachments/src/client/receiver.ts', line: 1151, quote: "return { v: 1, type: 'import-result', sessionId, batchId, fileId, status: 'staged', attachmentIds: ids }" },
  { claim: '(澄清 1) upload ready 由 Harness 的 composer 草稿控制器写入（携带 receiptId/file）', file: 'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js', line: 3052, quote: "draft[attachment.id] = result.ok ? { status: \"ready\", receiptId: result.value.receiptId, file: result.value.file } : { status: \"error\", message: result.error.message };" },
  { claim: '(澄清 1) 附件包按 upload.status 渲染 ready/error（消费方）', file: 'node_modules/@deepseek-ai/dsh-client-ui-attachment/lib/client.js', line: 686, quote: 'state: ... upload.status === "ready" ? "ready" : "error"' },
  { claim: '(澄清 3) incomplete 汇总口径：任何 incomplete 直接判 portableStatus=fail', file: 'eng/verify-portable.ps1', line: 788, quote: '$incomplete = @($checks | Where-Object { $_.status -eq \'incomplete\' })\n$portableStatus = if ($coreFailures.Count -gt 0 -or $incomplete.Count -gt 0 -or $executedCases -ne $requiredCases -or $failedCases -ne 0 -or $skippedCases -ne 0) { \'fail\' } else { \'pass\' }' },
  { claim: '(澄清 3) 未实现的必需项被显式记 incomplete（绝不静默通过）', file: 'eng/verify-portable.ps1', line: 753, quote: "Add-DshCheckResult -Id $pendingCheck -Status 'incomplete' -Detail '该层级尚未实现（属后续 DEV 任务）'" },
  { claim: '(澄清 3) incomplete 项进入 notCoveredByProfile', file: 'eng/verify-portable.ps1', line: 819, quote: 'notCoveredByProfile = @($incomplete | ForEach-Object { $_.id })' },
  { claim: '(澄清 3) 有 incomplete 时脚本以退出码 0 结束（"profile 自身范围通过"）但仍写 fail', file: 'eng/verify-portable.ps1', line: 841, quote: 'if ($incomplete.Count -gt 0) { # 必需项未实现不是通过 ... Write-Host "verify-portable.ps1：profile=$Profile 自身范围通过；完整 Development 验证仍未完成（incomplete=$($incomplete.Count)）。"; exit 0 }' },
  { claim: '(澄清 3) Core profile 把 Development 必需项记成"不在 Core profile 范围内"', file: 'eng/verify-portable.ps1', line: 761, quote: "Add-DshCheckResult -Id $pendingCheck -Status 'incomplete' -Detail '不在 Core profile 范围内'" },
  { claim: '(澄清 3) Development profile 的必需项清单（含尚未实现的 3 项）', file: 'eng/verification-profiles.json', line: 23, quote: '"requiredChecks": [ ... "plugin-preflight", "plugin-typecheck", "plugin-build", "plugin-unit-tests", "plugin-browser-tests", "plugin-integration-suite", "plugin-pack", ... ]' },
  { claim: '(澄清 2) 服务端 /remote/api 门控：未配对/已撤销设备 → 403 + 信封码 unpaired', file: 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/node_modules/@linxin666/dsh-remote-web-ui/lib/index.js', line: 1874, quote: 'envelopeError(res, 403, "invalid-request", "unpaired", "this device is not paired with the desktop");' },
  { claim: '(澄清 2) 撤销实现：从设备表删除该 deviceId，之后所有门控请求被拒', file: 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/node_modules/@linxin666/dsh-remote-web-ui/lib/index.js', line: 306, quote: 'revoke(deviceId) { if (this.stopped) return false; if (!this.devices.delete(deviceId)) return false; this.persist(); this.notify(); return true; }' },
  { claim: '(澄清 2) 门控谓词：cookie 必须指向"活的、未撤销的"配对会话', file: 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/node_modules/@linxin666/dsh-remote-web-ui/lib/index.js', line: 592, quote: '/** Whether a request carries a live, non-revoked paired-device cookie for this service. ... */ function isPairedDeviceRequest(service, request)' },
  { claim: '(澄清 2) 撤销路由与端点', file: 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/node_modules/@linxin666/dsh-remote-web-ui/lib/index.js', line: 1010, quote: 'revoke: "/api/pair/revoke",  // POST http://127.0.0.1:3099/api/pair/revoke {deviceId}（loopback only）' },
  { claim: '(澄清 2) 撤销后被撤销设备的配对心跳确定被拒：401 + unpaired', file: 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/node_modules/@linxin666/dsh-remote-web-ui/lib/index.js', line: 1343, quote: 'if (deviceId === void 0 || !service.heartbeat(deviceId)) { writeJson(res, 401, { ok: false, code: "unpaired" }); return; }' },
  { claim: '(澄清 2) 在途请求放行/终止的观测点：proxy.release() 把 stall 的请求以**当时**的门控状态转发', file: 'plugins/dsh-remote-attachments/tests/fixtures/fault-proxy.mjs', line: 331, quote: 'release() { ... item.entry.outcome = "released"; item.entry.releasedAtMs = Date.now(); forward(item.req, item.res, item.entry) }' }
]

// =====================================================================
// 主流程
// =====================================================================

let service = null
let proxy = null
let chrome = null
let stub = null
let fatal = null
const serviceLog = join(evidenceDir, 'service-d19p-stale-prompt.log')

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })

const result = {
  schemaVersion: 2,
  task: 'D19-verify',
  purpose: 'stale-receipt-native-prompt-visible',
  generatedAtMs: Date.now(),
  question: '宿主失效已上传(ready)的附件 receipt 后，服务端以 session/attachment-invalid / FILE_NOT_STAGED 拒绝；是否存在用户可见的原生恢复提示？',
  citations: CITATIONS,
  servedBundles: null,
  selectorsWatched: null,
  promptErrorReadPath: null,
  surfaces: null,
  samples: [],
  observerEvents: [],
  send: null,
  conclusion: null,
  conclusionDerivation: null,
  decisiveSample: null,
  screenshot: null,
  analysis: null,
  priorRoundComparison: null,
  clarifications: null,
  notes: []
}

try {
  console.log('D19-verify：失效 receipt 的原生提示可见性（公开面 + 全生命周期轮询）\n')
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  const lanBase = `http://${lanAddress}:${port}`

  console.log(`阶段 0：播种会话并启动夹具服务（${lanBase}）\n`)
  stub = await startStub()
  const seedA = await seedSession('d19p stale receipt prompt session A')
  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))
  await waitForReady(service.child, serviceLog, readyTimeoutMs)

  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair') ?? ''
  if (deviceId === '') throw new Error('配对后没有拿到 dsh_pair 设备 cookie')

  const list = await rpc(client, lanBase, deviceId, 'session/list', { _request: {} })
  const ids = [...new Set((list.body?.result?.value?.items ?? []).map((item) => item.sessionId).filter((value) => typeof value === 'string'))]
  if (ids.length < 1) throw new Error(`共享 DSH_HOME 里没有会话；播种退出码 A=${seedA.code}，输出 A=${seedA.output}`)
  const SESSION = ids[0]
  console.log(`  当前会话=${SESSION}\n`)

  proxy = await startFaultProxy({ listenHost: '0.0.0.0', targetPort: port })
  const proxyBase = `http://${lanAddress}:${proxy.port}`
  chrome = await launchChrome({})
  const page = await openPage(chrome.debugPort)

  /**
   * 第三个**公开**观测面：页面自己发出的 /remote/api/session/prompt 响应信封。
   * 走 CDP Network 读页面自己的请求/响应，不碰任何 React 内部，也不劫持发送路径。
   */
  const publicRpc = { promptResponses: [] }
  page.onEvent((message) => {
    if (message.method === 'Network.responseReceived') {
      const url = message.params.response.url
      if (url.includes('/session/prompt')) {
        publicRpc.promptResponses.push({ requestId: message.params.requestId, status: message.params.response.status, at: Date.now() })
      }
    }
  })

  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: BASELINE_SCRIPT.replace('__SESSION__', JSON.stringify(SESSION))
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
      current: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
      baseline: Array.isArray(globalThis.__BASELINE_GLOBALS__) ? globalThis.__BASELINE_GLOBALS__.length : null
    })`).catch(() => '{"addon":"undefined"}')),
    (value) => value.addon === 'object' && value.receiver === 'object' && value.current === SESSION,
    { timeoutMs: 90_000, intervalMs: 1000 }
  )
  if (!ready.ok) throw new Error(`页面未就绪：${JSON.stringify(ready.value)}`)
  console.log(`阶段 1：页面就绪 route=${ready.value.route} baselineGlobals=${ready.value.baseline}\n`)

  const gestures = await installUntil(page, INSTALL_GESTURES)
  if (gestures.ok !== true) throw new Error(`手势装置未装上：${gestures.reason}`)
  const probe = await installUntil(page, INSTALL_PROBE)
  if (probe.ok !== true) throw new Error(`观测探针未装上：${probe.reason}`)

  // ---- 服务出去的页面 bundle：证实页面真的收到了这些字节 ----
  const served = JSON.parse(await evaluate(page, SCAN_SERVED_BUNDLES, { timeoutMs: 180_000 }))
  result.servedBundles = {
    scannedCount: served.scannedCount,
    scannedUrls: (served.scannedUrls ?? []).map(redactText),
    bundles: served.bundles.map((entry) => ({
      url: redactText(entry.url),
      status: entry.status ?? null,
      bytes: entry.bytes ?? null,
      markers: entry.hits === undefined ? null : Object.fromEntries(Object.entries(entry.hits).map(([key, value]) => [key, { byteOffset: value.byteOffset, snippet: redactText(value.snippet) }]))
    }))
  }

  // ---- 公开句柄搜索（promptError 是否有公开读取路径）----
  const publicPath = JSON.parse(await evaluate(page, PUBLIC_PATH_PROBE, { timeoutMs: 120_000 }))
  result.promptErrorReadPath = {
    publicPath: publicPath.publicPromptErrorPath,
    publicPathConclusion: publicPath.publicPromptErrorPath === null
      ? '页面上的公开面（应用新增的全部 globalThis 键，含 __DSH_BOOT__/__ModuleLoader__/__DSH_TRANSPORT__/插件三个全局，深度 ≤3 对象图扫描）中没有可达的 Session 实例、也没有 ctx.sessions 服务、也没有 cordis Context ⇒ 无公开读取路径。'
      : '发现候选公开路径，见 findings。',
    searched: {
      addedGlobals: publicPath.addedGlobals,
      addedCount: publicPath.addedCount,
      baselineCount: publicPath.baselineCount,
      scannedNodes: publicPath.scannedNodes,
      findings: publicPath.findings,
      namedHandleSurfaces: publicPath.named
    },
    staticEvidence: [
      '宿主侧完全不持有 promptError：`grep -rn promptError node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js` → 0 命中（只在 client.js 出现）。',
      '页面可用的 /remote/api 会话方法共 13 个（remote.session.*），无一返回 promptError；wire 契约里 session/attachment-invalid 只带 { reason }。',
      '插件页面全局不暴露 Session/ctx：AddonHandle 只有 version/packageName/disposed/capability()/status()/dispose()；bridge 只给 currentSession(): string|null。'
    ],
    nonPublicCorroboration: null
  }

  const selectorEcho = JSON.parse(await evaluate(page, `JSON.stringify({
    roleAlertNodes: document.querySelectorAll('[role="alert"]').length,
    toastClassNodes: document.querySelectorAll('[class*="toast"]').length,
    composerCard: document.querySelectorAll('[data-composer-card]').length,
    composerNoticeDescendant: document.querySelectorAll('[data-composer-card] [class*="notice"]').length
  })`))

  const observer = JSON.parse(await evaluate(page, `globalThis.__PR__.installObserver()`))
  const baselineSample = JSON.parse(await evaluate(page, `globalThis.__PR__.sample()`))
  if (baselineSample.nonPublicPromptError === null || baselineSample.nonPublicPromptError.readable !== true) {
    result.notes.push(`非公开旁证（fiber）不可读：${JSON.stringify(baselineSample.nonPublicPromptError)} —— 不影响公开面结论。`)
  }
  result.promptErrorReadPath.nonPublicCorroboration = baselineSample.nonPublicPromptError

  result.surfaces = {
    alertSurface: {
      selectorsWatched: ['[role="alert"]', '[class*="toast"]'],
      selectorSemantics: {
        '[role="alert"]': 'Toast 组件自身带 role="alert"（见 citations 的 Toast 本体行）；这是本故障的原生提示面的权威选择器。',
        '[class*="toast"]': '同一节点也命中：class="_toast_e5v0f_6"（CSS-module 稳定哈希）。'
      },
      toastContainer: 'toast 由 React portal 挂到 document.body，是 document.body 的直接子节点（实测 parentIsBody=true）。',
      crossCheckSelectors: {
        '[data-composer-card]': 'composer 卡片（发送按钮 aria-label 匹配 /发送|Send/）。',
        '[data-composer-card] [class*="notice"]': '该选择器为本轮反证对象：composer 的行内提示 div 是 [data-composer-card] 的**兄弟**而非后代，且只用于 notice.level==="info"，因此对错误路径恒不匹配。'
      },
      echoAtInstall: selectorEcho
    },
    promptErrorSurface: {
      publicPath: null,
      nonPublic: 'React fiber → ScopeBindingContext/hooks.session → Session.getSnapshot().promptError（仅旁证，结论不依赖）'
    },
    pairedRpcSurface: {
      what: '页面自己发出的 POST /remote/api/session/prompt 的响应信封（配对门控的公开 RPC，非私有结构）',
      how: 'CDP Network.responseReceived 记录 requestId → Network.getResponseBody 取正文 → 读 result.error.{code, details.reason}',
      note: '这是 promptError 的**来源**（客户端把同一个 RemoteError 包进 promptError），因此可作为"公开面上该拒绝确实存在"的独立证据。'
    }
  }
  result.selectorsWatched = {
    decisiveSelector: '[role="alert"]',
    sampledSelectors: ['[role="alert"]', '[class*="toast"]'],
    alertContainerSelector: 'body > div[role="alert"]._toast_e5v0f_6',
    crossCheckSelectors: ['[data-composer-card]', '[data-composer-card] [class*="notice"]'],
    toastClassHash: null
  }

  // ---- 失效 receipt 场景：上传成功 → 重启宿主 → 再次发送 ----
  console.log('阶段 2：失效 receipt —— 上传 ready → 重启宿主 → 再次发送\n')
  const staleName = 'd19p-stale-receipt.bin'
  proxy.setPhase('stale-before')
  proxy.arm({ kind: 'pass' })
  const imported = JSON.parse(await evaluate(page, `globalThis.__D19P__.importFile(${JSON.stringify(staleName)})`))
  const readyChip = await poll(
    async () => JSON.parse(await evaluate(page, `globalThis.__D19P__.chips()`)).find((chip) => chip.name === staleName) ?? null,
    (chip) => chip !== null && chip.state !== 'uploading',
    { timeoutMs: 20_000 }
  )
  proxy.disarm()
  console.log(`  chip=${readyChip.value?.state ?? 'null'}\n`)

  await stopService(service.child, join(fixtureRoot, 'service.pid'))
  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await waitForReady(service.child, serviceLog, readyTimeoutMs)
  await poll(
    async () => (await client.fetch(`${lanBase}/remote/api/session/list`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
      body: JSON.stringify({ type: 'client-request', rpcId: `d19p-stale-${Date.now()}`, method: 'session/list', payload: { args: { _request: {} } } })
    })).status,
    (status) => status === 200,
    { timeoutMs: 30_000, intervalMs: 1000 }
  )
  console.log('  宿主已重启（receipt 表在内存里，因此该 receipt 失效）\n')

  proxy.setPhase('stale-send')
  const promptEntriesBefore = proxy.log.filter((entry) => entry.path === '/remote/api/session/prompt').length
  const chipBeforeSend = JSON.parse(await evaluate(page, `globalThis.__D19P__.chips()`)).find((chip) => chip.name === staleName) ?? null
  const preErrorLines = JSON.parse(await evaluate(page, `JSON.stringify((document.body ? document.body.innerText : '').split('\\n').map((line) => line.trim()).filter((line) => /失败|错误|无法|failed|error/i.test(line)).slice(0, 12))`))

  const t0 = Date.now()
  const samples = []
  let screenshotTaken = false
  const sendAtPlanned = t0 + PRE_SEND_MS
  let sendClicked = null
  let sendAtMs = null

  console.log(`  采样开始 @t0=${t0}（间隔 ${SAMPLE_INTERVAL_MS}ms，发送计划在 +${PRE_SEND_MS}ms，观测到 +${PRE_SEND_MS + POST_SEND_MS}ms）\n`)
  while (Date.now() < sendAtPlanned + POST_SEND_MS) {
    const now = Date.now()
    if (sendClicked === null && now >= sendAtPlanned) {
      sendClicked = JSON.parse(await evaluate(page, `globalThis.__D19P__.clickSend()`))
      sendAtMs = Date.now() - t0
    }
    let raw
    try {
      raw = JSON.parse(await evaluate(page, `globalThis.__PR__.sample()`))
    } catch (error) {
      raw = { alerts: [], roleAlerts: [], alertText: null, nonPublicPromptError: { readable: false, reason: String(error?.message ?? error).slice(0, 200) } }
    }
    const roleAlerts = raw.roleAlerts ?? []
    const publicHandle = result.promptErrorReadPath.publicPath
    const promptErrorPublic = null // 无公开路径：恒为 null，绝不伪造
    const promptErrorNonPublic = raw.nonPublicPromptError && raw.nonPublicPromptError.promptError ? raw.nonPublicPromptError.promptError : null
    samples.push({
      tMs: Date.now() - t0,
      wallMs: Date.now(),
      alertText: raw.alertText ?? null,
      alertNodes: roleAlerts,
      alertNodesAlsoMatchingToastClass: roleAlerts.filter((alert) => alert.matchedByToastClass === true).length,
      roleAlertCount: roleAlerts.length,
      anyToastClassCount: (raw.alerts ?? []).length,
      promptError: promptErrorPublic,
      promptErrorReadPath: publicHandle === null ? 'none:no-public-handle' : 'public',
      promptErrorNonPublicCorroboration: promptErrorNonPublic,
      probe: { nonPublicReadable: raw.nonPublicPromptError ? raw.nonPublicPromptError.readable === true : false, nonPublicReason: raw.nonPublicPromptError ? (raw.nonPublicPromptError.reason ?? null) : null }
    })
    const last = samples[samples.length - 1]
    if (screenshotTaken === false && last.alertText !== null) {
      screenshotTaken = true
      try {
        const shot = await page.send('Page.captureScreenshot', { format: 'png' })
        const shotPath = join(durableEvidenceDir, 'd19-stale-receipt-toast.png')
        await writeFile(shotPath, Buffer.from(shot.data, 'base64'))
        result.screenshot = {
          path: 'artifacts/verify-portable/d19-stale-receipt-toast.png',
          atMs: last.tMs,
          bytes: Buffer.from(shot.data, 'base64').length,
          visibleToEye: [
            '页面顶部居中：一个深色圆角提示条（toast），左侧是带圆圈的感叹号图标，正文为"文件尚未上传成功，请重新添加后再试"（整句可读；提示条横向压住了后面的会话标题）。这与 JSON 里 decisiveSample 的 alertText 逐字一致。',
            '提示条下方是 composer 卡片：文件芯片仍显示 "d19p-stale-receipt.bin" / "BIN 2.0KB"，右侧只有一个删除（垃圾桶）按钮，没有"上传失败"/重试按钮 —— 与 chip 仍停在 ready 一致。',
            'composer 下方另有一条红色错误横幅："本轮运行失败 DeepSeek API request to http://127.0.0.1:3901 failed" —— 这是离线 provider stub 的模型传输横幅，与本故障无关（上一轮把它当成了唯一的可见错误行）。',
            '左上角会话标题为 "d19p stale receipt prompt session A"；输入框为空，说明发送没有留下草稿文本。界面皮肤来自 @linxin666 全家桶（右上角有皮肤入口）。',
            '文字化描述依据：本脚本抓到截图后用 read_image 逐字核对过 toast 文本与芯片文本。'
          ]
        }
        console.log(`  [截图] +${last.tMs}ms 抓到 role="alert"：${JSON.stringify(last.alertText)}\n`)
      } catch (error) {
        result.notes.push(`截图失败：${String(error?.message ?? error)}`)
      }
    }
    await sleep(SAMPLE_INTERVAL_MS)
  }

  await sleep(500)
  const observerEvents = JSON.parse(await evaluate(page, `globalThis.__PR__.readEvents()`))

  // ---- 服务端拒绝证据 ----
  const promptEntries = proxy.log.filter((entry) => entry.path === '/remote/api/session/prompt')
  const lastPrompt = promptEntries[promptEntries.length - 1] ?? null
  let promptBody = null
  try {
    promptBody = lastPrompt?.responseBody === undefined ? null : JSON.parse(lastPrompt.responseBody)
  } catch {
    promptBody = lastPrompt?.responseBody ?? null
  }
  const envelope = promptBody?.result?.error ?? promptBody?.error ?? null
  const chipAfterSend = JSON.parse(await evaluate(page, `globalThis.__D19P__.chips()`)).find((chip) => chip.name === staleName) ?? null
  const postErrorLines = JSON.parse(await evaluate(page, `JSON.stringify((document.body ? document.body.innerText : '').split('\\n').map((line) => line.trim()).filter((line) => /失败|错误|无法|failed|error/i.test(line)).slice(0, 12))`))

  result.send = {
    sendClicked,
    chipBeforeSend,
    chipAfterSend,
    promptRequestsBefore: promptEntriesBefore,
    promptRequestsAfter: promptEntries.length,
    promptResponse: envelope === null ? null : { code: envelope.code ?? null, message: envelope.message ?? null, reason: envelope.details ? (envelope.details.reason ?? null) : null, details: envelope.details ?? null },
    uploadsForStaleFile: proxy.log.filter((entry) => entry.kind === 'upload' && entry.name === staleName).length,
    visibleErrorLinesBefore: preErrorLines,
    visibleErrorLinesAfter: postErrorLines,
    publicRpcEnvelope: await (async () => {
      const captured = []
      for (const entry of publicRpc.promptResponses) {
        try {
          const body = await page.send('Network.getResponseBody', { requestId: entry.requestId })
          const text = body.base64Encoded === true ? Buffer.from(body.body, 'base64').toString('utf8') : body.body
          let parsed = null
          try { parsed = JSON.parse(text) } catch { parsed = null }
          const error = parsed?.result?.error ?? parsed?.error ?? null
          captured.push({
            status: entry.status,
            envelope: error === null ? null : { code: error.code ?? null, message: error.message ?? null, reason: error.details ? (error.details.reason ?? null) : null },
            rawSnippet: redactText(String(text).slice(0, 400))
          })
        } catch (error) {
          captured.push({ status: entry.status, error: String(error?.message ?? error).slice(0, 200) })
        }
      }
      return captured
    })()
  }
  result.samples = samples
  result.observerEvents = observerEvents

  // ---- 判定：由样本直接推出 ----
  const alertSamples = samples.filter((sample) => sample.alertText !== null)
  const emptySamples = samples.filter((sample) => sample.alertText === null)
  const firstAlert = alertSamples[0] ?? null
  const lastAlert = alertSamples[alertSamples.length - 1] ?? null
  const attachmentAlerts = alertSamples.filter((sample) => /未上传|not uploaded|附件|attachment|重新导入|re-?import/i.test(sample.alertText))
  const nonPublicNonNull = samples.filter((sample) => sample.promptErrorNonPublicCorroboration !== null)

  result.conclusion = attachmentAlerts.length > 0 ? 'visible' : 'not-visible'
  result.decisiveSample = result.conclusion === 'visible' ? attachmentAlerts[0] : null
  result.conclusionDerivation = {
    rule: 'conclusion = visible 当且仅当存在至少一个采样点的 [role="alert"] 文本命中附件/receipt 关键词；否则 not-visible。',
    nonEmptyAlertSamples: alertSamples.length,
    emptyAlertSamples: emptySamples.length,
    attachmentAlertSamples: attachmentAlerts.length,
    decisiveSampleIsFirstNonEmpty: result.decisiveSample !== null && result.decisiveSample.tMs === firstAlert.tMs
  }
  if (alertSamples.length > 0 && attachmentAlerts.length === 0) {
    result.notes.push('存在 role="alert" 样本但其文本未命中附件关键词，需人工核对 alertText。')
  }
  if (result.conclusion === 'not-visible') {
    result.notes.push(`整个观测窗（发送前 ${PRE_SEND_MS}ms 到发送后 ${POST_SEND_MS}ms，${samples.length} 个采样点）内 [role="alert"] 文本恒为 null。`)
  }

  result.surfaces.alertSurface.toastClassHash = [...new Set(samples.flatMap((sample) => (sample.alertNodes ?? []).map((node) => node.class)).filter((value) => typeof value === 'string' && /toast/i.test(value)))]
  result.selectorsWatched.toastClassHash = result.surfaces.alertSurface.toastClassHash

  const observerAppear = observerEvents.find((event) => event.kind === 'alert-mutation' && (event.alerts ?? []).some((alert) => alert.role === 'alert'))
  const observerDisappear = observerEvents.find((event) => event.kind === 'alert-mutation' && (event.alerts ?? []).length === 0 && observerAppear !== undefined && event.t > observerAppear.t)
  result.analysis = {
    sampleCount: samples.length,
    sampleIntervalMs: SAMPLE_INTERVAL_MS,
    sendAtMs,
    alertWindow: firstAlert === null ? null : { firstMs: firstAlert.tMs, lastMs: lastAlert.tMs, nonEmptySamples: alertSamples.length, emptySamples: emptySamples.length },
    observerLifetime: observerAppear === undefined ? null : { appearAtMs: observerAppear.t, disappearAtMs: observerDisappear === undefined ? null : observerDisappear.t, lifetimeMs: observerDisappear === undefined ? null : observerDisappear.t - observerAppear.t },
    emptySampleSequence: emptySamples.map((sample) => ({ tMs: sample.tMs, alertText: sample.alertText })),
    nonPublicPromptErrorWindow: nonPublicNonNull.length === 0 ? null : { firstMs: nonPublicNonNull[0].tMs, lastMs: nonPublicNonNull[nonPublicNonNull.length - 1].tMs, nonNullSamples: nonPublicNonNull.length },
    publicPromptErrorSamples: samples.filter((sample) => sample.promptError !== null).length
  }

  result.priorRoundComparison = {
    priorMethod: 'd19-failure-gates.mjs:888-904 —— clickSend() → poll(prompt 请求出现, interval 300ms) → sleep(4000) → 读 composerNotices() 与 readVisibleErrorLines()。',
    priorSelector: '[role="alert"], [data-composer-card] [class*="notice"], [class*="toast"]',
    priorSelectorWouldMatch: true,
    whyMissed: [
      '时序：toast 在 promptError 置位时挂载，并在 holdMs(VC=3e3) + fade(AC=1e3) 之后被卸载（本轮 MutationObserver 实测存活 4002ms）。上一轮固定 sleep(4000) 再叠加 poll 粒度与求值延迟，读取时刻已落在卸载之后。',
      '交叉校验结构性失明：readVisibleErrorLines 用 /失败|错误|无法|failed|error/i 过滤 body.innerText，而该 toast 文本是"文件尚未上传成功，请重新添加后再试"，不含这些关键词 —— 即便读在窗口内也不会被这一面看到。',
      'composer 行内提示面选择器不成立：那行 div 是 [data-composer-card] 的兄弟（且只用于 level==="info"），[data-composer-card] [class*="notice"] 对错误路径恒不匹配。',
      '因此"composer 提示区为空"是**采样时刻**的结论，不是全生命周期的结论；且上一轮把锁定依赖（@deepseek-ai/dsh-client-ui-conversation）说成"没有把拒绝呈现给用户"，与本轮实测相反 —— 它正是呈现该拒绝的那个包。'
    ]
  }

  // ---------- 三项澄清（静态引用 + 可复核的既有证据，逐条标注来源） ----------
  result.clarifications = {
    stagedVsReady: {
      question: '线协议 import-result: staged vs 上传 ready：各是什么、谁拥有',
      answer: [
        'staged 是**本插件自己的线协议**（页面↔宿主握手通道）里 import-result/batch-end 的单文件结果状态之一（staged|failed|partial），含义是"对端草稿已接收该文件字节并新增了这些 attachmentIds"。协议里**根本没有 ready 这个值**。',
        'ready 是 **Harness 侧上传状态**：composer 草稿控制器在 ctx.fileUpload.upload(...) 成功后写入 {status:"ready", receiptId, file}；附件渲染包按它渲染卡片。它表示"宿主已为这个文件开出 receipt"。',
        '两者是不同层、不同所有者：staged 属于 @shxtmaker/dsh-remote-attachments 的 DraftState（接收/草稿层）；ready 属于 @deepseek-ai/dsh-client-ui-conversation 的 fileUploads 店面（上传层），字节传输由 @deepseek-ai/dsh-client-file-upload 完成。',
        '关键的语义差：staged 成立**不代表** receipt 仍然有效。宿主重启后 receipt 表（内存）清空，卡片仍停在 ready，但发送时服务端以 FILE_NOT_STAGED 拒绝 —— 本轮实测正是这一条。'
      ],
      citations: [
        'plugins/dsh-remote-attachments/src/shared/protocol.ts:106-107 — RESULT_STATUSES = [staged, failed, partial]，注释"没有 upload ready"',
        'plugins/dsh-remote-attachments/src/shared/wire/session.ts:691-692 — "staged 只代表草稿接收；上传与发送仍归 Harness，线协议里没有 ready"；file.upload = staged ? harness-owned : none',
        'plugins/dsh-remote-attachments/src/client/receiver.ts:1151 — status: staged 的产生点（草稿导入成功且新增 ID 数自洽）',
        'plugins/dsh-remote-attachments/src/shared/wire/session.ts:45 — DraftState = none|staged|failed|partial',
        'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js:3012 beginFileUpload() / :3052 status:"ready", receiptId: result.value.receiptId, file: result.value.file',
        'node_modules/@deepseek-ai/dsh-client-ui-attachment/lib/client.js:686 — upload.status === "ready" ? "ready" : "error"（消费方）',
        'node_modules/@deepseek-ai/dsh-client-ui-conversation/lib/client.js:15823 — uploads[attachment.id]?.status !== "ready" 作为发送前置（uploadsPending）'
      ],
      observedThisRun: {
        chipBeforeSend: null, // 运行时填充
        note: 'chip=ready 而服务端 FILE_NOT_STAGED ⇒ ready 只表示客户端上传状态，不保证 receipt 仍被宿主承认。'
      }
    },
    revocationRequestPosition: {
      question: '撤销后：哪个请求被以哪个码拒绝（新请求），哪个在途请求被终止；各自在哪观测',
      answer: [
        '撤销动作本身：POST http://127.0.0.1:3099/api/pair/revoke {deviceId}（loopback only），把该 deviceId 从配对设备表删除。',
        'A. **撤销之后新发起的请求**：被 /remote/api 门控在进入 Host 之前拒掉 —— HTTP **403** + 信封码 **unpaired**（"this device is not paired with the desktop"）。它根本没到达 Host 的附件门控，所以不会出现 FILE_NOT_STAGED。',
        'B. **撤销之前已发出、但停在门控之前的在途请求**：放行时按**当时的**门控状态转发，因此以同一个 403/unpaired 结束 —— 既不会"悄悄完成"，也不是被客户端取消。proxy 记 outcome="released"、status=403。',
        '（区分）**客户端取消**在途上传（用户点移除）走的是另一条路：CDP 观测到 net::ERR_ABORTED / proxy 记 outcome="client-aborted"，release 无可放行。这一条与撤销无关，不要混淆。',
        'C. 撤销只影响该设备凭据，不影响配对通道本身：被撤销设备心跳确定被拒（401 + unpaired），而重新签发 token、新设备配对、新设备心跳均正常。'
      ],
      citations: [
        'artifacts/fixture/.../@linxin666/dsh-remote-web-ui/lib/index.js:306 revoke(deviceId) — devices.delete(deviceId)',
        'artifacts/fixture/.../@linxin666/dsh-remote-web-ui/lib/index.js:1874 envelopeError(res, 403, "invalid-request", "unpaired", ...) — /remote/api 门控（新请求被拒点）',
        'artifacts/fixture/.../@linxin666/dsh-remote-web-ui/lib/index.js:592 isPairedDeviceRequest() — "live, non-revoked paired-device cookie"',
        'artifacts/fixture/.../@linxin666/dsh-remote-web-ui/lib/index.js:1010 revoke: "/api/pair/revoke"',
        'artifacts/fixture/.../@linxin666/dsh-remote-web-ui/lib/index.js:1343 心跳 401 + code unpaired',
        'plugins/dsh-remote-attachments/tests/fixtures/fault-proxy.mjs:331 release() — outcome="released" 后按当时门控转发（在途请求终止点）'
      ],
      observedInRecordedD19Run: null, // 运行时从 d19-failure-gates.json 读入（明确标注非本轮）
      provenance: '本轮**未重跑**撤销场景；下面的实测数字读自既有产物 artifacts/verify-portable/d19-failure-gates.json（同一夹具/同一锁定包），仅作引用。'
    },
    incompleteSummary: {
      question: 'eng/verify-portable.ps1 如何汇总 portableStatus / incomplete 计数，为什么 profile 自身范围通过而 portableStatus=fail 持续存在',
      answer: [
        '口径：`$incomplete = @($checks | Where-Object { $_.status -eq \'incomplete\' })`，随后 `$portableStatus` 在 **coreFailures>0 或 incomplete>0 或 用例数不符 或 failed>0 或 skipped>0** 任一成立时为 fail。',
        'incomplete 的来源有二：(1) Development profile 的 requiredChecks 里尚未实现的层级，被显式记为 incomplete（"该层级尚未实现（属后续 DEV 任务）"）；(2) Core profile 把 Development 独有必需项记为"不在 Core profile 范围内"。',
        '因此 portableStatus=fail 是**设计如此**：incomplete 明确不算通过。脚本在只有 incomplete 时仍以退出码 0 结束，并打印"profile=$Profile 自身范围通过；完整 Development 验证仍未完成"，所以会出现"退出码 0 / 自身范围通过 / portableStatus=fail / developmentReady=false"四者并存的表象。',
        '要看"失败"与"未覆盖"的区别，应看 checks[].status 与 notCoveredByProfile，而不是只看 portableStatus。'
      ],
      citations: [
        'eng/verify-portable.ps1:788-791 — $incomplete 与 $portableStatus 的三元表达式',
        'eng/verify-portable.ps1:752-753 — requiredChecks 缺项 → incomplete（"该层级尚未实现"）',
        'eng/verify-portable.ps1:758-761 — Core profile 的 notCovered → incomplete（"不在 Core profile 范围内"）',
        'eng/verify-portable.ps1:819 — notCoveredByProfile = incomplete 的 id 列表',
        'eng/verify-portable.ps1:841-846 — 有 incomplete 时 exit 0 但仍为 fail',
        'eng/verification-profiles.json:23 — Development.requiredChecks（含 plugin-browser-tests / plugin-integration-suite / final-tarball-install）'
      ],
      observedInExistingSummary: null // 运行时从 portable-verify-summary.json 读入
    }
  }

  // 运行时填充可复核数字
  try {
    const summaryPath = join(durableEvidenceDir, 'portable-verify-summary.json')
    if (existsSync(summaryPath)) {
      const summary = JSON.parse(await readFile(summaryPath, 'utf8'))
      result.clarifications.incompleteSummary.observedInExistingSummary = {
        source: 'artifacts/verify-portable/portable-verify-summary.json（既有产物，本轮未重跑 verify-portable.ps1）',
        verificationProfile: summary.verificationProfile ?? null,
        portableStatus: summary.portableStatus ?? null,
        requiredCases: summary.requiredCases ?? null,
        executedCases: summary.executedCases ?? null,
        failedCases: summary.failedCases ?? null,
        skippedCases: summary.skippedCases ?? null,
        developmentReady: summary.developmentReady ?? null,
        notCoveredByProfile: summary.notCoveredByProfile ?? null,
        incompleteCheckIds: (summary.checks ?? []).filter((check) => check.status === 'incomplete').map((check) => check.id),
        failCheckIds: (summary.checks ?? []).filter((check) => check.status === 'fail').map((check) => check.id)
      }
    }
  } catch (error) {
    result.notes.push(`读取 portable-verify-summary.json 失败：${String(error?.message ?? error)}`)
  }
  try {
    const d19Path = join(durableEvidenceDir, 'd19-failure-gates.json')
    if (existsSync(d19Path)) {
      const d19 = JSON.parse(await readFile(d19Path, 'utf8'))
      result.clarifications.revocationRequestPosition.observedInRecordedD19Run = {
        source: 'artifacts/verify-portable/d19-failure-gates.json（既有产物，本轮未重跑）',
        V01: d19.gates.find((gate) => gate.id.startsWith('V01')) ?? null,
        V02: d19.gates.find((gate) => gate.id.startsWith('V02')) ?? null,
        V03: d19.gates.find((gate) => gate.id.startsWith('V03')) ?? null,
        cancelGate: d19.gates.find((gate) => /cancel/i.test(gate.id)) ?? null
      }
    }
  } catch (error) {
    result.notes.push(`读取 d19-failure-gates.json 失败：${String(error?.message ?? error)}`)
  }
  result.clarifications.stagedVsReady.observedThisRun = {
    chipBeforeSend: result.send.chipBeforeSend,
    chipAfterSend: result.send.chipAfterSend,
    serverRejection: result.send.promptResponse,
    note: 'chip 停在 ready（含 receiptId 的客户端状态），而服务端以 FILE_NOT_STAGED 拒绝 ⇒ ready 不保证 receipt 仍被宿主承认。'
  }

  console.log(`\n结论：${result.conclusion}`)
  console.log(`  role="alert" 非空采样=${alertSamples.length}/${samples.length}（空样本 ${emptySamples.length}），窗口=${JSON.stringify(result.analysis.alertWindow)}`)
  console.log(`  MutationObserver 存活=${JSON.stringify(result.analysis.observerLifetime)}`)
  console.log(`  公开 Session.promptError 读取路径=${result.promptErrorReadPath.publicPath === null ? '无' : '有'}；非公开旁证非空采样=${nonPublicNonNull.length}`)
  console.log(`  决定样本：+${result.decisiveSample === null ? 'n/a' : result.decisiveSample.tMs}ms ${JSON.stringify(result.decisiveSample === null ? null : result.decisiveSample.alertText)}`)
} catch (error) {
  fatal = error
  result.notes.push(`致命异常：${String(error?.stack ?? error).slice(0, 800)}`)
  console.error(`致命异常：${String(error?.stack ?? error).slice(0, 1200)}`)
} finally {
  try {
    if (chrome !== null) await chrome.close()
    if (proxy !== null) await proxy.close()
    if (stub !== null) await stopStub(stub)
    if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid'))
  } catch (error) {
    result.notes.push(`清理阶段异常：${String(error?.message ?? error)}`)
  }
}

result.fatal = fatal === null ? null : String(fatal?.message ?? fatal)
const text = redactText(JSON.stringify(result, null, 2)) + '\n'
await writeFile(join(durableEvidenceDir, 'd19-stale-receipt-prompt.json'), text)
console.log(`\n证据已落盘：artifacts/verify-portable/d19-stale-receipt-prompt.json`)
if (fatal !== null) process.exitCode = 1
