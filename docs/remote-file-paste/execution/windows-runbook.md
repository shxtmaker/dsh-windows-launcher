# Windows 实机验证运行手册（D25 交接件）

本文件是 D25 交给 Windows 执行者的**运行手册**。它把 [windows-validation-plan.md](../windows-validation-plan.md)（下称"Windows 方案"）
第 2 节的输入材料表逐项落实成可执行步骤，并绑定到**同一候选**。任务与用例清单见
[windows-task-list.md](windows-task-list.md)；状态唯一真值仍是 [state.json](state.json)。
附件契约、生效限额、网络契约与诊断入口（Windows 方案 §2 材料表的其余几行）见 [§10](#10-附件契约限额网络与诊断入口材料表补全)。

> **口径纪律**：本手册描述的是**待执行**方案。DEV 轨道（Linux）已完成并记 `DevelopmentReady`；
> Windows 全部为 `WindowsPending`。本手册中的命令都**尚未在 Windows 上执行**，不得把本文件
> 的存在当作任何 Windows 用例已通过。正式发布、推送、npm 发布均需用户另行明确授权。

## 0. 交接件与候选身份

| 交接件 | 路径 | 说明 |
| --- | --- | --- |
| 交接清单（机器可读，权威绑定） | `artifacts/verify-portable/d25-handoff-manifest.json` | 候选/tarball/锁/Linux 证据/待验清单 |
| 插件候选 tarball | `plugins/dsh-remote-attachments/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz` | `sha256=f8961215a8c9dffcca44ff78b82a49e8f39708e5df59e84bad46a161b0bc9556`，59 文件，163 832 B |
| 包清单 | `artifacts/verify-portable/d22-package-manifest.json` | 干净安装证据 + 包内文件清单 + 运行期字节证明 |
| Linux 证据摘要 | `artifacts/verify-portable/portable-verify-summary.json` | runId `d24-2026-09-15T11-15-43-616Z-0a0f006c` |
| Linux 完整报告 | `artifacts/verify-portable/d24-linux-report.json` | L01–L13 覆盖表、零容忍控制、notRun 清单 |
| 源码身份 | 本仓库工作树（**未提交**） | branch `feat/remote-file-paste-attachments`，HEAD `df68795c3f7a0a34d6ed4fdd7804e369bdd0258b` |

**候选身份与失效规则**

- 工作树脏计数 37，`git status --porcelain=v1`（Trim 后）SHA-256 = `0b99fd98968999bb5173eea6c1865e9f614df27d7e12a2045beaf616afdc8c10`。
- 插件 `src` 树 18 个文件，逐文件指纹（路径+NUL+字节+NUL）= `c4597fe4e012c76571c8cde8211b23b0aef6698b8ffc366d4c8d8ae78b0dfbbe`。
- **任何**源码或工作树变化都会使 D24 证据与本清单失效：必须回到受影响任务重验，**不得**用旧报告充当新候选的通过证据（Windows 方案第 2 节拒收条件）。
- 仓库纪律提醒：`compatibility-lock.json` 的插件 tarball 摘要是 **D03 轮次快照**（`5d4016be…`），不随构建更新；**Windows 侧引用插件包摘要一律以本清单 `tarball.sha256`（`f8961215…`）为准**。

## 1. 上游身份与版本组合

| 项 | 值 | 来源 |
| --- | --- | --- |
| Harness CLI | `@deepseek-ai/dsh` `0.1.5-rc.1` | 本机全局已安装；未从源码构建 |
| 实际提供 UI 的 Harness 内部包 | `@deepseek-ai/dsh-base` / `dsh-web-app` 等 `0.1.5-rc.2` | CLI 自带 `node_modules`（实装事实） |
| 全家桶 | `@linxin666/dsh-web-all` `0.3.20` | `pnpm-lock.yaml` integrity |
| 远端 UI / 配对代理 | `@linxin666/dsh-remote-web-ui` `0.3.20` | 由全家桶依赖引入，**不单独安装** |
| 附件插件 | `@shxtmaker/dsh-remote-attachments` `0.1.0` | 本轮 tarball |

**已登记偏离（必须向 Windows 报告，不得隐去）**：研究快照为 `dsh-web 0.3.21` + `harness 0.1.5-rc.2` + commit
`0d78d391…`/`c291e796…`；本交付按用户决策"先沿用本机"使用 `0.3.20` + CLI `0.1.5-rc.1`。
本机**无** Harness / dsh-web 源码 checkout，两个源码 commit **未核实**。若日后对齐快照，必须重跑 D04–D13。

## 2. 工具依赖（测试机准备）

Windows 主机除 Windows 方案第 3.1 节的环境矩阵外，需要：

| 依赖 | 版本/要求 | 用途 | 缺失时 |
| --- | --- | --- | --- |
| .NET SDK | `10.0.400`（精确） | 构建与测试；`eng/verify.ps1 -DotNetPath` 指定 | 报环境未就绪，**不得**静默跳过 |
| PowerShell | `pwsh` 7.x（非 Windows PowerShell 5.1） | `eng/verify.ps1`、`eng/package.ps1` | 同上 |
| Node.js | 22+（锁定版本与 Linux 交接一致） | 生产互操作用例按 `eng/verify.ps1` 调用 Node 子进程 | 同上 |
| pnpm | 与 Linux 交接一致 | 插件依赖与打包（`plugins/dsh-remote-attachments`） | 同上 |
| Chromium / Playwright | 与 Linux 交接一致的固定版本 | C#→Node→Chromium 生产互操作用例（`production-interop-l13` 等价项） | 同上 |
| WebView2 SDK | `1.0.4129.50`（编制时值，以 `eng/release-constants.json` 为准） | 承载 | — |
| WebView2 Runtime | 实际版本从运行中的 WebView2 采集，**不得**用 Edge 版本推定 | 承载与 Runtime 异常用例 | — |
| WebView2 Bootstrapper / Standalone x64 | 冻结版本 + SHA-256 + 有效签名 | Offline/Online 安装 | 报缺件 |
| Inno Setup | `7.0.2`（`ISCC.exe`，Pyrsys B.V. 有效签名） | 正式打包 | 报缺件 |

## 3. 源码与待编译项

完整 solution `DshWindowsLauncher.slnx`（6 项目，6 份 `packages.lock.json`）：

| 项目 | TFM / RID | Linux 状态 | Windows 待办 |
| --- | --- | --- | --- |
| `src/DshLauncher.Core` | `net10.0` / 无 | 构建+546 用例真实执行 | 无需变更 |
| `tests/DshLauncher.Core.Tests` | `net10.0` / 无 | 546/546 通过（core 542 + interop 4） | 执行同一并集 |
| `src/DshLauncher.Platform.Windows` | `net10.0-windows` / `win-x64` | 仅交叉编译 | 实机执行 |
| `src/DshLauncher.Desktop` | `net10.0-windows` / `win-x64` | 仅交叉编译 | 实机执行（WPF/WebView2） |
| `tests/DshLauncher.Platform.Windows.Tests` | `net10.0-windows` / `win-x64` | **只枚举不执行** | 实机执行（WP/WW） |
| `tests/DshLauncher.Acceptance.Tests` | `net10.0-windows` / `win-x64` | **无法启动**（缺 `Microsoft.WindowsDesktop.App`） | 实机执行（WS/WB） |

本轮新增/改动的源码树（Windows 侧需一并编译）：

- `src/DshLauncher.Core/Attachments/`、`src/DshLauncher.Core/Remote/`（可移植）
- `src/DshLauncher.Platform.Windows/Attachments/`（真实 Win32，无 stub/无 `#if` 假实现）
- `src/DshLauncher.Desktop/Attachments/`（`RemoteAttachmentBridge/`、`NativePasteGestureRouter.cs`、`WebViewMessageTransport.cs`）
- `src/DshLauncher.Desktop/Remote/`（`RemotePageSession.cs`）
- 插件 `plugins/dsh-remote-attachments/`（TS host/client 双半区）
- 工程改动：`Directory.Build.props`、6 个 `.csproj`、`eng/{verify,verify-portable,package,package-internal,common}.ps1`、`eng/verification-profiles.json`、`eng/verification-tests.ps1`、`eng/expected-test-dataset.json`、新增 `DshWindowsLauncher.Portable.slnx`

**固定用例基线**（`eng/expected-test-dataset.json`）：Core.Tests 546、Acceptance.Tests 57、Platform.Windows.Tests 67；
本轮全部为**增量**更新（`removed=0`）。Windows 门禁必须按**同一清单**核对，缺工具报环境未就绪，
**不得**静默跳过新增用例。

## 4. 原生用例（已写、待实机执行）

| 来源文件 | 所在测试程序集 | 编号 | 数量 |
| --- | --- | --- | --- |
| `tests/DshLauncher.Platform.Windows.Tests/Attachments/WindowsPendingCases.md` | `DshLauncher.Platform.Windows.Tests` | `WP-01…WP-38`（xunit）+ `WM-01…WM-06`（实机准备） | 44 |
| `src/DshLauncher.Desktop/Remote/RemotePageSession.WindowsPending.md` | `DshLauncher.Acceptance.Tests`（`RemotePageSessionWindowsTests.cs` 载 WS-01…WS-10，`RemoteAttachmentBridgeWindowsTests.cs` 载 WS-11…WS-14） | `WS-01…WS-14`（xunit）+ `WM-07…WM-12` | 20 |
| `src/DshLauncher.Desktop/Attachments/NativePasteGesture.WindowsPending.md` | `DshLauncher.Acceptance.Tests`（`NativePasteGestureWindowsTests.cs`） | `WW-01…WW-06`（xunit）+ `WM-13…WM-32` | 26 |
| `src/DshLauncher.Desktop/Attachments/RemoteAttachmentBridge/RemoteAttachmentBridge.WindowsPending.md` | `DshLauncher.Acceptance.Tests`（`RemoteAttachmentBridgeWindowsTests.cs`） | `WB-01…WB-06`（xunit）+ `WM-33…WM-40` | 14 |

合计：**xunit 64 条**（`Platform.Windows.Tests` 38 条 = WP；`Acceptance.Tests` 26 条 = WW 6 + WS 14 + WB 6）、
**只能实机准备的手工用例 40 条**（WM-01…WM-40）。
这些编号同时映射到 Windows 方案的验收用例 `W-01`…`W-08.x`，映射表见
[windows-task-list.md](windows-task-list.md) 与各 `*.WindowsPending.md` 的"对应关系"小节。

## 5. 执行步骤（W00–W06）

每条用户执行指令**最多选择一个阶段**；一个阶段可继续多轮；发现缺陷先保存报告与恢复点，
不自动进入下一阶段、不自动开始安装下一产物、不自动发布。

### W00 接收、原生构建与首个安装

```powershell
# 1) 身份与基线（先核对第 0/1 节材料，再动手）
git -C <repo> rev-parse HEAD            # 期望 df68795c3f7a0a34d6ed4fdd7804e369bdd0258b
git -C <repo> status --porcelain=v1     # 期望 37 行（Trim 后 sha256 0b99fd98…）
Get-FileHash plugins/dsh-remote-attachments/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz -Algorithm SHA256  # 期望 f8961215…

# 2) 正式 Windows 门禁（完整 solution：locked restore + Release win-x64 + 全部原生测试）
pwsh -File .\eng\verify.ps1 -DotNetPath "$env:LOCALAPPDATA\DshWindowsLauncherDev\dotnet-10.0.400\dotnet.exe"
# 产出 verify-summary.json，必须绑定本候选；portable 摘要（portable-verify-summary.json）不是发布凭据

# 3) 正式打包（隔离干净 runner；模板见 eng/README.md 与 Windows 方案 §7.1）
pwsh -File .\eng\package.ps1 -Version 2.1.0 -PackageMode Offline ... -AllowInstallerExecutionForUninstallerVerification
pwsh -File .\eng\package.ps1 -Version 2.1.0 -PackageMode Online  ... -AllowInstallerExecutionForUninstallerVerification
```

通过标准：同一提交的 Windows 工程门禁 `PASS`；安装产物身份可核对；功能关闭/启用不破坏既有远程流程。

**W00 必须先处理的两个已知问题**（见第 7 节）：① `Platform.Windows.Tests` 反斜杠理论用例的
数据集转义隐患；② 插件包摘要以本清单 tarball 为准（`compatibility-lock.json` 是旧快照）。

### W01 粘贴与发送（W-02、W-03）

资源管理器/多文件/截图/焦点与 DPI；`W-02.1…W-02.8`；A-LAN 与 A-TLS × Cookie/免 Cookie 的真实上传与
主动发送闭环（本机快照 SHA-256 = 页面 `File` 字节 = 远端工具读回字节）。相关编号：`WW-01…WW-06`、
`WM-13…WM-32`、`WP-26…WP-34`。

### W02 身份与路径（W-04、W-05）

目标/草稿隔离、iframe/来源、原生手势、重解析点与不支持输入。相关编号：`WS-01…WS-14`、
`WM-07…WM-12`、`WP-01…WP-25`、`WP-35…WP-38`。

### W03 网络与恢复（W-06）

HTTP/HTTPS、Cookie/免 Cookie、断流、拒绝（413/401/403/5xx/200+`{ok:false}`）、撤销、取消、Harness 重启与
Renderer/App 重启后的语义。夹具：`plugins/dsh-remote-attachments/tests/fixtures/fault-proxy.mjs`
（Linux 侧故障注入代理，可按交接版本在 Windows 测试网络中使用）。相关编号：`WM-33…WM-40`。

### W04 资源与稳定性（W-07）

规定限额、长期混合使用、内存/磁盘/句柄、退出与暂存清理；沿用 Windows 方案 §5.7 的阈值与判定。
相关编号：`WP-01-windows-file-locks-acls`、`WP-02-windows-process-tree`、`WP-03-webview2-physical-boundary`
（D21 的三条 WindowsPending 资源项）。

### W05 完整安装与升级矩阵（W-08）

Windows 方案 §7.1–7.2：Offline/Online 干净安装、Runtime 缺失/旧版/损坏、包型互换、就地升级/修复、
卸载、功能回退、版本回退。候选改变时重验受影响的前序用例。

### W06 发布验收报告（RS-01…RS-15）

Windows 方案 §7.3、§8：对两种包分别执行 `eng/release-smoke.ps1 -ReleaseKind FirstRelease`；
汇总工程门禁、安装产物身份、W-01…W-08 结果、原有 RS 结果、支持/不支持版本组合与尚存问题。
`Human release confirmation: NO` 保持到发布方另行确认；**不自动发布**。

## 6. 安装与回退方案

- **功能关闭（首选回退）**：单独停用附件插件（`dsh plugin` 层面禁用本插件行），或使用本机附件能力开关。
  只关闭附件能力，**保留**全家桶配对、心跳与设备库（D23 兼容矩阵 `addon-disabled` 行已证明）。
- **不支持组合**：能力 fail closed（`unavailable` + `capability-disabled`），**不静默回退裸 `/api`**；
  内置图片路径与原厂基线同形。
- **ReloadRequired**：只恢复 `global` 变量不等于 runtime 恢复；必须提示并真实重载页面（D23 已双向验证）。
- **版本回退**：遵循现有禁止降级规则（安装器拒绝低版本）；可用功能开关或发布修复版本；
  **不得**恢复会复活已吊销设备的旧凭据快照；不清空设备库、不删除已发送远端文件。
- **数据**：普通卸载保留配对/目标数据；清数据只在有所有权证明的自有根内执行。
- **暂存**：只清理应用自有根（`%LOCALAPPDATA%\DshWindowsLauncher` 下的暂存根）内的文件；
  拒绝根外、重解析点与子目录；不递归清理系统 Temp 或用户真实目录。

## 7. 已知问题与决策点（Windows 侧必须先看）

1. **⚠️ `Platform.Windows.Tests` 基线转义隐患（W00 阻断项）**：4 条含反斜杠参数的理论用例，
   MTP 的 XML 输出把 `\` 再转义为 `\\`，而基线来自枚举 JSON 归一值 ⇒ 真实 Windows 上跑 XML 门禁会报
   `missing/unexpected`。修它会删除既有基线哈希，故 DEV 轨道按"只增不减"规则未动。
   **在此之前不得声称该程序集基线在 Windows 上可用**；W00 必须按真实 XML 重新推导或统一转义归一。
2. **插件包摘要以 D25 清单为准**：`compatibility-lock.json` 的插件条目是 D03 快照。
3. **上游版本偏离**：`0.3.20` + CLI `0.1.5-rc.1`（UI 包实为 `0.1.5-rc.2`）；源码 commit 未核实。
4. **`Session.promptError` 观测口径**：公开路径存在（客户端插件 `ctx.sessions.scope(id)` →
   `sessionOf(scope)` → `getSnapshot().promptError`）；当前黑盒测试**没有插件上下文句柄**，
   不得表述为"没有公开接口"。
5. **撤销语义**：证据只表述为"请求到达 Host 前被门控拒绝"（403 + `unpaired`），
   **不得**扩展为"已通过鉴权的上传流被主动终止"。
6. **D21 三条 WindowPending 资源项**与 **D22 四条打包/承载项**、**D23 `WindowsPackagingAndUpdate`**
   在 D24 门禁里是显式 `notRun`（白名单理由 `WindowsPending`），不计入任何 Linux 通过。

## 8. 证据采集与脱敏

目录组织、每用例记录字段、判定规则（错投目标/草稿、无用户动作读取、跨源泄漏、字节损坏、
重复自动发送、根外清理属阻断问题）一律按 Windows 方案 §6 执行。补充硬规则：

- 截图与日志**不得**含配对凭据、会话私人内容、完整本机路径或原始上传正文；URL 需展示时先隐藏查询与 fragment。
- 传输证据只保存路径模式、字节数、状态码与业务状态的**脱敏摘录**，不归档带凭据的 HAR。
- 结果只能取 `PASS / FAIL / BLOCKED / PENDING / N/A / RECORD`；`N/A` 必须附支持范围依据；
  环境不可用记 `BLOCKED`，**不能**算 `PASS`；没有真实运行的项目保留 `PENDING`。
- 每项结果必须绑定同一候选（HEAD + 脏计数 + 插件 tarball sha256 + `verify-summary.json`）。

## 9. 阶段结束时的记录要求

- 阶段结束记录 Windows 状态（各用例结果、新增 `FAIL`/`BLOCKED`、候选是否变化）与恢复点。
- Windows 轨道**不推进** DEV 任务；DEV 已在 D25 结束调度。
- 实机缺陷修复另登记代码任务，并复验受影响的 Linux 与 Windows 项；不临场修改 Harness 核心绕过失败。
- 正式发布 / 推送 / npm 发布均需用户**另行明确授权**，本手册不授权任何发布动作。

## 10. 附件契约、限额、网络与诊断入口（材料表补全）

本节补全 Windows 方案第 2 节材料表中"附件契约 / 限额与资源 / 网络契约 / 诊断入口"四行。

### 10.1 附件契约（v1，已冻结）

- 协议版本 `PROTOCOL_VERSION = 1`，schema 冻结在 `schemas/remote-attachments/v1/`
  （`schema.json` + `README.md` + 语言中立的 `expected.json` + `golden/`）。改动 schema 必须重跑 D10/D12/D13。
- **11 类消息**：`hello`、`capabilities`、`context`、`batch-begin`、`file-begin`、`chunk`、`ack`、`file-end`、
  `import-result`、`batch-end`、`cancel`。`additionalProperties:false`，整数范围与枚举显式声明。
- **身份绑定**：`operationId == batchId`；`fileId` 绑定 `targetId+documentEpoch+composerEpoch+sessionId+batchId`；
  `seq` 每文件从 0，`offset` 连续，单文件最多 2 块在途。
- **三态语义（验收点）**：`transport{idle,buffering,buffered}` / `draft{none,staged,failed,partial}` /
  `upload{none,harness-owned}`——枚举里**没有** `ready`/`uploaded`；`staged` 只表示对端草稿已接收字节并给出
  `attachmentIds`，**不保证 receipt 仍有效**（`ready` 是 Harness 上传层状态）。
- **错误码**：`ERROR_STAGES` 四阶段分开报告（`native-capture` / `protocol-transfer` / `draft-import` /
  `remote-upload`）；36 个协议拒绝码（`ERROR_CODES`）与 19 个结果码分工不混用。
- **输入所有权**：页面握手声明截图所有者（`native-paste` 或 `bridge`）；**同一动作只有一个消费者**
  （原生超时后桥不补导入）；v1 只有 `screenshot` 一个信号位，当前生产插件不含该特性 ⇒ 默认由 bridge 消费。
  同时具有文件列表与位图时**文件列表恒优先**。
- **取消策略**：客户端取消（观测为 `client-aborted`）与配对撤销门控（403 + `unpaired`，在进 Host 前拒绝）
  是两条独立路径；取消后同会话可开新批次，只有复用**同一** `batchId` 才是 `duplicate-operation`。
- **适配器接入**：客户端插件经 Cordis `ctx` 接入原生草稿/附件接口，**不依赖 React 私有状态或 DOM 猜测**
  （DOM 仅用于 file input 的 `change` 事件与卡片定位）。
- **页面全局**（均为页面内对象，不是可被任意网页调用的 API）：
  `__DSH_ATTACHMENTS_BRIDGE__`（`importFiles(...)`）、`__DSH_ATTACHMENTS_RECEIVER__`（分块接收）、
  `__DSH_ATTACHMENTS_ADDON__`（版本标记）、`__DSH_ATTACHMENTS_STATUS__`（诊断命名空间）。

### 10.2 生效限额、ACK 窗口与超时（`src/shared/protocol.ts`）

| 项 | 值 | 常量 |
| --- | --- | --- |
| 单文件上限 | 20 MiB | `DEFAULT_LIMITS.maxFileBytes` |
| 单批文件数 | 10 | `maxFilesPerBatch` |
| 单批字节 | 50 MiB | `maxBatchBytes` |
| 截图编码前像素 | 4000 万 | `maxScreenshotPixels` |
| 单目标暂存 | 100 MiB | `maxStagingBytesPerTarget` |
| 并发目标 | 2 | `maxConcurrentTargets` |
| 块大小 | 256 KiB | `CHUNK_BYTES` |
| 在途块 | 2 | `MAX_CHUNKS_IN_FLIGHT` |
| 握手上限 | 5 s | `WIRE_TIMEOUTS.handshakeMs` |
| 单块 ACK 上限 | 10 s | `ackMs` |
| `file-end` → `import-result` | 30 s | `fileEndMs` |
| 批次整体空闲 | 60 s | `batchIdleMs` |
| 重放/去重缓存 | 64 条 / 120 s / 活动操作钉住不可淘汰 | `REPLAY_CACHE_CAPACITY`、`REPLAY_CACHE_LIFETIME_MS` |

- **有效限额**还须取 Harness、模型图片能力与代理限制中的较小者（Windows 方案 §4）；实测值以页面诊断
  `__DSH_ATTACHMENTS_STATUS__.addon.status().handshake.effectiveLimits`（= `min(本端策略, 对端声明)`，
  含 `policyLimits` / `declaredLimits` / `clamps`）为准，`advertised.capabilities.limits` 是本端握手广告；
  **不得**用文档值推定运行时值。
- **暂存位置**：`<应用数据根>\attachments\staging\<targetId:N>`，其中应用数据根为
  `%LOCALAPPDATA%\DshWindowsLauncher`（自有根 + 所有权标记）。
- **清理时机**：释放/取消/目标移除/进程退出与启动清理；只删除自有根内由本功能拥有的普通文件，
  拒绝根外、重解析点与子目录（失败可恢复、有台账上界）。
- **功能关闭方法**：单独停用附件插件（或本机附件能力开关）。只关闭附件能力，**保留**全家桶配对、心跳与设备库；
  卸载/清数据按安装器契约执行（普通卸载保留应用数据）。

### 10.3 网络契约

- 主页面与附件上传都必须走 `/remote` 门控：上传路径为
  `POST <origin>/remote/api/session/uploadFileBinary`，认证为设备 Cookie（`dsh_pair`）或设备头
  （`x-dsh-remote-device`，调用时从 `sessionStorage` 读取）。
- **不静默回退裸 `/api`**：远端通道不可用时能力报 `unavailable` / `refused-no-remote-channel`（零请求），
  内置图片路径仍在原厂位置工作。
- 承载只对**同源且 `/api/` 前缀**改写（排除 `/api/pair/`、`/api/update/`）；设备头只对 `Headers` 实例 `set()`。
- HTTP LAN 与 HTTPS（含正常可信证书 / 反向代理）两种 origin 分别验证；Cookie 模式与免 Cookie 模式分别验证。
- 证据只记录认证模式、请求头是否存在、路径模式、字节数与状态码，**不记录**设备凭据、完整配对 URL、Cookie 值或正文（见 §8）。

### 10.4 诊断入口（默认不暴露任意本机读取）

- **页面诊断**：`window.__DSH_ATTACHMENTS_STATUS__`。host 段含 `enabled`（插件是否被显式禁用）；
  addon 段含完整 `status()`：`state`（`installed`/`disposed`）、`packageName`、`build`、`protocolVersion`、
  `origin` 事实、`capability{status,code,reason,missing,facts}`、`upload{hook,ownership,route}`、
  `advertised`（hello/capabilities）、`handshake`、三层状态 `transport`/`draft`/`upload`。
  只读诊断，**不提供任何任意本机路径读取接口**。
- **桥接面**：`__DSH_ATTACHMENTS_BRIDGE__.importFiles({sessionId?, files})` 与接收端 `__DSH_ATTACHMENTS_RECEIVER__`
  （仅页面内部投递）。两者都在页面上下文，不注册通用文件系统 host object，也不申请任意网页剪贴板权限。
- **原生表面**：公开成员只接受不透明 id（batchId/captureId/snapshotId），没有任何 Path 参数——
  由反射用例 WP-21…WP-25、WP-32、WP-37 在实机钉住。
- **来源校验**：只有规范化后逐字相等且是当前活动顶层文档的页面才获得能力；不存在"任意网页可调用的
  `readPath`"。诊断入口在普通网页中不出现，能力默认关闭。
