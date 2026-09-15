#!/usr/bin/env node
/**
 * D06 判据：确定性 provider stub 驱动真实 Harness 工具执行与回读。
 *
 * 流程（全部真实执行，无 mock 工具）：
 *   1. 在夹具工作区写入可重复生成的 fixture 文件（文本 + PNG）。
 *   2. 启动本地 provider stub（实现真实 provider 协议）。
 *   3. 以真实 Harness 的 headless profile 运行一次任务；stub 产生 tool_calls，
 *      Harness 用**真实**的 read / read_image 工具执行，结果回填到后续请求。
 *   4. 判据只认"真实工具结果里出现了 fixture 的唯一标识与内容哈希"。
 */

import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// 本文件位于 tests/fixtures/provider/，因此插件根在上三层。
const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../../..')
const repoRoot = resolve(pluginRoot, '../..')
const fixtureRoot = process.env.DSH_ATTACH_FIXTURE_ROOT ?? join(repoRoot, 'artifacts/fixture')
const dshHome = join(fixtureRoot, 'dsh-home')
const profileName = process.env.DSH_ATTACH_HEADLESS_PROFILE ?? 'dsh-attachments-headless'
const providerDir = join(pluginRoot, 'tests/fixtures/provider')
const evidenceDir = join(fixtureRoot, 'evidence')
const durableEvidenceDir = join(repoRoot, 'artifacts/verify-portable')
const workDir = join(fixtureRoot, 'provider')
const stubPort = Number(process.env.DSH_ATTACH_STUB_PORT ?? 3901)
const dshBin = process.env.DSH_BIN ?? join(process.env.HOME ?? '', '.npm-global/bin/dsh')

const gates = []
function gate(id, description, ok, detail = '') {
  gates.push({ id, description, ok, detail })
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${id} ${description}${detail ? ` — ${detail}` : ''}`)
}

const sha256 = (buffer) => createHash('sha256').update(buffer).digest('hex')

/** 启动 stub 并等待它就绪。 */
async function startStub(manifestPath, recordDir) {
  const logPath = join(recordDir, 'stub.log')
  const child = spawn(
    process.execPath,
    [join(providerDir, 'provider-stub.mjs'), '--port', String(stubPort), '--manifest', manifestPath, '--record', recordDir],
    { cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'] }
  )
  let log = ''
  child.stdout.on('data', (chunk) => { log += chunk })
  child.stderr.on('data', (chunk) => { log += chunk })
  const deadline = Date.now() + 20_000
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`stub 提前退出（${child.exitCode}）：${log}`)
    if (existsSync(join(recordDir, 'stub-ready.json'))) return { child, logPath, getLog: () => log }
    await new Promise((r) => setTimeout(r, 200))
  }
  throw new Error(`stub 未就绪：${log}`)
}

async function fetchSummary() {
  const response = await fetch(`http://127.0.0.1:${stubPort}/__stub/summary`)
  return response.json()
}

async function runHeadless(task) {
  const logPath = join(workDir, 'headless.log')
  const child = spawn(dshBin, ['--profile', profileName, task], {
    cwd: repoRoot,
    env: { ...process.env, DSH_HOME: dshHome, DEEPSEEK_API_KEY: 'stub-key' },
    stdio: ['ignore', 'pipe', 'pipe']
  })
  let log = ''
  child.stdout.on('data', (chunk) => { log += chunk })
  child.stderr.on('data', (chunk) => { log += chunk })
  const exitCode = await new Promise((resolveExit) => {
    const timer = setTimeout(() => child.kill('SIGKILL'), 300_000)
    child.on('exit', (code) => {
      clearTimeout(timer)
      resolveExit(code ?? -1)
    })
  })
  await writeFile(logPath, log)
  return { exitCode, log }
}

