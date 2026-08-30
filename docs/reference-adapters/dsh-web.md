# dsh-web 参考适配器实施说明

文档编号：`DSHWL-REF-DSH-WEB`<br>
文档版本：`1.0`<br>
状态：候选，等待描述符就绪版本与正式证据<br>
更新日期：2026-08-31<br>
上位契约：`../webui-compatibility-contract.md`

## 1. 角色与声明边界

dsh-web 是第三方 WebUI 通用兼容模型的首个参考适配器。它证明描述符、内置适配规则、页面能力快照和统一宿主强制可以覆盖一个真实复杂 WebUI。它不获得专用身份信任、隐藏允许清单、额外全局能力或旧路径回退。

当前已观察的 `@linxin666/dsh-web-all 0.3.5`、`zhu1090093659/dsh-web 0.3.7` 和 `sourceRev=e846fb34` 只作为旧行为与设计输入。它们没有完成本契约的有效同源描述符、提供方套件、双 Runtime、Edge 和签名候选绑定，因此不得写入正式参考支持记录。

正式参考身份必须在实施期间填写：

| 字段 | 要求 |
|---|---|
| 聚合 UI 与组件 | 精确 `uiId` 集合 |
| UI 版本 | 精确规范化 SemVer |
| 源身份 | 不可变 `sourceRev` 和源码 commit |
| 描述符 | 固定 well-known 地址的有效响应及 SHA-256 |
| Harness/LAN | 精确 Harness commit、LAN 插件、route 基线 |
| 规则 | `ruleId`、不可变 `ruleVersion`、registryVersion |
| 套件 | 提供方一致性套件版本、命令和摘要 |
| Runtime | 最低 WebView2 Runtime、验证时 Evergreen、SDK |

任一字段未知时只允许基础兼容，不得使用 `latest`、邻近版本、`^0.3.5` 或 HTML 指纹补齐。

## 2. 能力拆分

以下行为由通用基础兼容承担，不进入 dsh-web 专用规则：

- Harness 根页面、同源皮肤脚本、样式、字体、图片、API、SSE 和媒体；
- 任意仍绑定当前精确目标 origin 的同源 Wallpaper Engine/侧栏 frame；
- `data:` 图片；
- 当前目标页面创建并按资源类型绑定该页面能力周期的 `blob:` 资源；
- Linux 服务端选择的活动皮肤和组件入口。

持续 WebSocket 只有在双 Runtime、Edge 和后续契约版本明确纳入该条件能力后才进入基础范围；门禁前不得由旧专用策略维持。

扩展规则按实际外部能力拆分，不以聚合包名授权：

1. **创意工坊规则**：精确 `https://dsh-market.com` origin、固定 API/清单路径、资源类型、有限方法和图片，不含 challenge 执行能力。
2. **Turnstile 规则**：dsh-market 的精确 challenge frame 切片，以及精确 `https://challenges.cloudflare.com` origin 的固定 challenge/frame/脚本/静态资源路径、有限方法和父子 frame 关系。外部脚本只能在该审核跨 origin frame 内加载，Harness 主页面不得直接加载。

聚合 dsh-web 包只用于诊断展示。规则不得开放 WebMCP bridge、`/mcp`、`/.webmcp/rpc/*`、任意同站 Cloudflare 注入脚本、任意外部脚本、任意 CDN 或 better-sidebar 任意外站 frame。

每条规则必须在同源页面冒用 dsh-web 身份和 adapter key 时仍安全。路径、方法、资源类型、重定向和 frame 范围必须窄化到实际必需能力。

## 3. 描述符与部署顺序

通用兼容版本不为旧 dsh-web 合成描述符，也不从包资源、DOM、HTML、标题或路由猜测版本。无有效描述符的旧版只获得基础兼容。

推荐部署顺序：

1. Linux 管理者升级 dsh-web 到提供有效描述符的精确版本；
2. 验证 Harness/LAN、route、安全根页面和描述符响应；
3. 安装包含对应内置规则的 Windows 通用兼容版本；
4. 重新打开目标，计算快照并完成必要外部能力确认。

Windows 先升级时，安全根页面继续以基础兼容运行并显示版本/规则未命中。Linux 更新后重新打开目标即可重新评估，不清 UDF、目标配置或会话，不要求重新配对。启动器不安装、升级或回退 Linux 插件。

