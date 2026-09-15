# D18 · 验证真实 C# 与浏览器生产接收器互通

轨道：DEV。状态以 `../execution/state.json` 为唯一真值；本任务卡不预置通过结果。

依赖：D17。默认下一建议：D19（仅建议，不在同轮自动执行）。

## 单一目标

补齐跨语言运行证据：真实Core输出进入Chromium生产receiver，再把ACK返给同一Core。

## 允许改动范围

- `Core.Tests 互操作用例`
- `插件 tests/interop/ Node驱动`
- `Linux真实全家桶集成`

## 执行步骤

1. 在既有Core.Tests中用测试通道连接Node/Chromium，例如子进程stdio；只替代WebView2边界，不替代sender算法。
2. 生产Core读取fixture、发送块，JS构造File并进入真实Harnessadapter；双向ACK和import-result均返回Core。
3. 将新互操作用例纳入完整 Development profile；在同一程序集的 manifest 中显式区分 Core/Interop 集合并核对并集覆盖全量基线。记录驱动工具依赖和正式 Windows 全测试需要的准备，不静默跳过。

## 必须交付

- 真实C#↔JS↔Harness互通测试与日志，双端消息trace。

## 本任务验收

- 不仅是黄金样本或JS模拟sender；哈希、批次/文件ID和草稿ID正确。
- Core.Tests实际生产实现被调用，工具子进程退出与资源回收可证明。

## 失败与停止条件

若必须新增辅助项目，先显式调整任务/依赖/工程清单，不能偷偷绕开项目数量门禁。

本轮只完成本任务及其直接验证/局部修复。达到验收、出现具体阻断，或到达需要拆分的范围边界时，写入轮次记录与检查点并结束；不得转入下一任务。缺少 Windows 实机仅影响独立系统验收，不是跳过开发实现的理由。

## 轮末证据与恢复点

记录变更文件、实际命令/退出码/用例数、证据路径与被测候选摘要；列出未验项及所属轨道。状态为 partial/blocked 时写明已保留成果、剩余一步和解除条件；下一轮先核对工作树及进程再恢复。
