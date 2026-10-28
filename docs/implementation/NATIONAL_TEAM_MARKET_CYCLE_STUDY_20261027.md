# 市场周期查询接入（2026-10-27）

状态：源码与隔离验收阶段。正式行情覆盖、来源许可、历史可知性、择时有效性和联合退出预警仍未验收。

## 结构与查询

`Core/Domain/MarketCycle` 保存冻结输入、候选规则及评估结果；`Jobs/Application/MarketCycle/MarketCycleStudyUseCases` 是第十组类型化后台用例；`Infrastructure/MarketCycle/MarketCycleStudyStore` 在同一独立只读SQLite事务中读取价格、两市日历、成交知识链和广度档案，实际调用 `MarketCycleEvaluator`。聚合仓储仅转发。客户端第十一份按功能拆分文件通过 `market-cycle-study` 读取公共完整JSON分块，重算报告摘要及评估结果；不支持的旧服务明确失败。

桌面新增顶部“周期与风险 → 市场周期观察”，三张紧凑卡片分别展示顶部风险、底部区域及修复条件。缺口、依据、原件、口径分标签页展示；手动查询，改日期或截止时刻清除旧信号，超时/取消只结束查询。界面不联网、写库、计算金融指标或发送通知。

默认知识截止在独立数据库快照建立前固定，显式截止也不能晚于该时刻；查询途中发生的较晚写入不能借后移的系统时间描述为已读取。登记/审核时刻仍是事务内事件时间，不等于独立保存的提交完成时间；并发在途事务的绝对历史可知性尚未证明。统计日须早于截止的北京日期，暂不支持同一自然日收盘后的截止研究。向前读取400个自然日，不足250个有效交易日时关闭信号。

## 口径与证明

- 价格代理固定为国证A指399317，按既有方法涵盖沪深北符合条件的A股样本、剔除ST等；与沪深股票市场成交、沪深A股广度分别说明，不能宣称三者证券宇宙相同。价格、方法、单位证明分别保留来源、真实摘要及知识状态时刻，指数样本成交额不混入市场总额。
- 成交读取[知识链](NATIONAL_TEAM_MARKET_ACTIVITY_KNOWLEDGE_20261027.md)。同日SSE/SZSE都健康、已核验且无异值才合计金额及股数；同市同值多份原件只计一次，任一缺市、待审、损坏即关闭该日合计，缺失不是零。竞争事实及状态绑定知识摘要。
- 两市年度日历均须覆盖读取区间、健康且一致；查询重新解析年度原件核对日历表，防止表值改变而文件仍存在。各年度原件单独引用。现有日历表仍是当前指针，后来替换不能重建旧版全年快照；旧截止无法证明时保持不足，`CalendarReplayProof=CurrentReviewedPointerOnlyNotProven`。
- 250日价格、21日成交、6日广度及日历门槛全部通过才输出研究预览；规则计分不是概率，`TimingProof=NotProven`、`NotificationsEnabled=false`。系统实际登记时间不能证明原件在更早历史时点已公开可得。

两市合计、日历一致性及证券名单的摘要是**派生证明**，报告 `EvidenceKind` 明示区别；`Sources.OriginalHash` 才指向真实原件字节。报告包含完整输入及规则，可独立复算；尚未持久化历次真实评价输出，不能称已形成历史影子运行记录。

## 广度档案端口

普通SQLite新增 `market-breadth/1`：不可变 `nt_breadth_archive`、请求绑定 `nt_breadth_command`；缺表或未知迁移不自动重建。原始原件及标准化JSON保存于既有内容寻址证据库，登记 `source_fetch`，沿用Q15隔离、写门槛及业务命令事务。每次研究重新核验原件和标准化值，损坏为Critical。

`IMarketBreadthWriteRepository.ImportReviewedMarketBreadth` 仅是可信后台人工核验写端口，**没有公开Worker写RPC、文件选择界面或自动采集任务**，不授予来源许可。必须明确确认原件、核验人、说明、HTTPS来源、取得时刻及请求ID。首次可用从实际人工核验登记起算。同请求改参数拒绝，同档案再提交不制造重复日期；竞争档案保留并关闭完整信号，目前尚无竞争修订选定/撤回流程。

格式 `reviewed-breadth-v1`：统计日与证券名单日期一致，定义 `SSE-SZSE-Ashares-point-in-time-v1`，`PriceBasis=BackAdjustedToObservationDate`。`Members` 为当日名单，`Observations` 须逐证券一一覆盖；后台依据 `Close > Ma20`、`Close > Ma60`、`Close <= Prior250Low` 计数，前250日最低**收盘**不含当日。成员、均线、复权和原始历史的真实性、完整证券覆盖仍需人工与来源证明；代码只验证结构及明确核验记录，不凭文件名证明全市场覆盖。正式逐证券采集、退市/停牌/新股规则及证券宇宙验收尚未接通。

## 验证及剩余工作

专项入口：`CRS.Tests.NationalTeamWatch --market-cycle-study <新目录>`；34项覆盖快照后并发写入、缺资料、成交冲突/审核、知识截止、广度原件损坏、日历表改写、价格/方法/单位证明、不混入指数样本额、幂等、结构损坏、重启、后台查询和页面竞态。完整输入使用显式合成价格、成交、广度及日历，只证明规则可复算。没有使用完整正式价格与广度贯穿整个真实数据链。

布局入口：`CRS.Tests.PluginRecovery --market-cycle-visual-preview <专项输出> <图片输出>`，检查完整/缺失两状态、640/1040/1280 DIP、100/150%密度；跨进程检查纳入 `--activity-knowledge-pipe`，完整候选Service → Runner → 桌面客户端读取周期输入并核对审核前后的固定截止。所有隔离产物均标记合成资料和未安装。

后续继续：正式来源准入、完整历史采集、广度核验操作及修订流程、指数历史方法适用性、样本外评价和基线、真实影子输出持久化、国家队持仓/行业估值/NAV联合退出模型，以及保全式正式迁移安装。当前没有替换正式Worker、批准真实机构持仓或开启金融退出通知。

## 本地验收记录

以下均已终态通过，远端CI尚未执行，也不是正式服务验收：

| 范围 | 结果与证据（仓库内artifacts） |
| --- | --- |
| 38项目Release构建 | 0警告、0错误；`market-cycle-study-snapshot-build-20261027.log` |
| 最终周期专项 | 34项；`market-cycle-study-snapshot-checks-20261027.log`，对应目录保存完整/不足报告 |
| 国家队完整隔离回归 | `market-cycle-study-snapshot-regression-20261027.log` |
| 最终候选跨进程 | 16项；`market-cycle-study-pipe-v2-20261027/acceptance.json`，`Installed=false`，合成资料，监督Runner、实际命令与执行终态回读、相同健康PID；进程已在finally停止 |
| 候选包 | `market-cycle-study-worker-v2-20261027`；`market-cycle-study-package-hashes-v2-20261027.json`核验182份发行文件；开发包未安装，早期候选及日志保留 |
| 实际控件布局 | `market-cycle-study-visual-final-20261027`，12张完整/缺失两状态截图；700 DIP高度下正文438–474 DIP，已查看640和1040宽度 |
| 静态门禁 | 架构、11份客户端分文件、11组XAML/代码后置、49份XAML视觉规范及文档本地链接通过 |

布局后增加的共享快照时刻保护不改变页面源代码；其并发写入专项、完整回归及最终候选真实管道另已重验。以上没有读取或修改正式市场/税务资料，也未触发外网采集、正式资料清理或金融通知。
