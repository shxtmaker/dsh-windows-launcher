/**
 * D08 单元测试：草稿导入的同步 ACK 判定与拒绝路径。
 *
 * 用假依赖覆盖判定分支；真实 DOM 行为在真实 Chromium 中验证
 * （tests/fixtures/draft-gates.mjs）。这里刻意不 mock 被测对象自身——
 * importDraftFiles 是生产实现，假的是它的外部依赖。
 */

import assert from 'node:assert/strict'
import test from 'node:test'

import { importDraftFiles } from '../../lib/client/draft-adapter.js'

/** 构造一个可控的假依赖。 */
function makeDeps({
  attachmentIds = [],
  phase = 'plain',
  block = undefined,
  card = {},
  input = {},
  disabled = false,
  afterImport = null,
  onChange = () => {}
} = {}) {
  const calls = { setFiles: 0, dispatchChange: 0 }
  let current = { attachmentIds: [...attachmentIds], phase }
  const deps = {
    readState: () => ({ attachmentIds: [...current.attachmentIds], phase: current.phase }),
    readBlock: () => block,
    locateCard: () => card,
    locateFileInput: () => input,
    isDisabled: () => disabled,
    setFiles: (_input, files) => {
      calls.setFiles++
      current = afterImport ? afterImport(current, files) : current
    },
    dispatchChange: () => {
      calls.dispatchChange++
      onChange()
    }
  }
  return { deps, calls, files: [{ name: 'a.txt' }, { name: 'b.png' }] }
}

test('成功路径：旧 ID 完整保留、新增数等于导入数、顺序一致', () => {
  const { deps, calls, files } = makeDeps({
    attachmentIds: ['old-1', 'old-2'],
    afterImport: (state) => ({ ...state, attachmentIds: [...state.attachmentIds, 'new-1', 'new-2'] })
  })
  const context = { sessionId: 's1', epoch: 'e1' }
  const result = importDraftFiles(deps, context, files)
  assert.equal(result.ok, true)
  assert.deepEqual(result.added, ['new-1', 'new-2'])
  assert.deepEqual(result.previous, ['old-1', 'old-2'])
  assert.equal(calls.setFiles, 1, '只设置一次 files')
  assert.equal(calls.dispatchChange, 1, '只触发一次 change')
})

test('phase 非 plain 时拒绝（hidden input 的 disabled 不足以表达该 gate）', () => {
  const { deps, calls, files } = makeDeps({ phase: 'submitting' })
  const result = importDraftFiles(deps, { sessionId: 's1', epoch: 'e1' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'busy-phase')
  assert.equal(calls.dispatchChange, 0, '拒绝时不得触发 change')
})

test('composer 被阻塞时拒绝并带上阻塞原因', () => {
  const { deps, calls, files } = makeDeps({ block: { reason: '子代理运行中' } })
  const result = importDraftFiles(deps, { sessionId: 's1', epoch: 'e1' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'composer-blocked')
  assert.equal(result.detail, '子代理运行中')
  assert.equal(calls.dispatchChange, 0)
})

test('找不到所属卡片或文件 input 时拒绝，不猜归属', () => {
  const noCard = makeDeps({ card: null })
  assert.equal(importDraftFiles(noCard.deps, { sessionId: 's', epoch: 'e' }, noCard.files).code, 'no-composer-card')
  const noInput = makeDeps({ input: null })
  assert.equal(importDraftFiles(noInput.deps, { sessionId: 's', epoch: 'e' }, noInput.files).code, 'no-file-input')
})

test('文件 input 禁用时拒绝', () => {
  const { deps, calls, files } = makeDeps({ disabled: true })
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'file-input-disabled')
  assert.equal(calls.dispatchChange, 0)
})

test('触发 change 后无新增即失败，不重放', () => {
  const { deps, calls, files } = makeDeps({ attachmentIds: ['old-1'] })
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'input-state-unchanged')
  assert.equal(calls.dispatchChange, 1, '不得因失败而重放 change')
})

test('新增数量与导入数量不符时报告确定失败（校验拒绝或部分成功）', () => {
  const { deps, files } = makeDeps({
    attachmentIds: ['old-1'],
    afterImport: (state) => ({ ...state, attachmentIds: [...state.attachmentIds, 'new-1'] })
  })
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'attachment-count-mismatch')
  assert.match(result.detail, /期望新增 2 项，实际 1 项/)
})

test('旧附件被丢弃时报告 existing-attachments-lost', () => {
  const { deps, files } = makeDeps({
    attachmentIds: ['old-1', 'old-2'],
    afterImport: (state, imported) => ({ ...state, attachmentIds: [...imported.map((_, i) => `new-${i}`)] })
  })
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'existing-attachments-lost')
})

test('导入期间 phase 变化视为上下文漂移', () => {
  const { deps, files } = makeDeps({
    afterImport: (state) => ({ ...state, phase: 'submitting', attachmentIds: [...state.attachmentIds, 'new-1', 'new-2'] })
  })
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, files)
  assert.equal(result.ok, false)
  assert.equal(result.code, 'context-changed')
})

test('空文件列表直接拒绝，不触碰 DOM', () => {
  const { deps, calls } = makeDeps({})
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, [])
  assert.equal(result.ok, false)
  assert.equal(result.code, 'unsupported')
  assert.equal(calls.setFiles, 0)
})

test('失败结果仍带上已确认保留的旧 ID（供部分失败上报）', () => {
  const { deps, files } = makeDeps({
    attachmentIds: ['old-1'],
    afterImport: (state) => ({ ...state, attachmentIds: [...state.attachmentIds, 'new-1'] })
  })
  const result = importDraftFiles(deps, { sessionId: 's', epoch: 'e' }, files)
  assert.equal(result.ok, false)
  assert.deepEqual(result.previous, ['old-1'])
})
