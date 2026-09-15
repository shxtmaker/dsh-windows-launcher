/**
 * D10 语料一致性测试（TS 侧）。
 *
 * 直接调用**生产 codec**（`lib/shared/wire/`），不复制任何 codec 逻辑：
 * 判定路径与 `scripts/wire-contract-gates.mjs` 共用
 * `wire-corpus-support.mjs`，因此单测通过 = 门禁的同一判据通过。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import { MESSAGE_TYPES, PROTOCOL_VERSION, WIRE_ERROR_CODES, decodeMessage, WireSession } from '../../lib/shared/wire/index.js'
import {
  compareExpectation,
  listCorpusFiles,
  loadExpectations,
  loadSampleText,
  mutateExpectation,
  runSample
} from './wire-corpus-support.mjs'

const codec = {
  decode: (text) => decodeMessage(text),
  createSession: () => new WireSession()
}

test('语料覆盖全部消息类型，且 golden/malicious 都非空', async () => {
  const expected = await loadExpectations()
  assert.equal(expected.protocolVersion, PROTOCOL_VERSION)
  const golden = expected.samples.filter((sample) => sample.kind === 'golden')
  const malicious = expected.samples.filter((sample) => sample.kind === 'malicious')
  assert.ok(golden.length > 0, 'golden 语料不能为空')
  assert.ok(malicious.length > 0, 'malicious 语料不能为空')

  const covered = new Set()
  for (const sample of golden) {
    const decoded = decodeMessage(await loadSampleText(sample.file))
    assert.ok(decoded.ok, `${sample.id} 作为 golden 必须能解码`)
    covered.add(decoded.message.type)
  }
  for (const type of MESSAGE_TYPES) assert.ok(covered.has(type), `golden 未覆盖消息类型 ${type}`)
})

test('每个 golden 样本解码出期望的 canonical，每个 malicious 样本按期望码被拒绝', async () => {
  const expected = await loadExpectations()
  const files = await listCorpusFiles()
  const referenced = new Set(expected.samples.flatMap((sample) => [sample.file, ...(sample.setup ?? [])]))
  assert.deepEqual(files.filter((file) => !referenced.has(file)), [], 'expected.json 存在孤儿语料文件')
  assert.deepEqual([...referenced].filter((file) => !files.includes(file)), [], 'expected.json 存在悬空引用')

  const failures = []
  for (const sample of expected.samples) {
    const observed = await runSample(sample, codec)
    if (observed.failures.length > 0) failures.push(`${sample.id} ${sample.file}: ${observed.failures.join(' | ')}`)
  }
  assert.deepEqual(failures, [], failures.join('\n'))
  assert.equal(expected.samples.length, 86, '语料样本数变化时必须显式更新期望')
})

test('拒绝码都在冻结枚举内，且枚举本身无重复', async () => {
  const expected = await loadExpectations()
  const codes = new Set(WIRE_ERROR_CODES)
  assert.equal(codes.size, WIRE_ERROR_CODES.length, 'WIRE_ERROR_CODES 存在重复项')
  for (const sample of expected.samples) {
    for (const side of ['decode', 'apply']) {
      const value = sample.expect[side]
      if (value !== 'accept' && value !== 'skip') {
        assert.ok(codes.has(value.reject), `${sample.id} 使用了未冻结的拒绝码 ${value.reject}`)
      }
    }
  }
})

test('负向对照：逐样本变异期望后，同一条判定路径必须报错', async () => {
  const expected = await loadExpectations()
  let checked = 0
  for (const sample of expected.samples) {
    const observed = await runSample(sample, codec)
    assert.deepEqual(observed.failures, [], `${sample.id} 基线判定本应通过`)
    const mutated = mutateExpectation(sample)
    const failures = compareExpectation(mutated, observed)
    assert.ok(
      failures.length > 0,
      `${sample.id} 的期望被变异后判定路径仍然通过——说明该样本的期望从未被真正比较`
    )
    checked += 1
  }
  assert.equal(checked, expected.samples.length)
  assert.ok(checked >= 80, `对照样本过少：${checked}`)
})

test('负向对照：故意喂错 codec 输入时，golden 期望必须在同一判定路径上失败', async () => {
  const expected = await loadExpectations()
  const golden = expected.samples.find((sample) => sample.id === 'G05')
  assert.ok(golden, '缺少 G05 样本')
  // 同一个判定函数，输入换成一条类型都不对的消息的解码结果。
  const wrongDecoded = decodeMessage(JSON.stringify({ v: 1, type: 'batch-begin' }))
  assert.equal(wrongDecoded.ok, false)
  const observed = {
    id: 'wrong',
    file: golden.file,
    kind: 'golden',
    decode: { code: wrongDecoded.code, detail: wrongDecoded.detail },
    canonical: null,
    apply: 'skip',
    duplicate: false,
    importInvoked: false,
    fileRecord: null
  }
  const failures = compareExpectation(golden, observed)
  assert.ok(failures.length > 0, '错误输入竟然通过了 golden 期望')
  assert.match(failures.join(' '), /decode/)
})
