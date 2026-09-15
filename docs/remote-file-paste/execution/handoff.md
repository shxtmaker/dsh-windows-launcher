# 开发执行交接（D25 冻结）

方案版本：2.0。状态唯一真值见 [state.json](state.json)，实测基线见 [baseline.json](baseline.json)。

> **本文件在 D25 冻结**：DEV 轨道已结束调度（D00–D25 全部 done）。后续工作属 **Windows 实机轨道**
> （[windows-runbook.md](windows-runbook.md) + [windows-task-list.md](windows-task-list.md)），
> 需要用户明确指令才启动。任何实机缺陷修复另登记代码任务，并复验受影响的 Linux 与 Windows 项。

## 1. 当前状态

- 任务进度：**DEV 叶任务 D00–D24 全部 done（25 项）** + R15 修复轮 done + **D25（本任务）done**。
  **无未完成 DEV 任务**；无 `partial`/`blocked`。
- 项目状态：**`developmentStatus=DevelopmentReady`**（依据 [implementation-plan §7](../implementation-plan.md)、
  [windows-validation-plan §1](../windows-validation-plan.md)：全部 DEV 叶任务完成且证据有效 + D24 完整 Linux 门禁真实通过 +
  D25 源码/tarball/实机交接完整，**不需要 Windows 在线**）；
  `windowsStatus=pending`；`releaseEligible=false`（恒定）。
- 最近轮次：R29 / 发布 v2.1.0，结果 **done**，记录见 [rounds/R29-release-v2.1.0.md](rounds/R29-release-v2.1.0.md)；
  上一轮 R28 / D25 见 [rounds/R28-D25.md](rounds/R28-D25.md)。
- **✅ 已发布 v2.1.0（prerelease）**：分支 `main` 提交 `8e6ae61`（功能）+ `676f6a0`（Windows 交接材料补全），
  附注标签 `v2.1.0`（`f654e8f4…` → `676f6a0`）已推送 GitHub 与 Gitea；
  两平台 release `DSH Windows Launcher 2.1.0`（id `389136149` / `30`）各上传 7 个资产
  （附件插件 tarball、源码 tar.gz/zip、`SHA256SUMS.txt`、Linux 门禁证据、交接清单）。
- ⚠️ **安装包未产出**：`Setup-2.1.0-win-x64.exe` 需要 Windows + Inno Setup 7.0.2 + WebView2 输入与真实
  安装/卸载验证（`eng/package.ps1`），构建机为 Linux，故未构建、未上传；release 因此标记 prerelease，
  安装包由 Windows 轨道（[windows-runbook.md](windows-runbook.md) W00/W05）产出后另行附加。
- Linux 权威轮（runId `d24-2026-09-15T12-16-27-474Z-ed8d444f`，发布前同候选轮）：Development **32/32 checks pass、
  incomplete 0、fail 0**、`portableStatus=pass`、`crossBuildStatus=pass`（**独立判定**）、cases 546/546、
  `harnessIntegration=pass`、`productionInterop=pass`、`windowsValidation=notRun`、`releaseEligible=false`。摘要：
  `artifacts/verify-portable/portable-verify-summary.json`。（D25 轮为 `d24-2026-09-15T11-15-43-616Z-0a0f006c`，同样 32/32。）
- **口径纪律（务必遵守）**：`portableStatus=pass` **不等于**"所有测试通过"，更**不等于可发布**；
  `portable-verify-summary.json` 的 `developmentReady=false` 是 **profile 配置**（两端 profile 的
  `producesDevelopmentReady` 均为 false），与项目级 `DevelopmentReady` 是**两个口径**——项目级状态由 D25 按方案记账；
  正式发布门禁摘要只能是 Windows `eng/verify.ps1` 产出的 `verify-summary.json`。
- Windows：全部实机/打包/安装/发布事实 `WindowsPending`，**不计入任何通过**；`notRun` 合计 20 条
  （WindowsPending 13 + 显式白名单受限 7），`unjustified=0`。

## 2. 本轮候选摘要（已发布）

- 仓库 `https://github.com/shxtmaker/dsh-windows-launcher.git`，分支 **`main`**（`feat/remote-file-paste-attachments` 同内容）。
- 发布提交：**`8e6ae616c5bbacaab8cf458584d855271e127ae4`**（`feat: remote file paste attachments (2.1.0)`）
  + **`676f6a0e6d69a84c7c430a4952cfa5c5c5eea2f8`**（`docs: complete Windows handoff materials`，即发布前门禁轮里的 dirty 1）。
