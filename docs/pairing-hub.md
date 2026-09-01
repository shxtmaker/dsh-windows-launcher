# 配对枢纽协议契约（dsh-remote-web-ui「DSH 远程访问」）

本文固定 Windows 配对集中端依赖的 wire 契约。锚定 `@linxin666/dsh-remote-web-ui`
0.3.x 线（发布常量 `pairingBaseline` 固定参考版本与默认值）；改动本文所列任何事实
都必须同步 `eng/release-constants.json`、`src/DshLauncher.Core/Pairing/PairingProtocol.cs`
以及 `ReleaseScriptLogicTests.PairingBaselineMatchesTheProtocolImplementation`。

## 路由族（host 侧，由插件提供）

| 路径 | 方法 | 作用 | 集中端用法 |
|---|---|---|---|
| `/pair-accept?pair=<token>` | GET | 二维码入口页（设 cookie 后跳转 `/pair-app`） | 仅作为配对链接的严格解析目标 |
| `/api/pair/accept` | POST | `{token}` → `{ok,deviceId}` + `Set-Cookie: <name>=<deviceId>` | 兑换设备凭据 |
| `/api/pair/heartbeat` | POST | 携带设备 Cookie 刷新在场 → `{ok}` / 401 `{code:"unpaired"}` | 保活心跳 |
| `/api/pair/status` | GET | `{ok,paired,phase,lanAvailable,requirePairingForLan,...}` | 端点探测（添加仅地址目标时） |
| `/api/pair/issue` `/stop` `/revoke` `/events` `/lan-bind` | 混合 | 主机面板控制面 | **仅限主机回环**，集中端不可远程调用 |
| `/pair-app?device=<id>` | GET | 免 cookie 应用落地页（`touchDevice` 刷新在场） | 管理页「打开远程界面」入口 |
| `/remote/...` | 任意 | 门控代理通道（设备凭据：cookie / `x-dsh-remote-device` 头 / `device` 查询参数） | 由配对后的浏览器/客户端使用 |

## 集中端必须遵守的事实

- **Host 栅栏**：accept/heartbeat/status 校验 `Host` 头必须等于链接宣传的
  authority（或主机回环）。HttpClient 向链接 origin 发起请求即自动满足；
  不得为请求改写 `Host`。
- **accept 语义**：成功 200 返回设备 id 并 `Set-Cookie`；`404 {code:"invalid"}`、
  `409 {code:"used"}`、`403 {code:"forbidden"}`（栅栏拒绝）、`429 rate-limited`
  （每源 10 次/30 秒）。集中端在枢纽锁外执行 accept，按状态码映射为机器可读错误。
- **心跳凭据**：heartbeat 只读 cookie。集中端以 `Cookie: <name>=<deviceId>` 头发送；
  cookie 名以 accept 响应的 `Set-Cookie` 为准（默认 `dsh_pair`，主机配置可改名）。
- **在场窗口**：主机默认 `offlineAfterMs = 25s`——25 秒内无任何门控请求/心跳即显示
  离线。集中端默认每 10 秒心跳一次（可配 2–120 秒，必须严格小于 25 秒才算持续在线）。
- **空闲清除**：设备会话 30 天无活动被主机删除（`$DSH_HOME/remote-web-ui-devices.json`，
  0600 原子写，重启后凭据依然有效）。持续心跳使该窗口永不过期。
- **吊销语义**：主机面板「停止」或逐设备取消配对后，下一个心跳 401 `unpaired`。
  集中端将其判为 `Revoked`、停止保活循环并要求重新配对；主机重启不会吊销
  （会话持久化），仅网络中断时集中端按指数退避重试。
- **凭据处置**：设备 id 即会话凭据。集中端只把它持久化在应用数据根
  （所有权标记 + 防重解析点检查 + 原子写），绝不出现在 Web API 快照中；
  「打开远程界面」按需构造 `/pair-app?device=<id>` 链接（免 cookie 流）。
- **HTTP 明文**：局域网配对运行在纯 HTTP 上（插件设计如此，cookie 无 `Secure`）；
  隧道（HTTPS）路径共用同一契约。集中端不提供 TLS 终结。

## 错误码映射（Core `HubErrorCode`）

| 插件响应 | 集中端错误 | 可重试 |
|---|---|---|
| 200 + deviceId | `Paired` | — |
| 404 `invalid`/`expired`/`stopped` | `PairingInvalidToken` | 否（刷新二维码） |
| 409 `used` | `PairingTokenUsed` | 否 |
| 403 `forbidden` | `PairingForbidden` | 否 |
| 429 `rate-limited` | `PairingRateLimited` | 是 |
| 连接失败/超时 | `PairingUnreachable` | 是 |
| 心跳 401 `unpaired` | 目标转 `Revoked`，保活停止 | 重新配对 |
