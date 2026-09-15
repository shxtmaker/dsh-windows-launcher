# 兼容性与版本策略（附加插件本地文档）

本文件属于**本插件自己的文档**（`plugins/dsh-remote-attachments/docs/`），记录固定的上游组合、
实测安装结果、能力矩阵，以及"全家桶更新 / 本插件独立更新"的边界。

- 唯一真值：`tests/fixtures/compatibility-lock.json`（固定版本与 integrity）。
- 实测证据：`artifacts/verify-portable/d23-compat-gates.json`（判据）与
  `artifacts/verify-portable/d23-compatibility-matrix.json`（机器可读矩阵），
  历史运行在 `artifacts/verify-portable/d23-runs/<runId>/`。
- 复跑命令：`pnpm --dir plugins/dsh-remote-attachments run fixture:compat`
  （前置：`fixture:setup` 已创建夹具私有 `DSH_HOME`）。

## 1. 固定（pinned）与实测（observed）

| 组件 | 固定值（compatibility-lock.json） | 实测安装结果 |
| --- | --- | --- |
| Harness `@deepseek-ai/dsh` | `0.1.5-rc.1` | `dsh --version` = `0.1.5-rc.1` |
| 全家桶 `@linxin666/dsh-web-all` | `0.3.20` | 夹具 profile 内 `0.3.20` |
| `@linxin666/dsh-remote-web-ui` | `0.3.20`（由全家桶依赖引入，不单独安装） | 夹具 profile 内 `0.3.20`，组合配置里提供者恰好一份 |
| 本插件 tarball | `0.1.0`（D04 记录的 tarballSha256 `5d4016be…`） | 当前构建 tarball `0.1.0`，sha256 `6bb04fe508674bcd4bc9ee1bda668901512db395e39da6ceff53ff78a149dc39`，integrity `sha512-1f5SIxJCc5lHwj8EJxivb2XZSQPRfFwWNSUZ0RgxOQ11TfJVYdGzZ7dHPfi8vyOkEZbup3ZSBDRp/3N9fzVNnA==` |
| 插件声明的 Harness 引擎范围 | `package.json` → `dsh.engines.dsh: ">=0.1.5-rc.1"` | 运行时由 Harness 自己的加载器解释；本插件不做第二套版本判定 |

夹具 profile 的 bundle 列表（顺序固定）：
`@deepseek-ai/dsh-base`、`@deepseek-ai/dsh-web-app`、`@linxin666/dsh-web-all`、
`@shxtmaker/dsh-remote-attachments`；本插件在自己的 `cordis.patch.yml` 里只插入一行
（`id: remote-attachments`），不修改全家桶的聚合子插件清单。

**未采用**研究快照的 `0.3.21` / `0.1.5-rc.2`，也不跟随 npm `latest`（决策记录见
`compatibility-lock.json` 的 `selection`）；`0.3.22` 同样未验证。

## 2. 能力矩阵（行 = 组合，列 = 结论）

| 组合 | 能力状态 | 原因码 | 配对/心跳 | 附件行为 |
| --- | --- | --- | --- | --- |
| 已验证组合（remote 桌面 / LAN 已配对页） | `available` | 无 | 不受影响 | 走本插件承载 → `/remote` 门控上传 |
| 已存在未知 `__DSH_FILE_UPLOAD__` | `unavailable` | `capability-conflict` | 不受影响 | **保留原功能**：未知 hook 原样不覆盖，本插件不参与 |
| remote 降级（`requirePairingForLan: false`，客户端改写层未安装） | `unavailable` | `capability-disabled` | 不受影响 | 承载就地拒绝，**零请求**，不回退裸 `/api`；页面回到内置行为 |
| 不支持组合（上游内部契约与固定版本不符，例如 boot seat 被改名） | `unavailable` | `capability-disabled` | 不受影响 | fail closed：承载拒绝、零请求；改写层若仍工作则内置路径照常 |
| 本插件被独立停用（`enabled: false`） | LAN 页 `unavailable` / loopback 页 `degraded` | `capability-disabled` | 不受影响 | host 不再注入承载，页面回到**未修改的内置行为**（Harness 自己的上传传输） |

