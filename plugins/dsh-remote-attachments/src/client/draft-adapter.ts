/**
 * 版本化 composer 适配器（D08）。
 *
 * 目标：把已经组装好的 `File` 导入**当前可编辑主会话**的原生草稿，并且只在
 * 能证明"新增附件确实属于本次操作"时才报告成功。
 *
 * 接入方式（只用公开契约，见 @deepseek-ai/dsh-client-ui-conversation 的
 * `contract/input.ts` 与 `service.ts`）：
 *   - 会话输入：`ctx.conversation.input.for(scope)` → `SessionInput`
 *   - 状态读取：`input.state.getSnapshot()` → `InputState`（含 `attachmentIds`/`phase`）
 *   - 卡住原因：`ctx.conversation.blocks.storeFor(sessionId)` → `ComposerBlock | undefined`
 *   - DOM 入口：`[data-composer-card]` 内唯一的 `input[type=file][multiple]`
 *
 * 明确不做的事（方案 4.4 节硬约束）：不抢占 `conversation.input.attachments`
 * single slot、不读 React/Lexical 私有字段、不调用不存在的公开 `createDrafts`、
 * 不向 document 广播合成 drop、不在失败后自动重发 change。
 *
 * 关于 ACK：`change` 事件本身不是确认。真正的确认是**同一同步任务内**读到的
 * `attachmentIds` 差值——旧 ID 必须完整保留，新增 ID 数必须等于本次导入的文件数
 * 且顺序一致。返回 `dispatchEvent` 的布尔值、或检查卡片外观，都不算 ACK。
 */

/** 导入失败码。 */
export type DraftImportFailureCode =
  | 'no-session'
  | 'no-composer-card'
  | 'no-file-input'
  | 'file-input-disabled'
  | 'input-state-unchanged'
  | 'busy-phase'
  | 'composer-blocked'
  | 'attachment-count-mismatch'
  | 'attachment-order-mismatch'
  | 'existing-attachments-lost'
  | 'context-changed'
  // D13：能力层面的确定性失败（不是"这次导入没成功"，而是"附件能力当前不该被使用"）。
  | 'capability-conflict'
  | 'capability-disabled'
  | 'unsupported'

/** 导入结果。 */
export type DraftImportResult =
  | {
      ok: true
      /** 本次导入新增的附件 ID，顺序与传入文件一致。 */
      added: readonly string[]
      /** 导入前的旧 ID（必须被完整保留）。 */
      previous: readonly string[]
    }
  | {
      ok: false
      code: DraftImportFailureCode
      detail: string
      /** 已确认保留的旧 ID（部分失败时仍应保留原生结果）。 */
      previous: readonly string[]
    }

/** 适配器依赖的最小外部面（便于用假对象单测，不需要 DOM）。 */
export interface DraftAdapterDeps {
  /** 读取当前输入状态快照。 */
  readState(): { attachmentIds: readonly string[]; phase: string }
  /** 读取当前会话的 composer 阻塞原因（undefined = 未阻塞）。 */
  readBlock(): { reason: string } | undefined
  /** 定位所属 composer 卡片。 */
  locateCard(): unknown | null
  /** 在卡片内定位唯一的文件 input。 */
  locateFileInput(card: unknown): unknown | null
  /** 该 input 是否处于禁用态。 */
  isDisabled(input: unknown): boolean
  /** 用 DataTransfer 写入 files。 */
  setFiles(input: unknown, files: readonly unknown[]): void
  /** 只触发一次 change。 */
  dispatchChange(input: unknown): void
}

/** 单次导入调用的上下文身份，用于确认"同一操作"。 */
export interface DraftImportContext {
  sessionId: string
  /** composer/文档代数；调用前后必须一致。 */
  epoch: string
}

/**
 * 执行一次草稿导入并做同步 ACK。
 *
 * @param deps 外部依赖（DOM 与公开状态读取）
 * @param context 本次操作的身份
 * @param files 已组装好的 File 列表（数量即期望的新增附件数）
 */
