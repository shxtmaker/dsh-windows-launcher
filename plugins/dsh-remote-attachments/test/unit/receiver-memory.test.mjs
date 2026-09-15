/**
 * D12 接收端内存边界与取消释放测试（导入**构建产物** `lib/`）。
 *
 * 判据核心：接收缓冲有一个**可读的记账**，且在 cancel / 批次关闭 / 身份失效 / 超时 /
 * 淘汰时真的归零——不是"看起来释放了"。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import { CHUNK_BYTES, WIRE_TIMEOUTS, sha256HexSync } from '../../lib/shared/wire/index.js'
import { createAttachmentReceiver } from '../../lib/client/index.js'
import {
  SESSION_ID,
  accept,
  batchBegin,
  batchEndMessage,
  cancelMessage,
  chunkMessages,
  contextMessage,
  decodeStrict,
  feed,
  fileBegin,
  fileEnd,
  fileBytes,
  importStub,
  sourceBytes
} from './receiver-fixtures.mjs'

const CHUNK = 4096

async function startFile(receiver, { bytes, fileId = 'file-1', batchId = 'batch-1', fileCount = 1, totalBytes = bytes.length }) {
  assert.equal((await feed(receiver, contextMessage())).result.ok, true)
  const batch = await feed(receiver, batchBegin({ batchId, fileCount, totalBytes }))
  assert.equal(batch.result.ok, true, `batch-begin：${batch.result.ok ? '' : batch.result.code}`)
  const begin = await feed(
    receiver,
    fileBegin({ batchId, fileId, byteLength: bytes.length, sha256: sha256HexSync(bytes), name: 'd12.bin' })
  )
  assert.equal(begin.result.ok, true, `file-begin：${begin.result.ok ? '' : begin.result.code}`)
}

test('cancel（对端发起）释放该文件全部缓冲，迟到块不再入缓冲', async () => {
  const source = sourceBytes(3 * CHUNK, 41)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  await startFile(receiver, { bytes: source })
  const chunks = chunkMessages(source, { chunkBytes: CHUNK })

  await feed(receiver, chunks[0])
  await feed(receiver, chunks[1])
  assert.equal(receiver.bufferedBytes, 2 * CHUNK, '取消前必须真的有缓冲（否则"归零"是空的）')
  assert.equal(receiver.accounting.bufferedFiles, 1)

  const cancelled = await feed(receiver, cancelMessage('batch-1'))
  assert.equal(cancelled.result.ok, true)
  const accounting = receiver.accounting
  assert.equal(receiver.bufferedBytes, 0, 'cancel 之后接收缓冲必须为 0')
  assert.equal(accounting.bufferedFiles, 0)
  assert.equal(accounting.releasedBytes, 2 * CHUNK, '释放的字节数必须被记账')
  assert.equal(accounting.releasedFiles, 1)
  assert.equal(accounting.peakBufferedBytes, 2 * CHUNK, '峰值记账保留，可证明曾经真的占过内存')
  assert.equal(accounting.filesConstructed, 0, 'cancel 不得构造 File')
  assert.equal(calls.length, 0, 'cancel 不得触发导入')

  const late = await feed(receiver, chunks[0])
  assert.equal(late.result.ok, false)
  assert.equal(late.result.code, 'cancelled')
  assert.equal(receiver.bufferedBytes, 0, '取消后的迟到块不得重新占用缓冲')
})

test('cancel（接收端发起）同样释放缓冲，并把 cancel 交给对端', async () => {
  const source = sourceBytes(2 * CHUNK, 43)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  await startFile(receiver, { bytes: source })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(receiver.bufferedBytes, CHUNK)

  const result = await receiver.cancelActiveBatch('cancelled')
  assert.ok(result !== null && result.ok === true)
  assert.equal(receiver.bufferedBytes, 0)
  assert.equal(receiver.accounting.releasedBytes, CHUNK)
  const outgoing = await receiver.drainOutgoing()
  assert.equal(outgoing.length, 1)
  assert.equal(outgoing[0].type, 'cancel')
  assert.equal(outgoing[0].batchId, 'batch-1')
})

test('批次关闭后缓冲归零；迟到的 cancel 不会重建操作', async () => {
  const source = sourceBytes(CHUNK, 47)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  await startFile(receiver, { bytes: source })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  const end = await feed(receiver, fileEnd({ totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true)
  assert.equal(receiver.bufferedBytes, 0, 'File 构造后缓冲立即归零（字节归 File 所有）')
  assert.equal(receiver.accounting.constructedBytes, source.length)
  assert.equal(receiver.accounting.releasedBytes, 0, '构造路径不是"释放"')

  const closed = await feed(receiver, batchEndMessage('batch-1', [{ fileId: 'file-1', status: 'staged', attachmentIds: ['att-1'] }]))
  assert.equal(closed.result.ok, true)
  assert.equal(receiver.bufferedBytes, 0)

  // D10 语义：批次关闭后该 batchId 只再接受重复 batch-end 与 cancel；cancel 必须被接受，
  // 但不得借它重建任何缓冲或操作。
  const lateCancel = await feed(receiver, cancelMessage('batch-1'))
  assert.equal(lateCancel.result.ok, true)
  assert.equal(receiver.bufferedBytes, 0)
  assert.equal(receiver.accounting.filesConstructed, 1)
  const afterCancel = await feed(receiver, fileEnd({ totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(afterCancel.result.ok, false)
  // D10 的判定顺序是 cancelled 先于 batch-closed（session.ts guardBatch），语义一致。
  assert.equal(afterCancel.result.code, 'cancelled', 'cancel 不得让已关闭批次复活')
  assert.equal(receiver.accounting.filesConstructed, 1, '迟到的 file-end 不得构造第二个 File')
  assert.equal(receiver.bufferedBytes, 0)
})

test('身份失效（context 变化 / navigate）释放全部缓冲，迟到消息不得重建操作', async () => {
  const source = sourceBytes(2 * CHUNK, 53)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  await startFile(receiver, { bytes: source })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(receiver.bufferedBytes, CHUNK)

  // 同一个 sessionId、新的 composerEpoch：身份变化即失效，旧批次与缓冲都不得复用。
  const rebound = await feed(receiver, contextMessage({ composerEpoch: 23 }))
  assert.equal(rebound.result.ok, true)
  assert.equal(receiver.bufferedBytes, 0, '身份变化必须释放旧缓冲')
  assert.equal(receiver.accounting.releasedBytes, CHUNK)
  assert.equal(receiver.activeBatchId, null)

  const lateChunk = await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(lateChunk.result.ok, false)
  assert.equal(lateChunk.result.code, 'batch-not-open', '身份变化后旧批次状态被清空，迟到消息无法重建操作')
  const lateEnd = await feed(receiver, fileEnd({ totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(lateEnd.result.ok, false)
  assert.equal(lateEnd.result.code, 'batch-not-open')
  assert.equal(calls.length, 0)

  // sessionId 都不同时是 context-changed：身份门在任何状态机之前生效。
  const foreign = await feed(
    receiver,
    decodeStrict({
      v: 1,
      type: 'chunk',
      sessionId: 'session-other',
      batchId: 'batch-1',
      fileId: 'file-1',
      seq: 0,
      offset: 0,
      byteLength: 1,
      dataBase64: 'AA=='
    })
  )
  assert.equal(foreign.result.ok, false)
  assert.equal(foreign.result.code, 'context-changed')
  assert.equal(receiver.bufferedBytes, 0)
})

test('navigate() 释放缓冲并让身份过期，迟到消息是 context-changed', async () => {
  const source = sourceBytes(CHUNK, 59)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  await startFile(receiver, { bytes: source })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(receiver.bufferedBytes, CHUNK)

  receiver.navigate()
  assert.equal(receiver.bufferedBytes, 0)
  assert.equal(receiver.accounting.releasedBytes, CHUNK)
  assert.equal(receiver.currentIdentity.expired, true)

  const late = await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(late.result.ok, false)
  assert.equal(late.result.code, 'context-changed')
  assert.equal(receiver.bufferedBytes, 0)
})

test('目标暂存上限：超限块被拒绝而不是缓冲，且拒绝不推进状态机', async () => {
  const source = sourceBytes(3 * CHUNK, 61)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles, limits: { maxStagingBytesPerTarget: CHUNK + 512 } })
  assert.equal(receiver.accounting.stagingByteLimit, CHUNK + 512)
  await startFile(receiver, { bytes: source })

  const chunks = chunkMessages(source, { chunkBytes: CHUNK })
  assert.equal((await feed(receiver, chunks[0])).result.ok, true)
  assert.equal(receiver.bufferedBytes, CHUNK)

  // 第二块会把缓冲推到 2×CHUNK > CHUNK+512：必须拒绝，且**不得**写入缓冲。
  const over = await feed(receiver, chunks[1])
  assert.equal(over.result.ok, false)
  assert.equal(over.result.code, 'limit-staging-bytes')
  assert.equal(receiver.bufferedBytes, CHUNK, '超限块被拒绝而不是缓冲')

  // 拒绝没有推进 seq/offset：换一个能塞进上限的小块（seq=1, offset=CHUNK）必须被接受。
  const small = decodeStrict({
    v: 1,
    type: 'chunk',
    sessionId: SESSION_ID,
    batchId: 'batch-1',
    fileId: 'file-1',
    seq: 1,
    offset: CHUNK,
    byteLength: 256,
    dataBase64: Buffer.from(source.subarray(CHUNK, CHUNK + 256)).toString('base64')
  })
  const retry = await feed(receiver, small)
  assert.equal(retry.result.ok, true, `窗口/序号未被拒绝污染：${retry.result.ok ? '' : retry.result.code}`)
  assert.equal(receiver.bufferedBytes, CHUNK + 256)
})

test('并发组装上限：超过配置的文件数被拒绝（接收端自己的第二道防线）', async () => {
  const source = sourceBytes(2 * CHUNK, 67)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles, maxConcurrentAssemblies: 1 })
  assert.equal(receiver.accounting.maxConcurrentAssemblies, 1)
  await startFile(receiver, { bytes: source, fileCount: 2, totalBytes: 2 * source.length })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(receiver.accounting.bufferedFiles, 1, '一次只组装一个文件')

  const second = await feed(receiver, fileBegin({ fileId: 'file-2', byteLength: source.length, sha256: sha256HexSync(source), name: 'second.bin' }))
  assert.equal(second.result.ok, false)
  assert.equal(second.result.code, 'file-in-progress')
  assert.match(second.result.detail, /并发组装上限 1/, '必须是接收端自己的并发上限守卫生效')
  assert.equal(receiver.bufferedBytes, CHUNK, '被拒绝的文件不得建立缓冲')
  assert.equal(receiver.accounting.bufferedFiles, 1)
  assert.ok(receiver.accounting.peakBufferedFiles <= 1)
})

test('接收缓冲峰值不超过 文件上限 × 并发上限（有界性记账）', async () => {
  const source = sourceBytes(CHUNK_BYTES + 1024, 71)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles, maxConcurrentAssemblies: 1 })
  await startFile(receiver, { bytes: source })
  const chunks = chunkMessages(source, { chunkBytes: CHUNK_BYTES })
  for (const chunk of chunks) await feed(receiver, chunk)

  const accounting = receiver.accounting
  assert.equal(accounting.bufferedBytes, source.length)
  assert.equal(accounting.peakBufferedBytes, source.length)
  assert.ok(
    accounting.peakBufferedBytes <= accounting.stagingByteLimit,
    `峰值 ${accounting.peakBufferedBytes} 必须 ≤ 目标暂存上限 ${accounting.stagingByteLimit}`
  )
  assert.ok(accounting.peakBufferedFiles <= accounting.maxConcurrentAssemblies)

  const end = await feed(receiver, fileEnd({ totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true)
  assert.equal(receiver.bufferedBytes, 0)
  assert.equal((await fileBytes(end.result.assembled.file)).length, source.length)
})

test('超时判定：ACK 停滞的批次被取消并释放缓冲', async () => {
  const clock = { now: 1000 }
  const source = sourceBytes(2 * CHUNK, 73)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles, now: () => clock.now })
  await startFile(receiver, { bytes: source })

  // 接受但**不投递** ack：该块一直"在途"，才能触发 ackMs 停滞。
  const pending = await accept(receiver, chunkMessages(source, { chunkBytes: CHUNK })[0])
  assert.equal(pending.ok, true)
  assert.equal(receiver.bufferedBytes, CHUNK)
  assert.deepEqual([...receiver.checkTimeouts()], [], '未超时时不得取消')

  clock.now += WIRE_TIMEOUTS.ackMs + 1
  assert.deepEqual([...receiver.checkTimeouts()], ['batch-1'])
  assert.equal(receiver.bufferedBytes, 0, '超时取消必须释放缓冲')
  assert.equal(receiver.accounting.releasedBytes, CHUNK)
  const late = await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK })[1])
  assert.equal(late.result.ok, false)
  assert.equal(late.result.code, 'cancelled')
})

test('保留批次有界：容量之外的旧批次被淘汰（活动批次被钉住）', async () => {
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles, maxRetainedBatches: 2 })
  const source = sourceBytes(CHUNK, 79)

  for (const batchId of ['batch-a', 'batch-b', 'batch-c']) {
    await startFile(receiver, { bytes: source, batchId, fileId: `file-${batchId}` })
    await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId: `file-${batchId}` })[0])
    const end = await feed(receiver, fileEnd({ batchId, fileId: `file-${batchId}`, totalBytes: source.length, sha256: sha256HexSync(source) }))
    assert.equal(end.result.ok, true)
    const ids = [...end.result.import.message.attachmentIds]
    assert.equal(ids.length, 1, '每次导入恰好新增 1 个 ID')
    const closed = await feed(receiver, batchEndMessage(batchId, [{ fileId: `file-${batchId}`, status: 'staged', attachmentIds: ids }]))
    assert.equal(closed.result.ok, true)
  }

  assert.equal(calls.length, 3)
  assert.ok(receiver.accounting.retainedBatches <= 2, '保留批次必须有界')
  assert.equal(receiver.bufferedBytes, 0)

  // 被淘汰的最旧批次：迟到消息不再有"已关闭"记忆，判 batch-not-open（有界的代价）。
  const evicted = await feed(receiver, fileEnd({ batchId: 'batch-a', fileId: 'file-batch-a', totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(evicted.result.ok, false)
  assert.equal(evicted.result.code, 'batch-not-open', '最旧批次已被淘汰（容量有界的代价）')
  // 最新批次仍在保留集内，迟到消息仍是精确的 batch-closed。
  const retained = await feed(receiver, fileEnd({ batchId: 'batch-c', fileId: 'file-batch-c', totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(retained.result.ok, false)
  assert.equal(retained.result.code, 'batch-closed')
})