- 标签：**`v2.1.0`**（附注标签对象 `f654e8f4eb7ca854dca3053095a132647b5b5732`），已推送 GitHub 与 Gitea。
- 发布前门禁轮的候选身份：gitHead `8e6ae61`、worktree dirty 1（porcelain `fbecd7bba4f885314adf82bc44f9b4717628f12db758841072d21271b93dbb5f`，唯一变更 = runbook §10 =
  提交 `676f6a0`）⇒ 证据与 release 提交内容一致（仅文档差异）。
- 插件 `@shxtmaker/dsh-remote-attachments@0.1.0`；`src` 树 18 文件 `sha256=c4597fe4e012c76571c8cde8211b23b0aef6698b8ffc366d4c8d8ae78b0dfbbe`。
- tarball `plugins/dsh-remote-attachments/pack/shxtmaker-dsh-remote-attachments-0.1.0.tgz`：
  165 785 B / 59 文件，`sha256=64a359de44fd12185297ba8939268208246e61981bf149a1efd0591dfc3b996b`。
- 上游：Harness CLI `0.1.5-rc.1` + 内部 UI 包 `0.1.5-rc.2` + `@linxin666/dsh-web-all` / `dsh-remote-web-ui` `0.3.20`。
- 机器可读绑定清单：`artifacts/verify-portable/d25-handoff-manifest.json`（含 `release` 段）。

## 3. D25 交付件

| 交付件 | 路径 |
| --- | --- |
| 交接清单（候选/tarball/锁/证据/待验绑定） | `artifacts/verify-portable/d25-handoff-manifest.json` |
| 插件候选 tarball + manifest | `plugins/dsh-remote-attachments/pack/*.tgz`、`artifacts/verify-portable/d22-package-manifest.json` |
| Windows 运行手册 | [windows-runbook.md](windows-runbook.md) |
| Windows 侧分轮验收提示词 | [../windows-verification-prompt.md](../windows-verification-prompt.md) |
| Windows 任务列表与待验清单 | [windows-task-list.md](windows-task-list.md) |
| 交付报告（D25 冻结，含 Windows 轮次必须先看的问题） | [delivery-report.md](delivery-report.md) |
| Linux 完整报告 | `artifacts/verify-portable/d24-linux-report.json`、`artifacts/verify-portable/d24-runs/` |
| 2.1.0 发布说明 | [../../release-notes/v2.1.0.md](../../release-notes/v2.1.0.md) |
| 逐轮记录 | [rounds/](rounds/)（R00–R29，含 [R29 发布记录](rounds/R29-release-v2.1.0.md)） |

## 4. D25 本轮独立复核（父代理亲测）

1. **候选身份重绑**：独立重算工作树 porcelain 摘要与插件 `src` 逐文件指纹，与 D24 报告 `candidate` 逐字相同
   ⇒ D24 证据对当前工作树仍有效（候选未变）。
2. **tarball 重绑**：`pnpm run build` 后逐文件比对 tarball 解包内容 —— **59/59 字节相同**，sha256 与 D22 清单一致。
3. **门禁声明读码复核**：`Resolve-DshPortableStatus` 参数表无 `CrossBuildStatus`（结构性独立）；
   `Resolve-DshCrossBuildStatus` 只读 `cross-build-l12` 自身状态；`$summaryWritten` 在正式摘要落盘后置真，
   `catch` 仅在未落盘时兜底。
4. **纯函数负向控制 18/18**：从 `eng/verify-portable.ps1:76-148` 逐字提取三个纯函数，变异输入实测
   （零用例/跳过/未验/失败/计数不符/独立性/`toolchainUnsupported`/WindowsPending 不误报）。
5. **摘要覆盖缺陷端到端证伪**：脚本副本注入失败——保留守卫时正式摘要**未被覆盖**（`requiredCases=542`、
   保留 candidate、无 `failure` 字段）；**关闭守卫的对照确实被覆盖**（`requiredCases=0`、无 candidate、有 `failure` 字段）。
6. **Core profile 复跑**（上述实验副产物）：**7/7 pass**，Core 用例 **542/542**（failed/skipped/notRun 均 0）。
7. **状态审计**：D00–D24 全 done；**没有任何 Windows 平台证据被记 `pass`**；生产源码无 `TODO/FIXME`、无 stub 抛壳；
   证据路径仅 1 个临时夹具目录缺失（`artifacts/fixture/provider/record/`，夹具按设计清理）。

## 5. 恢复入口与注意事项（Windows 轨道）

