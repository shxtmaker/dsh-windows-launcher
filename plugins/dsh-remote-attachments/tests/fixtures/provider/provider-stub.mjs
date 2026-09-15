#!/usr/bin/env node
/**
 * D06 确定性 provider stub。
 *
 * 它实现固定版本 Harness 实际使用的 provider 协议（OpenAI/DeepSeek 兼容的
 * `POST /chat/completions` 流式 SSE），因此**模型侧**是离线确定的；但工具调用由
 * 真实 Harness 的 agent 循环发起、由真实工具执行，结果再回填到后续请求里。
 *
 * 判定原则（对应任务卡"不得以模型文字'已收到'冒充读回"）：本 stub 从不自己编造
 * 文件字节，它只产生 tool_calls；读回内容由 Harness 的 `read` / `read_image` 工具
 * 实际执行后写入请求，判据再从这些真实结果里核对唯一标识与哈希。
 *
 * 请求分类（实测固定版本行为）：
 *   - 带 `tools` 的请求：真实 agent 回合，按 manifest 步骤推进。
 *   - 不带 `tools` 的请求：会话标题等旁路调用，返回一行纯文本，不产生工具调用。
 *
 * 用法：node provider-stub.mjs --port 3901 --manifest <manifest.json> --record <dir>
 *
 * D09 扩展（唯一）：`step.arguments` 里形如 `"{{prompt-last:<regex>}}"` 的整串字符串，
 * 会在下发前用**本次请求**全部消息文本中该正则的最后一个匹配（捕获组 1 优先）替换。
 * 用途是把运行期才知道的附件暂存路径填进静态 manifest；解析结果记入
 * `<record>/stub-summary.json` 的 `placeholderResolutions`。
 */

import { createServer } from 'node:http'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

function parseArgs(argv) {
  const options = { port: 3901, host: '127.0.0.1', manifest: null, record: null }
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i].replace(/^--/, '')
    const value = argv[i + 1]
    if (key in options) options[key] = key === 'port' ? Number(value) : value
  }
  return options
}

const options = parseArgs(process.argv.slice(2))
const recordDir = options.record
  ? resolve(options.record)
  : join(dirname(fileURLToPath(import.meta.url)), 'record')
await mkdir(recordDir, { recursive: true })

const manifest = options.manifest
  ? JSON.parse(await readFile(resolve(options.manifest), 'utf8'))
  : { steps: [], final: { text: 'done' } }

const observations = {
  requests: 0,
  agentRequests: 0,
  sideChannelRequests: 0,
  toolCallRequests: 0,
  toolResultRequests: 0,
  seenToolNames: [],
  imagePayloads: [],
  stages: [],
  placeholderResolutions: [],
  toolResultTexts: []
}

/**
 * 极小占位符解析（D09 新增，唯一扩展）。
 *
 * 为什么需要：文本附件在 provider 请求里**不是**附件分块，而是 Harness 写入的一段
 * handle 文本（"… verbatim read-only copy saved at \"<暂存路径>\" …"）。暂存路径由
 * attachmentId 内容寻址决定，manifest 无法预先写死；因此 manifest 里可以写
 * `{{prompt-last:<regex>}}`，由 stub 在**本次请求**的全部消息文本里取该正则的
 * **最后一个**匹配（捕获组 1 优先）——"最后一个"保证多次发送时取到最新一条消息的路径。
 *
 * 边界（刻意不做的事）：只有"整串就是一个占位符"的字符串会被替换；不做嵌套模板、
 * 不做表达式求值、不递归进键名。解析失败时保留原样并把 `resolved:false` 记进
 * summary，让判据以"read 工具读到的是字面占位符"这种可见方式失败，而不是静默跳过。
 */
function resolveArguments(value, requestText) {
  if (typeof value === 'string') {
    const placeholder = /^\{\{prompt-last:([\s\S]*)\}\}$/.exec(value)
    if (placeholder === null) return { value, placeholders: 0, resolved: true }
    const pattern = new RegExp(placeholder[1], 'g')
    let last = null
    for (let match = pattern.exec(requestText); match !== null; match = pattern.exec(requestText)) last = match
    if (last === null) return { value, placeholders: 1, resolved: false }
    return { value: last[1] ?? last[0], placeholders: 1, resolved: true }
  }
  if (Array.isArray(value)) {
    let placeholders = 0
    let resolved = true
    const items = value.map((item) => {
      const result = resolveArguments(item, requestText)
      placeholders += result.placeholders
      if (!result.resolved) resolved = false
      return result.value
    })
    return { value: items, placeholders, resolved }
  }
  if (value !== null && typeof value === 'object') {
    let placeholders = 0
    let resolved = true
    const output = {}
    for (const [key, item] of Object.entries(value)) {
      const result = resolveArguments(item, requestText)
      placeholders += result.placeholders
      if (!result.resolved) resolved = false
      output[key] = result.value
    }
    return { value: output, placeholders, resolved }
  }
  return { value, placeholders: 0, resolved: true }
}

