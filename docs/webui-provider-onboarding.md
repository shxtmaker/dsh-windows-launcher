# 第三方 WebUI 提供方接入与维护规范

文档编号：`DSHWL-WEBUI-PROVIDER`<br>
文档版本：`1.0.0`<br>
状态：实施基线<br>
更新日期：2026-08-31<br>
上位契约：`webui-compatibility-contract.md`

## 1. 目的与原则

本规范定义第三方 WebUI 从材料接收、审核、验证、签名发布、复核到退出的完整治理流程。它只产生规则候选、审核结论和支持声明，不直接授予运行时能力。运行时授权唯一来源仍是签名托管主程序集内的内置注册表和当前页面能力快照。

每次审核的最小单位是一个精确组件组合、一个 Harness/LAN/route 基线和一个能力切片。聚合包、仓库名或“最新版本”不是审核单位。一个申请可以拆成多个审核单位；任何单位失败不得由其他单位的通过掩盖。

## 2. 角色与责任

| 角色 | 主要责任 | 必须独立的决定 |
|---|---|---|
| WebUI 提供方 | 提供可复现版本、描述符、套件、变更和维护承诺 | 不批准自己的规则或支持声明 |
| route owner | 证明 Host/Origin、会话、CSRF、数据权限、错误语义和升级兼容 | 不批准宿主安全例外 |
| 外部依赖 owner | 说明每个外站用途、路径、方法、资源类型、重定向和退出方案 | 不扩大到任意同站能力 |
| Windows 兼容维护者 | 受理、拆分审核单位、实现候选规则、维护 fixture 和影响清单 | 不单独批准安全边界或正式发布 |
| 安全维护者 | 审核全局上限、冒用安全、CSP、严重性和墓碑 | 对 Critical/High 拥有暂停建议权 |
| 测试执行者 | 在规定 Runtime、Edge、fixture 和实机环境生成可重复证据 | 不修改规则以制造通过 |
| 授权发布者 | 核对签名候选、证据身份、支持记录和发布确认 | 不跳过 FAIL/SKIP/MISSING |
| Linux 管理者 | 部署精确提供方版本和 Harness/LAN 基线，提供同 endpoint 环境 | 不代表 Windows 项目承诺支持 |
| 用户 | 接受或撤销目标级外部影响 | 不编辑规则或突破全局上限 |

Windows 兼容维护者、安全维护者和授权发布者应由可审计的责任主体承担。安全维护者不得与提供方自审合并。人员不足时流程保持关闭，不以匿名或占位角色替代。

## 3. 唯一机器规范来源

P0 必须建立并锁定以下路径；同名人类文档由生成器产生，禁止手工维护平行状态：

| 路径 | 唯一内容 |
|---|---|
| `schemas/webui-compatibility-descriptor.schema.json` | 同源描述符 |
| `schemas/webui-contract-capabilities.schema.json` | 契约版本和条件能力清单 |
| `schemas/webui-adapter-registry.schema.json` | 活动规则和墓碑 |
| `schemas/webui-provider-intake.schema.json` | 接入包 |
| `schemas/webui-review-decision.schema.json` | 审核决定 |
| `schemas/webui-support-record.schema.json` | 支持记录 |
| `schemas/webui-release-evidence.schema.json` | 候选结构化证据 |
| `schemas/verification-impact-map.schema.json` | 变更影响清单 |
| `compatibility/contracts/webui-contract-capabilities.json` | 当前契约实际授权能力 |
| `compatibility/registry/webui-adapter-registry.json` | 内置注册表规范数据 |
| `compatibility/providers/examples/` | 正向、负向和脱敏示例 |
| `eng/generate-webui-governance.ps1` | 校验、规范化和生成人类摘要 |

全部文件使用通用契约 5.1 的字节级规范化和摘要。生成器必须验证 Schema、状态迁移、交叉身份、摘要一致性和敏感字段允许集。生成失败阻止候选进入审核或发布。

## 4. 不可变接入包

接入包以 `intakeId + intakeRevision` 标识。已提交 revision 不可覆盖；补充材料产生新 revision 并保留前版。至少包含：

