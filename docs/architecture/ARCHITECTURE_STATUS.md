# 当前架构状态

更新：2026-10-29。本文件是当前模块边界、未关闭高风险、正式部署及证据的唯一状态索引。实施文档和验收日志保留历史时点；冲突时先核对现场回执，不能按文件日期推定已部署。时刻均为北京时间。

最新正式维护：代次`b73f99ecce244cf48f66db640fde3f60`于13:11:11 Committed，13:28维护后探针为PID62460 / Running。保全、原资料对照及40项只读检查通过；份额批次历史读验与原件保存外移已安装。自然运行六个日批次准备累计40693ms，提交累计持有写名额5416ms；任务仍因24项来源问题RetryDeferred，金融资料验收仍为false，见[维护汇总](../../artifacts/collection-commit-20261029/acceptance.json)。

最新资料回查：14:01系统服务和应用探针仍为同一PID62460 / Running，13:57原7项计划、24只ETF配置、29条股票投影及10条旧执行事实逐项对照通过。已通过正式后台完成全部24只配置ETF的2025-01-01至2026-10-29公告检索，共298条目录；另登记159915三份原件，两份年报22行待审候选及中期NotDisclosed均可回读。510300的原件下载连接失败，未登记；金融资料验收仍为false，见[资料闭环证据](../../artifacts/etf-report-coverage-20261029/acceptance.json)和[具名候选待审包](../../artifacts/etf-report-coverage-20261029/REVIEW_PACKET.md)。本轮只改运维取证工具，未再次安装后台或修改业务规则。

## 模块边界

| 模块 | 当前职责与约束 |
| --- | --- |
| CRS.Common | 时间、指纹、有界内容、路径防护、认证文件、文件暂存、有界FIFO准入/取消/排空及核验后文件句柄稳定租约；不包含国家队主体或金融规则 |
| CRS.Documents | Excel读写、PDF有界页/文字/坐标及行和矩形单元格提取；业务模板、来源审批、金额计算归所属领域 |
| Worker.Abstractions / Ipc / PluginHost / PluginRunner | 后台契约、授权通信、可信包及独立进程生命周期 |
| Worker.Service | 服务密钥、计划/执行/命令持久化、来源预算、维护、文件入口及健康；默认全局任务并发8 |
| NationalTeam.Core | ETF、成交冷热、机构目录、直接持股、行业、NAV、周期及退出规则，签发版本/准备/提交的窄端口；无数据库或WPF依赖 |
| NationalTeam.Jobs | Etf、EtfHoldings、Equity、Nav、IndustryValuation、MarketCycle、ExitAlerts、Evidence分目录；功能Store及同事务组合读；后台执行金融写入 |
| NationalTeamWatch / Plugin.Wpf | 版本化客户端、展示模型和页面；不直接开库或计算正式金额 |
| 收入/报税核心 | 独立个人密钥域及加密存储；不纳入国家队迁移；税务P0正确性按用户要求最后处理 |

国家队业务库和来源策略库已使用普通SQLite，审批认证仍需派生密钥；Worker调度管理库保留加密。`MarketDatabaseSession`拥有连接及只读事务，各功能Store拥有初始化及持久化，`MarketRepository`组合窄端口。

具名ETF披露的“仅公布日期”和“准确时刻”由Core明确区分，日期记录不填入零点；首次取得时间绑定已登记原件。日期审核须协商`named-etf-publication-date-v1`并使用独立操作，旧操作不接受该载荷；旧准确时刻序列化、历史结果及指纹保持不变。该修正不等于具备历史审核知识时间轴，多期历史仍为`CurrentSnapshotResearch`。实现及隔离边界见[具名持仓记录](../implementation/NATIONAL_TEAM_NAMED_ETF_HISTORY_20261028.md)。

后台业务入口由8个功能模块声明54个查询处理器和25项任务，4个框架查询处理分页/导出/状态。任务描述、执行组、查询分类和诊断白名单均从注册取得；扩展业务不修改中央分派switch。通用路由契约在Worker.Abstractions，宿主在受信启动和Runner握手时验证，调用方不能自报短读类别。

