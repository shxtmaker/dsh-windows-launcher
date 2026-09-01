# ADR 0006：Windows 侧重构为配对集中端，配对连接由 DSH 远程访问插件提供

日期：2026-09-02

## 状态

已接受（随配对集中端重构落地）。

## 背景

V1 的 Windows 启动器是一个 WPF + WebView2 壳：它把局域网内 Harness Web UI 嵌入
受限浏览器窗口，配对通过 harness 浏览器认证 Cookie 与 per-target 浏览器会话完成。
该形态带来三块长期成本：版本化 WebUI 兼容契约与适配器注册表、WebView2 运行时
修复与签名校验链、以及与 Harness 官方界面耦合的兼容性门禁。

与此同时，dsh-web 生态的「DSH 远程访问」（`dsh-remote-web-ui`）插件已经提供了
成熟的设备配对模型：一次性二维码令牌、可撤销/持久化的设备会话、门控 `/remote`
通道与设备在场窗口。它是 Harness 侧天然「提供配对连接」的位置。

## 决策

1. **角色重构**：Windows 侧软件从「Web UI 壳」重构为「配对集中端」——集中管理
   与多个 Harness 实例的配对、按心跳保活配对连接，并托管一个独立 Web 管理页面。
   它不再渲染 Harness 界面。
2. **配对协议外置**：配对连接完全由 Harness 侧远程访问插件提供。集中端实现其
   客户端一侧的 wire 契约（`/api/pair/accept`、`/api/pair/heartbeat`、
   `/api/pair/status`），并把契约事实冻结进发布常量 `pairingBaseline`。
3. **保活为一等公民**：每个已配对目标持有独立心跳循环（默认 10 秒 < 主机 25 秒
   在线窗口），网络失败指数退避、自动恢复；主机吊销（401 `unpaired`）立即转
   Revoked 并停止心跳。
4. **独立 Web 页面取代 WPF 窗口矩阵**：管理界面由本机 Kestrel（仅回环）托管的
   自包含页面承担；WPF/WinForms 收缩为托盘宿主与组合根。移除 WebView2、
   兼容契约与适配器注册表及其全部门禁。
5. **零第三方运行时依赖**：生产项目只引用 BCL 与 ASP.NET Core 共享框架；
   架构门禁固定 `Core ← WebUi/Platform ← Desktop` 的依赖图。

## 后果

- 兼容契约、适配器治理、WebView2 运行时修复与相关发布门禁整体删除；
  发布常量进入 schemaVersion 4（`pairingBaseline` 取代上述基线）。
- 集中端与远程访问插件形成显式客户端契约，插件线升级需按
  `docs/pairing-hub.md` 复核 wire 事实。
- 逆火：管理页面只绑定回环，远端浏览器不能直接管理集中端（刻意如此——
  设备凭据不应暴露给局域网 UI）。
- 安装器仍按用户 EXE 形态交付；WebView2 运行时对产品不再是前置条件。
