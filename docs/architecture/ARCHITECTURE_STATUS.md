# 当前架构状态

更新：2026-10-30。本文件是模块边界、未关闭高风险、正式部署及证据的唯一当前索引；实施文档保留历史。时刻均为北京时间，源码、隔离回放、正式后台和金融审核分别验收。

当前正式发行：代次`d2a33786b9b3404fb707cb10c98c65f6`于2026-10-30 17:40:09 Committed，PID10716 / Running / Auto / LocalService。新旧程序各182项、6048份快照及原密钥摘要逐项通过；7项计划、24只ETF配置、29条股票投影、10条旧执行和原11份登记原件逐项保留。索引SHA256：`DA2067FDE20EEFF0DF26765D98DD2797786AFB37E3F432E1857AADBA68B95FFC`。见[正式汇总](../../artifacts/etf-evidence-ports-20261030/acceptance.json)、[保全回执](../../artifacts/etf-evidence-ports-installed-20261030/preservation-acceptance.json)及[最终健康](../../artifacts/etf-evidence-ports-installed-20261030/final-health.json)。未恢复旧库或删除归档。

已安装ETF证据从存储到应用、通信客户端及界面的归层，保留分市场采集、公共请求跟踪、明确来源预算及异步批量路径。完整业务回归通过，随后补中文注释及六项边界断言，再完整编译0警告/0错误；最终20项应用边界、22项旧发行逐项对照及分层检查通过，候选28项验证两个实际重启窗口、另20项验证旧入口恢复。四张表结构及根格式2不变，新代码可直接重开旧发行所建库。见[本轮源码/候选](../../artifacts/etf-evidence-ports-20261030/source-acceptance.json)。

最新资料回查：24只ETF、298条目录、11份登记原件；510300、159915、159919各两份年报共66行NeedsReview，一份中期NotDisclosed、四份UnsupportedTemplate。已核验具名持仓0行，实际持仓未知。本轮86项覆盖及40项研究回读均可用、同一健康PID，批量完整读取24只；原件和候选内容均与升级前逐项一致。NAV/联合观察/周期仍为既有样本与口径，不能推广为全池金融资料完整。见[原件覆盖](../../artifacts/etf-evidence-ports-installed-20261030/after/report-coverage/etf-report-coverage.json)、[研究回读](../../artifacts/etf-evidence-ports-installed-20261030/after/feature-audit/feature-audit.json)及[逐项保全](../../artifacts/etf-evidence-ports-installed-20261030/after/baseline-comparison.json)。前轮查询失败负例保留，不用本轮成功改写。

新协议`etf-document-collection-v1`及沪深手动任务保留，任务共27项。沪市510300-2025原请求`70f579ede5384e478443fbb060ddd9ee`沿原ID于17:09:16.867 Completed/Succeeded，直接绑定执行`26c5295def8a4a4fab70856d12051a26`；原件SHA256为`9790CC85C6EE6611D52E653F4154632EFF35D588185D1269CADC77082F5FB4C4`，候选11行仍NeedsReview。17:43升级后回查仍为同请求、同执行、同原件和候选内容，当前任务无Pending；没有重新提交。深市前轮成功同样保留待审边界。见[升级后终态](../../artifacts/etf-evidence-ports-installed-20261030/after/sse-510300-2025/document-status.json)及[当前任务](../../artifacts/etf-evidence-ports-installed-20261030/after/task-activity.jsonl)。先前连接失败、超时和延期记录保留。

## 模块边界

ETF原件、口径、具名持仓和候选解析已从预警仓储/应用/界面合同移到ETF持仓端口，查询声明及调用点同步归层，原通信操作名和持久命令身份保留，已随本次正式发行安装。另按用户要求清点全部623份C#源码，给87份旧文件补中文说明、14处关键流程补注释；移除新增注释后原代码字节逐项一致。见[本轮源码/候选](../../artifacts/etf-evidence-ports-20261030/source-acceptance.json)及[中文注释核验](../../artifacts/chinese-comments-20261030/acceptance.json)。初次候选因旧PDF夹具目录缺失未启动宿主，失败保留；改用已保存且匹配模板SHA256的公开原件副本后通过。

| 模块 | 当前职责与约束 |
| --- | --- |
| CRS.Common | 时间、指纹、有界内容、路径防护、认证文件、暂存、有界FIFO准入/取消/排空及完整核验后的文件稳定租约；不承载国家队金融规则 |
| CRS.Documents | Excel读写、PDF有界页/文字/坐标及矩形单元格；业务模板、金额计算和来源审批归各领域 |
| Worker.Abstractions / Ipc / PluginHost / PluginRunner | 后台契约、授权通信、可信包及独立进程生命周期；Ipc拥有按插件/任务隔离、有界且保留未知请求ID的公共客户端跟踪器 |
| Worker.Service | 密钥、计划/执行/命令持久化、来源预算、维护及健康；全局任务并发默认8 |
| NationalTeam.Core | ETF、成交冷热、机构目录、直接持股、行业、NAV、周期及退出规则与窄端口；无数据库或WPF依赖 |
| NationalTeam.Jobs | Etf、EtfHoldings、Equity、Nav、IndustryValuation、MarketCycle、ExitAlerts、Evidence按功能分目录；Store及同事务组合读，后台执行金融写入 |
| NationalTeamWatch / Plugin.Wpf | 版本化客户端、展示模型和页面；不直接开库或计算正式金额 |
| 收入/报税核心 | 独立个人密钥域及加密存储，未纳入国家队普通SQLite迁移，税务P0正确性仍按用户要求最后处理 |

