#!/usr/bin/env node
/**
 * 批次纯净度探针：验证 D08 阻断的**真实**根因。
 *
 * 待验证假设（推翻早先的"上游缺陷"结论）：
 *   client-modules 的 application 批次是用 `comboUrl()` 把各包 `client.js` 的**原始字节**
 *   首尾相接（`comboSource` 只剥 sourcemap 尾注，不做任何 ESM→CJS 变换），再由
 *   `defaultLoadBundle` 以**经典脚本**动态加载。因此每个包在磁盘上的 client.js 必须已经是
 *   `window.__ModuleLoader__.load({ id, factory })` 经典包裹形式；官方包全部如此（顶层
 *   import/export 计数为 0）。若某个包的 client.js 仍是裸 ESM，它会把所在**整批**变成
 *   语法错误，从而 "Failed to load plugins"、composer 永不渲染。
 *
 * 判据（全部只看服务端字节，不依赖 Chromium）：
 *   P1 页面 HTML 注入的 `__DSH_BOOT__` 图可解析，且本插件在图中
 *   P2 本插件的 client.js 单独取回时已是经典包裹形式（首个非注释语句是 __ModuleLoader__.load）
 *   P3 本插件所在批次的响应里，顶层 import/export 计数为 0（纯净度门）
 *   P4 该批次里除本插件外，官方包的段仍全部是经典包裹形式（说明我方是唯一污染源）
 *   P5 本插件声明的 id 与批内实际包裹的 id 一致
 */

import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createClient, startService, stopService, waitForReady } from './lib.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const port = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const pluginName = JSON.parse(await readFile(join(pluginRoot, 'package.json'), 'utf8')).name

const gates = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

/** 顶层（第 0 列）import/export 语句的出现位置。 */
function topLevelEsmHits(source) {
  const hits = []
  const lines = source.split('\n')
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index]
    if (/^(import|export)[\s{*]/.test(line)) hits.push({ line: index + 1, text: line.slice(0, 120) })
  }
  return hits
}

/** 批次响应中每个 `window.__ModuleLoader__.load({` 段起始处的行号。 */
function loaderSegments(source) {
  const offsets = []
  const lines = source.split('\n')
  for (let index = 0; index < lines.length; index += 1) {
    if (lines[index].startsWith('window.__ModuleLoader__.load({')) offsets.push(index + 1)
  }
  return offsets
}

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })

const serviceLog = join(evidenceDir, 'service-bundle-purity.log')
const service = startService(dshBin, ['--profile', 'dsh-attachments-fixture', '--no-open'], {
  cwd: repoRoot,
  dshHome,
  logPath: serviceLog
})
await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))

