/**
 * 限额钳制（D13）。
 *
 * 线协议 v1 的 `capabilities` 只允许声明**不高于**冻结上限的生效限额
 * （`schemas/remote-attachments/v1/README.md` §3）。本模块把这件事做成一个纯函数，
 * 供三处复用，避免各写一套 min()：
 *
 *   1. 接收端构造：把**本端策略**钳到冻结上限（本地配置不能大于冻结值）；
 *   2. 收到对端 `capabilities`：把**对端声明**再钳到本端策略，得到生效限额；
 *   3. 握手/单测：把"声明了什么、实际生效什么、哪些被钳、哪些越界"逐项记录下来，
 *      让"限额被钳制"这件事可断言，而不是只在心里 min 一下。
 *
 * 越界（高于冻结上限）在正常路径上已由生产 codec 拒绝（`integer-out-of-range`）；
 * 这里再判一次是**纵深防御**，也让绕过 codec 的调用方（本地 API/单测）拿到确定的拒绝。
 */

import { DEFAULT_LIMITS } from './protocol.js'
import type { Limits } from './protocol.js'

/** 限额字段的冻结顺序（与 schema 的 `limits.required` 顺序一致）。 */
export const LIMIT_KEYS = Object.freeze([
  'maxFileBytes',
  'maxFilesPerBatch',
  'maxBatchBytes',
  'maxScreenshotPixels',
  'maxStagingBytesPerTarget',
  'maxConcurrentTargets'
] as const)

/** 限额字段名。 */
export type LimitKey = (typeof LIMIT_KEYS)[number]

/**
 * 每个限额的**下界**（来自 schema 的 `minimum`）。
 * `maxFilesPerBatch`/`maxConcurrentTargets` 的下界是 1：0 会让功能完全不可用。
 */
export const LIMIT_MINIMUMS: Readonly<Record<LimitKey, number>> = Object.freeze({
  maxFileBytes: 0,
  maxFilesPerBatch: 1,
  maxBatchBytes: 0,
  maxScreenshotPixels: 0,
  maxStagingBytesPerTarget: 0,
  maxConcurrentTargets: 1
})

/** 一条限额的钳制记录。 */
export interface LimitClamp {
  readonly key: LimitKey
  /** 声明值；未声明时为 null（表示沿用上限）。 */
  readonly declared: number | null
  /** 生效值 = min(声明, 上限)，且上限本身不超过冻结值。 */
  readonly effective: number
  /** 本端上限（已经钳到冻结值）。 */
  readonly ceiling: number
  /** 声明值确实被上限钳低了。 */
  readonly clamped: boolean
}

/** 非法声明（只在绕过 codec 时有意义）。 */
export interface LimitViolation {
  readonly key: LimitKey
  readonly declared: number
  readonly reason: 'above-frozen' | 'not-an-integer' | 'below-minimum'
  readonly limit: number
}

/** 钳制结果。 */
export interface LimitClampResult {
  readonly ok: boolean
  readonly limits: Limits
  readonly clamps: readonly LimitClamp[]
  readonly violations: readonly LimitViolation[]
}

/** 把一组上限逐项钳到冻结值，得到真正的本端上限集合。 */
export function limitCeiling(overrides?: Partial<Limits> | undefined): Limits {
  return {
    maxFileBytes: Math.min(DEFAULT_LIMITS.maxFileBytes, overrides?.maxFileBytes ?? DEFAULT_LIMITS.maxFileBytes),
    maxFilesPerBatch: Math.min(
      DEFAULT_LIMITS.maxFilesPerBatch,
      overrides?.maxFilesPerBatch ?? DEFAULT_LIMITS.maxFilesPerBatch
    ),
    maxBatchBytes: Math.min(DEFAULT_LIMITS.maxBatchBytes, overrides?.maxBatchBytes ?? DEFAULT_LIMITS.maxBatchBytes),
    maxScreenshotPixels: Math.min(
      DEFAULT_LIMITS.maxScreenshotPixels,
      overrides?.maxScreenshotPixels ?? DEFAULT_LIMITS.maxScreenshotPixels
    ),
    maxStagingBytesPerTarget: Math.min(
      DEFAULT_LIMITS.maxStagingBytesPerTarget,
      overrides?.maxStagingBytesPerTarget ?? DEFAULT_LIMITS.maxStagingBytesPerTarget
    ),
    maxConcurrentTargets: Math.min(
      DEFAULT_LIMITS.maxConcurrentTargets,
      overrides?.maxConcurrentTargets ?? DEFAULT_LIMITS.maxConcurrentTargets
    )
  }
}

/**
 * 把 `declared` 钳到 `ceiling`（`ceiling` 自身先钳到冻结值）。
 *
 * @param declared 声明限额；缺省/缺字段表示"沿用上限"
 * @param ceiling 本端上限；缺省即冻结上限
 */
export function clampLimits(
  declared?: Partial<Limits> | undefined,
  ceiling?: Partial<Limits> | undefined
): LimitClampResult {
  const caps = limitCeiling(ceiling)
  const frozen = DEFAULT_LIMITS
  const clamps: LimitClamp[] = []
  const violations: LimitViolation[] = []
  const limits = {} as Record<LimitKey, number>

  for (const key of LIMIT_KEYS) {
    const cap = caps[key]
    const raw = declared?.[key]
    if (raw === undefined) {
      limits[key] = cap
      clamps.push({ key, declared: null, effective: cap, ceiling: cap, clamped: false })
      continue
    }
    if (!Number.isInteger(raw)) {
      violations.push({ key, declared: raw, reason: 'not-an-integer', limit: cap })
    } else if (raw < LIMIT_MINIMUMS[key]) {
      violations.push({ key, declared: raw, reason: 'below-minimum', limit: LIMIT_MINIMUMS[key] })
    } else if (raw > frozen[key]) {
      violations.push({ key, declared: raw, reason: 'above-frozen', limit: frozen[key] })
    }
    const effective = Math.min(raw, cap)
    limits[key] = effective
    clamps.push({ key, declared: raw, effective, ceiling: cap, clamped: raw > cap })
  }

  return {
    ok: violations.length === 0,
    limits: limits as unknown as Limits,
    clamps,
    violations
  }
}