国家队业务库及来源策略库使用普通SQLite；Worker控制库保留SQLCipher，认证仍用派生密钥。`MarketDatabaseSession`拥有连接/只读事务及评价副本结构校验，各Store拥有初始化与持久化，Repository组合窄端口。后台8个模块声明54个业务查询、27项任务，另4个框架查询；扩展从注册取得，不修改中央业务switch。EtfHoldings的`EtfEvidenceStore`已拥有原件、可比口径、审核日志及具名持仓四张表；对应仓储、用例、客户端和界面均归ETF持仓端口。预警通过同一会话的窄证据读取及来源ID冻结历史，NAV与具名历史直接消费ETF证据。原通信操作名、兼容方法名和旧持久命令仍沿用Alert名称，所有权不再由兼容命名决定。

评价准备和原件SHA核验在写名额外完成，最终短事务核对资料版本并提交事件、冻结输入、Outbox和待评价标记；持续变更最多重试三次再明确延期。份额批次和八处采集读取使用独立快照/稳定租约，保持证据和待审边界。Common须与宿主字节一致。具名公布日期与准确时刻分开，日期不补零点；旧Worker不支持新契约时明确拒绝。详细边界及历史证据见[分层实施](../implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)、[存储迁移](../implementation/NATIONAL_TEAM_STORAGE_MIGRATION_20261028.md)和[具名历史](../implementation/NATIONAL_TEAM_NAMED_ETF_HISTORY_20261028.md)。

### 工作区并发与等待预算

| 类别 | 并发和排队 | 预算与边界 |
| --- | --- | --- |
| 短读 | 各层独立2项、最多4项等待 | 准入1秒，处理5秒；已冻结结果分页属于短读 |
| 研究读 | 独立1项、最多1项等待 | 同步准入2秒、原15秒期限；不占短读名额 |
| 写入 | management组1项、数据库单写、最多4项数据库等待 | 数据库准入5秒；执行名额非阻塞，持久命令保留身份；evaluation组独立1项 |
| 后台采集 | collection组2项、插件上限4、全局8 | 插件待调度上限16，来源NotBefore和原15分钟任务期限保留 |

采集应用读1项在途/4项等待/2秒准入，评价准备读独立1项/1项等待/2秒准入；完整评价另有同样独立门槛。维护取消并排空后才关闭连接。客户端繁忙重试2秒、身份限流10秒；旧Worker普通查询保持串行，新批量/异步功能须协商。

预警总览、联合观察和周期研究先协商`research-snapshot-jobs-v1`，提交作业后凭快照ID分页。最多2项驻留，构造90秒（研究准入30秒）、客户端100秒、相关页面110秒，结果10分钟过期。普通5/15秒期限不变。作业绑定请求、证券、日期及知识时点，失败/取消/重启失效不返回半份金融结果。

## 未关闭高风险

| 项目 | 当前缺口与证据 |
| --- | --- |
| 来源和原件管理 | 沪深原件均已实际下载完成，沪市沿原ID17:09:16成功且升级后终态保持；候选未批准，24只全池报告覆盖仍不完整。明确来源截止最短30秒，未知/过期及网络失败保守退避，长限制优先。每市场仍仅一条Pending/Running；本次端口归层已正式安装，不据研究入口可用推定持仓资料完整 |
| 查询和写入性能 | 本轮86项覆盖及40项研究全部可用，批量完整回读24只；普通5/15秒预算和研究90秒构造保持。前轮批量15秒超时、86项初查一次失败及后续异步成功均保留。单次可用不证明受控提速率或所有并发；持续负载、界面竞争、取消、排空及其它长读/通知/导出仍开放 |
| 启动及资料规模 | 本次17:27:47开始保全、17:40:09 Committed，包含6048份完整快照、核验、切换及启动；显式600秒启动预算内完成，没有恢复或归档删除。系统进程早于应用健康启动，不能混称可用；档案增长和维护停机成本仍需改进，不是普通查询耗时 |
| 持仓及方向 | 298条未冻结目录不能证明全部历史完整，六份年报66行候选未批准。法人/账户、独立持有、准确公布时刻、跨期可比性、其它ETF/报告和完整组合分母仍缺。股票29条/披露15条仍为样本，缺失不补零 |
| 估值/周期/退出有效性 | 行业公布PE不等于完整PE/PB估值，NAV不等于周期；行情/广度、知识时间轴、样本外和影子验证仍开放。联合观察不发正式退出通知，金融资料验收false |
| 发行和恢复 | 当前为开发包显式豁免；签名发行、第三方权限隔离、完整跨机灾备及桌面真人交互未由本机Committed关闭 |

