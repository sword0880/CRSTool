# 真实样本验收进展

2026-10-11。用户已确认 `sample` 下文件均为真实数据，并说明 IBKR 后续补充。现阶段使用 FUTU 2021—2025 年交易及收入 XLSX／PDF 原件；未把这些文件视为匿名或合成样本，原件只读且不复制到报告。

验证入口：

```powershell
dotnet run --project tests/CRS.Tests.Verification -c Release -- --verify-local-real sample/FUTU src/CRS.Desktop.Wpf/config artifacts/real-futu-acceptance
```

五年度均通过解析、规范化快照、SQLCipher 保存／读取、冻结结果恢复及同构建复算金额和 FIFO 匹配一致性检查。共读取 698 笔交易，原件验收前后 SHA256 一致。报告 `artifacts/real-futu-acceptance/local-real-samples.json` 只含年度、笔数、问题代码、状态、耗时和匿名原件索引／摘要，不含账户、原文件名或真实交易金额。数据存储验证使用独立临时加密保险库，不接触用户现有数据库。

当前每年单独验证，未假定期初为零或账户范围已完整，因此保持 Partial、预计补税为 null。没有独立人工金额基准，技术往返一致不能证明税额准确。2025 PDF 的 `INCOME_YEAR_UNCONFIRMED` 继续保留；不能由文件名推定收入年度已签认。本次耗时仅为本机单次观察，不是性能阈值验收。

独立金额验收仍使用 `--accept-real <manifest> <output>`：清单须提供完整年度／账户范围、期初依据、独立核对的金额和状态。`SourceKind` 支持 `real-private` 或实际已脱敏的 `real-anonymized`，必须按事实填写。该入口报告含核对金额，也属于私有资料；不应发布。缺失独立基准不会自动填写为程序输出。

IBKR 真实样本等待用户补充；既有合成回归继续执行。手机实机、跨电脑／Windows 用户恢复、断电／磁盘满演练、许可证及签名审核仍开放，`ReleaseApproved=false`。
