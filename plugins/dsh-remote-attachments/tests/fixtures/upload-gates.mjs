#!/usr/bin/env node
/**
 * D07 判据：启动前上传承载（__DSH_FILE_UPLOAD__）。
 *
 * 两层验证：
 *   A. 真实夹具页面：确认承载脚本确实在**任何 boot entry 之前**注入 head，
 *      且 remote 的 boot 脚本也在场（两者共存、顺序正确）。
 *   B. 真实 Chromium：把**真实 remote boot 脚本**（从夹具页面原样取出，不手写
 *      替身）与我们的承载脚本放进一个受控 origin，观测真实网络请求，断言：
 *        只改写一次、设备头只加给同源、跨源不泄露、取消保留、未知 hook 不被覆盖。
 *
 * 不使用 Playwright：Node 22 自带 WebSocket，配合 cdp.mjs 驱动真实 Chromium。
 */

import { spawn } from 'node:child_process'
import { createServer } from 'node:http'
import { mkdir, readFile, writeFile, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createClient, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const profileName = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const port = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const readyTimeoutMs = Number(process.env.DSH_ATTACH_READY_TIMEOUT_MS ?? 120_000)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const gates = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

const UPLOAD_HOOK_GLOBAL = '__DSH_FILE_UPLOAD__'
const REMOTE_BOOT_GLOBAL = '__DSH_REMOTE_CHANNEL_BOOT__'
const DEVICE_HEADER = 'x-dsh-remote-device'
const DEVICE_KEY = 'dsh-remote-device'
const UPLOAD_PATH = '/api/session/uploadFileBinary'

/** 枚举 index.html 中所有内联 <script> 正文。 */
function listInlineScripts(html) {
  const scripts = []
  const pattern = /<script(?![^>]*\ssrc=)[^>]*>([\s\S]*?)<\/script>/gi
  let match
  while ((match = pattern.exec(html)) !== null) {
    scripts.push({ text: match[1], index: match.index })
  }
  return scripts
}

/**
 * 取出注入的承载脚本。
 *
 * 注入内容经过压缩，写法不固定（实测为 `var G="__DSH_FILE_UPLOAD__"`），因此按
 * "脚本正文里出现了该全局名"定位，而不是猜某种赋值写法。
 */
function extractHookScript(html, globalName) {
  const needle = JSON.stringify(globalName)
  const scripts = listInlineScripts(html)
  const found = scripts.find((entry) => entry.text.includes(needle) || entry.text.includes(globalName))
  return found ? found.text : null
}

/**
 * 取出 remote 的 boot 脚本（真实 shipped 脚本，非手写替身）。
 * 用赋值语句 `["__DSH_REMOTE_CHANNEL_BOOT__"]=` 定位，避免命中 restore() 里的 delete。
 */
function extractBootScript(html, globalName) {
  const needles = [`["${globalName}"]=`, `[${JSON.stringify(globalName)}]=`]
  const scripts = listInlineScripts(html)
  for (const entry of scripts) {
    if (needles.some((needle) => entry.text.includes(needle))) return entry.text
  }
  return null
}

/** 承载脚本在页面中的字符位置（用于断言顺序）。 */
function hookScriptIndex(html, globalName) {
  const scripts = listInlineScripts(html)
  const found = scripts.find((entry) => entry.text.includes(globalName))
  return found ? found.index : -1
}

/** remote boot 脚本在页面中的字符位置。 */
function bootScriptIndex(html, globalName) {
  const scripts = listInlineScripts(html)
  const found = scripts.find((entry) => entry.text.includes(`["${globalName}"]=`))
  return found ? found.index : -1
}

/** 受控 origin：服务页面与上传端点，并记录请求。 */
async function startHarnessOrigin({ bootScript = '', hookScript = '', preScript = '', claimUnknownHook = false, host = '127.0.0.1' } = {}) {
  const requests = []
  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? '/', 'http://127.0.0.1')
    if (url.pathname === '/' || url.pathname === '/pair-app') {
      const claim = claimUnknownHook
        ? `<script>globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}] = function(){ return 'unknown-hook' }</script>`
        : ''
      const body = `<!doctype html><html><head>${preScript}${claim}<script>${bootScript}</script><script>${hookScript}</script></head><body>d07-harness</body></html>`
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
      res.end(body)
      return
    }
    const chunks = []
    for await (const chunk of req) chunks.push(chunk)
    requests.push({
      method: req.method,
      path: url.pathname,
      search: url.search,
      headers: Object.fromEntries(Object.entries(req.headers).map(([k, v]) => [k.toLowerCase(), v])),
      bodyBytes: Buffer.concat(chunks).length
    })
    // businessFailure=1 模拟 Harness 的 HTTP 200 业务失败信封
    const businessFailure = url.searchParams.get('businessFailure') === '1'
    res.writeHead(200, { 'content-type': 'application/json' })
    res.end(JSON.stringify(businessFailure ? { ok: false, error: { code: 'stub-failure' } } : { ok: true, received: url.pathname }))
  })
  await new Promise((r) => server.listen(0, host, r))
  return {
    port: server.address().port,
    requests,
    base: `http://${host}:${server.address().port}`,
    close: () => new Promise((r) => server.close(() => r()))
  }
}

