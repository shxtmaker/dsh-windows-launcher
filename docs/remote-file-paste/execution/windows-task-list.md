# Windows 实机验证任务列表与待验清单（D25 交接件）

本文件是 D25 交给 Windows 执行者的**任务列表**，与 [windows-runbook.md](windows-runbook.md)（怎么跑）、
[windows-validation-plan.md](../windows-validation-plan.md)（验收用例定义）配套。
状态唯一真值仍是 [state.json](state.json)；本文件不预置任何通过结果。

> **总口径**：DEV 轨道全部任务已完成并记 `DevelopmentReady`；Windows 轨道**尚未开始**，
> 本文件所有条目状态为 `pending`。没有 Windows 机器**不**影响开发实现的完成度，但也不能替代 Windows 验收。

## 0. 状态图例

| 状态 | 含义 |
| --- | --- |
| `pending` | 尚未执行（本文件当前全部条目） |
| `pass` / `fail` | 已真实执行并判定（**不得**在未执行时填写） |
| `blocked` | 环境/输入缺件导致无法执行，须写明缺件 |
| `record` | Windows 10 兼容环境仅记录，不判通过 |
| `n/a` | 不适用，须附支持范围依据 |

## 1. 执行阶段任务（W00–W06）

| 阶段 | 任务 | 前置 | 交付 | 状态 |
| --- | --- | --- | --- | --- |
| W00 | 接收材料核对、W-01 身份基线、Windows 原生完整构建与自动门禁、首个安装包 | D25 交接 + 测试机就绪 | `verify-summary.json`、安装产物身份、反斜杠基线隐患处置结论 | pending |
| W01 | 粘贴与发送（W-02、W-03） | W00 | 资源管理器/多文件/截图/焦点/DPI；真实上传与主动发送内容闭环 | pending |
| W02 | 身份与路径（W-04、W-05） | W01 | 目标/草稿隔离、iframe/来源、原生手势、重解析点与不支持输入 | pending |
| W03 | 网络与恢复（W-06） | W01 | HTTP/HTTPS × Cookie/免 Cookie、断流、拒绝、撤销、取消、重启 | pending |
| W04 | 资源与稳定性（W-07） | W02、W03 | 限额、长期混合负载、内存/磁盘/句柄、退出与暂存清理 | pending |
| W05 | 完整安装与升级矩阵（W-08） | W04 | Offline/Online 干净安装、Runtime 异常、互换、升级、卸载、回退 | pending |
| W06 | 发布验收报告（RS-01…RS-15） | W05 | 完整证据、候选一致性、`ReleaseReady` 判定（不自动发布） | pending |

每条用户指令最多选一个阶段；阶段结束记录 Windows 状态与恢复点，不自动开始下一阶段。

## 2. 验收用例清单（Windows 方案第 5、7 节）

