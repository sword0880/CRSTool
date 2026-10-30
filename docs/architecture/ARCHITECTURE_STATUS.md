# 当前架构状态

更新：2026-10-30。本文件是模块边界、未关闭高风险、正式部署及证据的唯一当前索引；实施文档保留历史。时刻均为北京时间，源码、隔离回放、正式后台和金融审核分别验收。

当前正式发行：代次`7bb915b629d3420cb5222451d3004f1e`于2026-10-30 11:10:04 Committed，PID66488 / Running / Auto / LocalService。新旧程序各182项、6044份快照及原密钥摘要逐项通过；7项计划、24只ETF配置、29条股票投影、10条旧执行和原11份登记原件逐项保留。索引SHA256：`6BAEC03D1A8E3BF912B1EF4C30678250FEFEE016485C8FFCDB02D10099A40E63`。见[正式汇总](../../artifacts/document-collection-20261030/acceptance.json)、[保全回执](../../artifacts/document-collection-installed-20261030/preservation-acceptance.json)及[最终健康](../../artifacts/document-collection-installed-20261030/final-health.json)。未恢复旧库或删除归档。

已安装分市场原件采集、公共客户端请求跟踪、既有明确来源等待预算及异步批量路径。最终解决方案0警告/0错误；36项采集、10项公共跟踪、28项旧来源恢复及业务契约通过。候选28项实际进程验证两种重启窗口，另20项验证旧入口恢复。完整默认回归早于最终SQLite元数据查询归层及测试专用扩展，最终36项评价准备复验归层改动，不冒充又一次完整默认回归。见[源码/候选证据](../../artifacts/document-collection-20261030/source-acceptance.json)。

最新资料回查：24只ETF、298条目录、11份登记原件；510300、159915、159919各两份年报共66行NeedsReview，一份中期NotDisclosed、四份UnsupportedTemplate。已核验具名持仓0行，实际持仓未知。本轮升级前后86项均可用且健康进程稳定。见[本轮完整覆盖](../../artifacts/document-collection-installed-20261030/after/report-coverage/etf-report-coverage.json)及[逐项保全对照](../../artifacts/document-collection-installed-20261030/after/baseline-comparison.json)。前轮86项初查有一次失败及后续顺序恢复的原记录保留，不用本轮成功改写旧负例。

新协议`etf-document-collection-v1`及沪深两项手动任务已正式安装，任务共27项。深市159919-2025经新入口Completed/Succeeded，11行仍NeedsReview；沪市510300-2025原请求`70f579ede5384e478443fbb060ddd9ee`连接失败后保留Pending/RetryDeferred至14:32:22.742，未完成此次重采。14:08:22是来源层截止，不能替代任务层30分钟网络失败预算。新请求按任务跟踪，来源等待不占用人工管理；旧入口及原ID保留，登记和待评价标记同事务，不自动批准持仓。见[深市正式回执](../../artifacts/document-collection-installed-20261030/szse-159919-2025/document-status.json)、[沪市执行原因](../../artifacts/document-collection-installed-20261030/sse-510300-2025/execution-probe.jsonl)、[实际等待截止](../../artifacts/document-collection-installed-20261030/after/task-activity.jsonl)及[实施续项](../implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)。

## 模块边界

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

国家队业务库及来源策略库使用普通SQLite；Worker控制库保留SQLCipher，认证仍用派生密钥。`MarketDatabaseSession`拥有连接/只读事务及评价副本结构校验，各Store拥有初始化与持久化，Repository组合窄端口。后台由8个功能模块声明54个业务查询和27项任务，另4个框架查询；任务、分类及诊断白名单从注册取得，扩展业务不修改中央分派switch。EtfHoldings拥有新原件窄端口；既有AlertStore仍拥有原件表初始化与登记事务，不能据此称ETF持久化已全部迁出。

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
| 来源和原件管理 | 新原件采集已脱离管理任务，候选与正式深市回执通过；沪市新请求连接失败仍待14:32:22，不能把旧已存在原件当本次采集完成。明确来源截止最短30秒，未知/过期及网络失败继续保守退避，长限制优先。每个市场仍只允许一条Pending/Running；旧管理入口仅保留原命令恢复，不转新ID绕过等待 |
| 查询和写入性能 | 08:21的40项回查有一项批量15秒RequestTimedOut，原失败保留。客户端切到已协商的异步作业后，在旧正式Worker完整回读24只14251ms；本次新正式Worker完整回读24只16875ms、同PID，超过原15秒仍正确完成。初轮覆盖86项也有一项读取失败，顺序复核86项全可用。不能计算受控提速率或推定所有并发均可用；持续负载、界面竞争、取消、维护排空及其它长读/通知/导出仍开放 |
| 启动及资料规模 | 本次10:56开始保全、11:10:04 Committed，包含6044份完整快照、核验、切换及启动；显式600秒启动预算内完成，没有恢复或归档删除。系统进程早于应用健康启动，两者不能混称可用；档案增长和维护停机成本仍需改进，不是普通查询耗时 |
| 持仓及方向 | 298条未冻结目录不能证明全部历史完整，六份年报66行候选未批准。法人/账户、独立持有、准确公布时刻、跨期可比性、其它ETF/报告和完整组合分母仍缺。股票29条/披露15条仍为样本，缺失不补零 |
| 估值/周期/退出有效性 | 行业公布PE不等于完整PE/PB估值，NAV不等于周期；行情/广度、知识时间轴、样本外和影子验证仍开放。联合观察不发正式退出通知，金融资料验收false |
| 发行和恢复 | 当前为开发包显式豁免；签名发行、第三方权限隔离、完整跨机灾备及桌面真人交互未由本机Committed关闭 |

