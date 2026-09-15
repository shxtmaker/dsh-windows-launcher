# D16 Windows 待执行用例清单（WindowsPending）

本文件登记 D16「原生粘贴手势与剪贴板适配」中**只能在 Windows 实机执行**的用例。
本轮没有 Windows 机器：下面的用例**一条都没有运行过**，因此不得把任何一条记为 PASS，
也不得把 Linux 上生成的 PNG 当作"截图采集已验"。

Linux 侧本轮真实执行的是平台中立规则与交叉编译：

- 生产规则（可移植、无 P/Invoke）：`src/DshLauncher.Core/Attachments/NativePasteGesturePolicy.cs`、
  `ClipboardSourcePolicy.cs`、`NativePasteConsumerElection.cs`、`NativePasteFailurePolicy.cs`、
  `ScreenshotPngPolicy.cs`、`ScreenshotPngEncoder.cs`、`NativePasteOrchestrator.cs`、
  `ScreenshotOwnerHandshake.cs`、`AttachmentComposerContext.cs`、`InMemoryNativeCapture.cs`
- Linux 已跑通的用例：`tests/DshLauncher.Core.Tests/Attachments/`（`NativePasteGestureAuthorizationTests`、
  `ClipboardSourcePreferenceTests`、`NativePasteConsumerElectionTests`、
  `NativePasteFailureClassificationTests`、`ScreenshotPngAdapterTests`、`NativePasteOrchestratorTests`）
- Windows 生产代码（真实 Win32 / WPF，无 stub、无 `#if` 假实现）：
  `src/DshLauncher.Platform.Windows/Attachments/WindowsClipboardNative.cs`、
  `WindowsClipboardPasteSource.cs`、`WindowsDroppedFilesSource.cs`、
  `src/DshLauncher.Desktop/Attachments/NativePasteGestureRouter.cs`
- Windows 用例：`tests/DshLauncher.Acceptance.Tests/EndToEnd/NativePasteGestureWindowsTests.cs`（WW-01…WW-06）、
  `tests/DshLauncher.Platform.Windows.Tests/Attachments/WindowsClipboardPasteTests.cs`（WP-26…WP-34，
  与 D14 的 WP-01…WP-25 同表，见 `../../../tests/DshLauncher.Platform.Windows.Tests/Attachments/WindowsPendingCases.md`）

**关键边界**：`WindowsClipboardPasteSource` 只在<b>有界探测</b>里碰剪贴板，真正的复制与编码在线程池线程上完成；
"打开剪贴板失败码、占用语义、CF_DIB 句柄生命周期、真实 STA 焦点表现、WPF 拖放数据对象的 COM 形态"
这五件事没有任何 Linux 证据。

## 0. 执行前准备与命令

1. Windows 11 支持版本（或登记的 Windows 10 22H2 兼容环境）、x64、NTFS 卷、标准用户，**必须有可交互桌面会话**
   （WW-01…WW-06 用真实 `SendInput`，锁屏/无桌面会话时会送不到前台窗口）。
2. 必须装有 **WebView2 Evergreen Runtime**（与产品安装包要求一致）。
3. 用例自带 Kestrel 回环主机与内存目标库，不需要外部 Harness；剪贴板内容由用例自己准备。
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
       -filter "/*/*/NativePasteGestureWindowsTests/*" -result-xml d16-native-paste.xml -noColor -noLogo -reporter quiet

   dotnet run --project tests/DshLauncher.Platform.Windows.Tests/DshLauncher.Platform.Windows.Tests.csproj -c Release -- `
       -filter "/*/*/WindowsClipboardPasteTests/*" -result-xml d16-clipboard.xml -noColor -noLogo -reporter quiet
   ```

5. 每条用例都要记录：结果（PASS/FAIL/BLOCKED）、OS 构建号、剪贴板来源（哪个程序写入的）、执行者、证据路径。
6. 测试会产生临时目录（`%TEMP%\DshLauncher.D16\*`、`dsh-d16-paste-*`）；用例结束会尝试删除。

