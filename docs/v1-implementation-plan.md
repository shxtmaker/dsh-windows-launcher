# DSH Windows Launcher V1 实施方案

文档编号：`DSHWL-V1-DEV`<br>
文档版本：`1.1`<br>
状态：已确认，可进入第三方 WebUI 通用兼容实施<br>
性质：独立开发交付文档<br>
更新日期：2026-08-31<br>
关联交付物：`v1-release-checklist.md`、`webui-compatibility-contract.md`、`webui-provider-onboarding.md`、`reference-adapters/dsh-web.md`

## 1. 目标与文档权威

V1 在 Windows 上提供一个独立桌面入口，用于连接局域网内已经运行的 DeepSeek Harness Web UI。用户首次输入 Linux 主机的 RFC1918 IPv4；端口留空时使用 `3080`。启动器自动探测目标，首次配对时要求用户再粘贴该 Harness 进程输出的完整 LAN 配对链接。配对成功后，启动器保存逐目标浏览器会话，后续启动自动打开默认目标。目标端点提供符合[第三方 WebUI 通用兼容契约](webui-compatibility-contract.md)的界面时，Windows 显示同一服务端选择的 UI；只依赖同源能力的界面获得基础兼容，经过审核的外部依赖通过内置适配规则获得扩展兼容。

本方案合并产品规格、术语、架构边界、实施顺序和 Linux 前置条件，是开发实施的完整独立基线。开发者无需读取临时规划票据、研究记录或对话即可排期和实现。[V1 发布检查表](v1-release-checklist.md)是独立的配套发布文档；它负责最终验证和人工发布确认，不改变本方案的产品要求。兼容契约、Schema、内置注册表、影响清单、fixture 和支持记录是本方案引用的版本化执行输入，不是可选背景材料。

规范关键词“必须”“不得”“只允许”表示发布要求。“建议”表示实现可替换，但替换后仍必须满足关联要求。

## 2. V1 产品边界

### 2.1 用户任务

1. 首次启动输入 Linux IPv4、可选端口和可选显示名称。
2. 启动器验证 Windows 网络类别、地址范围和目标服务身份。
3. 用户确认受信任局域网及 Linux 防火墙边界。
4. 首次连接时，用户从对应 Linux Harness 启动输出复制完整 LAN 配对链接并粘贴。
5. 启动器完成一次性配对，打开该目标的 Harness Web UI。
6. 用户可保存多个不同 Linux 主机，或同一主机的不同端口，并从目标中心分别打开。
7. 后续启动只自动打开默认目标；其他目标由用户主动打开。
8. 每个目标在独立窗口和独立 WebView2 用户数据目录中运行。
9. Linux 目标切换到任一契约兼容 WebUI 后，Windows 在重新打开或刷新目标时重新评估同源描述符，并显示同一服务端选择的 UI；不会维护第二套 UI 选择。

### 2.2 明确不做

- 不通过 SSH 安装、启动、停止、重启或升级 Linux Harness。
- 不结束占用 Linux 端口的远程桌面或其他进程，不扫描或猜测替代端口。
- 不支持公网、主机名、IPv6、VPN、Tailscale、SSH 隧道、反向代理或 HTTPS。
- 不实现便携版、MSIX、系统级安装、后台更新、托盘、开机启动或自动公开发布。
- 不增加后台看门狗或在宿主崩溃后自动恢复全部窗口。
- 不新增原生网页桥接、通用 Windows 到 Linux 文件传输或任何 Linux 到 Windows 下载。
- V1 只保留 Harness 页面现有的图片附件拖放和粘贴。
- 不把任意网站、独立端口、自定义认证体系或未绑定目标会话的页面纳入契约兼容 WebUI。
- 不同步 Linux 浏览器与 Windows UDF 的 Cookie、Web Storage、布局、主题或其他浏览器本地状态，也不承诺逐像素一致。
- 不提供运行时规则下载、后台规则更新、独立规则包、手工导入、环境变量覆盖、远程 kill switch 或页面自授权。
- 不允许适配规则突破下载、页面权限、证书、DevTools、原生桥接、任意外部协议和其他全局能力上限。

## 3. 固定基线与发布输入

### 3.1 已固定值

| 项目 | V1 值 |
|---|---|
| 产品名 | `DSH Windows Launcher` |
| 主程序 | `DshWindowsLauncher.exe` |
| 应用数据标识 | `DshWindowsLauncher` |
| Inno Setup `AppId` | `4440FC88-98CA-403E-8E20-3DFEBEF0E609` |
| 正式平台 | Windows 11 25H2+ x64 |
| 兼容平台 | Windows 10 22H2 x64，尽力兼容 |
| UI 技术 | C#、.NET 10 LTS、WPF |
| Web 容器 | Microsoft Edge WebView2，Evergreen Runtime |
| Harness | commit `cd5ef8148158c3a752a658978873241fdf8e2bbc`，`dsh-v0.1.2-alpha.1` |
| LAN 插件 | `dsh-web-lan-access 1.2.1` |
| WebView2 SDK | `1.0.4129.50` |
| Linux 参考系统 | Ubuntu Server 24.04 LTS x64 |
| 默认目标端口 | `3080` |
| 同时活动窗口承诺 | 四个目标窗口 |
| 首个正式版本 | `1.0.0`；候选版本 `1.0.0-rc.N` |

依赖基线在 M0 可显式刷新一次。刷新后必须重新核实 Harness、插件和 WebView2 契约，并把精确值写入发布常量。后续不得静默跟随“最新版本”。

### 3.2 实施可开始但正式发布前必须补齐

- 精确 .NET SDK 补丁版本和 `global.json`。
- 固定 Inno Setup 版本。
- 已实测的最低 Evergreen WebView2 Runtime 版本。
- Publisher、证书主体和签名设施。
- 固定 HTTPS 官方发布地址。
- `contractVersion`、描述符 Schema 版本与 SHA-256、`registryVersion`、规范化注册表 SHA-256、活动规则和墓碑摘要。
- 首个提供有效描述符的 dsh-web 精确版本、`sourceRev`、提供方套件摘要和参考规则版本。

上述发布身份集中保存在受版本控制的发布常量和机器清单中，再由项目文件、安装脚本、诊断版本信息和证据生成器读取。正式产物不得保留占位值；签名凭据不得进入仓库或普通 runner 文件系统。当前 `1.0.0` 尚未正式发布，因此通用兼容并入首个正式 `1.0.0`；若实施开始前该版本已经形成正式签名发布，则目标自动提升为 `1.1.0`，不得回写旧基线。

## 4. 规范需求

### 4.1 平台、进程与窗口

