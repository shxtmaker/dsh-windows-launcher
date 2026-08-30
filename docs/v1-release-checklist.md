# DSH Windows Launcher V1 发布检查表

文档编号：`DSHWL-V1-REL`  
文档版本：`1.0`  
状态：已确认  
性质：独立发布交付文档  
更新日期：2026-08-29  
关联交付物：`v1-implementation-plan.md`

## 1. 文档定位与固定基线

本文件可以脱离规划票据、研究记录和对话独立执行。表格中的 `REQ-*` 只用于与开发方案追踪；每个 `VFY-*` 和 `RS-*` 项目已经在本文件内给出完整操作与通过条件，不要求执行者另行查阅需求正文。

| 项目 | V1 固定值 |
|---|---|
| 产品 | `DSH Windows Launcher` |
| 主程序 | `DshWindowsLauncher.exe` |
| Inno Setup `AppId` | `4440FC88-98CA-403E-8E20-3DFEBEF0E609` |
| 正式平台 | Windows 11 25H2+ x64 |
| 记录性兼容平台 | Windows 10 22H2 x64 |
| 正式产物 | 签名的按用户 EXE 安装包，.NET 10 自包含 `win-x64` |
| Harness | commit `cd5ef8148158c3a752a658978873241fdf8e2bbc`，`dsh-v0.1.2-alpha.1` |
| LAN 插件 | `dsh-web-lan-access 1.2.1` |
| WebView2 SDK | `1.0.4129.50` |
| Linux 参考系统 | Ubuntu Server 24.04 LTS x64 |
| 正常目标 | `A:3080`、`A:3180`、`B:3180`、`B:3280` |
| 默认端口占用夹具 | `B:3080` |
| 资源参考机 | 4 逻辑处理器、8 GiB RAM、SSD |
| 同时活动窗口承诺 | 四个窗口，30 分钟 |

精确 .NET SDK、Inno Setup、最低 Evergreen WebView2 Runtime、Publisher、证书主体和 HTTPS 发布地址以候选版本发布常量为准。任一值缺失或仍是占位符时，不得形成正式候选版本。

## 2. 最小发布流程

每个正式候选版本只执行三个顺序步骤：

1. 运行 `eng/verify.ps1`，完成源码、自动化、架构和供应链统一门禁。
2. 运行 `eng/package.ps1`，固定并签名唯一安装包，计算 SHA-256。
3. 对该精确安装包运行 `eng/release-smoke.ps1`，完成下表中适用的实机冒烟，生成 `release-evidence.md`，再进行一次人工发布确认。

第 2 步必须先完成所有签名和可信时间戳，再冻结最终安装包 SHA-256。签名后不得重新打包、改写资源或修改任何字节。

本流程不要求自托管 CI，不维护 L1–L4 或 P0/P1/P2 分类，不默认采集截图或录像，也不自动上传或公开发布。CI 如存在，只能调用相同脚本。

### 2.1 内部测试安装包不属于发布流程

`eng/package-internal.ps1` 生成的安装包只用于 `releaseStatus=development` 阶段的内部测试。
它具有独立产品名、EXE 名、Inno Setup `AppId`、安装目录、应用数据根和单实例身份；文件名、
产品元数据及 manifest 必须同时标记 `INTERNAL TEST` 和 `UNSIGNED`。该入口仍须运行统一
`verify.ps1`，绑定同一个 clean Git 提交，并验证冻结 WebView2 Bootstrapper 的哈希、版本、
Microsoft 签名和可信时间戳。

内部测试安装包及其主程序、卸载器均未签名，不得作为正式候选版本，不得运行本检查表第
2 步或第 3 步，也不得填写人工发布确认。仅在当次获得明确上传授权后，才可作为
`prerelease=true` 的预发布上传；标题、正文和文件名必须完整保留 `INTERNAL TEST`、
`UNSIGNED`、`NOT FOR PRODUCTION USE`，且不得设为 latest 或正式发布。内部打包脚本只生成
安装包、确定性 manifest、校验和、验证摘要和 SBOM，不自动执行安装或卸载。任何内部测试
结果均不替代正式签名安装包的 `RS-01` 至 `RS-15` 证据。

