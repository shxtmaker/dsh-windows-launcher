/**
 * 附加插件桥的组合（D13）。
 *
 * 把 D12 的浏览器接收端、D08 的草稿桥/适配器、D07 的启动前上传承载与能力判定**接成一条
 * 通路**，并且只暴露一条通路：
 *
 * ```
 * 原生端 ──分块消息──▶ AttachmentReceiver ──组装出 File──▶ bridge.importFiles
 *                                                              └─▶ draft-adapter（生产草稿入口）
 * 原生端 ◀── ack / import-result / hello / capabilities ── drainOutgoing / beginHandshake
 * ```
 *
 * 本模块是**唯一**安装页面全局的地方，因此"装了哪些全局"与"撤销时删哪些"永远对得上：
 *
 * | 全局 | 归属 | 撤销时 |
 * | --- | --- | --- |
 * | `__DSH_ATTACHMENTS_BRIDGE__` | 本插件 | 仍是本实例时删除；桥同时进入拒绝态 |
 * | `__DSH_ATTACHMENTS_RECEIVER__` | 本插件 | 仍是本实例时删除；每个接收端同时撤销 |
 * | `__DSH_ATTACHMENTS_ADDON__` | 本插件 | 删除；诊断写入 `__DSH_ATTACHMENTS_STATUS__.addon` |
 * | `__DSH_FILE_UPLOAD__` | 可能是**别人的** | 只在品牌确属本插件时删除；未知 hook 绝不触碰 |
 * | `__DSH_ATTACHMENTS_STATUS__` | 本插件命名空间（host 启动前脚本创建） | 保留诊断段 |
 *
 * 能力判定在这里收敛（`probeCapability` + `resolveCapabilityStatus`），输入全部是**真实探测**：
 * 页面来源、remote 启动前 seat 是否存在且形状正确、上传承载的状态与归属、宿主服务是否就绪。
 * 局域网 HTTP 不是安全上下文，因此"来源"是一等事实，而不是假设。
 *
 * `ReloadRequired` 的判定同样是真实的：Harness 的 `FileUploadRuntime` 在 boot 阶段就捕获了
 * `hook.fetch` 闭包（固定版本契约，见 host/upload-hook.ts），删除或恢复全局变量都无法让它
 * 重新选择传输。所以撤销后我们**明确报告需要重载**，而不是假装改动已经生效。
 */

import {
  ADDON_BUILD,
  CAPABILITY_CODES,
  CAPABILITY_STATUS,
  CARRIER_BRAND,
  PACKAGE_NAME,
  STATUS_GLOBAL,
  UPLOAD_HOOK_GLOBAL,
  resolveCapabilityStatus,
  type CapabilityResolution,
  type OriginFacts,
  type UploadHookOwnership,
  type UploadHookState
} from '../shared/capabilities.js'
import { DEFAULT_LIMITS, PROTOCOL_VERSION } from '../shared/protocol.js'
import type { CapabilitiesMessage, HelloMessage, WireLimits, WireMessage } from '../shared/wire/index.js'
import { BRIDGE_GLOBAL, createAttachmentBridge, type AttachmentBridge, type SessionsFacade } from './bridge.js'
import {
  RECEIVER_GLOBAL,
  createReceiverHost,
  type AttachmentReceiver,
  type ReceiverFileResult,
  type ReceiverHost
} from './receiver.js'
import { AttachmentHandshake, readGlobalOrigin, type HandshakeSnapshot } from './handshake.js'

/** 组合层版本。调用方必须显式核对。 */
export const ADDON_VERSION = 1

/** 本插件在页面上的生命周期/状态座位。 */
export const ADDON_GLOBAL = '__DSH_ATTACHMENTS_ADDON__'

// 品牌与全局名同源于 shared/capabilities.ts（host 安装、client 探测/撤销共用）。
export { CARRIER_BRAND }