/** 一条消息的纯文本（content 可能是字符串或分块数组）。 */
function messageText(message) {
  const content = message?.content
  if (typeof content === 'string') return content
  if (Array.isArray(content)) {
    return content
      .map((part) => {
        if (typeof part === 'string') return part
        if (part?.type === 'text') return part.text ?? ''
        if (part?.type === 'image_url') return '[image]'
        return ''
      })
      .join('\n')
  }
  return ''
}

const toolResultMessages = (body) => (body.messages ?? []).filter((m) => m?.role === 'tool')
const assistantToolCalls = (body) => {
  const calls = []
  for (const message of body.messages ?? []) {
    for (const call of message?.tool_calls ?? []) {
      calls.push({ name: call.function?.name, arguments: call.function?.arguments })
    }
  }
  return calls
}
const hasTools = (body) => Array.isArray(body.tools) && body.tools.length > 0

/**
 * 决定本请求要下发哪一步：返回第一个**期望尚未在工具结果里出现**的步骤。
 * 只依据真实工具结果推进，因此不会因为"结果还没回来"而重复下发。
 */
function resolveStep(body) {
  const results = toolResultMessages(body)
  const resultText = results.map(messageText).join('\n')
  for (let index = 0; index < manifest.steps.length; index++) {
    const step = manifest.steps[index]
    if (step.kind !== 'tool-call') continue
    const needles = step.expect?.resultContainsAll ?? []
    const satisfied = needles.length > 0 && needles.every((needle) => resultText.includes(needle))
    if (!satisfied) return { index, step, results, resultText }
  }
  return null
}

function sseChunk(payload) {
  return `data: ${JSON.stringify(payload)}\n\n`
}

function makeChunk({ id, model, delta, finishReason = null }) {
  return {
    id,
    object: 'chat.completion.chunk',
    created: Math.floor(Date.now() / 1000),
    model,
    choices: [{ index: 0, delta, finish_reason: finishReason }]
  }
}

/** 把 tool_calls 拆成多个分片，贴近真实流式行为。 */
function toolCallFragments(argumentsValue, tool, callId) {
  const args = JSON.stringify(argumentsValue ?? {})
  const mid = Math.max(1, Math.floor(args.length / 2))
  return [
    { index: 0, id: callId, type: 'function', function: { name: tool, arguments: '' } },
    { index: 0, function: { arguments: args.slice(0, mid) } },
    { index: 0, function: { arguments: args.slice(mid) } }
  ]
}

async function writeSummary() {
  const summary = {
    requests: observations.requests,
    agentRequests: observations.agentRequests,
    sideChannelRequests: observations.sideChannelRequests,
    toolCallRequests: observations.toolCallRequests,
    toolResultRequests: observations.toolResultRequests,
    seenToolNames: observations.seenToolNames,
    imagePayloads: observations.imagePayloads,
    stages: observations.stages,
    placeholderResolutions: observations.placeholderResolutions,
    toolResultTexts: observations.toolResultTexts
  }
  await writeFile(join(recordDir, 'stub-summary.json'), JSON.stringify(summary, null, 2))
  return summary
}

