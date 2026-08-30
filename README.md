# DSH Windows Launcher

DSH Windows Launcher 是面向 Windows 的 WPF 桌面入口。它连接局域网内已经运行的 DeepSeek Harness Web UI，不安装、启动或管理 Linux Harness 进程。

开发和验收以 [V1 实施方案](docs/v1-implementation-plan.md) 与 [V1 发布检查表](docs/v1-release-checklist.md) 为准。

## 项目结构

| 项目 | 职责 |
|---|---|
| `DshLauncher.Core` | 目标目录、探测、配对、会话和忘记事务；不得引用 WPF、WebView2 或 Windows 适配器。 |
| `DshLauncher.WebView` | 绑定单一目标、origin 和 UDF 的受限 WebView2 内容宿主。 |
| `DshLauncher.Platform.Windows` | 文件系统、网络类别、IPC、剪贴板、Runtime 和系统集成适配器。 |
| `DshLauncher.Desktop` | WPF 表示层、单实例入口、窗口协调和应用组合根。 |
| `*.Tests` | Core、WebView、Windows 平台和跨模块验收测试。 |

生产项目依赖方向：

```text
DshLauncher.Core
├── DshLauncher.Platform.Windows
├── DshLauncher.WebView
└── DshLauncher.Desktop
    ├── DshLauncher.Platform.Windows
    └── DshLauncher.WebView
```

箭头按“被依赖项在上、依赖项在下”表达。`Desktop` 是唯一组合根；`Core` 没有项目或第三方包引用。

## 构建基线

- .NET SDK `10.0.400`，禁止补丁滚动。
- `net10.0-windows`、`win-x64`。
- WebView2 SDK `1.0.4129.50`。
- Release 主程序为自包含、多文件、非裁剪发布。
- NuGet 使用中心包版本管理，每个项目生成并提交 `packages.lock.json`。
- nullable、SDK analyzers、代码样式和警告即错误在所有项目统一启用。

首次生成或显式刷新锁文件：

```powershell
dotnet restore .\DshWindowsLauncher.slnx --force-evaluate
```

日常验证只允许锁定还原：

```powershell
dotnet restore .\DshWindowsLauncher.slnx --locked-mode
dotnet build .\DshWindowsLauncher.slnx -c Release --no-restore
```

`eng/release-constants.json` 当前处于 `development` 状态。正式候选前必须补齐其中的 Runtime、安装器、Publisher、证书和 HTTPS 发布地址，并通过对应 JSON Schema；未知值不能用占位字符串代替。
