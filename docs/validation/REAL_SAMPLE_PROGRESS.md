# 真实样本验收进展

2026-10-13 P0 续修复验：FUTU 五年度、IBKR 五种组合的只读导入、加密往返与同构建复算通过，原件摘要不变；报告在 `artifacts/p0-followup-acceptance`。账户期间凭证入口已接入，但不从样本起日自动确认真实开户事实。用户确认暂时没有独立计算记录，独立税额签认仍未通过，`ReleaseApproved=false`。

2026-10-13。用户已确认 `sample` 下文件均为真实数据，现已提供 FUTU 2021—2025 年交易及收入 XLSX／PDF，以及 `Sample/IBKR` 的三份真实 XML。未把这些文件视为匿名或合成样本，原件只读且不复制到报告。

验证入口：

```powershell
dotnet run --project tests/CRS.Tests.Verification -c Release -- --verify-local-real sample/FUTU src/CRS.Desktop.Wpf/config artifacts/real-futu-acceptance
```

五年度均通过解析、规范化快照、SQLCipher 保存／读取、冻结结果恢复及同构建复算金额和 FIFO 匹配一致性检查。共读取 698 笔交易，原件验收前后 SHA256 一致。报告 `artifacts/real-futu-acceptance/local-real-samples.json` 只含年度、笔数、问题代码、状态、耗时和匿名原件索引／摘要，不含账户、原文件名或真实交易金额。数据存储验证使用独立临时加密保险库，不接触用户现有数据库。

当前每年单独验证，未假定期初为零或账户范围已完整，因此保持 Partial、预计补税为 null。没有独立人工金额基准，技术往返一致不能证明税额准确。2025 PDF 的 `INCOME_YEAR_UNCONFIRMED` 继续保留；不能由文件名推定收入年度已签认。本次耗时仅为本机单次观察，不是性能阈值验收。

独立金额验收仍使用 `--accept-real <manifest> <output>`：清单须提供完整年度／账户范围、期初依据、独立核对的金额和状态。`SourceKind` 支持 `real-private` 或实际已脱敏的 `real-anonymized`，必须按事实填写。该入口报告含核对金额，也属于私有资料；不应发布。缺失独立基准不会自动填写为程序输出。

## IBKR 新增验证

`IBKR_2025.xml` 的正文期间为 2025-06-17 至 2025-12-31，与用户年中开户的说明相符。共 146 条成交，其中 43 条股票、103 条外汇；外汇成交不纳入股票 FIFO，保留 `UNSUPPORTED_ASSET`。其他费用保留 `UNSUPPORTED_CASH`，不会自动抵扣。开户前的期间缺口不会仅凭说明自动补零，期初与账户范围仍待确认。

`flex.1664545.2025TotalYear.20261009.20261009.xml` 的正文期间仅为 2026-10-09，当日没有成交。不能按文件名中的 `2025TotalYear` 认定年度或全年覆盖，也不能用于证明 2026 年其他日期没有交易。

用户随后补充的 `IBKR_2026.xml` 为主要 2026 验证原件，正文期间为 2026-01-01 至 2026-10-09，共 100 条成交（24 条股票、76 条外汇）及 30 条现金明细。单独导入有 `MISSING_COST`，券商收益核对为 3／4 一致；联合 2025 历史后该成本提示消失，券商收益 4／4 一致、现金 2／2 一致，FIFO 匹配为 9 条。该核对依据券商原始记录，不等于独立税额签认。

```powershell
dotnet run --project tests/CRS.Tests.Verification -c Release -- --verify-ibkr-real Sample/IBKR src/CRS.Desktop.Wpf/config artifacts/ibkr-real-acceptance
```

五种组合（2025、2026 年初至今、年初至今加 2025 历史、单日、单日加 2025 历史）均通过解析、SQLCipher 保存／读取、冻结快照复算与 FIFO／收益／现金核对一致性验证，原件摘要不变。2025 得到 16 条匹配，券商收益 10／10、现金 2／2 一致；2026 单日两种组合均没有当年成交及匹配。2026 四种组合保留临时测算标志。五种组合均为 Partial，预计补税为空，保留 `INCOMPLETE_PERIOD`，未将技术通过当作金额正确。报告 `artifacts/ibkr-real-acceptance/ibkr-real-samples.json` 只记录摘要、期间、计数与状态，不含身份、原件文件名或金额。

发布脚本新增 `-IbkrRealDirectory Sample/IBKR`，单独记录 `IbkrRealSampleTechnical`；FUTU 检查不再宣称 IBKR 未提供。仍需覆盖开户后完整期间的依据、2026 年 10 月 9 日之后的资料、期初与账户范围确认和独立人工金额基准。手机实机、跨电脑／Windows 用户恢复、断电／磁盘满演练、许可证及签名审核仍开放，`ReleaseApproved=false`。
