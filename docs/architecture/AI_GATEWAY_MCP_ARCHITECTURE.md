# CRSTool AI Gateway / MCP 双入口架构与落地方案

> 状态：**目标架构；本机双入口与附件核心已实现，远程 MCP 接入及生产部署未验收** | 版本：v1.1 | 更新：2026-11-13
> 
> 适用：CRSTool（.NET 10 / WPF / Windows Service / SQLCipher / 插件体系）
>
> 权威现状先读：[CURRENT_ARCHITECTURE.md](CURRENT_ARCHITECTURE.md)、[ARCHITECTURE_STATUS.md](ARCHITECTURE_STATUS.md)、[AI 双入口 POC](../implementation/AI_DUAL_ENTRY_POC.md)和[AI 附件处理](../implementation/AI_ATTACHMENTS.md)。本文保留目标架构与安全约束；当前实际项目、部署范围和验证状态以实施文档及源码为准。后续目标设计不能覆盖当前尚未实现的远程服务、OAuth、Tunnel 和生产部署边界。

## 0. 决策摘要（ADR-AI-001）

1. **统一业务边界，双入口**：WPF 内置助手通过独立的 `CRS.AI.Gateway` 发起 Responses API 分析；ChatGPT 经自定义 MCP / Secure MCP Tunnel 调用 Gateway 的同一套受控业务查询。两条路径共享工具契约、授权、脱敏、审计；不是让 Gateway 重复代 ChatGPT 调用模型。
2. **Phase 1 仅在合法受支持地区使用 OpenAI API**。Plus 可用于 WPF 内部 API 分析，但 API 独立计费。依 OpenAI 当前官方说明，自建 MCP 直接连接 ChatGPT 需具备支持套餐：Pro 可接只读 read/fetch，Business/Enterprise/Edu 可接完整 MCP；Plus 不作为此能力的交付前提。不得以个人新建 GPT Actions 作为兜底：2026-10-26 后个人套餐停止新建 GPT。最终以实际账号权限和官方最新政策为准。
3. **公网固定 IP 不是必要条件**：内网 Gateway 和 MCP Server 无公网入站端口；符合条件时 tunnel-client 从受支持地区向 `api.openai.com:443` 主动 HTTPS 连接 OpenAI。若企业出口 IP 白名单、代理或其他安全制度要求，出口地址另行验证。
4. **最高安全边界**：后台 Worker 既有服务权限仅访问公开市场研究数据，**绝不为了 AI 入口而开放个人 SQLCipher 税务库、TOTP 密钥或 WPF 登录会话**。个人数据仅由处于用户登录、明确解锁状态的交互式桌面进程发布最小范围、短时、只读的数据快照。
5. **AI 一律不掌握最终裁决权**：金额/税费/收益率/历史事实与证据由现有确定性计算引擎产生；AI 只能解释、比较、给出分析，不能修改账本、执行交易、申报税务或静默改动作业。
6. **强制 P0 门槛**：部署地、API 使用地区、支付资格和跨境数据授权未验证前，不启用外部 OpenAI 连接；离线工作流保持完整可用。

## 0.1 当前实现基线（2026-11-13）

本节描述当前 `main` 源码和已取得的自动化证据；后续架构图与分阶段表仍保留目标设计，不能反向解释为当前部署拓扑。

| 范围 | 当前实现 | 尚未证明的事项 |
| --- | --- | --- |
| 只读查询与 Gateway | `CRS.AI.Contracts`、`CRS.AI.Query`、`CRS.AI.Infrastructure`、`CRS.AI.Gateway` 已进入 main；固定工具白名单、Worker 专用管道、Windows 用户授权/撤销和最小审计已有实现及离线检查 | 正式生产安装、正式审计归档/备份、长期负载与真实运维恢复 |
| 模型调用 | `CRS.AI.Models` 提供 OpenAI Responses、Anthropic Messages、MiMo/DeepSeek 等模型适配；WPF 助手可在本机使用模型设置与工具调用 | 真实供应商 API 账户、地区/跨境合规、生产预算与外发审批 |
| MCP | `CRS.AI.Client` 承载 Stateless Streamable HTTP，当前仅绑定 `127.0.0.1:8765/mcp`，通过 Bearer 凭证识别本机调用者 | 公网 HTTPS、OAuth、多租户身份、Secure MCP Tunnel、ChatGPT 远程连通 |
| 附件 | WPF 与 MCP 共用 `CRS.AI.Models.Attachments`；MCP 提供 `crs_chat_start/message/remove_attachment/end` 和 `openai/fileParams` | 真实 ChatGPT 账号上传/下载端到端及目标套餐授权提示 |

