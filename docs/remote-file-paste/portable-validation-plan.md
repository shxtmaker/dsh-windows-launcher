# 非 Windows 项目开发验证方案

方案版本：2.0。任务调度见 [任务索引](task-index.md)和[分轮执行规则](execution-workflow.md)。本文件的完整验证矩阵按任务逐步实现，不要求 D02 一轮内建完全部测试。

适用方案：[全家桶与独立附加插件实施方案](implementation-plan.md)。目标开发环境为 Linux；不要求 Windows 设备在线。本文件所列新增命令及脚本是开发交付契约，尚未实现或通过，不应当作现有工具结果。

## 1. 验证职责

Linux 验证实际生产 Core、附加插件、浏览器接收器和真实 Harness＋全家桶链路。Windows Clipboard、STA、WPF、WebView2 原生事件和安装结果由 [Windows 实机验证方案](windows-validation-plan.md)单独验证。

不使用 Wine、远程 Windows、预先由 Windows 产生的构建文件或跳过的 Windows 测试满足 Linux 门禁。不把 JS 重写的状态机测试当作 C# 生产代码的验证。浏览器模拟宿主输入可以替代本轮的物理剪贴板输入，不可替代 Windows 验收。

## 2. 工程准备

### 2.1 分离构建目标

当前 Directory.Build.props 给全部六个项目指定 `net10.0-windows` 和 `win-x64`。实施时将平台属性移入项目声明：

| 项目 | 目标 | Linux 执行范围 |
| --- | --- | --- |
| DshLauncher.Core | net10.0、无 RID | 完整编译和行为验证 |
| DshLauncher.Core.Tests | net10.0、无 RID | 直接运行现有 xUnit v3 可执行 runner |
| DshLauncher.Platform.Windows | net10.0-windows、win-x64 | 尝试交叉编译；系统调用不执行 |
| DshLauncher.Desktop | 同上，保留 WPF/WinForms/WebView2 | 尝试交叉编译；窗口与原生消息不执行 |
| 两个 Windows 测试项目 | 保留现有 Windows 目标 | 尝试交叉编译；测试执行放入 Windows 专项 |

公共 props 保留语言版本、分析器、警告即错误、确定性和锁文件规则。Core 不得引入 WPF、WebView2、P/Invoke 或 FrameworkReference。Core.Tests 必须同时去掉 Windows RID，不能只改 TFM。

保留 global.json 的 SDK 10.0.400 精确锁定和 Microsoft.Testing.Platform 配置。新增 `DshWindowsLauncher.Portable.slnx` 仅包含两个可移植项目；完整原 solution 不移除 Windows 项目。受影响 lock 文件在工程变更时显式更新，验证时一律 locked restore。

### 2.2 门禁配置与原门禁衔接

新增 `eng/verification-profiles.json`：列出六个项目的预期 TFM/RID、允许执行 OS、测试程序集和证据类型。新增 `eng/verification-tests.ps1` 共享 runner 枚举、实际用例核对和结果解析，`eng/verify-portable.ps1` 调用它。

必须同时调整原 `eng/verify.ps1` 和 `eng/common.ps1` 的以下断言，保留正式门禁强度：

- 原先用公共 props 字符串检查 Windows TFM/RID，改为核对各项目最终 MSBuild 求值。
- 三测试项目统一 `--runtime win-x64` 改为按 profile 调用；Core 不传 Windows runtime。
- portable 只恢复两个项目时按对应清单核对新生成 lock/assets；完整 Windows 验证继续覆盖六份，不能借用旧 obj。
- `eng/expected-test-dataset.json` 保持全量真值；Linux 按 Core 子集精确比较用例集合。当前清单为 Core 49、Platform 25、Acceptance 35，不代表本轮执行数量。
- 增加用例时记录名称差异和原因，显式更新基线与触发标签；不自动用运行结果接受新基线，不吞错、不关闭 failSkips、不允许零用例成功。
- Core 纯净性、生产引用图、正式发行身份、发布常量与 Windows 安装门禁继续有效。不要为了非 Windows 开发删除这些检查。

## 3. 开发工具和命令契约

Linux runner 使用固定 .NET SDK、PowerShell 7、Node、pnpm 和 Chromium。Node/pnpm 满足所选 Harness/all 的 engines，并在 `compatibility-lock.json` 中记录确切版本及 tarball integrity；不在 CI 使用浮动 latest。Playwright 锁定版本，浏览器依赖安装在 runner 准备阶段。

主入口拟为：

```bash
pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable
```

该入口先检查平台和工具版本、创建输出目录，顺序执行属性/锁文件/架构检查、Core、插件、浏览器、真实宿主及 tarball 安装，最后汇总。D02 先提供 -Profile Core，只运行已登记的 Core 阶段检查；其通过不能输出 DevelopmentReady。完整 Development profile 对尚未实现的必需项报 incomplete，不能使用空脚本、零用例或假 pass。

