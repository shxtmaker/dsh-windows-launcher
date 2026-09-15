# D14 Windows 待执行用例清单（WindowsPending）

> **D16 追加（2026-09-14）**：本文件同时登记 D16「原生粘贴手势与剪贴板适配」在 Windows 程序集里的
> 实机用例 **WP-26…WP-34**（见 §1 表末与 §5）。D16 的手势/WPF 侧清单在
> `src/DshLauncher.Desktop/Attachments/NativePasteGesture.WindowsPending.md`（WW-01…WW-06 + WM-13…WM-32）。
> 追加是**增量**的：D14 的 WP-01…WP-25 与 WM-01…WM-06 一字未改。

本文件登记 D14「Windows 文件快照与暂存适配」中**只能在 Windows 实机执行**的用例。
本轮没有 Windows 机器：下面的用例**一条都没有运行过**，因此不得把任何一条记为 PASS。
Linux 侧本轮真实执行的是：平台中立判定（候选接受/限额/变化检测/清理归属的纯函数与
注入端口驱动的生产暂存服务）、`dotnet format` 洁净度、全 solution `--warnaserror` 交叉编译。

- 生产代码：`src/DshLauncher.Platform.Windows/Attachments/`（真实 Win32，无 stub、无 `#if` 假实现）
- 测试代码：`tests/DshLauncher.Platform.Windows.Tests/Attachments/`
- 可移植判定与生产服务：`src/DshLauncher.Core/Attachments/AttachmentStaging*.cs`
- Linux 已跑通的对应用例：`tests/DshLauncher.Core.Tests/Attachments/StagingPolicyTests.cs`、`StagingServiceTests.cs`

## 0. 执行前准备与命令

1. Windows 11 支持版本（或登记的 Windows 10 22H2 兼容环境）、x64、NTFS 卷、标准用户。
2. 文件符号链接用例（WP-06）需要**开发者模式**或提升权限；
   目录联接（junction）用例（WP-07、WP-15、WP-16）不需要额外权限。
3. 执行命令（正式 Windows 门禁，会构建并运行全部程序集）：

   ```powershell
   pwsh -File .\eng\verify.ps1
   ```

   只想跑本任务用例时，先用枚举确认过滤选择器，再执行（运行器支持 `-filter "<query>"`）：

   ```powershell
   dotnet run --project tests/DshLauncher.Platform.Windows.Tests/DshLauncher.Platform.Windows.Tests.csproj -c Release -- `
       -list full/json -preEnumerateTheories -printMaxStringLength 0 -noColor -noLogo
   # 确认 DisplayName 后：
   dotnet run --project tests/DshLauncher.Platform.Windows.Tests/DshLauncher.Platform.Windows.Tests.csproj -c Release -- `
       -filter "<确认过的 query>" -result-xml d14-staging-windows.xml -noColor -noLogo -reporter quiet
   ```

4. 每条用例都要记录：结果（PASS/FAIL/BLOCKED）、OS 构建号、卷类型与可用空间、执行者、证据路径。

