# D24 · 完整 Linux 开发验证收敛报告（草案）

> 本文件是**插件内草案**（允许改动范围：`plugins/dsh-remote-attachments/docs/**`）。正式轮次记录与 repo 级登记由父代理按文末《给父代理的登记要点》写入 `docs/remote-file-paste/execution/rounds/R27-D24.md`、`handoff.md`、`state.json`。

## 0. 身份与入口

| 项 | 值 |
| --- | --- |
| 任务 | D24（收敛完整 Linux 开发验证） |
| 候选 | 工作树（未提交），分支 `feat/remote-file-paste-attachments`，HEAD `df68795c3f7a0a34d6ed4fdd7804e369bdd0258b` |
| 工作树 | `dirty`，dirty 条目 `37`，porcelain sha256 `0b99fd98968999bb…` |
| 插件 | `@shxtmaker/dsh-remote-attachments` v0.1.0，src 树 sha256 `c4597fe4e012c76571c8cde8211b23b0aef6698b8ffc366d4c8d8ae78b0dfbbe`（18 文件） |
| 平台 | Linux（Ubuntu 26.04 LTS，x86_64），profile=Development |
| runId | `d24-2026-09-15T11-15-43-616Z-0a0f006c` |
| 机器可读报告 | `artifacts/verify-portable/d24-linux-report.json`（runId 副本 `artifacts/verify-portable/d24-runs/d24-2026-09-15T11-15-43-616Z-0a0f006c/report.json`，只追加索引 `d24-runs/index.json`） |
| 正式摘要 | `artifacts/verify-portable/portable-verify-summary.json` |
| 原始输出 | `artifacts/verify-portable/d24-run-console.log` |
| 运行窗口 | 2026-09-15T11:15:43.6161285Z → 2026-09-15T11:42:04.5119808Z（前台完整跑完，退出码 0） |

复跑命令（干净状态：先 `bash plugins/dsh-remote-attachments/tests/fixtures/down.sh`）：

```bash
source /tmp/dsh-env.sh
bash plugins/dsh-remote-attachments/tests/fixtures/down.sh
pwsh -NoProfile -File ./eng/verify-portable.ps1 -Profile Development -ArtifactsDirectory ./artifacts/verify-portable
```

## 1. 正式摘要（来源：`portable-verify-summary.json`）

| 字段 | 值 |
| --- | --- |
| `portableStatus` | `pass` |
| `crossBuildStatus` | `pass` |
| `developmentReady` | `false` |
| `releaseEligible` | `false` |
| `requiredCases` | `546` |
| `executedCases` | `546` |
| `failedCases` | `0` |
| `skippedCases` | `0` |
| `harnessIntegration` | `pass` |
| `productionInterop` | `pass` |
| `windowsValidation` | `notRun` |

- 检查：**32 pass / 0 incomplete / 0 fail**（必需项共 32，`notCoveredByProfile`=[]）。
- `developmentReady=false` 是 **profile 配置使然**（两端 profile 的 `producesDevelopmentReady` 均为 false）：Linux 可移植门禁不产出 DevelopmentReady，正式判定属 Windows `eng/verify.ps1`。
- 摘要的 `harnessIntegration` 已由 L06/L07 六项真实 Harness 检查推导：`pass`（修复了原先硬编码 `notRun` 的虚报）。
- `releaseEligible=false` 恒为 false：本摘要不是发布凭据（`windowsReleaseEntry.rejectsPortableSummary=true`）。

## 2. L01–L11、L13 覆盖表（L12 单独判定）