已安装的契约边界修复：Common作为Abstractions的传递契约共享，包中携带的Common须与宿主字节一致；名额仍是各会话的实例。Runner只传固定准入原因和公开异常类别，容量/等待预算及InputsChanged拒绝保存RetryDeferred并沿原持久命令30秒退避，其它失败及取消保留原边界。巡检依赖捕获改走独立读会话，结果提交仍受单写和版本比对约束。旧类型身份反例及36项进程检查保留于[前次源码证据](../../artifacts/runner-admission-20261028/source-acceptance.json)，当前扩展为39项，见[本轮源码证据](../../artifacts/evaluation-prepare-20261029/source-acceptance.json)。

预警评价的验收、原件SHA和历史计算已移到独立读会话的有界内存副本；最后取得写名额，在单事务内比对资料版本并提交事件、冻结输入、Outbox和待评价标记。专用只读连接的data_version能检测本写连接及外部连接的提交；连续变更最多重新准备三次，再明确退避。Common持有已完整核验的Windows只读句柄，提交完成并排空租约后释放，不采用文件时间/大小或跨请求缓存。上限为数据库256MiB、8192份文件、变更32MiB/100000条；超限拒绝，不截断金融结果。准备及SQL能力仅在后台内存流转，不经IPC接受调用方SQL。

份额导入事务保留待评价标记，采集回执记录`evaluation=IndependentJob`，不再因附带评价冲突延后已提交的采集终态。既有五分钟评价计划独立处理；停用该计划会保留标记，须手动评价或重新启用。人工审核/管理仍可立即评价。规则和九张持久表等价验证、取消及故障范围见[实施续项](../implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)。

成交采集前后核验、ETF历史缺口及池配置/日历/游标等八处只读调用已改走独立读快照。缺口待审日期按证券和区间直接投影，保留全范围原件核验、待审排除和Q15语义，避免构造整库展示行。真实文件暂停反例及28条隔离检查覆盖配置写入独立提交、取消/恢复、超过一万范围外修订和同哈希修复；它们证明这些路径的隔离，不证明所有后台写入均已优化，见[源码证据](../../artifacts/collection-read-20261029/source-acceptance.json)。

份额批次复用Common的稳定文件租约，在写名额外预读历史依赖、保存并核验原始/规范化字节；同次读快照签发单次启用池版本，池变化最多准备三次。它不冻结金融结论，无关写入不会使原件准备失效；提交体仍按当前库完整核验，新增引用仍会读验，逐页事务及待审规则保留。删除重复整库证据行物化；准入失败保存Interrupted待重试，旧载荷未记录的时间保持null。18条专项及首候选失败/修正版真实后台验收见[本轮源码](../../artifacts/collection-commit-20261029/source-acceptance.json)。

### 工作区并发与等待预算

| 类别 | 并发和排队 | 预算与边界 |
| --- | --- | --- |
| 短读 | 客户端/宿主/插件/应用独立2项；各层最多4项等待 | 各层准入最多1秒；宿主处理最多5秒；已冻结快照分页属于短读 |
| 研究读 | 独立1项；最多1项等待 | 同步准入最多2秒，原15秒请求期限保留；不会占短读名额 |
| 写入 | management执行组1项，数据库单写；短数据库操作最多4项等待 | 数据库准入最多5秒；宿主不持线程等待任务名额，持久命令保留原ID；evaluation仍有独立1项组 |
| 后台采集 | collection执行组2项；插件执行上限4，全局8 | 执行名额非阻塞尝试，插件共享待调度上限16；未到期的来源NotBefore保留；任务原15分钟期限及来源预算保留 |

采集/评价的应用读连接另有1项在途、4项等待及2秒准入预算，不与前台研究读共用名额。完整评价另有1项在途、1项等待及2秒准入，关闭时跟随应用排空。后台读不领取前台原件优先权。网络下载和评价长读验不占数据库单写门槛。客户端繁忙重试最多2秒，管道身份限流最多10秒；兼容旧Worker的普通查询保留串行，批量/异步能力未声明时明确拒绝对应新功能。