## 正式部署及证据

| 范围 | 当前证据 |
| --- | --- |
| ETF证据全链路归属 | 当前代次d2a337已安装EtfEvidenceStore及ETF应用/客户端/界面端口归层，四表结构不变，登记/来源/待评价/命令同事务，来源与Q15失效不能继承旧审核。20项应用边界、22项新旧发行对照及旧库重开、完整业务回归和候选28+20项通过；正式86+40项可用，原11份原件及候选内容逐项保留。见[源码/候选](../../artifacts/etf-evidence-ports-20261030/source-acceptance.json)及[正式汇总](../../artifacts/etf-evidence-ports-20261030/acceptance.json)。金融及真人界面验收仍开放 |
| 分市场原件与请求隔离 | 代次7bb915安装的27任务新协议由当前发行保留。36项采集、10项公共跟踪、36项最终评价准备及当轮候选28+20项通过；两次实际候选重启分别覆盖Pending与业务已提交但宿主回执Running，原件首见、摘要和原ID保持且不重复下载。正式沪深请求均已完成，沪市原ID成功终态跨本次升级保持。见[历史分层与候选](../../artifacts/document-collection-20261030/source-acceptance.json)、[当轮正式汇总](../../artifacts/document-collection-20261030/acceptance.json)及[本次终态保全](../../artifacts/etf-evidence-ports-20261030/acceptance.json)，真人界面和金融审核继续开放 |
| 159919解析发行 | 随前代次a1bde于昨晚23:18:01安装并由当前发行保留；各11行，2024名称跨62至63页、2025表跨66至67页。来源金样82项、原159915/510300回归90/74项、真实候选通信18项及当轮完整默认回归通过，解决方案0警告/0错误。见[实现记录](../implementation/NATIONAL_TEAM_ETF_REPORT_INDEX_20261027.md)、[159919闭环验收](../../artifacts/harvest-holders-20261029/acceptance.json)和[候选真实通信](../../artifacts/harvest-holders-pipe-20261029/acceptance.json)；不宣称远端CI或真人桌面验收 |
| 已恢复的SSE原请求 | 同一`c6f5d272480b428894347483d3a27d0d`最终绑定执行`73c05a20b15c4a6382d19311aad062e8`，01:43:19.544启动、HTTP200、01:43:33.646 Succeeded并返回11行NeedsReview；旧Failed记录仍保留。见[正式终态](../../artifacts/harvest-holders-installed-20261029/sse-510300-2024-status-20261030/document-status.json) |
| 159919正式闭环 | 两份年报分别Succeeded/11行NeedsReview，2024原请求在08:34:33.936启动、08:34:46.945完成，哈希和逐行内容对照独立图片摘录一致。见[2024原请求](../../artifacts/harvest-holders-installed-20261029/159919-2024/document-status.json)、[2025原请求](../../artifacts/harvest-holders-installed-20261029/159919-2025/document-status.json)和[四条汇金待审资料](../../artifacts/harvest-holders-20261029/REVIEW_PACKET.md)；首轮Pending快照另存，不删除历史延期 |
| 原请求跨正式升级恢复 | SSE510300-2025原请求`3b97b7ea5cf54498b1330eee3b2241db`及09:06:23.1191847截止跨本次维护逐项保留，09:06:23.122才启动，09:06:24.921 Succeeded/11行NeedsReview，执行`26a3fd69cf2347cfa171a9440edbc8d0`直接绑定回执。见[原请求恢复核验](../../artifacts/document-source-budget-installed-20261030/pending-request-restoration.json)，没有用新ID或重置来源绕过期限 |
| 预算及批量客户端续项 | 已安装明确来源预算及批量异步路径，保留冻结分母、元数据校验和旧服务兼容，普通5/15秒期限不变。见[源码/候选](../../artifacts/document-source-budget-20261030/source-acceptance.json)、[旧批量超时](../../artifacts/harvest-holders-installed-20261029/after/feature-audit-20261030/feature-audit.json)、[新正式批量](../../artifacts/document-source-budget-installed-20261030/after/acceptance-batch/acceptance-batch.json)及[旧事实对照](../../artifacts/document-source-budget-installed-20261030/after/baseline-comparison.json)；真人桌面交互未验收 |
| 前轮性能及机制 | 评价准备36项、来源恢复24项及旧候选跨重启19项等历史范围，均见[前轮验收](../../artifacts/etf-document-recovery-20261029/acceptance.json)。正式40项研究与旧性能样本有各自时点，不冒充本次负载测试 |

整个NationalTeam目标仍未完成。源码、候选包、正式安装、金融资料和模型有效性分别闭合；Pending、NeedsReview、NotDisclosed和不可计算状态均不能写成全部成功。

