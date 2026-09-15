# @shxtmaker/dsh-remote-attachments

DeepSeek Harness（DSH）远程会话的**附件粘贴附加插件**：把 Windows Launcher 采集的普通文件与截图，
经带序号/ACK/背压的分块传输导入**当前可编辑会话**的待发送草稿。纯文本粘贴保持 Harness 原行为；
用户主动发送前不自动发送。

## 这个包做什么，不做什么

| | 归属 |
| --- | --- |
| 附件能力（本插件独占） | `attachment-paste-import` |
| 配对令牌、设备凭据、`/api/pair/*`、`/remote` 门控代理 | `@linxin666/dsh-remote-web-ui`（全家桶内，**本包不再装第二份**） |
| 会话草稿、上传端点与 receipt | DeepSeek Harness |

安装本插件不会替换、重新打包或修改 `@linxin666/dsh-web-all` 的聚合子插件清单。
禁用本插件只停止附件能力，配对、心跳与设备库保持原状。

## 环境要求

| 依赖 | 版本/要求 |
| --- | --- |
| Node.js | 22+ |
| pnpm | 11.x（`pnpm-lock.yaml` 固定 devDependencies） |
| DSH Harness | CLI `>= 0.1.5-rc.1`（见 `package.json` 的 `dsh.engines`） |
| 全家桶（仅夹具需要） | `@linxin666/dsh-web-all` `0.3.20`（含 `dsh-remote-web-ui` `0.3.20`） |
| 浏览器（仅夹具需要） | 固定版本 Chromium / Google Chrome（`DSH_ATTACH_CHROME` 可覆盖） |

## 安装

```bash
pnpm install --frozen-lockfile
pnpm run build && pnpm run test:pack      # 产出 pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz
pnpm --dir <profile-dir> add file:<tarball 绝对路径>
```

本包通过 `cordis.patch.yml` 挂载：host 半区以 **fetch 形状载体**安装 `__DSH_FILE_UPLOAD__`
（消费者读 `hook.fetch`），client 半区经 Cordis `ctx` 接入原生草稿/附件接口。
页面侧全局：`__DSH_ATTACHMENTS_BRIDGE__`、`__DSH_ATTACHMENTS_RECEIVER__`、
`__DSH_ATTACHMENTS_ADDON__`、`__DSH_ATTACHMENTS_STATUS__`（只读诊断，不暴露任意本机读取）。

**不要手改** `lib/client.js`：它由 `scripts/build-client-bundle.mjs` 生成。client-modules 的批次是各包
`client.js` 的**原始字节拼接**，入口顶层出现任何 `import`/`export` 都会让整批解析失败。

## 目录结构（包内）

```text
src/host.ts                     包的 `.` 入口（host 半区）
src/host/index.ts               host 半区实现（含上传承载）
src/client/                     client 半区：桥、接收端、握手、草稿适配器、组合
src/shared/                     两端共用：protocol / limits / capabilities
src/shared/wire/                冻结 codec（session / messages / replay-cache / sha256 / base64）
scripts/                        构建、清理、打包卫生扫描、线协议门禁
test/unit/                      单元测试与语料运行器
tests/fixtures/                 真实 Harness/Chromium 夹具与各任务门禁
tests/interop/                  C# 生产协调器的对端驱动
docs/                           兼容/停用策略与 Linux 验证报告
cordis.patch.yml                单一 Cordis 行：id=remote-attachments
```

线协议 v1 的冻结语料位于仓库的 `schemas/remote-attachments/v1/`
（`schema.json` + `golden/` + `malicious/` + `expected.json`）：它与 C# 生产 codec 共用同一份期望，
且不随包发布（`package.json` 的 `files` 只含运行期产物）。

## 命令

```bash
pnpm run typecheck      # tsc --noEmit
pnpm run build          # tsc（ESM）→ CJS → 经典包裹产物 lib/client.js
pnpm run test:unit      # node --test test/unit/**
pnpm run test:wire      # 线协议语料门禁（读 schemas/remote-attachments/v1）
pnpm run test:pack      # 生成真实 tarball 并逐条核对内容（P01–P08，含卫生扫描）
pnpm run verify         # typecheck + build + test:unit + test:pack
```

`test:pack` 判定的是**实际 tarball 内的字节**（不是工作树、不是 source link）：包身份、双面入口与类型声明、
`cordis.patch.yml` 的单行声明、"不含凭据/生产数据/本机绝对路径"、"不含测试与源码目录"。

**夹具类测试**（`fixture:*`）需要真实 `dsh` CLI、私有 `DSH_HOME`、固定全家桶版本
（`tests/fixtures/compatibility-lock.json`）与真实 Chromium。它们只在**私有**夹具目录
（`artifacts/fixture`，可用 `DSH_ATTACH_FIXTURE_ROOT` 覆盖）内运行，**绝不触碰** `$HOME/.dsh`；
`fixture:down` 只在双重确认（命令行含 profile 名 + `DSH_HOME` 指向夹具）后停进程。

`tests/interop/d18-interop-driver.mjs` 是 C# 生产协调器的对端驱动，由
`DshLauncher.Core.Tests`（`-trait interop=production`）驱动运行，需与本仓库同工作区使用。

## 状态

- **线协议 v1 已冻结**：11 类消息、`operationId==batchId`、`fileId` 绑定
  `targetId+documentEpoch+composerEpoch+sessionId+batchId`、`seq` 每文件从 0、最多 2 块在途、256 KiB 块；
  36 个协议拒绝码与 19 个结果码分工不混用（四阶段：native-capture / protocol-transfer / draft-import / remote-upload）。
- **三态语义**：`transport{idle,buffering,buffered}` / `draft{none,staged,failed,partial}` /
  `upload{none,harness-owned}` —— 协议里**没有** `ready`/`uploaded`；`staged` 只表示对端草稿已接收字节，
  不保证 Harness 侧 receipt 仍有效。
- **能力与降级**：未知 hook ⇒ `capability-conflict` 且不覆盖不贴牌；远端不可用 ⇒
  `unavailable`/`capability-disabled` 且**不静默回退裸 `/api`**；停用/恢复与 `ReloadRequired` 可判定；
  配对、心跳、设备库不受影响。
- **限额**：单文件 20 MiB、单批 10 项 / 50 MiB、截图 4000 万像素、单目标暂存 100 MiB、并发目标 2；
  握手 5 s、单块 ACK 10 s、`file-end`→`import-result` 30 s、批次空闲 60 s；重放缓存 64 条 / 120 s。

## 已验与未验

- **Linux（已实跑）**：插件单测、线协议门禁（86 样本 + 332 敌意变异）、打包判据、真实 Harness＋全家桶
  夹具、`DshLauncher.Core.Tests` 的 C#↔浏览器生产互操作用例。证据见 `docs/D24-LINUX-REPORT.md`。
- **Windows 实机（未验，WindowsPending）**：WPF/WebView2/Win32 端到端、安装与升级矩阵尚未执行；
  相关待验清单见仓库 `docs/remote-file-paste/execution/`。不得据此宣称 Windows 已通过。

## 许可

`UNLICENSED`（见 `package.json`）。
