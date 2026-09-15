/**
 * D12 接收端协议级测试（导入**构建产物** `lib/`，与既有单测同款）。
 *
 * 覆盖：分块组装的字节正确性、seq/offset/累计大小违规、短块与超大块、哈希不符、
 * 重放幂等、跨文件污染、批次关闭后的迟到拒绝、同会话新批次免 context 准入，
 * 以及没有 Web Crypto 时的完整性校验。
 *
 * 这些测试**不**代替真实 Chromium 判据（tests/fixtures/d12-receiver-gates.mjs）：
 * 这里用 Node 的 File/Blob，那里用真实浏览器 File/Blob。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import {
  CHUNK_BYTES,
  encodeMessage,
  decodeMessage,
  sha256HexSync
} from '../../lib/shared/wire/index.js'
import {
  AttachmentReceiver,
  RECEIVER_GLOBAL,
  RECEIVER_VERSION,
  createAttachmentReceiver,
  createReceiverHost
} from '../../lib/client/index.js'
import {
  COMPOSER_EPOCH,
  DOCUMENT_EPOCH,
  SESSION_ID,
  accept,
  batchBegin,
  batchEndMessage,
  cancelMessage,
  chunkMessage,
  chunkMessages,
  contextMessage,
  feed,
  fileBegin,
  fileBytes,
  fileEnd,
  importStub,
  mutate,
  rawText,
  rechunk,
  sourceBytes
} from './receiver-fixtures.mjs'

const FILE_BYTES = 9000
const CHUNK = 4096

/** 起手：绑定身份 + 开批 + 声明文件。 */
async function startFile(receiver, { bytes, fileId = 'file-1', batchId = 'batch-1', name, mime, fileCount = 1, totalBytes = bytes.length }) {
  const context = await feed(receiver, contextMessage())
  assert.equal(context.result.ok, true, 'context 必须被接受')
  const batch = await feed(receiver, batchBegin({ batchId, fileCount, totalBytes }))
  assert.equal(batch.result.ok, true, `batch-begin 必须被接受：${batch.result.ok ? '' : batch.result.code}`)
  const begin = await feed(receiver, fileBegin({ batchId, fileId, byteLength: bytes.length, sha256: sha256HexSync(bytes), ...(name === undefined ? {} : { name }), ...(mime === undefined ? {} : { mime }) }))
  assert.equal(begin.result.ok, true, `file-begin 必须被接受：${begin.result.ok ? '' : begin.result.code}`)
  return { batchId, fileId }
}