## 1. 自动用例（xunit，已写好、待实机执行）

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WW-01 | `WW01ARealPasteHotkeyInTheActiveWindowImportsExactlyOnce` | 真实窗口激活 + 剪贴板放一个文件 + `SendInput` 送 Ctrl+V | 窗口抑制了浏览器默认粘贴；结局 `native-paste-imported`；`ReadOffSta=true`；该手势账目 `TotalImports==1` 且 `ExactlyOneConsumer` | 需要真实 WPF 窗口/焦点、真实剪贴板与真实 Win32 输入栈 |
| WW-02 | `WW02AHiddenWindowRefusesTheGestureWithoutReadingTheClipboard` | 隐藏窗口后送 Ctrl+V | 结局 `gesture-window-hidden`、不读剪贴板、`KeepsBrowserDefault=true` | 需要真实窗口可见性与前台窗口语义 |
| WW-03 | `WW03AWindowWithoutASessionRefusesWithNoSessionAndKeepsTheDefaultPaste` | 不报告任何 composer 上下文（无 session）后送 Ctrl+V | 结局 `no-session`、可恢复、未创建任何暂存文件、默认粘贴放行 | 需要真实页面会话与真实剪贴板 |
| WW-04 | `WW04ABitmapOwnedByTheBridgeIsNeverReadByTheHost` | 握手把截图所有者定为 bridge，剪贴板放位图，送 Ctrl+V | 结局 `delegated-bridge`、宿主读取次数 0、暂存创建数 0 | 需要真实位图剪贴板与真实握手时序 |
| WW-05 | `WW05ABitmapOwnedByNativePasteBecomesOneStagedPng` | 握手定为 native-paste，剪贴板放 32bpp 位图，送 Ctrl+V | 结局为原生导入；`EncodedBytes ∈ [1, 20 MiB]`；自有根内恰好新增 1 个暂存文件 | 需要真实 CF_DIB 句柄、真实线程池读取与真实磁盘写入 |
| WW-06 | `WW06ASecondHotkeyAfterANativeTimeoutIsNotReImportedByTheBridge` | 一次成功导入后再查该手势账目 | 账目仍只有 1 个消费者、`TotalImports==1`；新粘贴是新手势 | 需要真实手势时序与真实账本行为 |
| WP-26 | `WP26ARealFileDropListIsReadOffTheStaAndStagedWithTheRealBytes` | 真实 CF_HDROP（48 KiB 文件）→ 探测 → 读取 → 暂存 | `HasFileList`、`FileCount==1`、`ReadOffSta=true`；暂存字节与源文件逐一相等、SHA-256 相符 | `OpenClipboard`/`DragQueryFileW` 与句柄所有权是 Windows 事实 |
| WP-27 | `WP27ABusyClipboardIsARecoverableResultWithoutBlockingTheSta` | 另一个 STA 线程打开剪贴板不释放，再探测 | 得到 `clipboard-busy`/`clipboard-open-failed`；尝试次数 ≤ 3；**耗时 < 500 ms**（STA 没被拖住） | 剪贴板占用语义与 `ERROR_ACCESS_DENIED` 是 Windows 事实 |
| WP-28 | `WP28ProbingALargeBitmapDoesNotCopyPixelData` | 1920×1080 的 CF_DIB 放上剪贴板后探测 | 尺寸/位深正确；探测 < 100 ms（只读 124 字节头） | 需要真实 DIB 句柄与 `GlobalSize`/`GlobalLock` 行为 |
| WP-29 | `WP29ABitmapBecomesAPngSnapshotWithoutBlockingTheSta` | 64×32 位图 → 读取 → 暂存 | `ReadOffSta=true`；暂存文件以 PNG 魔数开头；SHA-256 与文件一致；显示名 `screenshot.png` | 需要真实位图剪贴板与真实磁盘 |
| WP-30 | `WP30AnOversizedClipboardBitmapIsRefusedBeforeAnyAllocation` | 头部声明 40000×30000 的 DIB | 读取阶段即得 `limit-screenshot-pixels`，不复制像素 | 需要真实 CF_DIB 头与真实尺寸判定路径 |
| WP-31 | `WP31MalformedClipboardBitmapDataIsRecoverable` | 只有 40 字节头、没有像素载荷的 DIB | 得 `clipboard-malformed` 且分类为可恢复 | 需要真实畸形剪贴板数据 |
| WP-32 | `WP32TheDropSurfaceAcceptsADataObjectAndNeverAPath` | 反射 `WindowsDroppedFilesSource`/`WindowsClipboardPasteSource` 公开表面 | 公开成员没有任何 `string`/`string[]` 路径参数；`Probe()` 无参 | 反射断言本身平台中立，但按门禁归 Windows 程序集，本轮只枚举 |
| WP-33 | `WP33ARealDropDataObjectYieldsCaptureTickets` | 自建真实 DROPFILES HGLOBAL 的 OLE 数据对象 → 登记 | 得到 1 个捕获票据，暂存字节与源文件相等 | 需要真实 `STGMEDIUM`/`ReleaseStgMedium` 所有权语义 |
| WP-34 | `WP34ADropBeyondTheBatchLimitIsRefusedWithoutRegistering` | 拖放 11 个文件（超过批内上限 10） | `limit-batch-files`；未注册任何票据；`PendingCaptureCount==0` | 同上 |

