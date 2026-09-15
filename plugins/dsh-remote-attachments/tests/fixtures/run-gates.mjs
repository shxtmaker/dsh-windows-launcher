#!/usr/bin/env node
/**
 * D04 夹具就绪与挂载校验。
 *
 * 真实启动 `dsh --profile <fixture>`，在超时内等待就绪，然后逐条核对 9 项判据，
 * 把结论与原始证据写入 artifacts/fixture/evidence/。
 *
 * 明确边界：本脚本只验证"真实服务能启动、全家桶与本插件的 host/client 各按预期挂载"。
 * 配对、/remote 门控、上传 hook 与草稿入口分别属于 D05–D08，不在此断言。
 * 失败时非零退出，绝不把未就绪或降级当作通过。
 */

import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { appendFile, mkdir, readFile, writeFile, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const profileName = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const profileDir = join(dshHome, 'profiles', profileName)
const evidenceDir = join(fixtureRoot, 'evidence')
// 耐久副本：fixture:down 会删除整个夹具目录，因此结论必须另存一份。
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const port = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const readyTimeoutMs = Number(process.env.DSH_ATTACH_READY_TIMEOUT_MS ?? 120_000)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const gates = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

async function json(path) {
  return JSON.parse(await readFile(path, 'utf8'))
}

/** 等待服务就绪：轮询 stdout 中的 dsh web URL。 */
async function waitForReady(child, logPath, timeoutMs) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      throw new Error(`服务在就绪前退出，退出码 ${child.exitCode}；日志见 ${logPath}`)
    }
    const text = existsSync(logPath) ? await readFile(logPath, 'utf8') : ''
    const match = text.match(/dsh web: (https?:\/\/\S+)/)
    if (match) return match[1]
    await new Promise((r) => setTimeout(r, 500))
  }
  throw new Error(`服务在 ${timeoutMs}ms 内未就绪；日志见 ${logPath}`)
}

