#!/usr/bin/env node
/**
 * composer 健康门：真实 Chromium 打开夹具页面后，客户端插件是否真的装配起来了。
 *
 * 存在理由（D08 教训）：D04 的 F05 只检查页面骨架（`__DSH_BOOT__`/`__ModuleLoader__`），
 * 因此"页面 200 + 有骨架"曾经掩盖了"client 模块整批解析失败、composer 根本没渲染"。
 * 本门断言 UI 真的可用，而不只是文档能取回。
 *
 * 判据：
 *   C01 页面无插件加载失败（无 "Failed to load plugins"，无未捕获异常）
 *   C02 真实出现 [data-composer-card]
 *   C03 composer 卡片内真实出现唯一 input[type=file][multiple]（D08 适配器的入口）
 *   C04 client 模块加载完成（__DSH_BOOT_READY__ 存在）
 *
 * 历史：本脚本前身是 `patch-probe.mjs`，用于验证"上游缺少 type=\"module\""这一假设。
 * 该假设已被证伪（真正原因在本包 client 入口的构建形态，见 R09-D08.md），所以那条判据
 * （引导标签带 type="module"）已随假设一并退役，不再断言。
 */

import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createClient, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const pluginRoot = resolve(here, '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const port = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const gates = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

await mkdir(evidenceDir, { recursive: true })
await mkdir(durableEvidenceDir, { recursive: true })

const serviceLog = join(evidenceDir, 'service-composer-gates.log')
const service = startService(dshBin, ['--profile', 'dsh-attachments-fixture', '--no-open'], {
  cwd: repoRoot,
  dshHome,
  logPath: serviceLog
})
await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))

let chrome = null
let page = null
try {
  const readyUrl = await waitForReady(service.child, serviceLog, 120_000)
  const lanAddress = existsSync(join(fixtureRoot, 'lan-address.txt'))
    ? (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
    : '127.0.0.1'
  const lanBase = `http://${lanAddress}:${port}`
  console.log(`composer 健康门（${lanBase}）\n`)

  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}'
  })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair')

  const appPage = await client.follow(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId ?? '')}`)
  const html = appPage.text
  await writeFile(join(evidenceDir, 'index-patched.html'), html)

  const bootstrapTag = html.match(/<script[^>]*dsh-client-modules\/client\.js[^>]*>/)?.[0] ?? ''

  chrome = await launchChrome({})
  page = await openPage(chrome.debugPort)
  const errors = []
  page.onEvent((m) => {
    if (m.method === 'Runtime.exceptionThrown') {
      errors.push(String(m.params.exceptionDetails?.exception?.description ?? m.params.exceptionDetails?.text ?? '').slice(0, 220))
    }
    if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') {
      errors.push((m.params.args ?? []).map((a) => a.value ?? a.description ?? '').join(' ').slice(0, 220))
    }
  })
  await page.send('Page.enable')
  await page.send('Runtime.enable')
  await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId ?? '')}`)
  await new Promise((r) => setTimeout(r, 12_000))

  const state = JSON.parse(
    await page.evaluate(`JSON.stringify({
      cards: document.querySelectorAll('[data-composer-card]').length,
      fileInputs: document.querySelectorAll('input[type=file]').length,
      multipleFileInputs: document.querySelectorAll('input[type=file][multiple]').length,
      inputsPerCard: Array.from(document.querySelectorAll('[data-composer-card]')).map(
        (card) => card.querySelectorAll('input[type=file]').length
      ),
      bootReady: typeof globalThis.__DSH_BOOT_READY__,
      bodyText: document.body.innerText.slice(0, 160)
    })`)
  )
  await writeFile(
    join(evidenceDir, 'composer-gates-state.json'),
    JSON.stringify({ bootstrapTag, state, errors }, null, 2)
  )

  const loadFailed = errors.some((e) => /Failed to load plugins|Cannot use import statement outside a module/i.test(e))
  gate(
    'C01',
    '页面无插件加载失败（含未捕获异常）',
    !loadFailed && errors.length === 0,
    errors.length > 0 ? errors[0].slice(0, 160) : '无相关报错'
  )
  gate('C02', '真实渲染出 [data-composer-card]', state.cards > 0, `cards=${state.cards}`)
  const uniquePerCard = state.inputsPerCard.length > 0 && state.inputsPerCard.every((count) => count === 1)
  gate(
    'C03',
    'composer 卡片内真实出现唯一 input[type=file][multiple]（D08 适配器入口）',
    state.multipleFileInputs > 0 && uniquePerCard,
    `multipleFileInputs=${state.multipleFileInputs} 每卡片 file input 数=${JSON.stringify(state.inputsPerCard)}`
  )
  gate('C04', 'client 模块加载完成（__DSH_BOOT_READY__ 存在）', state.bootReady !== 'undefined', `bootReady=${state.bootReady} 页面正文=${state.bodyText.slice(0, 60)}`)

  await page.close(); page = null
  await chrome.close(); chrome = null
} finally {
  if (page) await page.close().catch(() => {})
  if (chrome) await chrome.close().catch(() => {})
  await stopService(service.child, join(fixtureRoot, 'service.pid'))
}

const failed = gates.filter((g) => !g.ok)
const report = JSON.stringify(
  {
    schemaVersion: 1,
    task: 'D08',
    purpose: 'composer-health',
    gates,
    failedGateIds: failed.map((g) => g.id),
    result: failed.length === 0 ? 'pass' : 'fail'
  },
  null,
  2
) + '\n'
await writeFile(join(evidenceDir, 'composer-gates.json'), report)
await writeFile(join(durableEvidenceDir, 'd08-composer-gates.json'), report)

console.log(`\ncomposer 健康门: ${gates.length - failed.length}/${gates.length} 通过`)
if (failed.length > 0) {
  console.error('未通过：')
  for (const g of failed) console.error(`  - ${g.id} ${g.description}：${g.detail}`)
  process.exitCode = 1
} else {
  console.log('composer-gates: PASS')
}
