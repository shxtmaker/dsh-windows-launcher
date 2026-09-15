/**
 * 极简 Chrome DevTools Protocol 客户端（D07 浏览器判据用）。
 *
 * 为什么不引 Playwright/Puppeteer：Node 22 自带 WebSocket，本任务只需要
 * 「启动真实 Chromium、导航、求值、收集网络事件」四件事；自己实现可以避免
 * 额外的浏览器下载与版本绑定，也符合方案"锁定依赖、不用浮动 latest"的要求。
 *
 * 它启动的是**真实 Chromium**（headless=new），不是模拟页面。
 */

import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const CHROME_CANDIDATES = [
  process.env.DSH_ATTACH_CHROME,
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser'
].filter(Boolean)

/** 启动 Chrome 并返回可用于 CDP 的浏览器句柄。 */
export async function launchChrome({ port, chromePath, extraArgs = [] } = {}) {
  const userDataDir = await mkdtemp(join(tmpdir(), 'dsh-cdp-'))
  const debugPort = port ?? 9300 + Math.floor(Math.random() * 400)
  const binary = chromePath ?? (await resolveChrome())
  const child = spawn(
    binary,
    [
      '--headless=new',
      '--no-sandbox',
      '--disable-gpu',
      '--disable-dev-shm-usage',
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-background-networking',
      '--disable-component-update',
      `--remote-debugging-port=${debugPort}`,
      `--user-data-dir=${userDataDir}`,
      ...extraArgs,
      'about:blank'
    ],
    { stdio: ['ignore', 'pipe', 'pipe'] }
  )
  let stderr = ''
  child.stderr.on('data', (chunk) => { stderr += chunk })

  const deadline = Date.now() + 30_000
  let version = null
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Chrome 提前退出（${child.exitCode}）：${stderr.slice(-400)}`)
    try {
      const response = await fetch(`http://127.0.0.1:${debugPort}/json/version`)
      if (response.ok) {
        version = await response.json()
        break
      }
    } catch {
      // 还没起来
    }
    await new Promise((r) => setTimeout(r, 250))
  }
  if (version === null) throw new Error(`Chrome 未在超时内就绪：${stderr.slice(-400)}`)

  return {
    debugPort,
    userDataDir,
    version,
    // D21：子进程结束判据需要进程本身的身份（pid/句柄），这里据实暴露，不改变既有调用方。
    pid: child.pid,
    child,
    async close() {
      if (child.exitCode === null) child.kill('SIGKILL')
      await new Promise((r) => setTimeout(r, 300))
      await rm(userDataDir, { recursive: true, force: true })
    }
  }
}

async function resolveChrome() {
  const { access } = await import('node:fs/promises')
  for (const candidate of CHROME_CANDIDATES) {
    try {
      await access(candidate)
      return candidate
    } catch {
      // 继续找
    }
  }
  throw new Error(`未找到 Chrome：尝试过 ${CHROME_CANDIDATES.join(', ')}`)
}

/** 打开一个新页面目标并返回已连接会话。 */
export async function openPage(debugPort, { url = 'about:blank' } = {}) {
  const targetResponse = await fetch(`http://127.0.0.1:${debugPort}/json/new?${encodeURIComponent(url)}`, {
    method: 'PUT'
  })
  let target = null
  if (targetResponse.ok) {
    target = await targetResponse.json()
  } else {
    // 老版本 Chrome 用 GET /json/new
    const fallback = await fetch(`http://127.0.0.1:${debugPort}/json/new?${encodeURIComponent('about:blank')}`)
    target = await fallback.json()
  }
  return connect(target.webSocketDebuggerUrl)
}

/**
 * 连接一个 CDP WebSocket 端点，返回带事件收集的会话对象。
 */
export async function connect(webSocketDebuggerUrl) {
  const socket = new WebSocket(webSocketDebuggerUrl)
  const pending = new Map()
  const listeners = new Set()
  let nextId = 1

  await new Promise((resolve, reject) => {
    socket.addEventListener('open', () => resolve(), { once: true })
    socket.addEventListener('error', () => reject(new Error('CDP WebSocket 连接失败')), { once: true })
  })

  socket.addEventListener('message', (event) => {
    let message
    try {
      message = JSON.parse(event.data)
    } catch {
      return
    }
    if (message.id !== undefined) {
      const entry = pending.get(message.id)
      if (entry) {
        pending.delete(message.id)
        if (message.error) entry.reject(new Error(`${message.error.message} (${JSON.stringify(message.error.data ?? '')})`))
        else entry.resolve(message.result)
      }
      return
    }
    for (const listener of listeners) listener(message)
  })

  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
      const id = nextId++
      pending.set(id, { resolve, reject })
      socket.send(JSON.stringify({ id, method, params }))
    })

  return {
    send,
    onEvent(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    /** 求值一个表达式；返回 JSON 值（需要 await 时用 awaitPromise）。 */
    async evaluate(expression, { awaitPromise = false, returnByValue = true } = {}) {
      const result = await send('Runtime.evaluate', {
        expression,
        awaitPromise,
        returnByValue,
        userGesture: true
      })
      if (result.exceptionDetails) {
        const detail = result.exceptionDetails.exception?.description ?? result.exceptionDetails.text
        throw new Error(`页面求值异常：${detail}`)
      }
      return result.result?.value
    },
    async navigate(url) {
      const loaded = new Promise((resolve) => {
        const off = this.onEvent((message) => {
          if (message.method === 'Page.loadEventFired') {
            off()
            resolve()
          }
        })
      })
      await send('Page.navigate', { url })
      await Promise.race([loaded, new Promise((r) => setTimeout(r, 20_000))])
    },
    async close() {
      try {
        socket.close()
      } catch {
        // 忽略
      }
    }
  }
}