长研究客户端（预警总览、联合观察、周期研究）先协商`research-snapshot-jobs-v1`，再提交原请求、短轮询状态、按结果快照ID分页。最多2项驻留作业（包含完成未释放的结果），构造含排队采用90秒取消预算、争用研究名额最多等待30秒；客户端作业等待100秒，联合观察/周期研究页面110秒，结果/作业10分钟过期。普通请求仍保持5/15秒期限。随机作业ID绑定完整请求及指纹，分页仍绑定证券/日期/知识时点；失败、取消、超时或重启失效均不返回部分金融结果。维护经宿主到Runner取消并排空已受理作业，再关闭连接，已停止的Runner不会因排空被重新启动。所有预算均保留协作取消及不合作插件的进程终止保护。

## 未关闭高风险

| 项目 | 当前缺口与关闭证据 |
| --- | --- |
| 查询隔离及长研究正式验收 | 13:55的40项研究查询全部返回：24只ETF批量9187ms，总览14067ms、联合观察10297ms、周期4598ms，进程稳定；报告覆盖另有82项成功只读查询。单次采样未控制缓存/自然负载；持续负载、取消与在途异步研究维护排空的正式验收仍开放，不能用前后采样计算提速率 |
| 准入争用、其它写路径及启动规模 | 13:15份额六次日准备累计40693ms、日提交累计持有写名额5416ms，未测得单日峰值，也不包含前置建档/Fetching短写。并行新增依赖仍在写体核验；通知领取、证据导出等其它路径、分门槛观测及连续负载继续开放。正式成交/巡检仍有WaitBudget，原因不能区分读/写门槛。份额Failures=3沿用升级前状态，来源未齐仍退避，不算全池完成。归档增长和启动规模继续开放；本次显式600秒预算内直接Committed，无恢复，未删除归档或改变查询期限 |
| 持仓及方向 | 24只ETF均有正式成功命令和298条可读报告目录，但目录未冻结，不能证明全部历史披露完整。159915三份原件已正式登记：2024/2025年报各11行候选，其中两类汇金法人共四行待审；2026中期不披露具名表。原4份其它ETF原件仍UnsupportedTemplate；510300年报下载RequestFailed，未登记，外层命令仅返回Failed/OperationFailed，来源失败分类与恢复仍需改进；13:56来源冷却至14:05:40。没有批准真实持仓，24只具名查询均完成且已核验0行，实际持仓未知。股票29条/披露15条仍为样本；法人/账户、独立持有、公布时间精度及完整组合分母继续开放 |
| 估值/周期/退出有效性 | 行业公布PE不等于完整PE/PB估值，NAV不等于周期。完整行情/广度、历史知识时点、样本外及影子验证开放；联合观察不发正式退出通知 |
| 发行和恢复 | 当前为开发包显式豁免；签名发行、第三方权限隔离及完整跨机灾备未由本机Committed关闭 |

## 正式部署及证据