| L | 必需项（方案 §4） | 实现检查 | 状态 | 证据（均为本轮生成） |
| --- | --- | --- | --- | --- |
| L01 | MSBuild 求值、包锁、架构（Core/Core.Tests 无 Windows RID/API；六项目清单一致） | `platform-evaluation`(pass)、`portable-solution-shape`(pass)、`architecture-constraints`(pass)、`core-locked-restore`(pass)、`core-format`(pass)、`core-build`(pass) | **pass** | `eng/verification-profiles.json`、`DshWindowsLauncher.Portable.slnx` |
| L02 | C# Core 生产协调器（分块、ACK 窗口、取消、超时、乱序、重复、尺寸溢出、流释放） | `core-test-execution`(pass) | **pass** | `artifacts/verify-portable/DshLauncher.Core.Tests.core.test-evidence.json`、`artifacts/verify-portable/d11-coordinator-trace.json`；计数 tests=542 |
| L03 | C#/TS 双向协议（同一黄金样本与异常样本、字段/大小写/整数边界/错误码一致、生产 codec 交叉读取） | `wire-contract-goldens`(pass) | **pass** | `artifacts/verify-portable/d10-wire-gates.json`、`artifacts/verify-portable/d10-wire-parity-cases.json`；计数 gates=10 |
| L04 | JS 生产接收器（≤2 块在途、超限拒绝、恶意 offset/seq 不混入、无 Web Crypto 时哈希仍正确） | `browser-receiver-fixture`(pass)、`plugin-unit-tests`(pass) | **pass** | `artifacts/verify-portable/d12-receiver-gates.json`；计数 gates=13 |
| L05 | Linux Chromium 测试页（模拟 WebView bridge 消息产生 File；中文/空格名、零字节、PNG、批次上限） | `client-composer-fixture`(pass)、`draft-adapter-fixture`(pass) | **pass** | `artifacts/verify-portable/d08-composer-gates.json`、`artifacts/verify-portable/d08-ui-gates.json`；计数 gates=4、gates=13 |
| L06 | 真实 Harness＋全家桶＋附加插件（实际 host/client 挂载、早期注入、原配对/设置/附件 UI 不受影响） | `host-fixture-bundle`(pass)、`addon-bridge-fixture`(pass)、`addon-compatibility-fixture`(pass) | **pass** | `artifacts/verify-portable/d04-fixture-gates.json`、`artifacts/verify-portable/d13-bridge-gates.json`、`artifacts/verify-portable/d23-compat-gates.json`；计数 gates=10、gates=16、gates=18 |
| L07 | 真实上传和发送（/remote/api/session/uploadFileBinary、免 Cookie 设备头、receipt、发送前后无模型请求、工具读回） | `upload-hook-fixture`(pass)、`provider-fixture`(pass)、`host-fixture-minimal-loop`(pass) | **pass** | `artifacts/verify-portable/d07-upload-gates.json`、`artifacts/verify-portable/d06-provider-gates.json`、`artifacts/verify-portable/d09-loop-gates.json`；计数 gates=9、gates=9、gates=13 |
| L08 | 会话并发与安全（双目标、双会话、子代理、锁定 composer、后台页、iframe、导航、迟到 ACK；零错投） | `plugin-integration-suite`(pass) | **pass** | `artifacts/verify-portable/d20-isolation-gates.json`、`artifacts/verify-portable/d20-isolation-matrix.json`；计数 gates=43 |
| L09 | 网络与失败（HTTP 非 loopback、HTTPS 受信任测试证书、断流/超时/413/403/401/撤销/重启/上传失败；不自动重发） | `pairing-fixture`(pass)、`pairing-fixture-https`(pass)、`resource-and-failure-suites`(pass) | **pass** | `artifacts/verify-portable/d05-pairing-gates.json`、`artifacts/verify-portable/d05-https-gates.json`、`artifacts/verify-portable/d19-failure-gates.json`；计数 gates=12、gates=9、gates=32 |
| L10 | 打包安装（实际 tarball、独立隔离 profile、最终 bundle、锁定依赖、包内不含凭据或测试数据） | `supply-chain-scan`(pass)、`plugin-pack`(pass)、`final-tarball-install`(pass) | **pass** | `artifacts/verify-portable/d22-tarball-gates.json`、`artifacts/verify-portable/d22-package-manifest.json`；计数 gates=26 |
| L11 | 资源与退出（最大批次、重复 30 轮、接收内存上界、取消释放缓存、日志无秘密、不删除共享附件） | `plugin-browser-tests`(pass)、`resource-and-failure-suites`(pass) | **pass** | `artifacts/verify-portable/d21-resource-gates.json`、`artifacts/verify-portable/d21-resource-report.json`、`artifacts/verify-portable/d19-failure-gates.json`；计数 gates=52、metrics=45 |
| L12 | 全 solution 交叉构建（支持工具链时必过；已证实平台限制独立记录） | `cross-build-l12`(pass) | **pass** | `artifacts/verify-portable/d12-cross-build.log` |
| L13 | 真实 C#↔Chromium↔Harness 互操作（生产 sender/codec ↔ 生产 receiver，真实 ACK 回流、草稿确认、工具读取） | `production-interop-l13`(pass) | **pass** | `artifacts/verify-portable/d18-interop-gates.json`、`artifacts/verify-portable/DshLauncher.Core.Tests.interop.test-evidence.json`；计数 gates=12、tests=4 |

