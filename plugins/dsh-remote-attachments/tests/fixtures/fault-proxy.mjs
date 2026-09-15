/**
 * D19 故障注入反向代理（Linux 网络夹具）。
 *
 * 目的：把"上传网络故障"注入到**真实上传路径**上，而不是在测试里伪造一条捷径。
 *
 * 拓扑：
 *
 * ```
 * 真实 Chromium 页面  ──►  本代理（LAN:port，页面 origin）
 *                             │  非上传请求：原样转发
 *                             │  上传请求：按当前已装故障注入
 *                             ▼
 *                        真实 dsh 服务（0.0.0.0:3099）
 * ```
 *
 * 为什么用"换 origin 的反向代理"而不是 CDP 请求改写：remote 的启动前 boot 脚本
 * 只在**非 loopback 主机名**上安装改写层，且只改写**同源**请求。把页面放在
 * `http://<lan>:<proxyPort>` 上，页面 origin 依然是非 loopback，改写层照常生效；
 * 上传请求于是走 `<lan>:<proxyPort>/remote/api/session/uploadFileBinary`，
 * 正好落在本代理的匹配面上。代理再把 `/remote/...` 原样转发给真实服务
 * （Host 头改写成真实服务地址），配对 cookie / 设备头 / 门控判定全部由真实服务完成。
 *
 * 注入面（都是确定性的、按请求匹配的）：
 *   - `pass`             无故障（对照）
 *   - `stream-break`     把请求体转发 N 字节后同时掐断客户端与上游连接
 *   - `stall`            收下请求但不转发、不响应（保持连接挂起，可 release）
 *   - `http`             直接回一个 HTTP 状态码（413 / 403 / 401 …）
 *   - `business-failure` HTTP 200 + Harness 业务失败信封 `{ok:false,error:{code,message,details}}`
 *   - `drop`             不响应直接掐断（宿主重启场景）
 *
 * 故障作用范围由 `skip` / `times` 控制：`{skip:1, times:1}` 表示"放过第 1 个匹配请求，
 * 只对第 2 个生效"——这是"部分批次里一个成功一个失败"的注入手段。
 *
 * 代理自己就是**请求计数**的权威来源：每个匹配请求落一条日志（带阶段标签、结果、
 * 观测字节数、响应状态、`name` 查询参数），"不自动重发"与"成功项不被重放"都由它计数。
 */

import { createServer, request as httpRequest } from 'node:http'
import { gunzipSync } from 'node:zlib'

/** 上传路径（改写前）。远端改写层会把它变成 `/remote/api/session/uploadFileBinary`。 */
export const UPLOAD_PATH = '/api/session/uploadFileBinary'

/** 一个故障条目的默认形状。 */
const DEFAULT_FAULT = Object.freeze({ kind: 'pass' })

/**
 * 启动故障代理。
 *
 * @param {{
 *   listenHost?: string,
 *   listenPort?: number,
 *   targetHost?: string,
 *   targetPort: number,
 *   matchSuffix?: string,
 *   onLog?: (entry: object) => void
 * }} options
 * @returns {Promise<object>} 代理句柄
 */