| 用例 | 内容 | 对应原生编号 | 状态 |
| --- | --- | --- | --- |
| W-01 | 安装前身份与基线 | — | pending |
| W-02.1 | 单文件粘贴（恰好 1 个待发送附件、不插入路径、不自动发送） | WW-01、WM-13 | pending |
| W-02.2 | 多文件（名称/大小/数量、同名不同内容不互相覆盖） | WP-26、WM-19/20/21 | pending |
| W-02.3 | 边界数据（空文件、256 KiB 前后、限额 ±1） | WP-38、WM-06 | pending |
| W-02.4 | 截图（10 次逐次移除，单次单附件、无双重导入） | WW-04/05、WM-19 | pending |
| W-02.5 | 图片格式竞争（FileDrop + 位图并存，优先级） | WP-28/29、WW-04 | pending |
| W-02.6 | 文本与组合格式 | WM-14 | pending |
| W-02.7 | 快捷键/菜单（Ctrl+V、Shift+Insert、右键、入口） | WW-01、WM-15/16 | pending |
| W-02.8 | 焦点与 DPI（搜索框/设置框/非编辑器；100/150/200%） | WW-02、WM-18 | pending |
| W-03 | 真实上传与主动发送闭环（A-LAN/A-TLS × Cookie/免 Cookie） | WM-06、WM-33 | pending |
| W-04.1 | 新草稿（无 session 首页明确拒绝、不自动建会话） | WW-03、WM-23 | pending |
| W-04.2 | 切会话（迟到 ACK 不改投） | WS-02、WM-36 | pending |
| W-04.3 | 多目标（A1/B1 不串目标；每目标独立 UDF） | WS-01、WM-10 | pending |
| W-04.4 | 新建/删除草稿（旧 operation 不恢复） | WS-05、WM-24 | pending |
| W-04.5 | 隐藏窗口（不创建新操作；在途按契约） | WS-07、WM-09 | pending |
| W-04.6 | 导航/刷新（导航开始即失效；新文档重新握手） | WS-02/03/09、WM-07/36 | pending |
| W-04.7 | 删除目标（取消 + 暂存清理；其他目标不受影响） | WS-05、WB-05 | pending |
| W-04.8 | 不支持会话（子代理/composer 不支持时提前拒绝） | WS-13、WM-24 | pending |
| W-05.1 | 无任意路径读取入口（无原生手势不读剪贴板/磁盘） | WP-18…WP-25、WW-02 | pending |
| W-05.2 | 来源变化（外链、协议/端口/主机、跨源重定向） | WS-03/09、WM-07 | pending |
| W-05.3 | frame 与后台（仅授权活动顶层编辑器） | WS-08、WM-12、WM-35 | pending |
| W-05.4 | 非预期消息（未知类型/畸形 JSON/超大 chunk/错误序号） | WM-34/35 | pending |
| W-05.5 | 本地来源（目录/UNC/链接/联接/云占位/ACL） | WP-03…WP-11、WP-26/33、WM-01/22 | pending |
| W-05.6 | 快照一致性（粘贴时稳定状态；变化即失败） | WP-01/02/13/14、WM-05 | pending |
| W-05.7 | 暂存清理（重解析点、失败可恢复、资源有界、不越根） | WP-15/16/17、WM-03/31 | pending |
| W-05.8 | 诊断与权限（无通用 host object/剪贴板权限；日志脱敏） | WP-21…WP-25 | pending |
| W-06.1 | 背压（暂停 ACK、乱序/重复 ACK） | WP-12、WM-06 | pending |
| W-06.2 | 断网/超时（不自动重发；成功项不重复） | WM-27/28/30 | pending |
| W-06.3 | 业务拒绝（413/401/403/5xx/200+`{ok:false}`/畸形） | WM-33 | pending |
| W-06.4 | 本地取消（各阶段取消；迟到结果不复活） | WS-05/06、WM-08 | pending |
| W-06.5 | 吊销配对（后续请求失效；其他设备正常） | WS-10 | pending |
| W-06.6 | Harness 重启（旧 ready receipt 失效并提示重加） | — | pending |
| W-06.7 | Renderer/App 重启（不重放；暂存回收） | WS-06/12、WM-11/37/38 | pending |
| W-06.8 | 插件失配（能力 fail closed；文本/配对继续可用） | WM-32、WM-40 | pending |
| W-06.9 | 全家桶升级 / 停用附加插件 | WB-06、WM-40 | pending |
| W-07 | 响应性、内存与磁盘（RSS/CPU/句柄/暂存上界） | `WP-01/02/03`（D21 资源项） | pending |
| W-08.1–W-08.10 | Offline/Online 安装、Runtime 异常、互换、升级/修复、路径、卸载、功能与版本回退 | `WindowsPackaging`、`WindowsVerifyPs1`、`RealWebView2Boundary`、`LauncherByteSource` | pending |

## 3. 原生用例清单（已写待跑）

| 编号段 | 程序集/文件 | 条数 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `WP-01…WP-25` | `Platform.Windows.Tests` / `WindowsPendingCases.md` | 25 | pending | D14 暂存与快照（含反射边界） |
| `WP-26…WP-34` | 同上（`WindowsClipboardPasteTests`） | 9 | pending | D16 真实剪贴板与拖放 |
| `WP-35…WP-38` | 同上（`WindowsStagedByteSourceTests`） | 4 | pending | D17 暂存快照 → 只读字节源 |
| `WW-01…WW-06` | `Acceptance.Tests` / `NativePasteGestureWindowsTests.cs`（清单见 `NativePasteGesture.WindowsPending.md`） | 6 | pending | D16 原生粘贴手势与输入所有权 |
| `WS-01…WS-10` | `Acceptance.Tests` / `RemotePageSession.WindowsPending.md` | 10 | pending | D15 页面会话生命周期 |
| `WS-11…WS-14` | 同上（`RemoteAttachmentBridgeWindowsTests`） | 4 | pending | D17 附件桥组件 |
| `WB-01…WB-06` | `Acceptance.Tests` / `RemoteAttachmentBridge.WindowsPending.md` | 6 | pending | D17 真实 WebMessage 双向与释放 |
| `WM-01…WM-06` | 手工/实机准备 | 6 | pending | 云占位、磁盘耗尽、崩溃残留、挂载点、粘贴快照语义、分块哈希贯通 |
| `WM-07…WM-12` | 手工/实机准备 | 6 | pending | 重定向链、renderer 崩溃、隐藏窗口渲染、UDF Cookie 隔离、进程退出、后台节流 |
| `WM-13…WM-32` | 手工/实机准备 | 20 | pending | 原生快捷键覆盖、焦点/标签、截图所有权、剪贴板占用、连按、超时、暂存根篡改、插件停用 |
| `WM-33…WM-40` | 手工/实机准备 | 8 | pending | 真实插件互通、投递形态、洪泛、导航、崩溃、关闭顺序、bridge 截图模式、配对保活 |

