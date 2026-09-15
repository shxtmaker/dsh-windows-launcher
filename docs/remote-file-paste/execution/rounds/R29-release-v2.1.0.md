# R29 · 发布 v2.1.0（推送 Gitea/GitHub + release + Windows 交接补全）

方案版本 2.0。用户明确指令：完成 Windows 轮次交接文档、把完整项目文件打包推送至 Gitea 与 GitHub、
版本号 2.1.0、新建 release 并上传安装包。

## 0. 重要结论（先说清楚）

- **Windows Launcher 安装包（`Setup-2.1.0-win-x64.exe`）本次未构建、未上传**：构建机为 Linux，
  `eng/package.ps1` 需要 Windows + Inno Setup 7.0.2 + WebView2 Offline/Bootstrapper 输入，
  并在隔离 runner 上做真实安装/卸载验证；`eng/verify.ps1` 的正式 Windows 门禁与 `RS-01`…`RS-15`
  发布冒烟也未执行。故 release 标记为 **prerelease**，其安装包由 Windows 轨道
  （[windows-runbook.md](windows-runbook.md) W00/W05）产出后另行附加。
- 已上传的**可安装产物**是附件插件包 `shxtmaker-dsh-remote-attachments-0.1.0.tgz`（真实可安装：
  `pnpm --dir <profile> add file:...tgz`），另有完整源码包与 Linux 门禁证据。

## 1. 版本升级

- `eng/release-constants.json`：`product.version` `2.0.9 → 2.1.0`；`distribution.officialReleaseUri`
  `v2.0.9 → v2.1.0`；`releaseStatus` 保持 `candidate`（枚举只允许 `development|candidate`，正式打包要求 candidate）。
- `eng/release-constants.md`：固定值来源表同步。
- 新增 `docs/release-notes/v2.1.0.md`（release 正文来源）。
- 未改动：`.NET SDK 10.0.400`、`net10.0-windows`/`win-x64`、配对基线、不签名策略、`limits.maxTargets`。

## 2. Windows 轮次交接文档补全

`docs/remote-file-paste/execution/windows-runbook.md` 新增 **§10 附件契约、限额、网络与诊断入口**，
补齐 [windows-validation-plan.md](../windows-validation-plan.md)（标题：**远程文件粘贴 Windows 实机验证方案**）
第 2 节材料表余下四行：

- **附件契约**：协议 v1 冻结位置、11 类消息、身份绑定（`operationId==batchId`、`fileId` 组成）、
  三态语义（无 `ready`/`uploaded`、`staged` 不保证 receipt 有效）、四阶段错误分期与 36+19 错误/结果码、
  输入所有权（文件列表优先、单一消费者、v1 仅 screenshot 信号位）、取消策略（`client-aborted` vs 403+`unpaired`）、
  适配器接入（Cordis `ctx`，不依赖 React 私有状态）、页面四个全局。
- **限额与资源**：以 `src/shared/protocol.ts` 常量为准（20 MiB / 10 文件 / 50 MiB / 4000 万像素 / 100 MiB 暂存 /
  2 目标 / 256 KiB 块 / 2 块在途 / 握手 5 s / ACK 10 s / fileEnd 30 s / 批次空闲 60 s / 缓存 64×120 s）；
  实测值用 `status().handshake.effectiveLimits`；暂存路径 `<应用数据根>\attachments\staging\<targetId:N>`；
  清理时机与功能关闭方法。
- **网络契约**：上传必须走 `/remote/api/session/uploadFileBinary`，设备 Cookie `dsh_pair` 或设备头
  `x-dsh-remote-device`；不静默回退裸 `/api`；只改写同源 `/api/` 前缀；脱敏规则。
- **诊断入口**：`__DSH_ATTACHMENTS_STATUS__` 完整字段（`state`/`capability`/`upload`/`advertised`/`handshake`/
  `transport`/`draft`/`files`/`partialFailures`）、`__DSH_ATTACHMENTS_BRIDGE__`/`RECEIVER`/`ADDON`；
  原生反射边界（WP-21…WP-25/WP-32/WP-37）；无任意本机读取入口。

