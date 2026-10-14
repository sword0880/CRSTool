# 应用接口说明

`ExportEncryptedAsync(path,result,fingerprint,password,cancellation)` 沿用冻结结果、汇率指纹与会话取消门槛，输出独立密码 `.crsexport`；`RestoreEncryptedAsync(path,parentDirectory,password,cancellation)` 返回新建目录，不改写数据库。`IReportExporter` 对应加密导出／恢复端口由 Infrastructure 实现，Security 不进行文件 I/O。格式、密码与大小限制见 [加密底稿与恢复](../guides/ENCRYPTED_EXPORT.md)。

当前为本地 C# 调用接口，不是 HTTP API。未完成能力见 [当前问题](../KNOWN_ISSUES.md)。

## 前台入口

[IDesktopUseCases](../../src/CRS.Application/Abstractions/IDesktopUseCases.cs) 是 WPF 页面使用的接口。

| 成员 | 行为与边界 |
| --- | --- |
| CalculateAsync(request, cancellation) | 后台导入、校验、计算；保存前检查取消；返回结果和配置指纹 |
| HistoryAsync / LoadAsync | 读取索引／冻结结果，不自动重算 |
| QueryHistoryAsync(pageNumber, pageSize) | 查询分页索引和总数，页码从 1 开始，单页最多 100 份 |
| OriginalFileRequirementsAsync / VerifyOriginalFilesAsync | 查询报告和期初原件要求，并按历史摘要校验；不改写冻结结果 |
| ReplayAsync(id, cancellation) | 使用冻结口径复算，业务输出比对通过后另存并保留父运行 |
| AggregateAnnualAsync(sourceIds, ownershipConfirmed, cancellation) | 确认同一纳税人后汇总 2—100 份同年度来源任务，冻结汇率及原币事实统一计税，另存来源关系 |
| ReviewsAsync / SaveReviewAsync | 读取或保存人工依据；不修改原税额，不自动解除成本问题 |
| ExportAsync(path, result, fingerprint, cancellation) | 新计算校验当前配置指纹；冻结历史沿用原汇率；响应取消及会话失效，完成暂存后原子提交 |
| ExportCarryAsync(path, result, cancellation) | 仅 CarryEligible 结果允许写结转；响应取消及会话失效，失败保留原目标 |
| DownloadAsync | IBKR Flex 下载；支持取消 |
| RatesAsync(year) | 枚举已配置年度汇率 |
| Trades(result) | 优先会话交易；完整快照可恢复历史交易，引用型历史不虚构明细 |
| Sections(result, reviews) | 返回报表展示分组，不在前台重新聚合金额 |
| ConfigPath / ReadQueryId / LogFailure | 配置位置、查询 ID、脱敏异常类型 |

DesktopCalculationRequest 包含文件、年度、券商、期初与范围确认、结转／期初路径及完整快照保存选项。DesktopCalculationOutcome 包含 Result 和 RatesFingerprint。前台持有取消令牌和忙碌状态，不取得仓储。

请求增加可选 `AccountPeriodsPath`；后台 `IDesktopOperations.ReadAccountPeriods(path,cancellation)` 只读、有界加载凭证并返回规范化声明。`CalculationService.Calculate` 增加可选 `accountPeriods`，执行账户生命周期、归属、缺口及矛盾检查；见 [账户期间](../guides/ACCOUNT_PERIODS.md)。

`IVaultService.BackupAsync`／`RestoreAsync` 增加可选取消令牌。系统锁定在等待门闩前取消操作代次和会话，旧排队任务不能借重新解锁复活；见 [锁定中断](../implementation/LOCK_INTERRUPTION.md)。

## 本机设置端口

Application/Abstractions 中的 IBrokerImporter、IExchangeRateProvider、ICarryValidator、ICalculationEvidence、ICalculationRepository 和 IReportExporter 由 Infrastructure 实现。IDesktopOperations 仅用于后台装配，不供 ViewModel 调用。

IBrokerImporter.Parse 必须用一次冻结的原件字节解析并生成 SourceReport.Sha256。CalculationService 的活动／期初快照引用只采用该解析来源摘要；同名不同目录、缺失或冲突摘要拒绝，不在计算末尾重新读取原件。IBKR 同一路径重复选择先去重；富途继续执行唯一收入和重叠报告限制。详见 [导入一致性](../implementation/INPUT_CONSISTENCY.md)。

新快照生成、Replay、历史 Trades、原件要求查询和原件校验统一使用 CanonicalInputReader 校验版本、摘要、结构、读取上限及冻结归属。损坏或超限内容返回中文 CrsException，不能静默返回空明细或靠默认零值补全记录；前台可保留冻结汇总并显示明细不可读取的原因。详见 [快照读取校验](../implementation/CANONICAL_VALIDATION.md)。

## 计算与重放

带账户期间依据时，完整重放契约为 v3；不带依据继续支持 v2。下面历史 v2 的门槛同样适用于 v3，重放额外比较期间声明及覆盖输出。

CalculationService.Calculate 是同步后台服务，由 DesktopWorkflow 安排后台执行；Replay 和 VerifyOriginalFiles 已通过应用用例接入 WPF 历史操作。

Replay 要求 v2 完整规范化快照、输入摘要一致及 V3 排序口径。历史汇率缺失、重复、错年度或无效时拒绝重放；沿用历史配置指纹与临时状态。复算后比较金额、LOT、舍入余差、来源、问题、状态和所得原币事实，不一致不保存。新结果冻结并保存三个计算程序集摘要及业务载荷证据，已有封存构建不同则拒绝复算。业务库 v2 保存新任务的三个程序文件及关系，读取校验；旧记录不补造归档，旧格式转换与完整执行环境仍待完善。见 [冻结结果与运行证据](../implementation/FROZEN_EVIDENCE.md)及[数据库升级](../implementation/DATABASE_UPGRADE.md)。

## 错误与未知值

普通 `ExportAsync`、`ExportCarryAsync` 及 `RestoreEncryptedAsync` 增加可选 `SensitiveExportConsent`，缺省表示未确认明文、后台拒绝；界面只有在用户明确确认后提供本次声明。加密导出不要求明文确认。`SensitiveDataExportService` 统一执行目的地、汇率、结转、会话取消和固定事件策略；新后台端口为 `ValidateExportDestination` 与 `LogExportEvent`，见 [敏感导出](../implementation/SENSITIVE_EXPORT.md)。

业务失败使用 CrsException；取消使用 OperationCanceledException。资料不足可产生带 Issues 的部分结果。未知收益、差额与预计补税使用可空 decimal；不得在展示层补成零。

C# 实际签名以源码为准。本文件不声明尚未实现的旧提案模型或新的远程 API。


## 绩效用例

IPerformanceUseCases 独立提供 Load / Import / ImportManual / SavePortfolio / RenameAccount / ConfirmBaseCurrency / SetSourceIncluded / ReviseClassification / ConfirmTransfer / Calculate / OpenRun / Replay / Export。全部绑定原保险库会话并接受取消；不以税务 CalculationResult 作为绩效输入。ConfirmTransfer 冻结容差币种，Replay 冻结原输入及规则，Export 使用统一敏感导出策略。模型、限制和失败边界见[绩效当前实现](../implementation/PERFORMANCE_P0.md)。
