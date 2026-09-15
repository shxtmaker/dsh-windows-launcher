/**
 * D10 状态机与完整性测试（TS 侧）。
 *
 * 覆盖三态互不合并、重放缓存容量/生命周期/钉住、file-end 校验、批次关闭与 cancel、
 * 导航过期、超时判定，以及"没有 Web Crypto 时仍必须校验内容完整性"。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import {
  CHUNK_BYTES,
  MAX_CHUNKS_IN_FLIGHT,
  PureJsSha256,
  REPLAY_CACHE_LIFETIME_MS,
  WIRE_TIMEOUTS,
  ReplayCache,
  WireSession,
  createSha256,
  decodeMessage,
  makeReplayEntry,
  sha256Hex,
  sha256HexSync
} from '../../lib/shared/wire/index.js'
import { CORPUS_DIR, loadSampleText } from './wire-corpus-support.mjs'

const readSample = async (file) => {
  const decoded = decodeMessage(await loadSampleText(file))
  assert.equal(decoded.ok, true, `${file} 必须解码成功（${decoded.ok ? '' : decoded.code}）`)
  return decoded.message
}

const feed = async (session, file) => {
  const message = await readSample(file)
  return session.apply(message)
}

const newSession = () => new WireSession()

/** 三态断言：三层必须各自独立成立，任何一层顶替另一层都会失败。 */
function assertTriState(record, expected) {
  assert.ok(record, '缺少单文件记录')
  assert.equal(record.transport, expected.transport, 'transport 状态不符')
  assert.equal(record.draft, expected.draft, 'draft 状态不符')
  assert.equal(record.upload, expected.upload, 'upload 归属不符')
}