test('分块组装成真实 File：名称/类型/字节/哈希与源一致，导入恰好一次，三态不合并', async () => {
  const source = sourceBytes(FILE_BYTES)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source, name: 'd12-multi.bin', mime: 'application/octet-stream' })

  const chunks = chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })
  assert.ok(chunks.length >= 3, '本用例必须真的多块')
  let expectedBuffered = 0
  for (const chunk of chunks) {
    const { result, outgoing } = await feed(receiver, chunk)
    assert.equal(result.ok, true, `块 seq=${chunk.seq} 必须被接受：${result.ok ? '' : result.code}`)
    expectedBuffered += chunk.byteLength
    assert.equal(result.bufferedBytes, expectedBuffered, '接收缓冲记账必须等于已接受的字节数')
    assert.equal(outgoing.length, 1, '每接受一块应产出一条 ack')
    const ack = outgoing[0]
    assert.equal(ack.type, 'ack')
    assert.equal(ack.seq, chunk.seq)
    assert.equal(ack.offset, chunk.offset)
    assert.equal(ack.byteLength, chunk.byteLength)
    assert.equal(ack.bufferedBytes, expectedBuffered, 'ack.bufferedBytes 只反映接收缓冲')
    assert.equal(ack.inFlight, 0, '单块在途、投递后窗口为空')
    // 产出的 ack 必须是线协议合法报文（用生产 codec 复核）。
    const recheck = decodeMessage(encodeMessage(ack))
    assert.equal(recheck.ok, true, `ack 必须线协议合法：${recheck.ok ? '' : recheck.code}`)
    // ack 之后草稿仍未 staged：ack ≠ staged ≠ upload-ready。
    const record = receiver.fileRecord(fileId)
    assert.equal(record.draft, 'none')
    assert.equal(record.upload, 'none')
  }

  const hash = sha256HexSync(source)
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: hash }))
  assert.equal(end.result.ok, true, `file-end 必须被接受：${end.result.ok ? '' : end.result.code}`)
  assert.equal(end.result.importInvoked, true, '校验干净必须触发一次导入')

  const assembled = end.result.assembled
  assert.ok(assembled !== null, '必须构造出 File')
  assert.ok(assembled.file instanceof File, '必须是真实 File')
  assert.equal(assembled.file.name, 'd12-multi.bin')
  assert.equal(assembled.file.type, 'application/octet-stream')
  assert.equal(assembled.file.size, source.length)
  const readBack = await fileBytes(assembled.file)
  assert.deepEqual([...readBack], [...source], 'File 的字节必须与源逐字节相同')
  assert.equal(sha256HexSync(readBack), hash, '读回的字节哈希必须等于源哈希')

  assert.equal(calls.length, 1, '每个 file-end 恰好一次导入')
  assert.equal(calls[0].sessionId, SESSION_ID)
  assert.equal(calls[0].batchId, batchId)
  assert.equal(calls[0].fileId, fileId)
  assert.equal(calls[0].files.length, 1)
  assert.equal(calls[0].files[0], assembled.file, '交给草稿路径的必须是同一个 File 对象')

  // 导入结果被翻译成 import-result 并发出（草稿状态只由它改变）。
  assert.equal(end.outgoing.length, 1)
  assert.equal(end.outgoing[0].type, 'import-result')
  assert.equal(end.outgoing[0].status, 'staged')
  assert.deepEqual([...end.outgoing[0].attachmentIds], ['att-1'])

  const record = receiver.fileRecord(fileId)
  assert.equal(record.transport, 'buffered')
  assert.equal(record.draft, 'staged')
  assert.equal(record.upload, 'harness-owned')

  // 内存记账：File 构造后接收缓冲必须归零（字节归 File 所有）。
  const accounting = receiver.accounting
  assert.equal(accounting.bufferedBytes, 0)
  assert.equal(accounting.bufferedFiles, 0)
  assert.equal(accounting.peakBufferedBytes, source.length)
  assert.equal(accounting.filesConstructed, 1)
  assert.equal(accounting.importsInvoked, 1)
  assert.equal(accounting.importsSucceeded, 1)
})

test('seq/offset/累计大小违规：gap、overlap、越界一律拒绝且不写入接收缓冲', async () => {
  const source = sourceBytes(FILE_BYTES)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })

  const chunks = chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })
  const first = chunks[0]
  const second = chunks[1]
  assert.ok(first !== undefined && second !== undefined)

  const accepted = await feed(receiver, first)
  assert.equal(accepted.result.ok, true)
  assert.equal(accepted.result.bufferedBytes, CHUNK)

  // 跳号：seq 前进超过期望。
  const gap = await feed(receiver, rechunk(chunks[2], { seq: 2, offset: 2 * CHUNK }))
  assert.equal(gap.result.ok, false)
  assert.equal(gap.result.code, 'sequence-gap')
  assert.equal(gap.result.bufferedBytes, CHUNK, '被拒绝的块不得进入接收缓冲')

  // 回退：重复块（同 seq/offset）是 seq-overlap，绝不是重新入缓冲。
  const repeat = await feed(receiver, first)
  assert.equal(repeat.result.ok, false)
  assert.equal(repeat.result.code, 'seq-overlap')
  assert.equal(repeat.result.bufferedBytes, CHUNK)

  // offset 前进超过期望（seq 正确）。
  const offsetGap = await feed(receiver, rechunk(second, { seq: 1, offset: 3 * CHUNK }))
  assert.equal(offsetGap.result.ok, false)
  assert.equal(offsetGap.result.code, 'sequence-gap')
  assert.equal(offsetGap.result.bufferedBytes, CHUNK)

  // offset 回退（seq 正确）。
  const offsetBack = await feed(receiver, rechunk(second, { seq: 1, offset: 0 }))
  assert.equal(offsetBack.result.ok, false)
  assert.equal(offsetBack.result.code, 'seq-overlap')
  assert.equal(offsetBack.result.bufferedBytes, CHUNK)

  // 累计大小越界：已收 CHUNK 后，再来一块会超出 file-begin 声明的总长度。
  const overrun = await feed(receiver, chunkMessage(sourceBytes(5000, 99), { seq: 1, offset: CHUNK, batchId, fileId }))
  assert.equal(overrun.result.ok, false)
  assert.equal(overrun.result.code, 'size-mismatch')
  assert.equal(overrun.result.bufferedBytes, CHUNK, '越界块必须在写入缓冲之前被拒绝')

  // 正确的第二块仍然可以继续（拒绝没有破坏状态）。
  const secondOk = await feed(receiver, second)
  assert.equal(secondOk.result.ok, true)
  assert.equal(secondOk.result.bufferedBytes, 2 * CHUNK)

  const third = chunks[2]
  assert.ok(third !== undefined)
  const thirdOk = await feed(receiver, third)
  assert.equal(thirdOk.result.ok, true)
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true, `校验干净必须成功：${end.result.ok ? '' : end.result.code}`)
  assert.equal(calls.length, 1)
})