**在 Linux 上未执行的原因（两类，均不记通过）**：
① Windows 测试程序集只枚举不执行（真实 Win32/WebView2/WPF 语义不可在 Linux 复现）；
② `DshLauncher.Acceptance.Tests` 在 Linux 缺 `Microsoft.WindowsDesktop.App` 无法启动（退出码 150）。

## 4. 门禁级 `notRun` 清单（D24 权威轮，共 20 条）

### 4.1 `WindowsPending`（13 条，合法的 Windows 待办）

| id | 类型 | 理由 |
| --- | --- | --- |
| `DshLauncher.Platform.Windows` | Windows 项目 | Linux 只交叉编译（L12） |
| `DshLauncher.Desktop` | Windows 项目 | 同上 |
| `DshLauncher.Platform.Windows.Tests` | Windows 测试程序集 | 真实 Win32 用例；Linux 25 例中 15 例失败 |
| `DshLauncher.Acceptance.Tests` | Windows 测试程序集 | 缺 `Microsoft.WindowsDesktop.App`，无法启动 |
| `WindowsValidationAndDevelopmentReady` | Windows 层级 | 正式 DevelopmentReady 属 Windows 的 `eng/verify.ps1` |
| `WP-01-windows-file-locks-acls` | D21 资源项 | 真实 `CreateFileW` 独占/共享语义与继承 ACL |
| `WP-02-windows-process-tree` | D21 资源项 | WebView2 子进程树/作业对象/GPU 内存 |
| `WP-03-webview2-physical-boundary` | D21 资源项 | 真实 WebView2 物理消息边界下的资源采样 |
| `WindowsPackaging` | D22 | MSI/zip 正式打包 |
| `WindowsVerifyPs1` | D22 | 在 Windows 具 pwsh 环境运行 `eng/verify.ps1` |
| `RealWebView2Boundary` | D22 | 真实 WebView2 承载 |
| `LauncherByteSource` | D22 | Windows 侧字节源端口 |
| `WindowsPackagingAndUpdate` | D23 | 安装/更新流程 |

### 4.2 `justified-scope-limited`（7 条，显式白名单的受限场景）

| id | 归属 | 未验原因 |
| --- | --- | --- |
| `G21b-real-app-inside-subframe` | D20 | 真实应用在子框架内会 frame-bust 顶层文档，会毁掉夹具落点单元；子框架无能力已由 G19/G21/G22 + D15 策略覆盖 |
| `G33-busy-turn-is-real` | D20 | 点击发送后 30 秒内未观察到"停止生成"或非 plain 阶段；忙碌阶段拒绝导入未验证 |
| `G38-subagent-session-specific-state` | D20 | 夹具会话列表无可用子代理会话；只覆盖 context-changed |
| `RealAllBundleUpdate` | D23 | 未对真实 npm 源或生产执行更新（只从 lock/manifest 推理） |
| `UnverifiedUpstreamCombination` | D23 | `0.3.21/0.3.22` + `0.1.5-rc.2` 未安装 |
| `MarketplaceUpdateFlow` | D23 | 未走市场更新流 |
| `RealIndependentScopePluginUpdate` | D23 | 未安装第二个插件版本 |

> `unjustified = 0`。门禁默认拒绝：非 `WindowsPending` 且非白名单的 `notRun` 一律判失败。

## 5. W00 必须先做的两个决策/修复任务

| id | 内容 | 触发条件 | 状态 |
| --- | --- | --- | --- |
| W00-A | `Platform.Windows.Tests` 反斜杠理论用例基线转义：按真实 XML 重新推导或统一 `\`→`\\` 归一 | 真实 Windows XML 门禁报 `missing/unexpected`（4 条） | pending（**W00 阻断项**） |
| W00-B | 插件包摘要统一：Windows 侧引用 `d25-handoff-manifest.json` 的 `tarball.sha256`（`a6f865a4…`），忽略 `compatibility-lock.json` 的 D03 快照值 | 任何引用插件包摘要的位置 | pending |

## 6. 开发轨道收尾状态（本文件不改变）

- DEV 叶任务：**D00–D24 全部 done**（25 项）+ R15 修复轮 done；**D25（本任务）done**，开发调度结束。
- Linux 完整门禁：**32/32 pass、incomplete 0、fail 0**（runId `d24-2026-09-15T11-15-43-616Z-0a0f006c`），
  `portableStatus=pass`、`crossBuildStatus=pass`（独立判定）。
- 口径：`portableStatus=pass` **不**等于"所有测试通过"，更**不**等于可发布；
  `releaseEligible` 恒为 `false`；`eng/verify.ps1` 的 `verify-summary.json` 才是正式门禁摘要，
  `portable-verify-summary.json` 不是发布凭据。
- Windows：全部 `pending`，**不允许**任何未执行项被记 `PASS`。