| ID | 要求 |
|---|---|
| `REQ-PLAT-001` | 正式构建只生成 `win-x64`。Windows 11 25H2+ x64 是正式支持环境；不得主动阻止 Windows 10 x64 运行。 |
| `REQ-PLAT-002` | 应用始终以当前标准用户运行，不要求或自我提升权限。 |
| `REQ-PLAT-003` | 当前 Windows 用户只允许一个宿主进程。重复启动通过仅限当前用户的 IPC 转交打开或激活请求，不得并发写目标目录或 UDF。 |
| `REQ-PLAT-004` | 目标目录为空时进入添加流程；只有一个目标时打开该目标；有多个目标时只打开默认目标。不得恢复全部上次窗口。 |
| `REQ-PLAT-005` | 同一目标最多一个目标窗口，重复打开只激活既有窗口；不同目标窗口可同时运行。关闭目标中心不关闭目标窗口，退出应用只关闭本地窗口。 |
| `REQ-PLAT-006` | 原生 UI 只交付简体中文，用户可见字符串进入 `.resx`。核心流程支持纯键盘、清晰焦点、`AutomationProperties`、Windows 高对比度和 100%、150%、200% DPI。 |

### 4.2 目标目录与默认目标

| ID | 要求 |
|---|---|
| `REQ-TGT-001` | 目标输入只接受无前导零的 RFC1918 点分十进制 IPv4。端口为空时为 `3080`；显式端口只接受十进制 `1–65535`。 |
| `REQ-TGT-002` | 目标端点由 `http://IPv4:port` 规范化并去重。同一 IP 的不同端口是不同目标，不同 IP 可使用相同端口；重复添加相同端点只激活已有目标，不新增记录或静默重命名。 |
| `REQ-TGT-003` | 每个目标进入目录时获得与端点内容无关且不可变的 `targetId`。IP 或端口变化必须添加为新目标，不能原地修改安全身份。 |
| `REQ-TGT-004` | 目标配置只保存 `targetId`、IPv4、端口、可选显示名称和带策略版本的网络信任确认；不得保存 token、Cookie、WebView 数据或最近运行状态。 |
| `REQ-TGT-005` | 空显示名称动态显示 `IP:port`。名称可重复，不参与去重、安全判断或会话定位。 |
| `REQ-TGT-006` | 非空目录必须恰有一个默认目标。首个目标进入目录时即自动成为默认，即使仍待配对；新增目标不改变默认；连接失败不改变默认或触发故障转移。 |
| `REQ-TGT-007` | 忘记默认目标时，空目录将默认引用置空；只剩一个目标时自动继任；仍有多个目标时必须先由用户选择继任目标。 |
| `REQ-TGT-008` | 目标目录采用独立整数 `schemaVersion`、完整快照原子替换和一份上一版有效备份。主快照和备份都无效时停止写入及自动连接，并保留原数据供恢复。 |
| `REQ-TGT-009` | 目录提交必须重新验证模式、目标标识、端点唯一性和默认引用。不同目标操作可并行，目录写入短时串行。 |
| `REQ-TGT-010` | 目标目录、会话元数据和诊断包模式分别使用独立递增整数，只允许已知逐版本迁移。未知新模式必须停止写入和自动连接。 |

目标目录建议采用以下逻辑结构，实际 JSON 字段名应固定并由契约测试锁定：

```json
{
  "schemaVersion": 1,
  "defaultTargetId": "<target-id-or-null>",
  "targets": [
    {
      "targetId": "<immutable-id>",
      "ipv4": "192.168.1.20",
      "port": 3080,
      "displayName": null,
      "trustPolicyVersion": 1,
      "trustedAtUtc": "<timestamp>"
    }
  ]
}
```

### 4.3 网络信任与目标探测

| ID | 要求 |
|---|---|
| `REQ-NET-001` | 只允许 `http://` 和 RFC1918 IPv4。拒绝公网、回环、链路本地、组播、未指定地址、IPv6、主机名及 URL 形式输入。 |
| `REQ-NET-002` | Windows 当前网络为“公用网络”时阻止探测、配对和连接；“专用网络”仍必须由用户确认 Linux 防火墙边界。 |
| `REQ-NET-003` | 启动器不得声称能够验证 Linux 防火墙。首次配对必须提示 Harness 会话具有高权限，并要求确认目标与局域网可信。 |
| `REQ-NET-004` | 目标探测必须由原生网络模块在任何 WebView 导航前完成，不带 Cookie、token、`Origin`、伪造浏览器头或认证状态，并禁止自动重定向。 |
| `REQ-NET-005` | 固定基线下分别执行受限响应大小的 `GET /api` 与 `GET /`。锁定的 `401/401` 状态和响应指纹只表示“支持的认证型 Harness 候选”，不是健康检查或密码学身份。 |
| `REQ-NET-006` | `403` 分类为 Harness fence 或 LAN 配置拒绝；任意 `200`、`3xx`、`404` 或未知响应分类为错误服务或不支持版本并拒绝 WebView 导航。TCP 成功和静态资源 `200` 均不能证明 Harness 身份。 |
| `REQ-NET-007` | 端点不可达、超时、Harness 未启动、错误服务、旧版无认证 Harness和默认端口被其他进程占用必须分别呈现，不得自动扫描端口或改连其他目标。 |
| `REQ-NET-008` | Harness、插件或探测指纹变化时必须重建夹具并重跑网络、配对和 WebView 受影响数据集。 |

探测超时、响应上限和重试次数属于集中策略常量。它们必须有确定性测试，并保证用户可取消；不得散落在界面事件中。

### 4.4 首次配对与浏览器会话

| ID | 要求 |
|---|---|
| `REQ-PAIR-001` | 候选目标只有在地址、网络、探测和信任确认通过后才能原子进入目录。首次配对失败或取消时保留为待配对目标。 |
| `REQ-PAIR-002` | 首次配对只接受用户显式粘贴的完整 LAN URL；不接受裸 token，不通过 SSH 或后台通道取 token，也不接受 loopback URL。 |
| `REQ-PAIR-003` | 输入可含可选 `dsh web:` 前缀和首尾空白，但只能解析一个 URL。URL 必须为 `http://`、根路径 `/`、唯一 `token` 查询参数，且无用户名、密码、fragment 或额外参数。 |
| `REQ-PAIR-004` | 配对 URL authority 必须与当前目标规范化 `IPv4:port` 完全一致。属于其他目标的链接只提示归属并拒绝，不转发、不切换目标。 |
| `REQ-PAIR-005` | 每个目标同时最多一个配对事务；不同目标可并行。事务开始前写入不含凭据的进行中标记。token 只存在于该事务内存并只发送到已验证的对应端点。 |
| `REQ-PAIR-006` | 成功必须同时满足 token 请求返回预期 `303`、无 token 根页面返回 `2xx`、带会话的 `GET /api` 通过认证边界后返回固定基线预期的 `404`；WebView2 对 HTTP `4xx` 报告的导航失败标志不覆盖该精确状态与 origin 证据，缺失或错误 Cookie 会在路由分发前返回 `401`。三项完成后才原子提交会话元数据。 |
| `REQ-PAIR-007` | 配对输入和内存副本在成功、拒绝、失败或取消后都必须清除。仅当当前剪贴板仍等于刚粘贴原文时尽力清空当前剪贴板，不读取或修改剪贴板历史和云剪贴板。 |
| `REQ-PAIR-008` | 崩溃恢复发现未提交标记时，在任何导航前删除该目标未提交 UDF 和元数据并要求新链接；不得重放旧 token 或从残留 Cookie 推断成功。 |
| `REQ-PAIR-009` | 每个 `targetId` 使用独立稳定 UDF。Cookie、Web Storage、缓存、权限和导航历史不得跨目标共享；内容宿主不得读取 Cookie 值。 |
| `REQ-PAIR-010` | 自动连接先使用对应 UDF 导航干净根页面，并验证带会话的 `GET /api` 已通过认证边界进入未认领路由、返回固定基线预期的 `404`。出现 `401`、会话元数据损坏、未知模式、origin 不匹配或目标变成错误服务时，只清除该目标会话并退回待配对。`403`、不可达和暂时页面故障保留已提交会话。 |

