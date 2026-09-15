#!/usr/bin/env node
/**
 * D10 线协议门禁：用 **TS 生产 codec** 跑完整语料，并把逐样本结果写成证据。
 *
 * 闸门：
 *   W01 语料非空且覆盖全部消息类型
 *   W02 expected.json 与语料文件一一对应（无孤儿、无悬空引用）
 *   W03 全部样本与 expected.json 一致（golden 解码+canonical；malicious 按码拒绝）
 *   W04 SHA-256 有独立判据（NIST 向量 + coreutils sha256sum 交叉验证）
 *   W05 传输 ack / 草稿 staged / 上传归属 三态不合并
 *   W06 重放缓存容量、生命周期、活动操作钉住与幂等不重导
 *   W07 负向对照：逐样本变异期望必须让同一条判定路径失败
 *
 * 用法：pnpm --dir plugins/dsh-remote-attachments run build && pnpm --dir plugins/dsh-remote-attachments run test:wire
 * 失败时退出码 1；同时写 artifacts/verify-portable 与 artifacts/fixture/evidence。
 */

import { spawnSync } from 'node:child_process'
import { mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  CHUNK_BYTES,
  REPLAY_CACHE_CAPACITY,
  REPLAY_CACHE_LIFETIME_MS,
  MESSAGE_TYPES,
  PROTOCOL_VERSION,
  WIRE_ERROR_CODES,
  canonicalJsonString,
  createSha256,
  decodeMessage,
  sha256HexSync,
  PureJsSha256,
  ReplayCache,
  WireSession,
  makeReplayEntry
} from '../lib/shared/wire/index.js'
import {
  CORPUS_DIR,
  compareExpectation,
  listCorpusFiles,
  loadExpectations,
  loadSampleText,
  mutateExpectation,
  runSample,
  structuralEquals
} from '../test/unit/wire-corpus-support.mjs'

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const repoRoot = resolve(projectRoot, '..', '..')

const gates = []
const addGate = (id, description, ok, detail) => gates.push({ id, description, ok: Boolean(ok), detail: String(detail) })

const codec = {
  decode: (text) => decodeMessage(text),
  createSession: () => new WireSession()
}

/** 解析 schema 内部 $ref（只支持 #/$defs/... 形式）。 */
function resolveRef(schema, ref) {
  if (!ref.startsWith('#/')) throw new Error(`不支持的 $ref: ${ref}`)
  let node = schema
  for (const part of ref.slice(2).split('/')) node = node[part]
  if (!node) throw new Error(`$ref 无法解析: ${ref}`)
  return node
}

function deref(schema, node) {
  return node && node.$ref ? resolveRef(schema, node.$ref) : node
}

/**
 * 语料值必须落在 schema 声明的常量/枚举/整数范围内。
 * 这是"schema 与语料一致"的判据（不是消息校验）：只比对声明出来的约束，
 * 不复刻 codec 的判定顺序与拒绝码。
 */
