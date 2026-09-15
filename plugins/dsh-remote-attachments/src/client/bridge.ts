/**
 * 版本化附件桥（D08）。
 *
 * 职责：把"外部调用方（真实宿主/原生层/UI 测试）想导入一组文件"这件事，翻译成对
 * `draft-adapter` 的一次调用，并把**原生输入状态**的 ACK 原样返回。
 *
 * 为什么需要桥：适配器刻意只依赖一个最小外部面（`DraftAdapterDeps`），真实的
 * cordis 服务、会话 scope 与 DOM 定位都在这里完成。这样适配器可以用假依赖单测，
 * 而"真实归属确认"只有一处实现。
 *
 * 归属确认（方案 4.4 节要求，不能只做选择器命中）：
 *   1. 目标会话必须能被 `ctx.sessions.scope(sessionId)` 解析出 scope，否则 `no-session`；
 *   2. 该会话必须是**当前会话**（`ctx.sessions.list` 的 `current`），否则 `context-changed`
 *      ——当前会话是唯一被渲染出 composer 的那个，这条把"可见卡片"钉到身份上；
 *   3. 文档里必须**恰好一个** `[data-composer-card]`；多于一个说明渲染了多份，归属不确定，
 *      宁可失败也不猜；
 *   4. 卡片内必须恰好一个 `input[type=file]`（由适配器的 `locateFileInput` 保证）。
 *
 * 本桥**同步**返回结果：ACK 必须在触发 change 的同一个同步任务内完成比较，异步化会让
 * 无关的迟到新增混进本批。
 */

import {
  createConversationDeps,
  importDraftFiles,
  type ComposerBlockStore,
  type DraftImportFailureCode,
  type DraftImportResult,
  type SessionInputFacade
} from './draft-adapter.js'
import { CAPABILITY_CODES, type CapabilityResolution } from '../shared/capabilities.js'

/** 桥的协议版本。调用方必须显式核对，避免静默假设。 */
export const BRIDGE_VERSION = 1

/** 桥暴露到页面全局的名字。 */
export const BRIDGE_GLOBAL = '__DSH_ATTACHMENTS_BRIDGE__'

/** 一次导入请求。`sessionId` 缺省表示"当前会话"。 */
export interface BridgeImportRequest {
  sessionId?: string
  files: readonly File[]
}

/** 会话 scope 解析结果（形状来自 `ctx.sessions`）。 */
export interface SessionsFacade {
  scope(sessionId: string): unknown
  list: { getSnapshot(): { current?: string | null } }
}

/** 桥的最小运行时依赖。 */
export interface BridgeRuntime {
  /**
   * 取 `ctx.sessions`（缺失即为不支持）。
   *
   * **必须是取值函数、不能是取值结果**：实测宿主在插件 `apply` 期间还没有该服务，
   * 缓存一次 `undefined` 会让桥永久失效（表现为会话已打开却报告 unsupported）。
   * cordis 的服务本就可能在 apply 之后才就绪，因此每次都重新解析。
   */
  getSessions(): SessionsFacade | undefined
  /** 在给定 scope 上取 `conversation` 服务。 */
  conversationOf(scope: unknown): unknown
  /** 页面 document（浏览器环境）。 */
  document: Document
  /**
   * D13：当前能力判定。返回非 available 时桥拒绝导入（附件能力停用/冲突），
   * 而不是悄悄换一条别的路径把文件塞进草稿。
   */
  capability?(): CapabilityResolution | null
}

/** 从会话 scope 上取到的 conversation 服务形状（只用公开成员）。 */
interface ConversationFacade {
  input: { for(scope: unknown): SessionInputFacade }
  blocks: { storeFor(sessionId: string): ComposerBlockStore }
}

/** 桥的公开面。 */
export interface AttachmentBridge {
  version: number
  /** 当前会话 ID（无会话时为 null）。 */
  currentSession(): string | null
  /** 执行一次导入并返回原生 ACK。 */
  importFiles(request: BridgeImportRequest): DraftImportResult
  /** D13：撤销（禁用/卸载）后桥拒绝一切导入，并保留最后一次能力读数供诊断。 */
  dispose(reason?: string): void
  readonly disposed: boolean
}