### 4.5 忘记目标与数据安全

| ID | 要求 |
|---|---|
| `REQ-DATA-001` | 忘记目标前显示名称和端点，关闭该目标窗口，取消其探测、配对和连接，并释放 WebView2 资源。 |
| `REQ-DATA-002` | 先删除并验证该目标 UDF、会话元数据和事务标记，再原子删除目标配置、信任确认及默认引用。会话清理失败时不得报告成功。 |
| `REQ-DATA-003` | 主快照、可恢复备份和待删除标记必须共同保证旧备份不会复活已忘记目标。崩溃后最坏只能安全退化为待配对，不得留下目录外认证 Cookie。 |
| `REQ-DATA-004` | 所有递归清理只作用于规范化后位于当前用户应用数据根内、带所有权标记的精确目录，不跟随重解析点。 |
| `REQ-DATA-005` | 一个目标的失败、忘记、重命名、默认切换或会话清理不得改变其他目标的配置、窗口、UDF 或运行状态。 |
| `REQ-DATA-006` | 应用数据根以独立版本化记录原子保存已经完整校验并成功使用的最高 `registryVersion`。水位只能单调增加；当前内置版本低于水位、记录属于未知新 Schema 或记录损坏时，全部目标只保留基础兼容。数据清除卸载可以明确删除水位。 |
| `REQ-DATA-007` | 外部能力确认和脱敏兼容诊断缓存按 `targetId` 独立保存，不写入 `targets.json`。忘记目标必须精确删除该目标的两类记录并验证消失，不影响其他目标或全局注册表水位；旧专用允许项和历史使用不得迁移为确认。 |

### 4.6 目标内容宿主与 Harness 页面

| ID | 要求 |
|---|---|
| `REQ-WEB-001` | 一个目标内容宿主在整个生命周期绑定同一 `targetId`、精确 origin 和稳定 UDF，不得原地切换主机或端口。 |
| `REQ-WEB-002` | 已配对内容宿主只导航干净 origin。顶层 Document 只允许当前精确 `scheme + IP + port`；跨 origin、`file:`、`data:`、`javascript:` 和未知 scheme 一律阻止。子资源、frame、WebSocket 和其他页面能力只能来自基础兼容或当前不可变页面能力快照。 |
| `REQ-WEB-003` | 同 origin 页面路由由 Harness 处理。启动器不得复制 Harness RPC、WebSocket、会话、消息、模型、工具或 Linux 文件业务。 |
| `REQ-WEB-004` | 全部接管 `NewWindowRequested`。用户触发的同 origin 请求可在当前目标窗口打开；其他 HTTP/HTTPS 链接显示脱敏目标并逐次确认后交给系统浏览器。 |
| `REQ-WEB-005` | 只允许用户触发的 `mailto:` 在确认后交给系统。其他外部协议和非用户触发请求全部拒绝。 |
| `REQ-WEB-006` | 取消全部 `DownloadStarting`，包括 Harness ZIP 导出、保存网页和打印到文件，并显示 V1 不支持下载。 |
| `REQ-WEB-007` | 当前所有 `PermissionRequested` 默认拒绝且不持久化；拒绝通知、后台剪贴板读取及未知能力。不得用浏览器旗标伪造安全上下文或忽略证书错误。 |
| `REQ-WEB-008` | 不调用 `AddHostObjectToScript`，不提供通用 web message、共享缓冲区、文件系统对象或其他原生业务桥接。 |
| `REQ-WEB-009` | 发布版关闭默认浏览器菜单、DevTools、检查元素、地址栏、新标签、本地文件、保存、打印和前进后退入口；保留页面编辑、页内查找、缩放及原生关闭/刷新快捷键。 |
| `REQ-WEB-010` | 页面内 RPC、WebSocket reconnecting 和业务错误由 Harness 处理。宿主只处理导航、Runtime、UDF、renderer/browser 进程和无响应故障。 |
| `REQ-WEB-011` | renderer 首次失败可重新加载；重复失败后用同一 UDF 重建 WebView。browser 进程失败时等待资源释放后重建环境。自动恢复有限且去重，最终失败进入原生恢复页。 |
| `REQ-WEB-012` | 恢复后重新读取 Harness 状态，不自动重放消息、工具调用或图片提交，不清 UDF、不改变默认目标。 |
| `REQ-WEB-013` | 全部契约兼容 WebUI 使用同一分级模型。基础兼容包括同源资源、任意精确目标 origin 的同源 frame、受父 Document 约束的 `about:blank`、`data:` 图片、WebGL 和按资源类型绑定页面能力周期的 `blob:` 能力。WebSocket、Dedicated/Shared Worker、AudioWorklet、OOPIF、逐跳重定向和 CDP Fetch 只有双 Runtime、Edge 和新契约版本门禁完成后才可授权；`about:srcdoc`、Service Worker、持久离线响应、eval、Basic Auth 和客户端证书永久禁止。 |
| `REQ-WEB-014` | 目标内容宿主在可见根导航前把严格描述符结果与只读内置注册表匹配，与全局能力上限求交，按确定性规则合并并生成不可变页面能力快照。身份、版本、adapter key、用途或规则冲突、注册表损坏、超出上限或无法确定合并结果时，扩展能力整体失效并降级到基础兼容。 |
| `REQ-WEB-015` | 资源、HTTP 方法、每个重定向跳、frame、条件 WebSocket/Worker 和追加的 Document CSP 只消费同一页面能力快照。外部被动资源限 `GET/HEAD`，审核 API 限 `GET/POST/OPTIONS`；主页面不得直接加载外部脚本。服务端 CSP 必须逐项原样保留；宿主只对当前精确目标的 Document 追加策略。配对与自动连接从首次 token/根导航前启用独立最小 CSP、关闭页面脚本，且不读取描述符、注册表、外部能力确认或可见页面快照。 |
| `REQ-WEB-016` | 描述符固定从当前目标浏览器会话的 `/.well-known/dsh-webui-compatibility.json` 在可见根导航前读取，只接受无重定向的 `200 application/json; charset=utf-8`。响应上限 64 KiB、组件上限 64、解析深度有限；拒绝重复键、尾随内容、冲突身份、非法 adapter key、未知主版本和授权型扩展字段。缺失或无效时不猜测、不使用缓存授权，只保留基础兼容。 |
| `REQ-WEB-017` | 适配规则注册表只以规范化只读资源嵌入受 Authenticode 签名的托管主程序集。运行时规则精确匹配契约、`uiId`、`uiVersion`、可选 `sourceRev`、adapter key、`ruleId` 和不可变 `ruleVersion`；匹配域不得重叠。Harness/LAN/route 基线只限定证据和支持范围，不作为无法可信取得的运行时身份。撤销使用永久无能力墓碑，优先于活动规则，规则身份永不复用。 |
| `REQ-WEB-018` | 页面能力快照摘要绑定 `targetId`、全部组件精确身份、实际命中规则版本和最终规范化能力。首次出现外部能力或摘要发生任何变化时，用户按目标接受或拒绝精确站点与用途；用户不能编辑规则或能力。只有摘要完全一致才可复用确认，拒绝、撤销、规则墓碑和降级均只保留基础兼容。 |
| `REQ-WEB-019` | 安全根页面可用但部分能力或 WebUI 声明的必需外部 frame 失败时，目标窗口保持页面可用，并用原生窄提示和按需侧板显示脱敏兼容状态、稳定原因码和恢复动作。只有 Harness 根导航不可用、证书/协议失败或全局安全边界违反进入完整阻断/恢复页；不得提供临时放行或添加域名入口。 |
| `REQ-WEB-020` | 每次顶层打开、用户刷新、WebView/renderer/browser/environment 恢复和重新配对后首次可见导航都重新读取描述符并计算快照。当前 Document 生命周期内快照不得扩大；变化只在下一次完整页面能力周期生效。 |

