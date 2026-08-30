---
status: accepted
---

# 使用 WPF 与 WebView2 构建 Windows 外壳

Windows 启动器采用 C#、.NET 10 LTS、WPF 与 Evergreen WebView2。该产品的原生界面只是目标中心、目标窗口和故障外壳，主要内容由既有 Harness Web UI 提供；WPF 能以较少部署层满足单进程多窗口、标准用户运行和 Windows 10 尽力兼容，同时提供完整的 WebView2 宿主控制。

## Considered Options

- WinUI 3 + WebView2：原生界面能力更新，但会引入 Windows App SDK 部署与维护层，而本产品很少受益于这些界面能力。
- Edge 应用模式或 PWA：包体较小，但不能可靠掌控逐目标用户数据目录、导航和权限策略、进程恢复及窗口生命周期。
- WPF + WebView2：桌面技术较成熟，能直接承载所需的 WebView2 生命周期，并更容易保留 Windows 10 兼容运行路径。

## Consequences

WPF 只允许存在于表示层和 WebView2 宿主适配层。目标目录、连接、配对、会话和窗口请求模型不得依赖 WPF 类型；若未来迁移到 WinUI 3 或逐目标子进程，只替换桌面宿主，不改变这些模型及逐目标数据边界。
