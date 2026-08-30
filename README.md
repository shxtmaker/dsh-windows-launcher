# DSH Windows Launcher

DSH Windows Launcher 是面向 Windows 的 WPF 桌面入口。它连接局域网内已经运行的 DeepSeek Harness Web UI，不安装、启动或管理 Linux Harness 

## dsh-web 第三方 UI

启动器支持 [zhu1090093659/dsh-web](https://github.com/zhu1090093659/dsh-web) 注入到 Harness 根页面的第三方 UI。Linux profile 当前启用的皮肤、插件导航和服务端组件会由同一 `http://IPv4:port/` 提供，Windows 继续加载该根页面，不维护第二套 UI 选择。

兼容边界包括同源皮肤与 Wallpaper Engine iframe、`data:` 图片、绑定当前目标 origin 的 `blob:` 资源、同源 WebSocket、`dsh-market.com` 创意工坊及 Cloudflare Turnstile。服务端原有 CSP 会被保留。主窗口仍禁止任意外站 iframe、外部脚本、下载、页面权限、DevTools 和原生桥接。

Linux 浏览器的 localStorage 不会复制到 Windows 的逐目标 UDF，因此浏览器本地布局可能不同；Linux 服务端保存的活动皮肤会保持一致。使用 `dsh-remote-web-ui` 时，Windows 启动器当前只处理 Harness `?token=` 配对，不处理该插件的第二套设备配对。目标已由 `dsh-web-lan-access` 和 Harness 会话保护时，应在该插件设置中关闭“局域网访问要求配对”。完整契约见 [dsh-web UI 兼容说明](docs/dsh-web-ui-compatibility.md)。

创意工坊浏览和本机安装不依赖点赞、安装计数的人机验证。当前线上 challenge 的 CSP 存在上游 nonce 组合风险；启动器不通过扩大脚本或 RPC 权限绕过，限制与复核条件见兼容说明。

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