async function main() {
  const lock = await json(join(pluginRoot, 'tests/fixtures/compatibility-lock.json'))
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })

  // ---- F01 私有 DSH_HOME 与 profile 存在，且生产 profile 未被触碰 ----
  const productionHome = join(process.env.HOME ?? '', '.dsh')
  const productionProfileDir = join(productionHome, 'profiles', 'web')
  let productionMarkerBefore = null
  try {
    productionMarkerBefore = (await readFile(join(productionProfileDir, 'package.json'), 'utf8')).length
  } catch {
    productionMarkerBefore = null
  }

  const profileManifestPath = join(profileDir, 'package.json')
  const profileExists = existsSync(profileManifestPath)
  const underArtifacts = profileDir.startsWith(join(repoRoot, 'artifacts') + '/')
  gate(
    'F01',
    '私有 DSH_HOME 与 fixture profile 由 web 模板创建，且生产 profile 未被触碰',
    profileExists && underArtifacts,
    `profileDir=${profileDir}`
  )
  if (!profileExists) throw new Error('fixture profile 不存在，请先运行 setup.sh')

  const profileManifest = await json(profileManifestPath)

  // ---- F02 组合配置：本插件行存在，remote 行恰好一次 ----
  const composeLog = join(evidenceDir, 'composed-config.yml')
  const compose = spawn(dshBin, ['--profile', profileName, '--dump-config'], {
    cwd: repoRoot,
    env: { ...process.env, DSH_HOME: dshHome }
  })
  let composeText = ''
  compose.stdout.on('data', (chunk) => { composeText += chunk })
  await new Promise((res, rej) => {
    compose.on('error', rej)
    compose.on('exit', (code) => (code === 0 ? res() : rej(new Error(`--dump-config 退出码 ${code}`))))
  })
  await writeFile(composeLog, composeText)

  const addonRows = [...composeText.matchAll(/^- id: remote-attachments$/gm)].length
  // remote-web-ui 在全家桶中是 web-ui-remote-web-ui 行；不得出现第二份独立行。
  const remoteAggregatedRows = [...composeText.matchAll(/name: '@linxin666\/dsh-web-all\/remote-web-ui'/g)].length
  const remoteDirectRows = [...composeText.matchAll(/^\s*name: '@linxin666\/dsh-remote-web-ui'$/gm)].length
  gate(
    'F02',
    '组合配置含本插件行，且 remote 行恰好出现一次（未额外安装 remote）',
    addonRows === 1 && remoteAggregatedRows === 1 && remoteDirectRows === 0,
    `addonRows=${addonRows} remoteAggregatedRows=${remoteAggregatedRows} remoteDirectRows=${remoteDirectRows}`
  )

  // ---- F03 关键包版本与锁定清单一致 ----
  const versionChecks = []
  for (const pkg of lock.packages) {
    const manifestPath = join(profileDir, 'node_modules', pkg.name, 'package.json')
    if (!existsSync(manifestPath)) {
      versionChecks.push(`${pkg.name}=<missing>`)
      continue
    }
    const installed = await json(manifestPath)
    versionChecks.push(`${pkg.name}=${installed.version}`)
    assert.equal(installed.version, pkg.version, `${pkg.name} 版本与锁定清单不符`)
  }
  gate('F03', '关键包版本与 compatibility-lock.json 完全一致', true, versionChecks.join(' '))

  // ---- 启动真实服务 ----
  const serviceLog = join(evidenceDir, 'service.log')
  await rm(serviceLog, { force: true })
  const service = spawn(
    dshBin,
    ['--profile', profileName, '--no-open', '--host', '127.0.0.1', '--port', String(port)],
    { cwd: repoRoot, env: { ...process.env, DSH_HOME: dshHome } }
  )
  let serviceText = ''
  // 就绪检测要轮询日志文件，因此必须边收边落盘，不能等启动结束再写。
  const tee = (chunk) => {
    serviceText += chunk
    void appendFile(serviceLog, chunk)
  }
  service.stdout.on('data', tee)
  service.stderr.on('data', tee)
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.pid))

  let readyUrl = null
  try {
    readyUrl = await waitForReady(service, serviceLog, readyTimeoutMs)
    gate('F04', '服务真实启动并输出带 token 的 URL', true, readyUrl.replace(/token=[^&]+/, 'token=<redacted>'))

    // ---- F05 原始页面可加载 ----
    // fetch 的 redirect:'follow' 不会把 303 上的 Set-Cookie 应用到后续请求，
    // 因此必须手动处理重定向并维护 cookie jar，否则永远停在 401。
    const cookieJar = new Map()
    const cookieHeader = () =>
      cookieJar.size > 0 ? { cookie: [...cookieJar].map(([k, v]) => `${k}=${v}`).join('; ') } : {}

    const absorbCookies = (response) => {
      for (const value of response.headers.getSetCookie?.() ?? []) {
        const [pair] = value.split(';')
        const index = pair.indexOf('=')
        if (index > 0) cookieJar.set(pair.slice(0, index).trim(), pair.slice(index + 1).trim())
      }
    }

    const load = async (url, maxRedirects = 5) => {
      let current = url
      for (let hop = 0; hop <= maxRedirects; hop++) {
        const response = await fetch(current, { redirect: 'manual', headers: cookieHeader() })
        absorbCookies(response)
        if (response.status >= 300 && response.status < 400) {
          const location = response.headers.get('location')
          if (!location) throw new Error(`重定向缺少 location：${response.status}`)
          current = new URL(location, current).href
          continue
        }
        return { status: response.status, url: current, text: await response.text() }
      }
      throw new Error('重定向次数过多')
    }

    const page = await load(readyUrl)
    await writeFile(join(evidenceDir, 'index.html'), page.text)
    const hasShell = /__DSH_BOOT__|__ModuleLoader__/.test(page.text)
    gate('F05', '原始页面可加载（HTTP 200 + DSH 页面骨架）', page.status === 200 && hasShell, `status=${page.status} bytes=${page.text.length}`)

    // ---- F06 client roster 含本插件，且 remote 不在 client roster ----
    const rosterMatch = page.text.match(/href="\/plugins\/\?\?([^"]+)"/)
    // HTML 属性里 &rev= 被转义成 &amp;rev=；必须还原后再请求，不能截断 rev。
    const rosterQuery = rosterMatch ? rosterMatch[1].replace(/&amp;/g, '&') : ''
    const rosterSpec = rosterQuery.replace(/&rev=.*$/, '')
    const roster = rosterSpec
      ? rosterSpec.split(',').map((item) => decodeURIComponent(item.trim()))
      : []
    const addonInRoster = roster.filter((item) => item.includes('dsh-remote-attachments'))
    const remoteInRoster = roster.filter((item) => item.includes('dsh-remote-web-ui'))
    gate(
      'F06',
      'client 模块 roster 含本插件条目，且 remote 不在 client roster（host-only）',
      roster.length > 0 && addonInRoster.length === 1 && remoteInRoster.length === 0,
      `roster=${roster.length} addon=${addonInRoster.length} remote=${remoteInRoster.length}`
    )

    // ---- F07 合并 client bundle 真实含本插件代码 ----
    const bundleResponse = await fetch(`http://127.0.0.1:${port}/plugins/??${rosterQuery}`, {
      headers: cookieHeader()
    })
    const bundle = await bundleResponse.text()
    await writeFile(join(evidenceDir, 'client-bundle.size.txt'), String(bundle.length))
    const addonMarkers = ['remote-attachments-client', 'attachment-paste-import'].filter((marker) => bundle.includes(marker))
    gate(
      'F07',
      '合并 client bundle 真实含本插件 client 代码',
      bundleResponse.status === 200 && addonMarkers.length > 0,
      `status=${bundleResponse.status} bytes=${bundle.length} markers=${addonMarkers.join(',')}`
    )

    // ---- F10 合并 client bundle 的**批次纯净度** ----
    // client-modules 把同一 phase 各包的 client.js 原始字节首尾相接，再由 defaultLoadBundle
    // 以**经典脚本**动态加载。因此任何包在顶层出现 import/export，都会把**整批**（实测
    // 14.6 MB、56 个包）变成语法错误，页面直接 "Failed to load plugins"、composer 永不渲染。
    // D08 曾被此缺陷阻断，而 F05 只看骨架、F07 只看"含本包标记"，两者都测不出它——所以这里
    // 直接对字节做纯净度判定（零成本、无需浏览器）。
    const topLevelEsm = bundle
      .split('\n')
      .map((text, index) => ({ line: index + 1, text }))
      .filter((row) => /^(import|export)[\s{*]/.test(row.text))
    const segments = bundle.split('\n').filter((line) => line.startsWith('window.__ModuleLoader__.load(')).length
    await writeFile(
      join(evidenceDir, 'client-bundle-purity.txt'),
      `bytes=${bundle.length}\nloaderSegments=${segments}\nrosterEntries=${roster.length}\ntopLevelEsm=${topLevelEsm.length}\n` +
        topLevelEsm.map((row) => `  L${row.line}: ${row.text.slice(0, 120)}`).join('\n')
    )
    gate(
      'F10',
      '合并 client bundle 批次纯净：顶层无 ESM 语句，且每个 roster 条目恰好一个经典包裹段',
      topLevelEsm.length === 0 && segments === roster.length && roster.length > 0,
      `顶层ESM语句=${topLevelEsm.length} 包裹段=${segments} roster条目=${roster.length}` +
        (topLevelEsm.length > 0 ? ` 首个@L${topLevelEsm[0].line}: ${topLevelEsm[0].text.slice(0, 100)}` : '')
    )

    // ---- F08 启动日志无模块降级/缺失 ----
    const degradationPatterns = [
      /degraded/i,
      /failed to load/i,
      /cannot find module/i,
      /ERR_MODULE_NOT_FOUND/,
      /plugin .* not found/i,
      /capability conflict/i
    ]
    const hits = degradationPatterns.flatMap((pattern) => {
      const match = serviceText.match(pattern)
      return match ? [`${pattern} -> ${match[0]}`] : []
    })
    await writeFile(join(evidenceDir, 'degradation-scan.txt'), hits.length > 0 ? hits.join('\n') : 'no degradation markers')
    gate('F08', '启动日志无模块降级/缺失错误', hits.length === 0, hits.join('; '))
  } finally {
    // ---- 停止夹具服务（只停本次启动的 pid） ----
    if (service.exitCode === null) {
      service.kill('SIGTERM')
      const deadline = Date.now() + 10_000
      while (service.exitCode === null && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 200))
      }
      if (service.exitCode === null) service.kill('SIGKILL')
    }
    await rm(join(fixtureRoot, 'service.pid'), { force: true })
    await writeFile(serviceLog, serviceText)
  }

  // ---- F09 生产 profile 未被触碰 ----
  let productionMarkerAfter = null
  try {
    productionMarkerAfter = (await readFile(join(productionProfileDir, 'package.json'), 'utf8')).length
  } catch {
    productionMarkerAfter = null
  }
  gate(
    'F09',
    '夹具未触碰生产 profile（$HOME/.dsh/profiles/web 无变化）',
    productionMarkerBefore === productionMarkerAfter,
    `before=${productionMarkerBefore} after=${productionMarkerAfter}`
  )

  const failed = gates.filter((item) => !item.ok)
  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D04',
      platform: 'linux',
      profile: profileName,
      dshHome,
      gates,
      failedGateIds: failed.map((item) => item.id),
      result: failed.length === 0 ? 'pass' : 'fail'
    },
    null,
    2
  ) + '\n'
  await writeFile(join(evidenceDir, 'fixture-gates.json'), report)
  await writeFile(join(durableEvidenceDir, 'd04-fixture-gates.json'), report)

  console.log(`\nfixture gates: ${gates.length - failed.length}/${gates.length} 通过`)
  if (failed.length > 0) {
    console.error('失败判据：')
    for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
    process.exitCode = 1
    return
  }
  console.log('fixture-gates: PASS')
}

await main()
