#!/usr/bin/env node
/**
 * D05 配对与远程通道判据。
 *
 * 在 D04 的真实夹具上验证两条配对通路，并证明鉴权真的生效：
 *   - cookie 流：loopback 铸造令牌 → 浏览器 accept → 落到 /pair-app?device=
 *   - 免 Cookie 流：只用 x-dsh-remote-device 设备头访问 /remote
 *
 * 明确边界：**所有凭据断言都用非 loopback 私网地址**；loopback 只用于铸造令牌
 * 与吊销（这两件事按设计只允许本机）。因此"用 localhost 通过"在本脚本里不成立。
 *
 * 失败即非零退出。token、设备 ID 与 cookie 只参与判定，落盘证据一律脱敏。
 */

import { spawn } from 'node:child_process'
import { mkdir, readFile, writeFile, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createClient, findSecretLeaks, redact, startService, stopService, waitForReady, writeRedactedTrace } from './lib.mjs'

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
const trace = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}
function note(entry) {
  trace.push({ at: new Date().toISOString(), ...entry })
}

async function main() {
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })

  // 非 loopback 访问地址：loopback 不能代替真实 remote 模式。
  const lanAddressPath = join(fixtureRoot, 'lan-address.txt')
  const lanAddress = existsSync(lanAddressPath)
    ? (await readFile(lanAddressPath, 'utf8')).trim()
    : null
  if (!lanAddress || lanAddress === '127.0.0.1') {
    throw new Error(
      '缺少非 loopback 访问地址。请先运行 tests/fixtures/setup.sh，' +
        '并确认本机存在私网 IPv4（loopback 不能代替真实 remote 模式）。'
    )
  }
  const lanBase = `http://${lanAddress}:${port}`
  const loopbackBase = `http://127.0.0.1:${port}`
  console.log(`D05 配对与远程通道判据（非 loopback 基址 ${lanBase}）\n`)

  const serviceLog = join(evidenceDir, 'service-pairing.log')
  await rm(serviceLog, { force: true })
  const service = startService(dshBin, ['--profile', profileName, '--no-open'], {
    cwd: repoRoot,
    dshHome,
    logPath: serviceLog
  })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))

  const lan = createClient()
  let primaryDeviceId = null
  let secondaryDeviceId = null

  try {
    const readyUrl = await waitForReady(service.child, serviceLog, readyTimeoutMs)
    const readyTokenMatch = readyUrl.match(/token=([A-Za-z0-9_-]+)/)
    const readyToken = readyTokenMatch ? readyTokenMatch[1] : null
    note({ step: 'ready', url: readyUrl, lanAddress })

    // 让浏览器端起一个已认证会话（供后续 /remote 的 loopback 代理跳使用）。
    await lan.follow(`${lanBase}/?token=${readyToken}`)

    // 顺序很重要：下面的未配对判据必须在任何配对动作之前执行。
    // 一次成功的 accept 会把发起方的私网 host 加入 dynamicTrustedHosts（设计行为），
    // 之后再从同一 host 访问 /api 就会被信任，未配对判据将失去意义。
    // ---- H02 未配对不能读取工作区（本地 /api 直连） ----
    const localApi = await lan.fetch(`${lanBase}/api/present.host`, { method: 'GET' })
    const localBody = await localApi.text()
    let localJson = null
    try { localJson = JSON.parse(localBody) } catch {}
    note({ step: 'unpaired-local-api', status: localApi.status, body: localBody.slice(0, 160) })
    // 判据是"读不到宿主/工作区数据"而不是某个状态码：任何 2xx 都必须不含宿主数据才通过。
    const leakedHostData = localJson !== null && (typeof localJson.name === 'string' || 'fileManager' in localJson)
    gate(
      'H02',
      '非 loopback 未配对直连本地 /api 不能读取工作区/宿主数据',
      (localApi.status === 401 || localApi.status === 403) && !leakedHostData,
      `status=${localApi.status} hostDataLeaked=${leakedHostData}`
    )

    // ---- H01 未配对访问 /remote 必须被拒绝 ----
    // /remote 是 HTTP 代理；Harness 的 RPC 走 WebSocket，因此用真实的 HTTP 路由
    // /api/present.host 作为代理目标（D05 侦察确认它返回真实宿主数据）。
    const unpaired = await lan.fetch(`${lanBase}/remote/api/present.host`, { method: 'GET' })
    const unpairedBody = await unpaired.text()
    note({ step: 'unpaired-remote', status: unpaired.status, body: unpairedBody.slice(0, 200) })
    gate(
      'H01',
      '非 loopback 未配对访问 /remote 被拒绝（unpaired）',
      unpaired.status === 403 && unpairedBody.includes('unpaired'),
      `status=${unpaired.status} code=${unpairedBody.includes('unpaired') ? 'unpaired' : 'other'}`
    )

    // ---- H04 伪造设备头同样被拒绝 ----
    const forged = await lan.fetch(`${lanBase}/remote/api/present.host`, {
      method: 'GET',
      headers: { 'x-dsh-remote-device': 'forged-device-id' }
    })
    note({ step: 'forged-device', status: forged.status })
    gate('H04', '未配对时伪造设备头访问 /remote 同样被拒绝', forged.status === 403, `status=${forged.status}`)

    // ---- H03 loopback 铸造令牌（控制面只允许本机） ----
    const issue = await fetch(`${loopbackBase}/api/pair/issue`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}'
    })
    const issueJson = await issue.json().catch(() => null)
    const token = issueJson?.token ?? null
    note({ step: 'issue', status: issue.status, url: issueJson?.url, expiresAt: issueJson?.expiresAt })
    gate(
      'H03',
      'loopback 铸造配对令牌成功且返回非 loopback 配对链接',
      issue.status === 200 && typeof token === 'string' && token.length > 0 && String(issueJson?.url ?? '').includes(lanAddress),
      `status=${issue.status} urlHost=${String(issueJson?.url ?? '').replace(/pair=.*$/, 'pair=<redacted>')}`
    )
    if (typeof token !== 'string' || token.length === 0) throw new Error('未取得配对令牌，后续判据无法执行')

    // ---- H05 非 loopback accept：cookie 流 ----
    const accept = await lan.follow(`${lanBase}/pair-accept?pair=${token}`)
    primaryDeviceId = lan.jar.get('dsh_pair') ?? null
    const acceptLocation = accept.hops.map((hop) => hop.location).filter(Boolean).join(' -> ')
    note({ step: 'accept', hops: accept.hops, deviceIdPresent: primaryDeviceId !== null })
    gate(
      'H05',
      '非 loopback accept 成功：303 落到 /pair-app?device= 并下发设备 cookie',
      accept.hops.some((hop) => hop.status === 303) &&
        acceptLocation.includes('/pair-app?device=') &&
        typeof primaryDeviceId === 'string' &&
        primaryDeviceId.length > 0,
      `hops=${accept.hops.map((hop) => hop.status).join(',')} cookieIssued=${primaryDeviceId !== null}`
    )

    // ---- H06 cookie 流访问 /remote ----
    const viaCookie = await lan.fetch(`${lanBase}/remote/api/present.host`, { method: 'GET' })
    const cookieBody = await viaCookie.text()
    let cookieJson = null
    try { cookieJson = JSON.parse(cookieBody) } catch {}
    const cookieHasHostData = cookieJson !== null && typeof cookieJson.name === 'string'
    note({ step: 'remote-via-cookie', status: viaCookie.status, hasHostData: cookieHasHostData, body: cookieBody.slice(0, 200) })
    gate(
      'H06',
      'cookie 流经 /remote 真实代理到 Harness 并取回宿主数据',
      viaCookie.status === 200 && cookieHasHostData && !cookieBody.includes('unpaired'),
      `status=${viaCookie.status} bytes=${cookieBody.length} hasHostData=${cookieHasHostData}`
    )

    // ---- H07 免 Cookie 流：只用设备头 ----
    const cookieless = createClient()
    const viaHeader = await cookieless.fetch(`${lanBase}/remote/api/present.host`, {
      method: 'GET',
      headers: { 'x-dsh-remote-device': primaryDeviceId }
    })
    const headerBody = await viaHeader.text()
    let headerJson = null
    try { headerJson = JSON.parse(headerBody) } catch {}
    const headerHasHostData = headerJson !== null && typeof headerJson.name === 'string'
    note({ step: 'remote-via-header', status: viaHeader.status, jarCookies: cookieless.jar.size, hasHostData: headerHasHostData })
    gate(
      'H07',
      '免 Cookie 流：仅凭设备头即可经 /remote 取回宿主数据（jar 内无 cookie）',
      viaHeader.status === 200 && headerHasHostData && !headerBody.includes('unpaired') && cookieless.jar.size === 0,
      `status=${viaHeader.status} jarCookies=${cookieless.jar.size} hasHostData=${headerHasHostData}`
    )

    // ---- H08 控制面不经远程代理放开 ----
    const pairViaRemote = await lan.fetch(`${lanBase}/remote/api/pair/status`, { method: 'GET' })
    const pairBody = await pairViaRemote.text()
    const pluginManagerViaRemote = await lan.fetch(`${lanBase}/remote/api/plugin-manager`, { method: 'GET' })
    note({ step: 'control-plane-via-remote', pairStatus: pairViaRemote.status, pluginManager: pluginManagerViaRemote.status })
    gate(
      'H08',
      '配对控制面与插件管理不经 /remote 放开给远程设备',
      pairViaRemote.status === 403 && pluginManagerViaRemote.status === 403,
      `pair=${pairViaRemote.status} pluginManager=${pluginManagerViaRemote.status}`
    )

    // ---- H09 免 Cookie 落地页与伪造设备 ----
    // 事实（D05 侦察确认）：/pair-app 是官方外壳，本身不含数据，对任何 device 都返回
    // 同样的外壳 + 设备捕获脚本；真正的门禁在 /remote 代理层。因此这里断言代理层判定，
    // 而不是"伪造 device 拿不到外壳"——那会是一个错误的判据。
    const realLanding = await lan.fetch(`${lanBase}/pair-app?device=${primaryDeviceId}`)
    const realLandingText = await realLanding.text()
    const realIsShell = /__DSH_BOOT__|__ModuleLoader__/.test(realLandingText)
    const fakeLanding = await lan.fetch(`${lanBase}/pair-app?device=not-a-real-device`)
    const fakeLandingText = await fakeLanding.text()
    const placeholder = createClient()
    const fakeViaProxy = await placeholder.fetch(`${lanBase}/remote/api/present.host`, {
      method: 'GET',
      headers: { 'x-dsh-remote-device': 'not-a-real-device' }
    })
    note({
      step: 'pair-app-and-fake-device',
      realStatus: realLanding.status,
      realShell: realIsShell,
      fakeStatus: fakeLanding.status,
      fakeShell: /__DSH_BOOT__|__ModuleLoader__/.test(fakeLandingText),
      fakeViaProxy: fakeViaProxy.status
    })
    gate(
      'H09',
      '免 Cookie 落地页对外壳一致，但伪造设备经 /remote 仍被拒绝',
      realLanding.status === 200 && realIsShell && fakeViaProxy.status === 403,
      `landing=${realLanding.status}/shell=${realIsShell} fakeViaProxy=${fakeViaProxy.status}`
    )

    // ---- H10 远程通道已注入启动前脚本 ----
    const bootScripted = /__DSH_REMOTE_CHANNEL_BOOT__/.test(realLandingText) || /__DSH_REMOTE_CHANNEL_BOOT__/.test((await (await lan.fetch(`${lanBase}/`)).text()))
    note({ step: 'channel-boot-script', injected: bootScripted })
    gate('H10', '远程通道启动前脚本已注入页面（index-inject）', bootScripted, `injected=${bootScripted}`)

    // ---- H11 吊销后新请求被拒绝 ----
    const revoke = await fetch(`${loopbackBase}/api/pair/revoke`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ deviceId: primaryDeviceId })
    })
    const revokeJson = await revoke.json().catch(() => null)
    note({ step: 'revoke', status: revoke.status, ok: revokeJson?.ok })
    const afterRevoke = await lan.fetch(`${lanBase}/remote/api/present.host`, { method: 'GET' })
    const afterRevokeBody = await afterRevoke.text()
    const afterRevokeHeader = await cookieless.fetch(`${lanBase}/remote/api/present.host`, {
      method: 'GET',
      headers: { 'x-dsh-remote-device': primaryDeviceId }
    })
    note({ step: 'after-revoke', cookieFlow: afterRevoke.status, headerFlow: afterRevokeHeader.status })
    gate(
      'H11',
      '吊销后 cookie 流与免 Cookie 流的新请求都被拒绝',
      revoke.status === 200 && afterRevoke.status === 403 && afterRevokeHeader.status === 403,
      `revoke=${revoke.status} cookie=${afterRevoke.status} header=${afterRevokeHeader.status} body=${afterRevokeBody.includes('unpaired') ? 'unpaired' : 'other'}`
    )
  } finally {
    // 收拢自有资源：吊销本轮建立的设备，再停服务。
    for (const [label, deviceId] of [['primary', primaryDeviceId], ['secondary', secondaryDeviceId]]) {
      if (typeof deviceId === 'string' && deviceId.length > 0) {
        try {
          await fetch(`${loopbackBase}/api/pair/revoke`, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ deviceId })
          })
          note({ step: 'cleanup-revoke', label })
        } catch {
          note({ step: 'cleanup-revoke-failed', label })
        }
      }
    }
    await stopService(service.child, join(fixtureRoot, 'service.pid'))
  }

  // ---- H12 证据不含原始 token / 设备 ID / cookie ----
  // 脱敏 trace 同时落一份耐久副本：fixture:down 会删掉整个夹具目录。
  await writeRedactedTrace(join(evidenceDir, 'pairing-trace.jsonl'), trace)
  await writeRedactedTrace(join(durableEvidenceDir, 'd05-pairing-trace.jsonl'), trace)
  const traceText = await readFile(join(durableEvidenceDir, 'd05-pairing-trace.jsonl'), 'utf8')
  const leaks = findSecretLeaks(traceText)
  gate('H12', '脱敏 trace 不含原始 token、设备 ID 或 cookie 值', leaks.length === 0, leaks.join(','))

  const failed = gates.filter((item) => !item.ok)
  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D05',
      platform: 'linux',
      accessBase: lanBase,
      profile: profileName,
      gates,
      failedGateIds: failed.map((item) => item.id),
      result: failed.length === 0 ? 'pass' : 'fail',
      note: '凭据断言全部走非 loopback 地址；loopback 仅用于铸造令牌与吊销（按设计只允许本机）。'
    },
    null,
    2
  ) + '\n'
  await writeFile(join(evidenceDir, 'pairing-gates.json'), report)
  await writeFile(join(durableEvidenceDir, 'd05-pairing-gates.json'), report)

  console.log(`\nD05 gates: ${gates.length - failed.length}/${gates.length} 通过`)
  if (failed.length > 0) {
    console.error('失败判据：')
    for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
    process.exitCode = 1
    return
  }
  console.log('pairing-gates: PASS')
}

await main()