| 范围 | 当前状态 | 对应证据 |
| --- | --- | --- |
| Worker及授权 | 沿用户授权的保全流程，LocalService / Running / Auto，PID62460；代次`b73f99ecce244cf48f66db640fde3f60`于2026-10-29 13:11:11直接Committed，14:01确认同一发行和代次，本轮未触发恢复或回退 | [保全回执](../../artifacts/collection-commit-v2-installed-20261029/preservation-acceptance.json)；[最后健康](../../artifacts/etf-report-coverage-20261029/final-health.json) |
| 发行及资料保全 | 最新包`artifacts/collection-commit-worker-v2-20261029`，索引SHA256 `A4350A57928C18BE7EA3ADE3BA599D5DEDADF089D101D6041B3DBFA5814B3F99`；5990份快照、新旧发行各182项及原密钥摘要逐项通过，旧程序和旧归档保留。双业务库沿用普通SQLite，收入报税核心未修改 | 同上；[升级前检查](../../artifacts/collection-commit-v2-installed-20261029/before/preflight.json)；[原双库迁移](../implementation/NATIONAL_TEAM_STORAGE_MIGRATION_20261028.md) |
| 原配置及事实回读 | 13:57再核对7项计划身份/Cron/时区/启用/修订、24只ETF配置、29条股票投影及10条旧执行，均保留；原4份登记原件逐项保留，同一健康PID62460 | [最新逐项对照](../../artifacts/etf-report-coverage-20261029/final-state/baseline-comparison.json)；[原件及目录对照](../../artifacts/etf-report-coverage-20261029/acceptance.json) |
| 正式协议及研究读 | 三项既有能力均Ok：批量验收、异步研究快照、仅公布日期审核。13:55的40项研究检查全部返回，24只具名查询全部完成、批量返回24只；最新耗时见上方。缓存/自然负载未受控，不能按前后两次样本计算提速率 | [最新只读报告](../../artifacts/etf-report-coverage-20261029/final-feature-audit/feature-audit.json) |
| 金融覆盖 | 闭日2026-10-28；已核验具名ETF持仓0行（实际持仓未知），股票29条/披露持仓15条、成交667行、行业及指数各11序列。新增目录及原件仍未批准持仓，预警资料验收false，不能据此确定周期或退出 | 同上；[报告/原件覆盖](../../artifacts/etf-report-coverage-20261029/after/etf-report-coverage.json) |
| 自然份额及评价 | 13:15份额任务8查询日/120来源行/24来源问题，六个日批次累计准备40693ms、持有写名额5416ms，仍RetryDeferred；13:20未跳过评价准备8436ms、持有2ms/提交体2ms。均不是物理磁盘耗时或受控提速率，跳过的0ms不用作性能证据。既有通知计划继续，后验只读且未额外触发采集 | [本轮汇总](../../artifacts/collection-commit-20261029/acceptance.json)；[份额专门回读](../../artifacts/collection-commit-v2-installed-20261029/share-observation-01.jsonl)；[最新运行回读](../../artifacts/collection-commit-v2-installed-20261029/runtime-check/state.json) |
| 源码及隔离证据 | 本轮Release完整解决方案0警告/0错误，完整国家队含18条份额提交、28条读隔离、34项评价拆分；49项真实后台ETF通过，原35秒观察窗未变，首候选失败及旧阻塞反例保留。39项准入、监督21项、查询分类25项、慢诊断8项、149项维护、日期历史84项/管道10项及9张控件渲染保留前次证据；未验收桌面菜单真实交互，未声称远端CI已运行 | [本轮源码](../../artifacts/collection-commit-20261029/source-acceptance.json)；[前次读隔离](../../artifacts/collection-read-20261029/source-acceptance.json)；[评价及宿主](../../artifacts/evaluation-prepare-20261029/source-acceptance.json)；[前次维护](../../artifacts/runner-admission-20261028/source-acceptance.json)；[日期续项](../../artifacts/holding-time-20261028/source-acceptance.json) |
| 持有人解析来源 | 已安装159915的2024/2025年报各11行候选、2026中期NotDisclosed及深市附件日修正；保存公开原件与真实隔离管道验证，没有自动批准真实账户 | [来源及隔离证据](../../artifacts/efund-holders-20261028/acceptance.json)；[实现范围](../implementation/NATIONAL_TEAM_ETF_REPORT_INDEX_20261027.md) |
| 正式报告及待审原件 | 24只ETF官方公告检索均Completed/Succeeded，共298条；首次并行510300命令AlreadyRunning无执行，保留拒绝回执，后续串行成功。159915新增三份原件，摘要与已支持版式一致，22行NeedsReview及一份NotDisclosed；510300原件失败未补造，另一份未提交。82项覆盖查询成功，原件4→7，已核验持仓仍0 | [本轮汇总](../../artifacts/etf-report-coverage-20261029/acceptance.json)；[审核候选包](../../artifacts/etf-report-coverage-20261029/REVIEW_PACKET.md) |

整个NationalTeam目标仍未完成。源码、隔离机制、候选包、正式服务和金融数据/模型证据分别闭合。
