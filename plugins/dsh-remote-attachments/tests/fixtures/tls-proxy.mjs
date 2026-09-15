/**
 * D05 HTTPS 夹具：本地 TLS 终结反向代理。
 *
 * 与生产部署形态一致——webserver 本身只提供 HTTP（其 config schema 只接受
 * host/port），生产由反向代理做 TLS 终结。本模块用测试 CA 签发的证书提供
 * HTTPS origin，并把请求转发到夹具的 HTTP 后端。
 *
 * 关键约束：证书必须能被**默认**信任链校验通过（在专用上下文里信任测试 CA），
 * 不允许用 NODE_TLS_REJECT_UNAUTHORIZED 或 rejectUnauthorized:false 绕过。
 * 后者会让"HTTPS 可用"变成假象。
 */

import { createServer } from 'node:https'
import { request as httpRequest } from 'node:http'
import { readFileSync } from 'node:fs'

/**
 * 启动 TLS 反向代理。
 * @param {{ certPath: string, keyPath: string, port: number, targetPort: number, host?: string }} options
 */
export function startTlsProxy({ certPath, keyPath, port, targetPort, host = '0.0.0.0' }) {
  const server = createServer(
    {
      cert: readFileSync(certPath),
      key: readFileSync(keyPath),
      minVersion: 'TLSv1.2'
    },
    (req, res) => {
      const headers = { ...req.headers }
      // 保留原始 Host 供上游判定 fence；同时按常规代理约定补充转发头。
      headers['x-forwarded-proto'] = 'https'
      headers['x-forwarded-for'] = req.socket.remoteAddress ?? ''
      const upstream = httpRequest(
        {
          host: '127.0.0.1',
          port: targetPort,
          method: req.method,
          path: req.url,
          headers
        },
        (upstreamRes) => {
          res.writeHead(upstreamRes.statusCode ?? 502, upstreamRes.headers)
          upstreamRes.pipe(res)
        }
      )
      upstream.on('error', (error) => {
        if (!res.headersSent) res.writeHead(502, { 'content-type': 'text/plain' })
        res.end(`proxy error: ${error.message}`)
      })
      req.pipe(upstream)
    }
  )

  return new Promise((resolvePromise, rejectPromise) => {
    server.on('error', rejectPromise)
    server.listen(port, host, () => {
      const address = server.address()
      resolvePromise({
        server,
        url: `https://${host === '0.0.0.0' ? '127.0.0.1' : host}:${address.port}`,
        close: () =>
          new Promise((done) => {
            server.close(() => done())
          })
      })
    })
  })
}