test('在途窗口：同一文件连续三块未投递 ack 时第三块是 window-overflow', async () => {
  const source = sourceBytes(4 * CHUNK)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })
  const chunks = chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })

  assert.equal((await accept(receiver, chunks[0])).ok, true)
  assert.equal((await accept(receiver, chunks[1])).ok, true)
  const third = await accept(receiver, chunks[2])
  assert.equal(third.ok, false)
  assert.equal(third.code, 'window-overflow', '未投递 ack 时不得有第三块在途')
  assert.equal(receiver.bufferedBytes, 2 * CHUNK, '被拒绝的第三块不得进入接收缓冲')

  // 投递 ack 释放窗口后，同一块可以继续。
  assert.equal((await receiver.drainOutgoing()).length, 2)
  const retry = await accept(receiver, chunks[2])
  assert.equal(retry.ok, true, `窗口释放后必须接受：${retry.ok ? '' : retry.code}`)
  await receiver.drainOutgoing()
})

test('短块与超大块：codec 层拒绝且不触碰接收缓冲；256 KiB 边界可接受', async () => {
  const source = sourceBytes(CHUNK_BYTES + 10)
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })

  // 声明 4 字节、载荷 3 字节：codec 判 size-mismatch（字段级，早于任何缓冲）。
  const short = await feed(receiver, rawText(chunkMessage(source.subarray(0, 3), { seq: 0, offset: 0, batchId, fileId }), { byteLength: 4 }))
  assert.equal(short.result.ok, false)
  assert.equal(short.result.stage, 'decode')
  assert.equal(short.result.code, 'size-mismatch')
  assert.equal(receiver.bufferedBytes, 0)

  // 超过块上限：codec 判 integer-out-of-range（字段级，早于任何缓冲）。
  const oversized = await feed(
    receiver,
    rawText(chunkMessage(source.subarray(0, 4), { seq: 0, offset: 0, batchId, fileId }), { byteLength: CHUNK_BYTES + 1 })
  )
  assert.equal(oversized.result.ok, false)
  assert.equal(oversized.result.code, 'integer-out-of-range')
  assert.equal(receiver.bufferedBytes, 0)

  // 边界值：正好一个 256 KiB 块必须被接受。
  const boundary = await feed(receiver, chunkMessage(source.subarray(0, CHUNK_BYTES), { seq: 0, offset: 0, batchId, fileId }))
  assert.equal(boundary.result.ok, true, `256 KiB 边界块必须被接受：${boundary.result.ok ? '' : boundary.result.code}`)
  const tail = await feed(receiver, chunkMessage(source.subarray(CHUNK_BYTES), { seq: 1, offset: CHUNK_BYTES, batchId, fileId }))
  assert.equal(tail.result.ok, true)
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true)
  assert.equal(end.result.assembled.size, source.length)
})

