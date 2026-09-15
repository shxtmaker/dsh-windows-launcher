#!/usr/bin/env node
/**
 * D21 判据：背压与资源清理边界的**采样**（真实 Chromium + 真实已构建插件 + 真实可移植 Core）。
 *
 * 判据先冻结在 `d21-resource-criteria.mjs`（计量项 / 单位 / 采样点 / 阈值），本文件只负责采集：
 *
 *   1. **浏览器层**：在真实夹具页面里用已构建插件的生产接收端（`__DSH_ATTACHMENTS_RECEIVER__`）
 *      跑最大文件（20 MiB）、最大批次（10 文件）、超限拒绝、取消、暂停消费者与**连续 30 轮**；
 *      每轮用 CDP 强制 GC 后读 JS 堆（`HeapProfiler.collectGarbage` + `Runtime.getHeapUsage`）。
 *      组件依赖（origin / 当前会话 / 能力）沿用插件真实装配；导入走真实桥 → 生产草稿适配器，
 *      "导入真的发生"由桥的 `previous` 原生 attachmentIds 回读控制。
 *   2. **Core 层**：`dotnet run` 真实可移植 Core 的 D21 用例集（`-trait d21=resources`），
 *      用例把实测值写成 `DSH_D21_CORE_METRICS_DIR` 下的 JSON，本脚本逐条并入报告。
 *   3. **单元层**：`runDedupProbes()` 用已构建 `lib/` 的去重缓存 + 注入时钟做确定性 TTL/容量探测。
 *
 * 证据布局（D19/D20 同款，只增不改）：每次运行一个 `d21-runs/<runId>/`，顶层
 * `d21-resource-report.json` / `d21-resource-gates.json` 只是**最新一次**的镜像。
 *
 * 反例控制（"这条阈值真的能失败吗"）见报告 controls 与负向探针：
 *   - 逐行荒谬阈值自检（`falsifiabilitySelfTest`）；
 *   - 序列控制 N3a/N3b/N3c：无界增长与缓慢泄漏必须被判失败、有界抖动必须通过；
 *   - 冻结时钟控制 N4/N5：时钟不推进时 TTL 条目必须仍在；
 *   - 真·无界路径控制 N6：浏览器里每轮保留 1 MiB（30 轮）走同一采样与判定，必须被判失败；
 *   - 超限控制 B07/B08/B14：超批/超文件/超出在途窗口必须被拒绝。
 */

import { execFileSync, spawn, spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync } from 'node:fs'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { release as osRelease } from 'node:os'

import { createClient, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'
import {
  D21_CRITERIA_VERSION,
  LIMITS,
  buildRatioRow,
  buildRow,
  computeVerdicts,
  falsifiabilitySelfTest,
  judgeSeries,
  runDedupProbes,
  runSeriesNegativeControls
} from './d21-resource-criteria.mjs'

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
const coreProject = join(repoRoot, 'tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj')
const skipCore = process.env.DSH_D21_SKIP_CORE === '1'

/** dotnet 解析：显式覆盖 → PATH → $HOME/.dotnet（本机 SDK 装在后者，见 /tmp/dsh-env.sh）。 */
function resolveDotnet() {
  if (process.env.DSH_DOTNET !== undefined && process.env.DSH_DOTNET !== '') return process.env.DSH_DOTNET
  const onPath = spawnSync('bash', ['-lc', 'command -v dotnet || true'], { encoding: 'utf8' }).stdout.trim()
  if (onPath !== '') return onPath
  const local = join(process.env.HOME ?? '', '.dotnet/dotnet')
  return existsSync(local) ? local : 'dotnet'
}
const dotnet = resolveDotnet()

const sleep = (ms) => new Promise((resolvePromise) => setTimeout(resolvePromise, ms))
/** 与页面侧生成器同式的确定性字节：`(i*31+7)%251`（页面按 offset 现生成，不传 20 MiB 走 CDP）。 */
const patternBytes = (size, offset = 0) => {
  const buffer = Buffer.allocUnsafe(size)
  for (let index = 0; index < size; index += 1) buffer[index] = ((offset + index) * 31 + 7) % 251
  return buffer
}
const sha256 = (buffer) => createHash('sha256').update(buffer).digest('hex')

// ---------- 运行身份 / 证据目录（每次运行独立 runId；历史只增不改） ----------

const RUN_STARTED_AT_MS = Date.now()
const RUN_ID = `d21-${new Date(RUN_STARTED_AT_MS).toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`
const RUN_COMMAND = `node ${process.argv[1] ?? 'tests/fixtures/d21-resource-gates.mjs'}${process.argv.slice(2).length === 0 ? '' : ` ${process.argv.slice(2).join(' ')}`}`
const RUNS_DIR = join(durableEvidenceDir, 'd21-runs')
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

const sha256OfFile = (path) => {
  try {
    return createHash('sha256').update(readFileSync(path)).digest('hex')
  } catch {
    return null
  }
}

/** 插件源码树指纹（该目录未入库，工作树改动不体现在 HEAD 上）。 */
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
  let installedPlugin = null
  try {
    const manifest = JSON.parse(
      readFileSync(join(dshHome, `profiles/${fixtureProfile}/node_modules/@shxtmaker/dsh-remote-attachments/package.json`), 'utf8')
    )
    installedPlugin = { version: manifest.version ?? null }
  } catch {
    installedPlugin = null
  }
  return {
    gitHead: git(['rev-parse', 'HEAD']),
    gitBranch: git(['rev-parse', '--abbrev-ref', 'HEAD']),
    gitWorktree: {
      dirtyEntries: porcelain === '' ? 0 : porcelain.split('\n').filter((line) => line !== '').length,
      porcelainSha256: createHash('sha256').update(porcelain).digest('hex')
    },
    pluginVersion,
    installedPlugin,
    pluginSourceTree: sourceTreeSha256(join(pluginRoot, 'src')),
    criteriaVersion: D21_CRITERIA_VERSION,
    hashes: {
      gateScript: sha256OfFile(fileURLToPath(import.meta.url)),
      criteriaModule: sha256OfFile(join(here, 'd21-resource-criteria.mjs'))
    }
  }
}

// ---------- 证据收集 ----------

const gates = []
const negativeProbes = []
const notRun = []
const observations = {}
const rows = []
const series = {}

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
  console.log(`  [n/r] ${id} ${description} — ${reason}`)
}

/** 指标行 → 一条判据（明细即记录：整体结果就是所有记录的全称量化）。 */
function pushRow(row) {
  rows.push(row)
  gate(
    `D21-${row.id}`,
    `${row.metric}｜单位=${row.unit}｜采样点=${row.samplingPoint}｜阈值=${row.threshold}`,
    row.pass === true,
    `实测=${row.measured === null ? '(未采样)' : row.measured}${row.detail === undefined ? '' : ` — ${row.detail}`}`
  )
  return row
}

// ---------- 页面侧装置 ----------