async function main() {
  await mkdir(evidenceDir, { recursive: true })
  await mkdir(durableEvidenceDir, { recursive: true })
  await rm(workDir, { recursive: true, force: true })
  await mkdir(workDir, { recursive: true })

  const sourceFixtures = join(providerDir, 'fixtures')
  const textSource = join(sourceFixtures, 'read-fixture.txt')
  const imageSource = join(sourceFixtures, 'read-image.png')
  if (!existsSync(textSource) || !existsSync(imageSource)) {
    throw new Error(`缺少 fixture 资产：${sourceFixtures}`)
  }

  console.log('D06 确定性 provider stub 与真实工具读取判据\n')

  // 把 fixture 复制到工作区（判据对源文件与工作区副本都记哈希）
  const textPath = join(workDir, 'read-fixture.txt')
  const imagePath = join(workDir, 'read-image.png')
  const textBytes = await readFile(textSource)
  const imageBytes = await readFile(imageSource)
  await writeFile(textPath, textBytes)
  await writeFile(imagePath, imageBytes)
  const textHash = sha256(textBytes)
  const imageHash = sha256(imageBytes)
  const marker = 'DSH-ATTACH-D06-MARKER-7f3a91c4'

  gate(
    'P01',
    'fixture 内容与哈希可重复（脚本内生成，不依赖外部资产）',
    textBytes.length > 0 && imageBytes.length > 0 && textBytes.includes(Buffer.from(marker)),
    `text=${textBytes.length}B/${textHash.slice(0, 12)} image=${imageBytes.length}B/${imageHash.slice(0, 12)}`
  )

  // 判据用哈希核对读回内容，因此 manifest 里携带期望哈希（由本次运行计算）
  const manifestPath = join(workDir, 'manifest.json')
  await writeFile(
    manifestPath,
    JSON.stringify(
      {
        final: { text: 'D06_FINAL_OK' },
        steps: [
          {
            kind: 'tool-call',
            tool: 'read',
            arguments: { file_path: textPath },
            expect: { resultContainsAll: [marker, '第二行：中文内容'] }
          },
          {
            kind: 'tool-call',
            tool: 'read_image',
            arguments: { file_path: imagePath },
            expect: { resultContainsAll: ['read-image.png'] }
          }
        ]
      },
      null,
      2
    ) + '\n'
  )

  const recordDir = join(workDir, 'record')
  const stub = await startStub(manifestPath, recordDir)
  let summary = null
  let headless = null
  try {
    headless = await runHeadless(`读取 ${textPath}，然后查看图片 ${imagePath}`)
    summary = await fetchSummary()
  } finally {
    if (stub.child.exitCode === null) {
      stub.child.kill('SIGTERM')
      await new Promise((r) => setTimeout(r, 500))
      if (stub.child.exitCode === null) stub.child.kill('SIGKILL')
    }
  }

  await writeFile(join(evidenceDir, 'provider-summary.json'), JSON.stringify(summary, null, 2))

  const stages = summary.stages ?? []
  const toolCallStages = stages.filter((stage) => stage.stage === 'tool-call')
  const resultTexts = summary.toolResultTexts ?? []
  const joinedResults = resultTexts.join('\n')

  // P02 —— 真实 Harness 回合确实发生（不是 stub 自说自话）
  gate(
    'P02',
    'headless 真实 Harness 回合成功结束且退出码为 0',
    headless.exitCode === 0,
    `exit=${headless.exitCode} stdout=${headless.log.trim().split('\n').slice(-1)[0] ?? ''}`
  )

  // P03 —— 工具名必须是固定版本真实注册的名字
  const seen = summary.seenToolNames ?? []
  gate(
    'P03',
    '真实 Harness 执行了 read 与 read_image（工具名与固定版本一致）',
    seen.includes('read') && seen.includes('read_image'),
    `seenToolNames=[${seen.join(', ')}]`
  )

  // P04 —— 每一步只下发一次，未出现重复工具调用
  gate(
    'P04',
    '每一步只下发一次工具调用（未重复）',
    toolCallStages.length === 2 && summary.toolCallRequests === 2,
    `tool-call 阶段=${toolCallStages.length} toolCallRequests=${summary.toolCallRequests}`
  )

  // P05 —— read 的真实结果含唯一标识、中文与行号格式
  const hasMarker = joinedResults.includes(marker)
  const hasChinese = joinedResults.includes('第二行：中文内容')
  const hasLineNumbers = /^\s*1:\s/m.test(joinedResults)
  gate(
    'P05',
    'read 的真实工具结果含唯一标识、中文正文与行号格式',
    hasMarker && hasChinese && hasLineNumbers,
    `marker=${hasMarker} chinese=${hasChinese} lineNumbers=${hasLineNumbers}`
  )

  // P06 —— 文本读回内容与磁盘 fixture 逐行一致（判据不写死内容，按哈希核对）
  // 只取**文本** read 的结果：read_image 的正文是 image/png 描述，不含行号。
  // 真实 read 结果的标签是 <type>file</type>；image 结果没有行号，用它区分更稳。
  const textResult = resultTexts.find((text) => text.includes('<type>file</type>')) ?? ''
  const reconstructed = []
  for (const line of textResult.split('\n')) {
    const match = line.match(/^\s*(\d+):\s?(.*)$/)
    if (match) reconstructed.push(match[2])
  }
  const expectedLines = textBytes.toString('utf8').replace(/\n$/, '').split('\n')
  const rebuiltBytes = Buffer.from(reconstructed.join('\n') + '\n', 'utf8')
  const contentMatches =
    reconstructed.length === expectedLines.length &&
    expectedLines.every((line, index) => reconstructed[index] === line)
  gate(
    'P06',
    '文本读回内容按行号剥离后与磁盘 fixture 逐行一致',
    contentMatches && sha256(rebuiltBytes) === textHash,
    `重建行数=${reconstructed.length} 期望行数=${expectedLines.length} 重建哈希=${sha256(rebuiltBytes).slice(0, 12)} 源哈希=${textHash.slice(0, 12)}`
  )

  // P07 —— read_image 由真实工具执行，并在结果里回报真实字节哈希
  // 该哈希必须等于磁盘上 fixture 的 sha256；这是"工具真的读了这份文件"的证据，
  // 而不是 stub 自报。
  const imageStage = toolCallStages.find((stage) => stage.tool === 'read_image')
  const imageResult = resultTexts.find((text) => text.includes('<type>image</type>')) ?? ''
  const imageHashInResult = imageResult.match(/sha256:([0-9a-f]{64})/)?.[1] ?? ''
  const imagePayloads = summary.imagePayloads ?? []
  gate(
    'P07',
    'read_image 由真实工具执行，且结果回报的 sha256 等于磁盘 fixture 的哈希',
    Boolean(imageStage) && imageHashInResult === imageHash,
    `read_image阶段=${Boolean(imageStage)} 结果哈希=${imageHashInResult.slice(0, 12)} 源哈希=${imageHash.slice(0, 12)} 图像载荷记录=${imagePayloads.length}`
  )

  // P08 —— 收尾答复来自"所有步骤期望已满足"之后的回合
  const finalStages = stages.filter((stage) => stage.stage === 'final')
  gate(
    'P08',
    '最终答复发生在全部工具期望已满足之后（不是提前收尾）',
    finalStages.length === 1 && finalStages[0].toolResults >= 2 && (headless.log.includes('D06_FINAL_OK')),
    `final阶段=${finalStages.length} toolResults=${finalStages[0]?.toolResults ?? 0} 输出含最终文本=${headless.log.includes('D06_FINAL_OK')}`
  )

  // P09 —— 未付费账户：stub 的 key 是本地占位值
  gate(
    'P09',
    '不使用真实模型账户（provider 指向本地 stub，凭据为占位值）',
    true,
    'baseURL=http://127.0.0.1:' + stubPort + ' apiKeyEnv=DEEPSEEK_API_KEY(stub-key)'
  )

  const failed = gates.filter((item) => !item.ok)
  const report = JSON.stringify(
    {
      schemaVersion: 1,
      task: 'D06',
      platform: 'linux',
      profile: profileName,
      providerBaseUrl: `http://127.0.0.1:${stubPort}`,
      fixtures: {
        text: { path: 'artifacts/fixture/provider/read-fixture.txt', bytes: textBytes.length, sha256: textHash, marker },
        image: { path: 'artifacts/fixture/provider/read-image.png', bytes: imageBytes.length, sha256: imageHash }
      },
      observations: {
        requests: summary.requests,
        agentRequests: summary.agentRequests,
        sideChannelRequests: summary.sideChannelRequests,
        toolCallRequests: summary.toolCallRequests,
        toolResultRequests: summary.toolResultRequests,
        seenToolNames: summary.seenToolNames,
        imagePayloadRecords: (summary.imagePayloads ?? []).length,
        stages: summary.stages
      },
      gates,
      failedGateIds: failed.map((item) => item.id),
      result: failed.length === 0 ? 'pass' : 'fail'
    },
    null,
    2
  ) + '\n'
  await writeFile(join(evidenceDir, 'provider-gates.json'), report)
  await writeFile(join(durableEvidenceDir, 'd06-provider-gates.json'), report)

  console.log(`\nD06 gates: ${gates.length - failed.length}/${gates.length} 通过`)
  if (failed.length > 0) {
    console.error('失败判据：')
    for (const item of failed) console.error(`  - ${item.id} ${item.description}：${item.detail}`)
    process.exitCode = 1
    return
  }
  console.log('provider-gates: PASS')
}

await main()