## 1. 自动用例（xunit，已写好、待实机执行）

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WP-01 | `WindowsStagingCaptureTests.CaptureStagesARealFileUnderTheOwnedRootWithTheRealSha256` | 真实临时文件 300 000 字节 → 原生登记 → 快照 | 快照落在自有根内、字节逐一相等、SHA-256 等于 `SHA256.HashData` 结果、释放后文件消失 | 需要真实 Windows 卷上的 `CreateFileW`/`GetFinalPathNameByHandle`/`FlushFileBuffers` 与文件系统语义 |
| WP-02 | `CaptureClosesEveryHandleSoSourceAndSnapshotCanBothBeDeleted` | 快照成功后删除源文件与暂存文件 | 两次删除都成功（句柄未关时 Windows 会以共享冲突拒绝删除） | 句柄泄漏只有真实 Windows 共享语义才能观测 |
| WP-03 | `CaptureRefusesADirectoryCandidate` | 目录作为候选 | `candidate-directory`，台账不增加 | 需要真实目录属性位 |
| WP-04 | `CaptureRefusesAnUncCandidateWithoutTouchingTheNetwork` | `\\localhost\C$\Windows\win.ini` | `candidate-unc-path`，且暂存根内没有新条目（未发起网络访问） | UNC 语义与网络重定向器是 Windows 事实 |
| WP-05 | `CaptureRefusesDeviceDriveRelativeAndRootedCandidatePaths`（5 组） | `\\.\NUL`、`\\?\C:\...`、`\??\C:\...`、`C:win.ini`、`\Windows\win.ini` | 分别为 `candidate-device-path` / `candidate-path-not-absolute` | 设备命名空间与盘符相对语义是 Windows 事实 |
| WP-06 | `CaptureRefusesAFileSymbolicLinkWithoutReadingItsTarget` | 文件符号链接（需开发者模式） | `candidate-reparse-point`，未读取链接目标内容 | 重解析点属性与 `FILE_FLAG_OPEN_REPARSE_POINT` 行为是 Windows 事实 |
| WP-07 | `CaptureRefusesADirectoryJunctionAsADirectoryCandidate` | `mklink /J` 目录联接 | `candidate-directory`（属性同时含 DIRECTORY 与 REPARSE_POINT） | 同上 |
| WP-08 | `CaptureRefusesASourceLockedExclusivelyByAnotherProcess` | 另一句柄以 `FileShare.None` 占用源 | `source-locked` | 共享冲突码（Win32 32）是 Windows 事实 |
| WP-09 | `CaptureRefusesAnOfflineFile` | `SetAttributes(+Offline)` | `candidate-offline-file` | `FILE_ATTRIBUTE_OFFLINE` 是 Windows 属性位 |
| WP-10 | `CaptureRefusesAnAclDeniedSource` | `icacls /deny <user>:(R)` 后快照 | `source-unavailable`；测试结束会 `/remove:d` 复原 | ACL 与访问拒绝语义是 Windows 事实 |
| WP-11 | `CaptureFailsWhenTheSourceWasDeletedAfterTheNativeGesture` | 登记后删除源再快照 | `source-unavailable`，不产生暂存文件 | 需要真实文件删除与打开失败码 |
| WP-12 | `SourceHandleBlocksConcurrentWritersWhileTheSnapshotIsOpen` | 源句柄打开后另开写句柄 | 写句柄以共享冲突（HResult 低 16 位 = 32）失败 | 共享模式矩阵是 Windows 事实 |
| WP-13 | `ReplacingTheFileAtTheSamePathIsDetectedByFileIdentity` | 同名换文件（长度与最后写入时间相同） | 两次身份的文件标识不同，`StagingSourceChangePolicy.Compare` 判为变化 | 文件索引/卷序列号来自 `GetFileInformationByHandle` |
| WP-14 | `ExclusiveCreateRefusesToOverwriteAnExistingFile` | 目标名已存在时独占创建 | `staging-create-failed`，原内容一字未改 | `CREATE_NEW` 语义是 Windows 事实 |
| WP-15 | `CleanupRemovesOwnedRegularFilesAndRefusesEverythingElse` | 自有根内放入普通残留文件、目录联接、子目录 | 只删普通文件；联接与子目录被拒绝；根外文件内容原样保留 | 依赖真实重解析点与句柄式删除 |
| WP-16 | `InitializeRefusesAStagingRootThatIsAReparsePoint` | 暂存根本身是联接 | `Initialize` 抛 `AttachmentStagingException` | 需要真实联接与最终路径解析 |
| WP-17 | `InitializeRefusesATamperedOwnershipMarker` | 篡改所有权标记内容 | `Initialize` 抛 `AttachmentStagingException` | 需要真实文件写入与属性检查 |
| WP-18 | `CaptureForBridgeReturnsAReceiptWithoutAnyLocalPath` | 桥入口快照 | 回执含 id/字节数/SHA-256，类型上没有任何 Path 属性 | 依赖真实快照成功；边界本身在 Linux 也有等价反射用例 |
| WP-19 | `AConsumedCaptureIdCannotBeReplayed` | 同一 captureId 消费两次 | 第二次 `capture-already-consumed`，台账仍只有 1 条 | 需要真实快照成功 |
| WP-20 | `AnUnregisteredCaptureIdIsRefused` | 未登记的 captureId | `capture-id-unknown` | 同上 |
| WP-21 | `WindowsStagingBoundaryTests.TheAdapterPublicSurfaceOnlyAcceptsOpaqueIds` | 反射公开表面 | 公开 string 参数只有 batchId/captureId/snapshotId，无 Path 成员 | 反射断言本身平台中立，但按门禁归 Windows 程序集，本轮只枚举 |
| WP-22 | `TheOnlyWayToRegisterACandidateRequiresANativeGestureToken` | 反射 `RegisterNativeCapture` 与手势令牌 | 登记是 internal、首参是手势令牌；令牌无公开构造函数 | 同上 |
| WP-23 | `TheNativeCaptureTypeExposesNoPathAccessor` | 反射原生捕获类型 | 公开成员名不含 Path | 同上 |
| WP-24 | `TheBridgeReceiptAndCleanupOutcomeCarryNoLocalPath` | 反射回执与清理结局 | 公开属性名不含 Path | 同上 |
| WP-25 | `TheCaptureResultKeepsTheStagedPathOffTheBridgeSurface` | 反射两个入口 | `CaptureNative` 是 internal 且返回含路径的结果；桥入口返回无路径回执 | 同上 |

