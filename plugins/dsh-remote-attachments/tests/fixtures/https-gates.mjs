#!/usr/bin/env node
/**
 * D05 HTTPS 判据：受信任测试 CA 下的配对与远程通道。
 *
 * 部署形态与生产一致——webserver 只提供 HTTP，由反向代理做 TLS 终结。
 * 本脚本在其前面加一个用测试 CA 签发证书的本地 TLS 代理，并在该 HTTPS origin 上
 * 复跑配对与 /remote 判据。
 *
 * 证书信任约束：只在该 TLS 上下文里**增加**受信任 CA，其余仍走 Node 默认校验链。
 * 不使用 NODE_TLS_REJECT_UNAUTHORIZED / rejectUnauthorized:false——脚本首条判据
 * 就断言"不提供 CA 时必须失败"，用来证明校验没有被绕过。
 */

import { spawn } from 'node:child_process'
import { mkdir, readFile, writeFile, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  createClient,
  createTlsClient,
  findSecretLeaks,
  readTestCa,
  startService,
  stopService,
  tlsRequest,
  waitForReady,
  writeRedactedTrace
} from './lib.mjs'
import { startTlsProxy } from './tls-proxy.mjs'

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const profileName = process.env.DSH_ATTACH_FIXTURE_PROFILE ?? 'dsh-attachments-fixture'
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const httpPort = Number(process.env.DSH_ATTACH_FIXTURE_PORT ?? 3099)
const httpsPort = Number(process.env.DSH_ATTACH_HTTPS_PORT ?? 3443)
const readyTimeoutMs = Number(process.env.DSH_ATTACH_READY_TIMEOUT_MS ?? 120_000)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const gates = []
const trace = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}
const note = (entry) => trace.push({ at: new Date().toISOString(), ...entry })