/** 全家桶 remote 启动前 seat 的全局名（只读探测：绝不改写、绝不 restore）。 */
export const REMOTE_SEAT_GLOBAL = '__DSH_REMOTE_CHANNEL_BOOT__'

/** 上传承载的探测结果。 */
export interface UploadHookProbe {
  readonly state: UploadHookState
  readonly ownership: UploadHookOwnership
  /** 消费者真正读取的函数是否存在（`hook.fetch`）。 */
  readonly transport: boolean
}

/** 一次能力探测的完整事实（状态面逐字段回带）。 */
export interface CapabilityProbeFacts {
  readonly enabled: boolean
  readonly remoteChannel: boolean
  readonly harnessDraftImport: boolean
  readonly uploadHook: UploadHookState
  readonly uploadHookOwnership: UploadHookOwnership
  /** remote 的启动前 seat 是否存在且形状正确。 */
  readonly remoteSeat: boolean
  readonly originClass: OriginFacts['originClass']
  readonly hasSessionsService: boolean
  readonly hasDataTransfer: boolean
}

/** 组合层依赖。除 `scope` 外都可注入，便于单测不用浏览器。 */
export interface ComposeDeps {
  /** 页面全局对象；缺省 `globalThis`。 */
  readonly scope?: Record<string, unknown> | undefined
  /** 插件配置（与 host 侧同名）。 */
  readonly config?: { readonly enabled?: boolean } | undefined
  /** cordis 上下文（只用 `get`；缺省表示没有宿主服务）。 */
  readonly ctx?: { readonly get?: ((name: string) => unknown) | undefined } | undefined
  /** 页面 document（草稿适配器定位 composer 卡片）。 */
  readonly document?: Document | undefined
  /** 注入时钟。 */
  readonly now?: (() => number) | undefined
  /** 注入来源探测（单测用）。 */
  readonly origin?: (() => OriginFacts) | undefined
  /** 本端策略限额（会被钳到冻结上限）。 */
  readonly limits?: Partial<WireLimits> | undefined
  /** 本端构建标识。 */
  readonly clientBuild?: string | undefined
  /** 是否注册了 cordis effect 撤销回调（由 client 入口按 ctx 能力决定）。 */
  readonly cordisEffect?: boolean | undefined
}

/** 状态面里的单文件条目（文件账本 + 它来自哪个接收端实例）。 */
export interface AddonFileStatus extends ReceiverFileResult {
  /** 第几个 `host.create()` 出来的接收端（0 起）。 */
  readonly receiver: number
}

/** 可安全序列化的插件状态面。 */
export interface AddonStatus {
  readonly schemaVersion: 1
  readonly packageName: string
  readonly build: string
  readonly addonVersion: number
  readonly protocolVersion: number
  readonly state: 'installed' | 'disposed'
  readonly installedAtMs: number
  readonly disposedAtMs: number | null
  readonly disposeReason: string | null
  readonly origin: OriginFacts
  readonly capability: {
    readonly status: string
    readonly code: string | null
    readonly reason: string | null
    readonly missing: readonly string[]
    readonly facts: CapabilityProbeFacts
  }
  /** 上传路径：附件上传必须走门控的 remote 改写；局域网裸 `/api` 不是可接受的回退。 */
  readonly upload: {
    readonly hook: UploadHookState
    readonly ownership: UploadHookOwnership
    readonly route:
      | 'remote-rewrite'
      | 'loopback-direct'
      | 'refused-no-remote-channel'
      | 'unavailable'
      | 'disposed'
  }
  /** 本端握手广告（hello + capabilities），与 `beginHandshake()` 的内容同源。 */
  readonly advertised: { readonly hello: HelloMessage; readonly capabilities: CapabilitiesMessage }
  /** 最近一次握手的完整状态（来源、对端限额、钳制记录、会话身份）。 */
  readonly handshake: HandshakeSnapshot
  /** 三层状态分开：transport（接收缓冲）/ draft（草稿 staged）/ upload（归 Harness）。 */
  readonly transport: {
    readonly receivers: number
    readonly disposed: number
    readonly bufferedBytes: number
    readonly bufferedFiles: number
    readonly peakBufferedBytes: number
    readonly importsInvoked: number
    readonly importsSucceeded: number
    readonly importsFailed: number
    readonly rejectedMessages: number
    readonly hashBackends: readonly string[]
  }
  readonly draft: {
    readonly staged: readonly string[]
    readonly failed: readonly string[]
    readonly partial: readonly string[]
    readonly pending: readonly string[]
  }
  readonly files: readonly AddonFileStatus[]
  /** 逐 fileId 的部分失败明细（哪一项失败、码是什么）。 */
  readonly partialFailures: readonly {
    readonly fileId: string
    readonly status: string
    readonly code: string | null
  }[]
  /** 上传阶段永远归 Harness：本插件只承认 `none`/`harness-owned`，从不宣称 ready。 */
  readonly uploadOwnership: 'none' | 'harness-owned'
  readonly reloadRequired: {
    readonly required: boolean
    readonly reason: string | null
    /** Harness 在 boot 阶段已捕获承载闭包（固定版本契约）。 */
    readonly carrierCapturedAtBoot: boolean
  }
  readonly lifecycle: {
    readonly cordisEffect: boolean
    readonly disposeCount: number
    readonly globals: readonly string[]
  }
}