test('SHA-256：NIST 向量在两条后端上一致，且无 Web Crypto 时走纯 JS 增量实现', async () => {
  const vectors = [
    ['', 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    ['abc', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    ['abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq',
      '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1']
  ]
  for (const [input, want] of vectors) {
    const bytes = new TextEncoder().encode(input)
    assert.equal(sha256HexSync(bytes), want)
    assert.equal(await sha256Hex(bytes), want)
  }

  const webCrypto = createSha256()
  assert.equal(webCrypto.backend, 'webcrypto', 'Node 环境应优先使用 Web Crypto')
  const noCrypto = createSha256({})
  assert.equal(noCrypto.backend, 'pure-js', '没有 Web Crypto 时必须退回纯 JS 实现')

  // 增量分块（跨块边界、非对齐长度）必须与一次性摘要一致
  const payload = new Uint8Array(200000)
  for (let i = 0; i < payload.length; i += 1) payload[i] = (i * 7) % 256
  const hasher = new PureJsSha256()
  for (let offset = 0; offset < payload.length; offset += 61) {
    hasher.update(payload.subarray(offset, Math.min(offset + 61, payload.length)))
  }
  assert.equal(hasher.digestHexSync(), sha256HexSync(payload))
  assert.equal(hasher.digestHexSync(), sha256HexSync(payload), '摘要必须可重复读取')
})

test('传输 ack 只代表接收缓冲：三层状态分别成立且互不顶替', async () => {
  const session = newSession()
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-single.json')
  await feed(session, 'golden/file-begin-8b.json')
  await feed(session, 'golden/chunk-8b-seq0.json')
  const afterChunk = session.fileRecord('file-1')
  assertTriState(afterChunk, { transport: 'buffering', draft: 'none', upload: 'none' })

  await feed(session, 'golden/ack-8b-seq0.json')
  const afterAck = session.fileRecord('file-1')
  assertTriState(afterAck, { transport: 'buffered', draft: 'none', upload: 'none' })

  const endResult = await feed(session, 'golden/file-end-8b.json')
  assert.equal(endResult.ok, true)
  assert.equal(endResult.importInvoked, true, 'file-end 干净时必须触发一次草稿导入')
  assertTriState(session.fileRecord('file-1'), { transport: 'buffered', draft: 'none', upload: 'none' })

  await feed(session, 'golden/import-result-staged-1.json')
  assertTriState(session.fileRecord('file-1'), {
    transport: 'buffered',
    draft: 'staged',
    upload: 'harness-owned'
  })

  // 负向对照：同一断言函数必须能拒绝被顶替的状态（否则上面的断言是空的）。
  for (const wrong of [
    { transport: 'buffered', draft: 'staged', upload: 'none' },
    { transport: 'staged', draft: 'none', upload: 'none' },
    { transport: 'buffered', draft: 'none', upload: 'ready' }
  ]) {
    assert.throws(
      () => assertTriState(afterAck, wrong),
      /状态不符|归属不符/,
      `被顶替的三态 ${JSON.stringify(wrong)} 竟然通过了断言`
    )
  }
})

test('没有 Web Crypto 时内容完整性校验照常执行（HTTP 局域网路径）', async () => {
  const session = new WireSession({ crypto: {} })
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-single.json')
  await feed(session, 'golden/file-begin-8b.json')
  await feed(session, 'golden/chunk-8b-seq0.json')
  await feed(session, 'golden/ack-8b-seq0.json')
  const ok = await feed(session, 'golden/file-end-8b.json')
  assert.equal(ok.ok, true)
  assert.equal(ok.importInvoked, true)

  // 同一条路径（同样没有 Web Crypto）上，哈希不符必须被拒绝且不触发导入。
  const zeroSession = new WireSession({ crypto: {} })
  await feed(zeroSession, 'golden/context-basic.json')
  await feed(zeroSession, 'golden/batch-begin-zero.json')
  await feed(zeroSession, 'golden/file-begin-zero.json')
  const bad = decodeMessage(JSON.stringify({
    v: 1, type: 'file-end', sessionId: 'session-1', batchId: 'batch-2', fileId: 'file-1',
    totalBytes: 0, sha256: 'f'.repeat(64)
  }))
  assert.equal(bad.ok, true, bad.ok ? '' : bad.code)
  const mismatch = await zeroSession.apply(bad.message)
  assert.equal(mismatch.ok, false)
  assert.equal(mismatch.code, 'hash-mismatch')
  assert.equal(zeroSession.fileRecord('file-1').ended, false)
})

test('重复 chunk/file-end/batch-end 不产生第二次导入', async () => {
  const session = newSession()
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-single.json')
  await feed(session, 'golden/file-begin-8b.json')
  await feed(session, 'golden/chunk-8b-seq0.json')

  const repeatChunk = await feed(session, 'golden/chunk-8b-seq0.json')
  assert.equal(repeatChunk.ok, false)
  assert.equal(repeatChunk.code, 'seq-overlap', '重复块必须是重叠拒绝而不是重新入缓冲')

  await feed(session, 'golden/ack-8b-seq0.json')
  const first = await feed(session, 'golden/file-end-8b.json')
  assert.equal(first.importInvoked, true)
  const second = await feed(session, 'golden/file-end-8b.json')
  assert.equal(second.ok, true)
  assert.equal(second.duplicate, true)
  assert.equal(second.importInvoked, false, '重复 file-end 不得再次导入')

  await feed(session, 'golden/import-result-staged-1.json')
  const closed = await feed(session, 'golden/batch-end-staged.json')
  assert.equal(closed.ok, true)
  const repeatEnd = await feed(session, 'golden/batch-end-staged.json')
  assert.equal(repeatEnd.ok, true)
  assert.equal(repeatEnd.duplicate, true)
  assert.equal(repeatEnd.importInvoked, false, '重复 batch-end 不得再次导入')

  // 批次关闭后必须经新的 batch-begin 才能继续：迟到 file-end 被拒。
  const lateChunk = await feed(session, 'golden/chunk-8b-seq0.json')
  assert.equal(lateChunk.ok, false)
  assert.equal(lateChunk.code, 'batch-closed')
})

test('重放缓存：容量淘汰不驱逐活动操作，TTL 到期即失效', async () => {
  const clock = { now: 5000 }
  const summary = { importInvoked: true, attachmentIds: [], draft: 'none', status: null }
  const cache = new ReplayCache({ capacity: 1, lifetimeMs: REPLAY_CACHE_LIFETIME_MS, now: () => clock.now })
  const entry = (fileId) => makeReplayEntry({
    kind: 'file', documentEpoch: 7, composerEpoch: 3, batchId: 'batch-1', fileId,
    payloadDigest: fileId, summary, now: clock.now
  })

  const first = entry('file-1')
  const second = entry('file-2')
  cache.put(first)
  cache.put(second)
  assert.equal(cache.size, 2, '两个活动操作都必须保留（容量可被钉住条目超出）')
  assert.equal(cache.refusedEvictionCount, 1, '容量压力下必须拒绝驱逐活动条目')
  assert.ok(cache.get(first.key), '活动条目不得因淘汰消失')

  cache.complete(first.key, summary, 'file-1')
  cache.put(entry('file-3'))
  assert.equal(cache.evictedCount, 1, '已完成条目应被淘汰')
  assert.ok(cache.get(second.key), '淘汰不得波及活动条目')

  clock.now += REPLAY_CACHE_LIFETIME_MS - 1
  assert.equal(cache.size, 2, 'TTL 未到期时条目必须保留')
  clock.now += 2
  assert.equal(cache.size, 0, 'TTL 到期后条目必须失效')
  assert.equal(cache.get(second.key), undefined)
})

test('file-end 声明失败不触发导入；submittedItems 决定期望的新增 ID 数', async () => {
  const session = newSession()
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-single.json')
  await feed(session, 'golden/file-begin-8b.json')
  await feed(session, 'golden/chunk-8b-seq0.json')
  await feed(session, 'golden/ack-8b-seq0.json')

  const wrongBytes = await session.apply(await readSample('malicious/file-end-byte-count-mismatch.json'))
  assert.equal(wrongBytes.ok, false)
  assert.equal(wrongBytes.code, 'size-mismatch')
  assert.equal(session.fileRecord('file-1').ended, false, '校验失败不得把文件标记为已结束')

  // submittedItems=2（兼容 adapter 一次提交多项）：ACK 必须回 2 个新增 ID。
  const twoItems = decodeMessage(JSON.stringify({
    v: 1, type: 'file-end', sessionId: 'session-1', batchId: 'batch-1', fileId: 'file-1',
    totalBytes: 8, sha256: '9c56cc51b374c3ba189210d5b6d4bf57790d351c96c47c02190ecf1e430635ab',
    submittedItems: 2
  }))
  assert.equal(twoItems.ok, true, twoItems.ok ? '' : twoItems.code)
  const ended = await session.apply(twoItems.message)
  assert.equal(ended.importInvoked, true)

  const oneId = decodeMessage(JSON.stringify({
    v: 1, type: 'import-result', sessionId: 'session-1', batchId: 'batch-1', fileId: 'file-1',
    status: 'staged', attachmentIds: ['att-1']
  }))
  const rejected = await session.apply(oneId.message)
  assert.equal(rejected.ok, false)
  assert.equal(rejected.code, 'import-id-count-mismatch')

  const twoIds = decodeMessage(JSON.stringify({
    v: 1, type: 'import-result', sessionId: 'session-1', batchId: 'batch-1', fileId: 'file-1',
    status: 'staged', attachmentIds: ['att-1', 'att-2']
  }))
  const accepted = await session.apply(twoIds.message)
  assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.code)
  assert.deepEqual([...session.fileRecord('file-1').attachmentIds], ['att-1', 'att-2'])
})

test('cancel 保留已确认草稿、拒绝迟到结果，且不删除远端共享文件', async () => {
  const session = newSession()
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-two.json')
  await feed(session, 'golden/file-begin-two-a.json')
  // 用 batch-6 自己的 chunk：直接改 batchId 走生产 codec 的编码器，而不是手写协议。
  const raw = JSON.parse(await loadSampleText('golden/chunk-8b-seq0.json'))
  const chunk = decodeMessage(JSON.stringify({ ...raw, batchId: 'batch-6' }))
  assert.equal(chunk.ok, true, chunk.ok ? '' : chunk.code)
  assert.equal((await session.apply(chunk.message)).ok, true)
  const ack = decodeMessage(JSON.stringify({ ...JSON.parse(await loadSampleText('golden/ack-8b-seq0.json')), batchId: 'batch-6' }))
  assert.equal((await session.apply(ack.message)).ok, true)
  const end = decodeMessage(JSON.stringify({ ...JSON.parse(await loadSampleText('golden/file-end-8b.json')), batchId: 'batch-6' }))
  assert.equal((await session.apply(end.message)).ok, true)
  const staged = decodeMessage(JSON.stringify({
    v: 1, type: 'import-result', sessionId: 'session-1', batchId: 'batch-6', fileId: 'file-1',
    status: 'staged', attachmentIds: ['att-9']
  }))
  assert.equal((await session.apply(staged.message)).ok, true)

  const cancel = decodeMessage(JSON.stringify({
    v: 1, type: 'cancel', sessionId: 'session-1', batchId: 'batch-6', reason: 'cancelled', stage: 'protocol-transfer'
  }))
  assert.equal(cancel.ok, true, cancel.ok ? '' : cancel.code)
  const cancelled = await session.apply(cancel.message)
  assert.equal(cancelled.ok, true)
  assert.deepEqual([...session.fileRecord('file-1').attachmentIds], ['att-9'], 'cancel 不得丢弃已确认的草稿')

  const lateChunk = decodeMessage(JSON.stringify({
    ...JSON.parse(await loadSampleText('malicious/chunk-after-cancel.json')),
    batchId: 'batch-6'
  }))
  assert.equal(lateChunk.ok, true, lateChunk.ok ? '' : lateChunk.code)
  const lateResult = await session.apply(lateChunk.message)
  assert.equal(lateResult.ok, false)
  assert.equal(lateResult.code, 'cancelled')
})

test('导航/关闭清空缓存并让身份过期，迟到 file-end 不能重建操作', async () => {
  const session = newSession()
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-single.json')
  await feed(session, 'golden/file-begin-8b.json')
  assert.ok(session.replayCacheSize > 0)
  session.navigate()
  assert.equal(session.replayCacheSize, 0, '导航必须清空重放缓存')
  assert.equal(session.currentIdentity.expired, true)

  const late = await feed(session, 'golden/file-end-8b.json')
  assert.equal(late.ok, false)
  assert.equal(late.code, 'context-changed', '过期身份必须在进入缓冲前被拒绝')
  assert.equal(session.fileRecord('file-1'), null)

  // 新的 context 重新建立身份后可以继续。
  await feed(session, 'golden/context-basic.json')
  assert.equal(session.currentIdentity.expired, false)
})

test('超时判定：ACK 停滞与批次空闲都会取消操作并释放缓冲', async () => {
  const clock = { now: 1000 }
  const session = new WireSession({ now: () => clock.now })
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-single.json')
  await feed(session, 'golden/file-begin-8b.json')
  await feed(session, 'golden/chunk-8b-seq0.json')

  assert.deepEqual(session.checkTimeouts(), { stalledFileIds: [], cancelledBatchId: null })
  clock.now += WIRE_TIMEOUTS.ackMs + 1
  const ackStall = session.checkTimeouts()
  assert.deepEqual([...ackStall.stalledFileIds], ['file-1'], 'ACK 停滞必须被报告')
  assert.equal(ackStall.cancelledBatchId, 'batch-1')

  const late = await feed(session, 'golden/chunk-8b-seq0.json')
  assert.equal(late.ok, false)
  assert.equal(late.code, 'cancelled')

  // 空闲超时：新会话上没有块在途，批次整体空闲超时同样取消。
  const idleClock = { now: 2000 }
  const idleSession = new WireSession({ now: () => idleClock.now })
  await feed(idleSession, 'golden/context-basic.json')
  await feed(idleSession, 'golden/batch-begin-single.json')
  idleClock.now += WIRE_TIMEOUTS.batchIdleMs + 1
  assert.equal(idleSession.checkTimeouts().cancelledBatchId, 'batch-1')
})

test('冻结常量与窗口语义：块上限与在途窗口不可被绕过', async () => {
  assert.equal(CHUNK_BYTES, 262144)
  assert.equal(MAX_CHUNKS_IN_FLIGHT, 2)

  const session = newSession()
  await feed(session, 'golden/context-basic.json')
  await feed(session, 'golden/batch-begin-24b.json')
  await feed(session, 'golden/file-begin-24b.json')
  await feed(session, 'golden/chunk-24b-seq0.json')
  await feed(session, 'golden/chunk-24b-seq1.json')
  const third = await feed(session, 'malicious/window-overflow.json')
  assert.equal(third.ok, false)
  assert.equal(third.code, 'window-overflow')

  // 载荷经过生产 codec 的 Base64 解码，块大小上限由 schema 与 codec 双重约束。
  const tooBig = decodeMessage(JSON.stringify({
    v: 1, type: 'chunk', sessionId: 'session-1', batchId: 'batch-3', fileId: 'file-1',
    seq: 0, offset: 0, byteLength: CHUNK_BYTES + 1, dataBase64: 'AAAA'
  }))
  assert.equal(tooBig.ok, false)
  assert.equal(tooBig.code, 'integer-out-of-range')
})
