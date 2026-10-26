# NationalTeamWatch 插件功能分类与增长治理（2026-10-26）

## 背景和本次原则

国家队 ETF、直接持股、行业相对估值、市场周期、预警和证据核验会持续增长。如果全部注册成桌面“扩展功能”侧栏子菜单，用户很难找到功能，宿主也将被迫理解国家队业务分类。

本次在原有 `NationalTeamWatch / Core / Jobs` 进程边界上，新增**插件内分类工作台**；桌面宿主只出现一个“国家队研究工作台”，其内部按证据责任分类，不再将所有功能堆在一层菜单上。

## 当前实际分类（已接入 vs 待实施）

| 模块 | 内部导航 | 证据含义 | 当前可用性 |
| --- | --- | --- | --- |
| 资金与持仓 | ETF份额与市场成交 | 全市场 ETF 基金份额不等于国家队成交 | 正式既有页面 |
| 资金与持仓 | 机构直接持股 | 经核验机构财报持股；报告期差异不等于每天卖出 | 正式既有页面 |
| 行业与估值 | 全行业公布PE | 国证一级行业剔亏公布PE，研究历史位置 | 正式既有页面，本 PR 增双口径筛选 |
| 行业与估值 | 行业样本指数与PB | 国证1000子集指数，统计宇宙不同于全行业 | 正式既有页面 |
| 市场周期 | 市场顶部/底部 | ETF、广度、价格及基本面多证据融合 | **未来**：领域原型仍不足以成为正式导航 |
| 风险与预警 | 国家队大规模退出 | 需具名机构披露、可比性、长历史等多重门槛 | **未来**：有原型/后台能力，不冒充已可用 |
| 数据与核验 | 来源健康、回补与审核 | 统一读取Worker健康/任务中心，按子功能证据边界管理 | **未来**：暂复用主程序任务中心，不另造空壳 |

**严禁**为了菜单完整而给尚无真实数据的模块添加无效按钮或伪造正常状态。进入正式工作台必须同时满足页面可创建、真实 Worker 业务契约可读取，以及缺失资料故障关闭。

## 分类导航机制

1. `CRS.Plugin.Wpf.PluginPageContribution` 加可选属性 `ShowInNavigation`，默认 `true`，既有插件不用改。宿主只对该属性为真的插件入口创建侧栏子项。
2. NationalTeam 注册唯一可见路由 `national-team-workspace`。原有 `etf-monitor`、`direct-equity` 和 `industry-research` 路由仍注册但不重复出现在侧栏，保证老的宿主/测试依赖路由名不被删掉。**只保证路由身份保留；旧二进制/插件包的运行时 ABI 必须单独验收**。
3. 内部分类目录位于 `Presentation/Navigation/NationalTeamWorkspaceCatalog.cs`，每条项目有稳定 RouteKey、CategoryKey、显示名、证据限制和视图工厂。目录校验严格拒绝重复、未知分类或无证据边界。
4. `NationalTeamWorkspacePage.cs` 按分类构造左侧索引；初次打开显示说明，不创建任何子功能页面；点击时仅创建当前子页，切换时释放上个视图引用。ViewModel 与查询身份仍由既有Session管理，不在视图执行采集、SQL、金融运算。
5. 工作台没有跨证据类别的混合数据总表：全行业PE及国证1000指数PB必须分别进入自己的模型，不能由导航层直接 join。

## 本轮已实施：既有源码按业务领域归档（2026-10-26）

本轮不再只规定“以后新增代码怎么放”，而是把现有生产源码按功能搬入目录。**138 个既有源码文件做路径迁移**，同时将 XAML 与对应 code-behind 成对移动。保留既有 C# namespace、公开类型名、方法签名、数据库表/Schema、插件 ID、路由键和 Worker 调度契约；本轮不改业务算法，不声称已经完成真正的模块解耦。