/** 生命周期座位（页面全局 `__DSH_ATTACHMENTS_ADDON__`）。 */
export interface AddonHandle {
  readonly version: number
  readonly packageName: string
  readonly disposed: boolean
  /** 能力判定（每次读取都重新探测，不缓存过期结论）。 */
  capability(): CapabilityResolution
  /** 完整状态面（可 JSON 序列化）。 */
  status(): AddonStatus
  /**
   * 撤销：删除本插件拥有的全局、撤销桥与接收端、把 ReloadRequired 写进诊断。
   * 幂等。真实触发点是 cordis fiber 的 dispose（client 入口用 `ctx.effect` 注册）。
   */
  dispose(reason?: string): AddonStatus
}

/** `composeAttachmentsAddon()` 的返回面。 */
export interface ComposedAddon {
  readonly handle: AddonHandle
  readonly bridge: AttachmentBridge
  readonly receiverHost: ReceiverHost
  /** 本插件安装的全局名（与撤销逻辑同源）。 */
  readonly globals: {
    readonly bridge: string
    readonly receiver: string
    readonly addon: string
    readonly status: string
  }
  /** 传输层可以取走的本端握手消息（hello + capabilities），幂等。 */
  beginHandshake(): readonly WireMessage[]
}

// ————————————————————————————————————————————————————————————
// 探测
// ————————————————————————————————————————————————————————————

/** 探测 `__DSH_FILE_UPLOAD__`：状态 + 归属。绝不改写、绝不覆盖。 */
export function probeUploadHook(scope: Record<string, unknown>): UploadHookProbe {
  const value = scope[UPLOAD_HOOK_GLOBAL]
  if (value === undefined || value === null) {
    return { state: 'absent', ownership: 'none', transport: false }
  }
  const carrier = value as { readonly fetch?: unknown; readonly brand?: unknown }
  const transport = typeof carrier.fetch === 'function'
  const ours = carrier.brand === CARRIER_BRAND
  // 任何已存在的值都算"占了座位"：只有明确是我们自己装的才算 installed，其余一律 conflict。
  return { state: transport && ours ? 'installed' : 'conflict', ownership: ours ? 'ours' : 'unknown', transport }
}

/**
 * 探测 remote 的启动前 seat：**形状**必须正确（`restore` 是函数）。
 *
 * 不把"某个全局真值"当授权：这里只用来判断"`/remote` 改写层是否在这一页生效"，
 * 最终授权始终由服务端 `/remote` 决定。形状不符时按不可用处理（fail closed）。
 */