test('哈希不符：坏哈希不得构造 File、不得导入；修正后的 file-end 仍可完成', async () => {
  const source = sourceBytes(2 * CHUNK)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })

  for (const chunk of chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })) await feed(receiver, chunk)

  const badHash = 'f'.repeat(64)
  assert.notEqual(badHash, sha256HexSync(source))
  const bad = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: badHash }))
  assert.equal(bad.result.ok, false)
  assert.equal(bad.result.code, 'hash-mismatch')
  assert.equal(Object.hasOwn(bad.result, 'assembled'), false, '被拒绝的 file-end 不得给出 File')
  assert.equal(receiver.accounting.filesConstructed, 0, '坏哈希不得构造 File')
  assert.equal(receiver.accounting.importsInvoked, 0, '坏哈希不得触发导入')
  assert.equal(calls.length, 0)
  assert.equal(receiver.bufferedBytes, source.length, '未结束的文件其缓冲必须保留，等待修正后的 file-end')

  // 同一接收端上用正确哈希重发 file-end：必须成功（证明坏哈希路径不是"把状态搞坏"）。
  const good = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(good.result.ok, true, `修正后必须成功：${good.result.ok ? '' : good.result.code}`)
  assert.equal(good.result.importInvoked, true)
  assert.equal(calls.length, 1)
  assert.deepEqual([...(await fileBytes(good.result.assembled.file))], [...source])
})

test('file-begin 声明的哈希与 file-end 不符也在计算前拒绝', async () => {
  const source = sourceBytes(2 * CHUNK)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })
  for (const chunk of chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })) await feed(receiver, chunk)

  const declaredBad = await feed(
    receiver,
    fileEnd({ batchId, fileId, totalBytes: source.length, sha256: 'a'.repeat(64) })
  )
  assert.equal(declaredBad.result.ok, false)
  assert.equal(declaredBad.result.code, 'hash-mismatch')
  // file-begin 里声明的哈希是源哈希，这里 file-end 给的是另一个值 → 声明不符，直接拒绝。
  assert.equal(calls.length, 0)
  assert.equal(receiver.bufferedBytes, source.length)
})

test('重放幂等：重复 chunk/file-end/batch-end 不产生第二个 File，也不第二次导入', async () => {
  const source = sourceBytes(2 * CHUNK)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })
  const chunks = chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })

  await feed(receiver, chunks[0])
  const repeatChunk = await feed(receiver, chunks[0])
  assert.equal(repeatChunk.result.ok, false)
  assert.equal(repeatChunk.result.code, 'seq-overlap')

  await feed(receiver, chunks[1])
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true)
  assert.equal(calls.length, 1)

  const repeatEnd = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(repeatEnd.result.ok, true)
  assert.equal(repeatEnd.result.duplicate, true, '重复 file-end 是幂等重放')
  assert.equal(repeatEnd.result.importInvoked, false, '重复 file-end 不得再次导入')
  assert.equal(repeatEnd.result.assembled, null, '重复 file-end 不得构造第二个 File')
  assert.equal(calls.length, 1)
  assert.equal(receiver.accounting.filesConstructed, 1)

  // 冲突的重复 file-end（长度不同）必须被拒绝而不是当成重放。
  const conflicting = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: 'b'.repeat(64) }))
  assert.equal(conflicting.result.ok, false)
  assert.equal(conflicting.result.code, 'duplicate-file-end')

  const closed = await feed(receiver, batchEndMessage(batchId, [{ fileId, status: 'staged', attachmentIds: ['att-1'] }]))
  assert.equal(closed.result.ok, true, `batch-end 必须被接受：${closed.result.ok ? '' : closed.result.code}`)
  const repeatClosed = await feed(receiver, batchEndMessage(batchId, [{ fileId, status: 'staged', attachmentIds: ['att-1'] }]))
  assert.equal(repeatClosed.result.ok, true)
  assert.equal(repeatClosed.result.duplicate, true)

  // 批次关闭后的迟到消息一律不得重建操作。
  const lateChunk = await feed(receiver, chunks[0])
  assert.equal(lateChunk.result.ok, false)
  assert.equal(lateChunk.result.code, 'batch-closed')
  const lateEnd = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(lateEnd.result.ok, false)
  assert.equal(lateEnd.result.code, 'batch-closed')
  assert.equal(Object.hasOwn(lateEnd.result, 'assembled'), false)
  assert.equal(receiver.accounting.filesConstructed, 1)
  assert.equal(calls.length, 1)
  assert.equal(receiver.bufferedBytes, 0)
})