**覆盖缺口：0**（每个 L 层都映射到已通过的本轮检查，且每条证据存在、为本轮生成）。

- L12（全 solution 交叉构建）不并入 `portableStatus`，单独判为 `crossBuildStatus=pass`；证据 `artifacts/verify-portable/d12-cross-build.log`（本轮完整构建日志，0 警告 0 错误）。

## 3. 零容忍断言与负向控制

- 真实输入违规：**0**（`zeroTolerance.passed=True`）。规则：必需用例集合/实际执行 ≤0、执行≠必需、failed≠0、skipped≠0、任一必需层非 pass、任一用例证据计数 0、任何 notRun 非 WindowsPending/非白名单，均产生违规并令 `portableStatus=fail`。
- 摘要侧同款硬断言：`requiredCases=546`、`executedCases=546`、`failedCases=0`、`skippedCases=0`；`skippedCases` 已把 `skipped+notRun` 合并计数，故任何程序集跳过/未运行都会直接失败。

| 控制 | 变异 | 期望 | 实测违规数 | 成立 |
| --- | --- | --- | --- | --- |
| `C0-clean-input` | 干净输入（对照，防止误报） | 0 违规 | 0 | ✅ |
| `C1-zero-cases` | L02 用例证据计数改成 0 | ≥1 违规 | 1 | ✅ |
| `C2-skip` | 某程序集 skipped=1 | ≥1 违规 | 1 | ✅ |
| `C3-not-run` | 某程序集 notRun=1 | ≥1 违规 | 1 | ✅ |
| `C4-incomplete-layer` | 必需层 L02 状态 incomplete | ≥1 违规 | 1 | ✅ |
| `C5-unjustified-notRun` | notRun 无正当理由 | ≥1 违规 | 1 | ✅ |
| `C6-windows-pending-notRun` | notRun 明确为 WindowsPending | 0 违规（不误报） | 0 | ✅ |

结论：`C1`（0 用例）、`C2`（skip）、`C3`（notRun）、`C4`（层 incomplete）、`C5`（无理由 notRun）都确实产生违规；`C0`/`C6` 证明干净与 WindowsPending 输入不会误报——即判据**可证伪、不是恒真**。

## 4. 独立性与不可掩盖

- `portableStatus=pass`、`crossBuildStatus=pass`（`crossBuildAcceptable=True`）。
- 结构性隔离：`Resolve-DshPortableStatus` 的参数表**没有** `CrossBuildStatus`（`portableStatusReadsCrossBuild=false`，报告字段实测 false）；可移植失败集合显式排除 `cross-build-l12`。
- 摘要同时给出 `portableLayerFailures=[]` 与 `crossBuildIndependence` 块。

| 控制 | 变异 | 期望 | portableStatus | crossBuildStatus | 成立 |
| --- | --- | --- | --- | --- | --- |
| `I1-crossbuild-fail-only` | 把 cross-build-l12 改成 fail（可移植层不动） | portableStatus 仍为 pass；crossBuildStatus 变为 fail | pass | fail | ✅ |
| `I2-portable-fail-only` | 把 core-build 改成 fail（cross-build 不动） | portableStatus 变为 fail；crossBuildStatus 仍为 pass | fail | pass | ✅ |

结论：把 `cross-build-l12` 单独改成 fail，`portableStatus` 不变；把可移植检查 `core-build` 单独改成 fail，`crossBuildStatus` 不变。**二者不能互相掩盖**；交叉构建 fail 仍通过 `$failures` 令进程退出码为 1（不会静默通过）。

## 5. 候选一致性

- 权威身份：gitHead `df68795c3f7a0a34d6ed4fdd7804e369bdd0258b`、分支 `feat/remote-file-paste-attachments`、dirty `37`、插件 v0.1.0、src 树 `c4597fe4e012c76571c8cde8211b23b0aef6698b8ffc366d4c8d8ae78b0dfbbe`。
- 一致性判定：摘要 `commit` 必须等于 `git rev-parse HEAD`；所有内嵌 `candidate` 块的证据必须逐字段一致；D22 manifest 与 gates 的 tarball sha256 必须一致；所有 `artifacts/` 下的证据 mtime 必须落在本轮窗口内（不混入旧候选）。