export function hasRemoteSeat(scope: Record<string, unknown>): boolean {
  const seat = scope[REMOTE_SEAT_GLOBAL]
  if (typeof seat !== 'object' || seat === null) return false
  return typeof (seat as { readonly restore?: unknown }).restore === 'function'
}

/** 读宿主服务是否就绪（`sessions` 解析得到即为就绪）。 */
export function readSessions(deps: ComposeDeps): SessionsFacade | undefined {
  const get = deps.ctx?.get
  if (typeof get !== 'function') return undefined
  const value = get('sessions')
  return value === undefined || value === null ? undefined : (value as SessionsFacade)
}

/**
 * 读宿主半区写下的"本插件是否被显式禁用"（`__DSH_ATTACHMENTS_STATUS__.host.enabled`）。
 *
 * cordis 的浏览器半区加载器不带行配置，因此 client 半区拿不到 `enabled: false`；
 * host 半区在禁用时仍会注入一条只写该标记的脚本（见 host/upload-hook.ts）。缺省返回
 * `undefined`（没有标记 = 不据此定罪），只有明确 `false` 才把能力判成 degraded。
 */
export function readHostEnabled(scope: Record<string, unknown>): boolean | undefined {
  const seat = scope[STATUS_GLOBAL] as { readonly host?: { readonly enabled?: unknown } } | undefined
  const enabled = seat?.host?.enabled
  return typeof enabled === 'boolean' ? enabled : undefined
}

/** 组合层的能力探测：返回事实与判定（纯函数式，可逐字段断言）。 */
export function probeCapability(deps: ComposeDeps): {
  readonly facts: CapabilityProbeFacts
  readonly resolution: CapabilityResolution
} {
  const scope = deps.scope ?? (globalThis as unknown as Record<string, unknown>)
  const origin = (deps.origin ?? readGlobalOrigin)()
  const hook = probeUploadHook(scope)
  const seat = hasRemoteSeat(scope)
  const loopback = origin.originClass === 'loopback'
  const sessions = readSessions(deps)
  const hasDataTransfer = typeof scope['DataTransfer'] === 'function'
  // 显式禁用的两个来源：本半区自己的配置（单测/将来加载器带配置时）与宿主写下的标记
  // （真实 Harness 下 client 半区拿不到行配置，只能靠标记）。任一为 false 即视为禁用。
  const hostEnabled = readHostEnabled(scope)
  const facts: CapabilityProbeFacts = {
    enabled: deps.config?.enabled !== false && hostEnabled !== false,
    // 本机（loopback）桌面场景由应用直接访问自己的 origin，remote 的 boot 脚本按设计跳过；
    // 非 loopback 页面则必须真的有改写层，否则上传到不了门控通道。
    remoteChannel: loopback ? true : seat,
    harnessDraftImport: sessions !== undefined && hasDataTransfer,
    uploadHook: hook.state,
    uploadHookOwnership: hook.ownership,
    remoteSeat: seat,
    originClass: origin.originClass,
    hasSessionsService: sessions !== undefined,
    hasDataTransfer
  }
  const resolution = resolveCapabilityStatus({
    remoteChannel: facts.remoteChannel,
    harnessDraftImport: facts.harnessDraftImport,
    enabled: facts.enabled,
    uploadHook: facts.uploadHook,
    origin
  })
  return { facts, resolution }
}

// ————————————————————————————————————————————————————————————
// 组合
// ————————————————————————————————————————————————————————————