### 目录结构

    src/Plugins/
      CRS.Plugins.NationalTeam.Core/
        Domain/
          Etf/                  # ETF份额、ETF目录与度量
          Equity/               # 机构直接持股、历史披露与官方公告模型
          Evidence/             # 证据等级与来源语义
          ExitAlerts/           # 预警规则、事件与重放模型
          IndustryValuation/    # 行业PE、行业研究、质量证据闸门
          MarketCycle/          # 指数研究、周期规则、已审核交易日历
          Nav/                  # 基金NAV研究模型
        Application/
          Etf/ Equity/ Evidence/ ExitAlerts/
          IndustryValuation/ MarketCycle/ Nav/
          Sources/              # 来源策略端口
          Shared/               # 兼容性聚合契约、跨领域读取会话
      CRS.Plugins.NationalTeam.Jobs/
        Background/
          ExitAlerts/ IndustryValuation/ MarketCycle/ Nav/
          NationalTeamBackgroundEntry.cs
        Application/
          Etf/ Equity/ ExitAlerts/
          IndustryValuation/ MarketCycle/ Nav/
        Infrastructure/
          Etf/ Equity/ Evidence/ ExitAlerts/
          IndustryValuation/ MarketCycle/ Nav/
          Shared/               # 共享SQLite会话、通用日历与公共仓储入口
          Sources/              # HTTP网关、限流/冷却策略、通用响应证据
          Storage/              # 正式存储提供器
        Resources/               # 保留嵌入资源路径，避免更改ManifestResource名称
      CRS.Plugins.NationalTeamWatch/
        Application/Worker/
          Etf/ Equity/ ExitAlerts/ IndustryValuation/ MarketCycle/
        Presentation/
          Navigation/            # 唯一内部分类目录与按需加载工作台
          Etf/ Equity/ ExitAlerts/ IndustryValuation/ MarketCycle/

### 分类边界与保留项

- **ETF份额**：目录、采集、份额差额、覆盖率、Dashboard与对应页面/ViewModel。
- **直接持股与披露**：公司/机构持股、历史PDF、公告索引和对应查询/展示。
- **行业估值**：国证行业公布PE、修订日历、行业质量闸门和行业研究页面。
- **市场周期**：指数序列、市场成交活动、来源审批、周期研究与对应页面。
- **退出预警**：规则评估、事件/证据/生命周期、交付记录、Worker任务与页面。
- **NAV**：NAV来源、存储与研究。
- **Evidence / Sources / Shared**：只放跨领域原件存储、来源访问控制、共享会话/聚合契约；不能把业务特有代码为了省事继续放回共享目录。
- NationalTeamPluginEntry.cs、GlobalUsings.cs、项目文件、插件清单与锁文件仍留在插件根目录，作为装配/构建元数据，不是业务功能。
- Resources/cnd-2025-annual.json 保留原路径，防止隐式嵌入资源名变化。导航仍只公开“国家队研究工作台”；旧路由注册保持不变。
- WorkerEtfWorkspace.* 仍是历史命名的跨功能IPC客户端外观，但已按 ETF、直接持股、行业估值、市场周期、退出预警放入对应子目录；不在本次擅自更名或改变IPC契约。
- IEtfWorkspace 保留在 Application/Shared：尽管名字历史上带ETF，它现在是覆盖多个功能的兼容性Facade，不能因为文件挪动就假装它是纯ETF接口。

### 本轮验证与新增约束

新增 tests/Verify-NationalTeamFeatureLayout.ps1 并接入 WPF CI。它检查 Core 的 Domain/Application、Jobs 的 Application/Infrastructure、NationalTeamWatch 的 Presentation 和 Worker IPC 目录不能继续平铺业务源码，验证 XAML/code-behind 配对，以及 Worker 部分类归档位置。新功能若再把源码堆回根目录，会在 CI 中失败。

## 模块解耦第二阶段：窄工作区端口（2026-10-26）

本阶段把页面/用例的依赖方向从“所有页面都接收 `IEtfWorkspace`”逐步改成按业务功能注入专用端口。新增以下接口：