| 证据 | 声明字段 | 与权威一致 |
| --- | --- | --- |
| `artifacts/verify-portable/d19-failure-gates.json` | gitHead, gitBranch, pluginVersion, worktreeDirtyCount, pluginSourceTreeSha256 | ✅ |
| `artifacts/verify-portable/d20-isolation-gates.json` | gitHead, gitBranch, pluginVersion, worktreeDirtyCount | ✅ |
| `artifacts/verify-portable/d21-resource-report.json` | gitHead, gitBranch, pluginVersion, worktreeDirtyCount, pluginSourceTreeSha256 | ✅ |
| `artifacts/verify-portable/d22-package-manifest.json` | pluginVersion | ✅ |
| `artifacts/verify-portable/d22-tarball-gates.json` | pluginVersion | ✅ |

- 身份违规：**0**。
- 未内嵌身份的同期证据（`identityNotDeclared`）：`artifacts/verify-portable/d10-wire-gates.json`、`artifacts/verify-portable/d12-receiver-gates.json`、`artifacts/verify-portable/d13-bridge-gates.json`、`artifacts/verify-portable/d18-interop-gates.json`、`artifacts/verify-portable/d23-compat-gates.json`。
  - 理由：这些证据未内嵌 candidate 块（d18 由 C# interop fixture 写入，C# 不在 D24 允许改动范围；其余为早期脚本格式）。它们以『同一轮运行内重写』（mtime 落在本轮窗口内）+ 已声明身份证据的 gitHead/工作树/插件版本/源码树哈希一致来保证不混入旧候选；D24 报告只把已声明身份的证据计入 identityAgreement，其余列入 identityNotDeclared 而不静默忽略。
- `eng/verification-profiles.json` 的 Development `requiredChecks` 由 31 项增至 **32 项**（新增 `d24-linux-convergence`）。

## 6. notRun 清单（WindowsPending vs 无理由）

- 合计 **20**：WindowsPending **13**，显式白名单的受限场景 **7**，**无理由 `0`**。

