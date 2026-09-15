/**
 * 附件附加插件 —— host 半区。
 *
 * 职责边界（方案 2.0 第 3、4.3 节）：
 * - 只做附件能力所需的 host 侧工作，不接管配对路由、设备库或 `/remote` 代理；
 * - 不监听端口、不铸造配对令牌：`/api/pair/*` 与门控由全家桶中的
 *   @linxin666/dsh-remote-web-ui 独占提供。
 *
 * D03 只建立可打包、可加载、可测试的骨架：入口导出真实插件对象与能力状态，
 * 尚未注册 webserver/index-inject 注入（D07）或草稿入口（D08）。
 * 因此这里不声明任何 `inject`：没有假装可用的宿主服务依赖。
 */

import type { Context } from '@deepseek-ai/cordis'
import {
  CORDIS_ROW_ID,
  PACKAGE_NAME,
  PROVIDED_CAPABILITIES,
  resolveCapabilityStatus
} from '../shared/capabilities.js'
import { UPLOAD_ENDPOINT, UPLOAD_HOOK_GLOBAL, buildDisabledHookScript, buildUploadHookScript } from './upload-hook.js'
// 仅类型：拉入 host-webserver 对 Cordis Events 的 'webserver/index-inject' 增补。
// 与上游 dsh-web-all 的做法一致（它同样只把该包放在 devDependencies）。
import type { IndexInjection } from '@deepseek-ai/dsh-host-webserver'


/** 宿主侧配置。D03 只有显式开关；后续任务在此扩展。 */
export interface HostConfig {
  /**
   * 附件能力总开关。默认开启；关闭时插件保持挂载但明确报告 degraded，
   * 不关闭配对、心跳或 `/remote` 代理。
   */
  enabled?: boolean
}

/** 能力快照，便于宿主诊断与测试断言。 */
export interface CapabilitySnapshot {
  packageName: string
  rowId: string
  provided: readonly string[]
  status: string
  missing: string[]
  reason: string | null
}

/**
 * 计算当前能力快照。
 *
 * D03 尚未接入真实依赖探测：`remoteChannel` 与 `harnessDraftImport` 一律为
 * unknown，按 unavailable 处理。这样附件能力在依赖确认前不会假装可用。
 * D07/D08 会用真实探测结果替换这两个输入。
 */
export function snapshotCapabilities(config: HostConfig = {}): CapabilitySnapshot {
  const capability = resolveCapabilityStatus({
    remoteChannel: false,
    harnessDraftImport: false,
    enabled: config.enabled !== false
  })
  return {
    packageName: PACKAGE_NAME,
    rowId: CORDIS_ROW_ID,
    provided: PROVIDED_CAPABILITIES,
    status: capability.status,
    missing: capability.missing,
    reason: capability.reason
  }
}

/**
 * Cordis 插件入口。
 *
 * D07 起在 host 侧注册**启动前上传承载**：通过 `webserver/index-inject` 往
 * `<head>` 注入一段自包含脚本，在任何 boot entry 之前安装 `__DSH_FILE_UPLOAD__`。
 * 这样 Harness 的 FileUploadRuntime 无论何时捕获 fetch，拿到的都是承载函数。
 *
 * 不注册路由、不铸造令牌、不改写 `/remote`：那些由全家桶 remote 插件独占提供。
 */
export function apply(ctx: Context, config: HostConfig = {}): void {
  const snapshot = snapshotCapabilities(config)
  const logger = ctx.logger('remote-attachments')
  logger.info(
    `已挂载 ${snapshot.packageName}（row=${snapshot.rowId}）；能力状态=${snapshot.status}`
  )
  if (snapshot.reason !== null) {
    logger.info(snapshot.reason)
  }

  if (config.enabled === false) {
    // 显式禁用：不安装承载，也不触碰任何已有全局（未知 hook 保留）。
    //
    // 但仍要往页面里写一条**禁用标记**：cordis 的浏览器半区加载器是
    // `loader.create({ name })`，不带行配置，因此 client 半区看不到 `enabled: false`。
    // 没有标记时，被禁用的插件在 loopback 页面上（那里不需要承载）会把自己报成
    // available，桥也照旧导入——"禁用"在本地页面上等于失效（D23 实测）。
    ctx.effect(
      () =>
        ctx.on('webserver/index-inject', (table: IndexInjection[]) => {
          table.push({ kind: 'script', placement: 'head', text: buildDisabledHookScript() })
        }),
      'remote-attachments: disabled marker seat'
    )
    logger.info('附件能力已禁用；不安装上传承载，只写禁用标记供浏览器半区判定。')
    return
  }

  ctx.effect(
    () =>
      ctx.on('webserver/index-inject', (table: IndexInjection[]) => {
        table.push({ kind: 'script', placement: 'head', text: buildUploadHookScript() })
      }),
    'remote-attachments: upload hook seat'
  )
  logger.info(`已注册启动前上传承载注入（${UPLOAD_HOOK_GLOBAL} → ${UPLOAD_ENDPOINT}）`)
}

/** 插件名，供 Cordis 注册表与诊断使用。 */
export const name = 'remote-attachments'

export default { name, apply }