## 2. 手工验证步骤（原生快捷键覆盖，本卡的"手工验证步骤"）

> 目标：证明原生粘贴覆盖**只**发生在"远程窗口可见、是前台窗口、焦点在活动页、标签就是该目标"时，
> 且**没有全局截断 Ctrl+V**。每一步都要记录实际观察结果。

| id | 步骤 | 通过标准 |
| --- | --- | --- |
| WM-13 | 资源管理器里复制一个普通文件 → 切到 Launcher 远程窗口（活动页），按 Ctrl+V | 出现 1 个待发送附件；窗口顶栏显示"已导入 1 个附件"；**不**插入任何本机路径文本 |
| WM-14 | 复制一段纯文本 → 在远程页面编辑器里 Ctrl+V | 正常粘贴文本；顶栏**不**显示附件结果；剪贴板文件通路完全不参与 |
| WM-15 | 复制文件后，把焦点移到**别的应用**（记事本）再 Ctrl+V | 记事本正常粘贴；Launcher 不拦截、不导入（证明没有全局键盘钩子） |
| WM-16 | 复制文件后按 Ctrl+Shift+V、Ctrl+Alt+V、Shift+Insert（非粘贴语义的组合） | 只有 Shift+Insert 被视为粘贴手势；其余组合不被消费，页面/系统行为不受影响 |
| WM-17 | 复制文件后切到**另一个标签页**（另一目标）再 Ctrl+V | 该次手势不属于当前页：不得导入到别的目标；按标签焦点给出确定拒绝码（顶栏显示"无法导入：gesture-tab-not-active"或对应码） |
| WM-18 | 最小化窗口 / 隐藏到托盘后按 Ctrl+V | 不导入；按键不被吞掉（前台窗口照常收到） |
| WM-19 | 复制**截图**（Win+Shift+S 或 PrtSc）→ Ctrl+V（握手 native-paste） | 得到 1 个 PNG 附件；`截图.png` 可预览；顶栏显示已导入 |
| WM-20 | 复制**截图** → Ctrl+V（握手 bridge：让插件声明偏好桥） | 宿主不读取剪贴板；由页面自己的粘贴处理；**只出现一份**附件（不得两份） |
| WM-21 | 复制一个文件**同时**再复制一张截图（先复制文件，再用支持"带图复制"的程序叠加位图，或使用同时提供 CF_HDROP 与 CF_DIB 的程序） | 按规则**优先文件列表**：只导入 1 个文件附件，不导入截图 |
| WM-22 | 在资源管理器里复制一个目录、UNC 路径（`\\localhost\C$`）、符号链接/联接、OneDrive 占位文件后 Ctrl+V | 分别得到 `candidate-directory`/`candidate-unc-path`/`candidate-reparse-point`/`candidate-cloud-placeholder`，**且不触发**云下载、不读网络共享 |
| WM-23 | 打开一个**没有 session** 的页面（首页）后 Ctrl+V | 顶栏显示"无法导入：no-session"；不自动创建会话；普通文本仍可粘贴 |
| WM-24 | 会话处于**锁定**/子代理提示时 Ctrl+V | 分别得到 `composer-locked`/`subagent-prompt`；不静默丢弃、不自动创建会话 |
| WM-25 | 从资源管理器把文件**拖到**远程窗口上 | 得到 1 个附件；拖放由宿主消费（页面不同时收到）；拖到别的标签页时不导入 |
| WM-26 | 把 11 个文件拖到窗口上 | `limit-batch-files`；不产生附件；无残留暂存文件 |
| WM-27 | 在大文件复制（资源管理器"复制"一个 1 GiB 文件）占用剪贴板期间按 Ctrl+V | 得到 `clipboard-busy`（可恢复：顶栏提示可重试）；**界面不卡死**（STA 未被阻塞） |
| WM-28 | 在粘贴过程中（位图读取中）用别的程序改写剪贴板 | 得到 `clipboard-changed`；不产生半成品暂存文件 |
| WM-29 | 粘贴一次后立刻再按 Ctrl+V 两次（快速连按） | 每次按键各自产生独立手势与新附件；**同一个手势**绝不出现两份附件 |
| WM-30 | 让原生读取超时（把大位图读取人为拖慢，或临时调小 `NativePasteOrchestrator.ReadTimeoutMs`） | 顶栏显示 `clipboard-timeout`；随后把同一动作交给桥也**不会**补导入（账目 `BridgeMustNotImport`） |
| WM-31 | 篡改暂存根（把 `<应用数据根>\attachments\staging\<targetId>` 变成联接）后 Ctrl+V | 顶栏显示"附件暂存不可用"；不写入根外；不崩溃 |
| WM-32 | 关闭/停用附件插件后 Ctrl+V 一个文件 | 明确提示桥/能力不可用，**不**静默插入本机路径；纯文本粘贴不受影响 |