## 3. 候选版本输入

开始前必须具备：

- 候选版本号和提交哈希；
- 已补齐的发布常量；
- 固定 Harness、LAN 插件、WebView2 SDK 和最低 Runtime 基线；
- 唯一候选安装包及其 SHA-256；
- 有效的安装器、自有 EXE、卸载器 Authenticode 签名和可信时间戳；
- Windows 11 干净虚拟机快照、Windows 11 实体参考机、Linux A 和独立地址的 Linux B；
- 不含长期 token 的幂等错误服务夹具；
- 本次改动范围及由第 6 节确定的附加重跑集合。

安装包哈希一旦变化，既有实机结果全部失效。

## 4. 自动验证门禁

运行：

```powershell
pwsh -File .\eng\verify.ps1
```

脚本必须以非零退出码阻止以下任一失败。自动数据集使用下列八个稳定组，并在测试元数据中记录 `triggerTags`；适用夹具缺失或必测项被跳过也视为失败。

| 组 | 自动覆盖 |
|---|---|
| `VFY-01` 构建与代码质量 | 精确 SDK、locked restore、格式、nullable、analyzers、警告即错误、Release `win-x64`、版本和发布常量。 |
| `VFY-02` 目标领域模型 | IPv4/端口有限输入、端点去重、不可变 `targetId`、唯一默认目标、同目标互斥和多目标并发交错。 |
| `VFY-03` 持久化与删除 | 原子快照、备份、损坏及未知模式、迁移、配对标记、忘记崩溃点、旧备份防复活、所有权和重解析点保护。 |
| `VFY-04` 探测与网络 | RFC1918、公用网络、无凭据探测、响应与超时上限、`401/401` 指纹、非 HTTP、错误 `200`、`3xx`、`403`、`404` 和旧无认证服务。 |
| `VFY-05` 配对与凭据 | URL 解析、精确 authority、三阶段提交、中断与并行隔离、剪贴板竞态和全链路脱敏。 |
| `VFY-06` WebView 宿主 | origin、弹窗、协议、下载、权限、证书、无桥接/DevTools、renderer/browser 恢复及不重放。 |
| `VFY-07` Windows 与安装 | 单实例、窗口、逐目标 UDF、固定卷、危险路径拒绝、安装配置、诊断白名单、`.resx` 和可访问性元数据。 |
| `VFY-08` 供应链与证据 | 来源和版本锁、输入二进制哈希与签名、许可证、SBOM、漏洞、秘密扫描、结果模式和敏感字段扫描。 |

这些组共同包含：

- locked restore、依赖漂移或来源验证；
- `dotnet format --verify-no-changes`；
- nullable、警告视为错误和 analyzers；
- Release `win-x64` 构建；
- 目标规范化、默认目标、原子快照、备份恢复、迁移、忘记事务和跨目标隔离数据集；
- 探测、配对 URL、事务中断、会话失效、网络分类和凭据清理数据集；
- WebView 导航、外链、权限、下载、证书、快捷键和进程恢复适配器数据集；
- 安装目录资格、固定卷、重解析点、升级、修复、卸载和数据清除数据集；
- 架构规则，特别是 `DshLauncher.Core` 不得引用 WPF、WebView2 或 Windows 适配器；
- 第三方许可证、SBOM、秘密扫描，以及未处理的已知可利用 Critical/High 漏洞。

原 85 个基线场景作为参数化自动测试数据保留，不要求人工逐行签署。自动结果文件与候选报告共同保存。

## 5. 最终安装包实机冒烟

首次正式发布执行全部 15 项。后续普通补丁至少执行标为“每次”的项目；条件项按第 6 节触发。所有“每次”项目针对同一个最终安装包哈希。

