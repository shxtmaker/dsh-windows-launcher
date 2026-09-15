/**
 * 附件附加插件 —— 浏览器（client）半区。
 *
 * 职责边界（方案 2.0 第 3、4.4 节）：
 * - 在会话 scope 内定位所属 composer 的原生文件 input，用 DataTransfer 写入 File
 *   并只触发一次 change，随后在同一同步任务中确认新增附件 ID；
 * - 不抢占 `conversation.input.attachments` single slot，不读 React 私有字段，
 *   不向 document 广播合成 drop。
 *
 * D03 只建立可打包、可加载、可测试的骨架。**本模块在导入期不访问 window /
 * document / navigator**：这样入口既能在真实浏览器中由 client module host 加载，
 * 也能在 Node 中被打包与单测检查，从而用测试证明入口真实可用而不是空壳。
 *
 * 本文件是 **ESM 源形态**（供 typecheck 与单测使用）。真正被 client-modules 服务出去的
 * 是构建生成的 `lib/client.js`——它必须是 `window.__ModuleLoader__.load({id, factory})`
 * 的经典脚本包裹形态（见 scripts/build-client-bundle.mjs 与 D08 轮次记录）。
 */

import {
  PACKAGE_NAME,
  STATUS_GLOBAL,
  UPLOAD_HOOK_GLOBAL,
  resolveCapabilityStatus
} from '../shared/capabilities.js'
import { ADDON_GLOBAL, composeAttachmentsAddon, type ComposedAddon } from './compose.js'

/** 浏览器半区配置（cordis 的**第二个**参数，不是第一个）。 */
export interface ClientConfig {
  /** 附件能力总开关，与 host 侧同名配置语义一致。 */
  enabled?: boolean
}

/**
 * 浏览器半区可见的最小 cordis 上下文。
 *
 * 只声明本半区真正会用到的部分，避免把 host 侧服务的形状带进 client 产物；也让单测可以
 * 用一个普通对象调用 `apply`。**必须与 cordis 的调用约定一致：`apply(ctx, config)`**——
 * D08 实测把 `config` 当第一个参数会让 cordis 的 ctx 代理抛
 * `cannot get property "enabled" without inject`，从而整个 loader entry 挂载失败。
 */
export interface ClientContext {
  readonly logger?: {
    info(...args: unknown[]): void
    warn(...args: unknown[]): void
  }
  /** 取 cordis 服务（缺失时返回 undefined）。 */
  get?(name: string): unknown
  /** 页面全局，默认 `globalThis`；单测可注入。 */
  readonly [key: string]: unknown
}

/** 运行环境判定结果，用于在不触碰全局对象的前提下决定后续注册。 */
export interface ClientEnvironment {
  /** 存在可供挂载的 DOM 与浏览器全局。 */
  hasBrowserGlobals: boolean
  /** 是否存在可用的 DataTransfer 构造器（草稿适配器前置条件）。 */
  hasDataTransfer: boolean
}

/**
 * 探测运行环境。
 *
 * 全程使用 `globalThis` 且做存在性判断，因此导入期与调用期都不会在 Node 中抛错。
 * 该结果只描述"是否具备运行条件"，不代表草稿入口已经验证可用。
 */
export function detectClientEnvironment(): ClientEnvironment {
  const scope = globalThis as Record<string, unknown>
  const hasBrowserGlobals =
    typeof scope['window'] !== 'undefined' && typeof scope['document'] !== 'undefined'
  return {
    hasBrowserGlobals,
    hasDataTransfer: typeof scope['DataTransfer'] === 'function'
  }
}

/** client 半区能力快照，供诊断与测试断言。 */
export interface ClientCapabilitySnapshot {
  packageName: string
  status: string
  /** 不可用/降级的机器可读原因（D13）。 */
  code: string | null
  missing: readonly string[]
  environment: ClientEnvironment
  reason: string | null
}

/**
 * 计算 client 侧能力快照。
 *
 * 这里刻意保持**保守**：本函数在 `apply` 的返回值与单测里被调用，缺少页面与 cordis
 * 上下文时无法证明依赖齐备，因此按 unavailable 报告。真正的、逐字段可核对的能力
 * 探测在 `compose.ts` 的 `probeCapability()`——它读页面来源、remote seat、上传承载
 * 状态与宿主服务，结果通过 `__DSH_ATTACHMENTS_ADDON__.status()` 暴露。
 */
export function snapshotClientCapabilities(config: ClientConfig = {}): ClientCapabilitySnapshot {
  const environment = detectClientEnvironment()
  const capability = resolveCapabilityStatus({
    remoteChannel: false,
    harnessDraftImport: false,
    enabled: config.enabled !== false
  })
  return {
    packageName: PACKAGE_NAME,
    status: capability.status,
    code: capability.code,
    missing: capability.missing,
    environment,
    reason: capability.reason
  }
}

/**
 * 读取 cordis 的 `ctx.effect`（存在才用）。
 *
 * 必须用 try/catch 读取：cordis 的 ctx 代理对**未声明的属性**会直接抛
 * `cannot get property "…" without inject`（D08 实测），而 `effect` 是 Context 的
 * 自有方法、不是服务，因此"读取失败"与"没有该方法"都归为"拿不到 effect"。
 */
