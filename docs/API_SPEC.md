# 应用接口说明

当前为本地 C# 调用接口，不是 HTTP API。未完成能力见 [当前问题](KNOWN_ISSUES.md)。

## 前台入口

[IDesktopUseCases](../src/CRS.Application/Abstractions/IDesktopUseCases.cs) 是 WPF 页面使用的接口。

| 成员 | 行为与边界 |
| --- | --- |
| CalculateAsync(request, cancellation) | 后台导入、校验、计算；保存前检查取消；返回结果和配置指纹 |
| HistoryAsync / LoadAsync | 读取索引／冻结结果，不自动重算 |
| QueryHistoryAsync(pageNumber, pageSize) | 查询分页索引和总数，页码从 1 开始，单页最多 100 份 |
| OriginalFileRequirementsAsync / VerifyOriginalFilesAsync | 查询报告和期初原件要求，并按历史摘要校验；不改写冻结结果 |
| ReplayAsync(id, cancellation) | 使用冻结口径复算，业务输出比对通过后另存并保留父运行 |
| AggregateAnnualAsync(sourceIds, ownershipConfirmed, cancellation) | 确认同一纳税人后汇总 2—100 份同年度来源任务，冻结汇率及原币事实统一计税，另存来源关系 |
| ReviewsAsync / SaveReviewAsync | 读取或保存人工依据；不修改原税额，不自动解除成本问题 |
| ExportAsync(path, result, fingerprint) | 新计算校验当前配置指纹；冻结历史沿用原汇率 |
| ExportCarryAsync(path, result) | 仅 CarryEligible 结果允许写结转 |
| DownloadAsync | IBKR Flex 下载；支持取消 |
| RatesAsync(year) | 枚举已配置年度汇率 |
| Trades(result) | 优先会话交易；完整快照可恢复历史交易，引用型历史不虚构明细 |
| Sections(result, reviews) | 返回报表展示分组，不在前台重新聚合金额 |
| ConfigPath / ReadQueryId / LogFailure | 配置位置、查询 ID、脱敏异常类型 |

DesktopCalculationRequest 包含文件、年度、券商、期初与范围确认、结转／期初路径及完整快照保存选项。DesktopCalculationOutcome 包含 Result 和 RatesFingerprint。前台持有取消令牌和忙碌状态，不取得仓储。

## 本机设置端口

Application/Abstractions 中的 IBrokerImporter、IExchangeRateProvider、ICarryValidator、ICalculationEvidence、ICalculationRepository 和 IReportExporter 由 Infrastructure 实现。IDesktopOperations 仅用于后台装配，不供 ViewModel 调用。

## 计算与重放

CalculationService.Calculate 是同步后台服务，由 DesktopWorkflow 安排后台执行；Replay 和 VerifyOriginalFiles 已通过应用用例接入 WPF 历史操作。

Replay 要求 v2 完整规范化快照、输入摘要一致及 V3 排序口径。历史汇率缺失、重复、错年度或无效时拒绝重放；沿用历史配置指纹与临时状态。复算后比较金额、LOT、舍入余差、来源、问题、状态和所得原币事实，不一致不保存。旧快照仍可查看；执行程序集封存及旧格式转换仍待完成。

## 错误与未知值

业务失败使用 CrsException；取消使用 OperationCanceledException。资料不足可产生带 Issues 的部分结果。未知收益、差额与预计补税使用可空 decimal；不得在展示层补成零。

C# 实际签名以源码为准。本文件不声明尚未实现的旧提案模型或新的远程 API。
