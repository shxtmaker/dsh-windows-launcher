# D17 Windows 待执行用例清单（WindowsPending）

本文件登记 D17「完成 WebView2 与生产 Core 组合」中**只能在 Windows 实机执行**的用例。
本轮没有 Windows 机器：下面的用例**一条都没有运行过**，因此不得把任何一条记为 PASS。

**生产代码是完整的，只有"执行"待实机**：`WebViewMessageTransport`（真实
`CoreWebView2.PostWebMessageAsJson` / `WebMessageReceived`）、`RemoteAttachmentBridge`
（真实 `DispatcherTimer` 超时驱动与全路径释放）、`WindowsStagedByteSource`
（真实只读、无跟随的 Win32 句柄）与 `RemotePageSession`/`RemoteWindow` 的组合接线都已在位，
没有 TODO、没有 stub、没有 `#if` 假实现、没有用 mock 代替 WebView 或剪贴板。

Linux 侧本轮真实执行的是平台中立组合契约与交叉编译：

- 生产组合契约（可移植、无 P/Invoke）：`src/DshLauncher.Core/Attachments/RemoteBridge/`
  （`RemoteBridgeProtocol`、`RemoteBridgePorts`、`RemoteBridgeHandshake`、`RemoteBridgeMime`、
  `RemoteBridgeComposition`）
- Linux 已跑通的用例：`tests/DshLauncher.Core.Tests/Attachments/RemoteBridge*`（通道契约、身份/大小复核、
  握手路由与模式决策、传输/取消/迟到/释放、桥关闭不影响配对心跳）
- Windows 生产代码：`src/DshLauncher.Desktop/Attachments/WebViewMessageTransport.cs`、
  `src/DshLauncher.Desktop/Attachments/RemoteAttachmentBridge/RemoteAttachmentBridge.cs`、
  `StagingAdapterPort.cs`、`src/DshLauncher.Platform.Windows/Attachments/WindowsStagedByteSource.cs`、
  `WindowsAttachmentStagingAdapter.OpenStagedSource`
- Windows 用例：`tests/DshLauncher.Acceptance.Tests/EndToEnd/RemoteAttachmentBridgeWindowsTests.cs`（WB-01…WB-06）、
  `tests/DshLauncher.Platform.Windows.Tests/Attachments/WindowsStagedByteSourceTests.cs`（WP-35…WP-38，
  与 D14/D16 同表，见 `../../../../tests/DshLauncher.Platform.Windows.Tests/Attachments/WindowsPendingCases.md`）

## 0. 执行前准备与命令

1. Windows 11 支持版本（或登记的 Windows 10 22H2 兼容环境）、x64、NTFS 卷、标准用户，**必须有可交互桌面会话**
   （WB-03/WB-05 用真实 `SendInput` 送 Ctrl+V，锁屏/无桌面会话时送不到前台窗口）。
