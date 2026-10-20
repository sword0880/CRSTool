# 监控与预警真实数据核验（2026-10-20）

产品中文名统一为“小财牛”，品牌口号为“财富有数，投资有度”；品牌同步见[名称记录](PRODUCT_BRAND_20261020.md)。

本轮继续处理24只ETF的官方身份及份额口径、历史覆盖、待审异常与持续采集。真实数据验收与代码检查分别记录；历史回填不计入连续采集天数，PDF取得不等于可比性已批准。

## 已完成的代码与检查

真实服务单只份额查询曾发生管道EOF，随后服务停止。代码检查发现单只查询会扫描全库原件，且同步哈希循环不响应查询取消。已将单只份额和单只证据查询限制为其份额修订依赖的原件，保留每次查询重新校验；全池查询仍核验全库。工作区沿既有串行门槛传递取消令牌，循环取消后不返回半份结果，作用域结束恢复令牌。

新增12项真实文件/SQLite检查：无关证券原件独占锁不影响单只查询、全池仍检查全部依赖、自身原件损坏排除、独立证券不受污染、原哈希恢复保留恢复链、取消与后续正常查询，并以长递归SQL验证执行中的原生中断及连接继续使用。完整ETF确定性回归250项通过；Release构建0警告0错误，架构门禁通过。证据为`artifacts/monitor-regression.log`、`monitor-readiness-build.log`、`monitor-architecture.log`。不以这些检查代替长期服务验收。

首次保留数据升级事务`ea4bbf6468f44d16bc80854bcc2f563f`于07:36提交成功、服务健康，随后全池验收再次中断并于07:42意外停止，故首次候选不能判为运行问题已关闭。继续补齐SQLite执行中的原生`sqlite3_interrupt`取消，仅在取消令牌已取消且SQLite返回中断码9时转换为取消；其他存储错误仍按故障处理。串行用例结束注销回调，不能中断后一个查询。宿主未响应退出前增加分类诊断，写日志失败仍执行既有退出保护，未延长管道/插件期限。

现场数千文件的维护校验耗时较长，路径检查改为直接读取文件属性，仍对每个文件逐级重新检查祖先，不跨文件缓存检查结果。24项维护文件事务检查通过，包括缺失叶节点下的链接祖先拒绝、备份篡改和危险回退拒绝；隔离检查未操作SCM。证据`artifacts/monitor-maintenance-checks.log`。

## 官方原件核验

取得16只上交所、8只深交所ETF各一份2025年年报与2026年中报，共48份官方PDF，保留公开查询分页、下载URL、接收时间、SHA256及提取文本。已查看24只中报身份与份额变动表渲染页，核对主代码、管理人、跟踪指数及期初+申购-赎回=期末。历史拆分确实存在，不能默认所有期间可比。

部分报告注明总份额包含场外份额。因此本次审核清单严格限定2026-01-01至2026-06-30，并要求服务中2025-12-31及2026-06-30的已接受交易所事实，与报告期初和期末份额分别一致；交易所万份两位展示只按该来源精度核对，不改写原数。不符或缺少事实时保留`ShareScopeMismatch`/`AwaitingSameDateFact`，不审批。服务重新下载的PDF必须匹配核验哈希。不得将此区间延伸到最新日，亦不用于具名机构持仓证明。

原件清单与页图位于`artifacts/exit-alert-live/fund-evidence/`、`fund-evidence-szse/`、`fund-proof-pages/`；审核输入`reviewed-h1-metadata.json`。正式应用回执逐证券写入`artifacts/monitor-readiness/metadata-application/`。这些是本地操作证据，未提交为源码。

## 实机验收入口

`--monitor-audit-live`读取已结束日期的250日覆盖、20日连续采集、可比区间、待审修订和近期采集尝试，逐项保存进度，全部完成后生成`artifacts/monitor-readiness/audit.json`及`audit.md`。失败或中断的进度不能当作完整24只验收。

`--monitor-backfill-live from through`只提交既有官方份额同步作业，保存持久回执。`AlreadyRunning`不是补采完成；不重复生成命令冲刷在途任务。`--monitor-apply-reviewed-metadata`仅接受已核验逐证券清单，存在已启用外发渠道时拒绝应用，避免本次审核自动触发真实消息。

`--monitor-backfill-status`只读原请求ID的持久回执、执行记录与当前份额计划，保存`backfill-status.json`。任务执行成功仍需回读事实覆盖；来源退避或未发布不等于有效交易日已齐全。

## 最新实机记录

逐证券版本事务`c6f71c9d6f6544ae839bac7b9174d8db`首次因安装器40秒健康等待不足进入`NeedsManualRepair`。通过既有`RetryCandidate`恢复协议重验原快照、候选发行和真实健康，在维护健康探测中最多等待3分钟后确认`Committed`；未手工改写阶段、未回退旧库，业务管道和插件期限未延长。证据为`monitor-perfund-transaction.json`、`monitor-perfund-recovery.log`与`monitor-perfund-health.json`。自动维护启动等待仍有此实机限制，不能据恢复成功宣称直通升级验收通过。

24只验收于08:27完成（截至2026-10-19已结束日期）：11只满足250个有效交易日，其余13只为236至249日；两市日历均通过当前范围核验。沪市连续采集1/20、深市0/20，所有标的金融数据门槛仍未通过。共有40条历史待审修订，包含验收区间以外的修订，不能把该数字当作缺失交易日数。逐只结果、来源尝试与待审原件哈希见`artifacts/monitor-readiness/audit.json`、`audit.md`及`artifacts/monitor-audit-live.log`。

为完成维护核验，记录并暂停四项公开数据采集计划，宿主协作取消在途采集、保留已提交事实。口径比对后，已按`artifacts/p0-live/maintenance-original-plans.json`恢复四项原本启用的计划，Cron和时区保持原值；证据为`monitor-plans-resumed.log`及`artifacts/p0-live/maintenance-plans-restored.json`。早期不完整的进度记录已由本轮完整24只报告替代。

官方报告口径应用回执按证券保留；核验区间仅为2026上半年，不覆盖最新预警日期，也不等于真实具名ETF黄金样本确认。510050已通过期初/期末份额比对、服务原件哈希及正式审核回读。随后510100原件登记执行`fc17a40d15974bd6a07058229c9838c5`以`error=HttpRequestException`失败；此时不使用本地PDF冒充Worker原件，不继续反复下载。工具继续完成剩余只读比对，缺原件保持待处理；原件哈希不符或输入被拒绝仍立即停止。

24只比对回执全部完成：1只`Applied`，17只待后台原件（16只`AwaitingWorkerOriginal`、1只`RegistrationUnavailable`），3只`ShareScopeMismatch`，3只`ShareDisplayPrecisionUnconfirmed`。其中159629、159919、159922的差异小于万份两位展示精度，来源截断/舍入方式尚未确认，不能误写为已确认的份额范围错误；同样不自动审批。510310、159901、159915的报告含场外份额说明，仍需明确场内口径及原件审核。此分类保留原比对数值，未修改交易所事实或批准状态。

恢复计划后另提交2026-10-19单日份额补查，初始回执`Accepted`，只处理上一已结束日期，不重复全年度请求。最终执行状态单独保存为`artifacts/monitor-readiness/backfill-status.json`；08:27的完整覆盖报告保留原时点，不把排队补查提前算作新增有效日。

来源发布时间统计、使用许可、真实具名ETF黄金样本、同窗市场承接、连续20日、通知送达和生产签名仍需独立证据。`ReleaseApproved=false`。
