# D08 · 实现原生草稿入口及同步导入确认

轨道：DEV。状态以 `../execution/state.json` 为唯一真值；本任务卡不预置通过结果。

依赖：D03、D05。默认下一建议：D09（仅建议，不在同轮自动执行）。

## 单一目标

使用既有文件 input 和公开 input 状态接入正确草稿，不替换原附件 UI。

## 允许改动范围

- `插件 session overlay`
- `版本化 composer adapter`
- `真实 Chromium UI 测试`

## 执行步骤

1. 定位 session scope 的唯一data-composer-card和file input；核对scope、phase、blocks、可编辑和子代理状态。
2. DataTransfer设置files并触发一次change；同一同步任务比较旧IDs完整保留、新ID数和顺序，建立本次导入映射。
3. 测试txt/PNG、图片校验、已有附件、切会话和无session首页；不使用私有createDrafts或抢single slot。

## 必须交付

- 受支持版本adapter、导入ACK测试及不支持原因。

## 本任务验收

- 新增附件ID确定属于当前操作，无自动发送、无document广播drop。
- 普通文本/全家桶附件UI保留，校验拒绝或数量不符不自动重放。

## 失败与停止条件

建立最小失败案例，记录兼容补丁决策；依赖本任务的闭环保持blocked，不能只返回Unsupported后记done。

本轮只完成本任务及其直接验证/局部修复。达到验收、出现具体阻断，或到达需要拆分的范围边界时，写入轮次记录与检查点并结束；不得转入下一任务。缺少 Windows 实机仅影响独立系统验收，不是跳过开发实现的理由。

## 轮末证据与恢复点

记录变更文件、实际命令/退出码/用例数、证据路径与被测候选摘要；列出未验项及所属轨道。状态为 partial/blocked 时写明已保留成果、剩余一步和解除条件；下一轮先核对工作树及进程再恢复。
