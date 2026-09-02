# DSH Windows Launcher（配对集中端）

DSH Windows Launcher 是面向 Windows 的 **DSH 配对集中端独立客户端**：它集中管理局域网内多台
DeepSeek Harness 实例的「DSH 远程访问」配对，持续发送心跳保活配对连接，并在客户端内
内嵌显示 Harness 远程界面。它不安装、不启动、不管理远端 Harness 进程，管理操作全程
不依赖浏览器。

## 产品形态

- 独立客户端窗口（WPF 原生）：目标列表（配对/连接/心跳/异常），粘贴配对链接添加
  目标、重新配对、重命名、删除、手动心跳、保活开关；关闭窗口即最小化到托盘，
  保活继续。整体为极简科技风深色界面（深蓝黑底 + 青色强调）。
- 内嵌远程界面：每个目标可在客户端内打开一个 WebView2 窗口，直接显示 Harness 的
  `pair-app?device=<id>` 远程界面（按目标隔离的用户数据目录）；弹窗折叠回当前视图，
  全程不调用外部浏览器。窗口左侧提供可折叠的目标侧边栏，展示每个目标的 IP 地址、
  配对与连接状态（心跳实时刷新），点击其他目标可直接打开其远程窗口。
- 托盘常驻：单实例运行，托盘图标展示实时配对计数，双击重新打开管理窗口，菜单提供
  退出。
- 配对协议完全由 Harness 侧 [dsh-web](https://github.com/zhu1090093659/dsh-web) 的
  **DSH 远程访问（dsh-remote-web-ui）** 插件提供：一次性令牌 `/api/pair/accept` 兑换
  设备凭据，`POST /api/pair/heartbeat` 心跳保活（默认 10 秒一次，低于主机端 25 秒
  在线窗口），`/remote` 门控通道承载配对后的全部流量。协议细节见
  [docs/pairing-hub.md](docs/pairing-hub.md)。

## 项目结构

| 项目 | 职责 |
|---|---|
| `DshLauncher.Core` | 配对链接解析、配对传输、目标目录、心跳保活调度与持久化契约；无任何项目/包引用。 |
| `DshLauncher.Platform.Windows` | 应用数据根（防重解析点）、配对文档持久化、按目标浏览器数据目录、单实例 IPC。 |
| `DshLauncher.Desktop` | WPF/WinForms 托盘宿主与组合根：启动枢纽、Web 服务与托盘。 |
| `*.Tests` | Core、Windows 平台与跨模块端到端验收测试。 |

生产项目依赖方向：

```text
DshLauncher.Core             无项目引用
DshLauncher.Platform.Windows → Core
DshLauncher.Desktop          → Core, Platform.Windows
```

## 配对与保活

1. 在 Harness 桌面端「远程访问」面板铸造二维码并复制配对链接
   （`http://<host>:<port>/pair-accept?pair=<token>`）。
2. 在本机管理页面粘贴链接：集中端执行 accept 兑换设备凭据并持久化。
3. 之后由保活调度器按 10 秒（可配）间隔发送心跳；主机吊销设备后自动转入
   「已失效」并停止心跳，等待重新配对；网络中断按指数退避重试并自动恢复。

## 构建基线

- .NET SDK `10.0.400`，禁止补丁滚动；`net10.0-windows`、`win-x64`。
- 零第三方运行时依赖：生产项目只使用 BCL 与 ASP.NET Core 共享框架。
- NuGet 中心包版本管理，每个项目提交 `packages.lock.json`。
- nullable、SDK analyzers、代码样式与警告即错误全仓库启用。

首次生成或显式刷新锁文件：

```powershell
dotnet restore .\DshWindowsLauncher.slnx --force-evaluate
```

统一验证（还原、格式、Release 构建、全部测试、架构门禁、SBOM、秘密扫描）：

```powershell
pwsh ./eng/verify.ps1
```

`eng/release-constants.json` 处于 `candidate` 状态（schemaVersion 4，固定远程访问
配对契约基线）。正式候选必须通过 `verify.ps1` 并完成安装包实机冒烟。

## 正式安装包

安装包继续采用 Inno Setup 的按用户 EXE 形态（`eng/package.ps1` /
`eng/package-unsigned.ps1`，开源发行可选 Authenticode 策略并记录 `NotSigned`）。
`eng/package-internal.ps1` 生成明确标记 `INTERNAL TEST` 的内部测试包，禁止对外交付。
