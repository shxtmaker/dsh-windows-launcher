# 远程文件粘贴开发方案包

方案版本：2.0。日期：2026-09-13。环境：DeepSeek Harness＋`@linxin666/dsh-web-all` 全家桶＋独立附件插件，客户端为本仓库 Windows Launcher。

本方案包用于实施普通文件、多文件和截图粘贴为当前会话待发送附件。Windows 端采用局部重构。开发及项目开发验证在 Linux 完成，Windows 实机与安装验收另行执行。

## 文档入口

| 文件 | 用途 |
| --- | --- |
| [技术开发方案](implementation-plan.md) | 架构、数据协议、插件边界、局部重构和完成标准 |
| [26 项开发任务索引](task-index.md) | 每项依赖、任务卡、改动范围、步骤、交付和验收 |
| [分轮执行规则](execution-workflow.md) | 一轮一个任务、拆分、暂停、恢复及证据持久化 |
| [非 Windows 开发验证](portable-validation-plan.md) | 13 层验证、命令契约、真实集成与开发门禁 |
| [Windows 实机验证](windows-validation-plan.md) | 原生构建、输入、WebView2、资源、安装和独立分轮安排 |
| [DeepSeek Harness 完整提示词](deepseek-harness-prompt.md) | 可直接复制的实施指令，首轮仅 D00 |
| [依赖图](task-graph.json)、[初始台账](execution/state.json)、[交接入口](execution/handoff.md) | 可恢复的执行状态；当前所有开发任务均未开始 |
| [既有研究探针](probes/README.md) | 脚本级证据及其限制；不能代替 Linux/Windows 功能验证 |

## 使用步骤

1. 将本目录完整保存为目标 Launcher 仓库的 docs/remote-file-paste/。仅复制主方案或提示词会丢失任务卡与恢复规则。
2. 将完整提示词中分隔线后的正文交给 DeepSeek Harness。首轮只能执行 D00，核实基线并建立台账，不实施业务代码。
3. 检查该轮交接后发送“继续”，每次最多推进一个依赖满足的任务。任务未完成时继续同一任务，不能自动跑完整个项目。
4. D24 完整 Linux 门禁及 D25 交接完成后，开发结果记 DevelopmentReady，Windows 仍为 WindowsPending。
5. 在独立 Windows 实机任务中执行专项验证，通过正式门禁后再判定是否具备发布条件。

本包替代此前 .scratch 内不同部署环境及 P0–P4 粗粒度执行方案。研究源码快照不等同实际安装版本，所有功能与发布结论必须来自实施后同一候选的有效证据。本文编写完成不代表代码任务已执行。
