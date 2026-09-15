# D15 Windows 待执行用例清单（WindowsPending）

本文件登记 D15「局部提取远程页面生命周期」中**只能在 Windows 实机执行**的用例。
本轮没有 Windows 机器：下面的用例**一条都没有运行过**，因此不得把任何一条记为 PASS。

Linux 侧本轮真实执行的是平台中立规则（来源规范化/比较、主文档来源判定、外部链接不继承能力、
代际推进与迟到消息拒绝、取消触发、挂起语义、跨目标隔离）与交叉编译：

- 生产规则：`src/DshLauncher.Core/Remote/`（`RemoteOrigin`、`RemotePageOriginPolicy`、`RemotePageSessionState`）
- Linux 已跑通的用例：`tests/DshLauncher.Core.Tests/Remote/`（`RemoteOriginTests`、
  `RemotePageOriginPolicyTests`、`RemotePageSessionLifecycleTests`、`RemotePageSessionIsolationTests`）
- Windows 侧生产代码：`src/DshLauncher.Desktop/Remote/RemotePageSession.cs`（真实 WPF/WebView2，无 stub、无 `#if` 假实现）
- Windows 侧用例：`tests/DshLauncher.Acceptance.Tests/EndToEnd/RemotePageSessionWindowsTests.cs`

**关键边界**：`RemotePageSession` 只把真实 WebView2 事件翻译成 Core 规则的输入。
"事件何时到达、以什么顺序到达、隐藏窗口/切标签时渲染进程如何表现"这三件事没有任何 Linux 证据，
因此下面的 WS 用例全部待验，并且**不构成对 Linux 规则测试的替代**。

## 0. 执行前准备与命令

1. Windows 11 支持版本（或登记的 Windows 10 22H2 兼容环境）、x64、标准用户。
2. 必须装有 **WebView2 Evergreen Runtime**（与产品安装包要求一致）；本清单不覆盖"运行时缺失"路径。
3. 用例自带 Kestrel 回环主机（可信来源 + 外部来源）与内存目标库，不需要外部 Harness。
4. 执行命令（正式 Windows 门禁会构建并运行全部程序集）：

   ```powershell
   pwsh -File .\eng\verify.ps1
   ```

   只跑本任务用例：

   ```powershell
   dotnet run --project tests/DshLauncher.Acceptance.Tests/DshLauncher.Acceptance.Tests.csproj -c Release -- `
       -list full/json -preEnumerateTheories -printMaxStringLength 0 -noColor -noLogo
   # 确认 DisplayName 后（MTP 查询以 / 开头）：
   dotnet run --project tests/DshLauncher.Acceptance.Tests/DshLauncher.Acceptance.Tests.csproj -c Release -- `
       -filter "/*/*/RemotePageSessionWindowsTests/*" -result-xml d15-page-session.xml -noColor -noLogo -reporter quiet
   ```

5. 每条用例都要记录：结果（PASS/FAIL/BLOCKED）、OS 构建号、WebView2 Runtime 版本、执行者、证据路径。
6. 测试会产生临时用户数据目录（`%TEMP%\DshLauncher.D15\*`）；用例结束会尝试删除，
   若 WebView2 进程仍持有目录则保留残留，属预期，不影响判定。

