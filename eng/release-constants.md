# 发布常量清单

`release-constants.json` 是构建、打包、诊断版本信息和发布证据共同使用的版本化输入。`release-constants.schema.json` 固定字段、常量和正式候选完整性规则。

模式版本 4 固定 `pairingBaseline`（DSH 远程访问配对契约）、`verificationBaseline` 和显式签名策略。`pairingBaseline` 固定插件名、参考版本、设备 Cookie 名、accept/heartbeat/status 路径、25 秒在线窗口与默认心跳间隔；基线只接受上一个已支持正式版本的真实 Git 提交。首个正式版本或无法确定基线时保持 `null`，验证影响推导必须 fail-closed 执行全部 VFY 和 RS，不得填写推测提交。

## 状态规则

- `development` 允许尚未由发布负责人确定的候选输入为 `null`，只能生成开发或内部测试产物。
- `candidate` 要求 Inno Setup、Publisher、官方发布地址和 `distribution.signing.policy` 全部有效；`pairingBaseline.defaultHeartbeatIntervalSeconds` 必须严格小于 `onlineWindowSeconds`，且 `PairingProtocol.cs` 的实现必须与常量逐字对齐（verify 与测试双向校验）。
- `distribution.signing.policy=optional` 允许证书主体和时间戳服务为 `null`，正式 Release 必须记录 `NotSigned` 并发布 SHA-256；`required` 则要求有效证书主体和 HTTPS 时间戳服务。
- 打包流程在正式模式下必须验证 JSON Schema、要求 `releaseStatus=candidate`，并拒绝策略未允许的 `null`、未识别字段或输入哈希不一致。
- 修改 SDK、配对契约、安装器或签名策略后，必须按发布检查表重新执行受影响的验证集合。

## 固定值来源

| JSON 路径 | 值 | 来源 |
|---|---|---|
| `product.name` | `DSH Windows Launcher` | 发布检查表（产品身份延续） |
| `product.version` | `2.0.0` | 配对集中端重构后的首个候选版本 |
| `build.dotnetSdkVersion` | `10.0.400` | 依赖基线（V1 延续） |
| `build.targetFramework` | `net10.0-windows` | WPF 托盘宿主技术基线 |
| `build.runtimeIdentifier` | `win-x64` | 平台基线 |
| `pairingBaseline.plugin` | `@linxin666/dsh-remote-web-ui` | dsh-web「DSH 远程访问」插件 |
| `pairingBaseline.referenceVersion` | `0.3.10` | 协议核验所用的插件参考版本 |
| `pairingBaseline.cookieName` | `dsh_pair` | 插件默认设备 Cookie 名 |
| `pairingBaseline.acceptPath` / `heartbeatPath` / `statusPath` | `/api/pair/...` | 插件 `/api/pair` 路由族（见 docs/pairing-hub.md） |
| `pairingBaseline.onlineWindowSeconds` | `25` | 插件默认 `offlineAfterMs` |
| `pairingBaseline.defaultHeartbeatIntervalSeconds` | `10` | 保活默认间隔（< 在线窗口） |
| `distribution.innoSetup` | `7.0.2`（冻结 SHA-256） | 安装器工具基线 |
| `limits.webUiPort` | `4780` | 独立 Web 管理页面默认回环端口 |
| `limits.maxTargets` | `32` | 集中端目标数量上限 |

V1 的 `webUiCompatibility`、`dependencyBaseline`（Harness 指纹、LAN 插件、WebView2 SDK/Runtime）随 WebView2 壳架构一并移除：产品不再内嵌第三方 WebUI，配对连接完全由 Harness 侧远程访问插件提供（见 docs/adr/0006 与 docs/pairing-hub.md）。
