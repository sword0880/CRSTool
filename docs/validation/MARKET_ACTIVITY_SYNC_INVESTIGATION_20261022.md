# market-activity-sync 现场检查（2026-10-22）

后续修复、验证与本机部署记录见[成交历史补采续跑修复](../implementation/MARKET_ACTIVITY_RESUME_FIX_20261022.md)。本文保留修复前的现场证据。

范围：用户报告手动下载2026-01-01至2026-09-01成交记录始终未成功，并询问30分钟延迟原因。本次检查正式服务的只读管道、当前任务、执行检查点和源码；未重置来源策略、取消原请求、触发新采集或替换服务。该任务下载的是沪深市场每日股票成交额/成交量汇总，不是逐笔交易明细。

## 已确认的实际情况

- 原持久请求仍为 `2e4fc30c-a9d1-41c1-9c3e-f7a06ea407d3`。不能把重复点击或下一轮执行看成一个全新的下载请求。
- 执行 `0f8687c94e84495890ee1e8adb5318b5`：北京时间09:34:10开始，09:43:28结束，`RetryDeferred`；`days=274;sourceCalls=362;httpStarted=362;closedSources=186;emptySources=0;sourceIssues=1;rows=361;firstFailure=JsonReaderException;hasMore=False`。证明官方链路能取得并解析大量日记录，并非接口一直返回空数组。361是本轮解析数，包含可能重复的事实，不能称为361条新增、完整日期数或指定区间验收通过。
- 上述执行结束阶段为2026-10-01，274个自然日也与2026-01-01至2026-10-01一致；与用户描述的2026-09-01不同。当前公开回执不提供原始命令参数，本次不直接解密正式库读取参数，因此仅将执行日志显示的日期与用户目标分别记录。
- 10:13:28一轮：`httpStarted=2;rows=1;sourceIssues=2`；进度显示2026-01-05 SSE `RequestFailed`，2026-01-06 SSE冷却。这里是连接请求失败，而非已经证实的HTTP403/429或官方无数据。
- 10:43:33、11:13:33各轮仅约20毫秒，均为`httpStarted=0;rows=0;firstFailure=SourceCooldown`，从1月1日重新开始，处理4天休市后停在1月5日SSE；这些轮次没有实际请求官网。
- 11:41:02健康查询：服务Running；SSE为Cooldown，期限11:53:15；SZSE为Ready。此时间只是采样时的来源最早可用时间，其他任务失败可以延长它，不保证到时采集成功。
- 11:45:29当前任务：`official-share-sync`自11:42:02执行；`market-activity-sync`为Waiting/PluginBusy，插件队列第1位。同插件最大并发为1，因此来源退避到期后还可能等待ETF份额任务结束。
- 最终通过正式MarketActivity及修订查询取得用户目标区间2026-01-01至2026-09-01：244个自然日全部有登记，160个Complete、83个Closed、1个Incomplete；唯一缺口为**2026-02-26 SSE**，同日SZSE已有事实。市场有值日期数：SZSE 161、SSE 160。故不能将整请求未完成解释为整个区间没有数据。该缺口与长任务唯一解析失败相符，但早期错误原文/日志不足，不能直接证明该异常就是这个日期的响应，更不能断言官方该日无数据。

原始只读证据：

- `artifacts/market-activity-investigation-live-20261022.jsonl`：指定任务最近10次执行及每次最多128条进度。
- `artifacts/market-activity-investigation-health-20261022.jsonl`：健康、来源冷却、计划和最近执行。
- `artifacts/market-activity-investigation-activity-20261022.jsonl`：当前执行与队列。
- `artifacts/market-activity-coverage-final-20261022.json`：正式回读指定区间的完整/休市/缺口分类及对应市场。

## 30分钟的直接来源

`NationalTeamBackgroundEntry.cs`对手动成交采集返回`RetryDeferred`时固定给出`TimeSpan.FromMinutes(30)`。`EncryptedTaskRepository.End`将其持久化为`task.not_before`，并把同一个命令恢复成Pending/RetryDeferred。例：11:13:33结束后，下次尝试为11:43:33，正好30分钟。这是程序的重试间隔，不是官方响应要求；它可能早于官方来源冷却期，造成到点仍然无法请求。

