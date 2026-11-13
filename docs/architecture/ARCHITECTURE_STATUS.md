# 当前架构状态

> 状态快照：2026-11-13。本文是当前状态汇总，不是逐日开发日志。历史证据保留在 Git 提交和对应专项验证文档中；任何没有可复核回执的事项均标记为未验收。

## 1. 仓库与近期合并

- 当前分支：main；核对基线：b0acb9c6f2c29f554f4e3647e895b4f99bf096f4。
- PR #34：助手截图、输入区和导航体验优化，已合并。
- PR #35：WPF/MCP 统一附件支持，已合并。
- PR #36：同步 AI Gateway、MCP 和附件文档状态，已合并；该 PR 仅修改 Markdown。
- AI 只读查询与 Gateway 基础分别经 PR #31、#32、#33 合并。当前代码结构见 [当前架构](CURRENT_ARCHITECTURE.md)。

## 2. AI 当前实现状态

| 能力 | 已进入 main | 尚未验收/限制 |
| --- | --- | --- |
| 双入口只读工具 | MCP 与 OpenAI 适配器共享工具契约、查询、授权和审计；当前公开研究工具为 ETF overview、coverage | 不代表所有目标工具均已开放；当前 Worker 部署是否包含对应 AI 专用管道须单独核验 |
| Gateway | 本机 Windows 身份、授权/撤销、受限 Worker 管道、两阶段审计和本地审计文件实现 | 正式生产安装、审计留存/备份恢复、跨用户身份体系及长期负载未完整验收 |
| MCP | Streamable HTTP、Bearer 认证；当前监听 127.0.0.1:8765/mcp | ChatGPT 不支持直接连接 loopback；远程 HTTPS/Tunnel、真实账号连通和撤权演练未验收 |
| 模型适配 | WPF 助手、Responses API 入口及已接入的供应商适配代码 | 真实供应商账号、地区/数据跨境合规、费用预算和生产密钥管理须按部署环境验收 |
| 附件 | WPF/MCP 共用解析器、文件限制、内存会话和 MCP openai/fileParams 适配 | 真实 ChatGPT 上传/下载端到端未验收；只支持 UTF-8 TXT/MD/CSV/JSON |
| 隐私 | 原始附件和解析正文不写入 CRS 聊天历史、临时文件或日志 | ChatGPT 侧文件保留由 ChatGPT 自身数据控制管理，CRS 无法删除其云端原件 |

附件具体规则、命令和测试见 [AI 双入口与附件实现](../implementation/AI_DUAL_ENTRY_POC.md#ai-附件处理)。

### 最近 CI 证据

PR #36 对应 [run 43386238746](https://github.com/sword0880/CRSTool/actions/runs/43386238746) 是 main 代码基线的完整 Windows UI 验证；PR #37 文档分支的最新 [run 43397935610](https://github.com/sword0880/CRSTool/actions/runs/43397935610) 在恢复项目清单后再次验证，结果相同。PR #35 的 [run 43366766980](https://github.com/sword0880/CRSTool/actions/runs/43366766980) 也有同类失败：

- PR #37 的 Restore、Release Build、AI MCP/OpenAI 工具一致性、MCP 附件 fileParams/内存会话集成、项目依赖/架构检查及此前多项专项检查均通过。
- 工作流在 “Verify supervised plugin fault isolation and durable execution” 步骤失败：模拟 Runner 崩溃后已记录 `RunnerOutcomeUnknown`，但等待持久命令最终回执时，10 秒期限到达并抛出 `TaskCanceledException`（`SupervisedPluginChecks.cs:93/108`）。
- Worker PluginRunner 独立进程/项目图工作流 [run 43397935496](https://github.com/sword0880/CRSTool/actions/runs/43397935496) 通过；它不替代上述集成监督与持久命令验收。
- 后续步骤被跳过。不能将完整工作流或插件监督/持久执行验收标记为通过。PR #37 仅修改文档，没有修改业务源码。

## 3. Worker 与 NationalTeam 最近可确认状态

**重要：以下正式运行数据最后确认于 2026-10-31，不是 2026-11-13 的实时检查。** main 在其后继续合并代码，但没有在本次文档核对中取得新的正式 Worker 安装回执。

- main 已将每插件 `CRS.Worker.PluginRunner` 接入 Worker Service 组合根，通过 `IsolatedPluginSession` 运行受信插件；这是源码状态，不代表已安装服务已更新。最后记录的正式 Worker 代次为 c84d1bf74522495fa91a4cf4ce3a272b，2026-10-31 19:58:15 Committed；当时 PID 42984 / Running / Auto / LocalService。此信息只能作为最后已知基线。
- 当时完成新旧程序、6051 份快照、原密钥摘要、7 项计划、24 只 ETF 配置、29 条股票投影和 10 条旧执行的保全核对。
- ETF 资料完整性仍不满足发布：最近记录 24 只中 11 只达到 250 个有效交易日，连续 20 日通过为 0/24；报告原件和修订审核仍有缺口。
- 具名 ETF 披露：当时已登记原件中存在 66 行 NeedsReview；已核验具名持仓为 0 行，实际持仓未知。NotDisclosed 不等于零持仓或清仓。
- 510050 的 2025 年报原请求仍为 Pending/RetryDeferred；研究副本解析不能替代正式原件登记和审核。
- 行业 PE、NAV、市场周期、广度和联合退出研究均有独立功能，但真实历史覆盖、时间点一致性、样本外有效性和完整金融验收未关闭。

详细机制与证据分别见 [NationalTeam 模块架构](../implementation/NATIONAL_TEAM_ARCHITECTURE.md)、[ETF 证据](../implementation/NATIONAL_TEAM_EVIDENCE.md)、[股票持仓](../implementation/NATIONAL_TEAM_EQUITY.md)、[市场研究](../implementation/NATIONAL_TEAM_MARKET_RESEARCH.md)。

## 4. 发布与主要风险

- 当前发布结论：**ReleaseApproved=false / No-Go**。
- 税务计算独立 Golden Dataset、独立人工金额签认和完整真实案例复核仍未完成。
- 投资绩效的真实期初净值、价格、现金流/转仓完整性及独立金额对账仍未通过。
- Worker 程序代次/Schema 升级、ServiceKey 跨机器/服务身份恢复、生产签名、断电/磁盘满和真实安装生命周期仍有未关闭门槛。
- 插件 Runner 监督测试在最近工作流失败；不能以单项隔离输出通过替代完整生命周期验收。
- NationalTeam 的真实来源覆盖、ETF 披露审核、具名持仓、市场周期样本外验证和正式通知仍未完成。
- AI 外部连接、ChatGPT 真实 fileParams、生产 Gateway/Worker 配套部署、用户级身份和数据外发审查仍未完成。

当前可执行事项和关闭条件见 [问题清单](../KNOWN_ISSUES.md)；发布门槛见 [发布验收](../validation/RELEASE_ACCEPTANCE.md)。