async function main() {
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })

  const lanAddressPath = join(fixtureRoot, 'lan-address.txt')
  const fixtureProfile = join(fixtureRoot, 'dsh-home/profiles', profileName, 'package.json')
  if (!existsSync(fixtureProfile)) {
    throw new Error('缺少夹具 profile；请先运行 tests/fixtures/setup.sh（再运行 tls-setup.sh）')
  }
  if (!existsSync(lanAddressPath)) {
    throw new Error('缺少 lan-address.txt；请先运行 tests/fixtures/setup.sh')
  }
  const lanAddress = (await readFile(lanAddressPath, 'utf8')).trim()
  const ca = readTestCa(fixtureRoot)
  const httpsBase = `https://${lanAddress}:${httpsPort}`
  const httpLanBase = `http://${lanAddress}:${httpPort}`
  const loopbackBase = `http://127.0.0.1:${httpPort}`
  console.log(`D05 HTTPS 判据（TLS origin ${httpsBase}，测试 CA 显式信任）\n`)

  const serviceLog = join(evidenceDir, 'service-https.log')
  await rm(serviceLog, { force: true })
  const service = startService(dshBin, ['--profile', profileName, '--no-open'], {
    cwd: repoRoot,
    dshHome,
    logPath: serviceLog
  })
  await writeFile(join(fixtureRoot, 'service.pid'), String(service.child.pid))

  const proxy = await startTlsProxy({
    certPath: join(fixtureRoot, 'tls/server-cert.pem'),
    keyPath: join(fixtureRoot, 'tls/server-key.pem'),
    port: httpsPort,
    targetPort: httpPort,
    host: '0.0.0.0'
  })

  let deviceId = null
  try {
    await waitForReady(service.child, serviceLog, readyTimeoutMs)
    note({ step: 'ready', httpsBase: httpsBase.replace(/:\d+$/, ':<https-port>'), lanAddress })

    // ---- I01 校验未被绕过：不提供 CA 必须失败 ----
    let bareFailed = null
    try {
      await tlsRequest(`${httpsBase}/`)
      bareFailed = false
    } catch (error) {
      bareFailed = error.code ?? error.message
    }
    note({ step: 'no-ca-probe', failed: bareFailed })
    gate(
      'I01',
      '不给受信任 CA 时 TLS 校验必须失败（证明未绕过证书校验）',
      bareFailed !== false && bareFailed !== null,
      `error=${bareFailed}`
    )

    // ---- I02 未配对经 HTTPS 访问 /remote 被拒 ----
    const tlsAnon = createTlsClient(ca)
    const anon = await tlsAnon.request(`${httpsBase}/remote/api/present.host`)
    note({ step: 'https-unpaired-remote', status: anon.status, body: anon.text.slice(0, 120) })
    gate(
      'I02',
      'HTTPS 下未配对访问 /remote 被拒绝（unpaired）',
      anon.status === 403 && anon.text.includes('unpaired'),
      `status=${anon.status}`
    )

    // ---- I03 未配对经 HTTPS 直连本地 /api 读不到宿主数据 ----
    const anonLocal = await tlsAnon.request(`${httpsBase}/api/present.host`)
    let anonJson = null
    try { anonJson = JSON.parse(anonLocal.text) } catch {}
    const leaked = anonJson !== null && typeof anonJson.name === 'string'
    note({ step: 'https-unpaired-local-api', status: anonLocal.status, leaked })
    gate(
      'I03',
      'HTTPS 下未配对直连 /api 读不到宿主数据',
      (anonLocal.status === 401 || anonLocal.status === 403) && !leaked,
      `status=${anonLocal.status} hostDataLeaked=${leaked}`
    )

    // ---- I04 HTTPS 下的 cookie 配对流 ----
    const issue = await fetch(`${loopbackBase}/api/pair/issue`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}'
    })
    const issueJson = await issue.json().catch(() => null)
    const token = issueJson?.token ?? null
    if (typeof token !== 'string' || token.length === 0) throw new Error('未取得配对令牌')

    const accept = await tlsAnon.follow(`${httpsBase}/pair-accept?pair=${token}`)
    deviceId = tlsAnon.jar.get('dsh_pair') ?? null
    const locations = accept.hops.map((hop) => hop.location).filter(Boolean).join(' -> ')
    note({ step: 'https-accept', hops: accept.hops.map((h) => h.status), deviceCookieIssued: deviceId !== null })
    gate(
      'I04',
      'HTTPS 下 accept 成功：303 且重定向保持在 HTTPS origin，并下发设备 cookie',
      accept.hops.some((hop) => hop.status === 303) &&
        locations.includes('https://') &&
        typeof deviceId === 'string' &&
        deviceId.length > 0,
      `hops=${accept.hops.map((h) => h.status).join(',')} httpsRedirect=${locations.includes('https://')} cookie=${deviceId !== null}`
    )

    // ---- I05 HTTPS cookie 流经 /remote 取回宿主数据 ----
    const viaCookie = await tlsAnon.request(`${httpsBase}/remote/api/present.host`)
    let cookieJson = null
    try { cookieJson = JSON.parse(viaCookie.text) } catch {}
    const cookieHasHostData = cookieJson !== null && typeof cookieJson.name === 'string'
    note({ step: 'https-remote-cookie', status: viaCookie.status, hasHostData: cookieHasHostData })
    gate(
      'I05',
      'HTTPS cookie 流经 /remote 取回真实宿主数据',
      viaCookie.status === 200 && cookieHasHostData,
      `status=${viaCookie.status} hasHostData=${cookieHasHostData}`
    )

    // ---- I06 HTTPS 免 Cookie 流（仅设备头） ----
    const headerOnly = await tlsRequest(`${httpsBase}/remote/api/present.host`, {
      ca,
      headers: { 'x-dsh-remote-device': deviceId }
    })
    let headerJson = null
    try { headerJson = JSON.parse(headerOnly.text) } catch {}
    const headerHasHostData = headerJson !== null && typeof headerJson.name === 'string'
    note({ step: 'https-remote-header', status: headerOnly.status, hasHostData: headerHasHostData })
    gate(
      'I06',
      'HTTPS 免 Cookie 流仅凭设备头即可取回宿主数据',
      headerOnly.status === 200 && headerHasHostData,
      `status=${headerOnly.status} hasHostData=${headerHasHostData}`
    )

    // ---- I07 HTTPS 下吊销后拒绝 ----
    const revoke = await fetch(`${loopbackBase}/api/pair/revoke`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ deviceId })
    })
    const afterRevoke = await tlsRequest(`${httpsBase}/remote/api/present.host`, {
      ca,
      headers: { 'x-dsh-remote-device': deviceId }
    })
    note({ step: 'https-revoke', revokeStatus: revoke.status, afterRevoke: afterRevoke.status })
    gate(
      'I07',
      'HTTPS 下吊销后设备头请求被拒绝',
      revoke.status === 200 && afterRevoke.status === 403,
      `revoke=${revoke.status} after=${afterRevoke.status}`
    )

    // ---- I08 HTTPS 与 HTTP 两个 origin 的未配对行为一致 ----
    const httpAnon = createClient()
    const httpUnpaired = await httpAnon.fetch(`${httpLanBase}/remote/api/present.host`, { method: 'GET' })
    note({ step: 'origin-consistency', https: anon.status, http: httpUnpaired.status })
    gate(
      'I08',
      'HTTPS 与 HTTP 两个 origin 对未配对请求的判定一致（都拒绝）',
      anon.status === 403 && httpUnpaired.status === 403,
      `https=${anon.status} http=${httpUnpaired.status}`
    )
  } finally {
    if (typeof deviceId === 'string' && deviceId.length > 0) {
      try {
        await fetch(`${loopbackBase}/api/pair/revoke`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ deviceId })
        })
        note({ step: 'cleanup-revoke' })
      } catch {
        note({ step: 'cleanup-revoke-failed' })
      }
    }
    await proxy.close()
    await stopService(service.child, join(fixtureRoot, 'service.pid'))
  }

  await writeRedactedTrace(join(evidenceDir, 'https-trace.jsonl'), trace)
  await writeRedactedTrace(join(durableEvidenceDir, 'd05-https-trace.jsonl'), trace)
  const leaks = findSecretLeaks(await readFile(join(durableEvidenceDir, 'd05-https-trace.jsonl'), 'utf8'))
  gate('I09', 'HTTPS 脱敏 trace 不含原始 token、设备 ID 或 cookie 值', leaks.length === 0, leaks.join(','))

  const failed = gates.filter((item) => !item.ok)
  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D05',
      layer: 'https-test-ca',
      platform: 'linux',
      httpsOrigin: httpsBase.replace(/:(\d+)$/, ':<https-port>'),
      tlsTermination: 'local reverse proxy with test-CA certificate (mirrors production)',
      gates,
      failedGateIds: failed.map((item) => item.id),
      result: failed.length === 0 ? 'pass' : 'fail'
    },
    null,
    2
  ) + '\n'
  await writeFile(join(evidenceDir, 'https-gates.json'), report)
  await writeFile(join(durableEvidenceDir, 'd05-https-gates.json'), report)

  console.log(`\nD05 HTTPS gates: ${gates.length - failed.length}/${gates.length} 通过`)
  if (failed.length > 0) {
    console.error('失败判据：')
    for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
    process.exitCode = 1
    return
  }
  console.log('https-gates: PASS')
}

await main()