function checkGoldenAgainstSchema(schema, type, value, path, failures) {
  const def = Object.values(schema.$defs).find((candidate) => candidate.properties?.type?.const === type)
  if (!def) {
    failures.push(`${path}: schema 缺少消息类型 ${type} 的定义`)
    return
  }
  for (const [key, raw] of Object.entries(value)) {
    const spec = def.properties[key]
    if (!spec) {
      failures.push(`${path}.${key}: schema 未声明该字段`)
      continue
    }
    const resolved = deref(schema, spec)
    if (resolved.const !== undefined && raw !== resolved.const) {
      failures.push(`${path}.${key}: ${raw} ≠ schema const ${resolved.const}`)
    }
    if (resolved.enum && !resolved.enum.includes(raw)) {
      failures.push(`${path}.${key}: ${raw} 不在 schema 枚举 ${resolved.enum.join('|')}`)
    }
    if (resolved.type === 'integer') {
      if (!Number.isInteger(raw)) failures.push(`${path}.${key}: 非整数 ${raw}`)
      else if (raw < resolved.minimum || raw > resolved.maximum) {
        failures.push(`${path}.${key}: ${raw} 超出 schema 范围 ${resolved.minimum}..${resolved.maximum}`)
      }
    }
    if (resolved.type === 'array' && Array.isArray(raw)) {
      const items = deref(schema, resolved.items) ?? {}
      if (resolved.maxItems !== undefined && raw.length > resolved.maxItems) {
        failures.push(`${path}.${key}: 项数 ${raw.length} > schema maxItems ${resolved.maxItems}`)
      }
      if (items.enum) {
        for (const item of raw) if (!items.enum.includes(item)) failures.push(`${path}.${key}: ${item} 不在 schema 枚举`)
      }
    }
    if (resolved.type === 'object' && resolved.properties && raw && typeof raw === 'object') {
      for (const [nestedKey, nestedRaw] of Object.entries(raw)) {
        const nestedSpec = deref(schema, resolved.properties[nestedKey])
        if (!nestedSpec) {
          failures.push(`${path}.${key}.${nestedKey}: schema 未声明该字段`)
          continue
        }
        if (nestedSpec.type === 'integer' && (nestedRaw < nestedSpec.minimum || nestedRaw > nestedSpec.maximum)) {
          failures.push(`${path}.${key}.${nestedKey}: ${nestedRaw} 超出 schema 范围`)
        }
      }
    }
  }
  for (const key of Object.keys(value)) {
    if (!def.properties[key]) failures.push(`${path}: 语料出现 schema 未声明的字段 ${key}`)
  }
  for (const required of def.required ?? []) {
    if (!(required in value)) failures.push(`${path}: golden 缺少 schema 必填字段 ${required}`)
  }
  const refs = new Set((schema.oneOf ?? []).map((entry) => entry.$ref))
  if (![...refs].some((ref) => resolveRef(schema, ref) === def)) {
    failures.push(`${path}: ${type} 未被 schema.oneOf 引用`)
  }
}

async function newestMtime(dir, filter) {
  let newest = 0
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) newest = Math.max(newest, await newestMtime(path, filter))
    else if (filter(entry.name)) newest = Math.max(newest, (await stat(path)).mtimeMs)
  }
  return newest
}