function readCordisEffect(
  ctx: ClientContext
): ((callback: () => void | (() => void), label?: string) => unknown) | null {
  try {
    const candidate = ctx['effect']
    return typeof candidate === 'function'
      ? (candidate as (callback: () => void | (() => void), label?: string) => unknown).bind(ctx)
      : null
  } catch {
    return null
  }
}

/**
 * 浏览器半区插件入口（cordis 契约：`apply(ctx, config)`）。
 *
 * D13：把接收端、草稿桥、能力判定与生命周期**组合**起来，并且**只在这里**安装页面全局。
 * 返回的能力快照仍是 D03 的保守形态（见 `snapshotClientCapabilities`），
 * 真实状态从 `__DSH_ATTACHMENTS_ADDON__.status()` 读。
 *
 * @param ctx - client 侧 cordis 上下文（只读 `logger` 与 `get`）。
 * @param config - 插件配置，缺省表示全部默认。
 */
export function apply(ctx: ClientContext, config: ClientConfig = {}): ClientCapabilitySnapshot {
  const snapshot = snapshotClientCapabilities(config)

  // 安装版本化附件桥 + 分块接收端宿主 + 生命周期座位（唯一安装点，见 compose.ts）。
  // 服务是**每次调用时**惰性解析的，因此插件挂载顺序不影响结果。
  const scope = (globalThis ?? {}) as Record<string, unknown>
  const effect = readCordisEffect(ctx)
  const composed: ComposedAddon = composeAttachmentsAddon({
    scope,
    config,
    ctx: { get: (name: string) => (typeof ctx?.get === 'function' ? ctx.get(name) : undefined) },
    document: scope['document'] as Document,
    cordisEffect: effect !== null
  })

  // 真实禁用路径：cordis fiber 被撤销（插件被停用/卸载/重载）时执行 teardown。
  // 撤销后悔的是"已经捕获闭包的运行期"（Harness 的 FileUploadRuntime）——
  // 那部分无法靠恢复全局变量回退，因此状态面会明确报告 ReloadRequired。
  if (effect !== null) {
    effect(
      () => () => {
        composed.handle.dispose('cordis-dispose')
      },
      'remote-attachments: addon lifecycle'
    )
  }

  if (typeof ctx?.logger?.info === 'function') {
    ctx.logger.info(`remote-attachments-client: 能力状态 ${snapshot.status}（真实探测见 ${ADDON_GLOBAL}.status()）`)
  }
  return snapshot
}

export const name = 'remote-attachments-client'

export default { name, apply }

// D08：会话草稿适配器（公开契约 + DOM 适配，见该文件顶部说明）。
export {
  BRIDGE_GLOBAL,
  BRIDGE_VERSION,
  createAttachmentBridge,
  type AttachmentBridge,
  type BridgeImportRequest,
  type BridgeRuntime,
  type SessionsFacade
} from './bridge.js'

export {
  createConversationDeps,
  importDraftFiles,
  type ComposerBlockStore,
  type DraftAdapterDeps,
  type DraftImportContext,
  type DraftImportFailureCode,
  type DraftImportResult,
  type SessionInputFacade
} from './draft-adapter.js'

// D12：浏览器分块接收端（协议状态机复用 D10 的 WireSession，不复制消息模型）。
export {
  RECEIVER_GLOBAL,
  RECEIVER_VERSION,
  AttachmentReceiver,
  createAttachmentReceiver,
  createReceiverHost,
  type AssembledFile,
  type ReceiveAccepted,
  type ReceiveRejected,
  type ReceiveResult,
  type ReceiverAccounting,
  type ReceiverFileResult,
  type ReceiverHost,
  type ReceiverHostDeps,
  type ReceiverImportFn,
  type ReceiverImportOutcome,
  type ReceiverImportRequest,
  type ReceiverOptions,
  type ReceiverStage
} from './receiver.js'

// D13：握手（hello/capabilities/context 的真实检查）。
export {
  HANDSHAKE_VERSION,
  CLIENT_FEATURES,
  AttachmentHandshake,
  readGlobalOrigin,
  type HandshakeAcceptance,
  type HandshakeOptions,
  type HandshakeRejection,
  type HandshakeResult,
  type HandshakeSnapshot
} from './handshake.js'

// D13：组合层（唯一安装/撤销页面全局的地方，同时是状态面的来源）。
export {
  ADDON_GLOBAL,
  ADDON_VERSION,
  CARRIER_BRAND,
  REMOTE_SEAT_GLOBAL,
  composeAttachmentsAddon,
  hasRemoteSeat,
  installedAddon,
  probeCapability,
  probeUploadHook,
  readHostEnabled,
  readSessions,
  type AddonFileStatus,
  type AddonHandle,
  type AddonStatus,
  type CapabilityProbeFacts,
  type ComposeDeps,
  type ComposedAddon,
  type UploadHookProbe
} from './compose.js'

// D13：状态面的诊断全局名与上传承载全局名（host/client 同源）。
export { STATUS_GLOBAL, UPLOAD_HOOK_GLOBAL }
