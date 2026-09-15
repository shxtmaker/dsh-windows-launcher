# 远程文件粘贴附件 · 交付报告（D25 冻结）

> **后续进展（R29）**：本报告冻结于 D25。之后已按用户指令发布 **v2.1.0（prerelease）**——
> 分支 `main` 提交 `8e6ae61`+`676f6a0`、附注标签 `v2.1.0`，已推送 GitHub 与 Gitea 并各上传 7 个资产；
> **Windows Launcher 安装包未构建/未上传**（需 Windows + Inno Setup 7.0.2 + WebView2 与真实安装验证）。
> 详见 [rounds/R29-release-v2.1.0.md](rounds/R29-release-v2.1.0.md) 与 [handoff.md](handoff.md)。

方案版本 2.0。本报告在 **D25（冻结开发交付并移交 Windows 实机验证）**冻结；状态唯一真值仍是
[state.json](state.json)，逐轮细节见 [rounds/](rounds/)，恢复入口见 [handoff.md](handoff.md)。
按既定约定，本报告**只在出现需要 Windows 轮次处理的问题时**更新；本次更新即属该情形
（开发轨道收尾 + 向 Windows 移交），因此同时冻结终态与 Windows 侧必须先看的隐患。

## 1. 被测候选（唯一绑定的身份）

| 项 | 值 |
| --- | --- |
| 仓库 / 分支 | `https://github.com/shxtmaker/dsh-windows-launcher.git` · `feat/remote-file-paste-attachments` |
| HEAD | `df68795c3f7a0a34d6ed4fdd7804e369bdd0258b`（**未提交**，改动留在工作树） |
| 工作树 | dirty 37；`git status --porcelain=v1`（Trim）`sha256=0b99fd98968999bb5173eea6c1865e9f614df27d7e12a2045beaf616afdc8c10` |
| 附件插件 | `@shxtmaker/dsh-remote-attachments@0.1.0`；`src` 树 18 文件 `sha256=c4597fe4e012c76571c8cde8211b23b0aef6698b8ffc366d4c8d8ae78b0dfbbe` |
| 插件 tarball | `plugins/dsh-remote-attachments/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz`，163 832 B / 59 文件，`sha256=f8961215a8c9dffcca44ff78b82a49e8f39708e5df59e84bad46a161b0bc9556` |
| Harness 组合 | CLI `0.1.5-rc.1` + 内部 UI 包 `0.1.5-rc.2` + `@linxin666/dsh-web-all` / `dsh-remote-web-ui` `0.3.20`（用户决策"先沿用本机"） |
| 产品版本 | `2.1.0`，`releaseStatus=candidate`；支持 `Windows 11 25H2+ x64`，兼容记录 `Windows 10 22H2 x64` |
| 绑定清单 | `artifacts/verify-portable/d25-handoff-manifest.json` |

> 任何源码/工作树变化都使本报告与 D24 证据失效，须回到受影响任务重验，不得复用旧报告。

## 2. 交付状态

| 轨道 | 状态 | 依据 |
| --- | --- | --- |
| DEV（Linux 开发） | **`DevelopmentReady`** | D00–D24 全部 DEV 叶任务 done + R15 修复轮；D24 完整 Linux 门禁 32/32；D25 源码/tarball/实机交接齐备（[implementation-plan §7](../implementation-plan.md)、[windows-validation-plan §1](../windows-validation-plan.md)） |
| Windows 实机 | **`WindowsPending`** | 全部实机/打包/安装/发布用例未执行；见 [windows-task-list.md](windows-task-list.md) |
| 可发布 | **否（`releaseEligible=false`）** | 正式 `eng/verify.ps1` 的 `verify-summary.json` 与 Windows 验收全部缺失 |

## 3. 已交付能力（可复核）

