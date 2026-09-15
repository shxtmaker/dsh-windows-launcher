/**
 * 独立附加插件的能力声明与身份常量。
 *
 * 本包只声明自己拥有的附件能力，不声明配对、设备凭据或 `/remote` 代理：
 * 那些由全家桶中的 @linxin666/dsh-remote-web-ui 独占提供。host 与 client 半区
 * 都从这里读取同一份声明，避免两端对"谁提供什么"给出不同答案。
 */

/** 本插件的 npm 包名。与全家桶（@linxin666/dsh-web-all）和 remote 插件身份不同。 */
export const PACKAGE_NAME = '@shxtmaker/dsh-remote-attachments'

/** Cordis patch 中的行 id。与全家桶 web-ui-* / remote-* 命名空间区分。 */
export const CORDIS_ROW_ID = 'remote-attachments'

/**
 * 本插件自身的版本。必须与 `package.json` 的 `version` 一致，
 * 由 `test/unit/addon-compose.test.mjs` 直接比对两个文件，防止这里悄悄漂移。
 */
export const PACKAGE_VERSION = '0.1.0'

/**
 * 包名去掉 npm scope（`@scope/pkg` → `pkg`）。
 *
 * 线协议的 `clientBuild` 只允许 `^[A-Za-z0-9._/+:-]+$`，**不含 `@`**，
 * 因此不能直接把 npm 包名当构建标识用。
 */