目标内容宿主对 WPF 只暴露小型接口：

```text
Open(targetContext)
Reload()
Close()
StateChanged(phase, CompatibilityStatusDto)
```

`phase` 只允许 `Initializing | Loading | Ready | Recovering | Blocked | Failed`。`CompatibilityStatusDto` 只含层级、稳定原因码、脱敏用途、版本和摘要。外部能力确认通过注入 `IExternalCapabilityConfirmationPort.ConfirmAsync(request)` 完成；生产实现属于 Desktop，内存实现用于 WebView 测试。原始描述符、查询字符串、Cookie、页面内容和 WebView2 对象不得越过接口或确认 port。

目标内容宿主内部的每次页面能力周期固定为：

```text
Bootstrap：脚本关闭，仅允许精确描述符 GET Document，无重定向
→ 读取有界原始描述符
→ 解析并匹配内置注册表
→ 计算候选页面能力快照
→ 完成必要的外部能力确认
→ 激活 enforcement 与 CSP
→ 导航可见根页面
```

刷新和所有恢复路径复用同一周期，不建立“恢复时沿用旧能力”的旁路。详细描述符、适配规则、全局能力上限和原因语义见[第三方 WebUI 通用兼容契约](webui-compatibility-contract.md)。

### 4.7 图片、剪贴板与页面内容

| ID | 要求 |
|---|---|
| `REQ-IMG-001` | V1 只保持 WebView2 external drop 和用户触发粘贴可用，由 Harness 页面读取图片。Windows 原生层不得读取、缓存、转换、哈希或记录图片字节、文件名或剪贴板内容。 |
| `REQ-IMG-002` | 图片格式、数量、大小、模型能力、归一化、持久化和错误提示完全由固定 Harness 负责，启动器不建立第二套校验或上传协议。 |
| `REQ-IMG-003` | 图片只属于当前目标窗口的当前 Harness Session；不得建立跨目标草稿、附件队列、自动重试或 WPF 持久化。 |
| `REQ-IMG-004` | 文本和图片混合粘贴、Explorer 单图/多图拖放以及错误格式都必须保持 Harness 原生结果；宿主窗口不得抢占 drop。 |
| `REQ-IMG-005` | 通用 Windows 到 Linux 文件复制是后续升级；V1 不预留隐藏开关、Linux 接收插件或通用文件系统桥接。 |

### 4.8 原生诊断

| ID | 要求 |
|---|---|
| `REQ-DIAG-001` | 目标中心提供用户主动触发的“导出诊断信息”，使用 Windows 保存对话框生成 ZIP；它不属于 WebView 下载，也不自动上传。 |
| `REQ-DIAG-002` | 诊断包只含产品、OS、Runtime、依赖基线、脱敏目标状态和应用日志。允许在明确提示后记录规范化私有 IP 与端口。 |
| `REQ-DIAG-003` | 统一排除显示名称、目标配置原文件、UDF、Cookie、token、请求与响应体、页面内容、图片、剪贴板和 crash dump。 |
| `REQ-DIAG-004` | 应用日志最多保留 7 个滚动文件，每个不超过 5 MiB；日志只记录必要状态码、分类、版本和时间。 |
| `REQ-DIAG-005` | 所有错误和状态事件在进入日志、UI 或模块边界前脱敏。不得把配对 URL 查询参数传入异常文本或遥测。 |
| `REQ-DIAG-006` | 兼容诊断只允许记录契约/Schema/注册表/规则版本、规范化能力摘要、脱敏 origin 名称、稳定原因码和生命周期阶段。禁止记录描述符原文、query 值、Cookie、请求/响应体、页面内容、第三方业务数据和未脱敏绝对路径。字段允许集和敏感字段扫描必须由自动测试锁定。 |

### 4.9 安装、升级与卸载

