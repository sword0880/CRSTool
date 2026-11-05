# 国家队模块分层、并发与存储

<a id="national-team-application-boundary"></a>
<a id="national-team-concurrency"></a>
<a id="national-team-etf-storage-boundary"></a>
<a id="national-team-modular-refactor"></a>
<a id="national-team-storage-migration"></a>
<a id="national-team-plugin-progress"></a>

模块边界与发布状态的主入口为[当前架构](../architecture/CURRENT_ARCHITECTURE.md)和[架构状态](../architecture/ARCHITECTURE_STATUS.md)。本文保留实现机制，不重复维护每轮构建、PID或部署成功台账。

## 功能所有权

| 层 | 职责 |
| --- | --- |
| NationalTeam.Core | 领域规则、DTO和按功能划分的必需端口，不依赖WPF或数据库 |
| NationalTeam.Jobs | 采集、来源策略、类型化用例、Store和一致性读写 |
| NationalTeamWatch | 页面、展示模型、功能客户端，经Worker授权管道交互 |
| Common / Documents | 纯BCL时间/指纹/路径/有界传输，以及通用PDF/Excel读取，不承载金融模板与来源许可 |

后台`NationalTeamApplication`只装配、初始化和释放各功能用例；`MarketApplicationSession`管理短写门槛、独立读取、进度和关闭。后台不实现界面工作区合同，功能用例不持有完整总仓储。旧`EtfWorkspace`和无用兼容壳退役，不恢复跨项目业务源码链接。

`MarketDatabaseSession`拥有连接、命令、取消、只读快照和释放；`NationalTeamSchemaInitializer`协调模块初始化；`NationalTeamStores`装配Store。`MarketRepository`为组合转发入口，SQL和表初始化属于相应功能Store。各功能按ETF、ETF证据、直接持股、NAV、行业、周期、预警和原件分目录。

ETF原件、可比性、具名持仓和候选属于`EtfHoldings`，不借用预警仓储或界面合同。预警通过窄事实端口消费ETF资料；事件、冻结输入及Outbox沿既有事务提交，在途投递未知不自动重发。

## 并发、原件与事务

全局并发8时国家队总额度4，默认collection 2、evaluation 1、management 1。相同JobKey不重入；等待采集额度不占满全局名额，管理/评价可按组推进。实际额度仍受宿主配置及公平上限控制。

每次执行的进度、取消及请求计数独立。不同来源有限并行，网络等待不持数据库写门槛；提交、审核、命令幂等和金融事件保持短事务。来源间隔、共享冷却、响应预算与白名单不因增加并行而放宽。

完整性巡检先冻结来源身份/哈希，门槛外读取原件，再核对当前身份并短事务提交。同类巡检串行，新增或改变来源不冒充已核验。查询本身不登记Q15或修复事实；损坏与待审继续失败关闭。

## 加密与维护边界

国家队`market.db`、`source-policy.db`及市场快照使用普通SQLite，默认提供方`sqlite`。收入/报税保险库和Worker管理存储继续加密；国家队审核审计仍可使用独立认证密钥及HMAC，不能据此说研究数据库仍加密。

正常启动遇旧加密库明确拒绝，不重建空库。`LegacyNationalTeamExport`只处理固定两库的副本，恢复WAL、核对完整性、外键、Schema、逐行逻辑摘要及`sqlite_sequence`，发布到全新目标，原库和原WAL保留。

双库保全迁移必须先停写、快照和授权，持有`runner.lock`，用认证日志绑定操作及固定文件集合。两库全部准备通过再切换，旧库/WAL/SHM归档；恢复沿同一操作和已认证摘要推进，完成重放不覆盖之后产生的新事实。日志、快照或文件集合有歧义时保留现场。

维护健康要求全部应启动的后台入口与实际健康集合一致；故障不能仅凭宿主可查询就写Healthy。普通启动继续隔离单插件故障并报告Degraded。2026-10-28正式保全迁移已完成是历史验收事实，不自动关闭后续查询性能、覆盖及模型门槛。

## 验证与后续工作

`tests/Verify-NationalTeamFeatureLayout.ps1`与`tests/Verify-Architecture.ps1`检查目录、XAML配对、端口和依赖。主要专项：`--application-boundaries`、`--repository-contracts`、`--read-isolation`、`--public-storage`、`--concurrency`及完整WorkerETF包联调，详见[测试说明](../../tests/README.md)。

保全迁移与后续ETF证据归层的历史凭据位于`artifacts/national-team-storage-migration-checkpoint-20261028.json`、`artifacts/etf-evidence-store-20261030/acceptance.json`及`artifacts/etf-evidence-ports-20261030/acceptance.json`。金融资料完整性、真人审核、全池覆盖、真实通知、模型有效性和正式发布须分别验收；代码归层及注释整理不代表这些目标完成。