const server = createServer(async (req, res) => {
  if (req.method === 'GET' && req.url?.startsWith('/__stub/summary')) {
    const summary = await writeSummary()
    res.writeHead(200, { 'content-type': 'application/json' })
    res.end(JSON.stringify(summary))
    return
  }
  if (req.method !== 'POST' || !req.url?.startsWith('/chat/completions')) {
    res.writeHead(404, { 'content-type': 'application/json' })
    res.end(JSON.stringify({ error: { message: 'not found' } }))
    return
  }

  const chunks = []
  for await (const chunk of req) chunks.push(chunk)
  let body = null
  try {
    body = JSON.parse(Buffer.concat(chunks).toString('utf8'))
  } catch {
    res.writeHead(400, { 'content-type': 'application/json' })
    res.end(JSON.stringify({ error: { message: 'invalid json' } }))
    return
  }

  observations.requests++
  const requestIndex = observations.requests
  const model = typeof body.model === 'string' ? body.model : 'stub-model'
  const responseId = `stub-${requestIndex}`
  const agentTurn = hasTools(body)
  await writeFile(
    join(recordDir, `request-${String(requestIndex).padStart(3, '0')}.json`),
    JSON.stringify(body, null, 2)
  )

  const results = toolResultMessages(body)
  const calls = assistantToolCalls(body)
  for (const call of calls) {
    if (call.name && !observations.seenToolNames.includes(call.name)) observations.seenToolNames.push(call.name)
  }
  if (calls.length > 0) observations.toolCallRequests++
  if (results.length > 0) {
    // 请求里携带的是**累积历史**，因此只记录相对上次新增的工具结果，
    // 否则同一个结果会被重复计数（实测第三、四次请求都带上第一次的 read 结果）。
    const texts = results.map(messageText)
    const previous = observations.toolResultRequests
    if (texts.length > previous) {
      for (const text of texts.slice(previous)) observations.toolResultTexts.push(text)
      observations.toolResultRequests = texts.length
    }
  }

  // 图像载荷记录：供后续附件判据断言真实请求里的图像引用。
  for (const message of body.messages ?? []) {
    const content = message?.content
    if (!Array.isArray(content)) continue
    for (const part of content) {
      if (part?.type === 'image_url') {
        const url = typeof part.image_url === 'string' ? part.image_url : (part.image_url?.url ?? '')
        observations.imagePayloads.push({ requestIndex, kind: 'image_url', chars: url.length, prefix: url.slice(0, 24) })
      }
    }
  }

  res.writeHead(200, {
    'content-type': 'text/event-stream; charset=utf-8',
    'cache-control': 'no-store',
    connection: 'keep-alive'
  })

  const preamble = () =>
    res.write(sseChunk(makeChunk({ id: responseId, model, delta: { role: 'assistant', content: '' } })))

  // 旁路请求（会话标题等）：无 tools，返回一行纯文本。
  if (!agentTurn) {
    observations.sideChannelRequests++
    observations.stages.push({ requestIndex, stage: 'side-channel' })
    preamble()
    res.write(sseChunk(makeChunk({ id: responseId, model, delta: { content: 'D06 fixture read' } })))
    res.write(sseChunk(makeChunk({ id: responseId, model, delta: {}, finishReason: 'stop' })))
    res.write('data: [DONE]\n\n')
    res.end()
    return
  }

  observations.agentRequests++
  const resolved = resolveStep(body)
  if (resolved !== null) {
    const callId = `call_stub_${resolved.index + 1}`
    // 占位符只在**本轮请求**的文本里解析：静态 manifest + 运行期暂存路径。
    const requestText = (body.messages ?? []).map(messageText).join('\n')
    const args = resolveArguments(resolved.step.arguments ?? {}, requestText)
    if (args.placeholders > 0) {
      observations.placeholderResolutions.push({
        requestIndex,
        step: resolved.index,
        tool: resolved.step.tool,
        placeholders: args.placeholders,
        resolved: args.resolved,
        arguments: args.value
      })
    }
    observations.stages.push({
      requestIndex,
      stage: 'tool-call',
      step: resolved.index,
      tool: resolved.step.tool,
      ...(args.placeholders > 0 ? { resolvedArguments: args.value, placeholdersResolved: args.resolved } : {})
    })
    preamble()
    for (const fragment of toolCallFragments(args.value, resolved.step.tool, callId)) {
      res.write(sseChunk(makeChunk({ id: responseId, model, delta: { tool_calls: [fragment] } })))
    }
    res.write(sseChunk(makeChunk({ id: responseId, model, delta: {}, finishReason: 'tool_calls' })))
    res.write('data: [DONE]\n\n')
    res.end()
    return
  }

  // 收尾：所有步骤的期望都已在真实工具结果中出现。
  observations.stages.push({ requestIndex, stage: 'final', toolResults: results.length })
  preamble()
  // D20：可选地把这一轮"停在空中"，让客户端保持"正在生成"状态，用于验证
  // "回合进行中导入"这类受限状态。缺省 0 ⇒ 与既有判据逐字节同款，无行为变化。
  const holdOpenMs = Number(manifest.holdOpenMs ?? 0)
  if (Number.isFinite(holdOpenMs) && holdOpenMs > 0) {
    observations.heldOpenMs = holdOpenMs
    await new Promise((resolvePromise) => setTimeout(resolvePromise, holdOpenMs))
  }
  res.write(sseChunk(makeChunk({ id: responseId, model, delta: { content: manifest.final?.text ?? 'done' } })))
  res.write(sseChunk(makeChunk({ id: responseId, model, delta: {}, finishReason: 'stop' })))
  res.write(
    sseChunk({
      id: responseId,
      object: 'chat.completion.chunk',
      created: Math.floor(Date.now() / 1000),
      model,
      choices: [],
      usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 }
    })
  )
  res.write('data: [DONE]\n\n')
  res.end()
})

await new Promise((res) => server.listen(options.port, options.host, res))
const port = server.address().port
await writeFile(
  join(recordDir, 'stub-ready.json'),
  JSON.stringify({ port, host: options.host, startedAt: new Date().toISOString() }, null, 2)
)
console.log(`provider-stub: listening on http://${options.host}:${port}/chat/completions`)

for (const signal of ['SIGTERM', 'SIGINT']) {
  process.on(signal, async () => {
    await writeSummary()
    server.close(() => process.exit(0))
  })
}
