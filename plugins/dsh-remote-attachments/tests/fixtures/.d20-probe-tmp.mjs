#!/usr/bin/env node
// D20 预备探针：可见性、iframe 桥、localStorage 预设、原生 id 回读。
import { spawn } from 'node:child_process'
import { readFile, mkdir, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { createClient, startService, stopService, waitForReady } from './lib.mjs'
import { launchChrome, openPage } from './cdp.mjs'

const here = dirname(new URL(import.meta.url).pathname)
const repoRoot = '/home/lin-qingyue/AI-Project/DSH/dsh-windows-launcher/dsh-windows-launcher'
const fixtureRoot = join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const dshBin = join(process.env.HOME, '.npm-global/bin/dsh')
const port = 3099
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const lan = (await readFile(join(fixtureRoot, 'lan-address.txt'), 'utf8')).trim()
const lanBase = `http://${lan}:${port}`

const READ = `(() => {
  const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__
  return JSON.stringify({
    vis: document.visibilityState,
    hidden: document.hidden,
    origin: location.origin,
    href: location.href.slice(0, 60),
    bridge: typeof b,
    current: b === undefined ? null : b.currentSession(),
    cards: document.querySelectorAll('[data-composer-card]').length,
    native: b === undefined ? null : (b.importFiles({ files: [] })?.previous ?? null)
  })
})()`
const read = async (page) => JSON.parse(await page.evaluate(READ))

const serviceLog = join(fixtureRoot, 'evidence', 'probe-service.log')
await mkdir(join(fixtureRoot, 'evidence'), { recursive: true })
let service = null
let chrome = null
const pages = []
try {
  console.log('lan =', lan)
  service = startService(dshBin, ['--profile', 'dsh-attachments-fixture', '--no-open'], { cwd: repoRoot, dshHome, logPath: serviceLog })
  await waitForReady(service.child, serviceLog, 120_000)
  const client = createClient()
  const issue = await fetch(`http://127.0.0.1:${port}/api/pair/issue`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
  const token = (await issue.json()).token
  await client.follow(`${lanBase}/pair-accept?pair=${token}`)
  const deviceId = client.jar.get('dsh_pair') ?? ''
  const list = await client.fetch(`${lanBase}/remote/api/session/list`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-dsh-remote-device': deviceId },
    body: JSON.stringify({ type: 'client-request', rpcId: 'probe-1', method: 'session/list', payload: { args: { _request: {} } } })
  })
  const listBody = await list.json()
  const items = listBody?.result?.value?.items ?? []
  console.log('sessions:', items.length, items.slice(0, 3).map((i) => i.sessionId))
  const sessionA = items[0]?.sessionId
  if (sessionA === undefined) { console.log('NO SESSION - seed needed'); }

  chrome = await launchChrome({})
  async function open(sessionId, label) {
    const page = await openPage(chrome.debugPort)
    pages.push(page)
    await page.send('Page.enable'); await page.send('Runtime.enable'); await page.send('Network.enable')
    await page.send('Page.addScriptToEvaluateOnNewDocument', {
      source: sessionId === null
        ? `try{localStorage.removeItem('dsh.sessions.current')}catch(e){}`
        : `try{localStorage.setItem('dsh.sessions.current',JSON.stringify({sessionId:${JSON.stringify(sessionId)}}))}catch(e){}`
    })
    await page.navigate(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId)}`)
    const deadline = Date.now() + 60_000
    for (;;) {
      const s = await read(page)
      if (s.bridge === 'object' && s.cards === 1) break
      if (Date.now() > deadline) { console.log(label, 'TIMEOUT', JSON.stringify(s)); break }
      await sleep(1000)
    }
    const s = await read(page)
    console.log(label, JSON.stringify(s))
    return { page, state: s }
  }
  const p1 = await open(sessionA, 'P1(S1)')
  const p2 = await open(sessionA, 'P2(S1)')
  await p2.page.send('Page.bringToFront')
  await sleep(1500)
  console.log('after bringToFront P2: P1=', JSON.stringify(await read(p1.page)), 'P2=', JSON.stringify(await read(p2.page)))

  // iframe：同源 app 帧
  const frameProbe = `(() => {
    const f = document.createElement('iframe')
    f.id = 'd20-frame'
    f.style.width = '400px'; f.style.height = '300px'
    f.src = ${JSON.stringify(`${lanBase}/pair-app?device=${encodeURIComponent(deviceId)}`)}
    document.body.appendChild(f)
    return 'appended'
  })()`
  console.log('iframe append:', await p1.page.evaluate(frameProbe))
  await sleep(12_000)
  const frameState = `JSON.stringify({
    frames: window.frames.length,
    frameBridge: (() => { try { return typeof window.frames[0].__DSH_ATTACHMENTS_BRIDGE__ } catch (e) { return 'err:' + e.message } })(),
    frameHref: (() => { try { return window.frames[0].location.href.slice(0, 50) } catch (e) { return 'err' } })(),
    topBridge: typeof globalThis.__DSH_ATTACHMENTS_BRIDGE__,
    same: (() => { try { return window.frames[0].__DSH_ATTACHMENTS_BRIDGE__ === globalThis.__DSH_ATTACHMENTS_BRIDGE__ } catch (e) { return 'err' } })()
  })`
  console.log('frame state:', await p1.page.evaluate(frameState))

  // 无会话首页
  const home = await open(null, 'HOME(no-session)')
  console.log('home imported:', await home.page.evaluate(`JSON.stringify((() => { const b = globalThis.__DSH_ATTACHMENTS_BRIDGE__; if (b === undefined) return { bridge: 'undefined' }; const f = new File([new TextEncoder().encode('x')], 'h.txt', { type: 'text/plain' }); return { r: b.importFiles({ files: [f] }) } })())`))
} catch (error) {
  console.error('PROBE FAIL', error?.stack ?? error)
  process.exitCode = 1
} finally {
  for (const p of pages) await p.close().catch(() => {})
  if (chrome !== null) await chrome.close().catch(() => {})
  if (service !== null) await stopService(service.child, join(fixtureRoot, 'service.pid')).catch(() => {})
}
