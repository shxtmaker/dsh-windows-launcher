# R15 · D10 契约修复 · cancel 之后同会话无法开新批次

方案版本与被测候选摘要：方案 2.0。候选为 Launcher 仓库工作树，分支 `feat/remote-file-paste-attachments`，HEAD `df68795c3f7a0a34d6ed4fdd7804e369bdd0258b`（未提交）。插件 `@shxtmaker/dsh-remote-attachments@0.1.0`；C# 侧 `DshLauncher.Core`（可移植 `net10.0`）。

本轮任务：修 R14 记录在案的契约缺陷。**不推进 D13**，按用户指示先插一轮专修。

## 1. 缺陷与根因

**症状**：`cancel` 之后，同一会话**再也开不了新批次**——`batch-begin` 被永久拒为 `batch-in-progress`。用户取消一次粘贴后，不刷新页面就贴不进来。

**根因**：`cancel` 只把批次标记为 `cancelled`，**不标记 `closed`**，而三处准入/路由都只看 `closed`：

| 位置 | 修复前 | 修复后 |
| --- | --- | --- |
| TS 状态机 `src/shared/wire/session.ts:382` | `if (!this.batch.closed) return reject('batch-in-progress', …)`；随后无条件 `duplicate-operation` | 取消视同关闭（不再占用会话）；且**只有复用同一个 `batchId`** 才判 `duplicate-operation` |
| TS 接收端 `src/client/receiver.ts:576` | `if (open !== null && !open.closed)` → 把新批次路由给被取消的旧批次 | 加 `&& !open.cancelled`；新 `batchId` 走"新建状态机 + 内部预热身份"的正路 |
| C# 镜像 `src/DshLauncher.Core/Attachments/AttachmentSession.cs:325` | `_batch.Closed ? duplicate-operation : batch-in-progress` | 取消视同关闭；同一 `batchId` 才 `duplicate-operation`，否则放行新批次 |

方案依据："`cancel` 对指定操作停止后续传输，释放自有资源，拒绝迟到结果" + "批次关闭后**新批次必须经 `batch-begin` 承认**"——取消不应让会话报废。

**为什么 R14 没修成**：R14 只改了第一处，于是新批次虽被旧会话接受，接收端却仍把后续 `file-begin` 路由给旧批次，表现为 `batch-not-open`。本轮三处一起改才通——这也是当时完整回退的原因（半改比不改更糟）。

## 2. 判据（三层，都可证伪）

| 层 | 判据 | 结果 |
| --- | --- | --- |
| TS 单测 `test/unit/receiver-protocol.test.mjs` | cancel → 同会话新 `batchId` 被承认 → `file-begin`/分块/`file-end` 完成导入（`importInvoked`、恰好一次）→ 复用被取消的 `batchId` 仍拒 `duplicate-operation` | PASS（**修复前该用例失败**：先报 `batch-in-progress`，只改一处时报 `batch-not-open`） |
| C# 测试 `WireSessionTests.CancelReleasesTheSessionSoANewBatchIdIsAdmitted` | cancel → 复用旧 `batchId` 拒 `duplicate-operation` → 换新 `batchId` 被承认（非 `batch-in-progress`） | PASS |
| 真实 Chromium `G12-cancel-then-new-batch` | 页面内 cancel → 新 `batchId` 被承认 → 分块 → `file-end` 完成导入 → 页面读回字节与源一致（13065B，sha256 相同）；复用被取消的 `batchId` → `duplicate-operation` | PASS |

负向方向已实证：TS 用例在修复前确实失败（两次不同的失败码），因此判据不是恒真。

## 3. 复验

| 项 | 命令 | 结果 |
| --- | --- | --- |
| 插件单测 | `node --test "test/unit/**/*.test.mjs"` | **101/101 通过**（100 → 101） |
| D10 线协议门禁（语料 86 样本） | `pnpm run test:wire` | pass（**未改语料**：全库检索确认没有任何样本固化旧语义） |
| D12 接收端判据 | `node tests/fixtures/d12-receiver-gates.mjs` | **13/13 gates、7/7 负向探针、exit 0**（12 → 13，新增 G12） |
| C# 构建 / 测试 | `dotnet build --warnaserror` / `dotnet run --project tests/DshLauncher.Core.Tests … -result-xml` | 0 警告 0 错误 / **88/88 通过**（87 → 88） |
| 格式门 | `dotnet format … --verify-no-changes --no-restore` | exit 0 |
| 固定用例基线 | `eng/expected-test-dataset.json` | Core.Tests **87 → 88**，`removed=0 added=1`（先断言基线条目未消失才写入） |
| Development profile | `pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development` | 见 state.json |

## 4. 影响面与回归

- **两端语义现在一致**：TS 状态机与 C# 镜像的准入规则逐条对应；TS 接收端多一层"按 `batchId` 保留批次会话"的路由（`file-begin` 等仍按 `batchId` 找到自己的会话），这正是 R14 漏掉的一环。
- **既有行为未变**：仍在进行中的批次照样拒新批次（`batch-in-progress`）；已关闭批次的迟到 `chunk`/`file-end` 仍判 `batch-closed`；复用已关闭 `batchId` 仍判 `duplicate-operation`；D10 语料 86/86、D12 其余 12 条判据、D08 13/13、D09 13/13 全部照旧通过。

## 5. 未验项

- 未在 Windows/真机验证（本轮为 Linux + 真实 Chromium 夹具）。
- 未验证"取消后立刻重开"在**真实传输通道**下的时序（D12 尚未接通道，属 D13）；本轮验证的是接收端与状态机在受控输入下的行为。

## 6. 状态

R14 记录的契约缺陷**已修复并三层加锁**。D10 的冻结语义按方案原文更正（取消不再占用会话），语料无需变更。D12 保持 done。**下一步回到 D13**（浏览器接收端接入真实传输通道），其"取消后重开"路径现在有判据守住。
