# 远程附件线协议 v1（D10 冻结）

本目录是 version=1 线协议的**唯一冻结面**：

| 文件 | 作用 |
| --- | --- |
| `schema.json` | JSON Schema draft 2020-12：消息信封与全部消息类型（字段集、整数范围、枚举、`additionalProperties:false`） |
| `golden/*.json` | 有效报文（覆盖 11 种消息类型与边界值） |
| `malicious/*.json` | 无效报文（版本、枚举、范围、大小写、序号、身份、重放等） |
| `expected.json` | 语言中立的期望值：每个样本的 canonical 解码结果或**精确拒绝码** |

两端的生产 codec 必须读取**同一份** `expected.json`：

- TypeScript：`plugins/dsh-remote-attachments/src/shared/wire/`
- C#：`src/DshLauncher.Core/Attachments/`

任一实现偏离 `expected.json` 都会让 `plugins/dsh-remote-attachments/scripts/wire-contract-gates.mjs`
（`pnpm --dir plugins/dsh-remote-attachments run test:wire`）失败。
`expected.json` 由人工依据本文件与主方案 §4.2 编写，**不由任何一侧 codec 生成**；
其中载荷哈希来自 coreutils `sha256sum`，是独立于两端实现的第三方判据。

## 1. 身份

- 一次用户粘贴 = 一个 `batchId`；**首期 `operationId` 与 `batchId` 同值**，不维护两套可漂移身份。
- 每个文件有独立 `fileId`，绑定 `targetId` + `documentEpoch` + `composerEpoch` + `sessionId` + `batchId`。
- `documentEpoch` 由原生端掌握；`composerEpoch`/`composerScope` 由插件当前会话 scope 产生，
  scope 变化即视为失效（新 `context` 到达时旧身份被替换）。
- 没有有效 `sessionId` 不接收批次：`sessionId` 在 schema 中是可选的（握手期还不存在），
  但状态机对 `batch-begin`/`file-begin`/`chunk`/`ack`/`file-end`/`import-result`/`batch-end`/`cancel`
  一律要求它，缺失即 `no-session`，不匹配已绑定上下文即 `context-changed`。
- 过期身份在**进入缓冲前**被拒绝：`documentEpoch`/`composerEpoch`/`targetId`/`sessionId`
  任一与当前上下文不符即 `context-changed`，不写入任何接收缓冲。
- 导航/关闭调用 `navigate()`：清空重放缓存并让当前身份全部过期，直到新的 `context` 到达。

## 2. 消息表（类型 → 必填字段 → 范围）

信封恒有 `v`（const 1）与 `type`（枚举）。方向为约定用途，不写进 schema。

