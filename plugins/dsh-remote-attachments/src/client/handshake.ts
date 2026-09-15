/**
 * 握手（D13）：`hello` / `capabilities` / `context` 的**真实检查**。
 *
 * 线协议 v1 已经把字段集、范围与枚举冻结在 `schemas/remote-attachments/v1/`；
 * 生产 codec（`src/shared/wire/`）负责字段级判定。本模块只做 codec 不做的事：
 *
 *   1. **协议版本核对**：逐条再核对一次 `v`，并把结论写进状态面；
 *   2. **限额钳制**：本端策略先钳到冻结上限，对端声明再钳到本端策略，
 *      于是"生效限额 ≤ 冻结上限"恒成立，且逐项记录 declared/effective/clamped；
 *   3. **来源（origin）规则**：局域网 HTTP 不是安全上下文，不假设 `crypto.subtle`；
 *      握手绑定页面来源，来源变了（导航）即 `context-changed`，迟到消息不得再进来；
 *   4. **会话身份**：`context` 必须携带非空 sessionId，且（在能解析当前会话时）
 *      必须等于宿主当前会话；没有有效会话身份就不建立身份、不接受批次。
 *
 * 判定顺序固定：版本 → 来源 → 能力 → 字段/身份。任何拒绝都带确定的拒绝码与中文说明。
 */

import {
  MAX_EPOCH,
  MAX_ID_CHARS,
  PROTOCOL_VERSION,
  WIRE_FEATURES,
  WIRE_TIMEOUTS,
  checkProtocolVersion
} from '../shared/protocol.js'
import {
  CAPABILITY_CODES,
  classifyOrigin,
  type CapabilityResolution,
  type OriginFacts
} from '../shared/capabilities.js'
import { clampLimits, type LimitClamp } from '../shared/limits.js'
import type {
  CapabilitiesMessage,
  ContextMessage,
  HelloMessage,
  WireErrorCode,
  WireLimits,
  WireMessage
} from '../shared/wire/index.js'

/** 握手模块版本，调用方必须显式核对。 */
export const HANDSHAKE_VERSION = 1

/**
 * 浏览器接收端广告的线协议特性。
 *
 * 刻意**不含** `screenshot`：截图采集是原生端的能力，浏览器半区只接收字节，
 * 不宣称自己能产出截图。多文件批次与 cancel 由接收端状态机真实支持。
 */
export const CLIENT_FEATURES: readonly string[] = Object.freeze(
  WIRE_FEATURES.filter((feature) => feature !== 'screenshot')
)

/** 拒绝结果。拒绝码取自冻结的 `WIRE_ERROR_CODES`。 */
export interface HandshakeRejection {
  readonly ok: false
  readonly code: WireErrorCode
  readonly detail: string
  readonly path: string
  /** 拒绝的根本原因是能力问题时的结果类代码（`capability-*`）；否则为 null。 */
  readonly capabilityCode: string | null
}

/** 接受结果。 */
export interface HandshakeAcceptance {
  readonly ok: true
  readonly type: 'hello' | 'capabilities' | 'context'
  readonly duplicate: false
  /** 本端生效限额（已钳制）。 */
  readonly limits: WireLimits
  /** context 被接受时给出的绑定身份。 */
  readonly context: ContextMessage | null
}

export type HandshakeResult = HandshakeAcceptance | HandshakeRejection

/** 握手可观测状态（供状态面与证据逐字段断言）。 */
export interface HandshakeSnapshot {
  readonly version: number
  readonly phase: 'idle' | 'hello' | 'ready' | 'refused'
  readonly protocolVersion: number
  readonly clientBuild: string
  readonly features: readonly string[]
  readonly peerBuild: string | null
  readonly peerFeatures: readonly string[]
  /** 本端策略（已经钳到冻结上限）。 */
  readonly policyLimits: WireLimits
  /** 对端声明的限额；未收到时为 null。 */
  readonly declaredLimits: WireLimits | null
  /** 生效限额 = min(本端策略, 对端声明)。 */
  readonly effectiveLimits: WireLimits
  readonly clamps: readonly LimitClamp[]
  readonly origin: OriginFacts
  /** 握手绑定的来源；与当前来源不同即视为导航。 */
  readonly boundOrigin: string
  readonly originChanged: boolean
  readonly context: ContextMessage | null
  readonly refusals: number
  readonly lastRejection: { code: WireErrorCode; detail: string; capabilityCode: string | null } | null
  readonly startedAtMs: number
  readonly lastActivityMs: number
  readonly handshakeTimeoutMs: number
  /** 超过握手超时仍未 ready（纯计算，不自行起定时器）。 */
  readonly timedOut: boolean
}

