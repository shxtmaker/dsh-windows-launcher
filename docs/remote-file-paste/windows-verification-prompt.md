# 用于 DeepSeek Harness 的 Windows 实机验收分轮提示词

方案版本：2.0。本提示词把 [远程文件粘贴 Windows 实机验证方案](windows-validation-plan.md) 第 9 节的 W00–W06 阶段、
[Windows 实机验证运行手册](execution/windows-runbook.md) 与
[Windows 实机验证任务列表与待验清单](execution/windows-task-list.md) 组织成可在 Windows 测试机上执行的**分轮验收指令**。

用法：在 Windows 测试机上准备本仓库（含 `docs/remote-file-paste/` 全部文档），复制分隔线之后的完整正文作为首条提示词；
此后每次发送“继续”只推进**一个 W 阶段**。运行手册与任务列表必须随提示词可读，单独发送本提示词不能替代它们。

---

请按照仓库 `docs/remote-file-paste/` 下的 2.0 版方案，在**当前 Windows 测试机**上执行“远程文件粘贴附件”的
Windows 实机验收。始终用中文说明，代码、命令和报错保持原样。

## 一、被测候选与身份（每个结论都必须绑定）

- 仓库 `https://github.com/shxtmaker/dsh-windows-launcher.git`，分支 `main`，发布标签 **`v2.1.0`**
  （附注标签指向提交 `676f6a0e6d69a84c7c430a4952cfa5c5c5eea2f8`）；`main` 尖端另有发布记录提交。
- 附件插件 tarball：`plugins/dsh-remote-attachments/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz`
  （`sha256=64a359de44fd12185297ba8939268208246e61981bf149a1efd0591dfc3b996b`，59 文件，165 785 B）。
- Linux 开发门禁（发布前同候选轮，runId `d24-2026-09-15T12-16-27-474Z-ed8d444f`）：Development **32/32 pass**、
  cases **546/546**、`portableStatus=pass`、`crossBuildStatus=pass`。**这只是非 Windows 层级**。
- 上游：Harness CLI `0.1.5-rc.1`（实际 UI 包 `0.1.5-rc.2`）+ `@linxin666/dsh-web-all`/`dsh-remote-web-ui` `0.3.20`。
- 产品版本 `2.1.0`、`releaseStatus=candidate`；支持 `Windows 11 25H2+ x64`，兼容记录 `Windows 10 22H2 x64`。

**第一条铁律**：每一项结果都必须与上面**同一候选**绑定（提交 SHA + 工作树状态 + 插件 tarball SHA-256 +
`eng/verify.ps1` 的 `verify-summary.json` 摘要）。候选源码/依赖/测试配置一旦变化，按影响范围重跑相关用例，
**不得**用旧结果充当新候选的通过证据。开始前先 `git rev-parse HEAD` + `git status --porcelain` +
`Get-FileHash …tarball -Algorithm SHA256` 三项核对并把结果写进本轮记录。

## 二、最优先的执行节奏

1. **首次执行只完成 W00**：材料与身份核对、`eng/verify.ps1` 原生完整门禁、按 7.1 正式打包、
   至少一种包的隔离安装。完成 W00 后立即结束本轮，不进入 W01。
2. **此后每条用户明确执行指令最多执行一个 W 阶段**（W01→W02→W03→W04→W05→W06 的顺序受第 9 节前置约束）。
   “继续”默认恢复上轮 `in_progress`/`partial` 的阶段；上轮 `blocked` 且阻断未解除时可另选依赖已满足的阶段，
   但必须保留原阻断记录。用户点名阶段时优先尊重；前置未满足则说明依赖并结束。
3. 阶段完成、需要拆分、出现具体阻断或本轮被中断时，保存成果与检查点后结束。**不要**自行进入下一阶段，
   不要把 W00–W06 合并执行，不要在同轮并行推进两个阶段。
4. 工具返回、模型自动续写、上下文压缩、子代理完成、定时事件都**不是**新的执行轮。用户只询问进度时只回答，
   不因此启动下一阶段。不得用自动任务或后台代理绕过分轮限制。
5. 阶段过大时（尤其 W00 与 W05）本轮只登记子阶段（如 `W00.1 身份与门禁`、`W00.2 离线包与安装`、
   `W05.1 Offline`、`W05.2 Online`），父阶段标记 `split`，结束后等新的执行指令。
6. 子代理只能承担当前阶段内的工作；本轮结束前收拢结果、停止会继续写入的后台任务并持久化记录。
7. **首次执行 Windows 阶段不解除任何 Linux/DEV 任务**；DEV 轨道已结束调度（D00–D25 done + 发布轮 R29）。

