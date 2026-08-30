# Linux 网络夹具

`fixture-control.sh` 只在显式 RFC1918 地址和端口上启动无凭据测试服务。所有模式均不得用于生产。

`legacy-root-0.1.1-rc.2.html` 来自官方 npm 包 `@deepseek-ai/dsh@0.1.1-rc.2` 在全新 `DSH_HOME`、默认 Web profile 下生成的稳定根响应。它只用于识别并拒绝这一固定旧版无认证基线。对应 `/api` 响应为 `404` 和正文 `not found`。

该快照的上游仓库为 `https://github.com/deepseek-ai/deepseek-harness`，许可证见 `LICENSE.deepseek-harness`。