| notRun | 来源 | 分类 | 说明 |
| --- | --- | --- | --- |
| `DshLauncher.Platform.Windows` | eng/verification-profiles.json#expectedProjects | WindowsPending | 非可移植 Windows 项目：Linux 只做交叉编译（L12），实机行为属 WindowsPending。 |
| `DshLauncher.Desktop` | eng/verification-profiles.json#expectedProjects | WindowsPending | 非可移植 Windows 项目：Linux 只做交叉编译（L12），实机行为属 WindowsPending。 |
| `DshLauncher.Platform.Windows.Tests` | eng/verification-profiles.json#expectedProjects | WindowsPending | 用例调用真实 Win32 API；Linux 上 25 例中 15 例失败，且经 --runtime win-x64 调用时缺 Microsoft.WindowsDesktop.App。 |
| `DshLauncher.Acceptance.Tests` | eng/verification-profiles.json#expectedProjects | WindowsPending | 需要 Microsoft.WindowsDesktop.App 运行时，Linux 上无法启动（缺框架，退出码 150）。 |
| `WindowsValidationAndDevelopmentReady` | portable-validation-plan.md §1/§6 | WindowsPending | Windows Clipboard/STA/WPF/WebView2 实机与正式 DevelopmentReady 属 Windows 的 eng/verify.ps1；Linux profile 的 producesDevelopmentReady=false。 |
| `G21b-real-app-inside-subframe` | artifacts/verify-portable/d20-isolation-gates.json | justified-scope-limited | 实测该形态会触发应用自身的 frame-bust：子框架里的应用会把**顶层文档**导航成它自己的 URL，从而毁掉夹具自己的落点单元文档（本轮日志里可看到 3099/pair-app?device=… 的文档替换告警）。为了不让判据自己破坏被测对象，该形态未验证；子框架无文件能力这一结论由 G19/G21/G22 与 |
| `G33-busy-turn-is-real` | artifacts/verify-portable/d20-isolation-gates.json | justified-scope-limited | 点击发送后 76 次采样（30 秒窗口）内一直没有观察到"停止生成"按钮或非 plain 阶段：phase=plain stopVisible=false sendDisabled=[false] click=clicked；provider stub 的 agent 请求数=0（>0 才说明点击真的把请求送出去了）。 |
| `G38-subagent-session-specific-state` | artifacts/verify-portable/d20-isolation-gates.json | justified-scope-limited | 共享 DSH_HOME 的 session/list 里没有可识别的子代理会话（条目字段=["sessionId","updatedAt","running","blank","cwd","projections"]）；造出它需要一次真实的嵌套子代理回合，超出本次夹具范围。因此本操作只覆盖"瞄准非当前会话"这一确定性核 |
| `WP-01-windows-file-locks-acls` | artifacts/verify-portable/d21-resource-report.json | WindowsPending | WindowsPending：需 Windows 实机；按 D21 口径本轮不尝试（Core 侧已用等价的注入端口覆盖可移植部分） |
| `WP-02-windows-process-tree` | artifacts/verify-portable/d21-resource-report.json | WindowsPending | WindowsPending：需 Windows 实机；按 D21 口径本轮不尝试（Core 侧已用等价的注入端口覆盖可移植部分） |
| `WP-03-webview2-physical-boundary` | artifacts/verify-portable/d21-resource-report.json | WindowsPending | WindowsPending：需 Windows 实机；按 D21 口径本轮不尝试（Core 侧已用等价的注入端口覆盖可移植部分） |
| `WindowsPackaging` | artifacts/verify-portable/d22-tarball-gates.json | WindowsPending | WindowsPending：Linux 上不尝试，按任务卡口径保留 |
| `WindowsVerifyPs1` | artifacts/verify-portable/d22-tarball-gates.json | WindowsPending | WindowsPending：本检查只属 Linux Development 门禁 |
| `RealWebView2Boundary` | artifacts/verify-portable/d22-tarball-gates.json | WindowsPending | WindowsPending：Linux 夹具用真实 Chromium + 真实 /remote 通道替代，物理边界不同 |
| `LauncherByteSource` | artifacts/verify-portable/d22-tarball-gates.json | WindowsPending | WindowsPending：本轮用接收端分块协议直接驱动，等价于对端发送分块 |
| `WindowsPackagingAndUpdate` | artifacts/verify-portable/d23-compat-gates.json | WindowsPending | WindowsPending：本机为 Linux，无 WebView2/Windows 安装器 |
| `RealAllBundleUpdate` | artifacts/verify-portable/d23-compat-gates.json | justified-scope-limited | 按要求不得对生产执行更新；夹具固定 0.3.20 且不引入未验证版本，故只从 lock/manifest 推理 |
| `UnverifiedUpstreamCombination` | artifacts/verify-portable/d23-compat-gates.json | justified-scope-limited | 该组合未被选定（见 compatibility-lock.json 的 deviationFromResearchSnapshot），本机未安装 |
| `MarketplaceUpdateFlow` | artifacts/verify-portable/d23-compat-gates.json | justified-scope-limited | 夹具的 llm 基址为本地 stub，市场/更新入口不在夹具判据范围内 |
| `RealIndependentScopePluginUpdate` | artifacts/verify-portable/d23-compat-gates.json | justified-scope-limited | 本轮不安装第二个插件版本；独立范围由组合配置差异 + profile 清单/锁文件未被改写证明 |

口径：零容忍规则**默认拒绝**——只有 `reason` 明确以 `WindowsPending` 开头、或 `platform=windows`，或在配置白名单 `d24Convergence.acceptedScopeLimitedNotRun` 内的条目才被接受；其余一律 `unjustified` 并失败。白名单条目是跨轮 D19/D20/D23 遗留的、各自带 reason 的受限场景，既不算通过也不是 WindowsPending，D24 报告显式列出而不吞掉。

## 7. 顺带修复（D24 允许的“局部门禁修复”）

