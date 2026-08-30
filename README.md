# DSH Windows Launcher

DSH Windows Launcher 是面向 Windows 的 WPF 桌面入口。它连接局域网内已经运行的 DeepSeek Harness Web UI，不安装、启动或管理 Linux Harness 

## 第三方 WebUI 兼容

启动器通过版本化兼容契约和内置只读适配器注册表评估第三方 WebUI。每次打开、刷新或恢复页面时，启动器会先在脚本关闭状态下读取同源描述符，再生成不可变能力快照并应用响应 CSP。描述符缺失、无效或没有精确规则时，页面自动使用基础兼容模式。

扩展能力只允许精确来源、路径、方法和资源类型。首次使用外部依赖前会显示原生确认，用户可在兼容状态面板撤销确认。下载、页面权限、证书绕过、DevTools、原生桥接、任意外部脚本和未审核外站仍被禁止。服务端已有 CSP 会保留，并与启动器策略共同生效。

[dsh-web](https://github.com/zhu1090093659/dsh-web) 是参考提供方，不在产品代码中拥有专用白名单。当前生产注册表没有扩展规则，因此其页面只获得基础兼容能力。契约语义见 [WebUI 兼容契约](docs/webui-compatibility-contract.md)，参考状态见 [dsh-web 参考适配器](docs/reference-adapters/dsh-web.md)。

## 项目结构

| 项目 | 职责 |
|---|---|
| `DshLauncher.Core` | 目标目录、探测、配对、会话和忘记事务；不得引用 WPF、WebView2 或 Windows 适配器。 |
| `DshLauncher.Compatibility` | 严格解析描述符与内置注册表，生成不可变页面能力快照；不得引用 UI、WebView2 或 Windows 适配器。 |
| `DshLauncher.WebView` | 绑定单一目标、origin 和 UDF 的受限 WebView2 内容宿主。 |
| `DshLauncher.Platform.Windows` | 文件系统、网络类别、IPC、剪贴板、Runtime 和系统集成适配器。 |
| `DshLauncher.Desktop` | WPF 表示层、单实例入口、窗口协调和应用组合根。 |
| `*.Tests` | Core、Compatibility、WebView、Windows 平台和跨模块验收测试。 |

生产项目依赖方向：

```text
DshLauncher.Core                    无项目引用
DshLauncher.Compatibility           无项目引用
DshLauncher.Platform.Windows        → Core, Compatibility
DshLauncher.WebView                 → Core, Compatibility
DshLauncher.Desktop                 → Core, Compatibility, Platform.Windows, WebView
```

`Desktop` 是唯一组合根。`Core` 与 `Compatibility` 都不反向引用 UI、WebView2 或 Windows 适配器。

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

## 未签名内部测试安装包

`eng/package-internal.ps1` 只在 `releaseStatus=development` 时生成明确标记为
`INTERNAL TEST`、`UNSIGNED`、`NOT FOR PRODUCTION USE` 的内部测试安装包。它使用独立的
产品名、EXE 名、Inno Setup `AppId`、安装目录、应用数据根和单实例身份，不得覆盖或升级
正式安装。

该入口先运行统一 `verify.ps1`，并把 PASS 摘要绑定到同一个 clean Git 提交。它只接受
匹配冻结版本和 SHA-256、带有效 Microsoft 签名及可信时间戳的 WebView2 Evergreen
Bootstrapper。内部测试应用、安装器和卸载器保持未签名，不属于正式候选版本，禁止上传到
正式 Release 或交付给最终用户。仅在当次获得明确上传授权后，才可作为
`prerelease=true` 的预发布上传；标题、正文和文件名必须完整保留 `INTERNAL TEST`、
`UNSIGNED`、`NOT FOR PRODUCTION USE`，且不得设为 latest 或正式发布。脚本只生成产物，
不自动执行安装或卸载。