/** 构造选项。除 `clientBuild` 外全部可选：缺省即"不做该检查"。 */
export interface HandshakeOptions {
  /** 线协议 `hello.clientBuild`。 */
  readonly clientBuild: string
  /** 本端策略限额；会被钳到冻结上限。 */
  readonly limits?: Partial<WireLimits> | undefined
  /** 当前页面来源；缺省读 `globalThis.location`（Node 中为空来源，不检查来源）。 */
  readonly origin?: (() => OriginFacts) | undefined
  /** 宿主当前会话；返回 null 表示当前没有可用会话。缺省不检查。 */
  readonly currentSession?: (() => string | null) | undefined
  /** 当前能力判定；缺省视为 available（组件级单测用）。 */
  readonly capability?: (() => CapabilityResolution) | undefined
  /** 注入时钟。 */
  readonly now?: (() => number) | undefined
}

const reject = (
  code: WireErrorCode,
  detail: string,
  path: string,
  capabilityCode: string | null = null
): HandshakeRejection => ({ ok: false, code, detail, path, capabilityCode })

/** 冻结字段级再核对：id 字符集与长度（codec 已判，这里是纵深防御）。 */
function checkId(value: unknown, path: string): HandshakeRejection | null {
  if (typeof value !== 'string' || value.length === 0) {
    return reject('missing-field', `${path} 缺失或为空`, path)
  }
  if (value.length > MAX_ID_CHARS) {
    return reject('field-too-large', `${path} 长度 ${value.length} > ${MAX_ID_CHARS}`, path)
  }
  if (!/^[A-Za-z0-9._:-]+$/.test(value)) {
    return reject('invalid-field-value', `${path} 含不允许的字符`, path)
  }
  return null
}

/** 冻结字段级再核对：epoch 是非负 int32。 */
function checkEpoch(value: unknown, path: string): HandshakeRejection | null {
  if (!Number.isInteger(value)) return reject('invalid-field-type', `${path} 必须是整数`, path)
  const epoch = value as number
  if (epoch < 0) return reject('negative-integer', `${path} 不得为负`, path)
  if (epoch > MAX_EPOCH) return reject('integer-out-of-range', `${path} 超出 int32`, path)
  return null
}

/** 浏览器半区的握手状态机。 */
export class AttachmentHandshake {
  private readonly clientBuild: string
  private readonly originProvider: (() => OriginFacts) | undefined
  private readonly currentSession: (() => string | null) | undefined
  private readonly capability: () => CapabilityResolution
  private readonly now: () => number
  private readonly policy: WireLimits
  private readonly boundOrigin: string
  private readonly startedAtMs: number

  private phase: HandshakeSnapshot['phase'] = 'idle'
  private peerBuild: string | null = null
  private peerFeatures: readonly string[] = []
  private declared: WireLimits | null = null
  private effective: WireLimits
  private clamps: readonly LimitClamp[] = []
  private context: ContextMessage | null = null
  private refusals = 0
  private lastRejection: HandshakeSnapshot['lastRejection'] = null
  private lastActivityMs: number

  constructor(options: HandshakeOptions) {
    this.clientBuild = options.clientBuild
    this.originProvider = options.origin
    this.currentSession = options.currentSession
    this.capability =
      options.capability ??
      (() => ({ status: 'available', code: null, missing: [], reason: null }) as CapabilityResolution)
    this.now = options.now ?? (() => Date.now())
    const policy = clampLimits(options.limits, undefined)
    this.policy = policy.limits
    this.effective = policy.limits
    this.boundOrigin = this.currentOrigin().origin
    this.startedAtMs = this.now()
    this.lastActivityMs = this.startedAtMs
  }

  // ————————————————————————————————————————————————————————————
  // 公开读数
  // ————————————————————————————————————————————————————————————

  /** 当前页面来源事实。 */
  currentOrigin(): OriginFacts {
    if (this.originProvider !== undefined) return this.originProvider()
    return readGlobalOrigin()
  }

  /** 生效限额（已钳制，恒不超过冻结上限）。 */
  get limits(): WireLimits {
    return this.effective
  }

  get snapshot(): HandshakeSnapshot {
    const origin = this.currentOrigin()
    const now = this.now()
    const timedOut = this.phase !== 'ready' && now - this.startedAtMs > WIRE_TIMEOUTS.handshakeMs
    return {
      version: HANDSHAKE_VERSION,
      phase: this.phase,
      protocolVersion: PROTOCOL_VERSION,
      clientBuild: this.clientBuild,
      features: CLIENT_FEATURES,
      peerBuild: this.peerBuild,
      peerFeatures: this.peerFeatures,
      policyLimits: this.policy,
      declaredLimits: this.declared,
      effectiveLimits: this.effective,
      clamps: this.clamps,
      origin,
      boundOrigin: this.boundOrigin,
      originChanged: origin.origin !== this.boundOrigin,
      context: this.context,
      refusals: this.refusals,
      lastRejection: this.lastRejection,
      startedAtMs: this.startedAtMs,
      lastActivityMs: this.lastActivityMs,
      handshakeTimeoutMs: WIRE_TIMEOUTS.handshakeMs,
      timedOut
    }
  }