| ID | 频率与环境 | 操作与通过条件 | 主要需求 |
|---|---|---|---|
| `RS-01` | 每次；Windows 11 VM | 校验签名和时间戳已经完成，再冻结文件名、大小、版本和 SHA-256；核对 Publisher、SBOM 和依赖基线。安装包、自有 EXE、卸载器身份一致，此后不得改变任何字节。 | `REQ-DIST-001`、`REQ-DIST-002` |
| `RS-02` | 每次；干净 Windows 11 VM | 以标准用户从默认目录全新安装并启动真实 `DshWindowsLauncher.exe`。无提权、无后台进程；开始菜单和卸载登记正确。 | `REQ-PLAT-002`、`REQ-DIST-001` |
| `RS-03` | 首版或安装器/Runtime 变化；Windows 11 VM | 在 Runtime 已满足、缺失、低于下限和完全离线四种快照验证检测、离线安装及失败前不替换旧程序。自定义空目录仅接受本地固定卷，拒绝非空、UNC、网络盘、可移动盘和不明重解析路径。 | `REQ-DIST-003` 至 `REQ-DIST-006` |
| `RS-04` | 每次；Windows 11 实体机 | 空目录进入添加流程；重复启动转交既有进程。目标存在后只打开默认目标，同一目标重复打开只激活现有窗口。用键盘完成核心流程，并抽查高对比度及 100%、150%、200% DPI 无阻断。 | `REQ-PLAT-003` 至 `REQ-PLAT-006` |
| `RS-05` | 每次；Windows 11 实体机、Linux A | 输入 Linux A 的 IP，留空端口使用 `3080`；完成探测、信任确认和 authority 完全一致的 LAN 链接配对。验证 token 请求 `303`、干净根页面和认证 API 后进入 Harness UI。重启应用后复用会话自动连接。 | `REQ-NET-004` 至 `REQ-NET-006`、`REQ-PAIR-001` 至 `REQ-PAIR-006` |
| `RS-06` | 每次；Windows 11 实体机、Linux A/B | 保存 `A:3080`、`A:3180`、`B:3180` 和 `B:3280`。验证同主机不同端口、不同主机使用相同端口、唯一默认目标、显式切换默认、窗口去重和逐目标会话隔离；一个目标离线不影响其他窗口。忘记非默认目标后其他会话保持，忘记默认目标时按剩余数量选择或自动继任。 | `REQ-TGT-002` 至 `REQ-TGT-007`、`REQ-DATA-001` 至 `REQ-DATA-005` |
| `RS-07` | 首版或网络/探测/插件变化；Windows 11 实体机与夹具 | 让 `B:3080` 分别承载非 HTTP 和错误 HTTP `200`，让 Harness 使用明确的替代端口；验证拒绝错误服务且不扫描端口。再验证 `403`、旧无认证服务、非 RFC1918 输入和 Windows 公用网络阻断。 | `REQ-NET-001` 至 `REQ-NET-008` |
| `RS-08` | 首版或配对/数据变化；Windows 11 实体机 | 验证错误、过期、重复、loopback、跨目标和含额外参数的配对链接均不发送到错误目标。确认输入与内存 token 清除，当前剪贴板仅在内容未变化时尽力清空；取消、崩溃恢复和 `401` 只影响当前目标。 | `REQ-PAIR-002` 至 `REQ-PAIR-010` |
| `RS-09` | 每次；Windows 11 实体机、Linux A | 在真实 Harness 中创建或打开 Session，完成完整流式回复、持续 WebSocket、空闲后继续交互、页面内复制及 Linux 工作目录浏览。启动器不替代 Harness 业务。 | `REQ-WEB-003`、`REQ-WEB-010` |
| `RS-10` | 每次；Windows 11 实体机 | 验证跨 origin 导航被阻止；外部 HTTP/HTTPS 和 `mailto:` 仅由用户触发并逐次确认后交给系统；所有下载、弹窗、未知协议、权限、证书绕过、DevTools 和原生桥接入口被阻断。主动导出原生诊断 ZIP，并确认其中无 token、Cookie、页面、图片或剪贴板内容。 | `REQ-WEB-002` 至 `REQ-WEB-009`、`REQ-DIAG-001` 至 `REQ-DIAG-005` |
| `RS-11` | 每次；Windows 11 实体机、Linux A/B | 从 Explorer 拖入单图和多图，粘贴截图及文本图片混合内容；验证附件只进入当前目标当前 Session。不支持格式和非图片保持 Harness 原生错误；刷新或重建后不自动重复提交。 | `REQ-IMG-001` 至 `REQ-IMG-005` |
| `RS-12` | 首版或 WebView/Runtime/Harness 变化；Windows 11 实体机 | 验证网络中断恢复、主导航失败、renderer 失败、browser 进程失败和无响应处理。恢复复用对应 UDF，不清其他会话、不切换默认目标、不重放业务写入。 | `REQ-WEB-010` 至 `REQ-WEB-012` |
| `RS-13` | 每次；4 逻辑处理器、8 GiB RAM、SSD 的 Windows 11 实体参考机 | 同时打开四个不同目标窗口并混合使用 30 分钟。无崩溃、无无响应、无跨目标污染，四窗口均可继续操作；启动器 PID 及其 WebView2 子进程树 `Private Bytes` 峰值不超过 2.5 GiB，最后五分钟稳定空闲平均 CPU 不超过 10%，关闭后 60 秒内释放全部对应 UDF 文件锁。 | 同时活动窗口承诺 |
| `RS-14` | 每次含普通卸载；升级/修复部分在首版或安装器/数据模式变化时执行；Windows 11 VM | 验证同版修复、从固定且签名的前一 RC 到当前候选的就地升级、拒绝降级、运行中请求正常退出、已知数据迁移及未知模式失效保护。普通卸载保留目标和会话，重装后仍可使用；数据清除卸载只删精确应用数据根且不移除共享 Runtime。检查安装日志成功删除、失败脱敏保留。 | `REQ-DIST-007` 至 `REQ-DIST-015` |
| `RS-15` | 首版及平台/.NET/WebView2/安装器变化；Windows 10 22H2 x64 | 记录真实 EXE 启动、连接已配对目标和图片拖放/粘贴结果。Windows 10 专属失败不阻止发布；若同一缺陷影响安全、凭据或数据完整性，则按共同缺陷阻止发布。 | `REQ-PLAT-001`、`REQ-IMG-001` |