## 1. 自动用例（xunit，已写好、待实机执行）

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WS-01 | `WS01EveryTargetPageOwnsItsOwnWebViewChannelAndUserDataFolder` | 同一窗口打开两个目标页面 | 两个页面各有独立 `WebView2` 实例、独立 UDF、独立通道 id、独立可信来源；都还没有能力 | 需要真实 WPF 窗口、真实 WebView2 实例与两个真实 UDF；Linux 上 `Microsoft.WindowsDesktop.App` 不存在 |
| WS-02 | `WS02NavigationStartingRevokesTheOldEpochBeforeTheNewDocumentLoads` | 可信页面加载完成后，在同一 WebView 里导航到同源另一路径，并在 `NavigationStarting` 回调里读状态 | 事件回调<b>当刻</b>能力已为 null、代际已推进、旧代际令牌已取消、取消触发为 `navigation-started` | 断言的是真实事件顺序（`NavigationStarting` 与代际推进的先后）；Linux 无 WebView2 事件 |
| WS-03 | `WS03ExternalLinkDoesNotInheritFileCapability` | 可信页面加载完成后调用 `OpenExternalLink`（真实 `NewWindowRequested` 走同一入口） | 能力立即撤销；旧代际消息得 `message-epoch-stale`；外部文档加载完成后仍无能力，新操作被拒 | 需要真实 `NewWindowRequested`/同 WebView 导航 |
| WS-04 | `WS04TabSwitchSuspendsNewWorkWithoutCancellingInFlightWork` | 两个页面都加载完成，来回切换 `PageTabs` 选中项 | 非当前页阶段为 `Suspended`(TabSwitched)、拒绝新操作、在途令牌未被取消；切回后恢复 `Active` | 需要真实 WPF `SelectionChanged` 与真实标签页宿主 |
| WS-05 | `WS05TargetRemovalCancelsInFlightWorkAndRejectsLateResults` | 页面加载完成后 `RemovePage`（目标删除触发） | 令牌已取消、阶段 `Closed`、触发为 `target-removed`、迟到消息得 `session-closed`、标签与宿主子元素已移除 | 需要真实窗口/标签宿主与真实 WebView2 释放顺序 |
| WS-06 | `WS06ApplicationExitCancelsEveryPageSession` | 两个页面在途时 `AllowClose()` + `Close()`（应用退出路径） | 两个会话都被取消并关闭、触发为 `application-exit`、宿主子元素清空 | 需要真实 WPF `Closed` → `Detach` 顺序与真实 WebView2 释放 |
| WS-07 | `WS07HiddenWindowRefusesNewWorkUntilShownAgain` | 不带 `AllowClose` 的 `Close()`（窗口只隐藏）后 `ShowAndActivate()` | 隐藏时阶段 `Suspended`(WindowHidden)、拒绝新操作但不取消在途；重新显示后恢复 `Active` | 需要真实窗口可见性与 WPF 生命周期事件 |
| WS-08 | `WS08MainDocumentOriginIsVerifiedOnTheLoadedDocumentNotOnSubresources` | 可信页面里引用一个**另一个来源**的图片，等待该子资源真的被请求 | 子资源请求不撤销能力；能力来源逐字等于可信来源；新操作与消息仍被接受 | 需要真实浏览器网络栈证明"子资源请求 ≠ 主文档来源"；Linux 规则测试只能证明判定函数本身 |
| WS-09 | `WS09MainDocumentAtAnotherOriginNeverGetsCapability` | 把主文档导航到另一个来源 | 能力为 null、能力码 `origin-external`、新操作 `work-capability-absent` | 需要真实跨来源导航完成事件 |
| WS-10 | `WS10CredentialRevocationClosesOnlyThatTargetsPageSession` | 用 `FakeRemoteAccessHost` 真实配对后打开远程 UI，再让远端"停止"使心跳判定为已吊销 | 枢纽快照变化后该页面被关闭、令牌取消、触发为 `credential-revoked`、迟到消息得 `session-closed`、其他页面不受影响 | 需要真实 HTTP 配对/心跳时序与 WPF 事件派发 |

## 2. 只能人工/实机准备的用例（无法用代码合成）

| id | 场景 | 怎么做 | 通过标准 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WM-07 | 真实重定向链的事件顺序 | 让可信来源 302 到另一来源（再 302 回来），观察 `NavigationStarting`(IsRedirected=true) → `NavigationCompleted` 的顺序与最终 `Web.Source` | 旧代际在第一次 `NavigationStarting` 即失效；只有最终文档来源等于可信来源时才重新授予能力 | 需要真实网络重定向与真实 WebView2 事件序列 |
| WM-08 | 渲染进程崩溃/恢复 | 用 WebView2 故障注入或任务管理器结束对应 renderer，观察 `ProcessFailed` 与后续导航 | 崩溃不复活旧代际能力；恢复导航后按新代际重新验证；无越权消息被接受 | 需要真实 renderer 进程与 WebView2 故障语义（本轮未接入 `ProcessFailed` 处理，属已知边界） |
| WM-09 | 隐藏窗口时渲染进程的真实行为 | 在途传输中隐藏远程窗口，等 10 分钟再显示 | 隐藏期间无新操作被接受；已授权任务按契约继续或取消；重开不重放旧 ACK | 隐藏时 Chromium 可能挂起/丢弃渲染进程，属实机事实 |
| WM-10 | 每目标 UDF 的 Cookie 隔离 | 目标 A 在远程页面登录/写入 Cookie，再打开目标 B 的页面并读取同一 Cookie | B 读不到 A 的 Cookie；两个 UDF 目录都真实存在且互不指向 | 需要真实 WebView2 用户数据目录与 Cookie 存储 |
| WM-11 | 真实进程退出的收尾 | 用托盘"退出"结束 Launcher（而非 `Close()`），退出时页面正在分块 | 每个页面按 `application-exit` 取消；暂存按策略清理；重启后不重放 | 需要真实进程终止与 WPF `Shutdown` 顺序 |
| WM-12 | 标签切走后的后台节流 | 切到另一标签 5 分钟后切回，期间页面持续发心跳式消息 | 后台页不因节流被误判为失效；切回后仍按当前代际接受消息 | 依赖真实浏览器后台节流策略 |

## 3. 与 Windows 验证方案的对应关系

| 本清单 | Windows 方案条目 |
| --- | --- |
| WS-02/WS-03/WS-09、WM-07 | W-05.2 来源变化（外链、协议/端口/主机、跨源重定向） |
| WS-08、WM-12 | W-05.3 frame 与后台（仅授权顶层文档） |
| WS-04/WS-07、WM-09 | W-04.5 隐藏窗口 |
| WS-02、WM-07 | W-04.6 导航/刷新（导航开始即使旧桥失效） |
| WS-05 | W-04.7 删除目标 |
| WS-06、WM-11 | W-06.7 Renderer/App 重启 |
| WS-10 | W-06.5 吊销配对 |
| WS-01、WM-10 | W-04.3 多目标（凭据/文件/错误/取消不串目标） |
| WS-05/WS-06、WM-08 | W-06.4 本地取消（迟到结果不复活操作） |