  /** 导航/重载：已建立的 context 失效（迟到消息得 `context-changed`）。 */
  navigate(): void {
    this.context = null
    if (this.phase === 'ready') this.phase = 'hello'
    this.lastActivityMs = this.now()
  }

  // ————————————————————————————————————————————————————————————
  // 本端广告
  // ————————————————————————————————————————————————————————————

  /**
   * 本端要发给对端的握手消息：先 `hello` 后 `capabilities`。
   *
   * 顺序固定（`hello` 声明构建、`capabilities` 声明特性与生效限额）；
   * 由传输层显式取走，**不**混进 `drainOutgoing()` 的 ack/import-result 流，
   * 免得把"每个 chunk 恰好一条 ack"这类判据搅乱。
   */
  outgoing(): readonly WireMessage[] {
    const hello: HelloMessage = { v: 1, type: 'hello', clientBuild: this.clientBuild }
    const capabilities: CapabilitiesMessage = {
      v: 1,
      type: 'capabilities',
      features: [...CLIENT_FEATURES],
      limits: { ...this.policy }
    }
    return [hello, capabilities]
  }

  // ————————————————————————————————————————————————————————————
  // 入站
  // ————————————————————————————————————————————————————————————

  /** 检查来源是否仍然绑定（导航后为 false）。 */
  private originGate(): HandshakeRejection | null {
    const origin = this.currentOrigin()
    if (origin.origin === '' && this.boundOrigin === '') return null
    if (origin.origin !== this.boundOrigin) {
      return reject(
        'context-changed',
        `页面来源已由 ${this.boundOrigin} 变为 ${origin.origin}（导航后迟到消息一律拒绝）`,
        'origin'
      )
    }
    return null
  }

  /** 能力闸门：不可用时给出确定的结果类代码。 */
  private capabilityGate(): HandshakeRejection | null {
    const resolution = this.capability()
    if (resolution.status === 'available') return null
    const code = resolution.code ?? CAPABILITY_CODES.capabilityDisabled
    return reject(
      'no-session',
      `${code}：附件能力当前不可用（${resolution.reason ?? resolution.status}）`,
      'capability',
      code
    )
  }

  private refuse(rejection: HandshakeRejection): HandshakeRejection {
    this.refusals += 1
    this.lastRejection = {
      code: rejection.code,
      detail: rejection.detail,
      capabilityCode: rejection.capabilityCode
    }
    this.phase = 'refused'
    return rejection
  }

  /** 接受 `hello`：核对版本与构建标识，记录对端构建。 */
  acceptHello(message: HelloMessage): HandshakeResult {
    const version = checkProtocolVersion((message as { v?: unknown }).v)
    if (!version.ok) {
      return this.refuse(
        reject('version-mismatch', `hello.v=${String(version.received)}，期望 ${version.expected}`, 'v')
      )
    }
    const origin = this.originGate()
    if (origin !== null) return this.refuse(origin)
    const build = message.clientBuild
    if (typeof build !== 'string' || build.length === 0) {
      return this.refuse(reject('missing-field', 'hello.clientBuild 缺失', 'clientBuild'))
    }
    if (build.length > 64 || !/^[A-Za-z0-9._/+:-]+$/.test(build)) {
      return this.refuse(reject('invalid-field-value', `hello.clientBuild 形状不合法：${build.slice(0, 32)}`, 'clientBuild'))
    }
    this.peerBuild = build
    if (this.phase === 'idle') this.phase = 'hello'
    this.lastActivityMs = this.now()
    return { ok: true, type: 'hello', duplicate: false, limits: this.effective, context: this.context }
  }

