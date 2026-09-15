# D18 生产互通（C# Core ↔ 真实 Chromium 接收端）

本目录只有两件东西：**夹具准备脚本**与**Node 驱动**。互通用例本身在
`tests/DshLauncher.Core.Tests/Interop/`（C#），由它们 spawn 本驱动。

## 为什么要这一层

任务卡要求"只替代 WebView2 的物理消息边界，绝不替代发送算法"。因此：

| 环节 | 谁负责 | 是不是生产代码 |
| --- | --- | --- |
| 分块、2 块在途背压、seq/offset、SHA-256、批次状态机、去重缓存 | `src/DshLauncher.Core/Attachments/AttachmentTransferCoordinator.cs` | ✅ 生产 |
| 线协议编解码与状态机校验 | `src/DshLauncher.Core/Attachments/AttachmentCodec.cs`（出站帧先喂 D10 镜像状态机） | ✅ 生产 |
| 帧的物理搬运（WebView2 WebMessage → stdio） | `d18-interop-driver.mjs` + 驱动 stdio | ❌ 仅此一处替代 |
| 浏览器分块组装、哈希校验、组装 `File` | 页面全局 `__DSH_ATTACHMENTS_RECEIVER__`（D12，已构建产物） | ✅ 生产 |
| 草稿导入（原生 `attachmentIds`） | 页面全局 `__DSH_ATTACHMENTS_BRIDGE__` → D08 草稿适配器 | ✅ 生产 |
| 字节源（Windows 上是不透明暂存句柄） | Linux 用例注入的只读文件源（同一 `IAttachmentByteSource` 契约） | ❌ 平台替身 |
| context 握手投递 | 边界（真实承载层在 hello/capabilities 之后回执），帧仍由生产 codec 校验 | ❌ 平台替身 |

驱动**不**生成、不改写、不补发任何业务帧。唯一例外是显式开启的负向探针
`--probe wrong-batch-id`：它克隆真实 `import-result` 并把 `batchId` 换成不属于本批次的值、
把 `attachmentIds` 换成可识别的假 ID，用来证明 C# 生产协调器会拒绝它。

## 工具依赖（缺任何一项都必须**响亮失败**，不许跳过）

| 依赖 | 说明 |
| --- | --- |
| Node 22+ | 内置 `fetch` / `WebSocket`；`tests/fixtures/cdp.mjs` 直连 CDP，不引 Playwright |
| `dsh` CLI | 默认 `~/.npm-global/bin/dsh`，版本必须等于 `tests/fixtures/compatibility-lock.json` 的 `harness.version` |
| 真实 Chromium | `cdp.mjs` 解析顺序：`DSH_ATTACH_CHROME` → `/usr/bin/google-chrome` → `/usr/bin/chromium` |
| pnpm | `ensure-fixture.sh` 内部构建/打包（冻结依赖） |
| 夹具 | 私有 `DSH_HOME` + `dsh-attachments-fixture`/`dsh-attachments-headless` 两个 profile |

Windows 上跑同一批用例还需要：真实 Edge WebView2 运行时（D18 只替代它的物理边界）、
Windows 侧字节源端口（`WindowsStagedByteSource`）、pwsh 7 与 `eng/verify.ps1` 正式门禁环境。
详见 `eng/verification-profiles.json` 的 `productionInterop.windowsRequirements`。

## stdio 协议（NDJSON，一行一条）

C# → 驱动（stdin）：**就是生产 C# codec 编码出的报文 JSON 文本本身**（一行一帧）。

驱动 → C#（stdout）：

```jsonc
{"kind":"page","sessionId":"…","receiverVersion":1,"wiring":{…},"hashBackend":"pure-js"}  // 恰好一条，最先
{"kind":"frame","frame":{…}}       // 生产接收端产出的线协议帧（ack / import-result / cancel）
{"kind":"reject","type":"file-end","code":"hash-mismatch","detail":"…"}                     // 接收端拒绝了入站帧
{"kind":"done","exitReason":"stdin-eof","trace":"…","traceEntries":42}                      // 退出前最后一条
```

人类可读日志一律走 stderr，stdout 只承载上述记录。stdin EOF = 优雅退出。

双向 trace：`--trace <path>` 的 JSONL 按发生顺序**同步**追加（进程被强杀也保留已写部分），
每条含 `dir`（`c2d` = C#→驱动 / `d2c` = 驱动→C#）、`kind`、报文类型、`batchId`/`fileId`、
载荷摘要、以及（`assembled`）页面回读字节的 Node 侧 SHA-256。C# 侧另有自己的
`*.csharp-transcript.jsonl`，两边互为对照。

## 用法

```bash
# 幂等准备夹具（按需 build + test:pack + down.sh + setup.sh）
bash tests/interop/ensure-fixture.sh          # --force 无条件重建；--check 只检查

# 驱动需要夹具服务已就绪（由 Core.Tests 的互通夹具启动）
node tests/interop/d18-interop-driver.mjs \
  --loopback-base http://127.0.0.1:3099 \
  --lan-base http://<lan-ip>:3099 \
  --trace /tmp/d18.trace.jsonl \
  --handshake-only        # 只验证会话/配对/页面/接收端就绪，不进入帧循环
```

真实入口是 Development 门禁：

```bash
pwsh eng/verify-portable.ps1 -Profile Development     # 检查 production-interop-l13
```
