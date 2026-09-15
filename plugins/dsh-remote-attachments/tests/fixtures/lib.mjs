/**
 * D04/D05 夹具共享工具：进程启动与就绪检测、带 cookie jar 的 HTTP 客户端、脱敏。
 *
 * 这些是对外不可见的测试辅助；判据本身写在各自的 gates 脚本里。
 */

import { spawn } from 'node:child_process'
import { appendFile, readFile, rm, writeFile } from 'node:fs/promises'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { request as httpsRequest } from 'node:https'
import { request as httpRequest } from 'node:http'

/**
 * 启动夹具服务，把 stdout/stderr 边收边落盘（就绪检测要轮询该文件）。
 * @returns {{ child: import('node:child_process').ChildProcess, text: () => string }}
 */
export function startService(dshBin, args, { cwd, dshHome, logPath }) {
  // 日志按运行截断：waitForReady 轮询的是这个文件，而上一次运行残留的 `dsh web:`
  // 行会让探针在服务真正监听之前就判定"已就绪"（实测出现 ECONNREFUSED 竞态）。
  writeFileSync(logPath, '')
  const child = spawn(dshBin, args, { cwd, env: { ...process.env, DSH_HOME: dshHome } })
  let text = ''
  const tee = (chunk) => {
    text += chunk
    void appendFile(logPath, chunk)
  }
  child.stdout.on('data', tee)
  child.stderr.on('data', tee)
  return { child, text: () => text }
}

/** 轮询日志中的 `dsh web: <url>` 直到就绪或超时。 */
export async function waitForReady(child, logPath, timeoutMs) {
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

/** 停止服务并等待退出；必要时升级为 SIGKILL。 */
export async function stopService(child, pidFile) {
  if (child.exitCode === null) {
    child.kill('SIGTERM')
    const deadline = Date.now() + 10_000
    while (child.exitCode === null && Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 200))
    }
    if (child.exitCode === null) child.kill('SIGKILL')
  }
  if (pidFile) await rm(pidFile, { force: true })
}

/**
 * 带 cookie jar 的 HTTP 客户端。
 *
 * 必须手动处理重定向：fetch 的 redirect:'follow' 不会把 303 上的 Set-Cookie
 * 应用到后续请求，直接用 follow 会永远停在 401。
 */
export function createClient() {
  const jar = new Map()
  const cookieHeader = () =>
    jar.size > 0 ? { cookie: [...jar].map(([k, v]) => `${k}=${v}`).join('; ') } : {}

  const absorb = (response) => {
    for (const value of response.headers.getSetCookie?.() ?? []) {
      const [pair] = value.split(';')
      const index = pair.indexOf('=')
      if (index > 0) jar.set(pair.slice(0, index).trim(), pair.slice(index + 1).trim())
    }
  }

  return {
    jar,
    cookieHeader,
    /** 单次请求，不跟随重定向。 */
    async fetch(url, init = {}) {
      const response = await fetch(url, { redirect: 'manual', ...init, headers: { ...cookieHeader(), ...(init.headers ?? {}) } })
      absorb(response)
      return response
    },
    /** 跟随重定向（手动逐跳），返回最终响应与逐跳记录。 */
    async follow(url, maxHops = 5) {
      const hops = []
      let current = url
      for (let hop = 0; hop <= maxHops; hop++) {
        const response = await this.fetch(current)
        hops.push({ url: current, status: response.status, location: response.headers.get('location') })
        if (response.status >= 300 && response.status < 400) {
          const location = response.headers.get('location')
          if (!location) break
          current = new URL(location, current).href
          continue
        }
        return { response, hops, finalUrl: current, text: await response.text() }
      }
      return { response: null, hops, finalUrl: current, text: '' }
    }
  }
}

/**
 * 脱敏：证据里绝不出现原始 token、设备 ID、会话 cookie 或 bearer。
 * 判定逻辑使用真实值，只有落盘文本经过这里。
 */
