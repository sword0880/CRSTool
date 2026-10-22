# 成交日志与查询取消修复（2026-10-22）

## 问题与行为

诊断依据见[现场记录](../validation/MARKET_ACTIVITY_ZERO_AND_INTERRUPTION_20261022.md)。成交任务旧版的DayCommitted没有提供实测计数，桌面却将默认值显示为全部0；20:50:47另有插件查询取消宽限超时，触发安全退出并打断采集。旧日志没有业务operation，不能事后断言具体卡在哪个查询或哪一行。

成交任务新增专属阶段，逐日及区间保存实际收到、复用、已核验休市、空响应、未请求待重试和来源问题。收到是本轮解析的市场日记录，不等同于新增事实；不再套用ETF份额导入的新增/重复/待审计数。旧历史明确显示“旧版未记录当日计数”，不根据附近SourceParsed阶段推造新增数据。空响应和失败不当作零成交。

新增进度字段采用可空init属性，保留BackgroundProgress原构造和解构签名。旧记录字段缺失时仍是未记录；宿主继续校验阶段白名单、市场标识和非负计数，新的成交汇总必须带完整分类计数。来源冷却Until和原请求重试身份保持原有语义。

证据核验改为同一句柄的逐块异步读取，传递当前操作取消令牌；最多四个文件并行，仅本次操作去重，不跨查询缓存。取消或I/O失败不返回部分校验字典，仍核验规范化与原始哈希。仓储在SQL前后检查取消，保留原有sqlite3_interrupt；查询行序列化和时间归一过程中同样检查取消，不发布取消后的查询快照。

成交查询只扫描成交事实及所依赖的已审核交易日历；整页查询扫描份额、成交及日历依赖，使用延迟只读事务以免抢占写锁。后台完整性巡检继续全量扫描，相关证据损坏的查询继续失败关闭。未删除原件、未清空数据、未绕过来源限流。

宿主新增可选查询诊断端口，在同一15秒生命周期内调用。只允许有限业务操作名称，拒绝URL和参数；取消/故障分类携带操作名，超过1秒的查询增加耗时。取消后的1秒退出宽限和失控进程退出保护保留，未靠延长超时掩盖问题。旧插件不需要实现新接口。

## 验证与安装

验证结果：

- Release全解决方案、Debug桌面构建均0警告/0错误，桌面开发插件随构建同步。架构门禁及差异检查通过。
- 国家队完整隔离业务回归316项PASS，包含成交当日/区间分类、空响应、冷却待重试及相关原件损坏拒绝。`artifacts/market-query-fix-national-regression.log`。
- 生命周期31项PASS，包含业务操作分类、耗时、URL/参数拒绝、协作取消不强制退出，以及Start/Query/Stop/Dispose失控子进程真实退出码2。`artifacts/market-query-fix-lifecycle.log`。
- 原件专项6项PASS：正在等待的读取取消9ms，4个已打开句柄均释放；I/O失败不返回部分结果。真实仓储可靠性3项PASS：查询取消4ms，等待的采集继续提交2条，下一次查询成功；无关坏披露原件不拖累成交页但完整巡检仍发现；只读整页不抢占已有写锁。计时是隔离样本，不能视为正式服务性能承诺。`artifacts/market-query-fix-raw.log`、`artifacts/market-query-fix-reliability.log`。
- 实际后台会话和加密任务仓储验证首轮收到2条、重建后复用2条且零新增HTTP；新计数经落盘回读保留，缺失或负数拒绝。WPF模型验证旧历史未记录、新计数与ETF语义。`artifacts/market-query-fix-progress.log`、`artifacts/market-query-fix-wpf-log.log`。
- 与已安装Worker的44个既有公开类型比较，原构造器、方法、常量保留，没有新增必需抽象实现。完整候选包46项隔离服务联调通过，真实插件Started及概览查询通过。`artifacts/market-query-fix-binary-api.json`、`artifacts/market-query-fix-package-integration.log`、`artifacts/market-query-fix-package-startup.log`。