附件规则、MCP 会话 TTL、清理、隐私边界和测试命令见[AI 附件处理](../implementation/AI_ATTACHMENTS.md)。PR [#35](https://github.com/sword0880/CRSTool/pull/35) 已合并 main。其 Windows CI [run 43366766980](https://github.com/sword0880/CRSTool/actions/runs/43366766980) 中 Release Build、AI 工具一致性、附件集成和架构依赖检查通过；整个工作流因独立 Worker 插件监督测试的 `TaskCanceledException` 失败，不能标记为全绿。

**重要边界：** MCP 文件参数元数据和假下载器集成测试只证明本机协议实现与会话生命周期，不证明 ChatGPT 已经能通过远程网络访问本机 CRS。当前 MCP 仅监听 loopback。

## 1. 业务需求与边界

### 1.1 典型用例

| 查询 | 来源 | 最小输出 | 权限 |
|---|---|---|---|
| “按月份比较各券商已实现收益” | 绩效确定性服务 | 汇总、币种、期间、算法/数据版本、证据 ID | 私有、明确授权 |
| “本年度资本利得和股息税如何计算？” | 税务确定性服务 | 汇总与计算解释、法规口径标记、数据缺口 | 私有、明确授权 |
| “比较国家队 ETF 份额和价格变化” | NationalTeam 公开研究服务 | 时间范围、口径、数据源/采集时间、修订标记 | 公开研究 |
| “为什么两个图表不一致？” | 多来源查询 + 比较 | 差异原因、来源、缺失值、不确定性 | 按字段授权 |

### 1.2 不在首期范围

- AI 控制券商、下单、转账、执行税务申报；写入持仓、交易、资产或定时任务。
- 任意 SQL、任意文件读写、导出全量账户库、读取明文 API 密钥。
- Worker 无人值守时访问尚未解锁的个人税务数据。
- 无限制 RAG 上传整个数据库或证据原件至模型服务。
- 代理/第三方服务绕过地区、账号或使用条款限制。

## 2. 目标运行架构与安全域

以下拓扑为远程 MCP/Tunnel 等目标形态，不是当前已经部署的运行图。当前本机 MCP Host 位于 `CRS.AI.Client`，与 `CRS.AI.Gateway` 分离并仅监听 loopback；实际结构见本节前的实现基线。

```text
用户交互式 Windows Session                         外部受支持 OpenAI / ChatGPT
+--------------------------------------+          +----------------------------------------+
| CRS.Desktop.Wpf                      |          | ChatGPT（支持自定义 MCP 的套餐）       |
| UI 插件：AI Chat / 历史对比            |          |   | MCP tool call                        |
| 已解锁私有库的短时 Query Broker        |          |   v                                     |
| 仅基于 Windows 用户身份与显式解锁授权   |          | Secure MCP Tunnel 托管端                |
+-----------+--------------------------+          +-----------------^----------------------+
            | 本地授权 Named Pipes                                   | 出站 HTTPS:443
            v                                                       |
+-------------------------------------------------------------------+------------+
| 私有 Windows 主机 / 受支持地区的受控部署                            |
| CRS.AI.Gateway (.NET 10 Generic Host, 逻辑独立的进程)              |
|  - Policy / Identity / Tool Catalog / DLP / Rate Limit / Audit     |
|  - Responses API 客户端（仅 WPF 内置助手链路主动调用）              |
|  - MCP Server（ChatGPT 链路，不再重复模型推理）                     |
|  - tunnel-client（独立进程/Windows Service，最小权限与独立密钥）    |
|      |                            |                                 |
|      v                            v                                 |
|  公开数据 Reader             用户会话 Broker（临时快照，可能拒绝）   |
+------+-----------------------+-------------------------------------+
       | 内部只读协议          | 只读、一次性授权，TTL
       v                       v
CRS.Worker.Service        CRS.Desktop.Wpf / 解锁后的 SQLCipher 私有数据
(仅公开研究数据)
```

- `CRS.Worker.Service` 仍是唯一 Quartz Scheduler；AI Gateway **不成为第二调度中心**，不跨进程拿 Worker DI 容器。
- Gateway 不引用 WPF 类型，不绕过受限 IPC，通过显式业务查询契约访问公开市场研究结果。
- 对私有库的查询仍在用户桌面进程/可信业务服务中完成，只把白名单摘要 DTO 交给 Gateway。WPF 关闭、锁定、登出或授权过期 => 私有查询立即拒绝，公开市场数据仍可查询。
- 若未来确需无人值守处理私人数据，应单独制定密钥托管/用户许可/隐私边界 ADR；本方案**不默认支持**。

## 3. 目标项目分层（建议增量，不改变现有模块）

```text
src/
  CRS.AI.Contracts/         # BCL DTO、ToolDefinition、隐私等级、版本、错误码
  CRS.AI.Application/       # Tool Catalog / Authorization / Snapshot / Audit 协调
  CRS.AI.Infrastructure/    # OpenAI HTTP 适配、成本计量、密钥读取、审计仓储
  CRS.AI.Gateway/           # .NET 10 Generic Host；MCP endpoint/Responses orchestration
  CRS.AI.Desktop/           # UI 层适配，仅由 CRS.Desktop.Wpf 引用
tests/
  CRS.Tests.AI.Contracts/
  CRS.Tests.AI.Policy/
  CRS.Tests.AI.Integration/
```

- `CRS.AI.Contracts` 不依赖 OpenAI SDK、MCP SDK、WPF、SQLite 或业务插件实现；统一 DTO/Schema 版本和能力枚举。
- `CRS.AI.Application` 不引用 `PresentationFramework` 或网络实现，依赖现有业务应用层的**最小只读投影接口**；`CRS.Common` 仅放真正跨插件的纯通用能力，不塞 AI 特定逻辑。
- `CRS.AI.Infrastructure` 是外部供应商适配层，抽象 `IModelProvider`，避免 OpenAI SDK 类型外溢到业务层；不承诺其他供应商已兼容。
- `CRS.AI.Gateway` 按逻辑独立部署和最小权限运作，具体是本机独立服务还是同主机受控进程需 POC；不能直接扩展既有 Worker 进程权限。
- 若插件希望提供 AI 能力，仅暴露显式 `IAiReadToolProvider`/只读投影，宿主集中审核工具及敏感级别。不要为每个插件再建 MCP Server / API Key / 第二个 Gateway。

## 4. 工具契约和证据

首批只读工具白名单（名称为建议，可随 POC 冻结）：

| 工具 | 输入范围 | 返回字段 | 分类 |
|---|---|---|---|
| `get_capabilities` | 无 | 当前可访问数据域、隐私模式、版本 | public |
| `get_market_signal` | 标的白名单、时间区间、source | 指标、时间戳、来源、修订 | public |
| `get_portfolio_summary` | 账户别名、期间、币种 | 持仓聚合/收益统计，禁止账户号 | private |
| `get_performance_attribution` | 时间区间、币种、维度 | 分解结果、算法版本、缺失项 | private |
| `get_tax_summary` | 税年、券商别名、类别 | 计算汇总、证据编号、口径提示 | highly-sensitive |
| `get_source_evidence` | 已授权的证据 ID | 摘要、哈希、时间，不返回原始附件 | scoped-private |

统一响应外壳建议：
```json
{
  "schemaVersion": "1.0",
  "tool": "get_portfolio_summary",
  "asOfUtc": "2026-11-11T08:00:00Z",
  "timeZone": "Asia/Shanghai",
  "currency": "CNY",
  "dataVersion": "snapshot-id",
  "calculationVersion": "deterministic-engine-version",
  "sourceRefs": [{"id":"evidence-id","sha256":"hex-string"}],
  "completeness": {"status":"partial|complete","missingSources":[]},
  "redactionPolicy": "private-summary-v1",
  "data": {}
}
```
说明：以上仅为 Schema 样例，不代表真实运行数据；禁止在错误响应中回显 SQL、文件路径、堆栈、API Key 或账户信息。严格校验日期范围、账户白名单、最大结果条数、分页令牌、Schema 长度和调用时限。

## 5. 双入口调用时序

**A. WPF 内置分析（Plus + API 足够）：**
1. WPF 用户选择工具范围/分析模式 -> 本地权限校验与明确数据分享确认。
2. Gateway 调用确定性业务查询，生成最小快照；DLP 去除姓名、身份证号、券商账号、地址、原始 CSV、密钥。
3. Gateway 用 Responses API 执行分析，建议 `store=false`、不默认启用 hosted file search、保留自有审计摘要。
4. 返回“结论 + 依据 + 计算版本 + 不确定性 + 消耗”；WPF 只显示，用户可主动复制/export 受控报告。

**B. ChatGPT 直接查询（具备 MCP 权限后）：**
1. ChatGPT 对话调用 `get_market_signal` 等 MCP 工具。
2. OpenAI 托管通道经 `tunnel-client` 拉取，转发给内部 MCP Server。
3. Gateway 必须独立校验访问令牌、调用主体、audience、scope、工作空间/绑定、允许的工具、频次。
4. 公开数据直接返回；私有数据经短时用户会话 Broker，再次验证用户授权/解锁状态。无授权时返回 `authorization_required`，**不能用 Worker 服务身份代取**。
5. MCP 返回最小数据和来源，ChatGPT 在对话模型中分析；Gateway 不对同一次分析再做 Responses API 调用。

**关键**：Tunnel 解决内网**传输连通**，不是用户级的数据授权。将应用身份校验与数据访问策略独立实现；不能因有 Tunnel token 就默认为该用户获准读取全部本地资料。

## 6. 套餐、收费与采购决策（2026-11-11 核实）

| 能力 | Plus | Pro | Business | Enterprise/Edu |
|---|---|---|---|---|
| CRSTool 调用 OpenAI API | 可以，单独计费 | 同左 | 同左 | 同左 |
| 当前 ChatGPT 自建 MCP | 不以 Plus 为交付前提 | 可连 read/fetch | 支持完整 MCP | 完整 MCP + 企业治理 |
| ChatGPT 使用内容默认训练 | 个人设置可选择退出 | 同左 | 默认不训练 | 默认不训练 |

- 当前建议：**保留 Plus + 单独 API project**，先开发入口 A 与共同业务服务；入口 B 的 ChatGPT 直连验收需另有支持套餐。
- Pro 是否比 Business 合算，取决于是否只需只读。Business Standard 月付官方标价 US$25/席、年付折合 US$20/席，通常至少 2 席；部分新工作空间若结账提供可仅 1 席。价格、席位和账户权益以实际结账页为准。
- API 的输入/输出 Tokens、工具和存储可另计费，**ChatGPT 订阅不抵扣一般 API 用量**（如有针对特定套餐的促销额度须单独核实）。
- Gateway 强制计量：project + model + tool + requestId + tokens + estimated cost，支持每次/每天/每月预算、异常熔断、重试上限、配额告警和手工紧急停用。平台计费限额不能替代应用层闸门。
- 一期不指定具体模型与固定 Token 价格；由配置允许列表 + 官方 pricing 同步/人工审核并设置回归基准测试。

## 7. 网络、部署地区与服务可用性

| 流量 | 发起方 → 目标 | 方向 | 端口 | 设计 |
|---|---|---|---|---|
| API 推理 | Gateway → `api.openai.com` | 出站 | TLS 443 | 支持地区，受控代理/证书校验 |
| MCP Tunnel | tunnel-client → `api.openai.com/v1/tunnel/*` | 出站 | TLS 443 | 内网拉取请求，**不需要**固定公网 IP / 公网入站 |
| 可选 mTLS | tunnel-client → `mtls.api.openai.com` | 出站 | TLS 443 | 依官方支持及配置 |
| 内部 MCP | tunnel-client → Gateway | 本地或内网 | loopback/本地端点 | 不暴露公网 |
| 私有数据 | Gateway ↔ 已登录 WPF Broker | 本机 IPC | Named Pipes | SID/ACL、会话内短时授权、超时 |

- 默认防火墙 **拒绝所有来自互联网的入站连接**；只对白名单出站目的地开放必要访问。严禁为了 ChatGPT 开 8528、业务 API 或 SQLCipher 数据库公网端口。
- OpenAI Secure MCP Tunnel 默认约束包括请求截止时间 120 秒、序列化报文上限 10 MiB、共享吞吐限制；实际以官方最新规则为准。大结果必须摘要 + 分页，不能通过提高预算绕过。
- 出口固定 IP **不是 OpenAI API / Tunnel 的强制条件**；如企业 IP allowlist、网关访问审计、内部准入政策要求稳定出口，则属于额外网络治理选项。
- **地区是硬门槛**：OpenAI API 受支持国家/地区列表不含中国大陆；不能把大陆 Windows 主机通过代理、海外固定 IP 或 VPN 伪装为合规方案。若生产主机位于不支持地区，入口 A/B 的外部 OpenAI 接入应暂停；评估受支持地区的合法部署与数据跨境许可，或本地兼容模型方案。仅能访问网址不等于获得支持资格。
- 不对跨境税务/交易明细合法性做未经验证的判断；部署前由数据所有者确认个人金融信息出境、第三方数据使用权和适用法规。

## 8. 安全控制：强制 P0 / P1

**P0 — 必须实现后才能接个人数据：**

1. 默认 deny；Gateway/Worker 独立服务账户、文件 ACL、IPC SID 校验、API/MCP 独立身份验证和最小 scope。
2. 外部模型/ChatGPT 只接收用户明确授权的最小查询结果；禁止通配 SQL/文件工具、无限记录集、原始个人资料批量导出。
3. 个人 SQLCipher 私钥和 TOTP 永不交给 Worker/Gateway/tunnel-client；WPF 解锁 broker 有效期短，注销/锁屏即撤销。
4. 密钥放 Windows DPAPI/受控 Secret Store；服务账户 ACL、定期轮换、泄露吊销；绝不在 git、appsettings、浏览器、日志或模型上下文中出现。
5. 对工具字段做数据分类、结构化 DLP 和逐字段 allowlist；第三方文本视为不可信数据，防 prompt injection 和跨工具越权调用。
6. 客户端 / Gateway / MCP 都使用明确的请求上限、速率限制、超时、熔断、并发限制；防止提示诱导的递归工具调用和账单失控。
7. 审计仅记录调用主体、工具名、范围摘要、授权策略、输入/输出数据哈希、证据 ID、token/费用、拒绝原因、requestId；**默认不存完整 prompts、MCP payload、报税原文**。存储仍遵循 SQLCipher/现有安全规范。
8. AI 结果必须标注非确定性、来源及时间；税务计算必须由受控确定性引擎执行；禁止写账本、交易、税表。
9. 数据默认训练区别：API 默认不用于模型训练；个人 ChatGPT Plus/Pro 应检查“为所有用户改进模型”并关闭；Business 工作空间默认不用于训练。即便 `store=false`，默认 API 滥用监控日志仍可能保留最多 30 天，不能宣称 ZDR。
10. 拦截出错/离线应 **fail closed**：私有功能不可用但现有离线计算及公开查询维持不受影响。

**P1 — 扩展上线前：**工具 Schema 冻结、MCP OAuth/连接绑定、证据一致性监控、撤权生效时延验证、日志清理与备份恢复、API 预算分析、模型替换基准集、隐私策略版本化。

## 9. 实施计划、交付件和退出条件

| 阶段 | 工作 | 验收标准 | Go/No-Go |
|---|---|---|---|
| G0 约束核实 | 账号套餐、OpenAI 受支持地区、隐私授权、当前代码边界 | 证据记录与 ADR 审批 | 任何跨境/地区问题不通过则不接 |
| G1 Contract POC | `CRS.AI.Contracts`、三只公开只读工具、DTO/证据链/参数约束 | Mock 无外网测试、无 SQL 原文 | 禁止读取个人库 |
| G2 Gateway | AI Gateway + Responses API Provider + 预算/审计 | API Smoke + 人工核账 + 安全测试 | 公共数据上线 |
| G3 WPF private Broker | 解锁状态的用户会话 Broker + 授权弹窗 + 只读摘要 | 登录/锁屏/登出/超时/拒绝权限全通过 | 可开放个人数据 |
| G4 MCP/ChatGPT | MCP Server + Secure MCP Tunnel + 支持套餐 | ChatGPT 真实 read/fetch 工具调用、撤权即时生效 | 无套餐/隧道证据不得宣称完成 |
| G5 生产运行 | 服务安装/卸载、密钥轮换、异常、费用、审计运维 | 断网恢复、重启、限流、降级与回滚全部可复核 | 形成运维 Runbook |

建议先写对应测试：
- `PublicToolNeverReadsPrivateVault`
- `PrivateToolDeniedWithoutInteractiveUnlock`
- `LockScreenRevokesBrokerAccess`
- `PromptInjectionCannotCallUnlistedTool`
- `AuditDoesNotLeakSensitiveFields`
- `RepeatedPromptCannotBypassBudget`
- `ToolResponseContainsCalculationVersionAndEvidence`
- `RegionGateBlocksUnsupportedDeployment`
- `TunnelDisconnectDoesNotExposeInternalEndpoint`
- `DeterministicTaxResultIsNotOverwrittenByModel`

## 10. 反方审查与未关闭风险

| 风险/反方问题 | 影响 | 要求的反证或证据 |
|---|---|---|
| “共用 Gateway 是否导致 Worker 特权提升？” | P0 | 服务 Token/ACL/进程隔离测试；不得新增 Worker 对个人库权限 |
| “ChatGPT 能否静默读取锁屏状态下的账户？” | P0 | 断开/锁屏/注销后零数据返回；撤权自动生效 |
| “Plus 是不是可以直接自建 MCP？” | P0 | 官方套餐说明 + 真实账号 UI 测试；不要用文档假定权限 |
| “大陆部署经固定 IP 能否解决？” | P0 | 否；必须遵守 OpenAI 支持地区和数据适用法律，网络连通性不是合规资格 |
| “AI 的数字能不能用来申报税？” | P0 | 所有金额必须来自确定性引擎且带计算与证据版本 |
| “`store=false` 是否等于零留存？” | P0 | 否；必须披露 API 滥用监测等保留与业务自存数据 |
| “工具接口让 AI 见到多少私人明细？” | P0 | 字段白名单、返回规模限制、脱敏断言、用户同意审计 |
| “为何不用 WPF 直接调 OpenAI？” | P1 | 客户端泄漏密钥及权限、审计预算无中心控制；走 Gateway |
| “模型成本是否与 ChatGPT 订阅重复？” | P1 | API 通道按使用收费；ChatGPT MCP 通道由 ChatGPT 分析，不二次调用模型 |
| “MCP Tunnel 提供了登录认证吗？” | P0 | 它是传输机制；还需独立验证应用调用者与数据域授权 |

## 11. 官方参考（检索于 2026-11-11，复核更新）

- ChatGPT 自建 MCP 套餐与权限：https://help.openai.com/en/articles/12584461-connecting-custom-mcp-servers-in-chatgpt
- Secure MCP Tunnel：https://developers.openai.com/api/docs/guides/secure-mcp-tunnels
- MCP Tool / Responses API：https://developers.openai.com/api/docs/guides/tools-connectors-mcp
- ChatGPT/API 分开计费：https://help.openai.com/en/articles/9039756-managing-billing-for-chatgpt-and-the-api-platform
- ChatGPT Business 席位收费：https://help.openai.com/en/articles/8792536-managing-billing-and-seats-in-chatgpt-business
- 支持地区：https://help.openai.com/en/articles/5347006-openai-api-supported-countries-and-territories
- API 数据使用和保留：https://developers.openai.com/api/docs/guides/your-data
- 个人 ChatGPT 数据控制：https://help.openai.com/zh-hans-cn/articles/7730893-data-controls-in-chatgpt
- 个人套餐自定义 GPT 新建限制：https://help.openai.com/en/articles/8554397-creating-and-editing-gpts

**审查结论**：架构方向有条件可行；在 G0（地区与套餐）、G3（私有库会话隔离）和 G4（实际 ChatGPT MCP 接通）证据未齐前，不得发布“已支持个人账户直接在 ChatGPT 查询”的功能声明。