判定顺序（`src/shared/capabilities.ts`，同一组事实必然同一结论）：
未知 hook → remote 通道不可用 → 局域网页面上承载缺失 → 缺必需依赖 → 显式禁用 → `available`。
因此 `enabled: false` 在 LAN 页面上会被"承载缺失"先判成 `unavailable`，在 loopback 页面上才落到
`degraded`；两种情况的原因码都是 `capability-disabled`，能力都不会假装可用。

## 3. 独立停用（disable）的语义与范围

- 停用开关是插件自己的配置 `enabled: false`（host 与 client 半区同名）。host 半区不再注册
  上传承载注入，因此页面 **boot 时** `__DSH_FILE_UPLOAD__` 就不存在（不是"用得晚"）。
- **禁用状态要由 host 半区发布到页面**：cordis 的浏览器半区加载器是 `loader.create({ name })`，
  **不带行配置**，所以 client 半区收不到 `enabled: false`。因此 host 在禁用时仍会注入一条
  只写 `__DSH_ATTACHMENTS_STATUS__.host.enabled = false` 的标记脚本（不安装承载、不碰
  `__DSH_FILE_UPLOAD__`、不碰 remote seat），client 半区据此把能力判成 `degraded`
  （LAN 页面上因为承载缺席仍是 `unavailable`），桥对导入一律拒绝。
  *没有这条标记时（修复前）*，被禁用的插件在 loopback 页面上会把自己报成 `available` 并且
  照旧把文件塞进草稿——即"禁用"在本地页面上是失效的（D23 实测并已修复）。
- 停用只作用于本插件行。用一次性覆盖层停用的组合配置与未停用时**逐行对比**，差异只有本插件的
  `config: enabled: false`（外加 patch 来源注释）；profile 的 `package.json` 与 `pnpm-lock.yaml`
  逐字节不变——停用不需要、也没有动全家桶。
- **停用不调用 `PairingService.stop`**：`stop()` 的语义是"撤销所有设备会话并清空令牌"
  （`@linxin666/dsh-remote-web-ui` 自带注释）。判据用两路观察：夹具私有副本里对 `stop()` 的调用
  记录恒为 0，且设备库集合（数量 + 集合哈希）、心跳 200、`/remote` 读会话 200 全部不变；
  负向控制会真的调用一次 `stop()` 来证明这些断言**会**失败。
- 配对的**设备凭据**只由全家桶 remote 提供，本插件从不读取、替换或撤销它；本插件也没有任何
  代码引用 `PairingService`。
- **回退到内置行为**用同源对照证明：把本插件整行 `disabled: true`（页面上连 client 半区都不加载）
  得到"原厂基线"页，与 `enabled: false` 页逐项比较——真实文件都能经 composer 原生入口进入
  原生草稿，上传腿结果一致。本夹具的加固 LAN 部署下，内置后台上传走裸 `/api`，被 Harness 的
  浏览器凭据/配对栅栏拒绝（这正是本插件承载存在的理由）；loopback 上内置上传照常成功并落进
  主机内容寻址库（逐字节一致）。

## 4. `ReloadRequired`：恢复全局变量 ≠ 恢复 runtime

Harness 的 `FileUploadRuntime` 在 **boot 阶段**就读走了 `__DSH_FILE_UPLOAD__.fetch`
（固定版本契约，见 `src/host/upload-hook.ts` 顶部注释）。因此：

- 运行时停用后，即使把全局变量原样放回去（先只放承载、再放回全部四个全局），
  状态面仍必须报告 `reload-required`：`__DSH_ATTACHMENTS_ADDON__.status().reloadRequired.required === true`，
  上传路由为 `disposed`，保留引用的桥仍以 `capability-disabled` 拒绝导入。
  **不得**因为"全局变量回来了"就宣称 runtime 已恢复。
