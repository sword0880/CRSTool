# 成交历史补采续跑修复（2026-10-22）

## 修复行为

手动成交历史请求每轮从持久库读取本区间已核验的市场/日期，只请求缺口。复用条件同时要求当前未被拒绝的修订值唯一、没有未解决Q15问题，且关联原件与规范化字节哈希仍有效；冲突或损坏不能当成已完成。服务重启后仍由落盘事实恢复进度，不依赖进程内游标，不更改原From/To。全部事实已存在时返回Succeeded，而不是因为本轮下载0行返回NotPublished。自动近段采集保留原有重新查询行为，以继续发现官方修订。

SSE/SZSE各自记录本轮冷却；一个市场受限后只停止该市场的后续请求，另一个市场继续采集。没有请求的日期不写为已尝试失败，既有有效数据不清空。已核验休市日继续通过日历识别；未知日历/空响应仍不能作为成交事实或补零。

来源预算的Until、网络失败的持久退避时间及非200响应的现有冷却期限传入采集结果，最终进度保留Until。任务根据实际需要的来源恢复时间安排下一次尝试；同一市场多个失败采用较晚期限，不会被较早错误时间缩短。多个仍有缺口的市场采用最早可继续的市场期限，不用一个市场的长冷却阻止另一市场继续补齐。无来源期限的局部失败采用5分钟后重试，不再固定30分钟；自动无错误但仍需追赶的批次保持30秒续跑。首次手动调度规则不变，仍受插件执行名额和来源策略约束。

检查点增加existingSources、failureDate、failureMarket、retryAt。rows仍表示本轮收到的市场日记录，不是新增事实数；existingSources表示经原件核验后复用的市场日数量。任务结束前再次检查新收到的事实；冲突/损坏导致ObservationUnverified并保持待重试，不根据返回行数冒充完成。来源失败的响应正文保留、独立失败历史及成交查询全库扫描优化不属于本次修改。

未新增数据库表或迁移、未改私人税务存储、未清除官方来源冷却。本机发布继续采用现有开发版维护事务；开发发行包不是已签名生产发行。

## 验证

- Release构建0警告/0错误，架构门禁及差异格式检查通过。
- 成交续采专项16条PASS：模拟局部解析失败保留5条成功事实；销毁并重建会话后只请求唯一缺口；全区间完整时零重复请求；SSE冷却只触发一次而SZSE完成全部日期；Until保留；冲突、损坏不能跳过，重下载同值也不能把未审核冲突当成成功。`artifacts/market-activity-fix-resume-tests.log`。
- 正式后台会话/加密任务仓储专项通过：实际来源冷却期限写入NotBefore，原请求保持Pending/RetryDeferred；会话重建后已有两市事实时零新增HTTP并返回Succeeded。`artifacts/market-activity-fix-cooling-tests.log`。
- 原有ETF/市场回归310条PASS。`artifacts/market-activity-fix-national-team-regression.log`。
- 来源策略与旧版来源策略恢复回归通过，保留403/429长冷却、共享预算、失败持久化及损坏状态拒绝使用的行为。`artifacts/market-activity-fix-source-policy.log`。

完整Worker包：`artifacts/market-activity-resume-worker-20261022`。完整包46项隔离服务联调通过，日志`artifacts/market-activity-fix-package-integration.log`；不能由隔离来源代替官网数据验收。

## 本机安装与正式验收

更新前14:51:19当前任务为运行0、排队0、待执行0。通过既有维护事务创建5,095个文件的一致性快照，原程序及数据快照保留；维护代次`5500d3031761484b829cb04ec6dfb3ae`于14:58:50进入Committed。153个安装发行文件逐项摘要与候选一致，release.json一致，原service-key.dpapi摘要未改变。正式服务健康Running，两个插件Started。证据：`artifacts/market-activity-fix-upgrade.log`、`artifacts/market-activity-fix-installed-hashes.json`、`artifacts/market-activity-fix-installed-health.jsonl`。

对用户目标2026-01-01至2026-09-01提交一次正式验收命令`fe517dc2-a264-4eb1-acb5-687b710e1bca`。它曾因既有ETF份额任务占用插件写入名额而等待，验收工具2分钟窗口返回Unconfirmed后，继续只读核对同一编号，没有重建补采。15:04:20.746开始，15:04:21.770结束；命令Completed/Succeeded，执行`658b7ad396264850bcad4fbd8b6c13ad`。检查点：`days=244;sourceCalls=0;httpStarted=0;closedSources=166;emptySources=0;sourceIssues=0;rows=0;existingSources=322;firstFailure=None;hasMore=False`。证明新版能复用全部已核验事实，在零HTTP下完成历史请求，不再从头重采或把零新增误报NotPublished。证据：`artifacts/market-activity-fix-live-final-request.jsonl`。

正式覆盖回读：244个自然日，161个Complete、83个Closed，无Gaps、无MissingDates，SSE和SZSE各161个有值日期。证据：`artifacts/market-activity-fix-live-final-coverage.json`。采集期间的首次覆盖查询仍曾因现有查询开销超时，返回Unavailable；任务结束后的正式回读成功，未用空值代替查询失败。

因果边界：原请求`2e4fc30c-a9d1-41c1-9c3e-f7a06ea407d3`在旧服务13:56:45至14:05:48的执行已Completed/Succeeded，仍重采274天、362次HTTP，收到362条、sourceIssues=0；缺口在本次更新前已由旧任务补齐。不能声称新代码下载了最后一条缺失记录。本次实际验收证明完整区间不再重复下载；真正单缺口续采、故障恢复、来源隔离与新重试期限由上述专项回归验证。原请求回执证据：`artifacts/market-activity-fix-original-request.jsonl`。