| ID | 要求 |
|---|---|
| `REQ-DIST-001` | 唯一正式产物为受信任签名的 Inno Setup 按用户 EXE：`{Product}-Setup-{SemVer}-win-x64.exe`。应用为 .NET 10 自包含、多文件、非裁剪发布。 |
| `REQ-DIST-002` | 安装器、自有 EXE 和卸载器必须带可信时间戳的 Authenticode 签名；发行页提供 SHA-256 和发行说明。无有效签名的包只可内部测试。 |
| `REQ-DIST-003` | 默认目录为 `%LOCALAPPDATA%\Programs\DshWindowsLauncher`。首次安装可选择不存在或空的目录，但最终卷必须是 `DRIVE_FIXED` 且当前用户可写、空间充足。 |
| `REQ-DIST-004` | 拒绝 UNC、网络映射、可移动盘、设备路径和无法确认最终位置的重解析路径；不得通过提权绕过。其他非空目录不得接管、覆盖或清理。 |
| `REQ-DIST-005` | 应用数据根固定为 `%LOCALAPPDATA%\DshWindowsLauncher`，独立于程序目录。升级、修复或改变程序安装位置不得改变目标或会话身份。 |
| `REQ-DIST-006` | 正式安装包携带 x64 Evergreen WebView2 Standalone Installer，并复用兼容的共享 Runtime。缺失、损坏或低于下限时在替换程序文件前安装或修复。 |
| `REQ-DIST-007` | 允许较新版本就地升级和同版本修复，拒绝降级。升级和修复沿用登记目录；改变程序目录必须先普通卸载并保留数据，再重新安装。 |
| `REQ-DIST-008` | 安装器不得迁移应用数据。新版应用首次运行时才对已知旧模式执行带备份原子迁移；未知模式或迁移失败时停止写入和自动连接。 |
| `REQ-DIST-009` | 安装、修复、升级或卸载发现应用运行时，通过单实例 IPC 请求正常退出并等待 WebView2 释放；失败则中止，不强杀、不自动恢复窗口。 |
| `REQ-DIST-010` | 普通卸载删除程序和入口但保留应用数据。可选“删除全部本地配置和配对会话”默认不勾选并需明确确认；不得卸载共享 WebView2 Runtime。 |
| `REQ-DIST-011` | 数据清除只作用于带所有权标记的精确应用数据根且不跟随重解析点。失败时程序卸载仍可完成，但必须报告残留路径。 |
| `REQ-DIST-012` | V1 不后台检查或安装更新。“查看更新”经用户确认后只用系统浏览器打开构建时固定的 HTTPS 发布地址。 |
| `REQ-DIST-013` | 安装只创建当前用户开始菜单快捷方式和“已安装的应用”卸载项；不默认创建桌面快捷方式。首次安装完成页默认勾选“立即启动”，修复或升级后默认不启动；需要重启时只提示，不自动重启或启动。 |
| `REQ-DIST-014` | 运行时发现 WebView2 缺失或损坏时显示原生修复页；联网可运行随程序保留的 Microsoft Bootstrapper，离线要求重新运行完整正式安装包。 |
| `REQ-DIST-015` | 安装过程日志成功后删除，失败后保留并显示路径；不得记录目标、凭据、页面或应用数据内容。项目只维护最新签名补丁版，旧版本可留存但标记为不受支持。 |
| `REQ-DIST-016` | 内置适配规则注册表、契约和 Schema 只随完整签名启动器分发。不得提供运行时联网下载、后台更新、独立规则包、安装目录可编辑 JSON、命令行/环境变量覆盖、页面提供规则、手工导入或隐藏入口。 |
| `REQ-DIST-017` | 构建必须校验注册表 Schema、规范化、唯一性、版本单调、匹配域不重叠、墓碑和全局上限，计算 SHA-256，并把契约、Schema、注册表、活动规则/墓碑、参考适配器、支持记录和影响清单身份绑定到签名资源、包清单、SBOM 和正式发布证据。 |

## 5. 代码结构与模块边界

规划目录：

```text
src/
  DshLauncher.Core/
  DshLauncher.Compatibility/
  DshLauncher.WebView/
  DshLauncher.Desktop/
  DshLauncher.Platform.Windows/
tests/
  DshLauncher.Core.Tests/
  DshLauncher.Compatibility.Tests/
  DshLauncher.WebView.Tests/
  DshLauncher.Platform.Windows.Tests/
  DshLauncher.Acceptance.Tests/
installer/
eng/
  verify.ps1
  package.ps1
  release-smoke.ps1
  fixtures/linux/
docs/
  v1-implementation-plan.md
  v1-release-checklist.md
  webui-compatibility-contract.md
  webui-provider-onboarding.md
  reference-adapters/dsh-web.md
  adr/0005-use-contract-and-embedded-adapter-registry.md
```

### `DshLauncher.Core`

通过小型 `TargetManager` 接口封装目标目录、默认目标、探测结果、配对事务、自动连接决策和忘记事务。它不引用 WPF、WebView2 或 Windows 适配器。领域对象必须使端点唯一、默认引用、逐目标互斥和事务状态成为可测试不变量。

### `DshLauncher.Compatibility`

这是技术无关的兼容解析深模块。它只依赖 BCL，在一个小 interface 后隐藏严格描述符解析、Schema、内置注册表完整性、身份与版本匹配、URI/JCS 字节级规范化、冲突与墓碑、全局上限求交、能力合并、快照摘要和脱敏原因生成。外部调用形状固定为“给定目标身份与有界原始描述符结果，返回不可变 `PageCapabilityResolution`”；Harness/LAN/route 基线不进入运行时 matcher，只进入规则适用元数据、证据和支持记录。解析器、注册表 matcher、URI matcher 和规范化器都是内部实现，不分别暴露公共 interface。

内置注册表只有一个受信生产来源，不建立运行时可替换的注册表 port。测试直接通过该深模块 interface 使用确定性注册表 fixture；不得为了单元测试把内部步骤扩张为生产接口。

### `DshLauncher.WebView`

通过 `TargetContentHost` 隐藏 WebView2 初始化、描述符 bootstrap、页面能力周期、导航、权限、下载、UDF、进程恢复和脱敏状态。bootstrap 固定为“脚本关闭和仅描述符 GET → 解析/确认 → 原子激活 snapshot/CSP → 根导航”；根脚本不存在提前执行窗口。WPF 不直接订阅散落的 WebView2 安全事件。`TargetContentSecurityPolicy` 只消费 `TargetContentBinding + PageCapabilitySnapshot`；WebView2 adapter 只把请求、重定向、frame、WebSocket 和 CDP 响应映射为规范输入并应用决定，不持有第三方 UI 常量。

描述符读取是真实 adapter seam：生产 adapter 使用目标同一 WebView2/UDF/Cookie 会话并返回状态码、Content-Type、重定向事实和有界原始字节；内存 adapter 提供确定性测试响应。它不能返回“已信任描述符”。外部确认是同一 host 表示层边界上的小型回调 port，不暴露解析或 WebView2。临时配对和自动连接使用独立最小 CSP，不经过描述符 seam。

### `DshLauncher.Desktop`

包含 WPF 表示层和 `WindowCoordinator`。它只负责单实例入口、目标中心、目标窗口生命周期、外部能力确认、兼容窄提示/侧板和恢复页，不实现描述符解析、规则匹配、网络探测、持久化或 Harness 页面业务。

### `DshLauncher.Platform.Windows`

提供文件系统、原子替换、Windows 网络类别、剪贴板、系统浏览器、进程、Runtime 探测、保存对话框和当前用户 IPC 的生产适配器。新增本地兼容状态 adapter，使用应用数据根既有所有权、原子替换、备份和完整性规则保存注册表回退水位、逐目标外部能力确认和逐目标脱敏诊断缓存；测试使用内存 adapter。逐目标确认和诊断位于既有目标根，忘记操作仍只由 `TargetManager → ISessionPort.DeleteAsync` 整根删除事务提交，不增加第二条 Desktop/Compatibility 删除链。

### `installer/` 与 `eng/`

`installer/` 保存发布常量、Inno Setup 和签名构建逻辑，不被运行时代码引用。`eng/` 是开发者和 CI 的唯一入口；CI 如存在，只调用这些脚本，不复制逻辑。

项目依赖方向固定为：`Core` 与 `Compatibility` 都不引用其他生产项目；`WebView` 引用二者；`Platform.Windows` 引用 `Core` 与 `Compatibility`；`Desktop` 作为唯一组合根引用四者。架构测试必须阻止 `Compatibility` 引用 WPF、WebView2、Windows adapter 或可编辑运行时规则来源。

## 6. 数据与并发实现准则