- 同理，**撤销后的能力判定本身也不得报 `available`**：页面事实（承载、改写层、宿主服务）可能
  看起来完全健康，但这个实例的桥与接收端都已经进入拒绝态。`AddonHandle.capability()` 与
  `status().capability` 在撤销后固定为 `unavailable` + `capability-disabled`（原因里带
  `reload-required`），`facts` 仍如实回带现场事实。*修复前*这里会报 `available`——只恢复全局
  变量的页面上，能力字段会给出"已恢复"的假象（D23 实测并已修复，单测覆盖）。
- 真正的恢复路径是**页面重载**：重载后 addon 重新装配、能力回到 `available`、
  `reloadRequired.required === false`，并且真实分块批次能再次拿到原生 `attachmentIds`、
  上传 200、主机内容寻址库里逐字节一致。
- Harness **重启**（同一 profile、同一 `DSH_HOME`）后：设备凭据仍在（心跳 200、`/remote` 读会话
  200、设备集合不变），页面重载后附件通路照常。
- **重载路径的实测边界（上游，不是本插件）**：在 LAN 页面上，文档的**裸 reload** 会被 Harness 的
  浏览器凭据层以 `401 dsh web authentication required` 拒绝——同一页面上不经本插件任何代码的
  "干净对照"页裸 reload 行为完全一致，因此与本插件的停用/撤销无关。受支持的再入路径是配对页
  入口（`/pair-app`，手机上即"重新打开配对链接/由配对页的 service worker 服务导航"）；
  loopback 页面上浏览器凭据由启动 URL 的 token 交给浏览器，因此那里的 `Page.reload`
  是货真价实的文档重载（判据 G07b 就是在这条路径上证明"重载确实能恢复"）。

## 5. 更新边界（本轮不对任何来源执行更新）

**用户主动更新全家桶（`all`）** 预期行为：只会改写 profile 里 `@linxin666/dsh-web-all` 的解析
（及其依赖），**不会**改写本插件的依赖条目、tarball 路径与 `cordis.patch.yml` 行 id；反之亦然。
更新后必须重新按本文第 2 节核对：在新组合被核对通过之前，一律按"不支持组合"处理——
本插件会 fail closed（`unavailable` + `capability-disabled`、承载拒绝、不回退裸 `/api`），
而不是静默降级成另一条上传路径。实际执行一次 `all` 更新并复跑矩阵属 **notRun**
（不得对生产 `$HOME/.dsh` 执行更新；夹具固定 `0.3.20` 且不引入未验证版本）。

**独立 scope 的插件更新边界**：`dsh plugin --profile <name> <args…>` 把参数转发给 profile 目录里的
pnpm，因此 `dsh plugin --profile web add/update @shxtmaker/dsh-remote-attachments` 只影响这一个依赖；
它不会动全家桶。对生产 profile 执行该更新同样属 notRun（需要用户另行授权）。

**本地开发构建**：改动插件源码后必须 `pnpm run build && pnpm run test:pack` 再用
`tests/fixtures/install-plugin.sh` 装进夹具（pnpm 按 tarball 路径缓存 `file:` 依赖的 integrity，
同名路径不会自动重新解析，否则夹具会继续跑旧 `lib/`）。

## 6. 未验（不得当作通过）

- **WindowsPending**：Windows 实机上的 MSI/zip 安装、真实 WebView2 承载、Launcher 更新流程。
- 真实 npm 源上的 `all` 更新：`notRun`（见上）。
- `0.3.21` / `0.3.22` + Harness `0.1.5-rc.2` 组合的真实行为：`notRun`（未安装）。
- 客户端市场/插件管理器 UI 触发的更新路径：`notRun`（夹具面向本地 stub，不含市场入口）。
