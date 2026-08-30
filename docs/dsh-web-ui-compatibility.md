# dsh-web UI 兼容说明

> 状态：迁移前现行专用实现的行为基线，仅用于 P4/P5 对照。通用兼容目标以[第三方 WebUI 通用兼容契约](webui-compatibility-contract.md)为准，dsh-web 的迁移与完成门禁见[参考适配器实施说明](reference-adapters/dsh-web.md)。原子切换完成后，本文件退出规范地位并由参考适配器说明取代。

## 1. 目标与基线

Windows 启动器显示 Linux Harness 当前提供的同一套 UI，不复制、重写或选择另一套前端。兼容基线为 `zhu1090093659/dsh-web` 的根页面注入模型：Harness 继续提供 `/`，插件客户端、皮肤样式和资源由同一目标 origin 提供。

已核实的基线包括：

- `dsh-web` `0.3.5` 的根页面注入、皮肤和创意工坊契约；
- `dsh-web` `dev` 提交 `e846fb34`，包版本 `0.3.7`，目标 Harness `>=0.1.2-alpha.1`；
- 启动器固定 Harness `0.1.2-alpha.1` 的认证探测、`?token=`、根页面和 `/api` 边界。

后续 `dsh-web`、Harness、WebView2 或 LAN 插件改变资源 origin、iframe 路径、认证响应或配对机制时，必须重新核实并更新兼容清单。

## 2. 展示链路

1. Linux profile 将插件客户端注入 Harness 根 HTML。
2. 活动皮肤由 Linux 服务端状态选择，并通过同源 `/api/skin-center/v2/skins/*` 提供。
3. Windows 使用目标独立 UDF 导航同一 `http://IPv4:port/`。
4. Harness Cookie、API、流式连接和业务仍由页面处理，启动器只负责浏览器边界和 Runtime 生命周期。

活动皮肤属于 Linux 服务端状态，因此 Windows 与 Linux 会显示同一皮肤。侧栏布局、标签页和其他 localStorage 状态属于浏览器本地状态，不在 Linux 浏览器和 Windows UDF 之间同步。

## 3. 允许的浏览器能力

| 类别 | 允许范围 |
|---|---|
| 主导航 | 仅当前精确 `http://IPv4:port` origin |
| 同源资源 | 当前精确 origin 的 HTTP 资源与 `ws://` WebSocket |
| 内联资源 | `data:` 图片；仅绑定当前目标 origin 的 `blob:` 资源 |
| 同源 iframe | `/api/skin-center/we/web/*`、`/api/skin-center/we/scene-runtime/*`、`/sidebar/html/*`、绑定当前目标的 `blob:` 文档 |
| 创意工坊 | `https://dsh-market.com` 的 API/清单请求、图片与固定挑战文档 |
| 人机验证 | `https://challenges.cloudflare.com` 的 Turnstile 文档、固定路径脚本及静态资源 |
| CSP | 保留服务端全部 CSP；Document 使用 `'self'` 表达同源连接，WebSocket 再由资源策略收紧到精确 authority |

不在清单内的子资源返回 `403`，但单个可选资源被拒绝不会用原生错误层覆盖已经可用的 UI。主导航、iframe 导航、证书、协议和其他高风险操作被拒绝时仍进入阻断状态。

`dsh-market.com` 当前向 challenge 文档注入的 `/.webmcp/bridge.js`、`/mcp`、`/.webmcp/rpc/*` 和同站 `/cdn-cgi/challenge-platform/*` 脚本不是 `dsh-web` UI 或 Turnstile 的必要契约，保持拒绝。2026-08-30 实测 challenge 响应同时声明 nonce 并保留无 nonce 的内联挑战脚本；该上游 CSP 组合可能阻止点赞和安装计数的人机验证。创意工坊清单、图片、浏览和本机安装入口不依赖该结果；在上游修复并完成实机验证前，不承诺点赞与安装计数流程可用。

## 4. 保留的安全边界

以下能力不会因第三方 UI 适配而开放：

- dsh-better-sidebar 内置浏览器中的任意外站 iframe；
- 任意外部脚本或用户皮肤自行声明的网络 origin；
- 下载、文件保存、打印到文件及通用文件传输；
- 通知、摄像头、麦克风、后台剪贴板读取等页面权限；
- DevTools、默认浏览器菜单、HostObject、WebMessage 或其他原生桥接；
- `file:`、`javascript:`、未知 scheme 和证书错误绕过；
- 把 RFC1918 HTTP 伪装成安全上下文。

因此 PWA、Service Worker、需要安全上下文的 API，以及依赖任意外站嵌入或下载的可选功能不属于 UI 一致性承诺。

## 5. Linux 配置条件

目标必须继续满足启动器固定的 Harness 与 LAN 安全契约：

- 根页面和业务资源由同一 RFC1918 HTTP origin 提供；
- `dsh-web-lan-access`、Host/Origin fence 和 Harness Cookie 仍有效；
- 未认证 `/api` 与 `/`、token `303`、认证后根页面及裸 `/api` 保持固定 Harness 基线；
- 当前活动 UI 由 Linux profile 或服务端状态决定，不依赖 Linux 浏览器 localStorage。

`dsh-remote-web-ui` 默认启用 `requirePairingForLan`，会给非 loopback 桌面页面增加另一套 `/remote` 设备配对。启动器当前不接受该插件的配对码。若目标已使用 `dsh-web-lan-access` 和 Harness 会话作为局域网准入，应在 `dsh-remote-web-ui` 设置卡中关闭“局域网访问要求配对”。需要保留第二套配对时，必须另行设计 `/pair-accept?pair=`、设备 Cookie、撤销和诊断契约。

## 6. 验收

每次 `dsh-web` 或内容宿主变化至少验证：

1. Linux 激活皮肤后，Windows 根页面显示相同皮肤、插件入口和核心布局。
2. 同源脚本、样式、字体、图片、API、SSE 和 WebSocket 正常。
3. Web/Scene Wallpaper Engine iframe、创意工坊清单与图片正常；Turnstile 仅在上游 CSP 契约通过复核后验收点赞与安装计数。
4. 服务端 iframe CSP 仍存在，启动器 CSP 只增加限制，不覆盖原值。
5. 任意外站 iframe、外部脚本、下载、权限和原生桥接仍被拒绝。
6. 多目标 UDF、Cookie、缓存和 UI 状态不串目标；重启后仍显示 Linux 当前活动 UI。
7. 在相同窗口尺寸、DPI 和主题下，将 Windows WebView2 与访问同一 endpoint 的 Edge 对照；不持久保存包含 Session 内容的截图。