- `IEtfResearchWorkspace`：ETF配置、份额、历史、覆盖率、证据与同步。
- `IDirectEquityWorkspace`：机构直接持股、披露、官方报告与历史原文登记。
- `IIndustryValuationWorkspace`：国证行业PE、修订审核与采集回执。
- `IMarketCycleWorkspace`：市场成交活动与样本指数研究。
- `INavWorkspace`：基金NAV与独立修订历史。
- `IExitAlertWorkspace`：预警评估、事件、输入证据、审核与投递。
- `IWorkspaceAccess`：跨领域的管理能力提示；实际授权仍由Worker强制执行。

### 兼容策略

- 旧的 `IEtfWorkspace` 文件和成员声明保持不变，不通过修改其继承树来伪装二进制兼容。
- `EtfWorkspace` 与 `WorkerEtfWorkspace` 同时实现旧组合接口和新领域端口；Worker IPC的插件ID、命令名、JSON负载、分页快照和请求回执不变。
- 直接持股、行业估值、指数研究和退出预警的ViewModel改为依赖对应窄端口；ETF ViewModel现在在内部使用ETF端口和预警端口，旧构造入口通过显式兼容适配器转接。
- 兼容适配器只用于旧调用方；新代码应直接注入领域端口。新端口的默认失败实现仅用于明确报告旧后台不支持的功能，不能将失败折算为空结果。

### 当前存储层解耦状态：领域端口及多项领域存储已拆分

- `IndustryPeStore` 已从 `MarketRepository` partial 中独立出来，负责行业PE核心Schema初始化、官方原件导入、修订查询、审核、双回执幂等及知识时点校验。
- 新增 `MarketCalendarStore`，统一负责交易日历Schema、官方日历登记、已审核交易日判断，以及行业PE/指数研究共同使用的日历查询与原件核验；`RegisterReviewedCalendar`、`IsReviewedTradingDay`、`IndustryPeCalendar` 与 `IndexSeriesCalendar` 仍保留原公共入口。
- `IndexSeriesStore` 现已独立承接指数捕获证明、官方原件导入、价格引用、修订历史、审核及命令幂等；`IndexSeriesRepository` 只保留 `IMarketRepository` 兼容转发，指数日历通过共享 `MarketCalendarStore` 查询。
- `MarketActivityStore` 现已独立承接市场成交额/成交量事实、采集游标、来源原件登记、复核审计与冲突状态计算；原 `MarketActivityRepository` 保留方法签名并转发，复用同一会话和日历Store。
- `NavStore` 现已独立承接ETF基金NAV原件、修订、审核与知识截止查询；`NavRepository` 保留旧方法兼容。重启时核验 `etf-nav` 迁移登记和5张真实表是否一致，若缺表直接拒绝补建并保全现场。
- `AlertOutboxStore` 独立负责退出预警通知的查询、保存与重启前 `InFlight` 恢复查询；固定状态查询杜绝自由SQL条件字符串，兼容入口仍保留。
- `AlertEventStore` 独立负责事件列表、历史变更、版本查询、活跃事件选取和事件+变更日志写入。`ApplyAlertFinding` **仍从兼容宿主打开唯一事务**，在同一次提交中顺序调用 `AlertEventStore.RecordChange`、冻结输入快照和通知Outbox；没有把三者拆成分别提交。
- `AlertDeliveryStore` 独立负责待通知查询、按持久租约完成投递、失败退避/人工重试以及投递历史。`CompleteAlertNotification` 仍在**同一事务**内更新Outbox状态和插入投递审计；`ClaimAlertNotification` 保留在兼容宿主，以继续复核规则、订阅与正式数据门槛。
- 退出预警规则设置、历史修订回算、待处理标志、领取/投递生命周期仍保留在旧部分实现，不能宣称整个预警系统已经完全解耦。
- `MarketRepository` 继续保留旧的 `IMarketRepository` 公共入口，行业PE、日历和指数序列方法通过兼容Facade/共享组件转发，没有更改上层用例或Worker IPC。
- 新增内部 `IMarketRepositorySession` 契约，并由私有的 `MarketRepositorySession` 适配器提供共享连接、取消令牌、时间、原件核验和幂等回执能力。它**暂时仍由MarketRepository持有底层连接**；这一步抽离了领域逻辑，但尚未把数据库生命周期所有权完全搬出聚合类。
- 本次迁移保留同一个SQLCipher连接、原有事务提交点、原件保存顺序、`feature_schema_migration` 版本和 `nt_industry_*` 表结构。读写都仍经过既有 `ExecuteOperation` 取消门槛。