export function redact(value) {
  return String(value)
    .replace(/pair=[A-Za-z0-9_-]+/g, 'pair=<redacted>')
    .replace(/token=[A-Za-z0-9_-]+/g, 'token=<redacted>')
    .replace(/"(token|deviceId)":"[^"]*"/g, '"$1":"<redacted>"')
    .replace(/\b[0-9a-f]{32}\b/g, '<device-or-token-redacted>')
    .replace(/eyJ[A-Za-z0-9_.-]{20,}/g, '<jwt-redacted>')
    .replace(/(dsh_pair|dsh-auth-[A-Za-z0-9_-]+)=[^;\s"]+/g, '$1=<redacted>')
}

/** 写一份已脱敏的 trace。 */
export async function writeRedactedTrace(path, entries) {
  const lines = entries.map((entry) => redact(JSON.stringify(entry)))
  await writeFile(path, lines.join('\n') + '\n')
}


/**
 * 单次 HTTPS 请求（可选自定义 CA）。
 *
 * 刻意不用全局开关：NODE_TLS_REJECT_UNAUTHORIZED / rejectUnauthorized:false 会让
 * "HTTPS 可用"变成假象。这里只在 TLS 上下文里**增加**一个受信任 CA，其余仍走
 * Node 默认校验链（包括 hostname/SAN 校验），因此证书不被信任时必然失败。
 *
 * @param {string} url
 * @param {{ method?: string, headers?: Record<string,string>, body?: string, ca?: Buffer|string }} [options]
 * @returns {Promise<{ status: number, headers: import('node:http').IncomingHttpHeaders, text: string, setCookie: string[] }>}
 */
export function tlsRequest(url, options = {}) {
  const target = new URL(url)
  const isTls = target.protocol === 'https:'
  const send = isTls ? httpsRequest : httpRequest
  return new Promise((resolvePromise, rejectPromise) => {
    const req = send(
      {
        protocol: target.protocol,
        hostname: target.hostname,
        port: target.port === '' ? (isTls ? 443 : 80) : Number(target.port),
        path: `${target.pathname}${target.search}`,
        method: options.method ?? 'GET',
        headers: options.headers ?? {},
        ...(isTls && options.ca !== undefined ? { ca: options.ca } : {})
      },
      (res) => {
        const chunks = []
        res.on('data', (chunk) => chunks.push(chunk))
        res.on('end', () =>
          resolvePromise({
            status: res.statusCode ?? 0,
            headers: res.headers,
            text: Buffer.concat(chunks).toString('utf8'),
            setCookie: res.headers['set-cookie'] ?? []
          })
        )
      }
    )
    req.on('error', rejectPromise)
    if (options.body !== undefined) req.write(options.body)
    req.end()
  })
}

/**
 * 带 cookie jar 的 HTTPS 客户端（显式 CA）。行为与 createClient 一致：手动逐跳处理重定向。
 * @param {Buffer|string} ca
 * @param {Map<string,string>} [sharedJar] 允许与 HTTP 客户端共享 jar（同一 origin 内使用）
 */
export function createTlsClient(ca, sharedJar) {
  const jar = sharedJar ?? new Map()
  const cookieHeader = () =>
    jar.size > 0 ? { cookie: [...jar].map(([k, v]) => `${k}=${v}`).join('; ') } : {}
  const absorb = (setCookie) => {
    for (const value of setCookie) {
      const [pair] = value.split(';')
      const index = pair.indexOf('=')
      if (index > 0) jar.set(pair.slice(0, index).trim(), pair.slice(index + 1).trim())
    }
  }
  const request = async (url, init = {}) => {
    const response = await tlsRequest(url, {
      ...init,
      ca,
      headers: { ...cookieHeader(), ...(init.headers ?? {}) }
    })
    absorb(response.setCookie)
    return response
  }
  return {
    jar,
    request,
    async follow(url, maxHops = 5) {
      const hops = []
      let current = url
      for (let hop = 0; hop <= maxHops; hop++) {
        const response = await request(current)
        hops.push({ url: current, status: response.status, location: response.headers.location })
        if (response.status >= 300 && response.status < 400 && response.headers.location) {
          current = new URL(response.headers.location, current).href
          continue
        }
        return { response, hops, finalUrl: current, text: response.text }
      }
      return { response: null, hops, finalUrl: current, text: '' }
    }
  }
}

/** 读取夹具的测试 CA（PEM）。 */
export function readTestCa(fixtureRoot) {
  const path = `${fixtureRoot}/tls/ca-cert.pem`
  if (!existsSync(path)) throw new Error(`缺少测试 CA：${path}（请先运行 tests/fixtures/tls-setup.sh）`)
  return readFileSync(path)
}

/** 判断给定文本是否仍含未脱敏的敏感形态。 */
export function findSecretLeaks(text) {
  const patterns = [
    { name: 'pairing-url', re: /pair=[A-Za-z0-9_-]{8,}/ },
    { name: 'token-field', re: /"token"\s*:\s*"[A-Za-z0-9_-]{8,}"/ },
    { name: 'device-id-field', re: /"deviceId"\s*:\s*"[A-Za-z0-9_-]{8,}"/ },
    { name: 'cookie-value', re: /dsh_pair=[A-Za-z0-9_-]{8,}/ },
    { name: 'jwt', re: /eyJ[A-Za-z0-9_.-]{40,}/ }
  ]
  return patterns.filter((pattern) => pattern.re.test(text)).map((pattern) => pattern.name)
}