1. 本轮**没有**遗留后台门禁或夹具进程（D25 只跑了 Core profile 与两个脚本副本，副本与 `/tmp` 目录已删）。
   恢复前仍先核对：`git status --porcelain | wc -l` 应为 **37**；`pgrep -af dsh` 不应出现夹具 profile。
2. Windows 轨道启动条件：**用户明确的实机验证指令** + Windows 测试机就绪 + 按 [windows-runbook.md](windows-runbook.md) 第 0/1 节核对候选身份。
3. W00 先处理两个已知问题（见 [delivery-report.md](delivery-report.md) §7）：
   ① `Platform.Windows.Tests` 反斜杠理论用例基线转义；② 插件包摘要一律以 D25 交接清单的 tarball.sha256 为准。
4. 正式 Windows 门禁：`pwsh -File .\eng\verify.ps1 -DotNetPath <SDK 10.0.400>`；打包/安装模板见 `eng/README.md` 与 Windows 方案 §7.1。
5. **不自动**推送、打标签、发布、npm 发布或启动 Windows 阶段；普通"继续"不会启动这些工作。
6. 本机工具链脚本 `/tmp/dsh-env.sh` 在 `/tmp`，重启后可能丢失：恢复时先核对或重建
   （`DOTNET_ROOT=$HOME/.dotnet` 并加入 PATH；`pwsh` 在 `$HOME/tools/pwsh`）。

## 6. 已核实的 Linux 环境事实

- Ubuntu 26.04 LTS（内核 7.0.0-30-generic，x86_64，RID linux-x64）。
- .NET SDK 10.0.400（`$HOME/.dotnet`）；无 `Microsoft.WindowsDesktop.App`；无已安装工作负载。
- Node v22.23.2、npm 10.9.8、pnpm 11.7.0、PowerShell 7.5.4、git 2.53.0、python3 3.14.4。
- 浏览器：Chrome 151.0.7922.173；`$HOME/.cache/ms-playwright/chromium-1243`。
- 完整 solution 在 Linux **交叉构建成功**（六个项目，0 警告 0 错误）；L12 = **pass**（不是 `toolchainUnsupported`）。
- 两个 Windows 测试程序集在 Linux **无有效通过证据**：`Platform.Windows.Tests` 可直接加载但真实 Win32 用例失败，
  经 `--runtime win-x64` 调用缺 `Microsoft.WindowsDesktop.App`；`Acceptance.Tests` 无法启动（缺框架，退出码 150）。
  两者均记 `notRun`，属 WindowsPending，**不得**据此宣称 Windows 已通过。

## 7. 实测事实与陷阱（历轮保留，Windows 轨道同样适用）

### 7.1 协议与产品语义

- **三态语义（验收点）**：`transport{idle,buffering,buffered}` / `draft{none,staged,failed,partial}` /
  `upload{none,harness-owned}`——枚举里**没有** `ready`/`uploaded`。`staged` 是线协议 `import-result` 的结果，
  只表示对端草稿已接收字节并给出 `attachmentIds`；`ready` 是 **Harness 上传层**状态。
  **`staged` 不保证 receipt 仍有效**（D19 实测：芯片停在 ready 仍被 `FILE_NOT_STAGED` 拒）。
- **取消语义（R15 修复）**：`cancel` 后同会话必须能开新批次；只有复用**同一** `batchId` 才是 `duplicate-operation`。
  TS 状态机、TS 接收端、C# 镜像三处一致；语料未改。
- **撤销语义**：新请求被 `/remote/api` 门控在**进 Host 前**拒掉 → 403 + `unpaired`；在途请求放行时按当时门控转发。
  客户端取消是另一条路（`client-aborted`）。**不得**把撤销说成"已通过鉴权的上传流被主动终止"。
- **上传承载是 fetch 形状**：`__DSH_FILE_UPLOAD__` 消费者读 `hook.fetch`（README: "Fetch-shaped carrier"）；
  装成裸函数会让文本附件上传必然失败（D09 抓到的真实缺陷）。
- **intake 的原校验只看声明的 media type**，不做字节级校验；`image/tiff` 等按普通文件走 verbatim 路径。
  **不要**再写"非法图片必须在 intake 被拒绝"这类判据。

### 7.2 夹具与门禁

- **受控 harness 必须绑非 loopback 地址**：remote 的 boot 脚本按设计在 loopback 上直接 return。
- **夹具必须装当前构建**：pnpm 按 tarball 路径缓存 `file:` 依赖 integrity，重新打包后同名路径不会重新解析
  （实测会继续跑旧 `lib/`）。改完插件源码必须 `build` → `test:pack` → `install-plugin.sh`。
