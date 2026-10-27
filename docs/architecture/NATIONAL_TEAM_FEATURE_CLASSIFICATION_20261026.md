# NationalTeamWatch 功能分层与扩展边界

更新日期：2026-10-27。项目尚未正式上线，按用户要求直接调整接口，不保留旧接口、旧适配器或旧程序集兼容壳。本文描述当前结构；实施验证和剩余门槛见[分层重构续项](../implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)。

## 项目与依赖方向

- `NationalTeam.Core`：领域事实、纯规则及按功能划分的应用端口，引用外层 `CRS.Common`。
- `NationalTeam.Jobs`：采集、用例协调、后台任务、原件及普通SQLite研究存储；不依赖 WPF 或个人税务库。
- `NationalTeamWatch`：页面与 Worker 客户端；只读取DTO、提交命令和显示状态，不下载原件、不操作数据库、不计算金融指标。
- 外层 `CRS.Common`：北京时间、JSON时间、SHA256、读取公平租约及受限HTTP正文读取。来源许可、业务阈值、原件路径、密钥和数据库连接不进入公共库。
- 外层 `CRS.Documents`：有界PDF文字与坐标读取；只依赖PdfPig，富途与国家队共用，不含券商或持仓规则。
- 调度、来源共享预算和通知分别复用既有 Worker、`IBackgroundSourceContext` 和 `CRS.Notifications`，不重复建设。

## 按功能归属

| 功能目录 | 事实/规则 | 当前产品入口与边界 |
| --- | --- | --- |
| Etf | ETF目录、基金总份额、份额变化、采集与覆盖 | ETF份额与市场成交页；份额及市场总量不能归因到国家队 |
| EtfHoldings | 披露持有人候选、固定PDF模板、解析端口及草稿 | 原件核验页主动读取；首批两份510300年报，候选不自动升级具名证据，配置池扩展和报告发现仍开放 |
| Equity | 具名账户直接持股、逐账户审核、公告索引、历史原文 | 机构直接持股页；报告期持股变化不能称每日成交 |
| IndustryValuation | 国证行业公布PE、样本估值、基本面质量门槛 | 公布PE及行业样本指数/PB页；不同统计宇宙分开，低PE不等于低估 |
| MarketCycle | 市场成交活动、指数序列、周期研究、来源审批 | 指数研究及ETF页的市场活动；顶部/底部仍缺完整真实输入及样本外验证 |
| Nav | 单位净值、修订、知识时点及同日研究 | 顶部“NAV与份额金额”入口；同日NAV与份额研究，不代表机构成交或全市场周期 |
| ExitAlerts | 规则、证据、事件、历史回算、Outbox和投递 | ETF页内预警中心；独立退出导航/完整金融模型尚未交付 |
| Evidence / Sources | 原件、批次核验、来源许可和HTTP策略 | 插件内跨功能基础设施，不承载新业务 |
| Shared / Storage | 装配契约、连接生命周期、初始化顺序和普通SQLite存储组合 | 明确例外目录，禁止继续堆放领域特有实现 |

Core 的 `Domain`、`Application`，Jobs 的 `Background`、`Application`、`Infrastructure`，UI 的 `Application/Worker`、`Presentation` 均按以上业务归档。六个工作区端口位于各自 `Application/<功能>`；`IWorkspaceAccess` 与 `INationalTeamWorkspace` 留在Shared作为装配契约。

## 工作区接口与页面

`INationalTeamWorkspace` 仅继承六个功能端口，不重复声明成员；`NationalTeamProtocol.cs` 单独存放当前请求和分页DTO。旧 `IEtfWorkspace` 及三套Legacy UI适配器已删除。当前接口可直接重构，不承担旧二进制兼容承诺。

六个界面合同全部要求显式实现，不含默认抛错、空集合或忽略筛选的回退。后台不再实现界面合同；`NationalTeamApplication`装配八组功能用例，`MarketApplicationSession`拥有应用读写门槛与生命周期，各模块仅访问类型化仓储端口。旧后台`EtfWorkspace`删除，细节见[应用边界记录](../implementation/NATIONAL_TEAM_APPLICATION_BOUNDARY_20261026.md)。

