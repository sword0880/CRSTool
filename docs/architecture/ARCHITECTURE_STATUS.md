# 当前架构状态

更新：2026-10-30。本文件是模块边界、未关闭高风险、正式部署及证据的唯一当前索引；实施文档保留历史。时刻均为北京时间，源码、隔离回放、正式后台和金融审核分别验收。

当前正式发行：代次`a1bde7f66fc44405aedf3026dee47d3b`于2026-10-29 23:18:01 Committed，PID72468 / Running / Auto / LocalService。新旧程序各182项、6018份快照及原密钥摘要逐项通过；7项计划、24只ETF配置、29条股票投影和10条旧执行保留。发行索引SHA256：`7FB12199010305EAE6424C4DA555C27A04A0BEBF707B23DF58677C8F7CB362A4`。见[保全回执](../../artifacts/harvest-holders-installed-20261029/preservation-acceptance.json)、[旧事实对照](../../artifacts/harvest-holders-installed-20261029/after/baseline-comparison.json)和[最终健康](../../artifacts/harvest-holders-installed-20261029/final-health.json)。

最新资料回查：2026-10-30 08:11的83项正式查询全部返回，同一健康进程；24只ETF、298条目录、8份登记原件，三份年报33行NeedsReview、一份中期NotDisclosed、四份UnsupportedTemplate。已核验具名持仓0行，实际持仓未知。159919解析已安装，但2024原件请求仍Pending，2025原件尚未提交，不能把研究副本22行算成正式覆盖。见[正式覆盖](../../artifacts/harvest-holders-installed-20261029/after/report-coverage/etf-report-coverage.json)。

## 模块边界

| 模块 | 当前职责与约束 |
| --- | --- |
| CRS.Common | 时间、指纹、有界内容、路径防护、认证文件、暂存、有界FIFO准入/取消/排空及完整核验后的文件稳定租约；不承载国家队金融规则 |
| CRS.Documents | Excel读写、PDF有界页/文字/坐标及矩形单元格；业务模板、金额计算和来源审批归各领域 |
| Worker.Abstractions / Ipc / PluginHost / PluginRunner | 后台契约、授权通信、可信包及独立进程生命周期 |
| Worker.Service | 密钥、计划/执行/命令持久化、来源预算、维护及健康；全局任务并发默认8 |
| NationalTeam.Core | ETF、成交冷热、机构目录、直接持股、行业、NAV、周期及退出规则与窄端口；无数据库或WPF依赖 |
| NationalTeam.Jobs | Etf、EtfHoldings、Equity、Nav、IndustryValuation、MarketCycle、ExitAlerts、Evidence按功能分目录；Store及同事务组合读，后台执行金融写入 |
| NationalTeamWatch / Plugin.Wpf | 版本化客户端、展示模型和页面；不直接开库或计算正式金额 |
| 收入/报税核心 | 独立个人密钥域及加密存储，未纳入国家队普通SQLite迁移，税务P0正确性仍按用户要求最后处理 |

国家队业务库及来源策略库使用普通SQLite；Worker控制库保留SQLCipher，认证仍用派生密钥。`MarketDatabaseSession`拥有连接/只读事务，各Store拥有初始化与持久化，Repository组合窄端口。后台由8个功能模块声明54个业务查询和25项任务，另4个框架查询；任务、分类及诊断白名单从注册取得，扩展业务不修改中央分派switch。

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
| 来源和原件管理 | SSE原请求已于01:43:19到期后执行，01:43:33下载HTTP200并成功登记。SZSE159919请求`b2be8ab6fe484a0c91ac4cfe75a9cfa4`保留Pending/RetryDeferred，NotBefore08:34:33；进度中的实际来源截止08:10:28被默认30分钟预算扩大。修正先反例失败后28项通过，尚未安装。同一管理任务只允许一条Pending/Running，新的其它操作会AlreadyRunning而非隐含排队；来源下载与其它人工管理的隔离仍开放 |
| 查询和写入性能 | 独立评价读、短写提交及异步研究已安装；持续负载、界面竞争、取消和在途维护排空仍需正式证据。前轮40项研究均返回但总览/联合/批量约12至13秒，不计算受控提速率；其它长读/通知领取/导出仍需归因 |
| 启动及资料规模 | 本次从Prepared23:05:39到Committed23:18:01约741秒，包含完整快照、核验、切换和启动，显式600秒启动预算内完成；不是普通查询耗时。档案增长和启动开销未关闭，无归档删除或旧库回退 |
| 持仓及方向 | 298条未冻结目录不能证明全部历史完整。正式33行候选未批准；159919两份研究原件/22行不能冒充正式登记。法人/账户、独立持有、准确公布时刻、跨期可比性、其它ETF/报告和完整组合分母仍缺。股票29条/披露15条仍为样本，缺失不补零 |
| 估值/周期/退出有效性 | 行业公布PE不等于完整PE/PB估值，NAV不等于周期；行情/广度、知识时间轴、样本外和影子验证仍开放。联合观察不发正式退出通知，金融资料验收false |
| 发行和恢复 | 当前为开发包显式豁免；签名发行、第三方权限隔离、完整跨机灾备及桌面真人交互未由本机Committed关闭 |

## 正式部署及证据

| 范围 | 当前证据 |
| --- | --- |
| 159919解析发行 | 已安装固定SHA/路径版式，各11行，2024名称跨62至63页、2025表跨66至67页。来源金样82项、原159915/510300回归90/74项、真实候选通信18项及完整默认回归通过，完整解决方案0警告/0错误；未声称远端CI或真人桌面验收。见[实现记录](../implementation/NATIONAL_TEAM_ETF_REPORT_INDEX_20261027.md)、[研究下载](../../artifacts/harvest-holders-20261029/research-downloads.json)及[候选真实通信](../../artifacts/harvest-holders-pipe-20261029/acceptance.json) |
| 已恢复的SSE原请求 | 同一`c6f5d272480b428894347483d3a27d0d`最终绑定执行`73c05a20b15c4a6382d19311aad062e8`，01:43:19.544启动、HTTP200、01:43:33.646 Succeeded并返回11行NeedsReview；旧Failed记录仍保留。见[正式终态](../../artifacts/harvest-holders-installed-20261029/sse-510300-2024-status-20261030/document-status.json) |
| 159919正式缺口 | 2024原请求只延期、没有RequestStarted或登记；2025未提交。见[原请求](../../artifacts/harvest-holders-installed-20261029/159919-2024/document-status.json)、[具体分类执行](../../artifacts/harvest-holders-installed-20261029/document-history-20261030.jsonl)及[等待队列](../../artifacts/harvest-holders-installed-20261029/activity-20261030.jsonl)；延期回执无执行ID，具体分类按唯一时间窗与相同截止核对，不虚构直接关联字段 |
| 待安装的预算修正 | 明确来源截止按其时刻（最短30秒），未知/过期截止和网络失败仍保守退避，长来源冷却优先。反例失败及28项通过保存在[修正前](../../artifacts/document-source-budget-before-20261030.log)、[修正后](../../artifacts/document-source-budget-after-20261030.log)；候选与正式回执分别留证，不能视为当前服务已更新 |
| 前轮性能及机制 | 评价准备36项、来源恢复24项及旧候选跨重启19项等历史范围，均见[前轮验收](../../artifacts/etf-document-recovery-20261029/acceptance.json)。正式40项研究与旧性能样本有各自时点，不冒充本次负载测试 |

整个NationalTeam目标仍未完成。源码、候选包、正式安装、金融资料和模型有效性分别闭合；Pending、NeedsReview、NotDisclosed和不可计算状态均不能写成全部成功。