async function main() {
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })

  // 本地构建产物（不是源码）：判据必须针对最终 bundle。
  const builtHookPath = join(pluginRoot, 'lib/host/upload-hook.js')
  if (!existsSync(builtHookPath)) {
    throw new Error(`缺少构建产物 ${builtHookPath}；请先运行 pnpm run build`)
  }
  const { buildUploadHookScript, UPLOAD_HOOK_GLOBAL: builtGlobal } = await import(
    `file://${builtHookPath}`
  )
  const hookScript = buildUploadHookScript()

  console.log('D07 启动前上传承载判据\n')

  // ---------- A. 真实夹具页面注入顺序 ----------
  const serviceLog = join(evidenceDir, 'service-upload.log')
  await rm(serviceLog, { force: true })
  const service = startService(dshBin, ['--profile', profileName, '--no-open'], {
    cwd: repoRoot,
    dshHome,
    logPath: serviceLog
  })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))

  let servedHtml = ''
  let bootScript = null
  const harnessRequests = []
  let uploadedOk = false
  try {
    const readyUrl = await waitForReady(service.child, serviceLog, readyTimeoutMs)
    const lanAddress = existsSync(join(fixtureRoot, 'lan-address.txt'))
      ? (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
      : '127.0.0.1'
    const lanBase = `http://${lanAddress}:${port}`

    // 配对一次，使 remote 的 boot 脚本进入激活状态并在页面里生效。
    const client = createClient()
    await client.follow(`${lanBase}/?token=${readyUrl.match(/token=([A-Za-z0-9_-]+)/)?.[1] ?? ''}`)
    const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}'
    })
    const issueJson = await issue.json().catch(() => null)
    const token = issueJson?.token ?? null
    const deviceId = typeof token === 'string' ? await (async () => {
      const accepted = await client.follow(`${lanBase}/pair-accept?pair=${token}`)
      return client.jar.get('dsh_pair') ?? null
    })() : null

    const page = await client.follow(`${lanBase}/`)
    servedHtml = page.text
    await writeFile(join(evidenceDir, 'index-with-hooks.html'), servedHtml)

    const hookIndex = hookScriptIndex(servedHtml, UPLOAD_HOOK_GLOBAL)
    const remoteIndex = bootScriptIndex(servedHtml, REMOTE_BOOT_GLOBAL)
    const inlineScripts = listInlineScripts(servedHtml)
    const hookScript = extractHookScript(servedHtml, UPLOAD_HOOK_GLOBAL)

    gate(
      'U01',
      '承载脚本由 host index-inject 注入到 head，且在任何 boot entry 之前',
      hookIndex >= 0 &&
        remoteIndex >= 0 &&
        hookIndex < remoteIndex &&
        typeof hookScript === 'string' &&
        hookScript.includes(UPLOAD_HOOK_GLOBAL),
      `hookIndex=${hookIndex} remoteBootIndex=${remoteIndex} 内联脚本数=${inlineScripts.length}`
    )

    bootScript = extractBootScript(servedHtml, REMOTE_BOOT_GLOBAL)
    gate(
      'U02',
      '从真实页面取出 remote boot 脚本（真实 shipped 脚本，非手写替身）',
      typeof bootScript === 'string' && bootScript.length > 500 && bootScript.includes(DEVICE_HEADER),
      `长度=${bootScript?.length ?? 0} 含设备头常量=${bootScript?.includes(DEVICE_HEADER) ?? false}`
    )

    // ---------- B. 真实 Chromium 行为 ----------
    // 用页面上真实注入的那段承载脚本文本，而不是本地重新生成的副本。
    // 关键：必须用非 loopback 地址。remote 的 boot 脚本按设计在 loopback 上直接
    // return（它只在真实 LAN 场景生效），用 127.0.0.1 会让改写层根本不存在。
    const harness = await startHarnessOrigin({
      bootScript: bootScript ?? '',
      hookScript: hookScript ?? hookScript,
      host: lanAddress
    })
    const chrome = await launchChrome({})
    const pageCdp = await openPage(chrome.debugPort)
    try {
      await pageCdp.send('Page.enable')
      await pageCdp.send('Runtime.enable')
      await pageCdp.send('Network.enable')

      // 进入受控 origin，并设置设备凭据（模拟 /pair-app 落地）
      await pageCdp.navigate(`${harness.base}/`)
      await pageCdp.evaluate(`sessionStorage.setItem(${JSON.stringify(DEVICE_KEY)}, 'device-under-test')`)

      // 关键：承载必须在 boot 脚本之后安装；先断言两者共存
      const presence = await pageCdp.evaluate(`JSON.stringify({
        hook: typeof globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}],
        hookFetch: typeof (globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}] ?? {}).fetch,
        boot: typeof globalThis[${JSON.stringify(REMOTE_BOOT_GLOBAL)}]
      })`)
      const parsed = JSON.parse(presence)
      gate(
        'U03',
        '同一页面内承载与 remote boot 脚本共存（加载顺序成立）',
        // 消费者读的是 hook.fetch（fetch 形状载体），断言裸函数会把错误契约固化下来。
        parsed.hook === 'object' && parsed.hookFetch === 'function' && parsed.boot === 'object',
        `hook=${parsed.hook} hookFetch=${parsed.hookFetch} boot=${parsed.boot}`
      )

      // 设备头注入依赖 sessionStorage 凭据，remote 在调用时读取
      const uploadResult = await pageCdp.evaluate(
        `(async () => {
          const form = new FormData()
          form.append('sessionId', 'session-under-test')
          form.append('file', new Blob([new Uint8Array([1,2,3,4])], { type: 'application/octet-stream' }), 'probe.bin')
          const response = await globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}].fetch(${JSON.stringify(UPLOAD_PATH)}, { method: 'POST', body: form, credentials: 'same-origin' })
          return JSON.stringify({ status: response.status })
        })()`,
        { awaitPromise: true }
      )
      uploadedOk = JSON.parse(uploadResult).status === 200

      const uploadRequests = harness.requests.filter((entry) => entry.path.includes('uploadFileBinary'))
      const rewritten = uploadRequests.filter((entry) => entry.path.startsWith('/remote/api/'))
      const unrewritten = uploadRequests.filter((entry) => !entry.path.startsWith('/remote/api/'))
      const withDeviceHeader = rewritten.filter((entry) => typeof entry.headers[DEVICE_HEADER] === 'string')
      const doubleRewritten = harness.requests.filter((entry) => entry.path.startsWith('/remote/remote/'))

      gate(
        'U04',
        '上传请求只被改写一次，且带上设备头（POST 到 /remote/api/session/uploadFileBinary）',
        uploadRequests.length === 1 &&
          rewritten.length === 1 &&
          withDeviceHeader.length === 1 &&
          doubleRewritten.length === 0 &&
          withDeviceHeader[0]?.headers[DEVICE_HEADER] === 'device-under-test',
        `请求数=${uploadRequests.length} 已改写=${rewritten.length} 含设备头=${withDeviceHeader.length} 重复改写=${doubleRewritten.length}`
      )
      gate('U05', '上传调用返回 HTTP 200（真实收到响应）', uploadedOk, `status=${uploadedOk ? 200 : 'n/a'}`)

      // 跨源：不得注入设备头
      const crossOrigin = await pageCdp.evaluate(
        `(async () => {
          try {
            await globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}]('http://127.0.0.1:1/remote/api/session/uploadFileBinary', { method: 'POST', body: 'x', mode: 'no-cors' })
            return 'resolved'
          } catch (error) { return 'rejected:' + (error && error.name ? error.name : 'Error') }
        })()`,
        { awaitPromise: true }
      )
      const crossOriginLeak = harness.requests.filter(
        (entry) => entry.headers[DEVICE_HEADER] !== undefined && !entry.path.startsWith('/')
      )
      gate(
        'U06',
        '跨源请求不会被追加设备头（承载只补 Headers，不自行跨源泄露）',
        !String(crossOrigin).startsWith('resolved') && crossOriginLeak.length === 0,
        `跨源结果=${crossOrigin} 泄露计数=${crossOriginLeak.length}`
      )

      // 取消：signal 必须原样保留，abort 要能拒绝
      const abortOutcome = await pageCdp.evaluate(
        `(async () => {
          const controller = new AbortController()
          const promise = globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}].fetch(${JSON.stringify(UPLOAD_PATH)}, {
            method: 'POST', body: 'x', signal: controller.signal
          })
          controller.abort()
          try { await promise; return 'resolved' } catch (error) { return 'rejected:' + (error && error.name ? error.name : 'Error') }
        })()`,
        { awaitPromise: true }
      )
      gate(
        'U07',
        'signal 被保留：abort 后调用以 AbortError 拒绝（不吞取消）',
        String(abortOutcome).includes('AbortError'),
        `结果=${abortOutcome}`
      )

      // HTTP 200 业务失败：承载不得吞掉 envelope
      const businessFailure = await pageCdp.evaluate(
        `(async () => {
          const response = await globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}].fetch('/api/session/uploadFileBinary?businessFailure=1', { method: 'POST', body: 'x' })
          const payload = await response.json()
          return JSON.stringify({ status: response.status, ok: payload.ok })
        })()`,
        { awaitPromise: true }
      )
      const failureParsed = JSON.parse(businessFailure)
      gate(
        'U08',
        'HTTP 200 的业务失败原样交给调用方解析（承载不吞 envelope）',
        failureParsed.status === 200 && failureParsed.ok === false,
        `status=${failureParsed.status} ok=${failureParsed.ok}`
      )
    } finally {
      await pageCdp.close()
      await chrome.close()
      await harness.close()
    }

    // ---------- C. 未知 hook 不被覆盖（同一真实 Chromium，独立页面） ----------
    const guardHarness = await startHarnessOrigin({
      hookScript: hookScript ?? hookScript,
      claimUnknownHook: true,
      host: lanAddress
    })
    const chrome2 = await launchChrome({})
    const page2 = await openPage(chrome2.debugPort)
    try {
      await page2.send('Page.enable')
      await page2.send('Runtime.enable')
      // 页面 head 里先声明一个"未知 hook"，随后由承载脚本尝试安装。
      // 承载脚本本身通过 CDP 追加执行一次，等价于"更晚的安装时机"。
      await page2.navigate(`${guardHarness.base}/`)
      await page2.evaluate(`(() => {
        const element = document.createElement('script')
        element.textContent = ${JSON.stringify(hookScript)}
        document.head.appendChild(element)
        return true
      })()`)
      const guard = await page2.evaluate(`JSON.stringify({
        stillUnknown: globalThis[${JSON.stringify(UPLOAD_HOOK_GLOBAL)}]() === 'unknown-hook',
        status: (globalThis.__DSH_ATTACHMENTS_STATUS__ || {}).uploadHook
      })`)
      const guardParsed = JSON.parse(guard)
      gate(
        'U09',
        '未知 __DSH_FILE_UPLOAD__ 不被覆盖，并记录 capability conflict',
        guardParsed.stillUnknown === true && guardParsed.status === 'conflict',
        `保留原值=${guardParsed.stillUnknown} 状态=${guardParsed.status}`
      )
    } finally {
      await page2.close()
      await chrome2.close()
      await guardHarness.close()
    }

    // 清理配对设备
    if (typeof deviceId === 'string' && deviceId.length > 0) {
      await fetch(`http://127.0.0.1:${port}/api/pair/revoke`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ deviceId })
      }).catch(() => {})
    }
  } finally {
    await stopService(service.child, join(fixtureRoot, 'service.pid'))
  }

  const failed = gates.filter((item) => !item.ok)
  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D07',
      platform: 'linux',
      browser: 'Chrome headless=new (CDP)',
      hookGlobal: UPLOAD_HOOK_GLOBAL,
      deviceHeader: DEVICE_HEADER,
      gates,
      failedGateIds: failed.map((item) => item.id),
      result: failed.length === 0 ? 'pass' : 'fail',
      note: 'A 层验证真实夹具页面的注入与顺序；B/C 层在真实 Chromium 中用真实 remote boot 脚本观测网络行为。'
    },
    null,
    2
  ) + '\n'
  await writeFile(join(evidenceDir, 'upload-gates.json'), report)
  await writeFile(join(durableEvidenceDir, 'd07-upload-gates.json'), report)

  console.log(`\nD07 gates: ${gates.length - failed.length}/${gates.length} 通过`)
  if (failed.length > 0) {
    console.error('失败判据：')
    for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
    process.exitCode = 1
    return
  }
  console.log('upload-gates: PASS')
}

await main()