const INSTALL_HARNESS = `(() => {
  const host = globalThis.__DSH_ATTACHMENTS_RECEIVER__
  if (host === undefined || host === null) return JSON.stringify({ ok: false, reason: 'no-receiver-host' })
  const state = { receivers: {} }
  globalThis.__D21__ = state

  // 确定性字节：与 Node 侧 patternBytes 同式（(i*31+7)%251）；页面按 offset 现生成，不传字节。
  const generate = (offset, length) => {
    const out = new Uint8Array(length)
    for (let index = 0; index < length; index += 1) out[index] = ((offset + index) * 31 + 7) % 251
    return out
  }
  const base64 = (bytes) => {
    let binary = ''
    const step = 0x8000
    for (let index = 0; index < bytes.length; index += step) binary += String.fromCharCode.apply(null, bytes.subarray(index, index + step))
    return btoa(binary)
  }
  const plans = (byteLength, chunkBytes) => {
    const list = []
    for (let offset = 0, seq = 0; offset < byteLength; seq += 1) {
      const size = Math.min(chunkBytes, byteLength - offset)
      list.push({ seq, offset, byteLength: size })
      offset += size
    }
    return list
  }

  state.make = (name, options) => {
    state.receivers[name] = host.create(options === undefined ? {} : options)
    return JSON.stringify({ ok: true, name })
  }
  state.accounting = (name) => JSON.stringify(state.receivers[name].accounting)
  state.fileResults = (name) => JSON.stringify(state.receivers[name].fileResults)
  state.hostInfo = () => JSON.stringify({
    version: host.version,
    wiring: host.wiring,
    globals: {
      bridgeVersion: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.version ?? null,
      currentSession: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null,
      secureContext: globalThis.isSecureContext === true,
      subtle: typeof globalThis.crypto?.subtle
    }
  })

  const drainInto = async (receiver, stats) => {
    for (const message of await receiver.drainOutgoing()) {
      if (message.type === 'ack') {
        stats.acks += 1
        if (typeof message.inFlight === 'number') stats.inFlightMax = Math.max(stats.inFlightMax, message.inFlight)
      }
      if (message.type === 'import-result') stats.importResults += 1
    }
  }

  const send = async (receiver, stats, object) => {
    const result = await receiver.acceptText(JSON.stringify(object))
    const entry = { type: object.type, ok: result.ok === true, code: result.ok === true ? null : result.code }
    stats.results.push(entry)
    return { result, entry }
  }

  const contextOf = (spec) => ({
    v: 1,
    type: 'context',
    sessionId: spec.sessionId,
    targetId: spec.targetId,
    documentEpoch: spec.documentEpoch,
    composerEpoch: spec.composerEpoch,
    composerScope: spec.composerScope
  })

  /** 完整跑一个批次（context → batch-begin → 每文件 file-begin + chunk×N + file-end → drain）。 */
  state.runBatch = async (name, spec) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    const drainEvery = spec.drainEvery === undefined ? 1 : spec.drainEvery
    const stats = { acceptedChunks: 0, rejectedChunks: 0, acks: 0, importResults: 0, inFlightMax: 0, results: [], peakBufferedBytes: 0, peakBufferedFiles: 0, ends: [] }
    if (spec.withContext === true) {
      const context = await send(receiver, stats, contextOf(spec))
      if (!context.result.ok) return JSON.stringify({ ok: false, reason: 'context:' + context.entry.code, stats })
      await drainInto(receiver, stats)
    }
    const begin = await send(receiver, stats, {
      v: 1,
      type: 'batch-begin',
      sessionId: spec.sessionId,
      batchId: spec.batchId,
      targetId: spec.targetId,
      documentEpoch: spec.documentEpoch,
      composerEpoch: spec.composerEpoch,
      fileCount: spec.files.length,
      totalBytes: spec.files.reduce((sum, file) => sum + file.byteLength, 0)
    })
    if (!begin.result.ok) return JSON.stringify({ ok: false, reason: 'batch-begin:' + begin.entry.code, stats })
    for (const file of spec.files) {
      const started = await send(receiver, stats, {
        v: 1,
        type: 'file-begin',
        sessionId: spec.sessionId,
        batchId: spec.batchId,
        fileId: file.fileId,
        name: file.name,
        mime: file.mime,
        byteLength: file.byteLength,
        sha256: file.sha256
      })
      if (!started.result.ok) return JSON.stringify({ ok: false, reason: 'file-begin:' + started.entry.code, stats })
      let sinceDrain = 0
      for (const plan of plans(file.byteLength, spec.chunkBytes)) {
        const chunk = await send(receiver, stats, {
          v: 1,
          type: 'chunk',
          sessionId: spec.sessionId,
          batchId: spec.batchId,
          fileId: file.fileId,
          seq: plan.seq,
          offset: plan.offset,
          byteLength: plan.byteLength,
          dataBase64: base64(generate(plan.offset, plan.byteLength))
        })
        if (chunk.result.ok) stats.acceptedChunks += 1
        else stats.rejectedChunks += 1
        sinceDrain += 1
        if (sinceDrain >= drainEvery) {
          sinceDrain = 0
          await drainInto(receiver, stats)
        }
      }
      await drainInto(receiver, stats)
      const ended = await send(receiver, stats, {
        v: 1,
        type: 'file-end',
        sessionId: spec.sessionId,
        batchId: spec.batchId,
        fileId: file.fileId,
        totalBytes: file.byteLength,
        sha256: file.sha256
      })
      await drainInto(receiver, stats)
      const record = receiver.fileRecord(file.fileId)
      stats.ends.push({
        fileId: file.fileId,
        ok: ended.result.ok === true,
        code: ended.result.ok === true ? null : ended.entry.code,
        transport: record === null ? null : record.transport,
        draft: record === null ? null : record.draft,
        attachmentIds: record === null ? [] : Array.from(record.attachmentIds),
        receivedBytes: record === null ? null : record.receivedBytes
      })
      stats.peakBufferedBytes = Math.max(stats.peakBufferedBytes, receiver.accounting.peakBufferedBytes)
      stats.peakBufferedFiles = Math.max(stats.peakBufferedFiles, receiver.accounting.peakBufferedFiles)
    }
    if (spec.closeBatch !== false) {
      // 生产语义：batch-end 由**发送端**（本夹具扮演对端）在收齐 import-result 之后发出，
      // 接收端据此关闭批次并解除钉住（D12 G08 同款）。不关闭则下一个 batch-begin 会被
      // 正确地拒为 batch-in-progress（这正是接收端的状态机规则）。
      const statuses = stats.ends.map((item) => item.draft)
      const overall = statuses.every((value) => value === 'staged') ? 'staged' : statuses.some((value) => value === 'staged') ? 'partial' : 'failed'
      const closed = await send(receiver, stats, {
        v: 1,
        type: 'batch-end',
        sessionId: spec.sessionId,
        batchId: spec.batchId,
        status: overall,
        results: stats.ends.map((item) => ({ fileId: item.fileId, status: item.draft, attachmentIds: item.attachmentIds }))
      })
      await drainInto(receiver, stats)
      stats.batchEnd = { ok: closed.result.ok === true, code: closed.result.ok === true ? null : closed.entry.code, status: overall }
    }
    stats.accounting = receiver.accounting
    return JSON.stringify(stats)
  }

  /** 只喂块、不 drain（暂停消费者）；返回每块的接受/拒绝码。 */
  state.feedChunks = async (name, spec) => {
    const receiver = state.receivers[name]
    if (receiver === undefined) return JSON.stringify({ ok: false, reason: 'no-receiver:' + name })
    const stats = { accepted: 0, rejected: [], results: [] }
    if (spec.withContext === true) {
      await send(receiver, stats, contextOf(spec))
      await drainInto(receiver, stats)
    }
    await send(receiver, stats, {
      v: 1,
      type: 'batch-begin',
      sessionId: spec.sessionId,
      batchId: spec.batchId,
      targetId: spec.targetId,
      documentEpoch: spec.documentEpoch,
      composerEpoch: spec.composerEpoch,
      fileCount: 1,
      totalBytes: spec.byteLength
    })
    await send(receiver, stats, {
      v: 1,
      type: 'file-begin',
      sessionId: spec.sessionId,
      batchId: spec.batchId,
      fileId: spec.fileId,
      name: spec.name,
      mime: spec.mime,
      byteLength: spec.byteLength,
      sha256: spec.sha256
    })
    for (const plan of plans(spec.byteLength, spec.chunkBytes)) {
      const chunk = await send(receiver, stats, {
        v: 1,
        type: 'chunk',
        sessionId: spec.sessionId,
        batchId: spec.batchId,
        fileId: spec.fileId,
        seq: plan.seq,
        offset: plan.offset,
        byteLength: plan.byteLength,
        dataBase64: base64(generate(plan.offset, plan.byteLength))
      })
      if (chunk.result.ok) stats.accepted += 1
      else stats.rejected.push(chunk.entry.code)
    }
    stats.accounting = receiver.accounting
    stats.bufferedBytes = receiver.bufferedBytes
    return JSON.stringify(stats)
  }

  /** 恢复消费者：一次 drain 取走暂停期间积压的所有待发消息。 */
  state.resume = async (name) => {
    const receiver = state.receivers[name]
    const stats = { acks: 0, inFlightMax: 0, importResults: 0 }
    await drainInto(receiver, stats)
    stats.accounting = receiver.accounting
    return JSON.stringify(stats)
  }

  /** 传输中途取消（线协议 cancel）。 */
  state.cancel = async (name, spec) => {
    const receiver = state.receivers[name]
    const stats = { results: [] }
    const cancelled = await send(receiver, stats, {
      v: 1,
      type: 'cancel',
      sessionId: spec.sessionId,
      batchId: spec.batchId,
      reason: 'cancelled',
      stage: 'protocol-transfer'
    })
    await drainInto(receiver, stats)
    stats.ok = cancelled.result.ok === true
    stats.code = cancelled.result.ok === true ? null : cancelled.entry.code
    stats.accounting = receiver.accounting
    return JSON.stringify(stats)
  }

  state.dispose = (name) => {
    const receiver = state.receivers[name]
    receiver.dispose('d21-dispose')
    return JSON.stringify(receiver.accounting)
  }

  /** 反例控制：故意保留 1 MiB/轮（真无界路径），用同一采样方式观察。 */
  state.leakRound = (rounds) => {
    globalThis.__D21_LEAK__ = globalThis.__D21_LEAK__ ?? []
    for (let index = 0; index < rounds; index += 1) {
      const block = new Uint8Array(1024 * 1024)
      block[0] = index % 251
      globalThis.__D21_LEAK__.push(block)
    }
    return JSON.stringify({ blocks: globalThis.__D21_LEAK__.length, bytes: globalThis.__D21_LEAK__.length * 1024 * 1024 })
  }
  state.releaseLeak = () => {
    const blocks = globalThis.__D21_LEAK__?.length ?? 0
    globalThis.__D21_LEAK__ = []
    return JSON.stringify({ releasedBlocks: blocks })
  }

  /** 真实草稿路径控制：导入一个探针文件并回读导入前的原生 attachmentIds。 */
  state.draftProbe = (label) => {
    const bridge = globalThis.__DSH_ATTACHMENTS_BRIDGE__
    if (bridge === undefined || bridge === null) return JSON.stringify({ ok: false, reason: 'no-bridge' })
    const bytes = new TextEncoder().encode('d21-probe-' + label)
    const file = new File([bytes], 'd21-probe.txt', { type: 'text/plain' })
    try {
      const result = bridge.importFiles({ files: [file] })
      return JSON.stringify({ ok: true, added: Array.from(result.added ?? []), previous: Array.from(result.previous ?? []) })
    } catch (error) {
      return JSON.stringify({ ok: false, reason: String(error && error.message ? error.message : error) })
    }
  }

  const info = JSON.parse(state.hostInfo())
  return JSON.stringify({ ok: true, host: info })
})()`

