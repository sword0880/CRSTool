# 历史验证执行叙述

> 原文保留于 2026-10-13；当前门槛与矩阵见 [验证规范](../validation/TEST_SPEC.md)。

# 验证与验收规范

2026-10-13 P0 续修：六组控制台回归、原有 68 个综合场景、架构检查及 WPF 隔离窗口冒烟通过；账户期间依据的加密持久化和 Excel 底稿另有专项通过。IBKR 五组合／FUTU 五年度技术报告和窗口截图在 `artifacts/p0-followup-acceptance`。本轮未重新收集覆盖率与性能基准，不沿用旧百分比作为新代码覆盖率。详见 [本轮 P0 状态](../validation/P0_FIX_PROGRESS.md)。

2026-10-13 最新整体产物为 `artifacts/resource-protection-acceptance`。新增 `ResourceProtectionChecks` 覆盖读取前字节拒绝、深度／重复字段／集合／文本限制、超限保存不留任务、异常分块容器拒绝、超过 128 MiB 加密库恢复及旧加密 JSON 恢复；WPF 覆盖容量接近／超限提示。边界见 [分块备份与容量保护](../implementation/BACKUP_CAPACITY.md)。

此前 `artifacts/schema-protection-acceptance` 保留：`SchemaUpgradeChecks` 验证 v1→v2 前加密备份、失败回滚、标准恢复、旧结果保留、执行文件损坏与密钥隔离；金额显示由 `TaxPresentationChecks`、`TaxAmountViewChecks` 和 Excel `ExportChecks` 检查未知值、零值及原始公式与预计值的区别。最新整体回归仍执行这些检查。边界见 [数据库升级](../implementation/DATABASE_UPGRADE.md) 与 [金额展示](../implementation/AMOUNT_PRESENTATION.md)。

加密底稿专项由 Security 的 `PasswordExportChecks`、Infrastructure 的 `EncryptedExportChecks` 和 WPF 的 `EncryptedReportsInteractionChecks` 覆盖密码／用途隔离／完整性、路径与重复文件名拒绝、正常恢复、取消／锁定保留原密文、交互门槛及密码清理。此前产物为 `artifacts/encrypted-export-acceptance`，实施与限制见 [ENCRYPTED_EXPORT.md](../guides/ENCRYPTED_EXPORT.md)。

结果冻结专项由 `FrozenEvidenceChecks` 检查集合、LOT、元数据防改写、持久往返和固定证据编码；历史 SQL 损坏由 `HistoryPagingChecks` 验证读取拒绝。冻结专项产物为 `artifacts/frozen-evidence-acceptance`，新整体产物包含这些检查。真实私有 FUTU 五年度及 IBKR 五种组合的技术验收独立于人工金额签认，见 [进展与命令](../validation/REAL_SAMPLE_PROGRESS.md)。

手机认证专项当前证据与人工缺口见 [AUTH_IMPLEMENTATION.md](../guides/AUTH_IMPLEMENTATION.md)：RFC6238 向量、首次绑定、受限会话、并发防重放、恢复码消费、换机取消、密码恢复强制绑定、时间回退、持久冷却和原有业务回归均有自动检查；手机实机、跨电脑及系统故障测试不得以合成检查替代。

本文列出当前可执行检查和验收边界；[未完成的工作](../KNOWN_ISSUES.md)单独维护。

快照读取专项：`CanonicalInputChecks` 覆盖合法完整／引用快照及 22 类损坏／超限输入在三个后台入口统一拒绝；`CanonicalViewChecks` 检查明细失败不沿用旧交易、中文原因及冻结汇总保留。检查限制与未完成范围见 [快照校验说明](../implementation/CANONICAL_VALIDATION.md)，产物在 `artifacts/canonical-validation-acceptance`。

导入一致性专项新增 `ImportConsistencyChecks`：真实 XML／期初解析后替换删除、同源摘要与无原件复算、只读内存及独立数组副本、Excel／PDF 原路径删除后读取、空文件／超限／取消、同名和缺失／冲突摘要拒绝。正常券商解析、金额及复算继续通过原有回归；产物在 `artifacts/input-consistency-acceptance`，详见 [实现边界](../implementation/INPUT_CONSISTENCY.md)。

导出专项：应用层检查预取消与会话失效不调用输出适配器；基础设施检查 Excel 行构建取消、分块写入取消、写入异常、提交前取消、新目标失败无文件、原目标不变、临时文件清理，以及实际保险库写入暂停时锁定及时完成、旧会话禁止提交。WPF 检查两类取消命令、忙碌门槛、保留当前结果及取消不误报成功。Excel 与 LOT 结转成功后读取内容校验；故障注入为合成，不替代断电与真实磁盘满演练。