test('跨文件污染：给 B 的块绝不落进 A 的接收缓冲', async () => {
  const sourceA = sourceBytes(2 * CHUNK, 11)
  const sourceB = sourceBytes(2 * CHUNK, 29)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })

  await feed(receiver, contextMessage())
  await feed(receiver, batchBegin({ fileCount: 2, totalBytes: sourceA.length + sourceB.length }))
  await feed(receiver, fileBegin({ fileId: 'file-a', byteLength: sourceA.length, sha256: sha256HexSync(sourceA), name: 'a.bin' }))

  const chunkA = chunkMessages(sourceA, { chunkBytes: CHUNK, fileId: 'file-a' })
  assert.equal((await feed(receiver, chunkA[0])).result.bufferedBytes, CHUNK)

  // 活动文件是 A：声明 B 会被拒，且不得给 B 建立任何缓冲。
  const beginB = await feed(receiver, fileBegin({ fileId: 'file-b', byteLength: sourceB.length, sha256: sha256HexSync(sourceB), name: 'b.bin' }))
  assert.equal(beginB.result.ok, false)
  assert.equal(beginB.result.code, 'file-in-progress')
  assert.equal(receiver.bufferedBytes, CHUNK)

  // 直接给 B 发块：必须在进入缓冲前被拒（file-id-mismatch），A 的记账不变。
  const strayB = await feed(receiver, chunkMessages(sourceB, { chunkBytes: CHUNK, fileId: 'file-b' })[0])
  assert.equal(strayB.result.ok, false)
  assert.equal(strayB.result.code, 'file-id-mismatch')
  assert.equal(receiver.bufferedBytes, CHUNK, 'B 的块不得落进 A 的缓冲')

  // A 正常完成：内容必须还是 A 的。
  await feed(receiver, chunkA[1])
  const endA = await feed(receiver, fileEnd({ fileId: 'file-a', totalBytes: sourceA.length, sha256: sha256HexSync(sourceA) }))
  assert.equal(endA.result.ok, true, `A 必须完成：${endA.result.ok ? '' : endA.result.code}`)
  assert.deepEqual([...(await fileBytes(endA.result.assembled.file))], [...sourceA])

  // 然后 B 独立完成：内容必须是 B 的（不是 A 的，也不是拼接）。
  await feed(receiver, fileBegin({ fileId: 'file-b', byteLength: sourceB.length, sha256: sha256HexSync(sourceB), name: 'b.bin' }))
  for (const chunk of chunkMessages(sourceB, { chunkBytes: CHUNK, fileId: 'file-b' })) await feed(receiver, chunk)
  const endB = await feed(receiver, fileEnd({ fileId: 'file-b', totalBytes: sourceB.length, sha256: sha256HexSync(sourceB) }))
  assert.equal(endB.result.ok, true, `B 必须完成：${endB.result.ok ? '' : endB.result.code}`)
  assert.deepEqual([...(await fileBytes(endB.result.assembled.file))], [...sourceB])
  assert.equal(calls.length, 2)
  assert.notEqual(sha256HexSync(sourceA), sha256HexSync(sourceB))
})

