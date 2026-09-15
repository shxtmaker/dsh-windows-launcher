/**
 * 附件附加插件 —— host 半区入口。
 *
 * 该文件是包的 `.` 导出（`lib/host.js`）的来源：保持根级入口文件，避免 tsc 因
 * `rootDir` 把入口下沉到 `lib/host/index.js`，从而让 `main`/`exports` 与真实产物一致。
 */

export {
  apply,
  name,
  snapshotCapabilities,
  type CapabilitySnapshot,
  type HostConfig
} from './host/index.js'

export { default } from './host/index.js'
