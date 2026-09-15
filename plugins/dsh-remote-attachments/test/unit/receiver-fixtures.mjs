/**
 * D12 接收端测试夹具：用**生产 codec** 造消息、用确定性字节造载荷。
 *
 * 这里刻意不手写协议 JSON：所有消息都经过 `decodeMessage`（生产 codec）校验，
 * 因此测试喂给接收端的一定是合法报文；非法报文由 `mutate()` 显式破坏后再喂。
 */

import assert from 'node:assert/strict'

import { base64Encode, decodeMessage, planChunks, sha256HexSync } from '../../lib/shared/wire/index.js'

export const SESSION_ID = 'session-d12'
export const TARGET_ID = 'target-d12'
export const DOCUMENT_EPOCH = 11
export const COMPOSER_EPOCH = 22
export const COMPOSER_SCOPE = 'composer-scope-d12'

/** 解码并断言合法（生产 codec）。 */
export function decodeStrict(raw) {
  const decoded = decodeMessage(typeof raw === 'string' ? raw : JSON.stringify(raw))
  assert.equal(
    decoded.ok,
    true,
    `消息必须能被生产 codec 解码：${decoded.ok ? '' : `${decoded.code} ${decoded.detail} @${decoded.path}`}`
  )
  return decoded.message
}

/** 身份上下文。 */
export function contextMessage(overrides = {}) {
  return decodeStrict({
    v: 1,
    type: 'context',
    sessionId: SESSION_ID,
    targetId: TARGET_ID,
    documentEpoch: DOCUMENT_EPOCH,
    composerEpoch: COMPOSER_EPOCH,
    composerScope: COMPOSER_SCOPE,
    ...overrides
  })
}

export function batchBegin(overrides = {}) {
  return decodeStrict({
    v: 1,
    type: 'batch-begin',
    sessionId: SESSION_ID,
    batchId: 'batch-1',
    targetId: TARGET_ID,
    documentEpoch: DOCUMENT_EPOCH,
    composerEpoch: COMPOSER_EPOCH,
    fileCount: 1,
    totalBytes: 0,
    ...overrides
  })
}

export function fileBegin(overrides = {}) {
  return decodeStrict({
    v: 1,
    type: 'file-begin',
    sessionId: SESSION_ID,
    batchId: 'batch-1',
    fileId: 'file-1',
    name: 'd12-payload.bin',
    byteLength: 0,
    mime: 'application/octet-stream',
    ...overrides
  })
}

export function chunkMessage(bytes, { seq, offset, batchId = 'batch-1', fileId = 'file-1', ...rest } = {}) {
  return decodeStrict({
    v: 1,
    type: 'chunk',
    sessionId: SESSION_ID,
    batchId,
    fileId,
    seq,
    offset,
    byteLength: bytes.length,
    dataBase64: base64Encode(bytes),
    ...rest
  })
}

export function fileEnd(overrides = {}) {
  return decodeStrict({
    v: 1,
    type: 'file-end',
    sessionId: SESSION_ID,
    batchId: 'batch-1',
    fileId: 'file-1',
    totalBytes: 0,
    sha256: sha256HexSync(new Uint8Array(0)),
    ...overrides
  })
}

export function batchEndMessage(batchId, results, status = 'staged') {
  return decodeStrict({ v: 1, type: 'batch-end', sessionId: SESSION_ID, batchId, status, results })
}

export function cancelMessage(batchId, overrides = {}) {
  return decodeStrict({
    v: 1,
    type: 'cancel',
    sessionId: SESSION_ID,
    batchId,
    reason: 'cancelled',
    stage: 'protocol-transfer',
    ...overrides
  })
}

/** 把合法报文改成 codec 仍接受、但语义错误的变体（返回**未解码**的原始对象）。 */
export function mutate(message, patch) {
  return { ...JSON.parse(JSON.stringify(message)), ...patch }
}

/** 基于一条合法报文改字段，并断言改动后仍能被生产 codec 接受。 */
export function rechunk(message, patch) {
  return decodeStrict(mutate(message, patch))
}

/** 基于一条合法报文改字段，保持**未解码**文本形态（用于让 codec 自己拒绝）。 */
export function rawText(message, patch = {}) {
  return JSON.stringify(mutate(message, patch))
}

/** 确定性伪随机字节（同种子同字节，便于跨运行比对）。 */
export function sourceBytes(size, seed = 7) {
  const bytes = new Uint8Array(size)
  let state = seed >>> 0
  for (let index = 0; index < size; index += 1) {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0
    bytes[index] = (state >>> 24) & 0xff
  }
  return bytes
}

/** 按固定块大小把一份字节切成合法 chunk 消息。 */
export function chunkMessages(bytes, { chunkBytes, batchId = 'batch-1', fileId = 'file-1' } = {}) {
  return planChunks(bytes.length, chunkBytes ?? 4096).map((plan) =>
    chunkMessage(bytes.subarray(plan.offset, plan.offset + plan.byteLength), {
      seq: plan.seq,
      offset: plan.offset,
      batchId,
      fileId
    })
  )
}

/** 假的草稿导入：记录每次调用，缺省每次新增 1 个 ID。 */
export function importStub({ ok = true, added = null, code = 'busy-phase' } = {}) {
  const calls = []
  const importFiles = (request) => {
    calls.push(request)
    if (!ok) return { ok: false, code, detail: 'stub 拒绝', previous: [] }
    const ids = added === null ? [`att-${calls.length}`] : [...added]
    return { ok: true, added: ids, previous: [] }
  }
  return { importFiles, calls }
}

/** 喂一条消息并立即取走待发消息（生产传输层的常规节奏）。 */
export async function feed(receiver, raw) {
  const result = typeof raw === 'string' ? await receiver.acceptText(raw) : await receiver.accept(raw)
  const outgoing = await receiver.drainOutgoing()
  return { result, outgoing }
}

/** 只接受、不投递（用于在途窗口判定）。 */
export function accept(receiver, raw) {
  return typeof raw === 'string' ? receiver.acceptText(raw) : receiver.accept(raw)
}

/** 读回真实 File 的字节。 */
export async function fileBytes(file) {
  return new Uint8Array(await file.arrayBuffer())
}
