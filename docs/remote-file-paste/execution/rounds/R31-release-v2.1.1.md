# R31 · 发布 v2.1.1（2.1.0 更正版）并收敛独立插件仓库 v0.1.1

方案版本 2.0。候选：分支 `feat/remote-file-paste-attachments`，提交
`c2cae9b5adcdca35d0ab056cac9ca335fae3cac3`，**工作树 clean**；附注标签 **`v2.1.1` → `c2cae9b`**。

用户指令："均按推荐" —— 即：① launcher 以 **v2.1.1** 重新发布更正（v2.1.0 保持不可变）；
② 独立插件仓库收敛后发 **v0.1.1**。

## 1. launcher 侧变更（提交 `c2cae9b`）

| 变更 | 说明 |
| --- | --- |
| `eng/release-constants.json` | `product.version` `2.1.0 → 2.1.1`；`officialReleaseUri` → `.../tag/v2.1.1` |
| `eng/release-constants.md` | 固定值来源表同步 |
| `plugins/dsh-remote-attachments/README.md` | 冻结语料一句改为**布局中立**（"位于仓库的 `schemas/…`；且不随包发布"），使独立仓库可共用同一文本 |
| 新增 `docs/release-notes/v2.1.1.md` | 更正清单、兼容矩阵、下载与安装、验证状态、安装包缺件说明、沿用 2.1.0 的已知问题 |
| 活文档同步 | `windows-runbook.md`、`windows-task-list.md`、`delivery-report.md`、`handoff.md`、`windows-verification-prompt.md` → 新插件 tarball 摘要 `a6f865a4…`（165 802 B / 59 文件） |
| `docs/release-notes/v2.1.0.md` | **恢复**为实际发布值 `f8961215…` / 163 832 B（v2.1.0 不可变，不得被后续摘要覆盖） |

**功能源码零改动**：插件 `src` 树仍为 `c4597fe4e012c76571c8cde8211b23b0aef6698b8ffc366d4c8d8ae78b0dfbbe`（18 文件）；
C#、`eng/**`（除发布常量）、`schemas/**` 未改。

## 2. 同候选完整门禁复验

```bash
pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable
```

- **runId `d24-2026-09-15T17-11-50-245Z-b1a9284d`**：**32/32 checks pass、incomplete 0、fail 0**、exit 0。
- `portableStatus=pass`、`crossBuildStatus=pass`、cases **546/546**（failed 0 / skipped 0）。
- 候选：gitHead `c2cae9b`、**worktree clean**、`hardViolations=[]`、`identityViolations=[]`、证据一致性全绿。
- 零容忍负向控制 7/7、独立性 2/2；`notRunSummary` = 20（WindowsPending 13 + 白名单 7，unjustified 0）。
- **D22 tarball 门禁 26/26**；门禁重建 tarball = `a6f865a439297307501a58c9491e75aafb4438ad05c9f58a723a84923b55aac1`（165 802 B / 59 文件）。
- 交接清单 `artifacts/verify-portable/d25-handoff-manifest.json` 已更新到本轮（含 `release.correctiveRounds`）。

## 3. 独立插件仓库收敛并发布 v0.1.1

- `README.md` **复制上游同名文件**（逐字节相同，`sha256=64571cb2cd49048bd4ed90b6b98e6cf7d4253d835c879586d819ffe5b31ab929`）；
  来源/改动/同步说明移入新增的 `STANDALONE.md`。
- **两处构建的插件 tarball 逐字节一致**：`a6f865a4…`，165 802 B（`cmp` 通过）。
- 提交 `c1d9f08`；附注标签 `v0.1.1`；已推送 GitHub 与 Gitea 的 `main` + 标签。
- 本仓库内实跑：`pnpm run verify`（typecheck/build/unit/pack）通过、单元测试 **169/169**、
  `test:wire` **9/9**（86 样本）、`test:pack` **10/10**。

## 4. Release（两平台，均为 prerelease / 非 prerelease 按各仓库约定）

| 平台 | launcher v2.1.1 | 插件 v0.1.1 |
| --- | --- | --- |
| GitHub | release id `389349012`，7 个资产 | release id `389332244`，2 个资产 |
| Gitea | release id `33`，7 个资产 | release id `32`，2 个资产 |

- launcher 资产：插件 tarball、`dsh-windows-launcher-2.1.1-source.tar.gz`/`.zip`（按被验证提交 `c2cae9b` 生成）、
  `SHA256SUMS.txt`、`portable-verify-summary.json`、`d24-linux-report.json`、`d25-handoff-manifest.json`。
- launcher release 标记 **prerelease**（`Setup-2.1.1-win-x64.exe` 仍未构建，见 §5）。
- 插件资产：`shxtmaker-dsh-remote-attachments-0.1.0.tgz` + `SHA256SUMS.txt`，两平台下载后与本地构建 `cmp` 一致。

## 5. 未验 / 待定

- **Windows 实机全部事实**仍为 WindowsPending：WPF/WebView2/Win32 端到端、`eng/verify.ps1` 正式门禁、
  Offline/Online 正式打包与安装、升级/回退、`RS-01`…`RS-15`。
- **Windows Launcher 安装包仍未产出**（构建机 Linux；需 Windows + Inno Setup 7.0.2 + WebView2 输入与真实安装验证），
  v2.1.1 因此仍为 prerelease；补传步骤见 `rounds/R29-release-v2.1.0.md` §8（把 id 换成 v2.1.1 的两个 id）。
- **v2.1.0 保持不可变**：其 release 资产仍是当时的插件 tarball `f8961215…`（含过时 README），这是有意为之。

## 6. 状态

R31 **done**：v2.1.1 已双平台发布（源码 + 插件包 + 同候选 Linux 证据），独立插件仓库收敛为与上游逐字节一致的
`v0.1.1`。DEV 轨道仍 `DevelopmentReady`，Windows `pending`，`releaseEligible=false`。