候选包`artifacts/market-log-query-worker-20261022`为本机开发发行，尚未签署生产发行签名。隔离结果不能代替正式服务与真实数据验收。

更新前正式请求`4d7b526e-21ba-4961-b1c6-ebda89901c35`已Completed/Succeeded，关联执行`42367ceaea45408ea2b05f82b0c861f9`于21:05:53结束：366自然日、335次HTTP、收到335条、复用151条、休市246个市场日、来源问题0。它是在此次修复安装前由原服务续跑完成，不能归功于新代码。证据`artifacts/market-query-fix-before-receipt.jsonl`。

21:38:33维护前确认运行0、等待0、待执行0。既有维护事务保存5,590个文件的一致性快照及原程序，代次`2bb60708f885494eaa4277d7925bd7e9`于21:49:07进入Committed。153个安装发行文件逐项摘要、release.json与候选一致，原service-key.dpapi保持不变；Debug/Release各2个桌面插件、86个文件摘要检查通过。证据`artifacts/market-query-fix-upgrade-preflight.jsonl`、`artifacts/market-query-fix-upgrade.log`、`artifacts/market-query-fix-installed-hashes.json`、`artifacts/market-query-fix-desktop-hashes.json`。

正式后台21:49:07启动，PID35372，健康Running，两个插件Started。繁忙期间独立overview和主页面dashboard各有一次15秒RequestTimedOut，诊断分别记录`Query.overview.TimeoutCanceled`（15018ms）、`Query.dashboard.TimeoutCanceled`（15144ms），没有再次触发CancellationGraceExceeded或进程退出。期间原ETF采集执行`186ac22c41f846deb101ab303b662329`继续到21:53:04，以RetryDeferred结束（收到120条、来源问题24），未变成Interrupted；这是份额任务，不能当作成交历史验收，也不能声称来源问题已解决。后续健康回读仍是PID35372。证据`artifacts/market-query-fix-installed-refresh.log`、`artifacts/market-query-fix-installed-dashboard.json`、`artifacts/market-query-fix-running-share-history.jsonl`、`artifacts/market-query-fix-after-timeout-health.jsonl`及服务日志。

正式成交覆盖回读：2025-01-01至2026-01-01共366自然日，243个Complete、123个Closed，Gaps和MissingDates均为空，SSE/SZSE各243个有值日期。`artifacts/market-query-fix-installed-coverage.json`。这是既有事实的查询验证，不声称本轮重新下载了它们。

对截图日期2025-08-14至18只提交一次正式复用验收命令`ac72f41f-433c-40b7-b06c-cf2d4124922b`，执行`e072899dda9348c4a54dd980874c483f`返回Completed/Succeeded：sourceCalls=0、httpStarted=0、rows=0、existingSources=6、closedSources=4、sourceIssues=0。新MarketDayCompleted按14/15/18日各复用2条，16/17日各休市2个市场日持久化；MarketRangeCompleted保留Reused=6、Closed=4，其余新计数为实测0。`artifacts/market-query-fix-live-progress-acceptance.jsonl`。

21:57空闲时主页面真实整页查询成功：Overview=24、History=435、Activity=660，含分页往返总23142ms，服务端首次dashboard构建12729ms，前后健康Running且PID仍为35372。`artifacts/market-query-fix-installed-dashboard-idle.json`。全部原件只读A/B测量为5573份，旧串行15868ms、可取消批量11116ms，结果一致且Invalid=0。`artifacts/market-query-fix-live-raw-profile.json`。

剩余限制：繁忙时独立概览和整页查询仍可能超过15秒，页面会报告超时而不是空数据；取消/安全退出问题已修复并有上述正式证据，性能限制未宣称全部消除。当前会话查询是每次重新校验原件，未改为跨查询缓存，也未延长查询期限。桌面Debug/Release已构建，运行中的旧桌面需要关闭后重新打开才能使用新日志文案。