/** 判定 conversation 服务形状是否可用。 */
function asConversation(value: unknown): ConversationFacade | null {
  const candidate = value as Partial<ConversationFacade> | null | undefined
  if (candidate === null || candidate === undefined) return null
  if (typeof candidate.input?.for !== 'function') return null
  if (typeof candidate.blocks?.storeFor !== 'function') return null
  return candidate as ConversationFacade
}

/**
 * 构造桥。
 *
 * `runtime` 每次调用都会重新解析会话与 DOM，因此切换会话后不需要重建桥；这也让
 * "切会话时旧请求"自然落到 `context-changed`。
 */
export function createAttachmentBridge(runtime: BridgeRuntime): AttachmentBridge {
  let disposed = false
  let disposeReason: string | null = null

  const currentSession = (): string | null => {
    if (disposed) return null
    const current = runtime.getSessions()?.list?.getSnapshot?.().current
    return typeof current === 'string' && current.length > 0 ? current : null
  }

  const fail = (code: DraftImportFailureCode, detail: string): DraftImportResult => ({
    ok: false,
    code,
    detail,
    previous: []
  })

  return {
    version: BRIDGE_VERSION,
    currentSession,

    get disposed(): boolean {
      return disposed
    },

    dispose(reason = 'disposed'): void {
      if (disposed) return
      disposed = true
      disposeReason = reason
    },

    importFiles(request: BridgeImportRequest): DraftImportResult {
      const files = request.files ?? []

      // 生命周期优先：撤销之后连"当前会话"都不再成立，任何导入都必须确定失败。
      if (disposed) {
        return fail(
          'capability-disabled',
          `${CAPABILITY_CODES.capabilityDisabled}：附件桥已撤销（${disposeReason ?? 'disposed'}）；需要页面重载才能重新启用`
        )
      }

      // 能力闸门：remote 降级/禁用或已存在未知上传承载时，能力不可用，
      // 桥拒绝导入，而不是换一条路径继续把文件塞进草稿。
      const capability = runtime.capability?.() ?? null
      if (capability !== null && capability.status !== 'available') {
        const conflict = capability.code === CAPABILITY_CODES.capabilityConflict
        return fail(
          conflict ? 'capability-conflict' : 'capability-disabled',
          `${capability.code ?? CAPABILITY_CODES.capabilityDisabled}：${capability.reason ?? capability.status}`
        )
      }

      // 顺序有意为之：先区分"宿主契约不符"与"当前没有会话"。sessions 服务缺失说明本版本的
      // 宿主不满足本适配器的前置条件（unsupported），而不是用户停在首页（no-session）。
      const sessions = runtime.getSessions()
      if (sessions === undefined) {
        return fail('unsupported', 'sessions 服务不可用（宿主契约不符）')
      }

      const requested = request.sessionId ?? currentSession()
      if (requested === null || requested === undefined || requested === '') {
        return fail('no-session', '没有可用会话（首页/未选择工作区）')
      }

      const current = currentSession()
      // 归属：只有当目标是**当前**会话时，页面上唯一渲染的 composer 才确定属于它。
      if (current !== requested) {
        return fail(
          'context-changed',
          `目标会话 ${requested} 不是当前会话 ${current ?? '(无)'}；拒绝在别的会话上导入`
        )
      }

      const scope = sessions.scope(requested)
      if (scope === null || scope === undefined) {
        return fail('no-session', `会话 ${requested} 解析不出 scope`)
      }

      const conversation = asConversation(runtime.conversationOf(scope))
      if (conversation === null) {
        return fail('unsupported', 'conversation 服务不可用或形状不符（input.for/blocks.storeFor）')
      }

      const cards = runtime.document.querySelectorAll('[data-composer-card]')
      if (cards.length !== 1) {
        return fail(
          'no-composer-card',
          `文档内有 ${cards.length} 个 [data-composer-card]，归属不确定`
        )
      }
      const card = cards[0] as Element

      const sessionInput = conversation.input.for(scope)
      const blockStore = conversation.blocks.storeFor(requested)
      const deps = createConversationDeps({
        sessionInput,
        blockStore,
        document: runtime.document,
        // 锚点取当前会话的唯一卡片本身：`closest` 包含自身，因此卡片即其所属卡片。
        anchor: card
      })

      return importDraftFiles(deps, { sessionId: requested, epoch: current }, files)
    }
  }
}