- 应用数据根必须有可验证的所有权标记。所有 UDF 路径只由 `targetId` 推导，不能由显示名称、IP 或用户输入直接拼接。
- 原子快照使用同目录临时文件、刷新落盘、原子替换和上一版备份。任何恢复逻辑先验证完整不变量，再决定使用主快照或备份。
- 同一目标的配对、连接、忘记和内容宿主创建使用逐目标异步锁；目录快照提交使用独立短锁。网络和 WebView 操作不得持有目录写锁。
- 取消、退出和崩溃恢复必须清理未提交配对数据；已提交会话只能按明确失效矩阵或忘记事务删除。
- 自动连接和窗口创建应使用幂等命令。重复 IPC、重复点击和迟到完成事件不得产生第二窗口、第二配对提交或复活目标。
- 全局注册表回退水位使用独立版本化快照和备份；只有内置注册表完成 Schema、摘要、唯一性和确定性校验后才能提高。较低版本、未知新 Schema、损坏或校验失败只关闭扩展兼容，不影响目标目录和基础兼容。
- 外部能力确认与兼容诊断缓存位于对应 `targets/<targetId>/` 所有权目录，分别使用独立 Schema、主快照和备份。确认只保存快照摘要、规范化外部用途和决定，不保存描述符原文；诊断缓存不参与授权。
- 忘记事务沿用 `TargetManager → ISessionPort.DeleteAsync` 的唯一提交链，整根删除并验证 UDF、会话元数据、确认和诊断缓存；崩溃恢复也只由该事务处理。全局水位位于目标根外，不随忘记目标删除。

## 7. 实施里程碑

| 里程碑 | 交付内容 | 完成门槛 |
|---|---|---|
| M0 基线与骨架 | 建立解决方案、项目依赖规则、`global.json`、lock 文件、发布常量、三个脚本骨架和 Linux 夹具；显式刷新一次依赖基线。 | `eng/verify.ps1` 可在干净环境完成锁定还原和空骨架 Release 构建；发布常量没有未解释的漂移。 |
| M1 目标核心 | 实现目标模型、规范化、默认目标、版本化快照、备份恢复、迁移和忘记事务。 | 所有有限状态、损坏输入、原子中断和跨目标隔离数据集自动通过。 |
| M2 网络与配对 | 实现 Windows 网络类别、原生探测、能力指纹、信任确认、配对 URL 解析、事务提交、剪贴板清理和 Linux 参考夹具。 | 正常、`401`、`403`、错误 `200`、非 HTTP、超时、旧无认证服务、并行目标及崩溃清理自动和集成测试通过。 |
| M3 桌面外壳 | 实现单实例 IPC、目标中心、默认启动、独立窗口、逐目标状态和原生故障页。 | 重复启动、窗口去重、默认目标、失败隔离、键盘、高对比度和 DPI 检查通过。 |
| M4 Web 内容宿主 | 实现逐目标 UDF、导航与权限策略、下载阻断、外链确认、恢复状态机和 Harness 原生图片路径。 | WebView2 适配器矩阵通过；在真实 Harness 上完成 RPC、流式 WebSocket、Cookie 持久、图片、Linux 目录和进程恢复冒烟。 |
| M5 安装与维护 | 实现自包含发布、离线 WebView2 前置、签名、固定卷目录校验、修复、升级、卸载和应用数据迁移。 | `eng/package.ps1` 生成身份固定的候选包；安装生命周期数据集和干净虚拟机路径通过。 |
| M6 正式候选 | 完成诊断导出、依赖与许可证材料、最终安装包实机冒烟、四窗口 30 分钟和发布报告。 | `eng/verify.ps1` 与 `eng/release-smoke.ps1` 对同一安装包哈希通过，形成 `release-evidence.md`，等待人工发布确认。 |

M1 后可以并行准备 Linux 夹具和安装器骨架。M2 与 M3 可在接口固定后并行；M4 依赖二者。不得以“代码完成”替代里程碑完成门槛，也不在人员配置未知时编造日历工期。

### 7.1 第三方 WebUI 通用兼容迁移批次

现有产品已经完成 M0 至 M4 的专用 dsh-web 路径。通用兼容在同一分支按以下批次替换，不新增并行授权路径：

| 批次 | 实施内容 | 完成门槛 | 失败处理 |
|---|---|---|---|
| P0 正式契约与 Schema | 固化兼容契约、ADR、提供方接入与维护规范、描述符/契约能力/注册表/接入包/审核决定/支持记录/影响清单/发布证据 Schema、规范路径、模板、原因码和正反示例。 | 不读取临时规划材料即可实现身份、能力、错误、状态机、版本和证据规则；全部 Schema 可离线验证，并按统一 JCS 字节算法产生相同摘要。 | 只回退文档与数据定义，不改变产品行为。 |
| P1 纯兼容内核 | 新建 `DshLauncher.Compatibility`、注册表规范化与构建校验、恶意数据集、解析/匹配/快照/墓碑/摘要。此时不连接生产授权路径。 | `DshLauncher.Compatibility.Tests` 通过全部成功、部分匹配、降级、冲突、冒用、回退和损坏数据。 | 可撤销未接线项目；现有专用运行行为不变。 |
| P2 本地状态与原生体验 | 实现回退水位、逐目标确认、诊断缓存、原因码、窄提示、按需侧板和安全根页面状态。 | 原子保存、备份恢复、未知 Schema、精确删除、脱敏和可访问性测试通过。 | 丢弃开发候选；不得发布半成品通用兼容声明。 |
| P3 Runtime 与受控 fixture | 先拆开可见内容 CSP 与临时最小 CSP，把 Pair 的最小 CSP 前移到首次 token 导航前，再实现脚本关闭的同会话描述符 bootstrap 和统一快照输入；扩建双 HTTPS、异端口、OOPIF、WebSocket、Worker、重定向和到站计数 fixture。 | 配对/自动连接永不调用描述符或规则且首次导航前已启用最小 CSP；可见 adapter 在不执行根页面脚本前取得严格响应；每项条件能力的双 Runtime 和 Edge 夹具可复跑。通过的能力必须写入规范能力清单并提升契约版本；未通过能力不进入规则或产品。 | P5 前仍可丢弃候选，不建立运行时开关。 |
| P4 dsh-web 参考适配器 | Linux 提供首个有效描述符精确版本；建立 Market 与 Turnstile 精确规则、版本身份、提供方套件、CSP A/B 和正负 route 数据。 | 精确版本、`sourceRev`、规则、描述符、route、安全根页面和 CSP 债务均有确定结果。 | 证据不全时只承诺基础兼容；不合成描述符或恢复旧白名单。 |
| P5 同一候选原子切换 | 初次打开、刷新、WebView/renderer/browser/environment 恢复同时改用新 resolution；资源、方法、逐跳重定向、frame、WebSocket 和 CSP 同时消费快照；删除专用策略、常量和旧测试。 | 新旧授权路径不能并存；静态专用入口为零；所有可见和临时调用链通过。 | 整体回退候选或完整提交，继续使用上一签名正式版本；产品内无 fallback。 |
| P6 正式证据与支持记录 | 更新发布资产，运行全部 VFY、全部 RS、最低 Runtime、Evergreen、Edge、参考正负矩阵、性能、签名、时间戳、SBOM、哈希和支持记录生成。 | `FAIL`、`SKIP`、`MISSING`、身份不一致和专用入口残留均为零；证据绑定同一签名候选。 | 未发布候选整体丢弃；正式发布后只使用签名 patch 和墓碑修复。 |
| P7 提供方流程开放 | 发布并启用 P0 的接入规范、模板、正反示例、原因码表、审核决定和生成式支持记录，填入公开入口、私密安全入口、负责人和维护承诺。 | 所有角色、入口、签名发布身份无占位；Schema、双轴状态机、交叉身份、生成一致性和敏感字段自动验证通过；dsh-web 从接入包到机器支持记录及人类生成物完整走通。 | 条件不全时仅发布基础兼容自助材料，不宣称正式扩展接入或长期维护服务开放。 |