### 下一步存储层迁移顺序

1. **继续拆退出预警**：事件日志、Outbox和投递记录已提取。下一步针对 `ClaimAlertNotification` 的复合接收门槛定义单独的应用服务协调器，再处理规则配置、历史回算、pending标记和事件生命周期中的其余SQL；保留共享事务，尤其不得将 `OutcomeUnknown` 降格为可重发。
2. **完善存储测试**：除独立NAV真实SQLCipher存储、丢表拒绝自动补建之外，对市场活动、指数和行业PE追加回滚/重启/取消/知识截止对照，并校验来源哈希和两套幂等回执。
3. **再迁移直接持股，最后迁移ETF核心**：直接持股依赖证券主档和官方披露原件；ETF核心包含份额修订、质量问题、日历、仪表盘与同步游标，跨域耦合最多，最后处理。
4. **最后迁移连接所有权**：当各领域Store通过共享会话回归稳定后，再将SQLCipher连接/只读快照/Schema组合根抽到独立 `MarketDatabaseSession`，让 `MarketRepository` 退化为纯兼容Facade。
5. 每个领域迁移都必须通过真实存储测试：数据库关闭重开、写事务回滚、取消中断、重复命令回执、原件损坏、知识截止查询和并发只读快照。只通过编译或目录检查不算验收。

### 数据库与事务边界清单

- 主库仍是独立加密的 `market.db`；保持现有 `PRAGMA user_version=2`、SQLCipher密钥派生与只读连接设置。
- 构造/初始化目前串行调用日历、直接持股、市场活动、退出预警、NAV、行业PE、指数序列及市场周期审批等Schema初始化器。迁移后应由唯一的存储组合根按固定顺序调用一次；若迁移记录与表不匹配，继续失败关闭，不能自动重建空表。
- `source_fetch`、`quality_issue`、`worker_business_command`、原件存储与审计时间线是跨领域共享资源。领域仓储只能通过明确的共享端口访问，不能各自复制或另建一套证据表。
- `ExecuteOperation` 的取消中断、写前授权钩子、`Read` 的一致性快照、最多10000行的读取边界以及同请求幂等回执，都是行为契约，不是可随目录迁移改变的实现细节。

## 2026-10-26 外层 CRS 插件公共基础设施分离

本轮按用户要求把 NationalTeamWatch 中**不属于国家队业务**的可复用机制实体迁到外层共享工程 `src/CRS.Plugin.Common`：

- 首个迁移项：`PriorityReadAdmission`——有界异步租约、读取优先级与后台防饿、排队取消及活动计数，不持有交易日、市场来源、原件字节、私有密钥、数据库连接。容量和查询突发长度由每个插件实例分别决定，默认每连续8个查询读取让一个后台读取前进。
- 原 `NationalTeam.Jobs/Infrastructure/Evidence/RawEvidenceReadAdmission.cs` 降为兼容适配层，保留原本的 `RawEvidenceReadActivity` 和 `EnterAsync(bool,CancellationToken)` 接口，后续国家队调用方无需重写。
- `CRS.Plugin.Common` 不引用业务插件、SQLCipher、UI、Worker、私人税务或宿主；NationalTeam.Jobs 自己引用此公共项目，打包时仍遵循完整包的可信哈希索引和版本校验，绝不将各插件变成同一个未隔离的运行时状态。
- 公共契约不等于公共身份：来源允许域名、证据哈希的最大原件大小、知识可得时点、国家队ETF归因和退出风险规则**不得搬到共享工程**。
- 共享来源节流能力已在外层 Worker 的 `IBackgroundSourceContext` 实现；公共通知已有 `CRS.Notifications`，禁止新建并行预算或第二套邮件引擎。