## 4. 明确的未验与假设

- 本清单全部用例**未执行**；Linux 侧只有平台中立规则与交叉编译有证据。
- `RemotePageSession` 假设：`WebView2.NavigationStarting` 一定先于同一次导航的
  `NavigationCompleted`（因此旧代际在文档加载前已作废）。该顺序未在实机核对，由 WS-02 验证。
- 挂起语义按方案 §5 实现为"隐藏/切标签只拒绝**新**操作，不取消在途工作"；
  真实浏览器在隐藏时是否还能完成在途传输由 WM-09 验证。
- 本轮**没有**接入 `CoreWebView2.ProcessFailed` 与 `WebResourceRequested`：前者属崩溃恢复，
  后者刻意不用于能力判定（避免"任意请求即凭据"）。见 WM-08。
- 子资源不撤销能力只在 Linux 规则层与 WS-08 的浏览器侧各验证一半；
  真实跨来源 iframe 的行为未单独建例，归入 W-05.3。
- 固定数据集枚举的已知不一致（不改动、仅报告）：`eng/expected-test-dataset.json` 里
  `DshLauncher.Platform.Windows.Tests` 有 4 条含反斜杠参数的理论用例，
  按枚举 JSON + `"`→`\"` 归一得到的是 D14 写入的基线值；而 MTP 的 XML 结果会把 `\` 再转义为 `\\`，
  因此真实 Windows XML 门禁对这 4 条会报 missing/unexpected。本轮新增的 10 条都是无参 `[Fact]`，
  名称仅含 ASCII，不受该差异影响；该 4 条基线**未作修改**（改动会删除既有基线哈希）。

## 5. D17 追加：附件桥是页面会话的组件（已写，待实机执行）

D17 把「原生输入 → 暂存 → Core 传输 → WebView2 WebMessage」的组合根做成
`RemotePageSession` 的**组件**（`RemotePageSession.AttachmentBridge`），生命周期仍由本类独占：

- `InitializeAsync` 在 `EnsureCoreWebView2Async` 成功后调用 `AttachTo(core)`（真实 WebMessage 必须在 UI 线程上收发）；
- `NavigationStarting` → `Lifecycle.BeginNavigation` 之后立即 `OnPageAdvanced()`：静默作废本代际身份、
  释放协调器/字节源/暂存快照，并对旧文档不发任何帧；
- `NavigationCompleted` → 能力授予时 `OnMainDocumentLoaded()` 发宿主握手，否则 `OnCapabilityLost()`；
- `Close(trigger)` → 先 `AttachmentBridge.Close("session-closed")`（取消在途、释放快照、解除监听、停表），
  再关页面会话；`Dispose` → 释放桥与 WebView。

因此 D15 的判定结论全部保留（代际、通道、能力、取消触发），D17 只在其上接入组合链路。

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WS-11 | `RemoteAttachmentBridgeWindowsTests.WB01HostHelloCapabilitiesAndContextTravelOverRealWebMessages` | 真实导航完成后宿主握手与页面握手双向送达 | 桥 Ready、身份绑定、真实 composer 上下文台账被写入 | 需要真实 WebView2 的 `NavigationCompleted` 与 `WebMessageReceived` 顺序 |
| WS-12 | `RemoteAttachmentBridgeWindowsTests.WB05RemovingTheTargetReleasesTheListenerTheTimerAndTheScratch` | 传输中移除目标 | 桥 Closed、监听解除、定时器停止、快照释放、协调器为 null；后续帧不被消费 | 需要真实 WPF 标签宿主与真实 WebView2 释放顺序 |
| WS-13 | `RemoteAttachmentBridgeWindowsTests.WB04FramesPostedBeforeTheHandshakeAreRefusedAndNeverBindAnIdentity` | 页面在能力授予前抢跑 | 抢跑帧确定拒绝或由宿主握手接住；绝不绑定身份 | 需要真实页面脚本与 `NavigationCompleted` 的相对顺序 |
| WS-14 | `RemoteAttachmentBridgeWindowsTests.WB06ClosingTheBridgeLeavesTheRealPairingHeartbeatRunning` | 关闭某目标的页面/桥 | 枢纽心跳照常、目标仍 Paired、`IsStarted` 不变 | 需要真实配对心跳与 WPF 事件派发 |

未验与假设：

- `WebMessageReceived` 与 `NavigationCompleted` 的真实先后顺序未核对：页面若在宿主握手之前抢跑，
  会被 D15 准入确定拒绝（WS-13 覆盖）；生产插件按"收到宿主 hello 再回帧"工作，因此不依赖抢跑成功。
- `PostWebMessageAsJson` 必须在 UI 线程调用（跨线程会抛）；桥只在页面会话线程上收发，跨线程路径未构造，
  属实现约束而非已验事实。
- WebView2 在隐藏窗口/后台标签下是否会节流消息投递未核对，归入 WM-12 与 WM-35。