## 3. 与 Windows 验证方案的对应关系

| 本清单 | Windows 方案条目 |
| --- | --- |
| WW-01、WM-13/16/17/18、WM-29 | W-02 原生粘贴（活动窗口/标签/焦点与唯一消费） |
| WM-19/20/21、WW-04/05 | W-02.2 截图与来源优先 |
| WM-22、WP-26 | W-05.5 本地来源（目录/UNC/链接/云占位）沿用 D14 裁决 |
| WM-27/28/30、WP-27 | W-06.2 剪贴板占用与可恢复失败 |
| WW-06、WM-29/30 | W-06.4 本地取消与迟到结果不复活操作 |
| WM-23/24、WW-03 | W-04.4 会话/锁定/子代理闸门 |
| WP-32、WM-13/21 | W-05.1 无任意路径读取入口 |
| WM-15/16 | W-02.4 不全局截断粘贴快捷键 |

## 4. 明确的未验与假设

- 本清单全部用例**未执行**；Linux 侧只有平台中立规则、PNG 编码与交叉编译有证据。
- **不把 Linux PNG 当截图采集已验**：Linux 上验证的是"给定 DIB 字节 → 确定性 PNG"，
  真实剪贴板上"能不能拿到 CF_DIB、拿到的是不是同一张图"完全没有证据（WP-29 才验证）。
- `SendInput` 需要可交互桌面会话与前台窗口；无人值守/锁屏的 runner 上 WW-01…WW-06 会阻塞或失败，
  这类环境必须改用手工步骤 WM-13…WM-30 记录证据。
- WPF 的 `DataObject` 是否能被强转成 COM `IDataObject`（`WindowsDroppedFilesSource` 的入参）
  **尚未在实机核对**；若不能，拖放会得到 `drop-no-file-list`，需要改为在 Desktop 侧用
  `DataFormats.FileDrop` 走同一内部登记入口（那会削弱"公开 API 不收路径"的强度，必须先登记再改）。
- `CoreWebView2.AllowExternalDrop=false` 是否真的让外部拖放落到 WPF 而非页面，未在实机核对（WM-25/WM-26）。
- WebView2 里焦点在渲染子窗口时 `Keyboard.FocusedElement` 是否为 `null`（`WindowFocusProbe` 的第二层
  `GetFocus()`/`GetAncestor` 兜底是否必要）未在实机核对。
- 握手消息（capabilities → 截图所有者、context → composer 上下文）的真实解析与送达属 D17
  （WebView2 组合）；D16 只交付规则与 `RemoteWindow.ReportScreenshotHandshake`/`ReportComposerContext`
  这两个接入点。**在此之前，只有位图的粘贴会以 `consumer-mode-undecided` 被确定拒绝**，
  文件列表与拖放不受影响。
  > **D17 更新（已实现，实机待验）**：`RemoteBridgeComposition` 现在真实解析 `capabilities.features`
  > 与 `context`，并把结论经 `IRemoteBridgeHandshakeSink` 送给这两个接入点（窗口实现直接转发到
  > `ReportScreenshotHandshake`/`ReportComposerContext`）。因此：
  > 握手到达后截图模式为确定的 `native-paste`（页面声明 `screenshot` 且宿主原生采集可用）或
  > `bridge`（页面未声明，或宿主原生采集不可用）；**握手到达之前仍是 `undecided`**
  > （位图粘贴继续以 `consumer-mode-undecided` 拒绝）。Linux 已用真实 D16 编排器逐条断言这三段行为
  > （`tests/DshLauncher.Core.Tests/Attachments/RemoteBridgeHandshakeTests.cs`）；
  > 真实送达与事件顺序见 `RemoteAttachmentBridge.WindowsPending.md` 的 WB-01/WB-02。
- 文件列表通路不经截图所有者模式：CF_HDROP 永远走原生（浏览器拿不到），因此不受上述未决状态影响。