- **可移植 Core**：`net10.0`，Linux 直接运行；`eng/verify-portable.ps1 -Profile Core` **7/7**（本轮独立复跑：Core 用例 542/542，failed/skipped/notRun 均 0）。
- **线协议 v1 冻结**：`schemas/remote-attachments/v1/`（11 类消息、`additionalProperties:false`、显式范围与枚举、语言中立 `expected.json`）；36 个协议拒绝码与 19 个结果码分工不混用；双端生产 codec（TS + C#）读同一份期望，并有 332 条敌意变异的跨语言差分。
- **分块协调器**（C#）：注入字节源/通道/时钟；短读、源错误、乱序、超时、取消、流释放；待确认字节 ≤512 KiB、队列 ≤fileCount、缓存有界且活动操作钉住不可淘汰。
- **浏览器接收端**（TS）：分块装配成真实 `File`（页面读回字节+SHA-256 比对）；LAN HTTP 无 WebCrypto 时走纯 JS 增量 SHA-256 且不关闭校验；无整文件 base64。
- **附加插件桥**：握手（版本/限额钳制/origin/会话身份）；三态可分离（`transport` / `draft` / `upload=harness-owned`，**不存在 `ready`/`uploaded`**）；逐文件部分失败账本；未知 hook ⇒ 能力冲突且不覆盖不贴牌；远端降级 ⇒ 能力不可用且配对不受影响、**不静默回退裸 `/api`**；拆解撤销与 `ReloadRequired`。
- **原生层（Windows 代码写实，无 stub/无 `#if` 假实现）**：真实 Win32 暂存（`CreateFileW CREATE_NEW` 独占、无跟随重解析点、限额先于分配、边拷边算哈希、清理只动自有根）；原生粘贴手势（真实手势才铸造短期一次性授权，无全局键盘钩子，同一动作只有一个消费者，DIB→确定性 PNG）；每目标 `RemotePageSession`（独占 WebView2 + 独立 UDF，导航开始即撤销旧能力，来源只做规范化逐字相等）。
- **兼容与停用**：能力矩阵 5 行（`verified` / `unknown-hook` / `remote-degraded` / `unsupported-version` / `addon-disabled`），逐行 `pairingUnaffected=true`；停用/恢复双向、`ReloadRequired` 双向、Harness 重启后通路照常。

## 4. 门禁与实测（Linux，冻结轮）

**权威轮**：干净状态 `pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable`，
runId `d24-2026-09-15T11-15-43-616Z-0a0f006c`（约 26 分钟）→

| 项 | 结果 |
| --- | --- |
| checks | **32/32 pass**（incomplete 0、fail 0），exit 0 |
| `portableStatus` / `crossBuildStatus` | `pass` / `pass`（**独立判定**：`Resolve-DshPortableStatus` 参数表不含 CrossBuildStatus） |
| 用例 | required=executed=**546**（failed 0、skipped 0） |
| `harnessIntegration` / `productionInterop` | `pass` / `pass` |
| `windowsValidation` / `developmentReady`（profile 字段） | `notRun` / `false`（`producesDevelopmentReady=false`）；`releaseEligible=false` |
| L 覆盖 | L01–L11、L13 全 pass、缺口 0；L12 单独判定 pass（交叉构建 0 警告 0 错误） |
| 零容忍负向控制 | 7/7（C1 用例计数 0、C2 skipped、C3 notRun、C4 层 incomplete、C5 无理由 notRun 均确实失败；C0/C6 不误报） |
| 独立性负向控制 | 2/2（cross 单独 fail ⇒ portable 仍 pass；core-build 单独 fail ⇒ portable fail、cross 仍 pass） |
| `notRun` | 20 = WindowsPending 13 + 白名单受限 7，`unjustified=0` |

`portableStatus=pass` **不等于**"所有测试通过"，更**不等于**可发布；`developmentReady=false`
是 profile 配置使然（Linux 可移植门禁不产出 DevelopmentReady），项目级 `DevelopmentReady`
由 D25 按方案 §7 记账。

## 5. D25 本轮独立复核（父代理亲测，非引用子代理结论）

1. **候选身份重绑**：独立重算工作树 porcelain 摘要与插件 `src` 逐文件指纹，与 D24 报告 `candidate` **逐字相同** ⇒ D24 证据对当前工作树仍有效。
2. **tarball 重绑**：`pnpm run build` 后逐文件比对 tarball 解包内容 —— **59/59 字节相同**；sha256 与 D22 清单一致。
3. **门禁声明复核**：`Resolve-DshPortableStatus`（无 CrossBuildStatus 参数）、`Resolve-DshCrossBuildStatus`（只读自身）、`$summaryWritten`（catch 仅在未落盘时兜底）逐处读码确认。
4. **纯函数负向控制**：从 `eng/verify-portable.ps1:76-148` 逐字提取三个纯函数，18/18 控制成立（含零用例/跳过/未验/失败/计数不符/独立性/`toolchainUnsupported`/WindowsPending 不误报）。
5. **摘要覆盖缺陷端到端证伪**：用脚本副本注入失败后测 —— 保留守卫时正式摘要**未被覆盖**（`requiredCases=542`、保留 candidate、无 `failure` 字段）；**关闭守卫的对照确实被覆盖**（`requiredCases=0`、无 candidate、有 `failure` 字段）⇒ 证明测试非空转、D24 的第 3 处修复真实生效。
6. **状态审计**：DEV 任务 D00–D24 全 done；**没有任何 Windows 平台证据被记 `pass`**；生产源码无 `TODO/FIXME`、无 stub 抛壳；证据路径仅 1 个临时夹具目录（`artifacts/fixture/provider/record/`，夹具按设计清理，门禁运行期内重建）。