P2 与 P3 可在 P1 interface 稳定后部分并行；P4 依赖 P1 和 P3；P5 等待 P2 至 P4 的阻断项清空。P0 至 P4 的提交可以独立评审，但任何可运行候选在 P5 前不得包含“新快照已接入、部分事件仍走旧允许清单”的混合状态。

P5 必须在同一迁移版本中删除 `src/DshLauncher.WebView/DshWebUiCompatibilityPolicy.cs`，并移除 `TargetContentSecurityPolicy.cs`、`WebView2ResponseCspBoundary.cs`、可见 `WebView2TargetContentRuntime.cs` 和临时 `WebView2TargetRuntimePort.cs` 中的专用常量或引用。`dsh-market.com`、`challenges.cloudflare.com`、`/api/skin-center/we/` 和 `/sidebar/html/` 只允许存在于内置参考规则、参考 fixture 和版本化参考说明中。禁止旧新规则并集、feature flag、异常 fallback、合成描述符和 HTML 指纹。

### 7.2 文件级实施矩阵

| 路径 | 必须完成的变化 | 验证接口 |
|---|---|---|
| `DshWindowsLauncher.slnx`、`Directory.Build.*` | 加入 Compatibility 生产/测试项目，保持统一 analyzers、锁定还原、`win-x64` 和警告即错误。 | `VFY-01`、架构测试 |
| `src/DshLauncher.Compatibility/` | 实现单一 resolution interface、不可变模型、严格描述符、注册表、URI 规范化、规则合并、墓碑、上限、快照和原因码。 | `DshLauncher.Compatibility.Tests` |
| `src/DshLauncher.WebView/TargetContentHost.cs` | 把打开、刷新和恢复收敛为完整页面能力周期；以小型确认 port 和脱敏兼容状态 DTO 对接 WPF。 | host 生命周期、确认和恢复测试、`RS-12` |
| `src/DshLauncher.WebView/TargetContentSecurityPolicy.cs` | 删除专用调用，只消费 binding 与 snapshot；统一资源、方法、重定向、frame 和 WebSocket 判定。 | snapshot enforcement 数据集、`RS-07/10` |
| `src/DshLauncher.WebView/WebView2ResponseCspBoundary.cs` | 从 snapshot 生成可见 Document CSP；保留服务端 CSP；为临时流提供独立最小 CSP。 | CSP 确定性数据、CDP fail-closed、`RS-09/10` |
| `src/DshLauncher.WebView/WebView2TargetContentRuntime.cs` | 实现脚本关闭、仅精确描述符 GET、无重定向的 bootstrap；解析/确认后原子激活 snapshot/CSP，再允许根导航；全部事件映射到统一 enforcement。 | adapter 契约、真实 Runtime fixture |
| `src/DshLauncher.WebView/WebView2TargetRuntimePort.cs` | 配对和自动连接保持脚本关闭、绑定 origin；Pair 与 PrepareOpen 都在首次导航前启用独立最小 CSP，并证明从不调用 Compatibility。 | 临时流生效顺序和负向测试、`RS-05` |
| `src/DshLauncher.Desktop/App.xaml.cs`、`DshLauncher.Desktop.csproj` | 作为唯一全局组合根，在任何窗口前校验签名托管主程序集、内置注册表 Schema/摘要/唯一性和回退水位；嵌入规范注册表资源。 | 启动 fail-closed、签名资源和组合根测试 |
| `src/DshLauncher.Desktop/WindowCoordinator.cs` 与目标窗口 | 组合 resolver、reader、state adapter、WPF confirmation port 和兼容提示/侧板；不解析规则。 | UI 状态、可访问性、确认复用/撤销测试 |
| `src/DshLauncher.Platform.Windows/ApplicationDataStore.cs`、`JsonSessionPort.cs` 及新 JSON adapter | 增加水位及目标根内确认/诊断的独立 Schema、原子替换和备份；继续由 `TargetManager` 的整根删除事务完成忘记与崩溃恢复。 | `VFY-03`、TargetManager 忘记/恢复测试、`RS-14` |
| `src/DshLauncher.WebView/DshWebUiCompatibilityPolicy.cs` | 在 P5 删除。 | 静态扫描不得存在文件或引用 |
| `tests/DshLauncher.WebView.Tests/` | 用基础契约、快照 enforcement、最小 CSP 和 host 页面能力周期测试替换 dsh-web 允许清单测试；明确替换旧同源 frame/`about:srcdoc` 断言。 | replace-don't-layer；旧测试名/事实为零 |
| `schemas/webui-*.schema.json`、`schemas/verification-impact-map.schema.json` | 建立描述符、契约能力、注册表、接入包、审核决定、支持记录、影响清单和发布证据唯一 Schema。 | `VFY-08`、Schema 正反数据 |
| `compatibility/contracts/webui-contract-capabilities.json`、`compatibility/registry/webui-adapter-registry.json`、`compatibility/providers/examples/` | 建立唯一规范数据、正反示例和统一 JCS 字节摘要；人类支持清单只从机器记录生成。 | 构建/运行时/证据摘要一致 |
| `eng/generate-webui-governance.ps1` | 校验 Schema、状态机、身份、敏感字段和摘要，生成审核/支持人类摘要。 | 可重复生成、手工平行状态扫描 |
| `eng/verification-impact-map.json`、`eng/verify.ps1` | 路径和输入身份映射到 tag/RS；自动取并集；更新预期测试数据与专用入口扫描。 | 未映射输入强制全量；人工只能增加 |
| `eng/fixtures/linux/` | 扩建目标/异端口/双 HTTPS、描述符、重定向、OOPIF、WebSocket、Worker、缓存和到站计数。 | 双 Runtime 与 Edge 可复跑证据 |
| `eng/package.ps1`、`eng/release-smoke.ps1` | Authenticode 签名并验证 apphost EXE、托管主程序集、卸载器和安装器；绑定注册表/契约/支持身份，生成结构化证据，执行影响并集、双 Runtime、Edge 和全部触发 RS。 | `VFY-08`、签名应用集、`RS-01` 至 `RS-15` |
| 三个生产 `.csproj`、相关测试 `.csproj` | 固定 Compatibility 引用方向、托管资源嵌入和测试依赖；禁止 Compatibility 反向引用 UI/平台。 | 架构与构建图测试 |
| `README.md`、现有兼容说明、实施/发布文档、ADR | 在 P5 同步删除专用产品表述；README 只概述通用能力，dsh-web 只作为参考链接。 | 文档不得把临时规划材料作为规范来源；产品声明与代码一致 |