## 正式部署及证据

| 范围 | 当前证据 |
| --- | --- |
| 分市场原件与请求隔离 | 代次7bb915已安装27任务的新协议。36项采集、10项公共跟踪、36项最终评价准备及候选28+20项通过；两次实际候选重启分别覆盖Pending与业务已提交但宿主回执Running，原件首见、摘要和原ID保持且不重复下载。正式深市完成，沪市新请求仍Pending。见[本轮分层与候选](../../artifacts/document-collection-20261030/source-acceptance.json)及[正式汇总](../../artifacts/document-collection-20261030/acceptance.json)，真人界面和金融审核继续开放 |
| 159919解析发行 | 随前代次a1bde于昨晚23:18:01安装并由当前发行保留；各11行，2024名称跨62至63页、2025表跨66至67页。来源金样82项、原159915/510300回归90/74项、真实候选通信18项及当轮完整默认回归通过，解决方案0警告/0错误。见[实现记录](../implementation/NATIONAL_TEAM_ETF_REPORT_INDEX_20261027.md)、[159919闭环验收](../../artifacts/harvest-holders-20261029/acceptance.json)和[候选真实通信](../../artifacts/harvest-holders-pipe-20261029/acceptance.json)；不宣称远端CI或真人桌面验收 |
| 已恢复的SSE原请求 | 同一`c6f5d272480b428894347483d3a27d0d`最终绑定执行`73c05a20b15c4a6382d19311aad062e8`，01:43:19.544启动、HTTP200、01:43:33.646 Succeeded并返回11行NeedsReview；旧Failed记录仍保留。见[正式终态](../../artifacts/harvest-holders-installed-20261029/sse-510300-2024-status-20261030/document-status.json) |
| 159919正式闭环 | 两份年报分别Succeeded/11行NeedsReview，2024原请求在08:34:33.936启动、08:34:46.945完成，哈希和逐行内容对照独立图片摘录一致。见[2024原请求](../../artifacts/harvest-holders-installed-20261029/159919-2024/document-status.json)、[2025原请求](../../artifacts/harvest-holders-installed-20261029/159919-2025/document-status.json)和[四条汇金待审资料](../../artifacts/harvest-holders-20261029/REVIEW_PACKET.md)；首轮Pending快照另存，不删除历史延期 |
| 原请求跨正式升级恢复 | SSE510300-2025原请求`3b97b7ea5cf54498b1330eee3b2241db`及09:06:23.1191847截止跨本次维护逐项保留，09:06:23.122才启动，09:06:24.921 Succeeded/11行NeedsReview，执行`26a3fd69cf2347cfa171a9440edbc8d0`直接绑定回执。见[原请求恢复核验](../../artifacts/document-source-budget-installed-20261030/pending-request-restoration.json)，没有用新ID或重置来源绕过期限 |
| 预算及批量客户端续项 | 已安装明确来源预算及批量异步路径，保留冻结分母、元数据校验和旧服务兼容，普通5/15秒期限不变。见[源码/候选](../../artifacts/document-source-budget-20261030/source-acceptance.json)、[旧批量超时](../../artifacts/harvest-holders-installed-20261029/after/feature-audit-20261030/feature-audit.json)、[新正式批量](../../artifacts/document-source-budget-installed-20261030/after/acceptance-batch/acceptance-batch.json)及[旧事实对照](../../artifacts/document-source-budget-installed-20261030/after/baseline-comparison.json)；真人桌面交互未验收 |
| 前轮性能及机制 | 评价准备36项、来源恢复24项及旧候选跨重启19项等历史范围，均见[前轮验收](../../artifacts/etf-document-recovery-20261029/acceptance.json)。正式40项研究与旧性能样本有各自时点，不冒充本次负载测试 |

整个NationalTeam目标仍未完成。源码、候选包、正式安装、金融资料和模型有效性分别闭合；Pending、NeedsReview、NotDisclosed和不可计算状态均不能写成全部成功。