## 三、先读什么（顺序固定）

1. 仓库 `AGENTS.md`、`CONTEXT.md`、`eng/README.md`、`git status` 与当前分支/标签。
2. [远程文件粘贴 Windows 实机验证方案](windows-validation-plan.md)：验收用例 W-01…W-08.x、环境矩阵、证据模板、安装/回退矩阵。
3. [Windows 实机验证运行手册](execution/windows-runbook.md)：**入口文档**。§0 交接件与候选身份、§1 上游身份与版本组合、
   §2 工具依赖（测试机准备）、§3 源码与待编译项、§4 原生用例、§5 执行步骤（W00–W06）、§6 安装与回退方案、
   §7 已知问题与决策点（Windows 侧必须先看）、§8 证据采集与脱敏、§9 阶段结束时的记录要求、§10 附件契约/限额/网络/诊断入口。
4. [Windows 实机验证任务列表与待验清单](execution/windows-task-list.md)：阶段表、W-01…W-08.x 清单、
   `WP/WW/WS/WB/WM` 全量编号、20 条门禁级 `notRun`、§5 的 W00 决策项。
5. [远程文件粘贴附件·交付报告（D25 冻结）](execution/delivery-report.md) 与
   [开发执行交接（D25 冻结）](execution/handoff.md)：交付状态、门禁实测、实测陷阱与口径纪律。
6. `execution/state.json`（唯一真值）、`execution/rounds/R29-release-v2.1.0.md`（发布记录）、
   `plugins/dsh-remote-attachments/docs/COMPATIBILITY.md`（兼容与停用策略）、各 `*.WindowsPending.md`（原生用例原文）。

已有执行状态时必须**恢复**，不能清空后重新开始 W00，也不能覆盖既有证据。

## 四、环境与工具（缺失即报“环境未就绪”，不得静默跳过）

- Windows 11 25H2+ x64 实体机/VM（标准用户、NTFS）；兼容记录环境为 Windows 10 22H2 x64。
- PowerShell **7.x**（`pwsh`，不是 5.1）、.NET SDK **10.0.400**（精确）、Node.js 22+、pnpm、
  与 Linux 交接一致的固定 Chromium/Playwright、WebView2 SDK `1.0.4129.50`、
  实际 WebView2 Runtime（从运行中的 WebView2 采集，**不得**用 Edge 版本推定）、
  WebView2 Offline Standalone x64 与 Bootstrapper（冻结版本 + SHA-256 + 有效签名）、Inno Setup `7.0.2`。
- 记录并纳入每轮记录：OS 完整构建号、架构、CPU/内存、磁盘类型与可用空间、PowerShell/.NET/Node 版本、
  Launcher 文件版本与候选提交、WebView2 SDK 与 Runtime 完整版本、DPI 缩放、网络类型。
- 资源基准：4 逻辑处理器 / 8 GiB 内存；W-07 采样必须覆盖 Launcher **及其 WebView2 子进程树**，不能只看主进程。
- 网络目标：A-LAN（私有局域网**非 loopback** HTTP）、A-TLS（可信证书 HTTPS）、B（不同实例/profile）、
  A/B 扩展共四个目标；Cookie 模式与免 Cookie 设备凭据模式分别验证。

## 五、逐阶段执行要点

> 每个阶段的完整命令、断言与证据要求以**运行手册 §5** 与**任务列表 §1–§2** 为准，下面是不可省略的要点。