test('同会话新批次：新 batchId 免 context 被 batch-begin 承认；旧 batchId 与迟到消息不得重建操作', async () => {
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })

  // 批次 1
  const sourceA = sourceBytes(2 * CHUNK, 3)
  await startFile(receiver, { bytes: sourceA, batchId: 'batch-1', fileId: 'file-a' })
  for (const chunk of chunkMessages(sourceA, { chunkBytes: CHUNK, fileId: 'file-a' })) await feed(receiver, chunk)
  const endA = await feed(receiver, fileEnd({ batchId: 'batch-1', fileId: 'file-a', totalBytes: sourceA.length, sha256: sha256HexSync(sourceA) }))
  assert.equal(endA.result.ok, true)
  const closeA = await feed(receiver, batchEndMessage('batch-1', [{ fileId: 'file-a', status: 'staged', attachmentIds: ['att-1'] }]))
  assert.equal(closeA.result.ok, true)

  const identityBefore = receiver.currentIdentity
  assert.equal(identityBefore.sessionId, SESSION_ID)
  assert.equal(identityBefore.composerEpoch, COMPOSER_EPOCH)

  // 关键：**不重发 context**，直接用新 batchId 开新批。
  const sourceB = sourceBytes(CHUNK, 5)
  const batch2 = await feed(receiver, batchBegin({ batchId: 'batch-2', fileCount: 1, totalBytes: sourceB.length }))
  assert.equal(batch2.result.ok, true, `同会话新批次必须被 batch-begin 承认：${batch2.result.ok ? '' : batch2.result.code}`)
  assert.equal(receiver.activeBatchId, 'batch-2')
  const beginB = await feed(receiver, fileBegin({ batchId: 'batch-2', fileId: 'file-b', byteLength: sourceB.length, sha256: sha256HexSync(sourceB), name: 'b2.bin' }))
  assert.equal(beginB.result.ok, true, `新批次里 file-begin 必须被接受：${beginB.result.ok ? '' : beginB.result.code}`)
  await feed(receiver, chunkMessages(sourceB, { chunkBytes: CHUNK, batchId: 'batch-2', fileId: 'file-b' })[0])
  const endB = await feed(receiver, fileEnd({ batchId: 'batch-2', fileId: 'file-b', totalBytes: sourceB.length, sha256: sha256HexSync(sourceB) }))
  assert.equal(endB.result.ok, true, `新批次必须能完成：${endB.result.ok ? '' : endB.result.code}`)
  assert.equal(endB.result.importInvoked, true)
  assert.equal(calls.length, 2, '两个批次各自导入一次')
  assert.deepEqual([...(await fileBytes(endB.result.assembled.file))], [...sourceB])
  // 身份在整个过程中没有被替换过。
  assert.equal(receiver.currentIdentity.sessionId, identityBefore.sessionId)
  assert.equal(receiver.currentIdentity.composerEpoch, identityBefore.composerEpoch)
  assert.equal(receiver.currentIdentity.expired, false)

  // 复用已关闭批次的 batchId：必须是 duplicate-operation，不得再开一批。
  const reuse = await feed(receiver, batchBegin({ batchId: 'batch-1', fileCount: 1, totalBytes: 0 }))
  assert.equal(reuse.result.ok, false)
  assert.equal(reuse.result.code, 'duplicate-operation')
  // 已关闭批次的迟到 file-end 不得重建操作。
  const lateLate = await feed(receiver, fileEnd({ batchId: 'batch-1', fileId: 'file-a', totalBytes: sourceA.length, sha256: sha256HexSync(sourceA) }))
  assert.equal(lateLate.result.ok, false)
  assert.equal(lateLate.result.code, 'batch-closed')
  assert.equal(calls.length, 2)

  // 对照：navigate() 之后身份过期，新 batch-begin 必须重新 context（判据不是恒真）。
  receiver.navigate()
  const afterNavigate = await feed(receiver, batchBegin({ batchId: 'batch-3', fileCount: 1, totalBytes: 0 }))
  assert.equal(afterNavigate.result.ok, false)
  assert.equal(afterNavigate.result.code, 'context-changed')
  const rebind = await feed(receiver, contextMessage())
  assert.equal(rebind.result.ok, true)
  const afterRebind = await feed(receiver, batchBegin({ batchId: 'batch-3', fileCount: 1, totalBytes: 0 }))
  assert.equal(afterRebind.result.ok, true, `重新 context 后必须能开新批：${afterRebind.result.ok ? '' : afterRebind.result.code}`)
})