后续适合评估的可复用机制包括：通用文件原子落盘、通用SHA256内容身份与有界核验、通用来源HTTP禁重定向/超时/响应大小的传输适配；所有行为必须接受插件注入来源许可、原件大小上限、受保护目录和审计归档规则，**不能把NationalTeam的具体数据源规则写死到共享库**。有状态SQLCipher事务与 `IMarketRepositorySession` 目前维持国家队内部边界，不能为了复用而跨插件共享数据库连接、身份凭据、通知发件箱或审计数据。

本轮新增 `--shared-read-admission` 测试，包括容量、队列取消、前台优先/后台防饿、租约归还与两个不同插件实例的隔离。WPF CI、解决方案依赖图及宿主边界门禁已增加对应检查。编译通过和合成测试通过不等于正式已安装Worker验证；未更新正式发行包。

## 反方架构师审查：这次没有解决的问题

| 问题 | 本轮处理 | 不能夸大的结论 / 后续动作 |
| --- | --- | --- |
| 一个巨大的 MarketRepository 仍以 partial class 横跨ETF、直接持股、行业PE、NAV、市场周期和预警 | 将各领域仓储实现文件按功能归档，公共实现放到 Infrastructure/Shared | **这是可读性重构，不是模块隔离**。下一阶段应拆成独立仓储/适配器，通过窄端口共享连接、事务与原件服务；先做依赖图、事务边界与Schema初始化清单，再逐个领域迁移 |
| IEtfWorkspace 与 WorkerEtfWorkspace 的历史接口名不能代表实际职责 | 保留兼容外观，按领域归档实现文件 | 不直接改公共接口成员归属或类型名，避免旧插件二进制/调用点ABI变化。后续先定义版本化的 IEquityWorkspace、IIndustryValuationWorkspace 等窄端口，双读/适配验证后再淘汰旧Facade |
| C# namespace 仍使用原来的 CRS.Plugins.NationalTeamWatch.* | 本轮只改物理目录，不改namespace | 这是有意降低风险。目录已按功能分类，但namespace/程序集边界尚未实现同等程度的解耦；下一阶段需要评估公开API、插件加载和旧二进制兼容 |
| 共用 Ports.cs、共享查询会话和少数跨领域DTO仍是公共汇合点 | 归档到 Application/Shared | 共享目录必须是明确例外，不应成为新业务的默认位置；后续需将宽泛的聚合接口拆成窄接口，但保持组合根统一管理生命周期 |
| 仅通过目录整理无法证明业务逻辑正确 | 未改计算逻辑、数据来源与调度契约 | 必须由完整Windows构建、WPF/XAML编译、行业PE/市场周期/预警/IPC/Worker回归证明无回归；不能用静态目录检查替代功能验收 |

这次重构的成功标准是“已有源码能按业务领域定位，且行为不变”，不是“所有领域已独立部署”。真正拆分巨型仓储和宽泛接口作为后续单独阶段，不与本次大批量路径变更混在一起。

## 验收命令

```powershell
dotnet run --project tests/CRS.Tests.SectorUi -c Release -- --navigation
dotnet run --project tests/CRS.Tests.IndustryPe -c Release -- --output artifacts/industry-pe-checks
dotnet run --project tests/CRS.Tests.SectorUi -c Release -- --industry artifacts/industry-pe-checks/synthetic-industry-ui-report.json artifacts/industry-pe-checks/synthetic-industry-ui.png
dotnet build CRS.sln -c Release
```

CI 里这些是**合成分类/UI与旧行业回归测试**，不能代替正式插件包可信加载、升级前后密钥与Worker保全、真实市场数据或长期性能的发行验收。更新真实安装包前必须运行相应发行门禁并保持 PR 草稿至完成验证。