### 1.1 D16 追加：真实剪贴板与拖放（`WindowsClipboardPasteTests`）

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WP-26 | `WP26ARealFileDropListIsReadOffTheStaAndStagedWithTheRealBytes` | 真实 CF_HDROP（48 KiB 文件）→ 探测 → 读取 → 暂存 | `HasFileList`、`FileCount==1`、`ReadOffSta==true`；暂存字节与源文件逐一相等、SHA-256 相符 | `OpenClipboard`/`GetClipboardData`/`DragQueryFileW` 与剪贴板句柄所有权是 Windows 事实 |
| WP-27 | `WP27ABusyClipboardIsARecoverableResultWithoutBlockingTheSta` | 另一 STA 线程打开剪贴板不释放，再探测 | `clipboard-busy`/`clipboard-open-failed`；尝试次数 ≤ 3；**耗时 < 500 ms** | 剪贴板占用与 `ERROR_ACCESS_DENIED` 是 Windows 事实 |
| WP-28 | `WP28ProbingALargeBitmapDoesNotCopyPixelData` | 1920×1080 CF_DIB 放上剪贴板后探测 | 尺寸/位深正确；探测 < 100 ms（只读 124 字节头，不复制像素） | 需要真实 DIB 句柄与 `GlobalSize`/`GlobalLock` 行为 |
| WP-29 | `WP29ABitmapBecomesAPngSnapshotWithoutBlockingTheSta` | 64×32 位图 → STA 之外读取 → PNG → 暂存 | `ReadOffSta==true`；暂存文件以 PNG 魔数开头；SHA-256 与文件一致 | 需要真实位图剪贴板与真实磁盘写入 |
| WP-30 | `WP30AnOversizedClipboardBitmapIsRefusedBeforeAnyAllocation` | 头部声明 40000×30000 的 DIB | 读取即得 `limit-screenshot-pixels`，不复制像素 | 需要真实 CF_DIB 头 |
| WP-31 | `WP31MalformedClipboardBitmapDataIsRecoverable` | 只有 40 字节头、无像素载荷的 DIB | `clipboard-malformed` 且分类为可恢复 | 需要真实畸形剪贴板数据 |
| WP-32 | `WP32TheDropSurfaceAcceptsADataObjectAndNeverAPath` | 反射 `WindowsDroppedFilesSource`/`WindowsClipboardPasteSource` 公开表面 | 公开成员无任何 `string`/`string[]` 参数；`Probe()` 无参 | 反射断言本身平台中立，但按门禁归 Windows 程序集，本轮只枚举 |
| WP-33 | `WP33ARealDropDataObjectYieldsCaptureTickets` | 自建真实 DROPFILES HGLOBAL 的 OLE 数据对象 → 登记 → 暂存 | 1 个捕获票据；暂存字节与源文件相等 | 需要真实 `STGMEDIUM`/`ReleaseStgMedium` 所有权语义 |
| WP-34 | `WP34ADropBeyondTheBatchLimitIsRefusedWithoutRegistering` | 拖放 11 个文件（上限 10） | `limit-batch-files`；未注册任何票据；`PendingCaptureCount==0` | 同上 |