test('没有 Web Crypto（HTTP 局域网）时照常组装与校验，坏哈希仍被拒绝', async () => {
  const source = sourceBytes(2 * CHUNK, 13)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles, crypto: {} })
  assert.equal(receiver.accounting.hashBackend, 'pure-js', '没有 subtle 时必须用纯 JS 增量实现')

  const { batchId, fileId } = await startFile(receiver, { bytes: source })
  for (const chunk of chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })) await feed(receiver, chunk)
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true, `无 Web Crypto 时必须能完成：${end.result.ok ? '' : end.result.code}`)
  assert.equal(end.result.importInvoked, true)
  assert.deepEqual([...(await fileBytes(end.result.assembled.file))], [...source])

  // 同一路径上坏哈希必须照样失败：完整性校验没有因为缺少 subtle 而被关掉。
  const other = sourceBytes(CHUNK, 17)
  const second = createAttachmentReceiver({ importFiles, crypto: {} })
  await startFile(second, { bytes: other, fileId: 'file-x' })
  await feed(second, chunkMessages(other, { chunkBytes: CHUNK, fileId: 'file-x' })[0])
  const bad = await feed(second, fileEnd({ fileId: 'file-x', totalBytes: other.length, sha256: '0'.repeat(64) }))
  assert.equal(bad.result.ok, false)
  assert.equal(bad.result.code, 'hash-mismatch')
  assert.equal(second.accounting.filesConstructed, 0)
  assert.equal(calls.length, 1, '只有第一个接收端导入过')
})

test('接收端公开面：类/工厂/页面宿主全局名与版本，且宿主把 File 交给注入的导入函数', async () => {
  assert.equal(RECEIVER_VERSION, 1)
  assert.equal(RECEIVER_GLOBAL, '__DSH_ATTACHMENTS_RECEIVER__')
  assert.equal(typeof AttachmentReceiver, 'function')

  const { importFiles, calls } = importStub()
  const host = createReceiverHost({ importFiles })
  assert.equal(host.version, 1)
  const receiver = host.create()
  const source = sourceBytes(CHUNK, 19)
  const { batchId, fileId } = await startFile(receiver, { bytes: source })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })[0])
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))
  assert.equal(end.result.ok, true)
  assert.equal(calls.length, 1)
  assert.ok(calls[0].files[0] instanceof File)

  // 传进来的 `limit-staging-bytes` 生效：宿主可以给接收端更小的目标暂存上限。
  const small = host.create({ limits: { maxStagingBytesPerTarget: 16 } })
  assert.equal(small.accounting.stagingByteLimit, 16)
})

test('报文文本入口：解码失败不改变任何状态（malformed-json / version-mismatch / 未知字段）', async () => {
  const { importFiles } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })
  await feed(receiver, contextMessage())
  await feed(receiver, batchBegin({ fileCount: 1, totalBytes: 10 }))
  await feed(receiver, fileBegin({ byteLength: 10, sha256: sha256HexSync(new Uint8Array(10)) }))

  const malformed = await feed(receiver, '{ not json')
  assert.equal(malformed.result.ok, false)
  assert.equal(malformed.result.stage, 'decode')
  assert.equal(malformed.result.code, 'malformed-json')

  const version = await feed(receiver, JSON.stringify({ ...mutate(batchBegin({ batchId: 'batch-9' }), {}), v: 2 }))
  assert.equal(version.result.ok, false)
  assert.equal(version.result.code, 'version-mismatch')

  const unknownField = await feed(receiver, JSON.stringify({ ...JSON.parse(JSON.stringify(batchBegin({ batchId: 'batch-9' }))), extra: 1 }))
  assert.equal(unknownField.result.ok, false)
  assert.equal(unknownField.result.code, 'unknown-field')

  // 状态未被这些失败改动：活动批次仍可继续。
  assert.equal(receiver.activeBatchId, 'batch-1')
  assert.equal(receiver.bufferedBytes, 0)
  assert.equal(receiver.accounting.rejectedMessages, 3)
})

