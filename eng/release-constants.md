# 发布常量清单

`release-constants.json` 是构建、打包、诊断版本信息和发布证据共同使用的版本化输入。`release-constants.schema.json` 固定字段、V1 常量和正式候选完整性规则。

模式版本 3 固定 `webUiCompatibility`、`verificationBaseline` 和显式签名策略。兼容身份固定契约、描述符 Schema、注册表及影响映射的规范化 SHA-256；基线只接受上一个已支持正式版本的真实 Git 提交。首个正式版本或无法确定基线时保持 `null`，验证影响推导必须 fail-closed 执行全部 VFY 和 RS，不得填写推测提交。

## 状态规则

- `development` 允许尚未由发布负责人确定的候选输入为 `null`，只能生成开发或内部测试产物。
- `candidate` 要求最低 WebView2 Runtime、离线安装器、随程序保留的 Evergreen Bootstrapper、Inno Setup、Publisher、官方发布地址和 `distribution.signing.policy` 全部有效。
- `distribution.signing.policy=optional` 允许证书主体和时间戳服务为 `null`，正式 Release 必须记录 `NotSigned` 并发布 SHA-256；`required` 则要求有效证书主体和 HTTPS 时间戳服务。
- 打包流程在正式模式下必须验证 JSON Schema、要求 `releaseStatus=candidate`，并拒绝策略未允许的 `null`、未识别字段或输入哈希不一致。
- 修改依赖、SDK、Runtime、安装器或签名策略后，必须按发布检查表重新执行受影响的验证集合。

## 固定值来源

| JSON 路径 | 值 | 来源 |
|---|---|---|
| `product.name` | `DSH Windows Launcher` | `REQ-DIST-001` 及发布检查表 |
| `product.executableName` | `DshWindowsLauncher.exe` | V1 固定基线 |
| `build.dotnetSdkVersion` | `10.0.400` | M0 依赖基线 |
| `build.targetFramework` | `net10.0-windows` | V1 WPF 技术基线 |
| `build.runtimeIdentifier` | `win-x64` | `REQ-PLAT-001` |
| `dependencyBaseline.harness` | `0a53fb55...` / `dsh-v0.1.2-alpha.2` | V1 固定基线 |
| `dependencyBaseline.harness.probeFingerprint.api` | `/api`、`401`、`12` bytes、`e9d83f01...53181af` | Harness 固定 commit 的 `packages/client/connection/src/index.ts` |
| `dependencyBaseline.harness.probeFingerprint.root` | `/`、`401`、`68` bytes、`3aad6226...42acaf` | Harness 固定 commit 的 `packages/client/connection/src/browser-auth.ts` |
| `dependencyBaseline.harness.legacyUnauthenticatedBaseline` | `@deepseek-ai/dsh 0.1.1-rc.2`；`/api` 为固定 `404`，`/` 为固定 `200` | 官方 npm 包在全新 `DSH_HOME`、默认 Web profile 下的两次稳定实测；仅用于识别并拒绝这一旧无认证基线 |
| `dependencyBaseline.lanPlugin.version` | `1.2.1` | V1 固定基线 |
| `dependencyBaseline.webView2Sdk.version` | `1.0.4129.50` | V1 固定基线 |
| `dependencyBaseline.webView2Runtime.minimumVersion` | `151.0.4129.50` | 已实测 Runtime 下限 |
| `dependencyBaseline.webView2Runtime.offlineInstallerVersion` | `1.3.263.3` | 已核实 Microsoft 签名的 Evergreen Standalone x64 输入 |
| `dependencyBaseline.webView2Runtime.bootstrapper.version` | `1.3.263.3` | 已核实 Microsoft 签名的 Evergreen Bootstrapper；随程序保留供原生修复页使用 |
| `dependencyBaseline.webView2Runtime.bootstrapper.sourceUri` | `https://go.microsoft.com/fwlink/p/?LinkId=2124703` | Microsoft 官方固定入口 |
| `dependencyBaseline.webView2Runtime.bootstrapper.sha256` | `94314d8b...e449146` | 2026-08-30 冻结下载哈希 |
| `distribution.innoSetup.appId` | `4440FC88-98CA-403E-8E20-3DFEBEF0E609` | V1 安装身份 |
| `distribution.innoSetup.version` | `7.0.2` | 固定 Inno Setup 正式版 |
| `limits.defaultTargetPort` | `3080` | `REQ-TGT-001` |
| `limits.activeTargetWindows` | `4` | 同时活动窗口承诺 |

## 发布身份

`distribution.signing.publisher`、`distribution.signing.policy` 和 `distribution.officialReleaseUri` 必须使用项目维护者确认的值。证书和时间戳只在策略为 `required` 时属于必填发布输入。

WebView2 Standalone 与 Bootstrapper 是不同用途的冻结输入。Standalone 只由完整安装包在程序文件替换前安装或修复共享 Runtime；Bootstrapper 固定安装为 `MicrosoftEdgeWebview2Setup.exe`，不由 Inno 执行，只供应用运行时原生修复页在联网且用户确认后调用。

签名凭据、私钥、token 和 Cookie 不属于发布常量，不得写入本文件或普通 runner 文件系统。
