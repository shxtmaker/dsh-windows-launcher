# v2.0.8 安装包体积评估

评估日期：2026-09-06。计量单位采用 MiB，1 MiB = 1,048,576 bytes。

## 结论

显著缩小安装包可行，主要方式是增加在线安装版本，将 WebView2 Evergreen 离线安装器改为按需下载。基于 v2.0.8 原始发布文件的隔离测量，仅保留应用及在线 Bootstrapper 的载荷包为 **53.02 MiB**；相同测量脚本包含离线运行时的载荷包为 **299.44 MiB**，相差 **246.42 MiB（82.29%）**。这些是载荷对比结果，尚不是正式在线安装包的验收结果。

保留完整离线安装能力时，单纯提高压缩等级的收益很小：`lzma2/max` 改为 `lzma2/ultra64`，隔离载荷包仅减少 **1.06 MiB（0.35%）**。建议保留现有离线发行方式，后续单独实现具有同等安装保护和验证门禁的在线发行方式。本次仅完成评估，未修改正式打包脚本及其安装行为。

## 基线与组成

基线来自 `artifacts/package/2.0.8/release/package-manifest.json`，源提交为 `5f33e9ff9f753f4950158d5281842a531d519ba6`，记录的源工作树状态为 `clean`。本次重新读取实际文件长度，并验证正式安装包 SHA-256 与清单一致：

```text
522BF40A8C1CB94A2BC712BAB49B2B496CCCD76005E054BA06B3DA093C6BD681
```

| 对象 | 实测 bytes | MiB | 说明 |
| --- | ---: | ---: | --- |
| v2.0.8 正式安装包 | 315,102,618 | 300.51 | Inno Setup 最终 EXE |
| WebView2 Evergreen 离线安装器 | 258,510,544 | 246.53 | 外部输入 `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` |
| publish 目录，486 个文件 | 184,435,270 | 175.89 | 未压缩，含自包含 .NET、WPF、WinForms 和 Bootstrapper |
| publish 内在线 Bootstrapper | 1,783,000 | 1.70 | 已包含在上一项中，不应重复相加 |
| 13 种语言的卫星资源 | 17,844,072 | 17.02 | publish 子集 |
| 除简体、繁体中文外的 11 种语言资源 | 15,334,296 | 14.62 | publish 子集 |
| PDB 符号文件 | 97,228 | 0.09 | publish 子集 |
| XML API 文档 | 797,132 | 0.76 | publish 子集 |

publish 中最大的三个文件是 `System.Private.CoreLib.dll`（16,033,616 bytes）、`PresentationFramework.dll`（15,816,528 bytes）和 `System.Windows.Forms.dll`（13,715,240 bytes）。因此，本地程序目录的主要体积来自框架依赖；调整页面布局不会明显改变安装包大小。上述组成直接测量自 v2.0.8 的 staging 文件，不能把未压缩文件大小直接当作安装包内占比。

正式入口 `eng/package.ps1` 使用 `--self-contained true`、`PublishSingleFile=false`、`PublishTrimmed=false`；`installer/DshWindowsLauncher.iss` 已使用 `Compression=lzma2/max` 和 `SolidCompression=yes`。正式安装器包含离线运行时，同时在安装前执行版本检查、隔离环境健康检查和必要的运行时修复。

## 隔离压缩实验

使用现有 Inno Setup 7.0.2 编译器，将未修改的 v2.0.8 publish 目录作为输入，生成仅用于体积测量的安装器。离线组按“离线运行时、publish 全目录”的相同文件顺序压缩，所有组均启用 solid compression。实验没有运行生成的安装器，也没有改写 v2.0.8 产物。

| 实验 | bytes | MiB | 对比结果 |
| --- | ---: | ---: | --- |
| 离线运行时 + publish，`lzma2/max` | 313,981,633 | 299.44 | 实验基准 |
| 离线运行时 + publish，`lzma2/ultra64` | 312,873,852 | 298.38 | 减少 1,107,781 bytes，0.35% |
| 仅 publish，`lzma2/max` | 55,593,137 | 53.02 | 相对实验基准减少 258,388,496 bytes，82.29% |
| 仅 publish，排除 11 种非中文资源、PDB、XML | 53,859,004 | 51.36 | 相对仅 publish 再减少 1,734,133 bytes，1.65 MiB |

最后一组保留 `zh-Hans`、`zh-Hant`，排除 `cs`、`de`、`es`、`fr`、`it`、`ja`、`ko`、`pl`、`pt-BR`、`ru`、`tr` 及 `*.pdb`、`*.xml`。它仅验证压缩收益，没有验证语言回退及应用运行兼容性。