- **夹具必须保留 `/api` fence lockdown**（`connection.trustedHosts: []`）：删掉它会让未配对 LAN 客户端可直连整套桌面 API。
- 夹具私有 `DSH_HOME` 在 `artifacts/fixture/dsh-home`，bind `0.0.0.0:3099`；**绝不触碰** `$HOME/.dsh` 与生产 profile。
  `fixture:down` 只在双重确认后停进程；门禁 `finally` 自行收拢。**夹具根目录会被 `down.sh` 删除**，需要时重跑 `setup.sh`。
- 会话按 `DSH_HOME` 共享：`<DSH_HOME>/sessions/<slug>/session-<uuid>/…`；会话选择**没有 URL 路由**，
  靠 `localStorage['dsh.sessions.current']`（须在页面脚本前写入）。
- 非 loopback 下 `/api/*` 被 fence 拦（Node 直连 403）：走配对门控的 `/remote/api/<method>` 并带 `x-dsh-remote-device`。
- 配对页限流 10 次/30 秒/源 IP；`/pair-accept` 与 `/pair-app` 共用 page 桶。
- **client 入口是构建生成的经典包裹产物**（`lib/client.js` 由 `scripts/build-client-bundle.mjs` 生成，勿手改）：
  client-modules 批次是各包 `client.js` 的**原始字节拼接**，顶层出现任何 `import`/`export` 会让**整批**解析失败。
  源入口在 `src/client/index.ts`，ESM 形态在 `lib/client/index.js`（供单测）。
- **仓库纪律门（新增 C# 测试必踩）**：`dotnet format --verify-no-changes`、`eng/expected-test-dataset.json`
  固定用例哈希清单（**只增不减**，先断言基线条目未消失）、每个用例必须有合法 `VFY-0[1-8]` 的 `triggerTags`。
- **写判据要断言上游契约（消费者实际读什么），不要断言我们自己的实现形状**；关键契约都要留可证伪的反例。
- **不稳定判据必须用可控参数复现并消除，不能靠侥幸绿灯**；采样要覆盖**完整瞬时窗口**（禁止 `sleep(固定值)` 后单点读），
  不要只用"错误关键词正则"当唯一证据面，注意 portal 到 `document.body` 的元素。
- **读任何 JSON 之前先打印它的键名/结构再取值**——父代理在 D19/D21/D22/D23 连续四次因猜字段名误报。

### 7.3 环境与进程

- 全量 Development 门禁约 **20–26 分钟**：一律后台跑 + 耐心等完；前台的 10 分钟等待会误判为"未跑完"。
- 生产 profile（`$HOME/.dsh/profiles/web`）与已安装上游包**全程未被触碰**；门禁逐轮核对指纹。
- 用户自己的 DSH 运行时（`~/.npm-global/bin/dsh`、`dsh-launcher gui`、`--profile sdk`）与 Chrome
  **不得**被本任务清理；`$HOME/.dsh` 全树粗指纹会因用户自身运行时而漂移，那不是门禁行为。

## 8. 已知问题与口径纪律（Windows 轮次必读）

1. **⚠️ `Platform.Windows.Tests` 反斜杠理论用例基线转义隐患**：4 条含反斜杠参数的理论用例，MTP 的 XML 输出把 `\`
   再转义为 `\\`，而 D14 建立的基线来自枚举 JSON 归一值 ⇒ 真实 Windows 上跑 XML 门禁会报 `missing/unexpected`。
   修它会删除既有基线哈希，故 DEV 轨道按"只增不减"规则未动；**在此之前不得声称该程序集基线在 Windows 上可用**。
2. **上游版本组合是 D00 登记的偏离**：本机 `0.3.20` + CLI `0.1.5-rc.1`（实际 UI 包 `0.1.5-rc.2`），
   研究快照为 `0.3.21` + `0.1.5-rc.2`；Harness / dsh-web 源码 commit 未核实（本机无 checkout）。
   若日后对齐快照，必须重跑 D04–D13。
3. **`Session.promptError` 口径**：公开路径存在（插件 `ctx.sessions.scope(id)` → `sessionOf(scope)` →
   `getSnapshot().promptError`）；当前黑盒测试**没有插件上下文句柄**，**不得**说成"没有公开接口"。
4. **`incomplete>0` 时增量命令 `exit 0`** 可保留，但**完整开发验收必须看 `portableStatus` 与完整性字段**，
   不能只看退出码。
5. 仓库无 `AGENTS.md`（R00 已核实）；工程规则以 `CONTEXT.md`、`eng/README.md` 与各方案文档为准。
