# CRSTool AI Gateway / MCP 双入口架构与落地方案

> 更新：2026-11-13。本文是 AI 的唯一目标架构文档；当前源码与测试状态以 [AI 双入口与附件实现](../implementation/AI_DUAL_ENTRY_POC.md) 为准。架构图是目标形态，不等于远程连接或生产部署已经完成。

## 1. 决策摘要

1. **一个受控业务边界、两个模型入口。** WPF 助手通过 Gateway 调用模型 API；ChatGPT 等外部客户端通过 MCP 调用同一组受控只读工具。两条路径共享工具契约、数据质量、授权、脱敏和审计。
2. **AI 不成为金融事实源。** ETF、行业、持仓披露、周期和税务金额由现有确定性领域服务产生；模型只能解释、比较和提出待核验问题，不重算正式金额、不回写账本、不下单、不申报。
3. **首期只开放公开研究数据。** 个人税务、交易流水、持仓账户、绩效保险库和 TOTP/恢复密钥均不通过 Worker 或 MCP 暴露。
4. **Worker 仍是唯一后台调度宿主。** AI Gateway 不创建第二个 Quartz、不直接访问 Worker DI 容器或业务数据库。
5. **默认拒绝。** 未经身份、工具白名单、请求预算、数据质量、审计和部署验收，不开放外部网络入口。

## 2. 当前 main 的真实项目与边界

| 项目 | 当前职责 | 当前边界 |
| --- | --- | --- |
| CRS.AI.Contracts | 工具契约、Schema、调用者上下文、结果与错误码 | 不承载模型 SDK 或 WPF 类型 |
| CRS.AI.Query | 固定只读业务映射、Worker IPC、数据质量、分页、策略门禁 | 不接收任意 SQL、路径或 operation |
| CRS.AI.Adapters | MCP 与 OpenAI 工具协议适配 | 两入口复用同一业务网关 |
| CRS.AI.Infrastructure | 本地追加式审计文件实现 | 不是操作系统 WORM；ACL、备份和留存仍需运维验收 |
| CRS.AI.Gateway | 本机服务身份、授权/撤销、审计及受限 Worker 查询 | 生产安装和最新 Worker 配套尚未完成完整验收 |
| CRS.AI.Client | 本机 MCP Streamable HTTP 与客户端传输入口 | MCP 当前仅监听 127.0.0.1 |
| CRS.AI.Models | 模型供应商适配、模型设置和工具分发 | 真实供应商账户、地区、费用和外发策略另行验收 |