export async function startFaultProxy(options) {
  const listenHost = options.listenHost ?? '0.0.0.0'
  const listenPort = options.listenPort ?? 0
  const targetHost = options.targetHost ?? '127.0.0.1'
  const targetPort = options.targetPort
  const matchSuffix = options.matchSuffix ?? UPLOAD_PATH

  /** 当前故障与它的剩余作用次数。 */
  let fault = { ...DEFAULT_FAULT }
  let seen = 0
  let phase = 'idle'
  let seq = 0
  /** stall 期间挂起的请求（release 时统一放行）。 */
  const held = new Set()
  const log = []

  const matches = (req) => {
    const path = (req.url ?? '').split('?')[0]
    return path.endsWith(matchSuffix)
  }

  const record = (entry) => {
    const full = { seq: ++seq, phase, ...entry }
    log.push(full)
    if (typeof options.onLog === 'function') options.onLog(full)
    return full
  }

  /** 当前故障是否应当作用于这个匹配请求（skip / times 记账）。 */
  const faultFor = () => {
    if (fault.kind === 'pass') return null
    seen += 1
    const skip = fault.skip ?? 0
    if (seen <= skip) return null
    const times = fault.times ?? Number.POSITIVE_INFINITY
    if (seen > skip + times) return null
    return fault
  }

  const forward = (req, res, entry, { countBytes = false } = {}) => {
    const headers = { ...req.headers, host: `${targetHost}:${targetPort}` }
    const upstream = httpRequest({ host: targetHost, port: targetPort, method: req.method, path: req.url, headers })
    let requestBytes = 0
    let broken = false
    upstream.on('response', (upstreamRes) => {
      entry.status = upstreamRes.statusCode ?? 0
      // 业务失败信封等在测试侧还要读，因此顺带截留一小段响应正文。
      // 夹具服务开了 gzip（compression: gzip），截留后按需解压，否则读到的是二进制。
      const chunks = []
      upstreamRes.on('data', (chunk) => {
        if (chunks.length < 4) chunks.push(chunk)
      })
      upstreamRes.on('end', () => {
        let bytes = Buffer.concat(chunks)
        if (`${upstreamRes.headers['content-encoding'] ?? ''}`.includes('gzip')) {
          try {
            bytes = gunzipSync(bytes)
          } catch {
            // 截断的 gzip 解不开：保留原始字节，正文只是观测记录。
          }
        }
        entry.responseBody = bytes.toString('utf8').slice(0, 400)
        entry.endedAtMs = Date.now()
      })
      res.writeHead(upstreamRes.statusCode ?? 502, upstreamRes.headers)
      upstreamRes.pipe(res)
    })
    upstream.on('error', (error) => {
      entry.outcome = broken ? 'upstream-destroyed' : 'upstream-error'
      entry.error = String(error?.code ?? error?.message ?? error)
      if (!res.headersSent && !res.writableEnded) {
        try {
          res.destroy()
        } catch {
          // 已经断了
        }
      }
    })
    req.on('data', (chunk) => {
      requestBytes += chunk.length
      entry.requestBytes = requestBytes
      if (countBytes && requestBytes >= (entry.breakAfterBytes ?? 0) && !broken) {
        broken = true
        // 请求体转发到第 N 字节就断：客户端连接与上游连接同时掐断。
        entry.outcome = 'stream-break'
        entry.endedAtMs = Date.now()
        try {
          req.socket.destroy()
        } catch {
          // 已经断了
        }
        try {
          upstream.destroy()
        } catch {
          // 已经断了
        }
      }
    })
    req.pipe(upstream)
    req.on('end', () => {
      if (entry.requestBytes === undefined) entry.requestBytes = 0
      if (entry.outcome === undefined) entry.forwardedAtMs = Date.now()
    })
    req.on('aborted', () => {
      if (entry.outcome === undefined) entry.outcome = 'client-aborted'
      try {
        upstream.destroy()
      } catch {
        // 已经断了
      }
    })
  }

  const server = createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://proxy.invalid')
    const matched = matches(req)
    if (!matched) {
      // 非上传流量：原样通过，代理绝不在这里做任何判断。
      const entry = record({ kind: 'passthrough', method: req.method, path: url.pathname, query: url.search })
      forward(req, res, entry)
      return
    }

    const name = url.searchParams.get('name') ?? null
    const entry = record({
      kind: 'upload',
      method: req.method,
      path: url.pathname,
      query: url.search,
      name,
      sessionId: url.searchParams.get('sessionId') ?? null,
      deviceHeader: typeof req.headers['x-dsh-remote-device'] === 'string',
      cookiePresent: typeof req.headers.cookie === 'string' && req.headers.cookie.length > 0,
      startedAtMs: Date.now()
    })

    const active = faultFor()
    entry.injected = active === null ? 'pass' : active.kind
    if (active === null) {
      forward(req, res, entry)
      return
    }

    if (active.kind === 'drop') {
      entry.outcome = 'dropped'
      entry.status = 0
      entry.endedAtMs = Date.now()
      req.socket.destroy()
      return
    }

    if (active.kind === 'stream-break') {
      entry.breakAfterBytes = active.afterBytes ?? 1
      entry.requestBytes = 0
      forward(req, res, entry, { countBytes: true })
      return
    }

    if (active.kind === 'stall') {
      entry.outcome = 'stalled'
      // 收下连接但**不读请求体、不转发、不响应**：请求真的处于"在途"，
      // 客户端的 TCP 窗口会被写满，这是可持续的挂起而不是假装挂起。
      // release() 时才把同一个 req 管道到真实服务（此时服务端看到的门控状态
      // 是"放行那一刻"的状态——这正是"在途请求遇上撤销"的判定点）。
      const onGone = () => {
        if (entry.outcome !== 'stalled') return
        entry.outcome = 'client-aborted'
        entry.endedAtMs = Date.now()
        held.delete(item)
      }
      const item = { req, res, entry, onGone }
      req.on('aborted', onGone)
      res.on('close', onGone)
      held.add(item)
      return
    }

    if (active.kind === 'http') {
      const status = active.status ?? 500
      entry.outcome = 'http-injected'
      entry.status = status
      entry.responseBody = String(active.body ?? `injected HTTP ${status}`)
      entry.endedAtMs = Date.now()
      res.writeHead(status, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' })
      res.end(entry.responseBody)
      return
    }

    if (active.kind === 'business-failure') {
      // 与固定版本消费者解析的信封同形（见 @deepseek-ai/dsh-client-file-upload 的
      // parseFileUploadResult：ok:false 时要求 error.code/message/details 齐备）。
      const body = JSON.stringify({
        ok: false,
        error: {
          code: active.code ?? 'ATTACHMENT_WRITE_FAILED',
          message: active.message ?? 'attachment write failed (injected business failure)',
          details: {}
        }
      })
      entry.outcome = 'business-failure'
      entry.status = 200
      entry.responseBody = body
      entry.endedAtMs = Date.now()
      res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
      res.end(body)
      return
    }

    entry.outcome = `unknown-fault:${String(active.kind)}`
    entry.status = 500
    res.writeHead(500).end('unknown fault')
  })

  // WebSocket / SSE 升级也要透传：页面的事件流不能因为夹具而断，否则测出来的是
  // "夹具把页面弄坏了"而不是"上传故障的语义"。
  server.on('upgrade', (req, socket, head) => {
    const headers = { ...req.headers, host: `${targetHost}:${targetPort}` }
    const upstream = httpRequest({ host: targetHost, port: targetPort, method: req.method, path: req.url, headers })
    upstream.on('upgrade', (upstreamRes, upstreamSocket, upstreamHead) => {
      const lines = [`HTTP/1.1 ${upstreamRes.statusCode} ${upstreamRes.statusMessage}`]
      for (const [key, value] of Object.entries(upstreamRes.headers)) {
        if (Array.isArray(value)) for (const item of value) lines.push(`${key}: ${item}`)
        else if (value !== undefined) lines.push(`${key}: ${value}`)
      }
      socket.write(`${lines.join('\r\n')}\r\n\r\n`)
      if (upstreamHead.length > 0) socket.write(upstreamHead)
      if (head.length > 0) upstreamSocket.write(head)
      socket.pipe(upstreamSocket)
      upstreamSocket.pipe(socket)
    })
    upstream.on('error', () => socket.destroy())
    socket.on('error', () => upstream.destroy())
    upstream.end()
  })

  await new Promise((resolvePromise) => server.listen(listenPort, listenHost, resolvePromise))
  const port = server.address().port

  return {
    port,
    host: listenHost,
    /** 页面 base：测试用它作为浏览器 origin。 */
    baseFor: (host) => `http://${host}:${port}`,
    log,
    /** 装配一个故障（默认作用到之后所有匹配请求）。 */
    arm(spec) {
      fault = { kind: 'pass', ...spec }
      seen = 0
      return fault
    },
    /** 只解除注入但保留 skip/times 记账复位。 */
    disarm() {
      fault = { ...DEFAULT_FAULT }
      seen = 0
    },
    /** 给后续日志打阶段标签（判据按阶段计数）。 */
    setPhase(value) {
      phase = value
      return phase
    },
    /** 放行 stall 挂起的请求（它们会以**当时**的服务端门控状态被转发）。 */
    release() {
      const pending = [...held]
      held.clear()
      for (const item of pending) {
        item.req.off('aborted', item.onGone)
        item.res.off('close', item.onGone)
        if (item.req.destroyed || item.res.destroyed) {
          item.entry.outcome = 'client-aborted'
          item.entry.endedAtMs = Date.now()
          continue
        }
        item.entry.outcome = 'released'
        item.entry.releasedAtMs = Date.now()
        forward(item.req, item.res, item.entry)
      }
      return pending.length
    },
    heldCount() {
      return held.size
    },
    /** 计数：匹配到的上传请求（可按阶段 / 文件名过滤）。 */
    uploads(filter = {}) {
      return log.filter((entry) => {
        if (entry.kind !== 'upload') return false
        if (filter.phase !== undefined && entry.phase !== filter.phase) return false
        if (filter.name !== undefined && entry.name !== filter.name) return false
        return true
      })
    },
    /** 删除尚未结束的挂起项并关停监听；keep-alive 连接必须显式断开，否则 close 永不返回。 */
    async close() {
      for (const item of [...held]) {
        try {
          item.res.destroy()
        } catch {
          // 忽略
        }
      }
      held.clear()
      const closed = new Promise((resolvePromise) => server.close(() => resolvePromise()))
      // 关键顺序：server.close() 只回调到"所有连接都消失"为止，keep-alive 或
      // 事件流连接会让它永远不返回；所以必须先断开，再等 close 回调。
      server.closeAllConnections?.()
      await Promise.race([closed, new Promise((resolvePromise) => setTimeout(resolvePromise, 5_000))])
      server.closeAllConnections?.()
    }
  }
}