### 1.2 D17 追加：真实暂存快照 → 只读字节源（`WindowsStagedByteSourceTests`）

| id | 用例（xunit 方法） | 要跑什么 | 要断言什么 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WP-35 | `WP35AStagedSnapshotOpensAsThisSessionsReadOnlyByteSourceWithTheRealBytes` | 真实源文件 → 原生登记 → 快照 → `OpenStagedSource` 顺序读完 | 读回字节与源逐字相等、SHA-256 相符、`ByteLength` 等于台账 | 需要真实 `CreateFileW`（只读/共享/无跟随）与真实 `ReadFile` |
| WP-36 | `WP36TheByteSourceClosesItsHandleSoTheSnapshotCanBeReleasedAndDeleted` | 打开快照字节源后 `Dispose`，再 `Release` | `IsDisposed`、释放成功、台账归零（句柄未关时 Windows 会以共享冲突拒绝删除） | 句柄泄漏只有真实 Windows 共享语义才能观测 |
| WP-37 | `WP37TheByteSourceOnlyAcceptsATrackedSnapshotIdAndNeverAPath` | 未登记 id + 反射公开表面 | 抛 `AttachmentStagingException`；公开方法只收不透明 id，没有任何 path 参数 | 反射断言本身平台中立，但按门禁归 Windows 程序集，本轮只枚举 |
| WP-38 | `WP38AStagedFileThatGrewAfterTheSnapshotIsReportedAsASourceFault` | 快照落地后直接改写自有根内文件 | 读取得到确定的源故障码 `source-changed`，不静默截断 | 需要真实文件改写与真实句柄读取长度 |

## 2. 只能人工/实机准备的用例（无法在代码里合成）

| id | 场景 | 怎么做 | 通过标准 | 为什么不能在 Linux 上跑 |
| --- | --- | --- | --- | --- |
| WM-01 | 云占位自动下载拒绝 | 用 OneDrive/按需文件目录中的占位文件粘贴（属性含 `RECALL_ON_DATA_ACCESS` 或 `RECALL_ON_OPEN`） | 得到 `candidate-cloud-placeholder`，且**没有**触发下载（文件仍是占位、无网络流量） | 云同步引擎与占位属性只能由真实云盘客户端产生 |
| WM-02 | 磁盘不足 | 在容量受限的小 VHD（剩余空间 < 文件大小 + 64 MiB 保留量）上快照 | 写盘**之前**得到 `staging-free-space`；无半成品残留 | 需要真实卷空间耗尽 |
| WM-03 | 100 MiB 单目标暂存上限与崩溃残留清理 | 连续导入直到超过 `MaxStagingBytesPerTarget`；再强杀 Launcher 后重启 | 超限前明确拒绝 `limit-staging-bytes`；重启清理只删自有根内残留，页面不再拿到旧条目 | 需要真实进程终止与真实字节落盘 |
| WM-04 | 卷挂载点 / 无盘符卷 | 把测试卷挂到目录（mount point）后粘贴其中的文件；再对超长路径（>260，启用长路径）取文件 | 支持项按契约成功；挂载点被识别为环境事实并如实记录；路径形态判定给出确定码 | 挂载点与长路径是 Windows 事实 |
| WM-05 | 粘贴时快照语义（方案 F06/F07） | 复制文件后、粘贴前修改/删除它；再在复制大文件期间尝试占用 | 快照对应粘贴时的稳定状态；被占用/被删除给出确定拒绝；绝不产生混合字节 | 与真实剪贴板时序、真实占用有关，属 W-02/W-05 实机轨道 |
| WM-06 | 分块与哈希贯通（配合 D15/D16） | 对 256 KiB 边界文件与 20 MiB 上限文件走完整分块传输 | `file-end` 的 SHA-256 与本文件 WP-01 的暂存哈希一致，远端读回字节一致 | 需要 Windows 侧完整传输链路（D14 只交付暂存与快照） |

