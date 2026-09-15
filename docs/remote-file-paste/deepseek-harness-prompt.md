# 用于 DeepSeek Harness 的分轮代码迭代提示词

方案版本：2.0。先将整个 docs/remote-file-paste/ 目录放入目标仓库，保留 tasks、execution、task-graph.json 和所有专项文档。复制以下分隔线之后的完整正文作为首条实施提示词；首轮结束后，每次发送“继续”只推进一个任务。技术方案文件必须随提示词可读，单独发送此提示词不能替代方案。

---

请按照仓库 docs/remote-file-paste/ 下的 2.0 版方案，对 https://github.com/shxtmaker/dsh-windows-launcher 实施代码迭代。最终要交付真实代码、测试、插件包和验证报告，但必须跨多轮完成。始终用中文说明，代码、命令和报错保持原样。

最优先的执行节奏如下：

1. **首次执行只完成 D00。** 核实资料、git 状态、上游版本、工具链和已有变更，建立任务台账及 R00-D00 交接记录。允许写计划与记录，不修改业务代码，不开始 D01。完成 D00 后立即结束本轮。
2. **后续每条用户明确执行指令，最多执行一个依赖已满足的 DEV 叶任务。** “继续”默认恢复上轮 in_progress/partial 任务；若上轮 blocked 且阻断尚未解除，可在本次新轮选择另一个依赖已满足的最小编号独立任务，保留原阻断；无未完成项时同样选择就绪任务。用户指定任务且前置未满足时说明依赖，不能擅自改做其他任务。
3. 当前任务完成、需要拆分、出现具体阻断或本轮被中断时，保存成果和检查点后结束。不要自行开始下一任务，不要把 D00–D25 合并执行，不要在同轮并行开发不同任务。
4. 工具返回、模型自动续写、上下文压缩、子代理完成或定时事件都不是新的用户执行轮。用户只询问进度时回答问题，不因此启动下一任务。不得用自动任务或后台代理绕过分轮限制。
5. 当前任务过大时，本轮只登记子任务、依赖和验收，父任务标记 split，结束后等新的执行指令再做子任务。不得一次执行所有子任务。任务可以跨多轮，不可删减测试凑出“单轮完成”。
6. 子代理只能承担当前任务内的工作。本轮结束前收拢结果、停止会继续写入的后台工作并持久化记录。实际完成当前任务后立即向用户返回本轮结果，无需重复询问是否允许执行当前已授权任务。
7. 全部 DEV 任务结束后报告 DevelopmentReady 与 WindowsPending 并停止。普通“继续”不得自动启动 Windows 实机验收、安装、发布或推送；这些需要用户明确提出相应工作。

先阅读 AGENTS.md、CONTEXT.md、构建规则及以下文档：

- docs/remote-file-paste/implementation-plan.md：技术基线。
- docs/remote-file-paste/task-index.md 与 task-graph.json：26 个初始任务及依赖。
- docs/remote-file-paste/execution-workflow.md：调度、拆分、恢复和证据规则。
- docs/remote-file-paste/execution/state.json 与 execution/handoff.md：唯一动态状态及最近交接。
- 当前选中任务卡，以及 portable-validation-plan.md、windows-validation-plan.md 中相关验收。

本目录取代此前 .scratch 内不同部署形态及 P0–P4 的旧方案。若附件提供了文档，先保存到规定目录；缺失时明确报告缺件，不声称已经读过，也不自行换一套技术路线。已有执行状态时必须恢复，不能重置为 todo 或重新运行 D00 覆盖成果。保护用户已有改动，在合适的本地开发分支实施；不做破坏性清理。

目标环境固定为 **DeepSeek Harness＋@linxin666/dsh-web-all 全家桶＋一个新增独立附件插件**。全家桶中的 dsh-remote-web-ui 是唯一配对、设备凭据、心跳和 /remote 代理提供者。不要再装第二份 remote-web-ui，不重打全家桶、不复制设备库、不改成原生 Harness 加独立远程插件。

功能目标是：Windows Launcher 远程会话中，资源管理器复制的普通文件、多文件及截图，粘贴为当前可编辑主会话的待发送附件；纯文本保持原行为，用户主动发送前不得自动发送。有效 sessionId 的新建会话支持；没有 sessionId 的首页提示先建立会话再粘贴，不猜归属、不隐式创建会话或延后自动投递。首期不做目录、UNC、Shell 虚拟文件、云盘自动下载、自动解压或断点续传。

研究快照为 Launcher df68795c3f7a0a34d6ed4fdd7804e369bdd0258b、dsh-web 0d78d391b5f67ec4d9f04d47558b76352c74ee48（all/remote 0.3.21）、Harness c291e7961a515f6d7af9304e7fd1d257929aef26（0.1.5-rc.2）。它们不是当前部署保证。D00 核实实际版本，后续固定 lock、integrity 和测试 profile，不使用浮动 latest，不操作生产配置和真实用户凭据。

以下是跨任务必须遵守的技术边界，不是要求本轮把这些工作全部做完：

