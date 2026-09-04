# ADR 0008：不签名任何自有产物，以哈希与清单绑定作为分发完整性契约

日期：2026-09-03

## 状态

已接受（取代 ADR 0004 中「受信任签名的安装包」与「签名验证」的部分；ADR 0004 关于按用户
EXE 安装、自包含发布、离线前置、固定卷与就地升级的决策继续有效）。

## 背景

产品只在内网（`http://192.168.3.100:3300/...` 自建 Gitea）按用户分发，接收方是同一局域网内
的自有机器。原契约要求一张代码签名证书：`package.ps1` 强制 `-SignToolPath` 与
`-CertificateThumbprint`，校验证书私钥、有效期、Code Signing EKU 与主体逐字匹配发布常量，
再签名 apphost、托管主程序集、安装器和卸载器，并要求 `release-smoke.ps1` 只接受带可信时间戳
的 `Valid` 签名。实际无法取得这样的证书，导致唯一可跑的正式路径是绕开 `package.ps1` 的
`package-unsigned.ps1`——而那条路径同时丢掉了安装器的 `[Code]` 安全加固。签名要求在这里
既没有提供真实的分发信任（内网用户本来就要在 SmartScreen/未知发布者面前自行确认），又把
真正有价值的加固绑在了它上面。

## 决策

1. **自有产物一律不签名**：安装包、`DshWindowsLauncher.exe`（apphost）、`DshWindowsLauncher.dll`
   与卸载器的 Authenticode 状态必须是 `NotSigned`。发布常量
   `distribution.signing.policy` 收敛为唯一取值 `none`；`certificateSubject` 与
   `timestampServerUri` 字段删除；`publisher` 保留但降级为纯安装器元数据。
2. **加固与签名解绑**：`installer/DshWindowsLauncher.iss` 保留全部 `[Code]` 安全加固
   （固定卷路径锁定、重解析点拒绝、当前用户 IPC 退出、目录句柄锁定、清数据双确认），
   只是不再走 Inno 的 `SignTool` / `SignedUninstaller` 回环。`package.ps1` 因此成为可用的正式
   入口，`package-unsigned.ps1` 退化为「轻量内网包」路径（无 `[Code]` 加固、无真实安装验证）。
3. **反向卡控**：打包与冒烟不再验证签名，而是**断言未签名**——`Assert-Unsigned`、
   `Test-DshReleaseHostEvidence` 与 `validate-installation.ps1` 都要求 `NotSigned`，
   拒绝任何 `Valid`/`HashMismatch`/`Unknown` 状态混进发布或就地升级前的身份校验。
4. **完整性契约改由哈希承担**：冻结 SHA-256、`package-manifest.json`（`schemaVersion` 3，
   `ownArtifactsSigned=false`）、`.sha256` 旁挂文件、SBOM 与验证摘要构成信任链；
   `release-smoke.ps1` 校验安装包、候选 EXE 与已安装 EXE 三者身份一致。
5. **上游签名校验保留**：WebView2 Runtime/Bootstrapper 必须是 Microsoft 有效签名，
   Inno Setup 编译器与官方安装包必须是 Pyrsys B.V. 有效签名并带可信时间戳。这些校验
   验证的是别人交给我们的字节，与「我们不签名自己的产物」不冲突。

## 后果

- 首次运行与卸载会出现 Windows 的未知发布者提示；文档与发行说明必须显式给出 SHA-256，
  由接收方自行比对。这是本决策明确接受的代价。
- 无法再声称「受信任签名」；ADR 0004 的 `Consequences` 中关于签名流水线与
  「新的完整签名安装包」的表述不再适用。
- 未来若要恢复签名，需要重新引入证书契约、`SignTool` 定义与 `SignedUninstaller`，并再次
  分拆 `distribution.signing`；届时 `NotSigned` 断言必须一并反转，不能只加不改。
- 验证影响映射的 `baselinePolicy` 从 `last-supported-signed-release` 改名为
  `last-supported-release`，发布常量 `verificationBaseline.lastSupportedReleaseCommit` 同步改名；
  历史基线提交里的 v4 常量（含 `signing.policy=optional/required`）仍可参与身份比较，
  因为该路径只使用跨版本稳定的 `pairingBaseline` 与 `product` 子对象。