  /**
   * 接受 `capabilities`：核对版本、特性枚举，并把对端限额钳到本端策略。
   *
   * 对端声明的限额高于**冻结上限**是契约违规（schema 已禁止），这里再拒一次；
   * 高于本端策略但未越界则合法，按 min 钳低。
   */
  acceptCapabilities(message: CapabilitiesMessage): HandshakeResult {
    const version = checkProtocolVersion((message as { v?: unknown }).v)
    if (!version.ok) {
      return this.refuse(
        reject('version-mismatch', `capabilities.v=${String(version.received)}，期望 ${version.expected}`, 'v')
      )
    }
    const origin = this.originGate()
    if (origin !== null) return this.refuse(origin)

    const features = message.features
    if (!Array.isArray(features)) {
      return this.refuse(reject('invalid-field-type', 'capabilities.features 必须是数组', 'features'))
    }
    if (features.length > WIRE_FEATURES.length) {
      return this.refuse(reject('field-too-large', `capabilities.features 最多 ${WIRE_FEATURES.length} 项`, 'features'))
    }
    for (const feature of features) {
      if (typeof feature !== 'string' || !WIRE_FEATURES.includes(feature)) {
        return this.refuse(reject('unknown-enum-value', `未知特性：${String(feature)}`, 'features'))
      }
    }

    const clamped = clampLimits(message.limits as Partial<WireLimits>, this.policy)
    const violation = clamped.violations.find((item) => item.reason === 'above-frozen')
    if (violation !== undefined) {
      return this.refuse(
        reject(
          'integer-out-of-range',
          `对端限额 ${violation.key}=${violation.declared} 超过冻结上限 ${violation.limit}`,
          `limits.${violation.key}`
        )
      )
    }
    const other = clamped.violations[0]
    if (other !== undefined) {
      return this.refuse(
        reject(
          other.reason === 'not-an-integer' ? 'invalid-field-type' : 'integer-out-of-range',
          `对端限额 ${other.key}=${other.declared} 不合法`,
          `limits.${other.key}`
        )
      )
    }

    // 对端**原文**声明与被钳后的生效值分开保存：状态面必须能说出"它声明了什么、
    // 我们实际按什么执行"，只留一个数就会把"被钳"这件事藏起来。
    this.declared = { ...(message.limits as WireLimits) }
    this.effective = clamped.limits
    this.clamps = clamped.clamps
    this.peerFeatures = [...features]
    if (this.phase !== 'ready') this.phase = 'ready'
    this.lastActivityMs = this.now()
    return { ok: true, type: 'capabilities', duplicate: false, limits: this.effective, context: this.context }
  }

  /**
   * 接受 `context`：真正建立身份绑定。
   *
   * 拒绝顺序：版本 → 来源 → 能力 → 字段 → 会话身份。全部通过才算"有有效会话身份"。
   */
  acceptContext(message: ContextMessage): HandshakeResult {
    const version = checkProtocolVersion((message as { v?: unknown }).v)
    if (!version.ok) {
      return this.refuse(
        reject('version-mismatch', `context.v=${String(version.received)}，期望 ${version.expected}`, 'v')
      )
    }
    const origin = this.originGate()
    if (origin !== null) return this.refuse(origin)
    const capability = this.capabilityGate()
    if (capability !== null) return this.refuse(capability)

    for (const [path, value] of [
      ['sessionId', message.sessionId],
      ['targetId', message.targetId],
      ['composerScope', message.composerScope]
    ] as const) {
      const failure = checkId(value, path)
      if (failure !== null) return this.refuse(failure)
    }
    for (const [path, value] of [
      ['documentEpoch', message.documentEpoch],
      ['composerEpoch', message.composerEpoch]
    ] as const) {
      const failure = checkEpoch(value, path)
      if (failure !== null) return this.refuse(failure)
    }

    // 会话身份：没有有效 sessionId 不建立身份。
    if (message.sessionId.length === 0) {
      return this.refuse(reject('no-session', 'context.sessionId 为空：没有有效会话身份', 'sessionId'))
    }
    if (this.currentSession !== undefined) {
      const current = this.currentSession()
      if (current === null || current === '') {
        return this.refuse(
          reject('no-session', '宿主当前没有可编辑会话（首页/未选择工作区）：拒绝建立身份', 'sessionId')
        )
      }
      if (current !== message.sessionId) {
        return this.refuse(
          reject(
            'context-changed',
            `context.sessionId=${message.sessionId} 不是宿主当前会话 ${current}`,
            'sessionId'
          )
        )
      }
    }

    this.context = { ...message }
    this.phase = 'ready'
    this.lastActivityMs = this.now()
    return { ok: true, type: 'context', duplicate: false, limits: this.effective, context: this.context }
  }
}

/** 读取 `globalThis.location`（浏览器）；Node 中给出空来源（不做来源检查）。 */
export function readGlobalOrigin(): OriginFacts {
  const scope = globalThis as {
    location?: { origin?: unknown; hostname?: unknown; protocol?: unknown }
    isSecureContext?: unknown
    crypto?: { subtle?: unknown }
  }
  const location = scope.location
  const origin = typeof location?.origin === 'string' ? location.origin : ''
  const hostname = typeof location?.hostname === 'string' ? location.hostname : ''
  const protocol = typeof location?.protocol === 'string' ? location.protocol : ''
  return classifyOrigin({
    origin,
    hostname,
    protocol,
    isSecureContext: scope.isSecureContext === true,
    hasWebCrypto: scope.crypto?.subtle !== undefined && scope.crypto?.subtle !== null
  })
}