test('导入失败（草稿拒绝）翻译成 import-result failed，且不谎报成功', async () => {
  const source = sourceBytes(CHUNK, 23)
  const { importFiles, calls } = importStub({ ok: false, code: 'busy-phase' })
  const receiver = createAttachmentReceiver({ importFiles })
  const { batchId, fileId } = await startFile(receiver, { bytes: source })
  await feed(receiver, chunkMessages(source, { chunkBytes: CHUNK, batchId, fileId })[0])
  const end = await feed(receiver, fileEnd({ batchId, fileId, totalBytes: source.length, sha256: sha256HexSync(source) }))

  assert.equal(end.result.ok, true, 'file-end 本身校验干净（完整性通过）')
  assert.equal(calls.length, 1)
  assert.equal(end.result.import.result.ok, false)
  assert.equal(end.result.import.message.status, 'failed')
  assert.equal(end.result.import.message.code, 'draft-import-failed')
  assert.deepEqual([...end.result.import.message.attachmentIds], [])
  assert.equal(end.result.import.applied, true)
  const record = receiver.fileRecord(fileId)
  assert.equal(record.draft, 'failed')
  assert.equal(record.upload, 'none', '失败不得进入 harness-owned')
  assert.equal(receiver.accounting.importsFailed, 1)
})

// R15 回归：cancel 之后同一会话必须能再开新批次并完成导入。
// 旧症状：cancelled 批次 closed 仍为 false，接收端与状态机都据此挡住新批次，
// batch-begin 被永久拒为 batch-in-progress，用户不刷新页面就再也贴不进来。
test('cancel 之后同一会话可以开新批次并完成导入，且不能复用被取消的 batchId', async () => {
  const source = sourceBytes(CHUNK)
  const { importFiles, calls } = importStub()
  const receiver = createAttachmentReceiver({ importFiles })

  const context = await feed(receiver, contextMessage())
  assert.equal(context.result.ok, true, 'context 必须被接受')

  // 第一批：开批后取消（不传完文件）。
  const first = await feed(receiver, batchBegin({ batchId: 'batch-r15-1', fileCount: 1, totalBytes: source.length }))
  assert.equal(first.result.ok, true, `第一批应被承认：${first.result.ok ? '' : first.result.code}`)
  const cancelled = await feed(receiver, cancelMessage('batch-r15-1'))
  assert.equal(cancelled.result.ok, true, 'cancel 必须被接受')

  // 取消后的新批次：必须被 batch-begin 承认（回归点）。
  const admitted = await feed(receiver, batchBegin({ batchId: 'batch-r15-2', fileCount: 1, totalBytes: source.length }))
  assert.equal(admitted.result.ok, true, `取消后新批次被拒：${admitted.result.ok ? '' : admitted.result.code}`)
  assert.notEqual(admitted.result.code, 'batch-in-progress')

  // 新批次必须真的能用：file-begin → 分块 → file-end 完成导入。
  const newFileId = 'file-r15-2'
  const begin = await feed(receiver, fileBegin({
    batchId: 'batch-r15-2', fileId: newFileId, byteLength: source.length, sha256: sha256HexSync(source)
  }))
  assert.equal(begin.result.ok, true, `新批次的 file-begin 必须被接受：${begin.result.ok ? '' : begin.result.code}`)
  for (const chunk of chunkMessages(source, { chunkBytes: CHUNK, batchId: 'batch-r15-2', fileId: newFileId })) {
    await feed(receiver, chunk)
  }
  const done = await feed(receiver, fileEnd({
    batchId: 'batch-r15-2', fileId: newFileId, totalBytes: source.length, sha256: sha256HexSync(source)
  }))
  assert.equal(done.result.ok, true, `取消后的新批次必须能完成导入：${done.result.ok ? '' : done.result.code}`)
  assert.equal(done.result.importInvoked, true)
  assert.equal(calls.length, 1, '取消的批次不得产生导入，新批次恰好一次')

  // 复用被取消的旧 batchId 仍必须被拒（不能借 cancel 重建已停止的操作）。
  const reused = await feed(receiver, batchBegin({ batchId: 'batch-r15-1', fileCount: 1, totalBytes: source.length }))
  assert.equal(reused.result.ok, false)
  assert.equal(reused.result.code, 'duplicate-operation')
})