D03–D23 各任务完成时同步注册该任务的检查与证据；D24 汇总已存在的完整门禁。若后来在同一 Core.Tests 程序集增加需要 Node/浏览器的互操作用例，应在验证 manifest 明确划分 Core 与 Interop 用例集合，二者并集覆盖该程序集的完整基线；筛选使用实际 runner 已验证支持的能力，不能把未选择的集合伪装为跳过后通过。Development 和 Windows 完整门禁必须覆盖两者，纯 Core profile 只声明自己的范围。

内部核心命令应沿用现有 runner 语义；以下示例适用于初始纯 Core 用例集，互操作集合加入后由共享 helper 按显式 manifest 调度并核对全集：

```bash
dotnet restore DshWindowsLauncher.Portable.slnx --locked-mode
dotnet format DshWindowsLauncher.Portable.slnx --verify-no-changes --no-restore
dotnet build DshWindowsLauncher.Portable.slnx -c Release --no-restore --warnaserror
dotnet run --project tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj -c Release --no-build --no-restore --no-launch-profile -- -list full/json -preEnumerateTheories -printMaxStringLength 0 -noColor -noLogo
dotnet run --project tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj -c Release --no-build --no-restore --no-launch-profile -- -explicit on -failSkips -preEnumerateTheories -printMaxStringLength 0 -noColor -noLogo -reporter quiet -result-xml artifacts/verify-portable/core-results.xml
```

运行器输出必须与当前基线的预期用例集合比较；不能只看退出码。当前项目选择了 MTP，因此不得未经验证改为普通 `dotnet test`。[Microsoft MTP 运行说明](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-run-and-debug)

插件项目拟提供以下 scripts，目录在当前仓库内：

```bash
pnpm --dir plugins/dsh-remote-attachments install --frozen-lockfile
pnpm --dir plugins/dsh-remote-attachments run typecheck
pnpm --dir plugins/dsh-remote-attachments run build
pnpm --dir plugins/dsh-remote-attachments run test:unit
pnpm --dir plugins/dsh-remote-attachments run test:browser
pnpm --dir plugins/dsh-remote-attachments run test:integration
pnpm --dir plugins/dsh-remote-attachments run test:pack
```

构建完成后对最终 host/client bundle 运行集成，不仅对源码跑单测。`test:pack` 生成并检查实际 tarball，再在第二个隔离 profile 安装此 tarball 验证入口、依赖和资源；只通过 link 安装不算打包验证。

### 3.1 完整 Linux 交叉构建

首批尝试固定 SDK 的完整 solution 构建，后续在支持该工具链的 Linux runner 中将其设为必过项：

```bash
dotnet restore DshWindowsLauncher.slnx --locked-mode -p:EnableWindowsTargeting=true
dotnet format DshWindowsLauncher.slnx --verify-no-changes --no-restore
dotnet build DshWindowsLauncher.slnx -c Release --no-restore --warnaserror -p:EnableWindowsTargeting=true
```

不要向整个 solution 强制 `-r win-x64`，否则又污染可移植测试。微软的 EnableWindowsTargeting 支持非 Windows 下载目标包，但不能据此预先宣布本仓库 WPF 编译成功；交叉编译也不执行 Windows API，更不生成正式安装包。[官方说明](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)

普通 C#、XAML、引用或生成资源错误必须修复。若确有可复现、经最小案例确认的 SDK/任务平台限制，记录完整命令、日志和最小案例，把受影响的原生编译明确交接 Windows 专项；仍继续完成 Core、插件和宿主开发验证，不等待 Windows runner。不得移除项目、加空 stub 或忽略任意失败将交叉编译记为通过。该结果记为 `toolchainUnsupported`，而非 pass。

## 4. 分层验证矩阵