## 3. 与 Windows 验证方案的对应关系

| 本清单 | Windows 方案条目 |
| --- | --- |
| WP-03/04/05/06/07/09/10/11 | W-05.5 本地来源（目录/UNC/链接/只读/占用/ACL） |
| WP-01/02/13/14、WM-05 | W-05.6 快照一致性 |
| WP-15/16/17、WM-03 | W-05.7 暂存清理（路径重解析、失败可恢复、资源有上界） |
| WP-18…WP-25 | W-05.1 无任意路径读取入口 |
| WM-02 | W-07 磁盘上界 |
| WM-06 | W-02.3 / W-03 字节一致性 |

## 4. 明确的未验与假设

- 属性位常量值（`RECALL_ON_OPEN=0x00040000`、`RECALL_ON_DATA_ACCESS=0x00400000`、
  `OFFLINE=0x00001000`、`REPARSE_POINT=0x00000400`）与 `FILE_DISPOSITION_INFO` 单字节布局
  都是按 Win32 文档实现，**尚未在实机核对**；WP-09 与 WP-15 会实际验证其中两条。
- `FILE_SHARE_READ` 使得快照期间其他进程无法取得写句柄（WP-12 验证）；因此
  "复制中源被改写"在 Windows 上主要被共享模式挡住，身份复核是第二道防线。
  Linux 侧用注入假源真实执行了变化检测的每一条分支。
- 云占位、真实磁盘耗尽、卷挂载点必须由 WM-01…WM-04 在实机准备，尚未执行。

## 5. D16 追加的未验假设（与 WP-26…WP-34 对应）

- `GlobalUnlock` 在解锁计数归零时返回 `false` 并置 `ERROR_SUCCESS`：实现按文档忽略其返回值，
  未在实机核对（WP-26/WP-28/WP-29 会覆盖到该路径）。
- `GlobalSize(CF_DIB)` 给出的载荷长度与头部声明的 `biSizeImage` 是否一致未核对；
  实现只信 `GlobalSize`（WP-28/WP-30）。
- `WindowsDroppedFilesSource` 接收的 COM `IDataObject` 由 WPF `DataObject` 强转而来，
  该强转与拖放时 `GetData(CF_HDROP)` 的 `STGMEDIUM` 形态**未在实机核对**（WP-33）。
  若强转失败，拖放会得到 `drop-no-file-list`（确定码，不会崩），需要在 Desktop 侧改走
  `DataFormats.FileDrop` 的同一内部登记入口——那会削弱"公开 API 不收路径"的强度，须先登记再改。
- `SetClipboardData` 成功后 HGLOBAL 所有权归系统：用例不在 `finally` 释放该句柄（WP-26/WP-33 覆盖）。
- D17 追加：`WindowsStagedByteSource` 的只读句柄用 `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN`
  打开，且只接受 D14 台账里的 snapshotId；"快照在打开后被换成重解析点"这一条**未建例、未核对**
  （换掉文件会被 WP-38 的改写检测覆盖到长度变化，但纯链接替换只有实机能构造）。
- 用例专用的剪贴板写入 P/Invoke 在测试程序集里刻意保留 `DllImport`（测试程序集未开
  `AllowUnsafeBlocks`），生产侧仍使用源生成的 `LibraryImport`。