1. `eng/verify-portable.ps1`：`portableStatus` 原先由“所有 fail 检查”推导 ⇒ **交叉构建失败会把 portableStatus 一起打成 fail**（互相掩盖）。现改为只取可移植失败集合（排除 `cross-build-l12`），`crossBuildStatus` 独立由 `cross-build-l12` 推导。
2. 同上：交叉构建原先只跑 `dotnet build` 不落日志 ⇒ L12 只有结论没有证据。现改为 `Invoke-DshNativeCapture` 并把成功/失败原始日志写入 `artifacts/verify-portable/d12-cross-build.log`。
3. 同上：新增 D24 收敛检查（覆盖表/零容忍/独立性/候选一致性/正式摘要），并把 `d24-linux-convergence` 登记进 Development `requiredChecks`；新增 `d24Convergence` 配置段（层映射、身份证据、notRun 白名单）。
4. 同上（`eng/verify-portable.ps1` 原 2057 行）：任何检查失败时，`if ($failures.Count -gt 0)` 分支的 `Write-Error` 在 `$ErrorActionPreference='Stop'` 下抛出终止错误，被外层 `catch` 捕获并**用兜底摘要覆盖已写好的正式摘要**（`portableStatus=fail`、`crossBuildStatus=notRun`、cases 全 0、丢失 candidate/testAssemblies）。症状实测：第一次 D24 失败轮的 `portable-verify-summary.json` 只剩 `failure`+`checks`，计数与候选全丢。最小修复：新增 `$summaryWritten` 标志，`catch` 仅在真实摘要未落盘时才写兜底摘要。
5. 同上：摘要的 `harnessIntegration` 原为硬编码 `notRun`，与 L06/L07 六项真实 Harness 检查全部 pass 的事实矛盾（属虚报）。现由纯函数 `Resolve-DshHarnessIntegrationStatus` 按这六项状态推导（全 pass 才 pass），D24 报告与正式摘要共用。
6. D24 实现缺陷（在正式跑前的离线干跑中发现，非既有门禁缺陷）：`Get-DshD24NotRunInventory` 对没有 `notExecutableReason` 的 Windows 项目直接取属性，在 StrictMode 下抛错；已改为按 `PSObject.Properties.Name` 存在性取值并给默认理由。

## 8. 未验 / 不确定

- Windows 实机（Clipboard/STA/WPF/WebView2、MSI/zip 安装、`eng/verify.ps1` 正式 DevelopmentReady）全部 notRun=WindowsPending，不计入任何通过。
- 本报告只证明非 Windows 开发验证有效；`portableStatus=pass` **不等于**可发布。
- L12 目前为 pass（不是 toolchainUnsupported）；若未来在受限工具链上运行，`toolchainUnsupported` 必须附最小复现材料，普通编译错误仍为 fail。

## 9. 改动清单

- `eng/verification-profiles.json`：Development `requiredChecks` 增加 `d24-linux-convergence`；新增 `d24Convergence` 段（13 层映射、`identityEvidence`、`identityNotDeclared`、`acceptedScopeLimitedNotRun`）。
- `eng/verify-portable.ps1`：新增 D24 纯函数（`Resolve-DshPortableStatus`、`Resolve-DshCrossBuildStatus`、`Get-DshD24SourceTreeHash`、`Get-DshD24LayerViews`、`Get-DshD24ZeroToleranceViolations`、`Get-DshD24NotRunInventory`、`New-DshD24ProbeLayer`）；新增 `d24-linux-convergence` 检查；交叉构建日志落盘；`portableStatus` 与 `crossBuildStatus` 解耦；摘要新增 `candidate`/`d24Report`/`d24RunId`/`d24Coverage`/`d24Gaps`/`notRunInventory`/`notRunSummary`/`portableLayerFailures`/`crossBuildIndependence`。
- `artifacts/verify-portable/d24-linux-report.json` + `d24-runs/<runId>/report.json` + `d24-runs/index.json`（只追加）。
- 本草案 `plugins/dsh-remote-attachments/docs/D24-LINUX-REPORT.md`。
- 未改动：仓库级 `docs/**`、`schemas/**`、任何 C# 文件、`plugins/.../src/**`。

## 10. 给父代理的登记要点

1. **轮次记录**：新建 `docs/remote-file-paste/execution/rounds/R27-D24.md`，状态 **done**；内容：D24 目标、本次 runId、31→32 项检查、`portableStatus=pass`、`crossBuildStatus=pass`、`cases=546/546`、L01–L11+L13 覆盖表（无缺口）、零容忍与独立性负向控制、候选身份、notRun 清单（全部 WindowsPending 或显式白名单）。
2. **state.json**：`currentTask` 置空；D24 标 `done`；`round` 递增为 R27；记录 `portableStatus=pass`、`crossBuildStatus=pass`、`developmentReady=false`（profile 配置）、`releaseEligible=false`；下一建议任务 D25。
3. **handoff.md**：更新“当前状态”（D00–D24 done、下一轮 D25）、门禁现状（32/32 checks pass、incomplete 0）、新增的 D24 检查与报告路径、以及“portableStatus≠DevelopmentReady、L12 独立判定”的口径。
4. **不得**把 `portableStatus=pass` 写成“所有测试通过”或“可发布”；`releaseEligible=false` 恒定。
5. 本草案可直接作为 R27-D24 的“完整 Linux 报告”附件来源。