/** 组装并安装附加插件桥（幂等安装：同一个 scope 上重复调用时先撤销上一个实例）。 */
export function composeAttachmentsAddon(deps: ComposeDeps = {}): ComposedAddon {
  const scope = deps.scope ?? (globalThis as unknown as Record<string, unknown>)
  const now = deps.now ?? (() => Date.now())
  const origin = deps.origin ?? readGlobalOrigin
  const clientBuild = deps.clientBuild ?? ADDON_BUILD
  const installedAtMs = now()

  // 重复安装（例如热重载后再次 apply）：先撤销上一个实例，避免它的全局与回调泄漏。
  const previous = scope[ADDON_GLOBAL] as AddonHandle | undefined
  if (previous !== undefined && previous !== null && typeof previous.dispose === 'function' && previous.disposed !== true) {
    previous.dispose('reinstalled')
  }

  // 生命周期事实必须先于能力判定声明：撤销后能力判定要读它（见下）。
  let disposed = false
  let disposeReason: string | null = null
  let disposedAtMs: number | null = null
  let disposeCount = 0

  /**
   * 能力判定。
   *
   * 撤销后**不能**再只按页面事实回报（D23 实测的谎报来源）：承载、改写层、宿主服务都可能还在，
   * 于是"读时探测"会给出 available——可这个实例的桥与每个接收端都已经进入确定拒绝态，
   * Harness 也早在 boot 阶段捕获了旧的承载闭包。此时把 available 报出去，等于**仅凭
   * 恢复/残留的全局变量就宣称 runtime 已恢复**。因此生命周期是一等输入：撤销后的结论固定为
   * unavailable + `capability-disabled`，并在原因里点明需要页面重载。
   */
  const capability = (): CapabilityResolution => {
    const live = probeCapability(deps).resolution
    if (!disposed) return live
    const revoked: CapabilityResolution = {
      status: CAPABILITY_STATUS.unavailable,
      code: CAPABILITY_CODES.capabilityDisabled,
      missing: [],
      reason: `${CAPABILITY_CODES.capabilityDisabled}：附件插件已撤销（${disposeReason ?? 'disposed'}）；${CAPABILITY_CODES.reloadRequired}：需要页面重载才能重新启用。`
    }
    return live.facts === undefined ? revoked : { ...revoked, facts: live.facts }
  }
  const currentSession = (): string | null => bridge.currentSession()

  const bridge = createAttachmentBridge({
    // 刻意传取值函数而不是取值结果：宿主在 apply 期间可能还没有 sessions 服务。
    getSessions: () => readSessions(deps),
    conversationOf: (sessionScope) => {
      const carrier = sessionScope as { get?(name: string): unknown } | null | undefined
      if (typeof carrier?.get !== 'function') return undefined
      return carrier.get('conversation')
    },
    document: (deps.document ?? scope['document']) as Document,
    capability
  })

  const handshakeOptions = {
    clientBuild,
    limits: deps.limits ?? DEFAULT_LIMITS,
    origin,
    currentSession,
    capability,
    now
  } as const
  // 组合层自己的握手：用于状态面回答"本端广告了什么"，即便传输层还没接入。
  const primaryHandshake = new AttachmentHandshake(handshakeOptions)

  const receivers: AttachmentReceiver[] = []
  const innerHost = createReceiverHost({
    importFiles: (request) => bridge.importFiles({ sessionId: request.sessionId, files: request.files }),
    ...handshakeOptions
  })
  const innerCreate = innerHost.create.bind(innerHost)
  const receiverHost: ReceiverHost = {
    version: innerHost.version,
    wiring: innerHost.wiring,
    create(options) {
      const receiver = innerCreate(options)
      receivers.push(receiver)
      return receiver
    }
  }

  // 撤销会删掉承载，删完之后再探测就无从判断"当初是不是我们装的"；因此在安装时就钉住这个事实。
  let carrierCaptured = probeUploadHook(scope).ownership === 'ours'

  const status = (): AddonStatus => {
    const probe = probeCapability(deps)
    // 与 `handle.capability()` 同源：撤销后状态面也不得报 available（否则"恢复全局变量"会被读成恢复）。
    const resolved = capability()
    const hook = probeUploadHook(scope)
    const currentOrigin = origin()
    const files: AddonFileStatus[] = []
    for (let index = 0; index < receivers.length; index += 1) {
      const receiver = receivers[index]
      if (receiver === undefined) continue
      for (const file of receiver.fileResults) files.push({ ...file, receiver: index })
    }
    const pick = (statuses: readonly string[]): readonly string[] =>
      files.filter((file) => statuses.includes(file.status)).map((file) => file.fileId)
    const advertised = primaryHandshake.outgoing()
    const hello = advertised[0] as HelloMessage
    const capabilities = advertised[1] as CapabilitiesMessage
    const last = receivers.length > 0 ? receivers[receivers.length - 1] : undefined

    return {
      schemaVersion: 1,
      packageName: PACKAGE_NAME,
      build: clientBuild,
      addonVersion: ADDON_VERSION,
      protocolVersion: PROTOCOL_VERSION,
      state: disposed ? 'disposed' : 'installed',
      installedAtMs,
      disposedAtMs,
      disposeReason,
      origin: currentOrigin,
      capability: {
        status: resolved.status,
        code: resolved.code,
        reason: resolved.reason,
        missing: resolved.missing,
        facts: probe.facts
      },
      upload: {
        hook: hook.state,
        ownership: hook.ownership,
        route: disposed
          ? 'disposed'
          : hook.state !== 'installed'
            ? 'unavailable'
            : currentOrigin.originClass === 'loopback'
              ? 'loopback-direct'
              : hasRemoteSeat(scope)
                ? 'remote-rewrite'
                : // 非 loopback 且没有门控改写层：传载会**明确拒绝**而不是回退裸 /api。
                  'refused-no-remote-channel'
      },
      advertised: { hello, capabilities },
      handshake: last?.handshake ?? primaryHandshake.snapshot,
      transport: {
        receivers: receivers.length,
        disposed: receivers.filter((receiver) => receiver.isDisposed).length,
        bufferedBytes: receivers.reduce((total, receiver) => total + receiver.accounting.bufferedBytes, 0),
        bufferedFiles: receivers.reduce((total, receiver) => total + receiver.accounting.bufferedFiles, 0),
        peakBufferedBytes: receivers.reduce((peak, receiver) => Math.max(peak, receiver.accounting.peakBufferedBytes), 0),
        importsInvoked: receivers.reduce((total, receiver) => total + receiver.accounting.importsInvoked, 0),
        importsSucceeded: receivers.reduce((total, receiver) => total + receiver.accounting.importsSucceeded, 0),
        importsFailed: receivers.reduce((total, receiver) => total + receiver.accounting.importsFailed, 0),
        rejectedMessages: receivers.reduce((total, receiver) => total + receiver.accounting.rejectedMessages, 0),
        hashBackends: [...new Set(receivers.map((receiver) => receiver.accounting.hashBackend))]
      },
      draft: {
        staged: pick(['staged']),
        failed: pick(['failed']),
        partial: pick(['partial']),
        pending: pick(['pending'])
      },
      files,
      partialFailures: files
        .filter((file) => file.status === 'failed' || file.status === 'partial')
        .map((file) => ({ fileId: file.fileId, status: file.status, code: file.code })),
      uploadOwnership: files.some((file) => file.upload === 'harness-owned') ? 'harness-owned' : 'none',
      reloadRequired: {
        // 只有"承载确实是我们装的"时才需要重载：Harness 在 boot 阶段已经捕获了它的
        // fetch 闭包，恢复全局变量不能让它重新选择传输。
        required: disposed && carrierCaptured,
        reason:
          disposed && carrierCaptured
            ? `${CAPABILITY_CODES.reloadRequired}：Harness 的 FileUploadRuntime 在 boot 阶段已捕获承载闭包，恢复全局变量无法重新选择；需要重载页面。`
            : null,
        carrierCapturedAtBoot: carrierCaptured
      },
      lifecycle: {
        cordisEffect: deps.cordisEffect === true,
        disposeCount,
        globals: [BRIDGE_GLOBAL, RECEIVER_GLOBAL, ADDON_GLOBAL]
      }
    }
  }

  /** 只删除"仍然指向本实例"的全局；别人的同名全局绝不覆盖。 */
  const revokeGlobal = (name: string, instance: unknown): boolean => {
    if (scope[name] !== instance) return false
    try {
      delete scope[name]
      return scope[name] === undefined
    } catch {
      try {
        scope[name] = undefined
        return true
      } catch {
        return false
      }
    }
  }

  /** 诊断段写入 `__DSH_ATTACHMENTS_STATUS__.addon`（host 启动前脚本创建的命名空间）。 */
  const writeDiagnostics = (): void => {
    const snapshot = status()
    const target = (scope[STATUS_GLOBAL] ?? {}) as Record<string, unknown>
    scope[STATUS_GLOBAL] = target
    target['addon'] = {
      state: snapshot.state,
      build: snapshot.build,
      // capability 必须是**读时**探测，不能钉死安装期快照：本插件在 boot 早期 apply，
      // 而宿主服务（实测 `sessions` 由 @deepseek-ai/dsh-api-session-controller 的 client 半区
      // 稍后注册）可能在那一刻还没出现。安装期快照会把"依赖齐备、功能可用"长期谎报成
      // unavailable（运行时 file-end 却成功），让诊断面成为误导来源。其余字段是安装期/撤销期事实，
      // 保持快照语义。
      get capability() {
        const live = status().capability
        return {
          status: live.status,
          code: live.code,
          reason: live.reason,
          missing: live.missing,
          facts: live.facts
        }
      },
      upload: snapshot.upload,
      reloadRequired: snapshot.reloadRequired,
      disposeReason: snapshot.disposeReason,
      disposedAtMs: snapshot.disposedAtMs
    }
  }

  const handle: AddonHandle = {
    version: ADDON_VERSION,
    packageName: PACKAGE_NAME,
    get disposed(): boolean {
      return disposed
    },
    capability,
    status,
    dispose(reason = 'disposed'): AddonStatus {
      if (disposed) return status()
      disposed = true
      disposeReason = reason
      disposedAtMs = now()

      // 1) 撤销自有回调：桥与每个接收端进入确定拒绝态（保留引用也无法再导入）。
      bridge.dispose(reason)
      for (const receiver of receivers) receiver.dispose(reason)

      // 2) 撤销自有全局：只在仍是本实例时删除。
      revokeGlobal(BRIDGE_GLOBAL, bridge)
      revokeGlobal(RECEIVER_GLOBAL, receiverHost)

      // 3) 启动前上传承载：只删本插件品牌的对象（未知 hook 一律不动）。
      if (probeUploadHook(scope).ownership === 'ours') {
        carrierCaptured = true
        revokeGlobal(UPLOAD_HOOK_GLOBAL, scope[UPLOAD_HOOK_GLOBAL])
      }

      // 4) 撤销后仍要能读到"需要重载"：先把诊断落盘，再撤掉生命周期座位。
      disposeCount += 1
      writeDiagnostics()
      revokeGlobal(ADDON_GLOBAL, handle)
      return status()
    }
  }

  // 安装全局（与撤销逻辑同源）。
  scope[BRIDGE_GLOBAL] = bridge
  scope[RECEIVER_GLOBAL] = receiverHost
  scope[ADDON_GLOBAL] = handle
  writeDiagnostics()

  return {
    handle,
    bridge,
    receiverHost,
    globals: { bridge: BRIDGE_GLOBAL, receiver: RECEIVER_GLOBAL, addon: ADDON_GLOBAL, status: STATUS_GLOBAL },
    beginHandshake: () => primaryHandshake.outgoing()
  }
}

/** 当前页面是否已经安装了本插件的生命周期座位。 */
export function installedAddon(
  scope: Record<string, unknown> = globalThis as unknown as Record<string, unknown>
): AddonHandle | null {
  const value = scope[ADDON_GLOBAL] as AddonHandle | undefined
  return value === undefined || value === null ? null : value
}
