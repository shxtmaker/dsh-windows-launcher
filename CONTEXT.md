# DeepSeek Harness 配对集中端（Windows）

本上下文描述 Windows 配对集中端与 Harness 侧「DSH 远程访问」插件之间的产品边界和连接语言。

## Language

**配对集中端**：
Windows 上集中持有并管理一个或多个 Harness 实例配对的常驻软件。它不拥有远端服务进程的生命周期，也不渲染 Harness 界面。
_Avoid_: Harness 管理器、远程桌面客户端、启动器服务器

**正式支持环境**：
集中端发布验收和兼容性承诺覆盖的 Windows 客户端环境。当前指 Windows 11 25H2+ x64；Windows 10 22H2 x64 为兼容运行环境。
_Avoid_: 可运行环境、最低启动版本

**Harness 实例**：
一台 Linux 主机上通过独立端口运行的 DeepSeek Harness 服务及其 Web GUI。同一主机不同端口表示不同实例。
_Avoid_: Harness 主机、目标列表

**Harness 目标**：
集中端为一次配对保留的持久记录，由规范化 Web origin（协议、主机、端口）唯一标识，承载显示名称与配对凭据。同一 origin 重复添加会复用既有目标。
_Avoid_: IP 地址、服务器地址、候选目标

**配对链接**：
远程访问面板铸造的 `<origin>/pair-accept?pair=<token>` 链接。集中端只接受路径精确为 `/pair-accept`、唯一查询参数为 `pair`、且不带 userinfo/fragment 的链接。
_Avoid_: 登录地址、loopback 启动链接、token-only 输入

**配对令牌**：
链接中的一次性机密。由 Harness 侧铸造、限时、同一时间只有一枚有效；被使用/过期/停止后返回明确的错误码。
_Avoid_: launch token、登录密码

**设备凭据**：
accept 往返换回的设备身份：`Set-Cookie` 中的 cookie 名（默认 `dsh_pair`）与设备 id。它是集中端在该 Harness 上的会话凭据，持久化于应用数据根，随目标删除而销毁。
_Avoid_: 浏览器会话、WebView2 用户数据

**配对状态**：
目标的生命周期：`AwaitingPairing`（仅有地址）→ `Pairing`（accept 在途）→ `Paired`（持有凭据）→ `Revoked`（主机端吊销/停止/过期，凭据作废）。Revoked 不是离线，恢复只能重新配对。
_Avoid_: 在线目标、失败目标

**连接状态**：
保活心跳推导的运行时可见性：`Online`（最近心跳成功且在窗口内）、`Offline`（最近心跳网络失败，退避重试中）、`Unknown`（尚未心跳）。它与配对状态正交。
_Avoid_: 配对状态、健康检查

**保活心跳**：
对 `POST /api/pair/heartbeat` 携带设备 Cookie 的周期调用。默认 10 秒一次，严格低于主机端 25 秒在线窗口（`offlineAfterMs` 默认值）；它同时刷新主机端 30 天空闲清除窗口。
_Avoid_: 健康检查、自动连接

**保活调度**：
每个已配对目标的独立循环：成功即回到基准间隔；网络失败按指数退避（上限 5 分钟）持续重试；收到 401 `unpaired` 即判 Revoked 并停止循环。可用开关按目标暂停/恢复。
_Avoid_: 重连风暴、轮询探测

**集中配对事务**：
一次 accept 往返：在枢纽锁外调用传输端口，成功则提交凭据并启动保活，失败则回滚到既有状态并给出机器可读错误（`invalid`/`used`/`forbidden`/`rate-limited`/不可达）。每个 origin 同时最多一个事务。
_Avoid_: 登录会话、全局配对、Token 缓存

**远程访问插件**：
dsh-web 仓库的 `@linxin666/dsh-remote-web-ui`（「DSH 远程访问」）。它拥有 `/api/pair/*` 路由族、`/remote` 门控通道、设备会话持久化与局域网绑定；集中端是它的一个配对设备。
_Avoid_: 自研服务端、DSH 官方组件

**独立客户端**：
Windows 上的原生 WPF 管理窗口（托盘常驻）。提供目标列表、添加/重命名/删除、配对与保活控制，并在客户端内以 WebView2 打开远程界面窗口；管理操作与远程界面显示均不调用外部浏览器。
_Avoid_: 浏览器管理页、Harness 界面镜像、通用浏览器

**远程界面窗口**：
客户端内嵌显示一个已配对目标 `pair-app?device=<id>` 的 WebView2 窗口。按目标隔离的用户数据目录承载其浏览器状态；新窗口请求折叠回当前视图。
_Avoid_: 外部浏览器、多标签浏览器、远程桌面

**应用数据根**：
当前 Windows 用户持有集中端配置与配对凭据的稳定本地位置（`%LOCALAPPDATA%\DshWindowsLauncher`），由所有权标记与防重解析点检查保护。文档以主文件 + 备份的原子写持久化。
_Avoid_: 安装目录、浏览器数据目录

**验证门禁**：
`eng/verify.ps1` 的统一自动验证：发布常量契约、锁定还原、格式、Release 构建、三个测试程序集的固定数据集、VFY-01..08 触发标签覆盖、架构约束、漏洞/弃用扫描、SBOM 与秘密扫描。
_Avoid_: 构建成功、P0/P1/P2

**固定测试数据集**：
`eng/expected-test-dataset.json` 中按程序集固定用例名 SHA-256 的清单。只有显式审查并重新生成基线才能增删测试。
_Avoid_: 测试快照、临时记录

**不签名分发**：
本项目对自有产物（安装包、apphost、托管主程序集、卸载器）不加 Authenticode 签名的发布形态：
全链路反向断言 `NotSigned`，完整性由冻结 SHA-256 与 `package-manifest.json` 绑定保证；安装器的
`[Code]` 安全加固与上游第三方签名校验不因它而取消。
_Avoid_: 可选签名、临时跳过签名、免验证安装器
