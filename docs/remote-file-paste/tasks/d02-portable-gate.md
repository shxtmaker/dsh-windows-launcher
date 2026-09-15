# D02 · 建立可增量扩展的 Linux 开发门禁

轨道：DEV。状态以 `../execution/state.json` 为唯一真值；本任务卡不预置通过结果。

依赖：D01。默认下一建议：D03（仅建议，不在同轮自动执行）。

## 单一目标

建立能够真实检查已完成层级的开发入口，保留正式 Windows 门禁。

## 允许改动范围

- `eng/verify-portable.ps1`
- `eng/verification-profiles.json`
- `eng/verification-tests.ps1`
- `eng/verify.ps1`
- `eng/common.ps1`

## 执行步骤

1. 提供 Core 和 Development 两个显式 profile；Core profile 只验证明确的核心集合，不冒充整个项目。
2. 复用固定用例枚举、结果集合、failSkips 和触发标签核对；按项目求值 TFM/RID。
3. 新增 portable 摘要；Development 尚未实现的必需项保持 fail/notRun，不输出总通过。尝试完整 Linux 交叉构建并记录边界。

## 必须交付

- Core profile 可运行；Development profile 的检查清单及结果 schema。
- 原 verify 的公共 props、统一 win-x64 runner、锁文件数量断言已按显式配置调整。

## 本任务验收

- Core 格式/构建/实际测试可重复通过。
- 必需项缺失、零用例、一般编译错误和恢复失败均不能被摘要吞掉。
- Windows 发布入口拒绝将 portable 摘要用作正式发布凭据。

## 失败与停止条件

不把所有 Windows 项 skipped 后宣布完整验证通过；交叉构建平台限制需最小复现。

本轮只完成本任务及其直接验证/局部修复。达到验收、出现具体阻断，或到达需要拆分的范围边界时，写入轮次记录与检查点并结束；不得转入下一任务。缺少 Windows 实机仅影响独立系统验收，不是跳过开发实现的理由。

## 轮末证据与恢复点

记录变更文件、实际命令/退出码/用例数、证据路径与被测候选摘要；列出未验项及所属轨道。状态为 partial/blocked 时写明已保留成果、剩余一步和解除条件；下一轮先核对工作树及进程再恢复。