- 提供方、维护者和正式联系渠道；
- `uiId` 集合、精确 `uiVersion`、不可变 `sourceRev`、源码 commit 和构建来源；
- 描述符响应规范字节、Schema/契约版本和 SHA-256；
- 精确 Harness commit、LAN 插件版本、route 版本和部署步骤；
- route owner 对 Host/Origin、会话、CSRF、数据权限和错误语义的声明；
- 每个 adapter key 对应的能力切片；
- 每个外部 origin 的 owner、用途、显式端口、窄路径、方法、资源类型、frame 父子关系、重定向和状态依赖；
- WebSocket、Worker、AudioWorklet、OOPIF、CDP Fetch 等条件能力需求；
- CSP、CORS、SameSite、Cookie、证书和第三方服务前置；
- 可离线复跑的提供方一致性命令、fixture、预期结果和摘要；
- 数据分类、隐私影响、威胁模型、已知限制和退出方案；
- 支持窗口、变更通知渠道、维护承诺和停止维护条件。

接入包不得包含 token、Cookie、私钥、真实业务数据、页面内容、图片或可直接访问的敏感 URL。无法脱敏的安全材料只通过私密安全入口引用，不复制到普通仓库或证据。

## 5. 双轴状态机

### 5.1 申请状态

正常流转为：

```text
Received
→ AwaitingProvider
→ InReview
→ RuleCandidate
→ Verification
→ AwaitingSignedRelease
→ ClosedSupported
```

允许的终态是 `ClosedRejected`、`ClosedProviderWithdrawn` 和 `ClosedSuperseded`。任意回退必须建立新 revision、稳定原因码和责任人，不得重写历史状态。材料 30 个自然日没有提供方响应时，从 `AwaitingProvider` 进入 `ClosedRejected`，原因码为 `INCOMPLETE_IDENTITY` 或适用的稳定码；恢复时建立新申请。

### 5.2 支持状态

支持状态独立于申请状态，只允许：

- `NoFormalSupport`；
- `Supported`；
- `Paused`；
- `Superseded`；
- `PlannedDeprecation`；
- `Revoked`。

只有规则已进入精确签名版本、全部适用门禁通过、审核决定获批且支持记录生成后，才能进入 `Supported`。申请完成不自动产生支持；`Paused` 和 `Revoked` 不修改已发布旧客户端，只控制项目的新承诺并触发签名 patch/墓碑流程。

## 6. 稳定原因码

机器记录只使用以下稳定码；人类说明可以补充但不能替代：

| 原因码 | 适用情形 |
|---|---|
| `INCOMPLETE_IDENTITY` | 精确版本、revision、route 或责任主体缺失 |
| `UNREPRODUCIBLE` | 套件或环境无法复现 |
| `ROUTE_SECURITY_FAILED` | 会话、CSRF、权限或错误语义不合格 |
| `OUTSIDE_GLOBAL_CEILING` | 依赖永久禁止能力 |
| `RULE_TOO_BROAD` | origin、路径、方法、类型或重定向无法窄化 |
| `GATE_FAILED` | 自动、Runtime、Edge、实机或发布门禁失败 |
| `IDENTITY_MISMATCH` | 接入、规则、候选或证据身份不一致 |
| `MAINTENANCE_ENDED` | 维护承诺结束或无人接替 |
| `PROVIDER_WITHDRAWN` | 提供方主动退出 |
| `DEPENDENCY_UNAVAILABLE` | 必需外部依赖不可持续获得 |

新增或改变原因码语义需要 Schema 和生成器版本变化，不能用自由文本临时造码。

## 7. 审核决定与支持记录

审核决定至少绑定 `decisionId`、接入包摘要、审核单位、规则候选摘要、契约/Schema/基线身份、全局上限结论、冒用分析、适用门禁、风险、原因码、Windows 兼容维护者和安全维护者结论及时间。决定只可 `ApprovedForVerification`、`Rejected`、`NeedsProvider` 或 `Superseded`。

支持记录至少绑定 `supportRecordId`、支持状态、精确组件组合、Harness/LAN/route、契约版本、registry/rule 版本、启动器签名版本、安装包摘要、双 Runtime、Edge、fixture、结构化证据摘要、支持范围、已知限制、`lastVerifiedAt`、`reviewDueAt`、负责人、弃用日期和墓碑原因。记录是声明，不参与运行时授权。