const evaluate = (page, expression) => page.evaluate(expression, { awaitPromise: true })

/**
 * 页面调用：默认把 `{ok:false}` 当异常（装置故障必须响亮）。
 * 负向探针（超限必须被拒）显式传 `allowFailure`：拒绝是**期望结果**，由判据记录。
 */
async function pageCall(page, expression, { allowFailure = false } = {}) {
  const text = await evaluate(page, expression)
  const parsed = typeof text === 'string' ? JSON.parse(text) : text
  if (allowFailure !== true && parsed?.ok === false) throw new Error(`页面调用失败：${JSON.stringify(parsed)}`)
  return parsed
}

/**
 * 读一次浏览器内存读数：先强制 GC，再读 `Runtime.getHeapUsage`。
 *
 * D21 实测教训（反例控制 N6 第一次跑出来的）：`usedSize` **不含** ArrayBuffer/Blob 的
 * backing storage——30 MiB 的 `Uint8Array` 只让 `usedSize` 动 4 KB，却让
 * `backingStorageSize` 从 3.5 KB 涨到 30 MB。因此"JS 堆"读数取
 * `usedSize + backingStorageSize`，并把两个分量一并记录，避免用一个漏掉大块内存的口径。
 */
async function sampleHeap(page) {
  await page.send('HeapProfiler.collectGarbage')
  const usage = await page.send('Runtime.getHeapUsage')
  const backingStorageSize = typeof usage.backingStorageSize === 'number' ? usage.backingStorageSize : 0
  return {
    usedSize: usage.usedSize,
    backingStorageSize,
    totalSize: usage.totalSize,
    totalBytes: usage.usedSize + backingStorageSize
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
    child.stdout.on('data', (chunk) => {
      output += chunk
    })
    child.stderr.on('data', (chunk) => {
      output += chunk
    })
    child.on('exit', (code) => resolvePromise({ code, output: output.slice(-300) }))
  })
}

async function rpc(client, lanBase, deviceId, method, args) {
  const response = await client.fetch(`${lanBase}/remote/api/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({ type: 'client-request', rpcId: `d21-${method.replace(/\W/g, '-')}-${Date.now()}`, method, payload: { args } })
  })
  const text = await response.text()
  try {
    return { status: response.status, body: JSON.parse(text) }
  } catch {
    return { status: response.status, body: text.slice(0, 200) }
  }
}

/** 打开页面并把"桥认定的当前会话"等成目标会话（会话选择是异步客户端状态）。 */
async function openAppPage(chrome, lanBase, deviceId, sessionId) {
  const page = await openPage(chrome.debugPort)
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.send('Network.enable')
  await page.send('HeapProfiler.enable')
  await page.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
  })
  await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId ?? '')}`)
  const deadline = Date.now() + 90_000
  let last = null
  for (;;) {
    try {
      last = await pageCall(
        page,
        `JSON.stringify({ ok: true, value: { bridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__, current: globalThis.__DSH_ATTACHMENTS_BRIDGE__?.currentSession?.() ?? null, receiver: typeof globalThis.__DSH_ATTACHMENTS_RECEIVER__ } })`
      )
    } catch (error) {
      last = { value: { error: String(error.message) } }
    }
    if (last.value?.bridge === 'object' && last.value?.receiver === 'object' && last.value?.current === sessionId) {
      const installed = await pageCall(page, INSTALL_HARNESS)
      return { page, value: last.value, harness: installed }
    }
    if (Date.now() >= deadline) throw new Error(`页面未在超时内就绪：${JSON.stringify(last?.value)}`)
    await sleep(1000)
  }
}

// ---------- Core 指标（真实可移植 Core 的 D21 用例集） ----------

function runCoreCases() {
  return new Promise((resolvePromise) => {
    const metricsDir = join(RUN_DIR, 'core-metrics')
    mkdirSync(metricsDir, { recursive: true })
    const resultXml = join(RUN_DIR, 'd21-core-results.xml')
    const args = [
      'run',
      '--project',
      coreProject,
      '--configuration',
      'Release',
      '--no-restore',
      '--no-launch-profile',
      '--',
      '-explicit',
      'on',
      '-failSkips',
      '-preEnumerateTheories',
      '-printMaxStringLength',
      '0',
      '-noColor',
      '-noLogo',
      '-reporter',
      'quiet',
      '-trait',
      'd21=resources',
      '-result-xml',
      resultXml
    ]
    const startedAt = Date.now()
    const child = spawn(dotnet, args, {
      cwd: repoRoot,
      env: {
        ...process.env,
        DSH_D21_CORE_METRICS_DIR: metricsDir,
        // 隔离采样点：本次子进程只跑 6 个 D21 用例，托管堆序列才属于被测负载本身
        // （共享全量跑会混入其余 540 例的活动对象，实测斜率/包络都会失真）。
        DSH_D21_ISOLATED_HEAP_SAMPLING: '1',
        DOTNET_NOLOGO: '1',
        DOTNET_CLI_TELEMETRY_OPTOUT: '1'
      },
      stdio: ['ignore', 'pipe', 'pipe']
    })
    let output = ''
    child.stdout.on('data', (chunk) => {
      output += chunk
    })
    child.stderr.on('data', (chunk) => {
      output += chunk
    })
    child.on('exit', (code, signal) => {
      const payloads = []
      for (const name of readdirSync(metricsDir).filter((entry) => entry.endsWith('.json')).sort()) {
        try {
          payloads.push(JSON.parse(readFileSync(join(metricsDir, name), 'utf8')))
        } catch (error) {
          payloads.push({ case: name, rows: [], parseError: String(error.message) })
        }
      }
      resolvePromise({ exitCode: code, signal, wallMs: Date.now() - startedAt, stdout: output.slice(-4000), metricsDir, resultXml, payloads })
    })
  })
}

/** 采样一个进程的 RSS（观测值：说明采样点与噪声，不作为通过/失败阈值）。 */
function readRssBytes(pid) {
  try {
    const status = readFileSync(`/proc/${pid}/status`, 'utf8')
    const match = status.match(/VmRSS:\s+(\d+)\s+kB/)
    return match === null ? null : Number(match[1]) * 1024
  } catch {
    return null
  }
}

function pgrepCount(pattern) {
  const result = spawnSync('bash', ['-lc', `pgrep -af ${JSON.stringify(pattern)} || true`], { encoding: 'utf8' })
  // pgrep -f 会匹配到本探针自己的命令行；只统计真实残留进程（d09 同款教训）。
  return (result.stdout ?? '').split('\n').filter((line) => line.trim() !== '' && line.includes('pgrep') === false).length
}

const CORE_METRIC_IDS = [
  'C01-pending-bytes-peak',
  'C02-pending-chunks-peak',
  'C03-slow-ack-progress-per-ack',
  'C04-gate-waiting-peak',
  'C05-gate-refused-on-overflow',
  'C06-gate-active-peak',
  'C07-cancel-pending-bytes',
  'C08-cancel-pending-chunks',
  'C09-cancel-sources-disposed',
  'C10-dedup-ttl-live-before',
  'C11-dedup-ttl-gone-at',
  'C12-dedup-capacity-bound',
  'C13-series-heap-slope',
  'C14-series-heap-envelope',
  'C15-series-sources-disposed',
  'C16-series-replay-cache-size',
  'C17-dispose-gate-slots',
  'C18-dispose-idempotent',
  'C19-dispose-queued-released'
]
const LEAK_PATTERNS = [
  { name: 'pairing-url', re: /pair=[A-Za-z0-9_-]{8,}/ },
  { name: 'pair-cookie', re: /dsh_pair=[A-Za-z0-9_-]{8,}/ },
  { name: 'token-field', re: /"token"\s*:\s*"[A-Za-z0-9_-]{8,}"/ },
  { name: 'home-path', re: /\/home\/[A-Za-z0-9._-]+\// }
]

// ---------- 主流程 ----------

let service = null
let chrome = null
const pages = []
let client = null
let deviceId = null
let lanBase = null
let fatal = null
let coreRun = null
const serviceLog = join(evidenceDir, 'service-d21-resources.log')

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })
await mkdir(RUN_DIR, { recursive: true })