- 插件位于 plugins/dsh-remote-attachments/，具有独立包身份。先完成 D03–D08 所需的最小组件，D09 才验证真实 Linux Harness＋全家桶的最小闭环；成功路径不能只用自制服务器或浏览器测试页。
- host 通过 webserver/index-inject 在 FileUploadRuntime 捕获前安装 __DSH_FILE_UPLOAD__。上传时动态调用当前 window.fetch，使用 new Headers(init?.headers)，保留原始同源 /api/session/uploadFileBinary，由原 remote 只改写一次并注入设备头。保留 signal，解析 HTTP 200 的业务失败；不捕获裸 fetch、不预拼 /remote、不覆盖未知 hook。停用后需要重载才能重新选择 runtime 时明确提示。
- 草稿适配器使用 session-scoped overlay 锚点，定位所属 data-composer-card 内唯一文件 input，通过 DataTransfer 写入 files 并只触发一次 change。读取公开 session input 状态，在同一同步任务中确认旧附件 ID 保留、新增 ID 数量及顺序对应本次导入、session/composer epoch 不变。检查 phase、blocks、编辑权限与子代理限制。不拿事件返回值、卡片外观或无关异步新增 ID 当 ACK，不重放已导入文件。
- 不抢占 single attachments slot、不读取 React 私有字段、不调用不存在的公开 createDrafts、不用 Playwright setInputFiles 绕过生产 adapter。入口失败时先证明兼容缺口和最小补丁；尽量不改 Harness 核心，不因 Unsupported 就宣称功能完成。
- 首期使用 256 KiB 原始块、最多两块在途的 WebMessage 通路，Base64 仅编码单块。协议明确 operationId=batchId、独立 fileId、逐文件 seq、file-end/batch-end，以及接收 ACK、草稿 staged、上传 ready 和主动发送的不同含义。部分成功保留成功项，用户明确重试失败项时使用新操作标识，不自动重导入。
- 默认限额及 SHA-256 按技术方案执行。HTTP LAN 下 WebCrypto 不可用时使用锁定的纯 JS 实现，不关闭校验。完整 File/Blob 内存、暂存、重复消息缓存和取消后资源都要有上界。
- Windows 只做局部重构：Core 负责生产状态机、协议、背压、取消和上下文；Windows 保留 STA/CF_HDROP、真实原生手势、PNG、受控暂存、WebView2 与页面生命周期。原生文件读取不能由网页任意路径或 isTrusted 字段授权。截图原生入口与桥兜底只能有一个消费者。
- 普通上传错误复用原 draft 的 retryFileUpload；重启造成旧 ready receipt 失效时提示移除再导入，不自动发送消息。取消不删除 Harness 共享内容；撤销后拒绝新请求不等于主动中断已接受的上传。

开发执行和项目开发验证必须不依赖 Windows。Core/Core.Tests 改为 net10.0 且无 win-x64 RID，另外四个 C# 项目保留 Windows 目标。Windows 原生适配代码必须写完整，不能用缺少实机为由交付空实现。Linux 验证不得依赖 Wine、Windows runner 或 Windows 预构建产物。

逐任务扩展 verify-portable.ps1 与测试集合。Core profile 早期只证明 Core 阶段；最终 Development profile 必须覆盖方案要求的全部真实检查。保留 SDK 10.0.400、MTP 可执行 runner、固定用例集合、格式检查和原正式 Windows 门禁，不能盲目用 dotnet test、删除旧用例、关闭 failSkips 或自动接受新基线。

D18 必须让真实 C# 生产协调器通过测试通道把实际分块发送给 Chromium 内的生产接收器，ACK 返回同一 Core，再进入真实 Harness；只替代 WebView2 物理消息边界，不能用 JS 假 sender 代替 C#。无付费模型账号时采用确定性 provider stub，由真实 Harness 工具执行读取并核对内容；图像验证真实请求载荷。

完整 Linux 交叉构建必须尝试；工具链支持时必过，普通 C#/XAML/引用错误必须修复。只有最小案例证明 SDK/任务平台不支持时才记 toolchainUnsupported，并交接 Windows 编译，不能把网络、磁盘或包恢复失败改名豁免。HTTP 非 loopback、可信 HTTPS、真实全家桶、故障注入及最终 tarball 的隔离安装均按任务逐步验证。

每轮结束前，更新 execution/state.json，写 execution/rounds/Rxx-Dyy.md，更新 execution/handoff.md。记录实际变更、命令、平台、退出码、用例数、证据路径和候选内容摘要；中断或未执行不能写成通过。相关代码变化使旧证据失效时标记 stale，并登记复验，不沿用旧候选结果宣称新候选通过。

每轮最终答复只需包含：

1. 本轮任务 ID、名称与状态。
2. 完成内容和关键变更。
3. 实际验证、未执行项及其原因。
4. 台账、轮次记录与交接文件路径。
5. 下一轮建议，仅作为建议，不自动开始。

D24 的完整 Linux 必需门禁和 D25 交接真实完成后，才可报告 DevelopmentReady；Windows 未验单独报告 WindowsPending，不能宣称所有测试通过或 ReleaseReady。未经后续明确授权，不推送远端、不发布 npm 或 Release、不替换生产插件、不升级正式版本。

**现在只执行 D00。完成首轮基线核实、台账和交接后结束，等待用户下一条执行指令。**