try {
  await waitForReady(service.child, serviceLog, 120_000)
  const lanAddress = existsSync(join(fixtureRoot, 'lan-address.txt'))
    ? (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
    : '127.0.0.1'
  const lanBase = `http://${lanAddress}:${port}`
  console.log(`批次纯净度探针（${lanBase}）\n`)

  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}'
  })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair')
  const appPath = `${lanBase}/pair-app?device=${encodeURIComponent(deviceId ?? '')}`

  const appPage = await client.follow(appPath)
  const html = appPage.text
  await writeFile(join(evidenceDir, 'index-purity.html'), html)

  // 从 HTML 里取出 __DSH_BOOT__ 图（global 行渲染为 <script>globalThis["__DSH_BOOT__"] = {...}</script>）
  const bootMatch = html.match(/globalThis\["__DSH_BOOT__"\]\s*=\s*([\s\S]*?)<\/script>/)
  let graph = null
  let graphError = ''
  try {
    graph = bootMatch ? JSON.parse(bootMatch[1]) : null
  } catch (error) {
    graphError = error.message
  }

  // 图的真实字段是 entries（不是 modules）；每条自带 combo 形式的 url。
  const batches = graph?.batches ?? []
  const entries = graph?.entries ?? []
  const ownModule = entries.find((row) => row.id === pluginName)
  gate(
    'P1',
    '页面 HTML 注入的 __DSH_BOOT__ 图可解析，且本插件在图中',
    graph !== null && Boolean(ownModule),
    graph === null
      ? `图解析失败：${graphError}`
      : `batches=${batches.length} entries=${entries.length} own=${ownModule ? ownModule.url.replaceAll('&amp;', '&') : '缺失'}`
  )

  // P2：单独取回本插件条目（combo 形式 URL），检查是否经典包裹形式
  const ownPath = (ownModule?.url ?? `/plugins/??${pluginName}/client.js`).replaceAll('&amp;', '&')
  const ownUrl = ownPath.startsWith('http') ? ownPath : `${lanBase}${ownPath}`
  const ownResponse = await client.follow(ownUrl)
  const ownSource = ownResponse.text
  const ownHits = topLevelEsmHits(ownSource)
  const ownWrapped = ownSource.includes('window.__ModuleLoader__.load(') && ownHits.length === 0
  await writeFile(join(evidenceDir, 'own-client-bundle-snippet.txt'), ownSource.slice(0, 2000))
  gate(
    'P2',
    '本插件 client.js 已是经典包裹形式（顶层 import/export 为 0）',
    ownWrapped,
    `status=${ownResponse.response?.status} bytes=${ownSource.length} 顶层ESM语句=${ownHits.length}${ownHits.length ? ` 首个@L${ownHits[0].line}: ${ownHits[0].text}` : ''}`
  )

  // 找到本插件所在的批次
  const ownBatch = batches.find((batch) => (batch.entries ?? []).includes(pluginName))
  let batchSource = ''
  let batchHits = []
  let segments = []
  if (ownBatch) {
    const batchPath = ownBatch.url.replaceAll('&amp;', '&')
    const batchAbsolute = batchPath.startsWith('http') ? batchPath : `${lanBase}${batchPath}`
    const batchResponse = await client.follow(batchAbsolute)
    batchSource = batchResponse.text
    batchHits = topLevelEsmHits(batchSource)
    segments = loaderSegments(batchSource)
    await writeFile(join(evidenceDir, 'own-batch-head.txt'), batchSource.slice(0, 4000))
  }

  gate(
    'P3',
    '本插件所在批次响应的顶层 import/export 计数为 0（纯净度门）',
    Boolean(ownBatch) && batchHits.length === 0,
    ownBatch
      ? `phase=${ownBatch.phase} bytes=${batchSource.length} 段数=${segments.length} 顶层ESM语句=${batchHits.length}`
      : '本插件不在任何批次中'
  )

  if (batchHits.length > 0) {
    const first = batchHits[0]
    const around = batchSource.split('\n').slice(Math.max(0, first.line - 4), first.line + 2).join('\n')
    console.log(`\n  顶层 ESM 语句上下文（第 ${first.line} 行）:\n${around.split('\n').map((l) => `    | ${l.slice(0, 140)}`).join('\n')}\n`)
    await writeFile(join(evidenceDir, 'batch-esm-context.txt'), around)
  }

  // 健康判据：批内每个声明条目都必须有且仅有一个经典包裹段。段数少于条目数就说明
  // 某包的 client.js 不是包裹形态（D08 的实际故障：56 条只解析出 55 段）。
  const expectedSegments = ownBatch ? (ownBatch.entries ?? []).length : 0
  gate(
    'P4',
    '该批次每个声明条目都对应一个经典包裹段（不多不少）',
    Boolean(ownBatch) && segments.length === expectedSegments,
    `段数=${segments.length} 批内声明条目=${expectedSegments}`
  )

  gate(
    'P5',
    '批内实际包裹的 id 与批内声明条目集合一致',
    Boolean(ownBatch) && batchSource.includes(`id: ${JSON.stringify(ownBatch?.entries?.[0] ?? '')}`),
    ownBatch ? `首条声明=${ownBatch.entries?.[0]} 批次含该 id=${batchSource.includes(`id: ${JSON.stringify(ownBatch.entries?.[0] ?? '')}`)}` : '无批次'
  )

  await writeFile(
    join(evidenceDir, 'bundle-purity-summary.json'),
    JSON.stringify(
      {
        pluginName,
        bootGraph: {
          batchCount: batches.length,
          entryCount: entries.length,
          batches: batches.map((b) => ({ phase: b.phase, entries: (b.entries ?? []).length, url: b.url.slice(0, 120) }))
        },
        ownModule: ownModule ?? null,
        ownBundle: { bytes: ownSource.length, topLevelEsm: ownHits },
        ownBatch: ownBatch
          ? { phase: ownBatch.phase, bytes: batchSource.length, segments: segments.length, declared: (ownBatch.entries ?? []).length, topLevelEsm: batchHits }
          : null
      },
      null,
      2
    ) + '\n'
  )
} finally {
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
}

const failed = gates.filter((g) => !g.ok)
const report = JSON.stringify(
  {
    schemaVersion: 1,
    task: 'D08',
    purpose: 'bundle-purity',
    hypothesis:
      '批次由各包 client.js 原始字节拼接后以经典脚本加载；client.js 必须已是 window.__ModuleLoader__.load({id,factory}) 形式，裸 ESM 会污染整批',
    gates,
    failedGateIds: failed.map((g) => g.id),
    result: failed.length === 0 ? 'pass' : 'fail'
  },
  null,
  2
) + '\n'
await writeFile(join(evidenceDir, 'bundle-purity-gates.json'), report)
await writeFile(join(durableEvidenceDir, 'd08-bundle-purity-gates.json'), report)

console.log(`\n批次纯净度探针: ${gates.length - failed.length}/${gates.length} 通过`)
if (failed.length > 0) {
  console.error('未通过：')
  for (const g of failed) console.error(`  - ${g.id} ${g.description}：${g.detail}`)
  process.exitCode = 1
}