export function importDraftFiles(
  deps: DraftAdapterDeps,
  context: DraftImportContext,
  files: readonly unknown[]
): DraftImportResult {
  const before = deps.readState()
  const previous = [...before.attachmentIds]

  if (files.length === 0) {
    return { ok: false, code: 'unsupported', detail: '没有可导入的文件', previous }
  }

  // 编辑许可：phase 必须是 plain；claimed/submitting/adjudicating 都拒绝。
  // 注意 hidden input 的 disabled 只表达"子代理"，不足以表达完整 gate。
  if (before.phase !== 'plain') {
    return {
      ok: false,
      code: 'busy-phase',
      detail: `输入机处于 ${before.phase}，拒绝导入`,
      previous
    }
  }

  const block = deps.readBlock()
  if (block !== undefined) {
    return { ok: false, code: 'composer-blocked', detail: block.reason, previous }
  }

  const card = deps.locateCard()
  if (card === null) {
    return { ok: false, code: 'no-composer-card', detail: '未找到所属 composer 卡片', previous }
  }

  const input = deps.locateFileInput(card)
  if (input === null) {
    return { ok: false, code: 'no-file-input', detail: '卡片内没有唯一的文件 input', previous }
  }
  if (deps.isDisabled(input)) {
    return { ok: false, code: 'file-input-disabled', detail: '文件 input 处于禁用态', previous }
  }

  deps.setFiles(input, files)
  deps.dispatchChange(input)

  // 同步 ACK：必须在同一个同步任务里比较，避免把无关的异步新增算进本批。
  const after = deps.readState()
  const afterIds = [...after.attachmentIds]

  if (after.phase !== before.phase) {
    return {
      ok: false,
      code: 'context-changed',
      detail: `导入期间 phase 由 ${before.phase} 变为 ${after.phase}`,
      previous
    }
  }

  if (afterIds.length < previous.length) {
    return {
      ok: false,
      code: 'existing-attachments-lost',
      detail: `旧附件被丢弃：${previous.length} → ${afterIds.length}`,
      previous
    }
  }
  for (let index = 0; index < previous.length; index++) {
    if (afterIds[index] !== previous[index]) {
      return {
        ok: false,
        code: 'existing-attachments-lost',
        detail: `旧附件 ID 在第 ${index} 位发生变化`,
        previous
      }
    }
  }

  const added = afterIds.slice(previous.length)
  if (added.length === 0) {
    return {
      ok: false,
      code: 'input-state-unchanged',
      detail: '触发 change 后未观察到新增附件（不重放）',
      previous
    }
  }
  if (added.length !== files.length) {
    return {
      ok: false,
      code: 'attachment-count-mismatch',
      detail: `期望新增 ${files.length} 项，实际 ${added.length} 项（可能是校验拒绝或部分成功）`,
      previous
    }
  }

  return { ok: true, added, previous }
}

/** 由 `ctx.conversation.input.for(scope)` 得到的 facade 形状（只用公开成员）。 */
export interface SessionInputFacade {
  readonly state: { getSnapshot(): { attachmentIds: readonly string[]; phase: string } }
}

/** 由 `ctx.conversation.blocks.storeFor(sessionId)` 得到的 store 形状。 */
export interface ComposerBlockStore {
  getSnapshot(): { reason: string } | undefined
}

/**
 * 从公开的 cordis 服务构造适配器依赖。
 *
 * 这里刻意只读 `state` / `blocks` / DOM，不触碰任何 React 或 Lexical 内部结构。
 */
export function createConversationDeps(options: {
  sessionInput: SessionInputFacade
  blockStore: ComposerBlockStore
  document: Document
  anchor: Element
}): DraftAdapterDeps {
  const { sessionInput, blockStore, document: doc, anchor } = options

  const locateCard = (): Element | null => {
    // 从锚点向上找所属卡片：overlay 是非排他的，锚点必须在自己的卡片内。
    const closest = anchor.closest('[data-composer-card]')
    return closest ?? null
  }

  return {
    readState: () => {
      const snapshot = sessionInput.state.getSnapshot()
      return { attachmentIds: snapshot.attachmentIds, phase: snapshot.phase }
    },
    readBlock: () => blockStore.getSnapshot(),
    locateCard: () => locateCard(),
    locateFileInput: (card: unknown) => {
      const inputs = (card as Element).querySelectorAll('input[type=file]')
      // 必须唯一：多于一个说明归属不确定，宁可失败也不猜。
      return inputs.length === 1 ? inputs[0] ?? null : null
    },
    isDisabled: (input: unknown) => (input as HTMLInputElement).disabled === true,
    setFiles: (input: unknown, files: readonly unknown[]) => {
      const transfer = new DataTransfer()
      for (const file of files) transfer.items.add(file as File)
      // 只设置 files，不改动其它属性；原 onChange 链路自行读取。
      ;(input as HTMLInputElement).files = transfer.files
    },
    dispatchChange: (input: unknown) => {
      // 只触发一次 change，且不冒泡到 document（不广播合成 drop）。
      ;(input as HTMLInputElement).dispatchEvent(new Event('change', { bubbles: true }))
    }
  }
}