四小时浸泡、6/8 窗口、窗口激活 P95 和关闭后内存回落比例不是发布必做项。只在性能相关改动、资源回归或专项调查时运行并记录。

`RS-13` 以五秒为默认采样间隔，进程树口径和采样原始值写入临时脱敏结果。首次正式发布的 15 项均为必须项，`FAIL` 或 `SKIP` 都阻止发布；Windows 10 的功能结论记为 `RECORD`，但其揭示的共同安全或数据缺陷仍阻止发布。

## 6. 按改动范围追加重跑

| 改动范围 | 除“每次”项目外追加 |
|---|---|
| 首个 `1.0.0` 正式发布 | 全部 `RS-01` 至 `RS-15`，并执行所有自动参数化数据集。 |
| 安装器、发布常量、签名、.NET 自包含 Runtime、WebView2 前置 | `RS-03`、`RS-14`、`RS-15` 及完整安装生命周期数据集。 |
| 目标目录、模式版本、迁移、备份、忘记事务 | `RS-08`、`RS-14` 及完整数据损坏、崩溃点和删除安全数据集。 |
| 网络、探测、配对或 Windows 网络类别 | `RS-07`、`RS-08` 及完整地址、状态码、重定向、凭据和并行隔离数据集。 |
| Harness、LAN 插件或能力指纹基线 | `RS-05`、`RS-07`、`RS-08`、`RS-09`、`RS-11`、`RS-12`；重新核实源码契约和所有固定指纹。 |
| WebView2 SDK、最低 Runtime、内容宿主或权限策略 | `RS-09` 至 `RS-12`、`RS-13`、`RS-15` 及完整导航、权限、下载、进程和剪贴板数据集。 |
| 图片附件相关页面契约 | `RS-11` 和多目标隔离、重建不重试数据集。 |
| 多窗口、UDF 或进程生命周期 | `RS-06`、`RS-12`、`RS-13`，必要时额外执行 6/8 窗口专项观察。 |
| 仅文档且不影响构建输入或行为 | 仍运行 `eng/verify.ps1` 的适用静态检查；无需重新生成发布物。 |