实验 `.iss`、脚本、日志和结果保留在本地 `artifacts/package-size-assessment/`。原始脚本为 `measure.ps1`，前三组结果保存在 `results.jsonl`；最后一组通过 `app-languages-symbols-cleaned.iss` 编译，其结果直接读取生成 EXE 的文件长度。可使用同一编译器重新编译各 `.iss` 复核。

**实验安装器没有正式安装器的完整元数据、界面资源、辅助文件、安装保护和生命周期逻辑，不能发布给用户。** 实验基准与正式包相差 1,120,985 bytes，因而 53.02 MiB 应视作在线方案的载荷参考，而非承诺的正式包大小。solid compression 下各项收益也不能简单相加。

## 方案与边界

### 增加在线安装版本：收益最大

微软明确区分两种 Evergreen 分发方式：Bootstrapper 从微软服务器下载并安装匹配架构的运行时，Standalone Installer 支持离线安装。共享 Evergreen Runtime 已存在且健康时，可以直接复用；仍需检测缺失或异常状态。[微软 WebView2 分发文档](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

建议在线版本继续自包含 .NET，只改变 WebView2 的获取方式。该方案会把运行时下载流量转移到首次安装或修复时，并不消除运行时本身。代理限制、断网、下载中断及企业网络访问限制必须有明确的失败处理；离线环境继续使用完整离线包。正式包中现有的版本门槛、健康探测、修复后的复检及签名验证应保持一致。

仓库现有 `installer/DshWindowsLauncher.UnsignedRelease.iss` 头部明确说明，它不包含 `[Code]` 中的路径验证、重解析点拒绝、目录句柄锁定、就地升级身份校验等加固，也不执行真实安装/卸载验证；它与正式包的 AppId 不同。**不能把现有轻量内网包直接作为正式在线包替代品。** 正式在线版本应以 `DshWindowsLauncher.iss` 和 `eng/package.ps1` 的完整保护为基础实现。

### 提高压缩等级：收益有限

当前已经启用较强的 LZMA2 与 solid compression。官方文档指出，提高等级会增加压缩时间及内存需求；`max` 的字典为 8 MiB，`ultra64` 为 64 MiB。solid compression 还会牺牲随机访问能力，安装器读取后续文件时可能需要先解压前面的流。[Inno Setup 压缩配置](https://jrsoftware.org/ishelp/topic_setup_compression.htm)、[SolidCompression](https://jrsoftware.org/ishelp/topic_setup_solidcompression.htm)

实测不足 0.4% 的离线包收益，不足以使提高等级成为优先事项。如果以后采用，应重新测量完整正式包的构建资源、运行时修复等待时间和安装峰值资源。

### 排除非必要资源：小幅优化

本次排除资源、PDB 和 XML 后，压缩收益为 1.65 MiB。可以在明确支持语言后再实施。必须验证非中文 Windows 上的系统对话框、错误信息和资源回退；PDB 应保存在单独的诊断归档中。本次没有删除产品目录中的任何资源或 DLL。

### 改成依赖预装 .NET：技术可行，部署条件改变

官方说明，framework-dependent 发布可减少应用分发内容，但目标机器必须预先安装兼容 .NET 运行时；self-contained 则包含所需运行时。本项目为 `net10.0-windows` 且使用 WPF/WinForms，实施时需要检测兼容的 Windows Desktop Runtime，不能只检测一般 .NET Runtime。[微软 .NET 发布模式](https://learn.microsoft.com/en-us/dotnet/core/deploying/)

该方向更适合统一管理依赖的企业环境。此次未生成或测试 framework-dependent 发行包，不给出体积承诺，也不建议替换默认自包含发行方式。

### 不采用 WPF trimming 或手动删除框架程序集

微软当前文档明确说明 WPF 不兼容 trimming，.NET SDK 已禁用 WPF 裁剪支持；WinForms 同样存在限制。本项目同时启用两者，因此不应直接设置 `PublishTrimmed=true`，也不应绕过 SDK 限制或根据文件名手工删除框架 DLL。[微软 trimming 已知不兼容项](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)

## 后续实施验收

1. 保留离线版本，新增在线版本的明确发行身份、清单及说明。
2. 在缺失、低版本、健康和损坏 WebView2 状态下分别验证安装、升级和修复；覆盖联网、断网、代理失败和下载中断。
3. 保留正式安装器的路径、重解析点、身份及卸载保护，执行真实安装/卸载验证与现有统一验证门禁。
4. 用完整正式构建重新记录 EXE 字节数、SHA-256、供应链输入及安装资源占用，再冻结在线包体积目标。