try {
  console.log(`D21 背压与资源清理边界判据（判据版本 ${D21_CRITERIA_VERSION}）runId=${RUN_ID}\n`)
  if (skipCore) {
    markNotRun('D21-core-metrics', '真实可移植 Core 的 D21 用例集指标', 'DSH_D21_SKIP_CORE=1（显式跳过；报告因此不是完整通过）')
  }
  for (const [id, description] of [
    ['WP-01-windows-file-locks-acls', 'Windows 文件锁与 ACL（CreateFileW 独占/共享语义、继承 ACL、句柄计数）'],
    ['WP-02-windows-process-tree', 'Windows 原生整进程树资源（WebView2 子进程树、作业对象、GPU 进程内存）'],
    ['WP-03-webview2-physical-boundary', '真实 WebView2 物理消息边界下的同一批资源采样']
  ]) {
    markNotRun(id, description, 'WindowsPending：需 Windows 实机；按 D21 口径本轮不尝试（Core 侧已用等价的注入端口覆盖可移植部分）')
  }
  if (!existsSync(join(fixtureRoot, 'lan-address.txt'))) {
    throw new Error('缺少夹具 lan-address.txt；请先运行 bash tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
  lanBase = `http://${lanAddress}:${port}`

  // ---- 阶段 0：造会话（共享 DSH_HOME）+ 启动夹具服务 + 配对 ----
  console.log('阶段 0：造两条真实会话并启动夹具服务')
  const seeds = [await seedSession('d21 resources seed A'), await seedSession('d21 resources seed B')]
  observations.sessions = { seedExitCodes: seeds.map((item) => item.code) }
  service = startService(dshBin, ['--profile', fixtureProfile, '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))
  await waitForReady(service.child, serviceLog, 120_000)
  const servicePid = service.child.pid
  const serviceRssStart = readRssBytes(servicePid)

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
  if (sessionIds.length === 0) throw new Error('共享 DSH_HOME 里没有可用会话（headless 落盘失败）')
  const sessionA = sessionIds[0]
  const sessionB = sessionIds[1] ?? sessionIds[0]
  observations.sessions.list = { status: list.status, count: sessionIds.length, twoSessions: sessionIds.length >= 2 }
  gate(
    'D21-00-fixture-ready',
    '夹具就绪：私有 DSH_HOME 里有真实会话、夹具服务已监听、loopback 配对拿到 deviceId',
    sessionIds.length > 0 && deviceId !== '',
    `会话数=${sessionIds.length} deviceId=${deviceId === '' ? '(空)' : '已获取'} servicePid=${servicePid}`
  )

  console.log('\n阶段 1：真实 Chromium 打开两个页面（会话 A/B），每页安装 D21 装置')
  chrome = await launchChrome({})
  const openedA = await openAppPage(chrome, lanBase, deviceId, sessionA)
  const openedB = await openAppPage(chrome, lanBase, deviceId, sessionB)
  pages.push(openedA.page, openedB.page)
  const pageA = openedA.page
  const pageB = openedB.page
  const hostInfo = await pageCall(pageA, 'globalThis.__D21__.hostInfo()')
  observations.host = hostInfo
  gate(
    'D21-01-production-receiver-host',
    '真实已构建插件在真实 Chromium 中装配：接收端宿主版本 1、桥版本 1、组合依赖（origin/当前会话/能力）齐全、非安全上下文',
    openedA.harness.ok === true &&
      hostInfo.version === 1 &&
      hostInfo.wiring.hasOrigin === true &&
      hostInfo.wiring.hasCurrentSession === true &&
      hostInfo.wiring.hasCapability === true,
    `receiverVersion=${hostInfo.version} wiring=${JSON.stringify(hostInfo.wiring)} globals=${JSON.stringify(hostInfo.globals)}`
  )

  const chunkBytes = LIMITS.chunkBytes
  const identityA = { sessionId: sessionA, targetId: 'target-d21', documentEpoch: 41, composerEpoch: 42, composerScope: 'composer-scope-d21' }
  const identityB = { ...identityA, sessionId: sessionB }

  // ================= 最大文件（20 MiB / 80 块） =================
  console.log('\n阶段 2：最大文件（20 MiB，80 块，每 2 块 drain 一次以驱动 2 块窗口）')
  const maxFileSha = sha256(patternBytes(LIMITS.maxFileBytes))
  observations.maxFile = { byteLength: LIMITS.maxFileBytes, sha256: maxFileSha.slice(0, 16), chunkBytes }
  await pageCall(pageA, `globalThis.__D21__.make('maxfile')`)
  const maxFileRun = await pageCall(
    pageA,
    `globalThis.__D21__.runBatch('maxfile', ${JSON.stringify({
      ...identityA,
      withContext: true,
      batchId: 'batch-d21-maxfile',
      chunkBytes,
      drainEvery: 2,
      files: [{ fileId: 'file-d21-maxfile', name: 'd21-max.bin', mime: 'application/octet-stream', byteLength: LIMITS.maxFileBytes, sha256: maxFileSha }]
    })})`
  )
  const maxFileAccounting = await pageCall(pageA, `globalThis.__D21__.accounting('maxfile')`)
  observations.maxFile.run = {
    acceptedChunks: maxFileRun.acceptedChunks,
    rejectedChunks: maxFileRun.rejectedChunks,
    acks: maxFileRun.acks,
    inFlightMax: maxFileRun.inFlightMax,
    ends: maxFileRun.ends,
    accounting: maxFileAccounting
  }
  pushRow(buildRow('B01-max-file-peak-buffered-bytes', maxFileAccounting.peakBufferedBytes, { detail: `20 MiB 单文件峰值=${maxFileAccounting.peakBufferedBytes}B` }))
  pushRow(buildRow('B02-max-file-buffered-after', maxFileAccounting.bufferedBytes, { detail: `构造并导入后残留=${maxFileAccounting.bufferedBytes}B constructedBytes=${maxFileAccounting.constructedBytes}` }))
  pushRow(buildRow('B03-max-file-ack-inflight-peak', maxFileRun.inFlightMax, { detail: `acks=${maxFileRun.acks} 峰值 inFlight=${maxFileRun.inFlightMax}（每 2 块 drain 一次）` }))
  pushRow(
    buildRow('B04-max-file-ack-per-chunk', maxFileRun.acceptedChunks === 0 ? null : maxFileRun.acks / maxFileRun.acceptedChunks, {
      detail: `acks=${maxFileRun.acks} acceptedChunks=${maxFileRun.acceptedChunks}`
    })
  )
  gate(
    'D21-max-file-integrity',
    '最大文件走完整生产接收路径：每块被接受、file-end 通过哈希校验并构造真实 File（draft=staged，拿到原生 attachmentId）',
    maxFileRun.ends.length === 1 &&
      maxFileRun.ends[0].ok === true &&
      maxFileRun.ends[0].draft === 'staged' &&
      maxFileRun.ends[0].attachmentIds.length === 1 &&
      maxFileRun.ends[0].receivedBytes === LIMITS.maxFileBytes,
    `end=${JSON.stringify(maxFileRun.ends[0])}`
  )
  const draftAfterMaxFile = await pageCall(pageA, `globalThis.__D21__.draftProbe('after-maxfile')`)
  observations.draftProbeAfterMaxFile = draftAfterMaxFile
  gate(
    'D21-draft-path-control',
    '真实草稿路径控制：接收端导入确实落到原生草稿（探针回读到导入前的原生 attachmentIds ≥ 1）',
    draftAfterMaxFile.ok === true && draftAfterMaxFile.previous.length >= 1,
    `added=${JSON.stringify(draftAfterMaxFile.added)} previous=${draftAfterMaxFile.previous.length}`
  )
  await pageCall(pageA, `globalThis.__D21__.make('overfile')`)
  const overFile = await pageCall(
    pageA,
    `globalThis.__D21__.runBatch('overfile', ${JSON.stringify({
      ...identityA,
      withContext: true,
      batchId: 'batch-d21-overfile',
      chunkBytes,
      files: [{ fileId: 'file-d21-overfile', name: 'over.bin', mime: 'application/octet-stream', byteLength: LIMITS.maxFileBytes + 1, sha256: maxFileSha }]
    })})`,
    { allowFailure: true }
  )
  observations.overFile = overFile
  pushRow(buildRow('B08-over-file-rejected', overFile.ok === false ? 1 : 0, { detail: `20 MiB+1 的 file-begin 结果=${overFile.reason ?? '竟然被接受'}` }))
  await pageCall(pageA, `globalThis.__D21__.dispose('overfile')`)

  // ================= 最大批次（10 文件 × 1 MiB） =================
  console.log('\n阶段 3：最大批次（10 文件 × 1 MiB）')
  const batchFiles = []
  for (let index = 0; index < LIMITS.maxFilesPerBatch; index += 1) {
    const fileBytes = 1024 * 1024
    batchFiles.push({ fileId: `file-d21-batch-${index}`, name: `d21-batch-${index}.bin`, mime: 'application/octet-stream', byteLength: fileBytes, sha256: sha256(patternBytes(fileBytes)) })
  }
  await pageCall(pageA, `globalThis.__D21__.make('maxbatch')`)
  const maxBatchRun = await pageCall(
    pageA,
    `globalThis.__D21__.runBatch('maxbatch', ${JSON.stringify({ ...identityA, withContext: true, batchId: 'batch-d21-maxbatch', chunkBytes, files: batchFiles })})`
  )
  const maxBatchAccounting = await pageCall(pageA, `globalThis.__D21__.accounting('maxbatch')`)
  observations.maxBatch = {
    acceptedChunks: maxBatchRun.acceptedChunks,
    ends: maxBatchRun.ends.map((item) => ({ fileId: item.fileId, ok: item.ok, draft: item.draft, ids: item.attachmentIds.length })),
    accounting: maxBatchAccounting
  }
  pushRow(buildRow('B05-max-batch-peak-buffered-files', maxBatchRun.peakBufferedFiles, { detail: `10 文件批次峰值并发组装=${maxBatchRun.peakBufferedFiles}` }))
  pushRow(buildRow('B06-max-batch-peak-buffered-bytes', maxBatchRun.peakBufferedBytes, { detail: `峰值=${maxBatchRun.peakBufferedBytes}B（10 × 1 MiB，峰值由单文件在组装量决定）` }))
  gate(
    'D21-max-batch-integrity',
    '最大批次的每个文件都完成（10/10 staged 且各拿到 1 个原生 attachmentId）',
    maxBatchRun.ends.length === LIMITS.maxFilesPerBatch && maxBatchRun.ends.every((item) => item.ok === true && item.draft === 'staged' && item.attachmentIds.length === 1),
    `ends=${JSON.stringify(maxBatchRun.ends.map((item) => `${item.fileId}:${item.draft}/${item.attachmentIds.length}`))}`
  )
  await pageCall(pageA, `globalThis.__D21__.make('overbatch')`)
  const overBatch = await pageCall(
    pageA,
    `globalThis.__D21__.runBatch('overbatch', ${JSON.stringify({
      ...identityA,
      withContext: true,
      batchId: 'batch-d21-overbatch',
      chunkBytes,
      files: [...batchFiles, { ...batchFiles[0], fileId: 'file-d21-batch-extra', name: 'd21-batch-extra.bin' }]
    })})`,
    { allowFailure: true }
  )
  observations.overBatch = overBatch
  pushRow(buildRow('B07-over-batch-rejected', overBatch.ok === false ? 1 : 0, { detail: `11 文件批次被拒：${overBatch.reason ?? '竟然被接受'}` }))
  await pageCall(pageA, `globalThis.__D21__.dispose('overbatch')`)

  // ================= 取消（中途 cancel 回收缓冲） =================
  console.log('\n阶段 4：中途 cancel 回收缓冲（页面 B）')
  await pageCall(pageB, `globalThis.__D21__.make('cancel')`)
  const cancelFeed = await pageCall(
    pageB,
    `globalThis.__D21__.feedChunks('cancel', ${JSON.stringify({
      ...identityB,
      withContext: true,
      batchId: 'batch-d21-cancel',
      fileId: 'file-d21-cancel',
      name: 'd21-cancel.bin',
      mime: 'application/octet-stream',
      byteLength: 3 * chunkBytes,
      sha256: sha256(patternBytes(3 * chunkBytes)),
      chunkBytes
    })})`
  )
  const cancelBefore = await pageCall(pageB, `globalThis.__D21__.accounting('cancel')`)
  const cancelResult = await pageCall(pageB, `globalThis.__D21__.cancel('cancel', ${JSON.stringify({ ...identityB, batchId: 'batch-d21-cancel' })})`)
  const cancelAfter = await pageCall(pageB, `globalThis.__D21__.accounting('cancel')`)
  const cancelDisposed = await pageCall(pageB, `globalThis.__D21__.dispose('cancel')`)
  observations.cancel = { feed: cancelFeed, before: cancelBefore, cancel: cancelResult, after: cancelAfter, disposed: cancelDisposed }
  pushRow(buildRow('B09-cancel-buffered-bytes', cancelAfter.bufferedBytes, { detail: `cancel 前=${cancelBefore.bufferedBytes}B（${cancelBefore.bufferedFiles} 文件）cancel 后=${cancelAfter.bufferedBytes}B` }))
  pushRow(buildRow('B10-cancel-released-bytes', cancelAfter.releasedBytes, { detail: `cancel 记账 releasedBytes=${cancelAfter.releasedBytes} releasedFiles=${cancelAfter.releasedFiles}` }))
  pushRow(
    buildRow('B11-dispose-retained-after', cancelDisposed.retainedBatches + cancelDisposed.bufferedFiles + cancelDisposed.bufferedBytes, {
      detail: `dispose 后 retainedBatches=${cancelDisposed.retainedBatches} bufferedFiles=${cancelDisposed.bufferedFiles} bufferedBytes=${cancelDisposed.bufferedBytes} disposed=${cancelDisposed.disposed}`
    })
  )
  negative(
    'N1-cancel-buffer-was-real',
    '反例前提：取消前接收缓冲必须真的非空（否则"取消后归零"是空检查）',
    cancelBefore.bufferedBytes > 0,
    `cancel 前 bufferedBytes=${cancelBefore.bufferedBytes}`
  )

  // ================= 暂停消费者（不 drain） =================
  console.log('\n阶段 5：暂停消费者（不 drain，3 块）')
  await pageCall(pageB, `globalThis.__D21__.make('paused')`)
  const pausedFeed = await pageCall(
    pageB,
    `globalThis.__D21__.feedChunks('paused', ${JSON.stringify({
      ...identityB,
      withContext: true,
      batchId: 'batch-d21-paused',
      fileId: 'file-d21-paused',
      name: 'd21-paused.bin',
      mime: 'application/octet-stream',
      byteLength: 3 * chunkBytes,
      sha256: sha256(patternBytes(3 * chunkBytes)),
      chunkBytes
    })})`
  )
  const pausedResume = await pageCall(pageB, `globalThis.__D21__.resume('paused')`)
  observations.paused = { feed: pausedFeed, resume: pausedResume }
  pushRow(buildRow('B12-paused-consumer-outbox-entries', pausedResume.acks, { detail: `暂停期间接受 ${pausedFeed.accepted} 块，恢复时一次 drain 取出 ${pausedResume.acks} 条 ack` }))
  pushRow(buildRow('B13-paused-consumer-buffered-bytes', pausedFeed.bufferedBytes, { detail: `暂停期间被接受的缓冲=${pausedFeed.bufferedBytes}B（${pausedFeed.accounting.bufferedFiles} 文件）` }))
  pushRow(
    buildRow('B14-paused-consumer-overrun-rejected', pausedFeed.rejected.includes('window-overflow') ? 1 : 0, {
      detail: `被拒码=${JSON.stringify(pausedFeed.rejected)}（期望 window-overflow）`
    })
  )
  negative(
    'N2-paused-window-not-a-tautology',
    '反例前提：暂停期间必须有块被接受（否则"窗口拒绝"是空检查）',
    pausedFeed.accepted > 0,
    `暂停期间接受 ${pausedFeed.accepted} 块、拒绝 ${pausedFeed.rejected.length} 块`
  )
  await pageCall(pageB, `globalThis.__D21__.dispose('paused')`)

  // ================= 连续 30 轮（无单调无界增长） =================
  console.log('\n阶段 6：连续 30 轮（每轮 4 KiB 单文件，逐轮采样 JS 堆）')
  const rounds = LIMITS.seriesRounds
  const roundBytes = 4096
  const roundSha = sha256(patternBytes(roundBytes))
  await pageCall(pageB, `globalThis.__D21__.make('series')`)
  const warm = await pageCall(
    pageB,
    `globalThis.__D21__.runBatch('series', ${JSON.stringify({ ...identityB, withContext: true, batchId: 'batch-d21-series-warm', chunkBytes, files: [{ fileId: 'file-d21-series-warm', name: 'warm.bin', mime: 'application/octet-stream', byteLength: roundBytes, sha256: roundSha }] })})`
  )
  const heapSeries = []
  const roundFailures = []
  if (warm.ends?.[0]?.ok !== true) roundFailures.push(`warm:${warm.ends?.[0]?.code ?? 'failed'}`)
  for (let round = 0; round < rounds; round += 1) {
    const result = await pageCall(
      pageB,
      `globalThis.__D21__.runBatch('series', ${JSON.stringify({
        ...identityB,
        withContext: false,
        batchId: `batch-d21-series-${round}`,
        chunkBytes,
        files: [{ fileId: `file-d21-series-${round}`, name: `s${round}.bin`, mime: 'application/octet-stream', byteLength: roundBytes, sha256: roundSha }]
      })})`
    )
    const end = result.ends?.[0]
    if (end?.ok !== true || end?.draft !== 'staged') roundFailures.push(`round-${round}:${end?.code ?? end?.draft ?? 'missing'}`)
    heapSeries.push(await sampleHeap(pageB))
  }
  const seriesAccounting = await pageCall(pageB, `globalThis.__D21__.accounting('series')`)
  const heapTotals = heapSeries.map((sample) => sample.totalBytes)
  const seriesVerdict = judgeSeries(heapTotals, { minRounds: rounds })
  series.browserHeapBytesPerRound = {
    unit: 'bytes',
    samplingPoint: '每轮批次导入完成后 CDP HeapProfiler.collectGarbage → Runtime.getHeapUsage().usedSize + .backingStorageSize',
    values: heapTotals,
    components: {
      usedSize: heapSeries.map((sample) => sample.usedSize),
      backingStorageSize: heapSeries.map((sample) => sample.backingStorageSize)
    },
    analysis: seriesVerdict.analysis,
    interpretation:
      '这 30 轮走的是真实导入路径：草稿里因此累积 30 个真实附件及其卡片（有界的用户可见状态），斜率里含这部分产品状态；接收端自己的资源读数（B17/B18/B19）才是泄漏敏感项，且都是精确有界。'
  }
  const seriesDraftProbe = await pageCall(pageB, `globalThis.__D21__.draftProbe('after-series')`)
  observations.series = { rounds, roundBytes, roundFailures, accounting: seriesAccounting, draftProbe: seriesDraftProbe, warmEnd: warm.ends?.[0] ?? null }
  pushRow(
    buildRow('B15-series-heap-slope', seriesVerdict.analysis.slopeBytesPerRound, {
      detail: `30 轮斜率=${seriesVerdict.analysis.slopeBytesPerRound.toFixed(1)} B/轮 min=${seriesVerdict.analysis.min} max=${seriesVerdict.analysis.max} increases=${seriesVerdict.analysis.increases}/29`,
      raw: seriesVerdict.analysis
    })
  )
  pushRow(buildRow('B16-series-heap-envelope', seriesVerdict.analysis.envelope, { detail: `包络=${seriesVerdict.analysis.envelope}B` }))
  pushRow(buildRow('B17-series-buffered-bytes-after', seriesAccounting.bufferedBytes, { detail: `30 轮后 bufferedBytes=${seriesAccounting.bufferedBytes}B bufferedFiles=${seriesAccounting.bufferedFiles}` }))
  pushRow(buildRow('B18-series-retained-batches', seriesAccounting.retainedBatches, { detail: `30 轮后 retainedBatches=${seriesAccounting.retainedBatches}（maxRetainedBatches）` }))
  pushRow(buildRow('B19-series-released-bytes', seriesAccounting.constructedBytes, { detail: `constructedBytes=${seriesAccounting.constructedBytes}B（30 × 4 KiB + 热身轮）` }))
  gate(
    'D21-series-rounds-completed',
    '30 轮每一轮都走真实导入路径（staged 且拿到原生 attachmentId），没有"跑到一半就失败"的轮次',
    roundFailures.length === 0,
    roundFailures.length === 0 ? `30/30 轮 staged；草稿 previous=${seriesDraftProbe.previous.length}` : `失败轮次=${JSON.stringify(roundFailures)}`
  )

  // ---- 反例控制 N6：真·无界路径（每轮保留 1 MiB）必须被同一判定抓住 ----
  console.log('\n阶段 7：反例控制（页面里每轮保留 1 MiB 的真无界路径）')
  const leakHeap = []
  for (let round = 0; round < rounds; round += 1) {
    await pageCall(pageB, `globalThis.__D21__.leakRound(1)`)
    leakHeap.push(await sampleHeap(pageB))
  }
  await pageCall(pageB, `globalThis.__D21__.releaseLeak()`)
  const leakVerdict = judgeSeries(leakHeap.map((sample) => sample.totalBytes), { minRounds: rounds })
  series.browserLeakControlBytesPerRound = {
    unit: 'bytes',
    samplingPoint: '同上采样点；页面侧 globalThis.__D21_LEAK__ 每轮 push 1 MiB（30 轮共保留 30 MiB）',
    values: leakHeap.map((sample) => sample.totalBytes),
    components: {
      usedSize: leakHeap.map((sample) => sample.usedSize),
      backingStorageSize: leakHeap.map((sample) => sample.backingStorageSize)
    },
    analysis: leakVerdict.analysis
  }
  negative(
    'N6-heap-gate-catches-real-unbounded-path',
    '真无界路径控制：每轮保留 1 MiB（30 轮）时同一采样+判定必须判失败（斜率与包络都越界）',
    leakVerdict.pass === false,
    `斜率=${leakVerdict.analysis.slopeBytesPerRound.toFixed(0)} B/轮 包络=${leakVerdict.analysis.envelope} reasons=${leakVerdict.reasons.join('; ')}`
  )

  // ---- 控制 N12：接收端独占的 30 轮（每轮 2 块后 cancel、不进草稿）同样必须有界 ----
  console.log('\n阶段 7b：接收端独占 30 轮（每轮 2 块后 cancel，不进草稿）')
  const cancelChunkBytes = 2 * chunkBytes
  const cancelSha = sha256(patternBytes(cancelChunkBytes))
  await pageCall(pageB, `globalThis.__D21__.make('seriescancel')`)
  const receiverOnlySeries = []
  const receiverOnlyFailures = []
  for (let round = 0; round < rounds; round += 1) {
    const feed = await pageCall(
      pageB,
      `globalThis.__D21__.feedChunks('seriescancel', ${JSON.stringify({
        ...identityB,
        withContext: round === 0,
        batchId: `batch-d21-cancel-${round}`,
        fileId: `file-d21-cancel-${round}`,
        name: `c${round}.bin`,
        mime: 'application/octet-stream',
        byteLength: cancelChunkBytes,
        sha256: cancelSha,
        chunkBytes
      })})`
    )
    const cancelled = await pageCall(pageB, `globalThis.__D21__.cancel('seriescancel', ${JSON.stringify({ ...identityB, batchId: `batch-d21-cancel-${round}` })})`)
    await pageCall(pageB, `globalThis.__D21__.resume('seriescancel')`)
    if (feed.accepted !== 2 || cancelled.ok !== true) receiverOnlyFailures.push(`round-${round}:accepted=${feed.accepted},cancel=${cancelled.ok}`)
    receiverOnlySeries.push(await sampleHeap(pageB))
  }
  const receiverOnlyAccounting = await pageCall(pageB, `globalThis.__D21__.accounting('seriescancel')`)
  const receiverOnlyVerdict = judgeSeries(receiverOnlySeries.map((sample) => sample.totalBytes), { minRounds: rounds })
  series.browserReceiverOnlyBytesPerRound = {
    unit: 'bytes',
    samplingPoint: '每轮 cancel + drain 完成后 CDP HeapProfiler.collectGarbage → Runtime.getHeapUsage()（usedSize + backingStorageSize）',
    values: receiverOnlySeries.map((sample) => sample.totalBytes),
    analysis: receiverOnlyVerdict.analysis,
    interpretation: '这 30 轮不导入草稿（每轮取消），因此斜率只反映接收端/传输层自身；与上面的真实导入序列互为对照。'
  }
  observations.seriesReceiverOnly = { rounds, failures: receiverOnlyFailures, accounting: receiverOnlyAccounting }
  pushRow(
    buildRow('B20-series-receiver-only-slope', receiverOnlyVerdict.analysis.slopeBytesPerRound, {
      detail: `接收端独占 30 轮斜率=${receiverOnlyVerdict.analysis.slopeBytesPerRound.toFixed(1)} B/轮 包络=${receiverOnlyVerdict.analysis.envelope}B（对照：真实导入序列含草稿里 31 个附件的有界产品状态）`,
      raw: receiverOnlyVerdict.analysis
    })
  )
  negative(
    'N12-receiver-only-30-rounds-bounded',
    '接收端独占 30 轮（每轮 2 块后 cancel、不进草稿）在同一判定下必须仍然有界（斜率与包络都不越界），且每轮都真的接受了 2 块并取消成功',
    receiverOnlyVerdict.pass === true && receiverOnlyFailures.length === 0,
    `斜率=${receiverOnlyVerdict.analysis.slopeBytesPerRound.toFixed(0)} B/轮 包络=${receiverOnlyVerdict.analysis.envelope} 失败轮次=${JSON.stringify(receiverOnlyFailures)} reasons=${receiverOnlyVerdict.reasons.join('; ')}`
  )

  // ---- 子进程结束：Chromium 本体与进程树 ----
  console.log('\n阶段 8：子进程结束（Chromium）')
  const chromePid = chrome.pid ?? null
  const chromeProbe = `dsh-cdp-${chrome.userDataDir.split('/').pop()}`
  for (const openPageInstance of pages) {
    try {
      await openPageInstance.close()
    } catch {
      // 忽略
    }
  }
  await chrome.close()
  await sleep(500)
  const chromeLeft = pgrepCount(chromeProbe)
  const chromeAlive = chromePid !== null && existsSync(`/proc/${chromePid}`)
  observations.subprocess = { chrome: { pid: chromePid, probe: chromeProbe, remaining: chromeLeft, alive: chromeAlive } }
  negative(
    'N7-chromium-subprocess-exited',
    '子进程结束：Chromium 关闭后进程树必须消失（进程级资源清理读数）',
    chromeAlive === false && chromeLeft === 0,
    `pid=${chromePid} alive=${chromeAlive} pgrep(${chromeProbe})=${chromeLeft}`
  )
  chrome = null

  // ================= 单元层：去重缓存确定性探测 =================
  console.log('\n阶段 9：去重缓存（已构建 lib/ + 注入时钟）')
  const dedupProbe = runDedupProbes({ capacity: 8 })
  observations.dedup = dedupProbe.observations
  for (const row of dedupProbe.rows) pushRow(row)
  negative(dedupProbe.counterProbe.id, dedupProbe.counterProbe.description, dedupProbe.counterProbe.ok, dedupProbe.counterProbe.detail)

  // ================= Core 层：真实可移植 Core 的 D21 用例集 =================
  if (!skipCore) {
    console.log('\n阶段 10：真实可移植 Core 的 D21 用例集（dotnet run -trait d21=resources）')
    coreRun = await runCoreCases()
    observations.core = {
      exitCode: coreRun.exitCode,
      signal: coreRun.signal,
      wallMs: coreRun.wallMs,
      metricsFiles: coreRun.payloads.map((payload) => payload.case),
      resultXml: `d21-runs/${RUN_ID}/d21-core-results.xml`,
      stdoutTail: coreRun.stdout.slice(-1500)
    }
    const coreRows = []
    for (const payload of coreRun.payloads) {
      for (const row of payload.rows ?? []) {
        if (typeof row.numerator === 'number') coreRows.push(buildRatioRow(row.id, row.numerator, row.denominator))
        else coreRows.push(buildRow(row.id, row.measured, { detail: `${payload.case}：${row.detail ?? ''}` }))
      }
      for (const control of payload.controls ?? []) {
        negative(control.id, `${payload.case}：${control.description}`, control.ok === true, control.detail ?? '')
      }
      if (payload.series?.managedHeapBytesPerRound !== undefined) {
        const isolated = judgeSeries(payload.series.managedHeapBytesPerRound, { envelopeMaxBytes: LIMITS.seriesEnvelopeMaxBytes })
        series.coreManagedHeapBytesPerRound = {
          unit: payload.series.unit ?? 'bytes',
          samplingPoint: payload.series.samplingPoint ?? '未知',
          values: payload.series.managedHeapBytesPerRound,
          analysis: isolated.analysis,
          interpretation:
            '本序列来自**隔离的** D21 用例集进程（只跑 6 个 D21 用例），因此这里用紧包络阈值 8 MiB 判定；C14 那一行是同一条序列在**共享** MTP 进程里跑时的粗界（16 MiB，按实测噪声）。'
        }
        negative(
          'N13-core-isolated-series-tight-envelope',
          'Core 连续 30 轮序列在**隔离进程**下必须同时满足紧包络（8 MiB）与斜率阈值（共享进程跑的包络含其余 540 例的活动对象，另按 16 MiB 粗界判）',
          isolated.pass === true,
          `斜率=${isolated.analysis.slopeBytesPerRound.toFixed(0)} B/轮 包络=${isolated.analysis.envelope} reasons=${isolated.reasons.join('; ')}`
        )
      }
    }
    for (const row of coreRows) pushRow(row)
    const afterCore = pgrepCount('DshLauncher.Core.Tests')
    negative(
      'N8-core-subprocess-exited',
      '子进程结束：D21 Core 用例集进程必须正常退出（退出码 0）且不残留同名进程',
      coreRun.exitCode === 0 && afterCore === 0,
      `exit=${coreRun.exitCode} signal=${coreRun.signal ?? 'none'} wall=${coreRun.wallMs}ms 残留进程=${afterCore} 指标文件=${coreRun.payloads.length}`
    )
    gate(
      'D21-core-metrics-complete',
      `Core 侧 ${CORE_METRIC_IDS.length} 条指标全部落到机器可读文件（每个用例一个文件；未采样即缺失）`,
      coreRows.length === CORE_METRIC_IDS.length && coreRows.every((row) => row.notRun !== true) && CORE_METRIC_IDS.every((id) => coreRows.some((row) => row.id === id)),
      `收到 ${coreRows.length} 条：${coreRows.map((row) => row.id).join(', ')}`
    )
  } else {
    for (const id of CORE_METRIC_IDS) pushRow(buildRow(id, null, { detail: 'DSH_D21_SKIP_CORE=1：未采样' }))
  }

  // ================= 服务进程观测（非阈值项） =================
  const serviceRssEnd = readRssBytes(servicePid)
  observations.serviceProcess = {
    pid: servicePid,
    samplingPoint: 'Node 读 /proc/<pid>/status 的 VmRSS（30 轮前后各一次，非连续采样）',
    noise: 'RSS 含 V8 堆、zstd 会话缓存与分配器保留页，单次读数抖动可达数十 MB；因此只作观测记录，不设通过阈值',
    rssStartBytes: serviceRssStart,
    rssEndBytes: serviceRssEnd,
    deltaBytes: serviceRssStart === null || serviceRssEnd === null ? null : serviceRssEnd - serviceRssStart
  }

  // ================= 负向控制（序列/自检） =================
  console.log('\n阶段 11：反例控制与逐行自检')
  const sequenceControls = runSeriesNegativeControls()
  for (const control of sequenceControls.controls) negative(control.id, control.description, control.ok, control.detail)
  const selfTest = falsifiabilitySelfTest(rows)
  negative(
    'N9-every-threshold-can-fail',
    '逐行自检：把每条阈值换成必然不成立的荒谬值后，判定必须翻转为 false（没有恒真断言）',
    selfTest.ok,
    `检查 ${selfTest.checked.length} 行，未翻转=${JSON.stringify(selfTest.checked.filter((item) => item.flippedToFail !== true).map((item) => item.id))}`
  )
} catch (error) {
  fatal = error
  gate('D21-99-fatal', '判据在完成前抛出异常（绝不吞掉）', false, String(error?.stack ?? error).slice(0, 800))
} finally {
  for (const openPageInstance of pages) {
    try {
      await openPageInstance.close()
    } catch {
      // 忽略
    }
  }
  try {
    if (chrome !== null) await chrome.close()
  } catch {
    // 忽略
  }
  try {
    if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid'))
  } catch {
    // 忽略
  }
  await sleep(500)
  const leftovers = spawnSync('bash', ['-lc', "pgrep -af 'dsh-attachments-fixture|headless-chrome|provider-stub|dsh-cdp-' || true"], { encoding: 'utf8' })
  const leftoverLines = (leftovers.stdout ?? '').split('\n').filter((line) => line.trim() !== '' && line.includes('pgrep') === false)
  negative(
    'N10-no-leftover-processes',
    '收尾后不得残留夹具服务 / 无头 Chrome / provider stub 进程（本轮自己起的全部结束）',
    leftoverLines.length === 0,
    leftoverLines.length === 0 ? 'pgrep 零命中' : leftoverLines.join(' | ').slice(0, 400)
  )
}

// ---------- 报告与证据（每次运行独立 runId；历史只增不改） ----------

const verdicts = computeVerdicts(rows)
const candidate = candidateSummary()
const evidenceText = (value) => JSON.stringify(value, null, 2) + '\n'

// 泄漏检查必须在写盘之前：命中即报告为负向探针失败，并让整体结果失败。
const preWriteText = evidenceText({ observations, series, sample: rows.slice(0, 3) })
const leaks = LEAK_PATTERNS.filter((pattern) => pattern.re.test(preWriteText)).map((pattern) => pattern.name)
const redactionNegative = {
  id: 'N11-evidence-redaction',
  description: '证据落盘不得含 pair=/cookie/token/绝对家目录路径（写盘前对同一批数据做检查）',
  ok: leaks.length === 0,
  detail: leaks.length === 0 ? `零命中（检查 ${LEAK_PATTERNS.length} 类形态）` : `命中=${JSON.stringify(leaks)}`
}
negativeProbes.push(redactionNegative)

const failed = gates.filter((item) => !item.ok)
const failedNegative = negativeProbes.filter((item) => !item.ok)
const result = failed.length === 0 && failedNegative.length === 0 && verdicts.result === 'pass' ? 'pass' : 'fail'
const reportText = evidenceText({
  schemaVersion: 1,
  task: 'D21',
  purpose: 'backpressure-and-resources',
  runId: RUN_ID,
  criteriaVersion: D21_CRITERIA_VERSION,
  startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
  finishedAt: new Date().toISOString(),
  command: RUN_COMMAND,
  platform: { platform: process.platform, release: osRelease(), arch: process.arch, node: process.version, cwd: process.cwd(), dshBin, dotnet },
  candidate,
  limits: LIMITS,
  // 交付要求的一行一计量项表：{metric, unit, samplingPoint, threshold, measured, pass, scenario}
  metrics: rows.map((row) => ({
    id: row.id,
    layer: row.layer,
    scenario: row.scenario,
    metric: row.metric,
    unit: row.unit,
    samplingPoint: row.samplingPoint,
    threshold: row.threshold,
    comparison: row.comparison,
    thresholdValue: row.thresholdValue,
    measured: row.measured,
    pass: row.pass,
    notRun: row.notRun === true,
    role: row.role,
    detail: row.detail ?? null
  })),
  verdict: verdicts,
  series,
  observations,
  negativeProbes,
  controls: [
    { id: 'C01-control-draft-path', description: '最大文件导入后桥回读到导入前的原生 attachmentIds ≥ 1（导入真的发生，不是伪造回调）' },
    { id: 'N1-cancel-buffer-was-real', description: '取消前缓冲非空（取消归零不是空检查）' },
    { id: 'N2-paused-window-not-a-tautology', description: '暂停期间确实有块被接受（窗口拒绝不是空检查）' },
    { id: 'N3a/N3b/N3c', description: '无界增长与缓慢泄漏必须被判失败、有界抖动必须通过' },
    { id: 'N4/N5-frozen-clock', description: '注入时钟不推进时 TTL 条目必须仍在（过期断言可失败）' },
    { id: 'N6-heap-gate-catches-real-unbounded-path', description: '浏览器里每轮保留 1 MiB 的真无界路径必须被同一判定抓住' },
    { id: 'N7/N8-subprocess-exit', description: 'Chromium 与 Core 用例集子进程退出后不得残留进程' },
    { id: 'N9-every-threshold-can-fail', description: '逐行荒谬阈值自检：没有恒真断言' },
    { id: 'N10-no-leftover-processes', description: '收尾后夹具服务/无头 Chrome 零残留' },
    { id: 'N11-evidence-redaction', description: '证据写盘前零敏感形态' },
    { id: 'B07/B08/B14-limits', description: '超批（11 文件）、超文件（20 MiB+1）、超在途窗口必须被拒绝' }
  ],
  notRun,
  windowsPending: [
    'Windows 文件锁与 ACL（真实 CreateFileW 独占/共享语义、继承 ACL）——按 D21 口径留 Windows 专项，本轮不尝试',
    'Windows 原生整进程树资源（WebView2 子进程树、作业对象/句柄计数、GPU 进程内存）——属 WindowsPending',
    'Windows 侧 WebView2 物理消息边界下的同一批资源采样（Core 侧已用等价注入端口覆盖，但物理边界不同）'
  ],
  evidenceLayout: {
    runDir: `artifacts/verify-portable/d21-runs/${RUN_ID}`,
    index: 'artifacts/verify-portable/d21-runs/index.json',
    report: `artifacts/verify-portable/d21-runs/${RUN_ID}/report.json`,
    latestMirror: 'artifacts/verify-portable/d21-resource-report.json 与 artifacts/verify-portable/d21-resource-gates.json 都是**最新一次**运行的镜像；历史只认 runId 目录'
  },
  note: '浏览器层在真实 Chromium 里跑已构建插件的生产接收端（组件依赖由插件真实装配、导入走真实桥→生产草稿适配器）；Core 层跑真实可移植 Core 的 D21 用例集；单元层用已构建 lib/ 的去重缓存 + 注入时钟。阈值全部冻结在 tests/fixtures/d21-resource-criteria.mjs。'
})
const gatesEvidenceText = evidenceText({
  schemaVersion: 1,
  task: 'D21',
  purpose: 'backpressure-and-resources',
  runId: RUN_ID,
  criteriaVersion: D21_CRITERIA_VERSION,
  gates,
  negativeProbes,
  notRun,
  failedGateIds: failed.map((item) => item.id),
  failedNegativeIds: failedNegative.map((item) => item.id),
  result,
  report: `artifacts/verify-portable/d21-runs/${RUN_ID}/report.json`,
  metrics: rows.map((row) => ({ id: row.id, unit: row.unit, threshold: row.threshold, measured: row.measured, pass: row.pass }))
})

if (existsSync(join(RUN_DIR, 'report.json'))) throw new Error(`runId 目录已存在（拒绝覆写历史）：${RUN_DIR}`)
await mkdir(RUN_DIR, { recursive: true })
await writeFile(join(RUN_DIR, 'report.json'), reportText)
await writeFile(join(RUN_DIR, 'gates.json'), gatesEvidenceText)
await writeFile(
  join(RUN_DIR, 'run.json'),
  evidenceText({
    runId: RUN_ID,
    task: 'D21',
    criteriaVersion: D21_CRITERIA_VERSION,
    startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
    finishedAt: new Date().toISOString(),
    durationMs: Date.now() - RUN_STARTED_AT_MS,
    command: RUN_COMMAND,
    platform: { platform: process.platform, release: osRelease(), arch: process.arch, node: process.version, cwd: process.cwd(), dshBin, dotnet },
    candidate,
    result,
    exitCode: result === 'pass' ? 0 : 1,
    metrics: { total: rows.length, passed: rows.length - verdicts.failedIds.length, failedIds: verdicts.failedIds, notRunIds: verdicts.notRunIds },
    gates: { total: gates.length, passed: gates.length - failed.length, failedGateIds: failed.map((item) => item.id) },
    negativeProbes: { total: negativeProbes.length, passed: negativeProbes.length - failedNegative.length, failedIds: failedNegative.map((item) => item.id) },
    fatal: fatal === null ? null : String(fatal?.stack ?? fatal).slice(0, 600)
  })
)

// 顶层镜像（最新一次）+ 夹具侧副本（down.sh 会删除夹具目录，故两处都写）
await writeFile(join(durableEvidenceDir, 'd21-resource-report.json'), reportText)
await writeFile(join(durableEvidenceDir, 'd21-resource-gates.json'), gatesEvidenceText)
await writeFile(join(evidenceDir, 'd21-resource-gates.json'), gatesEvidenceText)

const indexPath = join(RUNS_DIR, 'index.json')
const index = existsSync(indexPath) ? JSON.parse(await readFile(indexPath, 'utf8')) : { schemaVersion: 1, runs: [] }
if (index.runs.some((item) => item.runId === RUN_ID)) throw new Error(`runId 冲突（拒绝覆写历史）：${RUN_ID}`)
index.runs.push({
  runId: RUN_ID,
  startedAt: new Date(RUN_STARTED_AT_MS).toISOString(),
  finishedAt: new Date().toISOString(),
  durationMs: Date.now() - RUN_STARTED_AT_MS,
  result,
  metrics: `${rows.length - verdicts.failedIds.length}/${rows.length}`,
  failedIds: verdicts.failedIds,
  notRunIds: verdicts.notRunIds,
  gates: `${gates.length - failed.length}/${gates.length}`,
  failedGateIds: failed.map((item) => item.id),
  criteriaVersion: D21_CRITERIA_VERSION,
  candidate: { gitHead: candidate.gitHead, gitBranch: candidate.gitBranch, worktreeDirtyEntries: candidate.gitWorktree.dirtyEntries, pluginVersion: candidate.pluginVersion },
  command: RUN_COMMAND,
  report: `artifacts/verify-portable/d21-runs/${RUN_ID}/report.json`,
  stdout: `artifacts/verify-portable/d21-runs/${RUN_ID}/stdout.log`,
  coreMetrics: coreRun === null ? null : `artifacts/verify-portable/d21-runs/${RUN_ID}/core-metrics`
})
await writeFile(indexPath, evidenceText({ ...index, note: '只增不改：每次运行 push 一条；历史报告在各自的 runId 目录里，本文件与顶层 latest 镜像都不得作为历史依据。' }))

console.log(`\nD21 指标：${rows.length - verdicts.failedIds.length}/${rows.length} 在阈值内；未采样=${verdicts.notRunIds.length}`)
console.log(`D21 判据：${gates.length - failed.length}/${gates.length} 通过；负向探针 ${negativeProbes.length - failedNegative.length}/${negativeProbes.length} 成立`)
if (fatal !== null) console.error(`致命异常：${String(fatal?.stack ?? fatal).slice(0, 600)}`)
console.log(`[run] runId=${RUN_ID} 判据版本=${D21_CRITERIA_VERSION}`)
console.log(`[run] 报告=artifacts/verify-portable/d21-runs/${RUN_ID}/report.json（顶层镜像 d21-resource-report.json）`)
console.log(`[run] 证据=artifacts/verify-portable/d21-resource-gates.json 与 artifacts/fixture/evidence/d21-resource-gates.json`)
if (failed.length > 0 || failedNegative.length > 0 || verdicts.result !== 'pass') {
  console.error('失败项：')
  for (const item of [...failed, ...failedNegative]) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
  process.exitCode = 1
} else {
  console.log('d21-resource-gates: PASS')
}
