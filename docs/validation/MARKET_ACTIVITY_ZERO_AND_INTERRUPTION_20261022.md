# 成交统计日志零计数与中断诊断（2026-10-22 晚间）

范围：对应用户截图最后进度序号1224、2025-08-18 SZSE HTTP 200、旧执行显示中断。本次只读查询正式服务的执行、健康、活动、已有请求回执及Windows事件；未提交新采集、取消任务、重启或替换服务，未直接打开正式数据库，也未调用可能触发全库扫描的插件业务查询。

## 已确认：日汇总的零计数不代表一直没取得数据

截图对应执行 `f0fcd5dd39d04115bebf3242f37ed1df`，20:42:52.619开始。

| 时间（北京时间） | 日期/市场 | 实际持久阶段 | Received |
| --- | --- | --- | --- |
| 20:50:26.637 | 2025-08-14 SZSE | SourceParsed，序号1207 | 1 |
| 20:50:26.639 | 2025-08-14 | DayCommitted，序号1208 | 0 |
| 20:50:29.674 | 2025-08-15 SZSE | SourceParsed，序号1214 | 1 |
| 20:50:29.676 | 2025-08-15 | DayCommitted，序号1215 | 0 |
| 20:50:29.682 | 2025-08-16 | DayCommitted，序号1217 | 0 |
| 20:50:29.688 | 2025-08-17 | DayCommitted，序号1219 | 0 |

源码 `Application/MarketActivityWorkspace.cs` 在 `SourceParsed` 中填入实际收到的市场日记录数，但 `DayCommitted` 仅传阶段和日期，Received/Added/Duplicates/Pending均保留结构默认值0。桌面 `ExecutionLogViewModel.DescribeProgress` 将这些默认值统一翻译为“收到0条；新增已接受0条，重复跳过0条，新增待审核0条”。成交统计保存接口返回void，也没有为该阶段提供完整新增/重复/审核统计，因此不能把这些字段当作实测零值。

2025-08-16和17日分别是周六、周日，截图中这两天没有来源请求阶段；代码对已核验休市日或已存在有效事实会跳过下载。仅靠两个0无法区分休市、复用既有数据或其它结果。前面的交易日已有SourceParsed=1而DayCommitted=0，足以确认统一日汇总文案失真。这些行数也不是成交额或成交股数。当前证据证明收到/解析了市场日记录，不据此声称整个区间已完整或每条均为新增。

原始证据：`artifacts/market-zero-interrupted-history-20261022.jsonl`。

## 已确认：插件查询取消失效触发整个Worker退出

- 20:50:32.656：旧执行最后保存序号1224，为2025-08-18 SZSE收到HTTP 200响应头。没有后续正文校验、解析成功或该日提交阶段，HTTP 200不能作为该日完成证据。
- 20:50:47.123：服务日志 `PluginLifecycle / crs.national-team / Query.CancellationGraceExceeded`。
- 20:50:47.133：服务日志 `PluginUnresponsive / service / CancellationGraceExceeded`。
- 20:50:47.268：Windows系统事件7031确认“小财牛后台服务”意外终止，并配置60秒后重启。
- 20:53:08.441：新进程打开任务仓储时，将遗留Running执行标记为Interrupted；截图的“记录结束”时间是恢复整理时间，不是原进程实际退出时间。
- 20:53:12.449：服务日志重新Started/Running，新执行在20:53:12.635启动。

源码 `BackgroundPluginCatalog.InvokeAsync` 对查询使用15秒期限，取消后再给1秒退出宽限；任务仍未结束则报告上述诊断。正式宿主 `Program.cs` 的对应回调调用 `Environment.Exit(2)`，以避免不响应取消的插件继续持有存储运行。这会连带打断仍在执行的成交采集。此轮不是15分钟采集执行期限自然到期，也没有证据将其归因于官网403/429或HTTP 200正文出错。

日志中的Query未携带业务operation或调用栈，不能认定具体哪个页面/查询发起了这次调用。当前源码中的同步原件SHA256核验、全库完整性扫描与只读查询共用仓储门槛，是需要进一步测量的取消/性能风险点：`VerifyMany`有循环取消，但单文件 `Verify` 使用同步读取和 `SHA256.HashData(stream)`，没有逐块取消。现有现场证据尚不足以把本次查询准确归因到这一行。

证据：正式 `C:/ProgramData/CRS/Background/logs/service-20261022.jsonl`；`artifacts/market-zero-interrupted-system-events-20261022.json`；上述执行详情。

## 当前恢复状态（采样时刻）

21:02:58健康为Running，PID28296，两个插件Started。21:02:59活动列表显示成交采集执行中，其余同插件5项等待PluginBusy。没有把排队称为已执行。

21:04:53只读回执核对：请求 `4d7b526e-21ba-4961-b1c6-ebda89901c35` 为Running/Accepted，关联新执行 `42367ceaea45408ea2b05f82b0c861f9`，EndedUtc和最终Checkpoint均为空；最后进度已到2025-12-04 SSE来源间隔等待。因此服务已恢复且采集仍在继续，但尚无最终完成回执，不能宣称补采完成。

证据：`artifacts/market-zero-interrupted-health-20261022.jsonl`、`artifacts/market-zero-interrupted-activity-20261022.jsonl`、`artifacts/market-zero-interrupted-receipt-20261022.jsonl`。

## 修复方向（本次未实施）

1. 成交任务使用专属进度语义：逐日区分休市、已核验复用、实际解析、失败和实际提交；没有测量的新增/重复/审核数量不显示为0。ETF导入计数与市场成交日汇总分开。
2. 查询增加安全的operation级分段计时，定位SQL、原件核验、仓储等待和序列化开销；长查询与同步IO必须在取消期限内退出，优化只核验本查询需要的证据，同时保留原件完整性校验。通过取消和采集并行回归后再讨论安装更新。

本报告记录诊断证据，不代表上述缺陷已经修复，也不代表这段历史范围已通过正式覆盖验收。

后续已获授权实施修复；代码、回归与安装结果独立记录在[成交日志与查询取消修复](../implementation/MARKET_LOG_QUERY_CANCELLATION_FIX_20261022.md)，不改变本报告的原始诊断时点。