| 编号 | 层级与真实执行内容 | 关键判据 |
| --- | --- | --- |
| L01 | MSBuild 求值、包锁、架构 | Core/Core.Tests 无 Windows RID/API；Windows 项目平台未被误改；六项目清单一致 |
| L02 | C# Core 生产协调器 | 分块、ACK窗口、取消、超时、乱序、重复、尺寸溢出、源异常；所有流按所有权释放 |
| L03 | C#/TS 双向协议 | 同一黄金样本与异常样本；字段/大小写/整数边界/错误码一致；生产 codec 生成的样本被对端读取 |
| L04 | JS 生产接收器 | 最多两块在途；超限拒绝；恶意 offset/seq/跨操作不得混入；HTTP 下无 Web Crypto 时哈希仍正确 |
| L05 | Linux Chromium 测试页 | 模拟 WebView bridge 消息产生 File；中文/空格名、零字节、PNG、批次上限；不是模拟整个 Harness |
| L06 | 真实 Harness＋全家桶＋附加插件 | 实际 host/client 只挂载应有实例；脚本早期注入；原配对/设置/附件 UI 不受替换 |
| L07 | 真实上传和发送 | `/remote/api/session/uploadFileBinary`；免 Cookie 设备头；receipt envelope；发送前无模型请求；发送后工具读回准确字节 |
| L08 | 会话并发与安全 | 两个目标、两个会话、新草稿、子代理、锁定 composer、后台页、iframe、导航及迟到 ACK；零错投 |
| L09 | 网络与失败 | HTTP 非 loopback、HTTPS受信任测试证书；断流、超时、413、403/401、撤销、重启、上传失败；不自动重发消息 |
| L10 | 打包安装 | 实际 tarball，独立隔离 profile，最终 bundle，锁定依赖；包不包含凭据或测试数据 |
| L11 | 资源与退出 | 最大批次、重复30轮；接收内存有上界，取消释放缓存；日志无秘密；不删除共享附件 |
| L12 | 全 solution 交叉构建 | 支持工具链时必过；已证实平台限制独立记录，不影响其他必需层执行 |
| L13 | 真实 C#↔Chromium↔Harness 互操作 | 生产 C# sender/codec 与生产 JS receiver 互通，真实 ACK 回流、草稿确认和工具读取；测试通道仅替代 WebView2 边界 |

Mock 只用于网络错误、可控时钟/文件源等边界。L06–L09 必须运行真实 Harness 和全家桶；不能用自制 `/remote` 服务器替代真实成功路径。失败注入可在真实服务前放测试代理。

### 4.1 任务与验证的对应关系

| 任务 | 增量接入的验证 |
| --- | --- |
| D00 | 基线、工具与资料可用性；不宣称功能验证 |
| D01–D02 | L01、现有 Core 用例和格式检查；首次尝试 L12 |
| D03–D08 | L06 的挂载/真实配对、L07 上传与草稿各自的独立路径；插件包和最小 fixture |
| D09 | L06–L07 的最小真实发送闭环，包含确定性 provider 和实际工具读取 |
| D10–D13 | L02–L05 协议、分块、接收器、能力桥及对应组件回归 |
| D14–D17 | 生产 Core 边界与组合验证，完整 Windows 代码检查，受影响 L12 再尝试 |
| D18 | L13 生产互操作及其在完整 manifest 中的注册 |
| D19–D21 | L08–L09、L11 故障、隔离和资源 |
| D22–D23 | L10 最终包安装、L06 兼容/停用/更新后的行为 |
| D24 | 同一候选上的 L01–L13 完整汇总与有效证据校验 |
| D25 | 交付证据、tarball 摘要、兼容清单及 Windows 待验清单 |

表中的层级可能在多个任务中增量完成；例如 D09 的单次上传通过不代表 L09 的全部故障场景通过。任务 done 只表示自身明确的验收成立，项目 DevelopmentReady 仍需全部必需矩阵有效。

### 4.2 生产互操作的最低实现

在既有 Core.Tests 中运行实际生产协调器和字节源。测试通道可使用子进程标准输入输出连接 Node 驱动，再由 Node 将消息送入 Chromium 的生产插件 receiver；反向 ACK、取消和 import-result 原样回到同一 C# 协调器。传输驱动只搬运消息，不计算窗口、构造替代分块或代替接收器确认。

浏览器访问真实 Harness＋全家桶＋构建后的附加插件。正常路径必须经过生产 File 组装、DataTransfer 草稿适配、真实上传、receipt、主动发送及真实工具读取。至少验证多文件含空文件和中文名、截图图案、两个在途块边界、重复 file-end 不重复导入，以及会话切换时迟到结果拒绝。

此测试同时检验双端序列化和运行时交互，不能被黄金样本、JS 模拟发送器或分别通过的单元测试替代。保存脱敏双向消息 trace、fileId→attachmentIds 映射、内容哈希和子进程退出证据。Node/Chromium/宿主夹具是测试依赖，不进入 Launcher 正式运行依赖；Windows 完整门禁执行同一用例集合时也须提供对应测试工具，缺少工具不得静默跳过。

## 5. 隔离宿主与全家桶实验

### 5.1 可重复环境

在测试工作目录创建私有 DSH_HOME 与 profile，使用固定 Harness/all/tarball 版本，记录最终解析包、源码 commit 和 integrity。禁用该测试实例的主动更新、自动隧道和无关外网任务，不改变用户生产配置。