async function main() {
  const expected = await loadExpectations()
  const corpusFiles = await listCorpusFiles()
  const samples = expected.samples

  // —— W00 构建新鲜度：lib/ 必须不早于 src/，否则门禁会给出假绿 ——
  const builtAt = (await stat(join(projectRoot, 'lib/shared/wire/index.js'))).mtimeMs
  const sourceAt = await newestMtime(join(projectRoot, 'src'), (name) => name.endsWith('.ts'))
  addGate(
    'W00',
    'lib/ 构建产物不早于 src/*.ts（防止对旧构建给出假绿）',
    builtAt >= sourceAt,
    `builtAt=${new Date(builtAt).toISOString()} sourceAt=${new Date(sourceAt).toISOString()}` +
      (builtAt >= sourceAt ? '' : '；请先执行 pnpm run build')
  )

  // —— W01 语料覆盖 ——
  const goldenTypes = new Set()
  let goldenCount = 0
  let maliciousCount = 0
  for (const sample of samples) {
    if (sample.kind === 'golden') {
      goldenCount += 1
      const decoded = decodeMessage(await loadSampleText(sample.file))
      if (decoded.ok) goldenTypes.add(decoded.message.type)
    } else {
      maliciousCount += 1
    }
  }
  const missingTypes = MESSAGE_TYPES.filter((type) => !goldenTypes.has(type))
  // malicious 样本必须在 decode 或 apply 至少一侧被拒绝，否则它不构成恶意样本。
  const toothless = samples
    .filter((sample) => sample.kind === 'malicious')
    .filter((sample) => sample.expect.decode === 'accept' && sample.expect.apply === 'accept')
    .map((sample) => sample.id)
  const duplicateIds = samples.map((sample) => sample.id).filter((id, index, all) => all.indexOf(id) !== index)
  addGate(
    'W01',
    '语料非空、golden 覆盖全部消息类型、malicious 均至少一侧被拒、样本 id 唯一',
    goldenCount > 0 && maliciousCount > 0 && missingTypes.length === 0 && toothless.length === 0 && duplicateIds.length === 0,
    `golden=${goldenCount} malicious=${maliciousCount} 覆盖类型=${goldenTypes.size}/${MESSAGE_TYPES.length}` +
      (missingTypes.length > 0 ? ` 缺失类型=${missingTypes.join(',')}` : '') +
      (toothless.length > 0 ? ` 无拒绝判据=${toothless.join(',')}` : '') +
      (duplicateIds.length > 0 ? ` 重复 id=${duplicateIds.join(',')}` : '')
  )

  // —— W02 expected.json 与语料一一对应 ——
  const referenced = new Set(samples.flatMap((sample) => [sample.file, ...(sample.setup ?? [])]))
  const orphans = corpusFiles.filter((file) => !referenced.has(file))
  const dangling = [...referenced].filter((file) => !corpusFiles.includes(file))
  addGate(
    'W02',
    'expected.json 无孤儿样本、无悬空引用',
    orphans.length === 0 && dangling.length === 0,
    `corpus=${corpusFiles.length} referenced=${referenced.size} orphans=${orphans.length} dangling=${dangling.length}` +
      (dangling.length > 0 ? ` 悬空=${dangling.join(',')}` : '')
  )

  // —— W03 全语料一致 ——
  const results = []
  for (const sample of samples) results.push(await runSample(sample, codec))
  const failed = results.filter((result) => result.failures.length > 0)
  addGate(
    'W03',
    'TS 生产 codec 与 expected.json 全语料一致',
    failed.length === 0,
    `样本=${results.length} 通过=${results.length - failed.length} 失败=${failed.length}` +
      (failed.length > 0 ? ` 首例=${failed[0].id}: ${failed[0].failures[0]}` : '')
  )

  // —— W04 SHA-256 独立判据 ——
  const vectors = [
    ['', 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    ['abc', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    ['abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq',
      '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1'],
    ['a'.repeat(1000000), 'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0']
  ]
  const vectorFailures = []
  for (const [input, want] of vectors) {
    const bytes = new TextEncoder().encode(input)
    const pure = sha256HexSync(bytes)
    const web = await (async () => {
      const hasher = createSha256()
      hasher.update(bytes)
      return hasher.digestHex()
    })()
    if (pure !== want) vectorFailures.push(`纯 JS(${input.slice(0, 8)})=${pure.slice(0, 12)}`)
    if (web !== want) vectorFailures.push(`WebCrypto(${input.slice(0, 8)})=${web.slice(0, 12)}`)
  }
  // 增量分块必须与一次性结果一致（含非对齐边界）
  const big = new Uint8Array(300000)
  for (let i = 0; i < big.length; i += 1) big[i] = i % 251
  const once = sha256HexSync(big)
  const incremental = new PureJsSha256()
  for (let offset = 0; offset < big.length; offset += 7919) incremental.update(big.subarray(offset, Math.min(offset + 7919, big.length)))
  if (incremental.digestHexSync() !== once) vectorFailures.push('增量分块与一次性摘要不一致')
  // coreutils 交叉验证：语料里的 256 KiB 载荷
  const chunkText = await loadSampleText('golden/chunk-256k-seq0.json')
  const chunkPayload = Buffer.from(JSON.parse(chunkText).dataBase64, 'base64')
  const coreutils = spawnSync('sha256sum', [], { input: chunkPayload, encoding: 'utf8' }).stdout.trim().split(/\s+/)[0]
  const corpusSha = JSON.parse(await loadSampleText('golden/file-end-256k.json')).sha256
  if (coreutils !== corpusSha) vectorFailures.push(`coreutils=${coreutils} 语料=${corpusSha}`)
  if (once.length === 0) vectorFailures.push('空摘要')
  const incrementalEquals = incremental.digestHexSync() === once
  addGate(
    'W04',
    'SHA-256 通过 NIST 向量与 coreutils 交叉验证（含增量分块）',
    vectorFailures.length === 0 && incrementalEquals,
    `向量=${vectors.length} 增量一致=${incrementalEquals} coreutils=${coreutils.slice(0, 16)}… 失败=${vectorFailures.length}` +
      (vectorFailures.length > 0 ? ` ${vectorFailures.join('; ')}` : '')
  )

  // —— W05 三态不合并 ——
  const triSession = new WireSession()
  const feed = async (file) => {
    const decoded = decodeMessage(await loadSampleText(file))
    if (!decoded.ok) throw new Error(`W05 预置解码失败 ${file}: ${decoded.code}`)
    return triSession.apply(decoded.message)
  }
  await feed('golden/context-basic.json')
  await feed('golden/batch-begin-single.json')
  await feed('golden/file-begin-8b.json')
  await feed('golden/chunk-8b-seq0.json')
  const afterChunkKeepAlive = triSession.fileRecord('file-1')
  await feed('golden/ack-8b-seq0.json')
  const afterAck = triSession.fileRecord('file-1')
  await feed('golden/file-end-8b.json')
  const afterEnd = triSession.fileRecord('file-1')
  await feed('golden/import-result-staged-1.json')
  const afterStaged = triSession.fileRecord('file-1')

  const triFailures = []
  if (!afterChunkKeepAlive || afterChunkKeepAlive.transport !== 'buffering') triFailures.push('chunk 后 transport 应为 buffering')
  if (!afterChunkKeepAlive || afterChunkKeepAlive.draft !== 'none') triFailures.push('chunk 后 draft 必须仍为 none（ack≠staged）')
  if (!afterAck || afterAck.transport !== 'buffered') triFailures.push('ack 后 transport 应为 buffered')
  if (!afterAck || afterAck.draft !== 'none') triFailures.push('ack 后 draft 必须仍为 none（传输确认≠草稿接收）')
  if (!afterAck || afterAck.upload !== 'none') triFailures.push('ack 后 upload 必须仍为 none')
  if (!afterEnd || afterEnd.ended !== true || afterEnd.draft !== 'none') triFailures.push('file-end 后 draft 仍应为 none（尚未收到 import-result）')
  if (!afterStaged || afterStaged.draft !== 'staged') triFailures.push('import-result 后 draft 应为 staged')
  if (!afterStaged || afterStaged.upload !== 'harness-owned') triFailures.push('staged 后 upload 应为 harness-owned（仍归 Harness）')
  // 线协议里不允许出现 ready/uploaded 取值
  const statusEnums = JSON.parse(await readFile(join(CORPUS_DIR, 'schema.json'), 'utf8'))
  const resultStatus = statusEnums.$defs.resultStatus.enum
  if (resultStatus.some((value) => /ready|uploaded|upload-ready/i.test(value))) {
    triFailures.push('结果枚举里出现了 ready/uploaded')
  }
  addGate(
    'W05',
    'transport-ack / staged / upload-harness-owned 三态互不合并',
    triFailures.length === 0,
    `afterAck(transport=${afterAck?.transport},draft=${afterAck?.draft},upload=${afterAck?.upload}) ` +
      `afterStaged(draft=${afterStaged?.draft},upload=${afterStaged?.upload}) 失败=${triFailures.length}` +
      (triFailures.length > 0 ? ` ${triFailures.join('; ')}` : '')
  )

  // —— W06 重放缓存 ——
  const cacheFailures = []
  const clock = { now: 1_000 }
  const cacheSession = new WireSession({ now: () => clock.now })
  const feedCache = async (file) => {
    const decoded = decodeMessage(await loadSampleText(file))
    if (!decoded.ok) throw new Error(`W06 预置解码失败 ${file}: ${decoded.code}`)
    return cacheSession.apply(decoded.message)
  }
  await feedCache('golden/context-basic.json')
  await feedCache('golden/batch-begin-single.json')
  await feedCache('golden/file-begin-8b.json')
  await feedCache('golden/chunk-8b-seq0.json')
  await feedCache('golden/ack-8b-seq0.json')
  const firstEnd = await feedCache('golden/file-end-8b.json')
  if (!firstEnd.ok || firstEnd.importInvoked !== true) cacheFailures.push('首次 file-end 必须触发一次导入')
  const pinnedAfterFirstEnd = cacheSession.replayCachePinned
  if (pinnedAfterFirstEnd < 1) cacheFailures.push('file-end 后活动操作未被钉住')
  const repeatedEnd = await feedCache('golden/file-end-8b.json')
  if (!repeatedEnd.ok || repeatedEnd.duplicate !== true || repeatedEnd.importInvoked !== false) {
    cacheFailures.push('重复 file-end 必须幂等且不再导入')
  }

  // 容量压力：两个活动操作撑住容量，淘汰被拒绝而不是牺牲活动操作。
  const pressure = new ReplayCache({ capacity: 1, lifetimeMs: REPLAY_CACHE_LIFETIME_MS, now: () => clock.now })
  const activeA = makeReplayEntry({
    kind: 'file', documentEpoch: 7, composerEpoch: 3, batchId: 'batch-1', fileId: 'file-1',
    payloadDigest: 'a', summary: { importInvoked: true, attachmentIds: ['att-1'], draft: 'staged', status: 'staged' }, now: clock.now
  })
  const activeB = makeReplayEntry({
    kind: 'file', documentEpoch: 7, composerEpoch: 3, batchId: 'batch-1', fileId: 'file-2',
    payloadDigest: 'b', summary: { importInvoked: true, attachmentIds: [], draft: 'none', status: null }, now: clock.now
  })
  pressure.put(activeA)
  pressure.put(activeB)
  if (pressure.size !== 2) cacheFailures.push(`活动条目被驱逐：size=${pressure.size}`)
  if (pressure.refusedEvictionCount < 1) cacheFailures.push('容量压力下未拒绝驱逐活动条目')
  if (pressure.get(activeA.key) === undefined) cacheFailures.push('活动条目在容量压力下丢失')
  // 完成一个条目后，它可以被淘汰，活动条目必须留下。
  pressure.complete(activeA.key, activeA.summary, activeA.payloadDigest)
  const activeC = makeReplayEntry({
    kind: 'file', documentEpoch: 7, composerEpoch: 3, batchId: 'batch-1', fileId: 'file-3',
    payloadDigest: 'c', summary: { importInvoked: true, attachmentIds: [], draft: 'none', status: null }, now: clock.now
  })
  pressure.put(activeC)
  if (pressure.evictedCount < 1) cacheFailures.push('已完成条目未被淘汰')
  if (pressure.get(activeB.key) === undefined) cacheFailures.push('已完成的竞争对手驱逐了活动条目')

  // 生命周期：TTL 到期后条目在查询时被丢弃。
  clock.now += REPLAY_CACHE_LIFETIME_MS - 1
  if (cacheSession.replayCacheSize < 1) cacheFailures.push('TTL 到期前条目不应消失')
  clock.now += 2
  if (cacheSession.replayCacheSize !== 0) {
    cacheFailures.push(`缓存生命周期未生效：过期后仍有 ${cacheSession.replayCacheSize} 条`)
  }

  // 导航清理 + 身份过期
  const navSession = new WireSession()
  await navSession.apply(decodeMessage(await loadSampleText('golden/context-basic.json')).message)
  navSession.navigate()
  if (navSession.replayCacheSize !== 0) cacheFailures.push('导航未清空缓存')
  const lateAfterNavigate = await navSession.apply(
    decodeMessage(await loadSampleText('golden/batch-begin-single.json')).message
  )
  if (lateAfterNavigate.ok || lateAfterNavigate.code !== 'context-changed') {
    cacheFailures.push('导航后身份未过期（迟到消息码：' + (lateAfterNavigate.ok ? 'ok' : lateAfterNavigate.code) + '）')
  }
  addGate(
    'W06',
    `重放缓存：容量 ${REPLAY_CACHE_CAPACITY}、TTL ${REPLAY_CACHE_LIFETIME_MS}ms、活动操作钉住、导航清理`,
    cacheFailures.length === 0,
    `pinnedAfterFirstEnd=${pinnedAfterFirstEnd} pressureEvicted=${pressure.evictedCount} ` +
      `refused=${pressure.refusedEvictionCount} 失败=${cacheFailures.length}` +
      (cacheFailures.length > 0 ? ` ${cacheFailures.join('; ')}` : '')
  )

  // —— W07 负向对照：逐样本变异期望必须失败 ——
  const controlFailures = []
  for (let index = 0; index < samples.length; index += 1) {
    const sample = samples[index]
    const observed = results[index]
    const mutated = mutateExpectation(sample)
    const controlFailuresForSample = compareExpectation(mutated, observed)
    if (controlFailuresForSample.length === 0) {
      controlFailures.push(`${sample.id}(变异后期望仍通过)`)
    }
    // 同一条判定路径对未变异期望必须通过，否则 W03 已经失败——这里只确认对照是"活"的
    const baseline = compareExpectation(sample, observed)
    if (baseline.length !== observed.failures.length) controlFailures.push(`${sample.id}(判定路径不确定)`)
  }
  addGate(
    'W07',
    '负向对照：每个样本变异期望后同一条判定路径必须报错',
    controlFailures.length === 0,
    `对照样本=${samples.length} 未触发失败=${controlFailures.length}` +
      (controlFailures.length > 0 ? ` 首例=${controlFailures[0]}` : '')
  )

  // —— W09 敌意输入健壮性：解码器不得抛异常，拒绝码必须在冻结枚举内 ——
  const fuzzFailures = []
  const parityCases = []
  let fuzzCases = 0
  let seed = 0x5eed1234
  const nextRandom = () => {
    // 确定性 LCG：同一语料每次得到同一批变异，证据可复现。
    seed = (seed * 1103515245 + 12345) & 0x7fffffff
    return seed
  }
  const hostileChars = ['"', '{', '}', '[', ']', ':', ',', '0', '-', '\\', '\u0000', 'e', '.']
  const mutate = (text) => {
    const mode = nextRandom() % 4
    if (text.length === 0) return '{"v":1'
    const at = nextRandom() % text.length
    if (mode === 0) return text.slice(0, at) + hostileChars[nextRandom() % hostileChars.length] + text.slice(at + 1)
    if (mode === 1) return text.slice(0, at)
    if (mode === 2) return text.slice(0, at) + '"x":1,' + text.slice(at)
    return text.slice(0, at) + String(nextRandom() % 200 - 100) + text.slice(at)
  }
  for (const file of corpusFiles) {
    const original = await loadSampleText(file)
    for (let round = 0; round < 4; round += 1) {
      let candidate = original
      for (let step = 0; step <= round; step += 1) candidate = mutate(candidate)
      fuzzCases += 1
      let decoded
      try {
        decoded = decodeMessage(candidate)
      } catch (error) {
        fuzzFailures.push(`${file}#${round} decode 抛异常：${error?.message ?? error}`)
        continue
      }
      if (candidate.length <= 2048) {
        // 差分样例：把 TS 生产 codec 的结论固化下来，供 C# 侧逐条比对。
        parityCases.push({
          file,
          round,
          text: candidate,
          ok: decoded.ok,
          code: decoded.ok ? null : decoded.code,
          canonicalHash: decoded.ok
            ? sha256HexSync(new TextEncoder().encode(canonicalJsonString(decoded.canonical)))
            : null
        })
      }
      if (!decoded.ok) {
        if (!WIRE_ERROR_CODES.includes(decoded.code)) {
          fuzzFailures.push(`${file}#${round} 未冻结的拒绝码 ${decoded.code}`)
        }
        continue
      }
      try {
        const session = new WireSession()
        await session.apply(decoded.message)
      } catch (error) {
        fuzzFailures.push(`${file}#${round} apply 抛异常：${error?.message ?? error}`)
      }
    }
  }
  addGate(
    'W09',
    '敌意变异输入下 decode/apply 不抛异常且只返回冻结拒绝码',
    fuzzFailures.length === 0,
    `变异用例=${fuzzCases} 失败=${fuzzFailures.length}` +
      (fuzzFailures.length > 0 ? ` 首例=${fuzzFailures[0]}` : '')
  )

  // —— W08 schema 与语料一致（常量/枚举/整数范围/字段集） ——
  const schema = JSON.parse(await readFile(join(CORPUS_DIR, 'schema.json'), 'utf8'))
  const schemaFailures = []
  const seenKeys = new Map()
  let schemaChecked = 0
  for (const sample of samples) {
    if (sample.kind !== 'golden') continue
    const value = JSON.parse(await loadSampleText(sample.file))
    checkGoldenAgainstSchema(schema, value.type, value, sample.file, schemaFailures)
    const seen = seenKeys.get(value.type) ?? new Set()
    for (const key of Object.keys(value)) seen.add(key)
    seenKeys.set(value.type, seen)
    schemaChecked += 1
  }
  // 字段集覆盖是"每个类型至少出现一次"：可选字段不必出现在每个样本里。
  for (const [type, seen] of seenKeys) {
    const def = Object.values(schema.$defs).find((candidate) => candidate.properties?.type?.const === type)
    for (const declared of Object.keys(def.properties)) {
      if (!seen.has(declared)) schemaFailures.push(`${type}: golden 语料从未覆盖 schema 字段 ${declared}`)
    }
    for (const key of seen) {
      if (!def.properties[key]) schemaFailures.push(`${type}: golden 使用了 schema 未声明的字段 ${key}`)
    }
  }
  const definedTypes = new Set(
    Object.values(schema.$defs)
      .map((def) => def.properties?.type?.const)
      .filter((type) => typeof type === 'string')
  )
  for (const type of MESSAGE_TYPES) {
    if (!definedTypes.has(type)) schemaFailures.push(`schema 缺少消息类型定义：${type}`)
  }
  addGate(
    'W08',
    'schema.json 与 golden 语料一致（字段集、常量、枚举、整数范围）',
    schemaFailures.length === 0,
    `golden 校验=${schemaChecked} 类型定义=${definedTypes.size}/${MESSAGE_TYPES.length} 失败=${schemaFailures.length}` +
      (schemaFailures.length > 0 ? ` 首例=${schemaFailures[0]}` : '')
  )

  // —— 汇总与证据 ——
  const failedGateIds = gates.filter((gate) => !gate.ok).map((gate) => gate.id)
  const payload = {
    schemaVersion: 1,
    task: 'D10',
    purpose: 'wire-contract',
    platform: 'linux',
    protocolVersion: PROTOCOL_VERSION,
    corpus: {
      dir: 'schemas/remote-attachments/v1',
      files: corpusFiles.length,
      samples: samples.length,
      golden: goldenCount,
      malicious: maliciousCount,
      messageTypes: MESSAGE_TYPES.length,
      errorCodes: WIRE_ERROR_CODES.length,
      chunkBytes: CHUNK_BYTES
    },
    gates,
    failedGateIds,
    result: failedGateIds.length === 0 ? 'pass' : 'fail',
    samples: results.map((result) => ({
      id: result.id,
      kind: result.kind,
      file: result.file,
      decode: typeof result.decode === 'string' ? result.decode : `reject:${result.decode.code}`,
      apply: typeof result.apply === 'string' ? result.apply : `reject:${result.apply.code}`,
      duplicate: result.duplicate,
      importInvoked: result.importInvoked,
      ok: result.failures.length === 0,
      failures: result.failures
    }))
  }

  const json = JSON.stringify(payload, null, 2) + '\n'
  const targets = [
    join(repoRoot, 'artifacts/verify-portable/d10-wire-gates.json'),
    join(repoRoot, 'artifacts/fixture/evidence/d10-wire-gates.json')
  ]
  for (const target of targets) {
    await mkdir(dirname(target), { recursive: true })
    await writeFile(target, json, 'utf8')
  }

  // 跨语言差分样例（由 C# 侧 tests/.../Attachments/WireParityTests.cs 消费）。
  const parityTarget = join(repoRoot, 'artifacts/verify-portable/d10-wire-parity-cases.json')
  await writeFile(
    parityTarget,
    JSON.stringify(
      {
        schemaVersion: 1,
        task: 'D10',
        purpose: 'wire-parity',
        generatedBy: 'plugins/dsh-remote-attachments/scripts/wire-contract-gates.mjs',
        note: '对语料做确定性敌意变异后 TS 生产 codec 的结论；C# 生产 codec 必须逐条一致。',
        cases: parityCases
      },
      null,
      1
    ) + '\n',
    'utf8'
  )

  for (const gate of gates) {
    console.log(`${gate.ok ? 'PASS' : 'FAIL'} ${gate.id} ${gate.description} — ${gate.detail}`)
  }
  const verbose = process.argv.includes('--verbose') || process.argv.includes('-v')
  for (const result of results) {
    if (result.failures.length > 0) {
      console.log(`  sample ${result.id} ${result.file}: ${result.failures.join(' | ')}`)
    } else if (verbose) {
      const decode = typeof result.decode === 'string' ? result.decode : `reject:${result.decode.code}`
      const apply = typeof result.apply === 'string' ? result.apply : `reject:${result.apply.code}`
      console.log(
        `  sample ${result.id} [${result.kind}] ${result.file} decode=${decode} apply=${apply}` +
          (result.duplicate ? ' duplicate=true' : '') +
          (result.importInvoked ? ' importInvoked=true' : '')
      )
    }
  }
  console.log(`samples: ${results.length}（golden=${goldenCount} malicious=${maliciousCount}）`)
  console.log(
    `evidence: ${[...targets, parityTarget].map((target) => target.replace(`${repoRoot}/`, '')).join(', ')}`
  )
  console.log(`result: ${payload.result}`)

  if (failedGateIds.length > 0) process.exitCode = 1
}

main().catch(async (error) => {
  // 意外失败也必须留下证据：追加一条失败闸门后写盘，避免"跑挂了但没有产物"。
  addGate('W99', '门禁脚本自身未抛异常', false, String(error?.stack ?? error).split('\n')[0])
  const failedGateIds = gates.filter((gate) => !gate.ok).map((gate) => gate.id)
  const payload = {
    schemaVersion: 1,
    task: 'D10',
    purpose: 'wire-contract',
    platform: 'linux',
    protocolVersion: PROTOCOL_VERSION,
    gates,
    failedGateIds,
    result: 'fail'
  }
  const json = JSON.stringify(payload, null, 2) + '\n'
  for (const target of [
    join(repoRoot, 'artifacts/verify-portable/d10-wire-gates.json'),
    join(repoRoot, 'artifacts/fixture/evidence/d10-wire-gates.json')
  ]) {
    await mkdir(dirname(target), { recursive: true })
    await writeFile(target, json, 'utf8')
  }
  console.error(`wire-contract-gates: FAIL — ${error?.stack ?? error}`)
  process.exitCode = 1
})