人类审核摘要和支持清单必须由规范机器记录生成。生成物显示必要身份和限制，不显示敏感字段。

## 8. 安全事件、暂停与修复

| 严重性 | 判定 | 响应 |
|---|---|---|
| Critical | 可直接突破全局上限、泄露凭据或执行原生能力，且有现实利用路径 | 立即暂停支持，阻止候选，准备签名 patch 和永久墓碑；不等待常规弃用期 |
| High | 可跨目标/跨 origin 越权、绕过强制或产生重大数据暴露 | 暂停新承诺和发布，完成根因与签名修复；不等待常规弃用期 |
| Medium | 受限功能或防御纵深缺陷，无已知直接越界 | 建立修复时限和影响清单，未按期完成转 `Paused` |
| Low | 文档、可观测性或低风险一致性问题 | 纳入常规维护，不得用其掩盖更高严重性 |

公开入口接收普通兼容申请；私密安全入口接收漏洞和敏感复现。安全事件先保全脱敏最小证据，再按最后受支持版本计算影响并集。旧客户端无法远程停用；项目必须明确这一限制，并用完整签名 patch 和永久墓碑修复。

## 9. 复核、变更与故障分流

以下事件立即触发重验：WebUI/组件版本、`sourceRev`、route、外部依赖、Harness/LAN、Runtime、契约、Schema、注册表、CSP 债务、维护者或威胁模型变化；身份未变但行为变化按 `IDENTITY_MISMATCH` 暂停。

每个正式候选都执行影响清单推导。`Supported` 记录每季度进行一次轻量复核，核对维护状态、外部依赖、已知安全事件和可复现性；每年进行一次完整治理复核。`reviewDueAt` 逾期时转 `Paused` 并停止新支持承诺，但离线规则不依系统时间失效。

问题按以下顺序分流：

1. 同 endpoint Edge 也失败且服务响应一致，归 route owner、提供方或外部依赖 owner；
2. Edge 成功而 WebView2 因快照、CSP、导航或 Runtime 失败，归 Windows 兼容维护者；
3. 身份、材料或环境无法确认，先转 `AwaitingProvider`，不猜测归因；
4. 可能突破全局上限时先按安全事件处理，再决定最终 owner。

## 10. 弃用、退出与维护权转移

非 Critical/High 退出至少经过一个正式签名版本的 `PlannedDeprecation`，写明替代身份、最后支持版本和目标撤销日期；随后以墓碑进入 `Revoked`。Critical/High 可以直接暂停并在首个可用签名 patch 中墓碑撤销。

提供方退出、依赖不可用或维护者结束时，使用相应稳定原因码。新维护者接管必须提交新的责任声明和接入 revision，完成身份、套件、安全与渠道复核；不得只改联系人延续旧承诺。被新版本替代的记录转 `Superseded`，旧 rule ID 不复用。

## 11. 记录保留与沟通

正式接入包、审核决定、支持记录、规则/墓碑、签名候选身份和最终结构化证据永久保存。普通失败原始日志保留到下一次成功验证或最多 90 天，取较早者；稳定原因、根因摘要和修复身份永久保存。所有记录遵守敏感字段允许集。

状态变化、补件、暂停、弃用和撤销只通过已登记正式渠道通知并写入机器记录。聊天、临时票据、个人邮箱转述或口头确认不能改变状态。对外声明必须从当前支持记录生成。

## 12. 流程开放门槛

正式扩展接入流程只有在以下条件全部满足后才可宣布开放：

- 所有角色、公开入口、私密安全入口、签名发布身份和 SLA 无占位值；
- 全部 Schema、规范数据、生成器、接入模板、正反示例和原因码表已发布并自动验证；
- 双轴状态机、交叉身份、摘要一致性和敏感字段扫描测试通过；
- 至少一个参考适配器从接入包到签名候选、审核决定、支持记录和对外生成物完整走通；
- 发布检查表能够按影响清单重放该流程。

条件不全时，只能发布基础兼容自助材料。不得宣称正式扩展接入、长期维护服务或参考适配器已经受支持。
