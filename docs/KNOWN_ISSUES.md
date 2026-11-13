# 当前问题与验收缺口

> 更新：2026-11-13。只保留仍未关闭的问题。历史过程记录可通过 Git 历史追溯；各问题关闭前必须附带当前 head 的测试或正式环境证据。

## P0 — 阻止正式发布

| 编号 | 当前问题 | 关闭条件 / 证据入口 |
| --- | --- | --- |
| P0-01 | 程序代次、数据代次与 Schema 升级/恢复的完整生产闭环仍需实机验证。 | 覆盖真实 Windows SCM 安装、升级、卸载、重装、各插件 Schema、进程强杀、断电和磁盘满；不得静默回退新事实。见 [Worker 升级与恢复](deployment/WORKER_UPGRADE_AND_RECOVERY.md)。 |
| P0-02 | ServiceKey 跨机器/服务身份灾备恢复尚未形成完整生产闭环。 | 完成可信授权导出、目标身份重封装、全库只读验证、显式激活及两台干净 Windows 机器演练。见 [ServiceKey 恢复设计](architecture/WORKER_SERVICE_KEY_RECOVERY.md)。 |
| P0-03 | 税务金额独立正确性验收未完成。 | 建立独立 Golden Dataset，覆盖公司行动、费用、币种、FIFO、结转和边界案例；完成独立人工金额签认。见 [税务规则规范](specifications/tax/README.md)、[真实样本进展](validation/REAL_SAMPLE_PROGRESS.md)。 |
| P0-04 | 公开版安全发布门槛未关闭。 | 完成生产签名、依赖许可证/NOTICE、隐私告知、发行包敏感扫描、真实跨设备恢复和离线安全验收。见 [安全规范](specifications/security/README.md)。 |

## P1 — 业务验收与运行可靠性

| 编号 | 当前问题 | 关闭条件 / 证据入口 |
| --- | --- | --- |
| P1-01 | 最近 Windows 工作流未全绿：PR #35 对应 run 43366766980 在 Worker 插件监督/持久执行测试失败。 | 修复或解释 SupervisedPluginChecks 的 TaskCanceledException；在最新 main head 重跑完整 Windows 工作流，确认后续步骤全部执行。失败日志见 [run 43366766980](https://github.com/sword0880/CRSTool/actions/runs/43366766980)。 |
| P1-02 | 最新 main 代码与最后已确认的正式 Worker 安装版本不一致。 | 获取新 head 对应的正式安装回执，验证版本、服务身份、IPC 协议、数据保全和回滚；在此之前不得把 main 代码视为已部署。最后已知正式代次记录于 2026-10-31。 |
| P1-03 | NationalTeam 公开数据覆盖、ETF 披露审核和市场模型有效性仍不足。 | 重新核验官方日历、250 日历史、连续 20 日、报告原件/修订、具名持仓、点时历史和样本外回测；10-31 的 11/24、0/24、66 行 NeedsReview、0 行已核验持仓只作为历史快照。 |
| P1-04 | 投资绩效缺少独立真实金额对账。 | 补齐真实期初净值、价格、现金流/转仓和原币逐项容差，独立核对 TWR/XIRR。见 [绩效验收](validation/PERFORMANCE_ACCEPTANCE.md)。 |
| P1-05 | AI 双入口已进入 main，但远程连接和生产安全边界未验收。 | 完成目标地区/账号策略核验、远程 HTTPS 或 Secure MCP Tunnel、真实 ChatGPT 工具调用、撤权、服务账户/ACL、审计归档与备份恢复；附件 fileParams 必须真实账号端到端通过。见 [AI 架构](architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) 和 [AI 实施记录](implementation/AI_DUAL_ENTRY_POC.md)。 |
| P1-06 | AI 附件首版格式和多用户边界有限。 | 当前只支持 UTF-8 TXT/MD/CSV/JSON；PDF/DOCX/XLSX/OCR 需单独做资源和安全审查；多用户部署前需用户级身份、独立会话所有权和撤权。 |
| P1-07 | Worker 长期负载、查询竞争、维护排空和真实源站恢复仍需持续验收。 | 提供跨日负载、并发/取消/排空、磁盘容量、服务重启和源站恢复证据；单次样本成功不等于容量通过。见 [查询性能](implementation/QUERY_PERFORMANCE.md)、[共享来源预算](architecture/SHARED_SOURCE_BUDGETS.md)。 |

## 反方检查

- 代码已合并 ≠ 已部署。
- 编译通过 ≠ 全部测试通过。
- 单项专项通过 ≠ 完整工作流通过。
- Worker 进程健康 ≠ 金融资料完整。
- ETF 份额变化 ≠ 国家队真实交易。
- AI 能读取公开数据 ≠ AI 可读取个人税务/绩效数据。
- 文件解析成功 ≠ 文件内容可信；附件始终是不可信输入。
- 测试夹具、合成数据和研究副本不能替代正式原件与独立业务签认。

整改任务编号、执行顺序和通用验收要求见 [架构整改计划](architecture/ARCHITECTURE_REMEDIATION_PLAN.md)。发布判断见 [发布验收](validation/RELEASE_ACCEPTANCE.md)。