2. 必须装有 **WebView2 Evergreen Runtime**（与产品安装包要求一致）。
3. 用例自带 Kestrel 回环主机（页面里带 D17 传输脚本）与内存目标库，不需要外部 Harness。
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
       -filter "/*/*/RemoteAttachmentBridgeWindowsTests/*" -result-xml d17-bridge.xml -noColor -noLogo -reporter quiet
   ```

5. 每条用例都要记录：结果（PASS/FAIL/BLOCKED）、OS 构建号、WebView2 Runtime 版本、执行者、证据路径。
6. 测试产生临时目录（`%TEMP%\DshLauncher.D17\*`、应用数据根下的 `attachments\staging\<targetId>`）；
   用例结束会尝试删除，WebView2 进程仍持有 UDF 时保留残留属预期。

## 1. 自动用例（xunit，已写好、待实机执行）

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WB-01 | `WB01HostHelloCapabilitiesAndContextTravelOverRealWebMessages` | 真实 WebView2 加载可信页面；宿主发 hello+capabilities；页面回 hello+capabilities+context | 页面脚本真的收到宿主两帧；桥把页面 identity 绑成 `session-1`；真实 composer 上下文台账被写入 | 需要真实 `CoreWebView2`、真实 `WebMessageReceived`/`PostWebMessageAsJson` 与真实导航完成事件 |
| WB-02 | `WB02AScreenshotCapablePageGetsTheNativePasteOwnerOverTheRealChannel` | 页面 capabilities 含 `screenshot` | 真实截图所有者台账（原生路由读的那一份）在该 target+epoch 上给出 `native-paste`；桥的 `ScreenshotMode` 一致 | 需要真实 WebMessage 送达与真实页面脚本时序；Linux 上只验证过纯函数与假通道 |
| WB-03 | `WB03ARealClipboardFileTravelsToThePageAsChunksAndCompletesTheBatch` | 真实剪贴板放 300 KiB 文件 + 真实 Ctrl+V；页面逐块 ack、file-end 回 import-result | 批次 Closed、文件 `staged`、`SentBytes==307200`、导入调用 1 次；页面累计 `receivedBytes==307200` 且收到 file-end；自有暂存根内没有残留文件 | 需要真实剪贴板、真实 `SendInput`、真实 WebView2 消息往返与真实磁盘写入 |
| WB-04 | `WB04FramesPostedBeforeTheHandshakeAreRefusedAndNeverBindAnIdentity` | 页面在文档刚加载时抢跑发 hello（宿主尚未授权/握手） | 抢跑帧要么被确定拒绝（`message-capability-absent`/`message-epoch-stale`/封包码）要么被宿主握手接住；**绝不绑定身份**、不写 composer 上下文 | 需要真实事件顺序（页面脚本先于/晚于 `NavigationCompleted` 执行） |
| WB-05 | `WB05RemovingTheTargetReleasesTheListenerTheTimerAndTheScratch` | 传输中（700 KiB、多块在途）`RemovePage(TargetRemoved)` | 桥 Closed、通道 `IsClosed`、快照全部释放、定时器停止、协调器为 null；页面再发帧也不被消费 | 需要真实 WebView2 释放顺序、真实定时器与真实暂存清理 |
| WB-06 | `WB06ClosingTheBridgeLeavesTheRealPairingHeartbeatRunning` | 真实 `PairingHub` + `FakeRemoteAccessHost` 配对后打开远程页面，移除该目标（关闭桥） | 心跳计数继续增长、目标仍 `Paired`、`IsStarted` 仍为 true | 需要真实 HTTP 配对/心跳时序与真实 WPF 事件派发 |

## 2. 只能人工/实机准备的用例（无法用代码合成）

| id | 场景 | 怎么做 | 通过标准 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WM-33 | 真实插件（D12/D13 接收端）与宿主的互通 | 在真实 Harness+全家桶+本插件页面里做一次粘贴 | 页面收到宿主 hello/capabilities/context，宿主收到页面 hello/capabilities/context；附件真的进入草稿 | 需要真实插件 bundle 与会话；正式互通属 D18 |
| WM-34 | 页面对象形态 vs 字符串形态投递 | 分别用 `postMessage(envelope)` 与 `postMessage(JSON.stringify(envelope))` | 两种形态都被接受（封包归一），非法文本得到 `malformed-json` | 需要真实 WebView2 对两种投递的 `WebMessageAsJson` 形态 |
| WM-35 | 页面洪泛消息 | 页面在 1 秒内发 200 条封包 | 队列上界生效：超出即丢弃并记 `bridge-inbound-overflow`，界面不卡、内存不涨 | 需要真实消息泵与真实时序 |
| WM-36 | 传输中导航/刷新 | 大文件传输中点刷新或导航到同源另一路径 | 旧代际全部作废、快照释放；迟到帧得 `message-epoch-stale`；新文档重新握手 | 需要真实导航事件与真实 WebView2 生命周期 |
| WM-37 | WebView2 进程崩溃/恢复 | 任务管理器结束对应 renderer 后重载页面 | 崩溃不复活旧身份；重载后重新握手；无越权帧被接受 | 需要真实 renderer 与 WebView2 故障语义 |
| WM-38 | 真实关闭顺序下的资源回收 | 关闭远程窗口（隐藏）与托盘退出两条路径各做一次传输中退出 | 隐藏窗口不取消在途（已授权任务继续）；退出时全部取消并释放，暂存无残留 | 需要真实 WPF `Closed`/`Shutdown` 顺序与真实 WebView2 释放 |
| WM-39 | 剪贴板与页面同时拿到图片粘贴（bridge 模式） | 页面 capabilities 不含 `screenshot` 时粘贴一张截图 | 宿主完全不读剪贴板；页面自己的 paste 处理；**只出现一份**附件 | 需要真实位图剪贴板与真实页面 paste 行为 |
| WM-40 | 附件桥关闭期间的配对保活 | 桥关闭前后各观察 30 秒枢纽状态 | 心跳照常、目标不消失、凭据不失效；附件功能停用不影响配对 | 与 WB-06 同源，但需要真实墙钟观察窗口 |

## 3. 与 Windows 验证方案的对应关系

| 本清单 | Windows 方案条目 |
| --- | --- |
| WB-01/02、WM-33/34 | W-04 远程界面接入与握手 |
| WB-03、WM-39 | W-02 原生粘贴 / W-02.2 截图与来源优先 |
| WB-04、WM-36 | W-04.6 导航/刷新（导航开始即失效） |
| WB-05、WM-38 | W-06.7 资源回收与退出 |
| WB-06、WM-40 | W-06.5 配对与保活不受附件功能影响 |
| WM-35/37 | W-06 故障与上界 |

## 4. 明确的未验与假设

- 本清单全部用例**未执行**；Linux 侧只有平台中立组合契约、假通道/假暂存端口与交叉编译有证据。
- v1 线协议与截图相关的信号只有 `capabilities.features` 里的 `screenshot` 一位，因此
  `PluginPrefersNativePaste` 与 `PluginAdvertisesScreenshot` 同源；当前生产插件的
  `CLIENT_FEATURES` **不含** `screenshot`，所以生产上截图所有者会是 `bridge`（页面自己粘贴），
  这正是方案 §4.1「普通截图优先保留浏览器真实 paste」；`native-paste` 需要页面显式声明该特性（WB-02 覆盖）。
- v1 的 `context` 只有身份字段，没有"可编辑/锁定/子代理"字段：建绑即视为存在当前可编辑 composer，
  锁定/被阻塞会在 `import-result` 阶段以确定码返回。真实表现由 WM-33 核对。
- 页面侧应答脚本是**测试夹具**（记录宿主帧、按需回 ack/import-result），不是生产接收端；
  生产接收端互通属 D18（WM-33）。
- `RemoteWindow` 的挂起语义（隐藏/切标签只拒绝新操作）沿用 D15 结论；WM-38 覆盖真实退出顺序。
- `documentsEpoch` 在 v1 里由页面声明、宿主复核其稳定性；宿主自己的页面代际走封包 `epoch`
  （每次收发都复核）。真实插件写什么值由 WM-33 核对。