### 7.3 完成状态

1. **开发方案完成**：本文件、发布检查表、通用契约、提供方接入与维护规范和参考适配器说明已经覆盖需求、interface、adapter seam、文件矩阵、阶段、回滚和门禁。此状态不表示代码已改。
2. **通用兼容产品完成**：P0 至 P6 全部落地，专用入口静态为零，运行行为统一，精确参考适配器和全部正式证据通过。
3. **正式接入流程开放**：P7 的负责人、公开入口、私密安全入口、发布身份、模板、示例、机器支持记录和生成物无占位值，并完整跑通一个 dsh-web 接入生命周期。

三种状态不得相互替代。当前仓库在本方案更新后只达到第一种；`releaseStatus=development`、空 Publisher/证书/时间戳/官方地址或未完成实机证据时，不得声明后两种。

## 8. 构建、验证与打包入口

### `eng/verify.ps1`

无必需交互。一次完成：

- locked restore；
- `dotnet format --verify-no-changes`；
- nullable、警告视为错误和锁定 SDK 对应 analyzers；
- Release `win-x64` 构建；
- Core、WebView、Windows 平台及参数化集成测试；
- Compatibility 深模块、描述符、注册表、能力快照、回退水位和参考适配器数据集；
- 架构测试，禁止 Core 引用 WPF、WebView2 或 Windows 适配器；
- 架构测试，禁止 Compatibility 引用 WPF、WebView2、Windows adapter 或运行时可编辑规则源；
- 依赖漂移、来源、许可证、SBOM、已知 Critical/High 可利用漏洞和秘密扫描。

任一必须项失败返回非零。覆盖率只用于发现缺口，不设置全局百分比目标。安全、凭据、数据、删除及跨目标隔离分支必须有明确自动测试标识。

### `eng/package.ps1`

接收明确 SemVer，只消费已通过验证的 Release 输出。它生成自包含应用、离线 Runtime 前置、安装器、SBOM 和发行说明输入；规范化内置注册表必须在编译前完成校验并嵌入托管主程序集，其版本与 SHA-256 进入 `package-manifest.json`、SBOM、结构化证据和发行说明输入。正式模式按固定顺序 Authenticode 签名并验证 apphost EXE、托管主程序集和卸载器，再生成、签名并验证安装器；三者共同构成签名应用集。启动前再次校验托管程序集签名和注册表摘要。对最终不可变安装包计算 SHA-256 后，任何字节变化都产生新候选并使既有实机报告失效。

### `eng/release-smoke.ps1`

接收精确安装包路径和证据输出目录，负责环境预检、影响清单并集、夹具控制、可自动采集部分、脱敏日志、结构化证据及 `release-evidence.md` 骨架。需人工操作的 Explorer 拖放、剪贴板、外链/外部能力确认、Edge 对照和可用性检查由同一脚本逐项提示并记录结果。详细步骤见[V1 发布检查表](v1-release-checklist.md)。

## 9. Linux 部署前置

启动器不执行以下操作；Linux 管理者在每台 Harness 主机上完成。

### 9.1 基线安装与启动

固定 Harness 与插件版本后，为 web profile 安装插件：

```sh
dsh plugin --profile web add dsh-web-lan-access
```

默认端口启动：

```sh
dsh --profile web --no-open --port 3080
```

若 `3080` 已被远程桌面或其他服务占用，先用 `ss -ltnp` 或等价工具确认占用，再由 Linux 管理者选择一个明确固定的空闲端口，例如：

```sh
dsh --profile web --no-open --port 3180
```

不得传 `--host 0.0.0.0`。LAN 插件负责最终绑定；Harness CLI 会主动拒绝该参数。不得使用 `--port 0` 作为正式部署，因为动态端口无法形成稳定目标身份。

### 9.2 防火墙与网络

- Linux 防火墙优先只放行指定 Windows 客户端 IPv4；必要时才放行最小可信 RFC1918 网段。
- 启动前确保目标 LAN 网卡已存在。记录内核、Node、Harness、插件和防火墙版本。
- 用 `ss -ltnp` 确认 Harness 监听预期端口，并从允许的 Windows 客户端验证可达；不要把插件的 Host/Origin fence 当作来源 IP 防火墙。
- 启动输出中的 `dsh web:` LAN URL 含高权限 launch token。只交给对应 Windows 用户，不写入普通工单、聊天记录或长期日志。
- 参考适配器部署时，Linux 侧先升级到提供有效 `/.well-known/dsh-webui-compatibility.json` 的精确 dsh-web 版本，再安装 Windows 通用兼容候选。Windows 先升级时只获得基础兼容；Linux 更新后重新打开目标重新评估，不清 UDF 或重新配对。

### 9.3 参考测试拓扑

- Linux A：Ubuntu Server 24.04 LTS，运行两个认证型 Harness 实例，例如 `A:3080` 和 `A:3180`。
- Linux B：使用独立网络栈和独立 RFC1918 LAN 地址的 Ubuntu Server 24.04 LTS 虚拟机，在替代端口运行两个认证型 Harness，例如 `B:3180` 和 `B:3280`；默认 `B:3080` 留给端口占用夹具。`A:3080`、`A:3180`、`B:3180`、`B:3280` 共同用于四窗口承诺，并同时覆盖不同主机使用相同端口。
- 临时夹具：在 `B:3080` 轮换非 HTTP 服务和错误 HTTP `200` 服务，并提供 `403` fence、旧无认证 Harness、离线和超时。兼容 fixture 另提供目标 origin、同机异端口 origin、两个使用受测试机信任证书的 HTTPS origin，以及描述符、逐跳重定向、frame/OOPIF、WebSocket、Worker、缓存、304、压缩、CDP Fetch 失败和拒绝请求未到站场景。夹具准备与清理必须幂等，不保存 token 或业务内容，不允许证书绕过。
- Windows：一台 Windows 11 实体参考机和一份干净虚拟机。资源参考机为 4 逻辑处理器、8 GiB RAM、SSD；这不是最低硬件声明。

## 10. 独立交付说明

- 本文件是 V1 开发实施的规范入口。复制到新的代码仓库后，文件内容仍然完整有效。
- `REQ-*` 是稳定需求标识。实现、自动测试、变更记录和缺陷应引用这些标识，不应引用历史票据编号。
- 配套的 `v1-release-checklist.md` 独立定义三个发布脚本、八个自动验证组、十五项实机作业、重跑触发和证据模板。
- `webui-compatibility-contract.md` 定义运行时语义；`webui-provider-onboarding.md` 定义接入和维护治理；`reference-adapters/dsh-web.md` 只定义参考身份、规则拆分和特有验收，不产生专用授权路径。
- `CONTEXT.md` 和 ADR 是可选追溯材料；研究报告、原型、临时规划票据、截图和对话都不是理解或执行本方案的前置依赖。
- 正式交接必须携带本文件、`v1-release-checklist.md` 以及二者引用的版本化契约、Schema、注册表、影响清单、fixture 和生成规则。人类从两个规范入口开始执行，但不得省略其机器输入。