### W00 接收、原生构建与首个安装
1. 核对 §一 的三项身份；核对材料表（运行手册 §0–§1、§10 的附件契约/限额/网络/诊断入口）。
2. **先处理两个决策项**（任务列表 §5）：
   - `W00-A` `Platform.Windows.Tests` 反斜杠理论用例基线转义：真实 Windows XML 门禁会因 MTP 双重转义报
     `missing/unexpected`（4 条）。必须按真实 XML 重新推导或统一 `\`→`\\` 归一；**在此之前不得声称该程序集基线可用**。
   - `W00-B` 插件包摘要一律用 D25 交接清单的 `tarball.sha256`（`64a359de…`）；`compatibility-lock.json` 的插件条目是旧快照。
3. 运行正式 Windows 门禁（完整 solution，不是只构建 Core）：
   ```powershell
   pwsh -File .\eng\verify.ps1 -DotNetPath "$env:LOCALAPPDATA\DshWindowsLauncherDev\dotnet-10.0.400\dotnet.exe"
   ```
   保存完整输出与 `verify-summary.json`。注意：`portable-verify-summary.json` 是 Linux 摘要，**不是发布凭据**。
4. 按方案 §7.1 正式打包（隔离干净 runner，保留 `installer/DshWindowsLauncher.iss` 的安装保护）：
   ```powershell
   pwsh -File .\eng\package.ps1 -Version 2.1.0 -PackageMode Offline ... -AllowInstallerExecutionForUninstallerVerification
   pwsh -File .\eng\package.ps1 -Version 2.1.0 -PackageMode Online  ... -AllowInstallerExecutionForUninstallerVerification
   ```
5. 至少完成一种包的隔离安装，记录产物身份（候选 EXE、已安装 EXE、托管程序集、Bootstrapper、卸载器 SHA-256、
   `NotSigned` 状态）。

### W01 粘贴与发送（W-02、W-03）
资源管理器单文件/多文件/截图/焦点与 DPI（`W-02.1…W-02.8`）；A-LAN 与 A-TLS × Cookie/免 Cookie 的真实上传与
**主动发送后内容读取**闭环（本机快照 SHA-256 = 页面 `File` 字节 = 远端工具读回字节），并检查没有绕过代理的
裸 `/api/session/uploadFileBinary` 请求。对应原生编号 `WW-01…WW-06`、`WM-13…WM-32`、`WP-26…WP-34`。

### W02 身份与路径（W-04、W-05）
目标/草稿隔离、iframe/来源、原生手势、重解析点与不支持输入（`WS-01…WS-14`、`WM-07…WM-12`、
`WP-01…WP-25`、`WP-35…WP-38`）。重点：无用户动作不得读取；不存在网页可调用的通用 `readPath`；
来源只做规范化后**逐字相等**；隐藏/切标签/删除目标/导航各自的确定拒绝码。

### W03 网络与恢复（W-06）
HTTP/HTTPS × Cookie/免 Cookie、断流、413/401/403/5xx/HTTP 200 `{ok:false}`/畸形响应、撤销、取消、
Harness 重启与 Renderer/App 重启后的语义（`WM-33…WM-40`）。
**口径**：撤销只表述为“请求到达 Host 前被门控拒绝”（403 + `unpaired`），**不得**写成“已通过鉴权的上传流被主动终止”。

### W04 资源与稳定性（W-07）
限额、长期混合使用、内存/磁盘/句柄、退出与暂存清理；沿用方案 §5.7 的阈值与判定；
覆盖 D21 的三条 Windows 资源项（`WP-01-windows-file-locks-acls`、`WP-02-windows-process-tree`、
`WP-03-webview2-physical-boundary`）。

### W05 完整安装与升级矩阵（W-08）
Offline/Online 干净安装、Runtime 缺失/旧版/损坏、包型互换、就地升级/修复、安装路径、卸载、
功能回退与版本回退（方案 §7.1–7.2）。候选改变时重验受影响的前序用例。

### W06 发布验收报告（RS-01…RS-15）
对两种包分别执行：
```powershell
pwsh -File .\eng\release-smoke.ps1 -InstallerPath '<实际安装包>' -EvidenceDirectory '<证据目录>' -PackageMode Offline -ReleaseKind FirstRelease
pwsh -File .\eng\release-smoke.ps1 -InstallerPath '<实际安装包>' -EvidenceDirectory '<证据目录>' -PackageMode Online  -ReleaseKind FirstRelease
```
汇总工程门禁结果、安装产物身份、W-01…W-08 专项结果、原有 RS 结果、支持/不支持版本组合与尚存问题，
给出 `Windows 验证完成` 或“未完成 + 责任项 + 复测条件”的明确结论。保持 `Human release confirmation: NO`
直到发布方另行确认。**不自动发布**。

## 六、证据与判定规则

- 每个用例只允许 `PASS / FAIL / BLOCKED / PENDING / N/A / RECORD`。`N/A` 必须附支持范围依据；
  环境不可用记 `BLOCKED`，**不能**算 `PASS`；没有真实运行的项目保留 `PENDING`；**未执行一律不得写 PASS**。
- 证据目录按方案 §6 组织，每条用例至少记录：用例 ID、结果、执行时间与执行者、候选提交/包摘要、
  OS/WebView2/DPI、环境 ID/目标别名/网络与认证模式、前置与合成数据 ID、步骤与阶段、期望/实际、
  脱敏 operation/draft/session 关联、三方字节数与 SHA-256、是否用户主动发送、资源峰值/取消时延/清理结果、
  证据相对路径、问题编号与复测结果。
- 阻断级问题（错投目标/草稿、无用户动作读取、跨源泄漏、字节损坏、重复自动发送、根外清理）不得因其它用例通过而豁免；
  安全或数据问题在 Windows 10 兼容环境同样记 `FAIL`。
- 脱敏：不得保存配对凭据、设备密钥、完整配对 URL、Cookie 值、请求正文、文件内容或完整本机路径；
  需要展示 URL 时隐藏查询与 fragment；不归档带凭据的 HAR。
- **台账与轮次持久化**：每轮结束前更新 `execution/state.json`（`project.windowsStatus`：
  `pending`/`inProgress`/`passed`/`failed`，并新增/更新 `windows` 段记录当前阶段、阶段状态、证据路径、
  候选身份），写 `execution/rounds/W<st>-<阶段>.md`（如 `W00-native-build.md`、`W05-install-matrix.md`），
  更新 `execution/handoff.md`。Windows 证据另存，**不得覆盖** Linux 报告与既有 artifacts。

轮次记录模板：

```markdown
# W<st> / <阶段名>
用户执行指令：
候选摘要（提交/工作树/tarball sha256/verify-summary）：
本轮范围：
执行前状态：
实际变更：
实际验证：命令、平台、退出码、用例数、证据路径
未执行项与原因：
本轮结果：done / partial / blocked / split / interrupted
已保留成果：
剩余步骤或解除条件：
本轮仍运行的后台写入：无
下一轮建议：仅建议，不自动执行
```

## 七、Windows 侧必须先处理的已知问题（不得当作已通过）

1. ⚠️ `Platform.Windows.Tests` 反斜杠理论用例基线转义隐患（W00 阻断项，见 W00-A）。
2. 插件包摘要以 D25 交接清单为准（`64a359de…`）；`compatibility-lock.json` 为 D03 旧快照。
3. 上游版本偏离：本交付为 all/remote `0.3.20` + CLI `0.1.5-rc.1`（UI 包实为 `0.1.5-rc.2`），
   研究快照为 `0.3.21` + `0.1.5-rc.2`；Harness/dsh-web 源码 commit **未核实**。对齐快照须重跑 D04–D13。
4. `Session.promptError` 的公开路径存在（客户端插件 `ctx.sessions.scope(id)` → `sessionOf(scope)` →
   `getSnapshot().promptError`）；不得表述为“没有公开接口”。
5. 撤销语义见 W03 口径；`incomplete>0` 时增量命令 `exit 0` 可保留，但完整验收必须看 `portableStatus` 与完整性字段。
6. 20 条 `notRun`（WindowsPending 13 + 白名单受限 7）与 `unreported` 场景不得被改写成通过；
   受限场景（G21b/G33/G38、四个更新类场景）如需覆盖须先登记新任务。

## 八、禁止事项

- 不自动推送、打标签、发布 Release、发布 npm、替换生产插件或升级正式版本；
  补传安装包与把 release 转正**只在用户明确授权后**执行（步骤见 `execution/rounds/R29-release-v2.1.0.md` §8）。
- 不修改 Harness 核心或已安装上游包来绕过失败；不为通过而删除测试、关闭 `failSkips`、
  放宽阈值、改写验收标准或把 `PENDING`/`BLOCKED` 标成 `PASS`。
- 不在生产 profile、真实用户凭据或日常开发机上做破坏性操作；Runtime 缺失/旧版/损坏与卸载/回退只在隔离快照执行。
- 不向证据或日志写入凭据、Cookie、配对链接、请求正文、文件内容或完整私人路径。
- 不用 Linux 浏览器测试替代 WebView2 实机证据，也不用交叉编译结果宣称 Windows 行为已验证。

## 九、每轮最终答复只包含

1. 本轮阶段 ID、名称与状态（`done`/`partial`/`blocked`/`split`/`interrupted`）。
2. 完成内容与关键变更。
3. 实际验证：命令、平台、退出码、用例数、证据路径；以及未执行项及其原因。
4. 台账与轮次记录路径（`state.json`、`execution/rounds/W*.md`、`handoff.md`、Windows 证据目录）。
5. 下一轮建议（仅建议，不自动开始）。

**Windows 验证完成的判定**：方案 §8 的五项条件全部满足且所有必需项无 `FAIL/BLOCKED/PENDING`
（Windows 10 的 `RECORD` 按现有兼容政策保留）。未满足时明确写“Linux 开发验证已完成；
Windows 实机验证待完成/存在失败项”，列出责任项与复测条件，**不得**声称 Windows 功能或正式发行已通过验证。

**现在只执行 W00。完成材料与身份核对、Windows 原生完整门禁、正式打包与至少一种包的隔离安装，
（如时间不足则按 §二.5 登记 W00 拆分子阶段）后结束本轮，等待用户下一条执行指令。**