若一次改动同时命中多行，取 `triggerTags` 的并集。无法可靠判断影响范围时执行全部矩阵。不能用开发者主观判断跳过安全、凭据、数据删除或跨目标隔离检查。

## 7. 发布验证报告

`eng/release-smoke.ps1` 生成并维护一份 `release-evidence.md`。最小内容如下：

```md
# Release evidence

- Version:
- Commit:
- Worktree state:
- Installer file:
- Installer size:
- Installer SHA-256:
- Installer signature subject and timestamp:
- Installed EXE path, version and SHA-256:
- Dependency baseline:
- Actual .NET, Inno, WebView2 Runtime and offline installer versions:
- Windows 11 VM snapshot:
- Windows 11 physical reference:
- Linux A/B baseline:
- verify.ps1 start/end/result: PASS | FAIL
- Automated result files and SHA-256:
- Executed smoke IDs and PASS/FAIL/RECORD:
- RS-13 Private Bytes, CPU and UDF lock release:
- release-smoke.ps1 start/end/result: PASS | FAIL
- Failures, root cause, fix commit and rerun link:
- Windows 10 informational result:
- Human release confirmation: YES | NO
- Confirmation time:
- Confirmed installer SHA-256:
- Sensitive-data exclusion checked: YES | NO
```

只永久保存该报告和自动测试结果。默认不采集截图或录像。原始失败日志只保留到下一次成功发布或最多 90 天，取较早者；任何日志均不得包含 token、Cookie、图片、剪贴板、页面内容或请求响应体。

## 8. 阻止发布的条件

出现以下任一情况时，不得进行人工发布确认：

- `eng/verify.ps1` 或任一适用 `RS-*` 失败；
- 候选安装包哈希与报告不一致；
- 签名、可信时间戳、Publisher、版本或发布常量缺失或不一致；
- 出现未处理的已知可利用 Critical/High 漏洞、依赖漂移或秘密扫描命中；
- token、Cookie、图片、页面内容或剪贴板进入日志或证据；
- Windows 11 上出现崩溃、无响应、跨目标污染、错误服务误认、会话串用、数据损坏或危险删除；
- 四窗口承诺未达到；
- Harness、插件、SDK、Runtime 或安装包变化后，受影响结果没有重跑。

偶发失败不能由一次重跑通过直接覆盖。必须保留失败摘要，完成根因归类或修复，再生成新的有效结果。

## 9. 人工发布确认

全部适用门禁通过后，由有权执行发布的人核对 `release-evidence.md` 中的版本、提交和安装包 SHA-256，并把 `Human release confirmation` 改为 `YES`、记录时间。此确认只表示该精确产物可以进入另行授权的发布动作。

本检查表不授权签名凭据使用、上传文件、创建公开 Release 或发送发布消息。任何远程发布仍需当次明确授权。

## 10. 独立交付说明

- 本文件是发布和验证的唯一规范入口，可单独复制给测试或发布执行者。
- `v1-implementation-plan.md` 是开发配套文档，但缺失时不影响本文件中任何 `VFY-*` 或 `RS-*` 项目的执行和判定。
- `.scratch` 票据、研究报告、原型、ADR、截图和对话都不是发布输入。
- 正式交接只需携带本文件和 `v1-implementation-plan.md`；实际执行产生的 `release-evidence.md` 属于版本证据，不属于长期维护的第三份规范。
