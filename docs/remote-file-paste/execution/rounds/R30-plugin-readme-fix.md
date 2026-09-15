# R30 · 更正插件 README 并删除遗留探针（发布后修复轮）

方案版本 2.0。候选：分支 `feat/remote-file-paste-attachments`，提交
`aecb3f3e6e9c80616e84aa803b8ef2850fa16194`，**工作树 clean**。

用户指令：① 改正上游插件 README；② 补一个删除提交（移除含本机绝对路径的遗留探针）。

本轮任务：发布后局部修复 + 复验。违反"一轮一个 DEV 叶任务"的情形不适用——DEV 轨道已于 D25 结束调度，
本轮是按用户明确指令进行的**发布后修复轮**。

## 1. 实际变更

| 变更 | 说明 |
| --- | --- |
| `plugins/dsh-remote-attachments/README.md` | 从 **D03 阶段的过时描述**（"尚未注册上传承载（D07）与草稿入口（D08）"）更正为实际状态：线协议 v1 冻结要点、三态语义、能力与降级、限额、安装、命令、Linux 已验 / Windows 未验 |
| 删除 `plugins/dsh-remote-attachments/tests/fixtures/.d20-probe-tmp.mjs` | 遗留临时探针，**含硬编码本机绝对路径** `/home/lin-qingyue/AI-Project/...`；不参与任何门禁（全库无引用），但已被跟踪并随 v2.1.0 源码包与 release 资产外泄该路径 |
| 6 份文档同步新 tarball 摘要 | `f8961215…` → `64a359de…`（165 785 B / 59 文件）：`windows-runbook.md`、`windows-task-list.md`、`delivery-report.md`、`handoff.md`、`windows-verification-prompt.md`、`release-notes/v2.1.0.md` |

**未改动**：`src/**`（插件 `src` 树指纹仍为 `c4597fe4…`／18 文件，与 D24/D25 一致）、C#、`eng/**`、
`schemas/**`、CI/发布脚本。历史轮次记录（`rounds/R28-D25.md`、`rounds/R29-release-v2.1.0.md`）**保持原样**，
其中的旧摘要 `f8961215…` 是当时事实，不回改。

## 2. 复验（干净工作树，完整 Development 门禁）

```bash
pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable
```

- **runId `d24-2026-09-15T16-25-41-981Z-3c29392e`**：**32/32 checks pass、incomplete 0、fail 0**、exit 0。
- `portableStatus=pass`、`crossBuildStatus=pass`、cases **546/546**（failed 0 / skipped 0）。
- 候选身份：gitHead `aecb3f3e…`、**worktree clean**（porcelain 空串 sha256 `e3b0c442…`）、
  插件 src 树 `c4597fe4…`；`hardViolations=[]`、`identityViolations=[]`、证据 `agreementAll=true`。
- 零容忍负向控制 7/7、独立性 2/2 成立；`notRunSummary` = 20（WindowsPending 13 + 白名单 7，unjustified 0）。
- **D22 tarball 门禁 26/26 pass**（`failedGateIds=[]`），门禁重建的 tarball：
  `64a359de44fd12185297ba8939268208246e61981bf149a1efd0591dfc3b996b`，165 785 B / 59 文件，
  `builtAt 2026-09-15T16:45:32.613Z`。
- 插件侧同轮实跑：`pnpm run verify`（typecheck + build + unit + pack）通过、`test:pack` **10/10**。

## 3. 独立交叉核对

把 launcher 版与独立仓库版 tarball 解包逐文件比对：**文件清单完全一致（59 个），唯一内容差异是
`README.md`**（两边各自描述自身布局与来源）。因此两版 tarball 摘要不同只源于 README 文本。

## 4. 独立仓库（dsh-remote-attachments）同步

- 其 `README.md` 中"上游副本停留在 D03 描述"一句因本轮更正而失效，已改为
  "上游副本已在 `aecb3f3` 同步更正"（提交 `538de64`，已推送 GitHub 与 Gitea 的 `main`）。
- 该仓库 `v0.1.0` 标签与 release 资产**未动**。

## 5. 未验 / 待定

- **Windows 实机**：仍为 WindowsPending，本轮未执行任何 Windows 动作。
- **v2.1.0 release 资产仍是更正前的插件 tarball**（`f8961215…`，含过时 README）：GitHub release
  `389136149`、Gitea release `30` 的 `shxtmaker-dsh-remote-attachments-0.1.0.tgz` 与 `SHA256SUMS.txt` 未替换。
  候选处理：①原位置换该资产；或 ②另发 v2.1.1。**未在本轮擅自变更已发布 release。**
- 独立仓库与上游副本仍是"同源但非逐字节相同"（差异为布局相关的路径解析与 README 文本），
  如需收敛需在独立仓库复制上游 README 并把来源说明移入单独文件、另发 `v0.1.1`。

## 6. 状态

R30 **done**：README 已更正、遗留探针已删除、6 份文档摘要已同步；同候选完整 Development 门禁复跑
**32/32、546/546、clean、零违规**。DEV 轨道仍为 `DevelopmentReady`，Windows 保持 `pending`，
`releaseEligible=false`。