服务绑定测试网络。HTTP 访问必须使用非 localhost/127.* 的地址，例如容器网络地址或 Linux 实例的私网地址，确保走真实 remote 模式；不能用 loopback 通过代替 HTTP LAN。HTTPS 使用测试 CA 并在专用浏览器上下文信任，不能设置全局忽略证书错误。

独立 test helper 只向测试实例的 loopback 铸造配对令牌，浏览器执行实际 accept，日志隐藏令牌和设备凭据。同时覆盖 cookie 及 `/pair-app?device=...` 的免 Cookie 流。不得将控制面通过公网代理开放用于测试方便。

### 5.2 草稿输入 PoC

在真实全家桶页面加载 addon overlay 的 session scope，取得所属 composer 的唯一文件 input。公开状态访问路径为 `scope = ctx.sessions.scope(sessionId)`、`input = ctx.conversation.input.for(scope)`、`input.state.getSnapshot()/subscribe`；scope 不存在即拒绝。捕获 sessionId/composerEpoch，在同一同步任务中读取旧 attachmentIds、用 DataTransfer 写入 input.files 并只触发一次 change、读取新 attachmentIds。成功条件为旧 IDs 完整保留、新增 IDs 数等于本次导入调用的文件数、顺序及上下文对应，随后记录 batchId/fileId→attachmentIds 映射并返回 staged ACK。正式桥每次 file-end 调用一项，因此新增数为 1；批量 PoC 按实际调用项数比较。订阅用于后续上传变化，不能把无关异步新增 IDs 归给本批。

必须额外检查 input phase、composer blocks、可编辑状态和会话类型；hidden file input 的 disabled 本身不足以证明允许导入。图片校验拒绝或数量不符时报告明确部分结果，不自动重新触发 change。切会话过程中一律取消旧导入。

PoC 必须同时证明普通文件和截图可用，并不影响全家桶原附件 UI、主题、设置和拖拽。找不到唯一 input、拿不到可靠会话/状态确认、或上游版本不匹配则失败；先定位原因，不改用 React 私有字段或 document 合成 drop。

### 5.3 模型与真实读取

无需真实账号或付费模型。用本地确定性 provider stub 实现 Harness 所需协议：收到含只读文件引用的请求后，产生可执行的文件读取工具调用；由真实 Harness 工具执行，再由 stub 校验返回正文或哈希后完成响应。工具调用名和参数以固定版本实际注册工具为准，不能自制假工具结果。

对 PNG，核对 provider 请求中的实际图像字节；如上游规范化图像，验证尺寸、格式和预设测试图案，不能要求转码后哈希等于原图。此步骤证明发送协议与工具路径可用，不宣称真实模型视觉理解准确率。

## 6. 证据输出与判定

拟输出目录 `artifacts/verify-portable/`：环境清单、项目求值、Core 枚举/XML、协议结果、Node/浏览器测试报告、Playwright trace、脱敏网络路径、宿主日志、fixture 哈希、tarball 清单与 SHA-256、交叉构建日志。

摘要使用 `portable-verify-summary.json`，至少包括：

```json
{
  "schemaVersion": 1,
  "commit": "实际提交或工作树内容摘要",
  "platform": "linux",
  "verificationProfile": "Development",
  "portableStatus": "pass|fail",
  "crossBuildStatus": "pass|fail|toolchainUnsupported",
  "requiredCases": 0,
  "executedCases": 0,
  "failedCases": 0,
  "skippedCases": 0,
  "harnessIntegration": "pass|fail|notRun",
  "productionInterop": "pass|fail|notRun",
  "windowsValidation": "notRun",
  "releaseEligible": false
}
```

示例中的计数必须由实际报告填充，必需集合为零、必需项 notRun、跳过或失败均不能得到 portableStatus=pass。`toolchainUnsupported` 必须有已确认的平台限制材料；源码编译错误仍为 fail。摘要不叫原正式 verify-summary，`package.ps1` 不得接受它作为发布凭据。

开发交付需要 L01–L11 及 L13 真实通过，且 L12 的 crossBuildStatus 只能为 pass 或附有最小案例证据的 toolchainUnsupported；网络、包恢复、磁盘及其他 fail 均不合格，不能改名为平台限制。所有已发现源码错误必须修复，Windows 适配代码完整，实机方案及输入材料齐备。此时可报告“非 Windows 开发验证通过，Windows 编译/实机待验证”，不可写“所有测试通过”。

## 7. 方案证据边界

本方案基于先前已读取的工程目标、MTP runner、固定用例清单与上游源码。本次只修订任务和验证要求，尚未在 Linux 执行上述新门禁。源码提取的既有 Node 探针只能证明脚本级请求改写，不等同于 L05–L09 或 L13；其运行平台和限制见实施方案的证据记录。