桌面宿主只注册一个可见“国家队研究工作台”。内部目录由 `NationalTeamWorkspaceCatalog` 管理分类、路由、证据限制和视图工厂；按需创建当前页，切换释放旧视图引用。三个旧平铺宿主路由已移除；ETF、持股、NAV、公布PE与样本指数由工作台内部目录导航。无调用方的 `IndustryResearchHostPage` 同步删除。

NAV份额研究使用独立跨模块只读投影，冻结输入按通用有界JSON块传输；公共机制进入`CRS.Common`，金融计算仍在插件应用/领域层。范围和数据缺口见[NAV工作区记录](../implementation/NATIONAL_TEAM_NAV_WORKSPACE_20261026.md)。

工作台采用顶部“资金与持仓 / 行业与估值”分类下拉菜单，不再保留占用正文宽度的第二层侧栏。当前研究说明按需展开，正文独占整行。通用菜单控件及样式归属外层 `CRS.Plugin.Wpf/PluginSectionNavigation`，插件仅提供分类与目的地；菜单不创建业务页、不查询数据，选中态在页面构造成功后提交。

尚无真实输入的顶部/底部、独立退出导航不以空壳入口充数。现有页面展示缺失或不可判定状态，不将旧服务不支持、超时或缺失资料折算为零或成功。

## 存储与事务

当前已独立：`EtfStore`、`IndustryPeStore`、`MarketCalendarStore`、`IndexSeriesStore`、`MarketActivityStore`、`NavStore`、`EquityStore`、`AlertStore` 和 `EvidenceIntegrityStore`。预警模块内部装配 `AlertEventStore`、`AlertOutboxStore`、`AlertDeliveryStore`。

`EquityStore` 分持股、公告和历史原文文件，经过 `IMarketRepositorySession` 取得连接、原件、取消与命令幂等能力。`AlertStore` 分评价、证据、历史、生命周期和领取文件，跨模块读取只经过 `IAlertMarketFacts`。总仓储的这两个领域入口只转发，不再保留SQL。

这些模块继续使用同一个数据库会话及已有提交点，保留：

1. 事件、变更日志、冻结输入与通知Outbox一起提交；任何一项失败全部回滚。
2. 通知持久领取后才允许外发；重启将InFlight转OutcomeUnknown，禁止自动或人工安全重试入口重复提交未知结果。
3. 接受/拒绝修订、首见/知识截止、原件哈希、逐账户身份和期间可比基线不因重构改变。
4. 正式查询独立只读连接和一致性快照；取消只作用于当前查询，不停止背景采集。
5. `market.db`、Schema及业务表沿用当前定义；不为目录重构丢弃既有事实。

`EvidenceIntegrityStore`独立负责原件扫描、只读失败关闭、后台Q15损坏/恢复审计及批次应用，继续使用同一会话与事务；总仓储仅保留转发。验收按128证券分组读取份额和最近20个交易日的采集收据，复用本次哈希结果并按需补验阶段原件；不能跨请求复用核验结论。

ETF核心已移入`EtfStore`；连接、独立读取与释放由`MarketDatabaseSession`拥有，初始化由`NationalTeamSchemaInitializer`和各功能Store拥有。国家队两库和快照按用户要求改为普通SQLite，收入/报税加密保留，旧国家队加密资料只能显式离线保留式导出。见[存储边界记录](../implementation/NATIONAL_TEAM_ETF_STORAGE_BOUNDARY_20261026.md)。

`AlertStore`仍收拢部分用例协调，来源审批注册表仍有聚合partial实现。全池验收的完整性依赖范围仍较宽，后续需将ETF数据门槛与通知冻结输入核验明确分离，再缩小查询依赖；不能只为提速删除原件核验。以上属于下一步解耦工作，不能称整个插件层重构已经结束。

## 门禁与维护

`tests/Verify-NationalTeamFeatureLayout.ps1` 检查功能目录、XAML配对、领域端口、旧接口退役及SQL不能回流总仓储。`Verify-Architecture.ps1` 联合项目依赖图，防止公共工程反向依赖业务、Worker引用WPF或插件接触税务。

`CRS.Time` 和类型转发已删除，各层直接引用 `CRS.Common`，Runner清单只打包当前公共实现。新的完整包必须独立生成并核对可信索引；源码/隔离回归、候选包和已安装Worker验收分别记录。