| type | 方向 | 必填字段（`?` 为可选） | 范围 / 枚举 |
| --- | --- | --- | --- |
| `hello` | 双向 | `clientBuild` | 1..64 字符，`^[A-Za-z0-9._/+:-]+$` |
| `capabilities` | 双向 | `features[]`, `limits{}` | features ≤5 且唯一；limits ≤ `DEFAULT_LIMITS` |
| `context` | client→native | `sessionId`, `targetId`, `documentEpoch`, `composerEpoch`, `composerScope` | epoch 0..2147483647；id 1..128 |
| `batch-begin` | client→native | `batchId`, `targetId`, `documentEpoch`, `composerEpoch`, `fileCount`, `totalBytes`（`sessionId?`） | fileCount 1..10；totalBytes 0..52428800 |
| `file-begin` | client→native | `batchId`, `fileId`, `name`, `byteLength`, `mime`（`sha256?`） | name 1..255 且无 `/`、`\`、NUL；byteLength 0..20971520 |
| `chunk` | client→native | `batchId`, `fileId`, `seq`, `offset`, `byteLength`, `dataBase64` | seq 0..79；offset 0..20971519；byteLength 1..262144；Base64 ≤349528 字符且解码字节数必须等于 byteLength |
| `ack` | native→client | `batchId`, `fileId`, `seq`, `offset`, `byteLength`, `bufferedBytes`, `inFlight` | bufferedBytes 0..104857600；inFlight 0..2 |
| `file-end` | client→native | `batchId`, `fileId`, `totalBytes`, `sha256`（`submittedItems?`） | totalBytes 0..20971520；sha256 必须 64 位小写十六进制；submittedItems 1..16，缺省 1 |
| `import-result` | native→client | `batchId`, `fileId`, `status`, `attachmentIds[]`（`code?`） | status ∈ {staged, failed, partial}；attachmentIds ≤16 且唯一 |
| `batch-end` | 双向 | `batchId`, `status`, `results[]` | results 1..10，每项 {fileId, status, attachmentIds?, code?} |
| `cancel` | 双向 | `batchId`（`reason?`, `stage?`） | reason ∈ `ERROR_CODES`；stage ∈ `ERROR_STAGES` |

`import-result.status` 与 `batch-end.results[].status` **没有** `upload ready`/`uploaded`：
上传阶段状态由 Harness 拥有，不进入线协议（见 §6）。

字段名逐个精确匹配；大小写变体是 `field-name-invalid`，其它多余字段是 `unknown-field`，
两者都属于拒绝理由，不做宽容解析。

## 3. 限额与超时

限额沿用 `protocol.ts` 的 `DEFAULT_LIMITS`（20 MiB/文件、10 项/批、50 MiB/批、
4000 万像素、100 MiB/目标暂存、2 个并发目标）与 `CHUNK_BYTES`(256 KiB)、
`MAX_CHUNKS_IN_FLIGHT`(2)。`capabilities` 只能声明**不高于**这些上限的生效限额；
批次/文件按生效限额判定，超限分别是 `limit-batch-files`、`limit-batch-bytes`、
`limit-file-bytes`、`limit-staging-bytes`。

单条消息 UTF-8 编码后 ≤ `MAX_MESSAGE_BYTES`(512 KiB)，超出即 `message-too-large`
（在字段级检查之前判定，避免解析超大载荷）。

超时冻结为 `WIRE_TIMEOUTS`（毫秒）：握手 5000、ACK 10000、file-end→import-result 30000、
批次空闲 60000。状态机通过 `checkTimeouts(nowMs)` 判定，不自行起定时器。

## 4. 序号与窗口

- `seq` **每文件从 0 开始**，`offset` 连续（下一个块的 offset = 已接收字节数）。
- 最多两块在途（已发送未 ACK）；第三块是 `window-overflow`。
- `seq`/`offset` 前进超过期望是 `sequence-gap`，回退（重复块）是 `seq-overlap`。
- `ack` 只证明**接收缓冲已接受**该块：不改变草稿状态，也不代表上传。
  重复 ack 是幂等重放；ack 指向从未发送的块是 `sequence-gap`。
- `file-end` 校验该文件累计字节（= `totalBytes` = `file-begin.byteLength`）与最终 SHA-256；
  只有干净时才允许构造 `File` 并调用**恰好一次**草稿导入（`importInvoked = true`）。
- `file-end` 与 `batch-end` 语义不同：前者结束一个文件，后者汇总并关闭整批；
  已 staged 的文件在 `batch-end` 时不得再次整体导入。
- 每 `file-end` 默认向原生入口提交 1 个 `File`，该次导入 ACK 要求新增 ID 数为 1；
  兼容 adapter 提交多项时用 `submittedItems` 声明实际项数，按实际项数比较
  （不符为 `import-id-count-mismatch`）。

## 5. 重放/去重缓存与批次生命周期

- 键：`documentEpoch` + `composerEpoch` + `batchId` + `fileId`（批级用空 `fileId`）。
- 容量 `REPLAY_CACHE_CAPACITY` = 64；生命周期 `REPLAY_CACHE_LIFETIME_MS` = 120000。
- 过期条目在查询时即被丢弃；**活动操作被钉住**，容量淘汰绝不驱逐活动条目
  （容量可被钉住条目暂时超出），因此活动操作不会因淘汰而被重新执行。
- 重复 `chunk`、`file-end`、`ack`、`batch-end` 不产生第二次导入：
  内容完全一致的重复是幂等重放（`duplicate = true`、`importInvoked = false`）；
  内容冲突的重复分别是 `duplicate-file-end`（file-end）与 `duplicate-operation`（batch-end）。
- 批次关闭后，该 `batchId` 只再接受**重复的 `batch-end`**（幂等/冲突判定）与 `cancel`；
  迟到的 `file-end`/`chunk`/`import-result` 一律 `batch-closed`，不得凭迟到消息重建操作。
- 关闭后要重新导入必须走新的 `batch-begin`（并生成新的 `batchId`/`fileId`）；
  复用已关闭批次的 `batchId` 是 `duplicate-operation`。
- 同一会话已有开放批次时再开一批是 `batch-in-progress`；每目标同时只处理一个文件，
  已有活动文件时再 `file-begin` 另一个是 `file-in-progress`。
- 部分成功保留已确认草稿：`cancel` 或部分失败不丢弃已 staged 的结果，
  由 `batch-end.results[]` 逐 `fileId` 说明成功/失败/待处理（不存在批次级布尔值）。
- `cancel` 只停止后续传输、释放自有资源并拒绝迟到结果；不自动撤销已发送消息、
  不删除远端共享文件。取消后的迟到消息是 `cancelled`。

## 6. 三种状态的精确含义

| 层 | 取值 | 含义 | 什么消息会改变它 |
| --- | --- | --- | --- |
| `transport` | `idle` / `buffering` / `buffered` | 接收缓冲是否已接受字节 | 仅 `chunk` / `ack` |
| `draft` | `none` / `staged` / `failed` / `partial` | 草稿是否已接收该 File | 仅 `import-result` |
| `upload` | `none` / `harness-owned` | 上传与发送归 Harness | `import-result`（staged 后置为 `harness-owned`） |

三者**从不合并**：`ack` 之后 `transport = buffered` 而 `draft` 仍为 `none`；
`import-result` 报 `staged` 之后 `draft = staged`、`upload = harness-owned`，
线协议里**不存在** `ready`/`uploaded` 取值。`expected.json` 中 G07/G08（ack 后仍未 staged）
与 G10（staged 后进入 harness-owned）逐字段断言这三层，任何一层被写成另一层的值都会失败。

## 7. 拒绝码

`WIRE_ERROR_CODES`（36 个，定义于 `protocol.ts`，与 schema/expected.json 对齐）：

`malformed-json`、`version-mismatch`、`unknown-message-type`、`unknown-field`、
`field-name-invalid`、`missing-field`、`invalid-field-type`、`invalid-field-value`、
`unknown-enum-value`、`integer-out-of-range`、`negative-integer`、`message-too-large`、
`field-too-large`、`limit-batch-files`、`limit-batch-bytes`、`limit-file-bytes`、
`limit-staging-bytes`、`no-session`、`context-changed`、`batch-not-open`、`batch-closed`、
`batch-in-progress`、`file-in-progress`、`file-id-mismatch`、`file-not-ended`、
`sequence-gap`、`seq-overlap`、`window-overflow`、`duplicate-operation`、
`duplicate-file-end`、`size-mismatch`、`hash-mismatch`、`import-id-count-mismatch`、
`result-incomplete`、`result-conflict`、`cancelled`

与结果类代码的分工：`ERROR_CODES`（协议四阶段的结果码，如 `draft-import-failed`、
`upload-failed`、`reload-required`）只出现在报文的 `code` 字段里，
不用作拒绝码；两者不混用。

判定顺序（两端必须一致，否则同一份恶意样本会得到不同的码）：

1. JSON 可解析性 → `malformed-json`
2. 整条消息 UTF-8 字节数 → `message-too-large`
3. `v`（缺失/类型/取值）→ `missing-field` / `invalid-field-type` / `version-mismatch`
4. `type`（缺失/类型/枚举）→ `missing-field` / `invalid-field-type` / `unknown-message-type`
5. 字段名扫描（按键名升序）：大小写变体 → `field-name-invalid`；多余键 → `unknown-field`
6. 按冻结字段顺序逐个字段：缺失 → `missing-field`；类型 → `invalid-field-type`；
   负数 → `negative-integer`；越界 → `integer-out-of-range`；超长 → `field-too-large`；
   枚举 → `unknown-enum-value`；格式/Base64 → `invalid-field-value`；
   chunk 声明字节数与载荷不符 → `size-mismatch`
7. 状态机：`no-session` → `context-changed` → 批次准入（`batch-in-progress` /
   `batch-not-open` / `batch-closed`）→ 限额 → 每类语义（序号、窗口、哈希、去重）

## 8. 复核方式

```bash
pnpm --dir plugins/dsh-remote-attachments run build
pnpm --dir plugins/dsh-remote-attachments run test:wire
pnpm --dir plugins/dsh-remote-attachments run test:unit
source /tmp/dsh-env.sh
dotnet build DshWindowsLauncher.slnx -c Release --warnaserror
dotnet test tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj -c Release
```

- TS 侧 `test/unit/wire-corpus.test.mjs` 与 `test/unit/wire-session.test.mjs` 调用生产 codec，
  判定路径与门禁脚本共用 `test/unit/wire-corpus-support.mjs`。
- C# 侧 `tests/DshLauncher.Core.Tests/Attachments/` 读取**同一份** `expected.json`，
  并有逐样本的期望变异负向对照。
- `test:wire` 产出三份证据：
  `artifacts/verify-portable/d10-wire-gates.json`、
  `artifacts/fixture/evidence/d10-wire-gates.json`，以及跨语言差分样例
  `artifacts/verify-portable/d10-wire-parity-cases.json`
  （由 C# 的 `WireParityTests` 消费：对同一批敌意变异输入，两端必须给出相同的
  ok/拒绝码/canonical 摘要；样例缺失时该测试会显式跳过）。