附件解析器和会话模型位于 CRS.AI.Models 的 Attachments 功能内，并非独立项目。WPF 与 MCP 共用同一解析策略，详情见 [实现记录的附件章节](../implementation/AI_DUAL_ENTRY_POC.md#ai-attachments)。

### 当前已开放的工具

| 工具 | 当前用途 | 限制 |
| --- | --- | --- |
| crs_get_etf_overview_v1 | 读取已发布 ETF 概览 | 固定公开字段；不归因国家队买卖 |
| crs_get_etf_coverage_v1 | 读取已登记日历区间的覆盖与质量 | SSE/SZSE、六位代码、最多 30 天；缺口/未核验返回 NotReady |
| crs_chat_start / crs_chat_message / crs_chat_remove_attachment / crs_chat_end | 管理当前 MCP 调用者的临时附件会话 | 只处理附件上下文，不增加金融查询权限；属于会话状态变更工具 |

概览/覆盖查询单次最多 64 条、8 页、48 KiB JSON、15 秒；跨页必须使用同一快照。保留 null，不补造 0。ETF 份额变化、全市场成交额不能直接证明某机构真实买卖。

## 3. 目标运行拓扑

- WPF → 本机 Gateway → 专用 Worker 只读管道 → NationalTeam 已注册查询。
- ChatGPT → 远程 MCP 连接 → Secure MCP Tunnel 或受控公网 HTTPS 入口 → 本机 MCP/Gateway → 同一只读工具边界。
- Gateway 不引用 WPF 类型，不持有个人 SQLCipher 解密密钥，不因 AI 请求扩大 Worker 权限。
- 当前 MCP 只绑定 127.0.0.1:8765/mcp，并使用安装级 Bearer 凭证。ChatGPT 不能直接连接 loopback；远程使用必须完成 Tunnel 或受控 HTTPS 方案。
- Worker 的 .ai-public 专用管道和 Gateway 服务身份已有候选代码及隔离测试；2026-11-12 的本机核验仍记录已安装 Worker 未提供对应管道，候选 Gateway 查询返回 UpstreamUnavailable。不能把隔离测试写成正式 Worker 联通。

## 4. 查询执行顺序

1. MCP/Gateway 入口校验真实调用者身份、凭证、请求大小和连接来源。
2. 根据固定工具名校验 JSON Schema、证券范围、日期范围、分页和调用预算；不接受任意 operation、SQL、路径或用户自报身份。
3. 写入 Attempt 审计；审计失败时不访问 Worker。
4. 通过专用 IPC 和真实服务身份调用 Worker 二次授权的只读操作。
5. 校验快照、业务协议、质量状态、返回行数、字节数和时间范围；缺失或矛盾时失败关闭。
6. 释放结果前写入 Outcome 审计；审计失败时丢弃已取得的结果。
7. 模型将工具返回视为不可信事实材料，输出中区分事实、推断、反证和数据缺口。

当前审计只记录 requestId、时间、主体指纹、工具白名单名、阶段、决策和参数指纹，不记录原始提示词、工具参数、业务响应、附件正文或 API Key。现有本地文件写入器不等于防篡改存储；容量告警、轮转/归档、不可变备份、服务 ACL 和恢复演练仍是上线条件。

## 5. 数据与隐私安全域

| 数据级别 | 示例 | 首期策略 |
| --- | --- | --- |
| 公开且已发布 | ETF/市场聚合事实、公开披露摘要 | 通过白名单和质量门禁后可读 |
| 内部未审核 | 候选原件、内部诊断、待审修订 | 不外发原文；最多返回不含内容的状态 |
| 私人金融 | 用户持仓、交易、绩效 | 禁止由无人值守 Worker/MCP 读取 |
| 高敏感 | 税务原件、姓名/账户号、TOTP、主密钥 | 禁止外发；密钥永不进入模型上下文 |

附件是用户提供的不可信数据：不得执行其中的脚本、宏、SQL、命令或链接；不得让附件内容改变系统规则、工具白名单或权限。附件只作为当前用户消息的引用材料，不授予额外工具权限。

## 6. ChatGPT MCP 与 OpenAI API 的使用前提

**两种服务必须分开判断，不能混淆地区、套餐和计费。**

- OpenAI 当前帮助中心说明：ChatGPT Business、Enterprise/Edu 提供完整自定义 MCP；Pro 仅可连接 read/fetch；Plus 不在自定义 MCP 连接的可用套餐中。CRS 的附件 start/message/remove/end 会改变临时会话状态，属于写入/修改工具，因此不能按 Pro 只读权限验收，目标工作区需具备完整 MCP 权限。
- 自定义 MCP 连接目前仅支持 ChatGPT Web；本机 loopback 服务不能直接连接。官方说明建议为私有网络/本机 MCP 使用 Secure MCP Tunnel，或配置受控公网 HTTPS。
- OpenAI API 与 ChatGPT MCP 是不同入口。API 另行计费并受 API 支持地区列表约束；截至 2026-11-13，API 官方支持列表未列出中国大陆。若部署/使用场景涉及中国大陆，必须按官方当前列表和适用法规评估，不得通过代理、VPN 或固定 IP 伪装地区。
- OpenAI 当前 MCP 帮助页说明自定义 MCP 连接本身无单独地理限制；这不等于 API 地区限制自动豁免，也不代表 Tunnel、账号和组织政策一定可用。
- ChatGPT 订阅不等于 API 额度；不在设计文档中固定套餐价格。实际开通前重新核对套餐、工作区权限、模型费用和数据外发条款。

官方依据（本次核对：2026-11-13）：
- [ChatGPT 自定义 MCP 连接、套餐和权限](https://help.openai.com/en/articles/12584461-connecting-custom-mcp-servers-in-chatgpt)
- [OpenAI API 支持国家和地区](https://help.openai.com/en/articles/5347006-openai-api-supported-countries-and-territories)
- [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)
- [API 数据使用与保留](https://developers.openai.com/api/docs/guides/your-data)

## 7. 生产发布前置条件

| 阶段 | 必须完成的验收 | 未通过时 |
| --- | --- | --- |
| G0 身份与地区 | 目标账号/套餐、API 地区、组织权限、数据外发审批 | 不启用外部 API/MCP |
| G1 Worker 边界 | 专用服务 SID、Named Pipe ACL、Worker 二次授权、版本协商 | Gateway 不访问真实业务数据 |
| G2 审计 | 真实目录 ACL、容量/轮转、归档、备份、恢复、审计失败关闭 | 不释放查询结果 |
| G3 MCP 远程 | Secure MCP Tunnel 或 HTTPS、真实 ChatGPT 连接、工具权限、撤权 | 只保留本机测试 |
| G4 附件 | 真实账号 fileParams 上传/下载、恶意文件、超时、清理和保留边界 | 不宣称 ChatGPT 附件已端到端支持 |
| G5 生产运行 | 签名、密钥轮换、服务升级/回滚、费用预算、告警与运维手册 | 不发布生产服务 |

## 8. 反方审查

- **Gateway 是否扩大 Worker 权限？** 必须用独立服务身份和 Worker 二次授权证明；不能仅依赖 MCP Bearer token。
- **本机 MCP 是否等于 ChatGPT 已连接？** 否。loopback 不能被 ChatGPT 远程直接访问，必须有 Tunnel/HTTPS 和真实账号验收。
- **fileParams 是否证明真实上传成功？** 否。自动化测试使用假下载器，真实账号端到端仍开放。
- **AI 是否能读税务/持仓？** 首期不允许；不能因 WPF 登录或模型请求而绕过私有保险库会话。
- **本地 JSONL 是否是不可篡改审计？** 否。当前实现仍需 OS ACL、备份、容量管理和恢复验证。
- **CI 的 AI 专项通过是否代表发布通过？** 否。PR #35 的 AI 专项步骤通过，但同一工作流在 Worker 插件监督测试失败。

当前实现、测试命令和剩余风险以 [AI 双入口与附件实现](../implementation/AI_DUAL_ENTRY_POC.md) 及 [问题清单](../KNOWN_ISSUES.md) 为准。
