# D01 · 分离 Core 的平台目标

轨道：DEV。状态以 `../execution/state.json` 为唯一真值；本任务卡不预置通过结果。

依赖：D00。默认下一建议：D02（仅建议，不在同轮自动执行）。

## 单一目标

让生产 Core 与原有 Core.Tests 在 Linux 使用 net10.0 且不继承 Windows RID。

## 允许改动范围

- `Directory.Build.props`
- `六个现有 .csproj 与相关 lock 文件`
- `DshWindowsLauncher.Portable.slnx`

## 执行步骤

1. 平台属性移至项目声明；Core/Core.Tests 无 RID，另四项目保持原 Windows 目标。
2. 保留 SDK、MTP、分析器、发行身份与架构约束；显式重建受影响 lock。
3. 在 Linux 求值 MSBuild 属性、locked restore、格式检查、构建并直接运行原有 Core 测试。

## 必须交付

- 可移植 solution 和明确的项目平台定义。
- Core 基线枚举、实际执行结果与 lock 差异。

## 本任务验收

- Core/Core.Tests 最终 TFM=net10.0、RID为空，生产 Core 不增加平台依赖。
- 原有 Core 用例实际执行；零用例、跳过或未说明的基线删除不合格。

## 失败与停止条件

SDK/恢复失败保存日志，不换 SDK 或删除门禁凑通过；Windows 无在线设备不是此任务阻断。

本轮只完成本任务及其直接验证/局部修复。达到验收、出现具体阻断，或到达需要拆分的范围边界时，写入轮次记录与检查点并结束；不得转入下一任务。缺少 Windows 实机仅影响独立系统验收，不是跳过开发实现的理由。

## 轮末证据与恢复点

记录变更文件、实际命令/退出码/用例数、证据路径与被测候选摘要；列出未验项及所属轨道。状态为 partial/blocked 时写明已保留成果、剩余一步和解除条件；下一轮先核对工作树及进程再恢复。
