---
status: accepted
---

# 将 Harness Web UI 限制在单目标内容宿主中

每个目标窗口使用一个绑定不可变目标标识、精确 origin 和逐目标 UDF 的目标内容宿主。Harness Web UI 保留全部业务逻辑；宿主集中执行导航、弹窗、权限、下载、证书、快捷键和 WebView2 故障策略，只向 WPF 暴露打开、重新加载、关闭、脱敏状态和小型外部影响确认 port。这样既能承载完整 Harness 页面，又不会把远端页面变成通用浏览器或 Windows 原生能力入口。

## Considered Options

- 直接采用 WebView2 默认行为：实现最少，但会留下跨站导航、未受管弹窗、下载、权限持久化和故障白屏等不可接受行为。
- 为文件和系统操作提供通用原生桥接：功能扩展快，但会把高权限 Windows 能力暴露给远端页面，并使启动器耦合 Harness 私有页面实现和版本变化。
- 使用严格单目标内容宿主：把全部浏览器策略隐藏在一个深模块中，外链经确认交给系统浏览器，只保留 Harness 原生图片附件，发布页面不获得原生对象。

## Consequences

实现必须完整接管相关 WebView2 事件，并为真实 Runtime 和确定性测试分别提供适配器。基础同源能力与经过审核的扩展依赖统一遵循[能力契约与内置适配规则注册表](0005-use-contract-and-embedded-adapter-registry.md)；目标内容宿主在可见导航前固定页面能力快照，资源、方法、逐跳重定向、frame、条件 WebSocket/Worker 和 CSP 不得保留专用授权旁路。服务端 CSP 必须保留，宿主 CSP 只能追加更严格的边界。目标内容宿主保持 Harness 图片拖放和粘贴可用，但不读取图片或建立上传接口；Harness ZIP 导出及其他目标窗口下载被阻断。新增页面权限、任意外站 iframe、通用文件传输、外部协议、原生桥接或 Linux 到 Windows 文件传递均需显式重新决策。Harness、WebUI、内置规则与 WebView2 升级必须按影响清单重跑宿主验收矩阵。