同一轮修订了 `delivery-report.md` 的产品版本引用（2.0.9 → 2.1.0）。

## 3. 同候选 Linux 门禁（发布前权威轮）

```bash
pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable
```

- **runId `d24-2026-09-15T12-16-27-474Z-ed8d444f`**：**32/32 checks pass、incomplete 0、fail 0**、exit 0。
- `portableStatus=pass`、`crossBuildStatus=pass`、cases **546/546**（failed 0 / skipped 0）。
- 候选身份：gitHead `8e6ae61`（C1）、worktree dirty 1（唯一未提交变更 = runbook §10，porcelain
  `fbecd7bb…`）、插件 src 树 `c4597fe4…`（18 文件）；`hardViolations=[]`、`identityViolations=[]`、
  证据一致性 `agreementAll=true`。
- 零容忍负向控制 7/7、独立性 2/2 成立；`notRunSummary` = 20（WindowsPending 13 + 白名单 7，unjustified 0）。
- 证据：`artifacts/verify-portable/{portable-verify-summary.json,d24-linux-report.json,d24-runs/}`。

## 4. Git 提交、标签与推送

| 提交 | 内容 |
| --- | --- |
| `8e6ae61` (C1) | `feat: remote file paste attachments (2.1.0)` —— 全部功能源码/测试/schema/插件/eng/交接文档 + 版本 2.1.0 |
| `676f6a0` (C2) | `docs: complete Windows handoff materials` —— runbook §10（dirty 1 即此变更） |

- 分支：`main`（C1、C2 均已推送）、`feat/remote-file-paste-attachments`（同内容一并推送）。
- 标签：**`v2.1.0`（附注标签，对象 `f654e8f4`，指向 C2 `676f6a0`）**，已推送到两个 remote。
- Remote：`origin` = GitHub `shxtmaker/dsh-windows-launcher`；`gitea` = `192.168.3.100:3300/lqy/dsh-windows-launcher`。
- 推送**只包含源码与文档**；`artifacts/`（证据）与构建产物按 `.gitignore` 不入库。

## 5. Release 与资产

两个平台均创建 release **`DSH Windows Launcher 2.1.0`**（tag `v2.1.0`，`prerelease=true`，未 draft）：

- GitHub release id `389136149`；Gitea release id `30`。
- 资产（两平台一致，各 7 个，HTTP 201）：
  `shxtmaker-dsh-remote-attachments-0.1.0.tgz`（163 832 B）、
  `dsh-windows-launcher-2.1.0-source.tar.gz`（4 578 339 B）、
  `dsh-windows-launcher-2.1.0-source.zip`（4 911 881 B）、
  `SHA256SUMS.txt`、`portable-verify-summary.json`、`d24-linux-report.json`、`d25-handoff-manifest.json`。
- `d25-handoff-manifest.json` 已更新为 2.1.0 绑定：release 段（tag/commit/prerelease/`installerBuilt=false`）、
  新 runId、新候选身份。
- **未上传安装包**：见 §0。

## 6. 未验 / 不确定（不计通过）

- Windows 实机全部事实、正式打包/安装/升级/回退、`eng/verify.ps1` 正式门禁与 `RS-01`…`RS-15`：**WindowsPending**。
- 上游版本组合仍是 D00 登记的偏离（all/remote `0.3.20` + CLI `0.1.5-rc.1`，UI 包实为 `0.1.5-rc.2`）；
  Harness/dsh-web 源码 commit 未核实。
- `Platform.Windows.Tests` 反斜杠理论用例基线转义隐患未修（W00 阻断项）。
- 本次 release 为 **prerelease**；正式 Windows 安装包与 `releaseEligible=true` 均未达成。

## 7. 状态

v2.1.0 源码与文档已双平台推送，release 已创建并上传可用资产；安装包因缺少 Windows 构建环境未产出。
DEV 轨道保持 `DevelopmentReady`，Windows 保持 `pending`，`releaseEligible=false`。