## 4. CSP 迁移

`dsh-market.com` 和 Turnstile origin 必须从全局静态 CSP 移入依据页面能力快照生成的精确策略。服务端 CSP 原样保留，宿主只追加独立且更严格的策略。目标 WebSocket source 按绑定 authority 精确生成，禁止裸 `https:`、裸 `ws:` 和 `*`。

以下项目必须逐项执行有/无 A/B：

- `'unsafe-inline'`；
- `blob:` script；
- `worker-src` 和具体 Worker 类型；
- Turnstile challenge 所需的 frame/script/connect 范围。

只有证明属于全部契约兼容页面不可避免的宿主能力时，才可把某项记录为不可由适配规则扩大的全局兼容债务。否则它属于精确参考规则或保持拒绝。证据冲突、缺失或无法归因时，不得宣布参考适配器完成。

已观察到 challenge 响应可能同时声明 nonce 并保留无 nonce 内联脚本。创意工坊清单、图片、浏览和本机安装入口分别验收；若点赞或安装计数在 Edge 与 WebView2 中因同一上游 CSP 失败，记录为上游限制，不扩大权限制造成功。

## 5. 正向验收

在精确描述符版本和同一 endpoint 上至少验证：

- Harness 根页面、Session、流式回复、持续 WebSocket 和 Linux 工作目录浏览；
- Linux 激活的皮肤、插件入口和核心布局在 Windows 重新打开后出现；
- Skin Center、Web/Scene Wallpaper Engine、sidebar 等同源组件；
- 创意工坊清单、图片、浏览和本机安装入口；
- 规则命中后 Turnstile 的必需文档、脚本和静态资源；
- 页面内复制、图片拖放/粘贴和目标恢复；
- 四目标代表性负载下的功能兼容；性能承诺按独立门禁判定。

## 6. 负向验收

至少覆盖：

- 描述符缺失、重定向、错误 Content-Type、超限、重复键、冲突身份、未知 Schema、冒用和版本错误；
- 规则未命中、重叠/冲突、墓碑、注册表损坏和回退水位；
- Market/Turnstile 越界路径、方法、资源类型、双跳重定向和外部脚本；
- 未列出的外站 frame、任意 CDN、WebMCP/RPC、页面权限、下载、证书绕过、DevTools 和原生桥接；
- 配对和自动连接不读取描述符或规则；
- 被拒请求未到站，单个可选资源失败后安全根页面仍可使用；
- 刷新、renderer/browser/environment 恢复重新取描述符，当前 Document 内能力不扩大；
- 多目标 UDF、Cookie、缓存、确认和诊断记录不串目标。

## 7. 完成与故障归因

参考适配器完成必须取得：

- 精确描述符版本、`sourceRev`、提供方套件和规则身份；
- 完整正向/负向 fixture 与到站计数；
- 固定最低 Runtime 和当前 Evergreen；
- 同一 endpoint Edge 对照；
- 全部 `VFY-01` 至 `VFY-08`、全部 `RS-01` 至 `RS-15`；
- CSP A/B 确定结论；
- 绑定同一签名候选、注册表摘要、SBOM、安装包哈希、审核决定和支持记录的正式证据。

故障按以下顺序归因：

1. Edge 成功而 WebView2 因能力快照、宿主 CSP 或请求门禁失败，属于启动器缺陷；
2. Edge 与 WebView2 因相同服务响应、CSP、CORS、Cookie、frame policy、限流或外部服务失败，属于上游；
3. 描述符、组件身份、route 或依赖行为违反已提交契约，属于提供方或 route owner。

描述符、规则、CSP A/B、真实 Runtime、Edge、route 或签名证据缺失时，只能声明基础兼容或迁移未完成。不得保留旧专用允许清单维持表面通过。

## 8. 明确不承诺

启动器不修复或代理 Harness 与插件业务 RPC、流式/安装/数据语义，Market schema 漂移，Turnstile nonce/CSP 组合错误，CORS、SameSite、Cookie、`X-Frame-Options`、`frame-ancestors`、证书和重定向错误；不实现 `dsh-remote-web-ui` 第二套设备配对、未认证 route、PWA、Service Worker、麦克风、下载、原生桥接或浏览器本地状态同步。