当前源码`BackgroundRuntime.Activity.cs`的`ManualNotBefore`只对已经返回RetryDeferred的同一请求应用该期限；第一次受理的手动请求直接进入调度，仍须等待插件名额。安装程序集含该方法，已有安装记录及本次运行状态与此一致。已有Pending/Running请求时，新编号的重复RunNow返回AlreadyRunning，不会强制清除原期限。不能将页面展示的重试时间解释为每次点击都新增了30分钟延迟。

SSE多个域名在`SourceBudgetRegistry`合并到共享SSE预算。份额、成交和其他同源任务的连接失败、超时、403/429/5xx及特定来源校验失败都可能触发或延长冷却。403/429为6小时加抖动；一般连续失败采用5分钟起的指数退避、最高6小时加抖动。本次成交记录不足以认定发生了403/429。

## 确认的程序问题

1. **手动历史重试没有按已提交事实续采。** `SyncMarketActivityRangeAsync`每轮遍历原From/To，未读取已成功的市场/日期来跳过。一次局部失败就让整段重采；耗费官方请求额度，增加后续失败和冷却概率。官方数据成功返回与请求最终未完成可以同时成立。
2. **固定重试时间没有结合来源恢复期限。** 30分钟到期时仍可能处于SSE冷却，产生零HTTP的空跑；最后补发的Cooldown阶段还会覆盖有Until的阶段，使当前进度丢失恢复时间。
3. **一个市场的冷却会中断整轮。** SSE冷却时提前返回，SZSE即使Ready，也无法继续该天及后续日期。保护来源预算是必要的，但两市进度应分开保存。
4. **解析故障诊断不足。** `OfficialMarketActivitySource.FetchAsync`在解析前没有保存异常响应原件；工作区仅保留有限异常类型。`JsonReaderException`能确认返回内容未被成功解析为JSON，不能据此确认是HTML、截断、反爬或接口格式变更。长任务的进度仅保存最近512条，单次公开查询仅返回最近128条，早期失败可被后续成功进度挤出。因此本次无法还原09:34那一轮唯一失败的具体市场/日期及正文。
5. **正式成交覆盖查询存在可用性问题。** 对目标2026-01-01至2026-09-01，前两次正式MarketActivity查询均返回RequestTimedOut，第三次成功取得上述缺口清单。源码中该查询先进行全库`ValidateEvidence`，与采集共用仓储门槛，而服务查询期限为20秒；存在全库原件扫描和写任务竞争的开销，尚未用分阶段性能证据区分各自占比。不能将查询超时当作库内没有数据。

## 后续整改范围

以原请求区间为准，持久化每个市场/日期的完成和失败情况，只重试缺口；保留显式“重新核验已有数据”的操作。休市以核验日历为准，空响应、缺日历、失败与成功不能混为一类。没有失败但仍待分段的续采与来源失败退避分开处理。

重试期限应使用实际所需来源的恢复时间，保留Until；SSE冷却不阻止SZSE补齐。失败检查点和受控原件诊断需独立于滚动进度保留。覆盖查询按本次成交事实及对应日历/原件核验，避免无关ETF原件参与，并继续保留证据损坏时停止给出完整结论的约束。

本轮只新增只读`CRS.Tests.WorkerEtf --market-coverage-probe <from> <to>`诊断入口，并完成Release构建（0警告/0错误）及差异格式检查；未修改采集、调度或正式部署行为。工具遇到服务超时输出Unavailable，不输出零缺口或空覆盖的成功结果。实际运行已成功取得160完整/83休市/1缺口的结果。既有手动立即执行专项11项断言全部通过，证据为`artifacts/market-activity-manual-immediate-check-20261022.log`；这验证当前源码的调度规则，不代替正式采集验收。本次源码检查和读取记录已经确认上述重试缺陷，2026-02-26 SSE补齐、修复后的正式服务回执和完整覆盖验收仍未完成。