export const PACKAGE_UNSCOPED = PACKAGE_NAME.replace(/^@[^/]+\//, '')

/**
 * 线协议 `hello.clientBuild` 使用的构建标识。
 *
 * 主机半区注入的启动前脚本与浏览器半区广告的 hello 用同一个值，
 * 因此"页面里的插件"与"注入 hook 的插件"能被对端认成同一构建。
 * 形状由 `test/unit/d13-handshake.test.mjs` 按 schema 的正则与 64 字符上限守住。
 */
export const ADDON_BUILD = `${PACKAGE_UNSCOPED}/${PACKAGE_VERSION}`

/** 本插件自有的诊断/状态全局名（host 启动前脚本创建，client 半区补写 addon 段）。 */
export const STATUS_GLOBAL = '__DSH_ATTACHMENTS_STATUS__'

/**
 * 启动前上传承载的全局名（D07 安装、D13 探测与撤销共用同一份常量）。
 *
 * host 半区在 boot 之前把 `{ fetch, brand }` 装到这个座位；client 半区只**读**它，
 * 并在品牌确属本插件时才有权撤销。
 */
export const UPLOAD_HOOK_GLOBAL = '__DSH_FILE_UPLOAD__'

/**
 * 上传承载对象的品牌字段。
 *
 * 撤销时必须能回答"这个 hook 是我们装的吗"：只有品牌匹配才允许删除，
 * 别人的未知 hook 一律原样保留（方案 §4.3：不覆盖未知 hook）。
 */
export const CARRIER_BRAND = `${PACKAGE_NAME}/carrier@${PACKAGE_VERSION}`

/**
 * 结果类代码（`ERROR_CODES` 的子集）：能力冲突、能力停用与"必须重载"。
 *
 * 这些码只出现在报文的 `code` 字段、状态面与本地拒绝结果里，
 * 不会被当成线协议**拒绝码**（`WIRE_ERROR_CODES`）使用。
 */
export const CAPABILITY_CODES = Object.freeze({
  /** 已存在未知的 `__DSH_FILE_UPLOAD__`：保留原功能，附件能力不可用。 */
  capabilityConflict: 'capability-conflict',
  /** 依赖（remote 通道/上传承载/草稿入口）不可用：附件能力停用。 */
  capabilityDisabled: 'capability-disabled',
  /** 运行期已经捕获了闭包、恢复全局变量不能重新选择：需要页面重载。 */
  reloadRequired: 'reload-required'
})

/** 结果类代码的取值类型。 */
export type CapabilityCode = (typeof CAPABILITY_CODES)[keyof typeof CAPABILITY_CODES]

/** 页面来源分类。局域网 HTTP 不是安全上下文，不能假设 `crypto.subtle` 存在。 */
export type OriginClass = 'loopback' | 'lan' | 'unknown'

/** 页面来源事实（握手与能力判定共用；不写进线协议字段）。 */
export interface OriginFacts {
  /** `location.origin`；非浏览器环境下为空串。 */
  readonly origin: string
  readonly hostname: string
  readonly protocol: string
  readonly originClass: OriginClass
  /** `isSecureContext`（局域网 HTTP 上为 false）。 */
  readonly isSecureContext: boolean
  /** 是否有可用的 `crypto.subtle`。 */
  readonly hasWebCrypto: boolean
  /** 完整性校验实际使用的后端。 */
  readonly hashBackend: 'webcrypto' | 'pure-js'
}

/** 判定来源事实。纯函数，可在 Node 里用假输入单测。 */
export function classifyOrigin(input: {
  origin?: string | undefined
  hostname?: string | undefined
  protocol?: string | undefined
  isSecureContext?: boolean | undefined
  hasWebCrypto?: boolean | undefined
}): OriginFacts {
  const origin = input.origin ?? ''
  const protocol = input.protocol ?? (origin === '' ? '' : `${origin.split('://')[0] ?? ''}:`)
  let hostname = input.hostname ?? ''
  if (hostname === '' && origin !== '') {
    const match = /^[a-z][a-z0-9+.-]*:\/\/([^/?#]*)/i.exec(origin)
    const authority = match?.[1] ?? ''
    hostname = authority.replace(/^.*@/, '').replace(/:\d+$/, '')
  }
  const lower = hostname.toLowerCase()
  const loopback =
    lower === 'localhost' || lower === '::1' || lower === '[::1]' || /^127(\.\d{1,3}){3}$/.test(lower)
  const originClass: OriginClass = hostname === '' ? 'unknown' : loopback ? 'loopback' : 'lan'
  const hasWebCrypto = input.hasWebCrypto === true
  return {
    origin,
    hostname,
    protocol,
    originClass,
    isSecureContext: input.isSecureContext === true,
    hasWebCrypto,
    hashBackend: hasWebCrypto ? 'webcrypto' : 'pure-js'
  }
}

/** 启动前上传承载的安装状态。 */
export type UploadHookState = 'installed' | 'conflict' | 'absent'

/** 上传承载的归属：`ours` = 本插件安装的（可撤销），`unknown` = 别人的（绝不触碰）。 */
export type UploadHookOwnership = 'ours' | 'unknown' | 'none'

/** 本插件独占提供的能力。 */
export const PROVIDED_CAPABILITIES = Object.freeze(['attachment-paste-import'])

/**
 * 由其它插件提供、本插件只消费的能力。
 * 附件上传复用 Harness 原有上传端点与全家桶 remote 的 `/remote` 改写，
 * 因此这里显式声明依赖，而不是自己实现一份。
 */
export const CONSUMED_CAPABILITIES = Object.freeze({
  /** 由 @linxin666/dsh-remote-web-ui 独占：配对、设备凭据、/remote 门控代理。 */
  remoteChannel: '@linxin666/dsh-remote-web-ui',
  /** 由 Harness 提供：会话草稿、上传端点与 receipt。 */
  harnessDraftImport: '@deepseek-ai/dsh-client-ui-conversation'
})

/**
 * 能力状态。能力未就绪时附件功能明确不可用，而不是静默降级成另一种行为。
 */
export const CAPABILITY_STATUS = Object.freeze({
  /** 依赖齐备，附件能力可用。 */
  available: 'available',
  /** 依赖存在但被禁用或降级。 */
  degraded: 'degraded',
  /** 依赖缺失或版本不匹配。 */
  unavailable: 'unavailable'
})

/** 能力状态取值。 */
export type CapabilityStatus =
  (typeof CAPABILITY_STATUS)[keyof typeof CAPABILITY_STATUS]

/** 能力判定输入。 */
export interface CapabilityFacts {
  /** 全家桶 remote 通道（配对 / 设备凭据 / `/remote` 代理）是否可用。 */
  remoteChannel: boolean
  /** Harness 原生草稿导入入口是否可用。 */
  harnessDraftImport: boolean
  /** 附件插件是否被显式启用；省略视为启用。 */
  enabled?: boolean
  /**
   * 启动前上传承载的状态。局域网页面上只有 `installed` 才能保证上传走
   * 门控的 remote 改写；`conflict` 表示已存在未知 hook（保留原功能）。
   */
  uploadHook?: UploadHookState | undefined
  /** 页面来源事实（用于区分 loopback 与局域网 HTTP）。 */
  origin?: OriginFacts | null | undefined
}

/** 能力判定结果。 */
export interface CapabilityResolution {
  status: CapabilityStatus
  /** 不可用/降级的机器可读原因；`available` 时为 null。 */
  code: CapabilityCode | null
  missing: string[]
  reason: string | null
  /** 判定所依据的事实（原样回带，便于状态面与证据逐字段断言）。 */
  facts?: CapabilityFacts
}

/**
 * 汇总能力状态。判定顺序固定（同一组事实必然得到同一结论）：
 *
 *   1. 未知 hook ⇒ unavailable + `capability-conflict`（保留原功能，绝不覆盖）；
 *   2. remote 通道不可用 ⇒ unavailable + `capability-disabled`；
 *   3. 局域网页面上承载缺失 ⇒ unavailable + `capability-disabled`
 *      （没有承载就只能回退裸 `/api`，方案禁止这种静默回退）；
 *   4. 其它必需依赖缺失 ⇒ unavailable（missing 列出缺谁）；
 *   5. 显式禁用 ⇒ degraded；
 *   6. 否则 available。
 *
 * 注意第 4 步在第 5 步之前：缺依赖优先于"被禁用"，否则禁用会把缺依赖伪装成降级。
 */
export function resolveCapabilityStatus(facts: CapabilityFacts): CapabilityResolution {
  const origin = facts.origin ?? null
  const loopback = origin?.originClass === 'loopback'
  // `uploadHook` 省略 = 本次判定没有探测承载（例如 host 侧快照），不当成"缺失"来定罪。
  const uploadHook = facts.uploadHook

  if (uploadHook === 'conflict') {
    return {
      status: CAPABILITY_STATUS.unavailable,
      code: CAPABILITY_CODES.capabilityConflict,
      missing: [],
      reason: `已存在未知的 __DSH_FILE_UPLOAD__：保留原功能，附件能力不可用（不覆盖未知 hook）。`,
      facts
    }
  }

  if (!facts.remoteChannel) {
    const missing: string[] = [CONSUMED_CAPABILITIES.remoteChannel]
    if (!facts.harnessDraftImport) missing.push(CONSUMED_CAPABILITIES.harnessDraftImport)
    return {
      status: CAPABILITY_STATUS.unavailable,
      code: CAPABILITY_CODES.capabilityDisabled,
      missing,
      reason: '远程通道不可用（remote 被禁用或降级）：附件能力停用；配对与心跳不受影响。',
      facts
    }
  }

  // 局域网页面上承载缺失：Harness 会走它自己捕获的传输，主 window 的 remote 改写拦不到；
  // 附件能力必须明确停用，而不是让上传悄悄打到裸 /api。
  if (uploadHook !== undefined && !loopback && uploadHook !== 'installed') {
    return {
      status: CAPABILITY_STATUS.unavailable,
      code: CAPABILITY_CODES.capabilityDisabled,
      missing: [],
      reason: '启动前上传承载未安装：局域网页面上无法保证上传走门控通道，附件能力停用。',
      facts
    }
  }

  const missing: string[] = []
  if (!facts.harnessDraftImport) missing.push(CONSUMED_CAPABILITIES.harnessDraftImport)
  if (missing.length > 0) {
    return {
      status: CAPABILITY_STATUS.unavailable,
      code: null,
      missing,
      reason: '缺少必需依赖：附件能力不可用；配对与心跳不受影响。',
      facts
    }
  }

  if (facts.enabled === false) {
    return {
      status: CAPABILITY_STATUS.degraded,
      code: CAPABILITY_CODES.capabilityDisabled,
      missing: [],
      reason: '附件插件已被显式禁用；配对、心跳与设备库保持原状。',
      facts
    }
  }

  return { status: CAPABILITY_STATUS.available, code: null, missing: [], reason: null, facts }
}