## 6. 未验项（**不计入任何通过**）

- **Windows 实机全部事实**：Windows 项目/测试程序集、Win32/WebView2/WPF 语义、真实安装与 Runtime 矩阵、正式打包与发布门禁 ⇒ 全部 `WindowsPending`，明细见 [windows-task-list.md](windows-task-list.md) 第 3–4 节。
- D20 三条受限场景（`G21b` / `G33` / `G38`）与 D23 四条更新类场景（`RealAllBundleUpdate` / `UnverifiedUpstreamCombination` / `MarketplaceUpdateFlow` / `RealIndependentScopePluginUpdate`）为民用白名单 `notRun`。
- 本轮**未**在完整 Development 门禁中重跑（候选未变化，D24 权威轮证据仍绑定同一工作树）；本轮只做 Core profile 复跑与门禁函数级/摘要保护级校验。

## 7. Windows 轮次必须先看的问题（⚠️）

1. **`Platform.Windows.Tests` 基线转义隐患（W00 阻断项）**：4 条含反斜杠参数的理论用例，MTP 的 XML 输出把 `\` 再转义为 `\\`，而 D14 建立的该程序集基线来自枚举 JSON 归一值 ⇒ 真实 Windows 上跑 XML 门禁会报 `missing/unexpected`。修它会删除既有基线哈希，故按"只增不减"规则未在 DEV 轨道修改。**在此之前不得声称该程序集基线在 Windows 上可用**；W00 须按真实 XML 重新推导或统一转义归一。
2. **插件包摘要以 D25 交接清单为准**：`compatibility-lock.json` 的插件条目是 D03 快照（`5d4016be…`），不随构建更新；权威值是本报告第 1 节的 `f8961215…`。
3. **上游版本偏离**：本机为 all/remote `0.3.20` + CLI `0.1.5-rc.1`（实际 UI 包 `0.1.5-rc.2`），研究快照为 `0.3.21` + `0.1.5-rc.2`；Harness / dsh-web 源码 commit **未核实**（本机无 checkout）。若日后对齐快照，必须重跑 D04–D13。
4. **口径**：`Session.promptError` 公开路径存在（插件 `ctx.sessions.scope(id)` → `sessionOf(scope)` → `getSnapshot().promptError`）；当前黑盒测试**没有插件上下文句柄**，**不得**表述为"没有公开接口"。撤销证据只表述为"请求到达 Host 前被门控拒绝"（403 + `unpaired`），**不得**扩展为"已通过鉴权的上传流被主动终止"。

## 8. 如何复核

```bash
source /tmp/dsh-env.sh   # 或等价导出 DOTNET_ROOT=$HOME/.dotnet 与 PATH
# Linux 门禁（完整 Development）
pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable
# C# 侧
dotnet build DshWindowsLauncher.slnx -c Release --no-restore --warnaserror
dotnet run --project tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj -c Release -- -result-xml /tmp/core.xml
# 插件侧
pnpm --dir plugins/dsh-remote-attachments install --frozen-lockfile
pnpm --dir plugins/dsh-remote-attachments run verify
pnpm --dir plugins/dsh-remote-attachments run test:wire
bash plugins/dsh-remote-attachments/tests/fixtures/setup.sh
pnpm --dir plugins/dsh-remote-attachments run fixture:loop      # 最小闭环
pnpm --dir plugins/dsh-remote-attachments run fixture:receiver  # 接收端
pnpm --dir plugins/dsh-remote-attachments run fixture:bridge    # 附加插件桥
bash plugins/dsh-remote-attachments/tests/fixtures/down.sh
```

Windows 侧复核步骤见 [windows-runbook.md](windows-runbook.md)。证据目录：
`artifacts/verify-portable/`（门禁摘要、D25 交接清单与各判据 JSON）、`artifacts/verify-portable/d24-runs/`（权威轮 runId 副本）。
