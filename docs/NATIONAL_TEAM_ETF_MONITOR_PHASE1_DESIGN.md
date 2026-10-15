# CRS「国家队持仓与ETF观察」插件——第一期设计及后续股票扩展规划

> 文件：`NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md`  
> **更新日期：2026-10-15；版本：v2.2（Windows Service后台运行专项同步）**  
> 状态：**有条件通过；免费数据通道M0、真实WPF插件POC和升级恢复P0尚未验证**  
> 技术：C# / .NET 10 / WPF / SQLite(可选SQLCipher)  
> **总体架构依据：**[CRS_PLUGIN_ARCHITECTURE.md](./CRS_PLUGIN_ARCHITECTURE.md)；**后台技术唯一专项依据：**[CRS_BACKGROUND_SERVICE_DESIGN.md](./CRS_BACKGROUND_SERVICE_DESIGN.md)  
> 第一阶段：中央汇金及国家队**核心宽基ETF，正式监控池上限20只**；初始链路验证5只。  
> 后续：扩展**中央汇金等机构对上市公司的直接持股/增减持披露研究**，不把股票“国家队持仓”误作实时交易。

---

## 0. 新增决策（ADR-NT-003：Windows Service无人值守ETF采集）

1. **实际运行采用两个独立宿主**：`CRS.Desktop.Wpf`只加载`NationalTeam.Ui`；`CRS.Background.Service`只加载`NationalTeam.Jobs + Core/Data`，由SQLCipher持久任务定义并在Quartz RAMJobStore恢复触发器；关闭WPF、锁屏、未登录Windows不阻断公开ETF数据作业。
2. **仍是一个业务插件**：稳定插件ID为`crs.national-team`，发布包含`uiEntry`/`backgroundEntry`两个可选入口；**至少分出不依赖WPF的Core/Jobs与WPF Ui程序集**，不能沿用“一期一个含WPF的DLL同时在服务执行”的旧实现。
3. **Service独占数据写入**：调度/原始证据/ETF数据库在`%ProgramData%\CRS`；WPF经授权Named Pipes请求服务提供只读DTO、任务状态和管理命令，不直接改写Quartz库/ETF事实表。可选独立公开数据SQLCipher密钥由服务账户提供，绝不从个人税务TOTP会话解锁。
4. **每天自动同步而非登录后启动**：Quartz计划抓取、交易日缺口补采、修订重核；任务时间仍是可调整的假设值，必须按官网实际发布时间验证。Service因系统关机/睡眠漏跑后补齐缺口，不向交易所密集重放历史触发次数。
5. **后台插件暂停、升级独立于UI生命周期**：服务维护时暂停触发→有界取消/等待→Checkpoint持久化→一致性备份/迁移→切换包与数据→验证服务启动；若只有UI启动失败，不影响已稳定执行的ETF作业。
6. **本章优先于后续v2.1旧示例**；宿主SDK、Named Pipes权限、SQLite单实例Quartz JobStore及安装卸载验收详见上述两份宿主文档。

## 0.1 历史修改结论与冻结决策（ADR-NT-002）

1. 将原 `crs.national-team-etf` 草案标识调整为**尚未发行的新插件ID `crs.national-team`**；发布后此ID必须永久稳定。展示名「国家队持仓与ETF观察」，两个业务页签分别为 `ETF份额监控` 与未来 `机构直接持股`。
2. **不是两个业务插件**：ETF与股票直接持股研究共享机构目录、公告证据、时间轴、数据质量与来源审核，因此放在同一个有清晰内部子模块的业务插件中；其他将来独立的绩效、行业研究等功能才新建插件。
3. **宿主与插件解耦**：UI端使用 `ICrsPluginEntry/IPluginSession`；服务端独立使用`IBackgroundPluginEntry/IBackgroundPluginSession`，二者共享领域服务但**不共享Session/DI**。宿主原有税务Domain/Security不得引用ETF插件。
4. **数量上限是业务约束不是采集清单注释**：正式有效监控池不得超过20只；任何新增需要先核对持仓证据、跟踪指数、已有覆盖及重要性。原开源项目的23只池**不可照抄为默认正式池**。
5. 第一期仅实现**官方来源ETF日总份额+质量/历史+份额变化**；取得同日有效官方基金NAV时再提供净申赎估算；持有人持仓证据结构预留，股票持仓二期交付，归因与回测三期。
6. 数据语义严格分层：**A：已披露机构持仓，B：ETF全市场份额变化，C：疑似政策性资金行为假设**。A/B/C不可互相替代；不得把ETF净申购称为中央汇金买入。
7. 上交所/深交所公开网页的HTTP接口**不是已承诺的开放API**；须先通过使用许可、真实样本、自动化获取、历史深度、连续性POC。失败降级为官方文件导入，只能称离线研究版本，不得宣称自动监控完成。
8. 插件启停/升级**重启生效**。一期WPF插件优先通过**默认AssemblyLoadContext**加载并统一UI依赖；只有真实XAML/BAML双插件POC通过才能改用自定义ALC。二进制、数据库和原始证据一致性要由升级日志协调，不把进程内插件当安全或故障隔离边界。
9. ETF公开数据保存在插件私有SQLite；不读取CRS税务SQLCipher数据库和认证密钥。未来关联账户持仓须经宿主明确授权的只读快照能力接入。
10. 明确保留`TradeDate / SourcePublishedAt / FirstSeenAt / RetrievedAt / AcceptedAt`信息，采用不可变原始证据与修订链，防止未来回测穿越。
11. **反过度设计**：监控池≤20只，不实现每个计算窗口的持久化依赖DAG；采用确定性窗口失效重算。首期只落地必要表，股票持仓解析及其业务表在二期迁移。
12. **当前代码现实**：仓库`CRS.sln`已具有独立Security和原项目内的绩效P0；不得为本插件重复建设或搬迁现有绩效模块。

### 0.1 概念边界和产品命名

**国家队**不是一个拥有统一可见交易账户的法人主体。首期重点研究：中央汇金投资、中央汇金资产管理及有独立公告证据确认的相关持有主体；其他机构需要扩展机构目录并逐个审核。上市公司股东表中的长期战略直接持股、ETF基金份额持仓、股票二级市场增持公告以及ETF持仓所间接覆盖的成分股，是**四个不同概念**。系统不把它们加总为“今日国家队买入”。

**不做**：账户操作/交易、抓取需要绕过登录验证码的通道、收费API强制依赖、神秘资金精确买卖推断、投资指令自动执行。Windows Service作为无人值守进程常驻，但关机/睡眠/网络或网站故障期间**不承诺实时采集**，恢复后按缺口补偿。

### 0.2 第二轮反方评审（v2.1）发现的关键缺陷

| 级别 | 设计问题 | 落地修改 |
|---|---|---|
| P0 | 旧宿主要求自定义ALC承载WPF，风险未关闭 | 采用宿主v2.1的**Default优先+真实WPF/双插件POC**，不把ALC作为安全边界 |
| P0 | 原插件DI构建/使用与销毁责任不闭环 | 改为`ICrsPluginEntry → IPluginSession`，私有DI由插件自己管理 |
| P0 | 插件版本与SQLite升级“原子切换”忽略RawStore/WAL/断电 | 引用宿主`UpgradeJournal`完整恢复规范，加入故障注入测试 |
| P0 | 当前仓库已有绩效P0，错误假定为待建插件 | 不重复迁移绩效；ETF作为首个**新增**业务插件 |
| P1 | ≤20ETF仍设计完整metric依赖DAG、多种未来表 | 改为**确定性窗口失效重算**；一期只建必要表，股票持股业务表二期创建 |
| P1 | 历史数据缺口可能被“上一有效观测日”跳过 | 严格使用上一**相邻交易日**，缺口时标`Incomplete` |
| P1 | 一期资金汇总对NAV接口的实际可行性描述偏乐观 | 官方NAV未验证前**只交付份额监控**，金额为可选增强，禁止跨市场残缺合计伪装完整 |
| P1 | 官方HTTP接口许可与可达性的风险 | M0必须明确2026当前合法可持续接口路径，手动版单独命名，不谎称自动监控 |

## 1. 与CRS现有代码如何衔接

已核实公开仓库的`CRS.sln`列出`CRS.Domain / CRS.Application / CRS.Infrastructure / CRS.Security / CRS.Desktop.Wpf`及既有测试项目，桌面框架`net10.0-windows10.0.19041.0`；现有`docs/architecture/PROJECT_STRUCTURE.md`还表明**投资绩效P0已在原分层中落地**。本次未逐文件检查最新实现，新增工程仍是目标结构；开发前必须生成实际依赖/导航/发布对照表。不得为了新ETF插件搬迁已落地的绩效P0或税务业务。

- **主程序改动仅限**：初始化`CRS.PluginHost`、暴露插件管理入口、导航贡献点、启停状态与错误面板；一期官方发行包预装，不开发任意ZIP安装入口。
- **禁止更改**税务FIFO/汇率/账户导入业务规则来满足ETF功能；插件不能拿税务连接、密钥、TOTP信息。
- **新功能优先插件化，但不泛化所有小功能**：插件适合有独立数据、生命周期、页面和版本的模块。宿主公共控件、安全升级或已有税务Bug修复仍在原项目内。
- 通用宿主架构、加载边界、升级回滚及跨插件权限以`CRS_PLUGIN_ARCHITECTURE.md`为准，本文只描述`NationalTeamWatch`业务插件，防止未来多个文档定义冲突的宿主契约。

## 2. 插件架构——实施级边界和代码结构

### 2.1 部署拓扑（同一插件双入口、双进程）

```text
CRS.Desktop.Wpf（普通用户进程，UI/TOTP/税务）
 └─ CRS.PluginHost（UI宿主）→ NationalTeam.Ui（WPF视图/只读查询客户端）
                 │
          Named Pipes（授权IPC：查询/控制/进度）
                 │
CRS.Background.Service（Windows服务账户，无界面）
 └─ CRS.Background.PluginHost（后台宿主）→ NationalTeam.Jobs
       ├─ Quartz.NET（唯一Scheduler，单实例Quartz RAMJobStore + SQLCipher持久定义）
       ├─ NationalTeam.Core / Data（无WPF依赖）
       └─ SSE/SZSE/官方证据 → ProgramData中的market.db/raw/checkpoint

不可越界：Service不加载NationalTeam.Ui，不打开税务库、不使用TOTP；
UI不直接写Quartz库/market.db，不创建第二套定时任务。
```

**第一期物理发布至少Core/Jobs与Ui分离**，二者由同一个`plugin.json`发行和管理。后台服务独立于WPF运行；在服务未安装/未启动时，ETF页面可展示缓存状态与明确的“后台服务不可用”，不能暗中启动WPF自有Quartz降级造成双调度。后台宿主的权威协议见[CRS_BACKGROUND_SERVICE_DESIGN.md](./CRS_BACKGROUND_SERVICE_DESIGN.md)。

### 2.2 依赖约束

```text
CRS.Desktop.Wpf -> PluginHost(UI) -> Plugin.Wpf + Plugin.Abstractions
NationalTeam.Ui -> NationalTeam.Core + Plugin.Wpf + Background.Ipc.Client契约
CRS.Background.Service -> Background.PluginHost + Quartz.NET + Background.Ipc
NationalTeam.Jobs -> NationalTeam.Core/Data + Background.Abstractions
NationalTeam.Core/Data -> 无WPF的领域模型、官方数据源接口与持久化

禁止：NationalTeam.Jobs/Core/Data -> WPF、Desktop、税务Security、TOTP库
禁止：UI -> Quartz、后台任务实现DLL、直接更改后台事实表/JobStore
禁止：宿主和其他插件静态ProjectReference到NationalTeam插件实现
```

### 2.3 插件契约与私有DI（以宿主v2.1为准）

```csharp
// CRS.Plugin.Abstractions：不向入口接口暴露 IServiceCollection
public interface ICrsPluginEntry
{
    IPluginSession CreateSession(IPluginHostContext context);
}
public interface IPluginSession : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
public interface IPluginHostContext
{
    string PluginId { get; }
    IPluginBackgroundClient Background { get; } // 只读和授权命令的IPC客户端
    IPluginDiagnostics Diagnostics { get; }
}
```

- 插件入口从manifest发现，`CreateSession`**在插件实现内部**构建自己的DI容器/应用服务，不修改宿主根容器，也不让宿主负责构建/访问插件`IServiceProvider`。
- `StartAsync`仅注册UI路由、订阅和查询端点；**后台任务由单独的`IBackgroundPluginSession`在Service中注册**；Session可选实现`CRS.Plugin.Wpf`的`IPluginWpfSession.GetPages()`，宿主在启动成功后通过导航注册令牌挂载页面，停止时由宿主移除导航、取消UI订阅；后台任务不受UI关闭影响；`DisposeAsync`由Session释放数据库连接、服务容器和UI资源。生命周期要支持重复停止和异常恢复。
- WPF UI贡献采用`CRS.Plugin.Wpf`中的`IPluginWpfSession`与`PluginPageContribution`桥接接口，界面只能由Dispatcher线程创建/销毁，路由采用`PluginId + RouteKey`；应测试真实WPF-UI主题、LiveCharts和二插件同时加载。
- **首期默认在`AssemblyLoadContext.Default`加载可信官方WPF插件**，宿主与插件锁定统一的WPF/UI/DI依赖版本；不承诺可加载两份冲突版本的相同程序集。只有自定义ALC路径的真实WPF资源测试通过后才可替换默认方案，参见<https://github.com/dotnet/wpf/issues/1700>。
- 插件是与宿主同权限的受信代码，`Http/DataPaths`只是API契约，**不是网络/文件权限沙箱**。本期禁用/升级重启生效，不支持第三方任意DLL安装。

### 2.4 插件包样例（双入口，尚未发行）

```json
{
  "id": "crs.national-team",
  "displayName": "国家队持仓与ETF观察",
  "version": "1.0.0",
  "contractApi": "1.0",
  "minHostVersion": "1.0.0",
  "schemaVersion": 1,
  "uiEntry": {
    "assembly": "CRS.Plugins.NationalTeam.Ui.dll",
    "type": "Crs.Plugins.NationalTeam.Ui.NationalTeamUiEntry"
  },
  "backgroundEntry": {
    "assembly": "CRS.Plugins.NationalTeam.Jobs.dll",
    "type": "Crs.Plugins.NationalTeam.Jobs.NationalTeamBackgroundEntry"
  },
  "requestedCapabilities": ["navigation", "background-jobs", "http-public", "storage-public", "logging"]
}
```

**清单字段是目标协议示意，不是已有代码**。插件包同时包含BCL业务Core/Jobs与WPF Ui，服务只加载`backgroundEntry`，桌面只加载`uiEntry`；不能用服务进程反射加载WPF插件。首期仅官方随包受信发行，清单与文件哈希必须由外部可信发布链绑定；单纯JSON自报哈希不构成信任。升级由Service维护期与宿主`UpgradeJournal`共同协调，并核验Quartz JobStore、市场DB和RawStore引用完整性。后续外部插件包签名验证/撤销另立规范。

### 2.5 WPF导航结构

```text
主导航
  ├── 税务工具（原样）
  ├── 既有导入/计算/报表（原样）
  ├── [插件贡献] 国家队观察
  │     ├── ETF监控概览                 # 一期
  │     ├── ETF详情/资金份额/来源证据   # 一期
  │     ├── 数据同步/质量状态           # 一期
  │     └── 机构直接持股               # 二期，未开放时不显示菜单
  └── 插件管理                          # 宿主能力
```

## 3. 监控标的、机构目录与未来扩展

### 3.1 第一阶段仅5只验证，正式池≤20只

| 代码 | 市场 | 研究对象 | 首期标签 |
|---|---|---|---|
| 510300 | SSE | 华泰柏瑞沪深300ETF | DataPilot，待核最新持仓主体 |
| 510050 | SSE | 华夏上证50ETF | DataPilot，待核最新持仓主体 |
| 588000 | SSE | 华夏科创50ETF | DataPilot，待核最新持仓主体 |
| 159919 | SZSE | 嘉实沪深300ETF | DataPilot，待核最新持仓主体 |
| 159915 | SZSE | 易方达创业板ETF | DataPilot，待核最新持仓主体 |

**特别提醒**：这些是**接口验证标的**，不宣称最新披露日均由中央汇金直接持有。二期通过基金定期报告的**机构全称+基金代码+份额+报告日+首次发布时间**建立已确认监控池。避免同名基金、旧简称以及基金份额折算混淆。

正式池生成规则（人工审查、插件内可维护）：

1. 证据门槛：官方基金定期报告或官方持有人公告确认中央汇金投资/汇金资管等持有；仅媒体摘要作为线索，不直接变成Confirmed。
2. 优先核心宽基：沪深300、上证50、中证500、中证1000、科创50、创业板等；小众行业/主题默认不进入≤20只核心池，除非符合明确例外。
3. 同类ETF按**确认持有量、流动性和指数代表性**选择；覆盖多个基金公司、沪深市场；在监控池中明确`InclusionReason / ReviewedBy / LastVerifiedReport / ValidFrom/To`。
4. 20只上限由业务服务与数据库事务校验（例如对有效状态按序列化事务进行计数），**不是WPF列表只显示前20个**。退出监控池后保留历史证据。
5. 超过20只时按明确优先级替换最弱标的；不暗中扩大容量，不将“23只开源样本”全部称为监控对象。

### 3.2 实体模型预留（不要把股东名单写死在代码里）

- `Institution`：注册全称、曾用名、别名、主体类型、法人身份关系、证据；明确「中央汇金投资有限责任公司」「中央汇金资产管理有限责任公司」是不同法人主体。
- `SecurityMaster`：`Market+Code+SecurityType`，覆盖ETF/股票；中文名称、证券类别、上市状态、上市日期等可版本化。
- `InstitutionalExposure`：机构—证券的关系，`DirectEquityHolding / EtfUnitHolding / ViaFundUnderlyingExposure / StrategicControl / PublicActionAnnouncement`，不得把ETF成分股穿透视为直接持股。
- `DisclosureEvidence`：来源、公告ID/URL、报告期末、实际发布时间、首次观察时间、存证哈希、主体名称及可信状态。
- `WatchlistMembership`：业务池、证券、入池/出池时间、原因、报告依据。ETF上限20只；股票后续单独池规则，不受20只ETF上限误伤。

### 3.3 开源案例参考与不得复制之处

参考项目：<https://github.com/wwww1998/national-team-etf-flow>。它追踪**23只**宽基ETF，按指数分组，使用ETF份额差×NAV计算金额代理，提供历史图和定时刷新。可以借鉴**页面结构、指数汇总、连续交易日缓存与数据新鲜度检查**。

通过阅读公开`refresh.py`可见其**使用东财份额快照兜底**、**缺少同日NAV时可能沿用最近NAV**、并依靠份额与净值比值自动判定拆分后将金额置零。**CRS第一期不照搬这些行为**：本项目坚持批准的官方免费源；无当日对应口径NAV显示`Unavailable`，份额折算须有公告证据才执行，可疑异常进入人工复核。开源实现是研究参考，不是交易所正式API保证，也不代表中央汇金真实交易记录。

## 4. 免费官方数据源与接口可用性验证（P0阻断门槛）

### 4.1 官方入口清单（已确认有公开页面 ≠ API已验证）

| 优先级 | 数据源 | 官方入口 | 当前已确认 | 待POC验证 |
|---|---|---|---|---|
| P0 | 上交所ETF总份额 | https://www.sse.com.cn/market/funddata/volumn/etfvolumn/ | 官网展示清算后日期、代码、总份额（万份） | 实际请求路径、返回格式、参数、历史查询、频率及可使用条件 |
| P0 | 上交所基金历史规模 | https://etf.sse.com.cn/fundlist/scalelist/index.shtml?code=510310 | 官网有按日期展示基金规模（亿元）与Excel导出入口 | 该规模口径不能直接等同份额；仅作校验/补充 |
| P0 | 深交所基金信息 | https://www.szse.cn/market/ | 官网菜单存在“基金数据→基金规模→ETF规模” | 对应具体公开数据页面、导出路径、日期/代码过滤、历史覆盖及单位 |
| P1 | 上交所ETF申购赎回清单 | https://www.sse.com.cn/disclosure/fund/etflist/detail.shtml?fundid=588000 | 部分清单包含基金份额净值及最小申赎单位等 | 逐基金、逐日期可得性；净值日的精确对应关系 |
| P1 | 基金管理人披露 | 发行人的官方网站及交易所公告 | 定期披露可用于持仓证据 | 公告发布日期、文件解析与持有人口径 |

**不可直接承诺**`query.sse.com.cn/commonQuery.do`和`www.szse.cn/api/report/ShowReport/data`就是稳定API。它们是可能存在的网页内部数据通道，是否能用、是否允许自动访问、具体`sqlId`/`CATALOGID`及字段含义，必须在现场浏览器开发者工具与官方页面输出之间交叉验证；若不满足要求，使用官方允许的CSV/XLSX手动或半自动导入，不实施对访问限制的绕过。

### 4.2 每个数据源的POC记录模板

建议固定输出`DATA_SOURCE_POC.md`，一行一个场景：

| 检查项目 | 通过标准 |
|---|---|
| 来源真实性 | 域名为官方交易所/基金管理人，入口能够从官网正常导航 |
| 使用许可 | 审查网站声明、robots/合理调用边界、授权要求及软件分发场景；记录结论 |
| 访问 | 正常HTTP成功/合法文件下载，**无需验证码绕过、代理绕过** |
| 格式 | JSON/CSV/XLSX/HTML中的一种且可稳定解析；保留原始原文 |
| 精确匹配 | 日期、基金代码、份额单位、记录数量一致，可与可见页面对账 |
| 历史范围 | 能否从当前向前逐日获取至少12个月；不能时记录最早可用日 |
| 分页/上限 | 确认请求区间、分页、单次最大行数，不因截断而误判完整 |
| 时间 | 明确当日清算后发布时点、可修订窗口与北京时间/交易日处理 |
| 限流/失败 | 429、403、5xx、超时的分类、有限重试和熔断降级 |
| 结果 | `Verified` / `Partial` / `Blocked`，附样本哈希、请求日期与复测日期 |

**P0裁决**：SSE、SZSE分别必须验证**至少一条合法可持续的自动采集渠道**，才能宣布该市场具备自动监控能力。若只有经允许的官方文件导入渠道，属于“离线研究预览”，不得以手动导入代替自动采集验收通过；实际入口限制、历史深度和发布延迟以现场POC为准。

### 4.3 推荐验证案例（不要使用未来日期作为已得数据）

- 上交所官方页面2026-10-13公开记录中，`510300=2,489,718.77 万份`，`510050=729,876.68 万份`，页面注明“当日清算后数据”。应把上述数值当作POC官方校验样本（数据源为截至2026-10-14可见网页，若官网事后修订则以修订版本为准）。
- 对`588000`的同一日期份额需另行验证页内代码过滤/分页结果，不凭未出现的首屏片段推断。
- 对159919、159915：分别测当日、上日、跨月、跨年度、多页查询；如果官网没有逐日历史则启动官方文件导入备选方案。

## 5. 数据采集架构：小规模监控，可靠性优先

```text
SSE / SZSE 官方网页或官方可下载文件
          ↓  Provider（每市场一个适配器；不得绕开权限）
按日期/页分批获取 → SourcePayload（原始内容，上限受控）
          ↓  RawEvidenceStore（保留压缩文件+SHA256+时间/来源）
          ↓  ParserVersion → NormalizerVersion → Schema Validation
          ↓  DataQualityRules + 人工复核
          ↓  share_observation_revision（只追加）
          ↓  accepted_current指针 → MetricDependencyIndex → Recalculate
          ↓  WPF只读Query模型（数据AsOf/覆盖率/证据级别）
```

**20只以内不需要Kafka、微服务、分布式队列或过度抽象的流处理框架**。实际Provider按官网支持的“逐日或区间/分页”能力决定调用方案；建议每次请求只保留一个受控`SourcePayload`在内存中，并立即落RawStore。不要把一年历史所有原始网页`byte[]`组合成单个大`FetchResult`返回；即使只有20只，原始HTML/公告可能很大。

### 5.1 C#接口（示意，≤20只无需通用流处理框架）

```csharp
public enum ExchangeMarket { SSE, SZSE }
public enum FetchState
{
    Success, NotPublished, NotFound, Forbidden, RateLimited,
    TransientError, InvalidContent
}
public sealed record EtfKey(ExchangeMarket Market, string Ticker);
public sealed record ShareFetchRequest(
    IReadOnlyList<EtfKey> Symbols, DateOnly From, DateOnly To);
// Provider在持久化原始响应之后仅返回轻量引用，不返回整年的byte[]
public sealed record RawEvidenceRef(
    string EvidenceId, string Sha256, long SizeBytes,
    DateTimeOffset FirstSeenAt);
public sealed record ShareFetchResult(
    string ProviderId, FetchState State,
    IReadOnlyList<RawEvidenceRef> Evidence,
    string? ErrorCode);
public interface IEtfShareSource
{
    string ProviderId { get; }
    ExchangeMarket Market { get; }
    Task<ShareFetchResult> FetchBatchAsync(
        ShareFetchRequest request, CancellationToken cancellationToken);
}
```

- 由于最多20只，首期使用**日期段/单页为边界的有界`Task`批次**即可，无需强制`IAsyncEnumerable`和分布式消息/流处理管线。Provider内部按已验证分页协议循环，单次只在内存保留一个有大小限制的响应，立即写入`RawStore`。
- 调用方自行分段、推进检查点，并在`Accepted`成功后才标记该ETF/交易日完成；结果集合只携带证据引用。未来若出现海量批量数据，再评估流式接口。
- 未经验证的交易所URL、`sqlId`、`CATALOGID`及响应字段，不作为公共接口参数或既定能力。

### 5.2 Provider和HTTP使用策略

- `SseEtfShareSource` / `SzseEtfShareSource`：内部采用宿主`IPluginHttpClientProvider`供给的限域客户端；代码/日期/分页/字段映射由已完成POC生成Source-specific规范和测试Fixture，不能抄历史`sqlId`当作已验证API。
- `OfficialFileImportSource`：只有当官方允许的下载文件可核验、或用户人工从官方页面下载后导入，才进入使用；导入记录`ManualImport`并追溯文件哈希与来源。
- 固定域名允许名单、HTTPS证书校验、超时、保守速率、有限重试、HTTP状态分类；`403/429/验证码`停止/降速并提示，禁止变换IP、绕过保护。
- 官方发布可能T+1甚至更迟，必须以抓取验证得到的实际`TradeDate`及`FirstSeenAt`展示数据新鲜度。
- 日期单位均不采用网页隐式时区：市场交易日`DateOnly`，事件/观察时间使用UTC `DateTimeOffset`；金额`decimal`人民币元，份额`decimal`份。

### 5.3 计算核心的工程约束

- 同样一批数据重复采集，不新增重复的有效业务修订；但抓取Attempt及必要原始证据依然可审计。
- 初始5ETF历史目标为**最近250个有效交易日**。完整率、首末可用日、缺口、访问失败/未发布/未上市四种状态分开。
- 20ETF池日常采集按同源支持的批量/分页运行，不因只跟踪20只而按20倍重复请求全市场页面。
- 每个交易日与市场的更新状态独立；跨市场比较默认选择**共同已验证的截止日**；若只能单边更新，给出“单市场最新、全池汇总不可用”而不是把缺失市场当零。

## 6. 历史数据采集及增量同步

### 6.1 一次性历史回补

1. 初始化5只ETF登记表、交易日历、市场/代码映射；按**最近一年→更早**分段回补。
2. 每个Provider先探测允许的最大日期区间；若只能按日查询，用单日批量；若有分页必须追到末页且记录总行数。
3. 对每个`(provider, market, ticker, tradeDate)`保存`SyncCheckpoint`，支持暂停、继续与重复执行。
4. 一段下载成功后，先存原始证据，再规范化、校验和接受版本；解析失败只能标记失败，不能推进为`Complete`。
5. 识别未上市日、休市日与应该有份额却缺失的日期；缺少的日期先补采，不允许前向填充到“每日份额”。
6. 完成后执行抽样对账：按ETF至少10个随机有效交易日 + 所有疑似跳变日，用官网可见页面或允许的官方文件逐条核对。
7. 若实际允许下载的历史不足一年，展示真实覆盖率和最早可用日期，避免生成伪造历史序列。

### 6.2 每日更新和补偿机制

- **Service独立启动时**：自动检查过去`30`个**有效交易日**的缺口，优先填补未完成日期；GUI是否登录不影响公开数据任务。
- 建议Quartz每日触发：**北京时间23:30**尝试当日份额；次日**08:30**再核对上一交易日。时间仅为方案假设，不是交易所保证发布时点；需以M0实测调整。若电脑关机/睡眠或Service未运行，**下次Service启动先扫描交易日缺口**后补偿；不能补偿时标注具体原因。
- 对最近5个交易日执行有限度修订复核；每周对过去30个交易日进行一次抽样复核；人工可触发指定日期重核。
- `403 / 429`：立即降低访问频率或停止；禁止通过切换IP、伪造访问凭证绕过；支持手动官方导入兜底。
- `200`但响应为空或JSON结构变化：**不能当作0份额**；保留证据并标记`ParseError`/`NotPublished`，触发格式变更告警。
- `Abort/CancellationToken`：中止当前请求且保留已提交区间，下一次可续跑。
- 插件禁用：停止后续同步；保留本地历史数据库与日志；删除数据为独立显式操作。

### 6.3 状态机

`Planned -> Fetching -> RawSaved -> Parsed -> Validating -> Accepted`  
异常分支：`NotPublished | AccessBlocked | ParseFailed | QualityRejected | NeedsReview | Cancelled`。  
只有`Accepted`且校验完成才产生可展示计算指标；失败及未知必须显示为`无数据`，绝不默认为零流入。

## 7. 私有数据存储、双时间轴与修订模型

### 7.1 物理隔离及数据库版本

- Service拥有的`%ProgramData%\CRS\Plugins\crs.national-team\market.db`存结构化快照/修订/任务Checkpoint；`raw/`按SHA-256内容寻址保存压缩原始响应/官方文档；`backup/`保存升级或人工恢复用一致性快照。SQLite足够支撑20只ETF多年份额，不必引入服务器数据库。
- 公开市场数据使用独立SQLCipher（遵循CRS加密规范），禁止在该库写入用户交易、税务计算、凭据等隐私数据；使用**独立服务密钥及DPAPI保护**，不复用税务主密钥；服务密钥由Service身份生成/解封，**与WPF/TOTP登录态无关**；服务账户更换时需密钥迁移预案。
- DB单写入者串行事务；`source_fetch`先写原始证据引用再解析入库；接受新版本和失效标记在同一事务中完成。迁移操作只针对本插件库，不修改税务Schema。`RawStore`采用内容寻址、临时文件落盘并校验后提交引用；异常恢复校验DB对原始证据文件的引用，孤儿文件延迟清理。
- **升级**：冻结插件写入，采用SQLite Backup API或`VACUUM INTO`得到一致快照（WAL模式**不可只复制主.db文件**），迁移候选库，写`UpgradeJournal`再切换版本/库引用。故障时按日志重入恢复；新版本若已写入事实，必须保留新数据，不得静默用旧快照覆盖；RawStore文件引用需自洽。

### 7.2 必须具备的时间字段（股票披露同样使用）

| 字段 | 含义及约束 |
|---|---|
| `TradeDate` / `ReportAsOfDate` | ETF交易日 / 持仓报告期末（不是披露时间） |
| `SourcePublishedAtUtc` | 来源明确给出时填写；无法核实则**NULL**，不能臆造T日18:00 |
| `FirstSeenAtUtc` | 本程序首次能够观测到记录的时间；通常晚于真实公布时间 |
| `RetrievedAtUtc` | 每次抓取/导入的时间，可多次 |
| `AcceptedAtUtc` | 对某个修订版本校验通过的时间 |
| `EffectiveFrom/To` | 证券名称、机构关系、入池状态的业务有效期（如适用） |

未来回测按策略当时**可用**的公告和数据版本重放；`ReportAsOfDate=2026-06-30`不意味着该日已知半年报数据，`SourcePublishedAt`未知时用保守的`FirstSeenAt`界定最早可用时点。

### 7.3 数据库逻辑表（一期必建与后续扩展区分）

| 表名 | 关键字段 / 唯一性 / 职责 |
|---|---|
| `institution` | `institution_id, legal_name, category, alias, legal_entity_status`；机构主体，别名归一不可混为同一法人 |
| `security_master` | `security_id, market, ticker, security_type, valid_from,to`；ETF与股票共享身份模型，支持版本化别名与指数归属 |
| `watchlist_membership` | `list_id, security_id, from,to, reason, evidence_id, review_status`；ETF有效池≤20，股票池独立 |
| `disclosure_evidence` | `id, source_url/document_id, report_as_of, source_published_utc, first_seen_utc, content_hash, review_state`；官方持有证据来源 |
| `source_fetch` | `fetch_id, provider, request_key, retrieved_utc, source_published_utc(NULL可), status, content_type, sha256, raw_store_ref, parser_version`；原始证据只追加 |
| `share_observation_revision` | `revision_id, security_id, trade_date, total_shares, original_value/unit, fetch_id, revision_no, accepted_at, first_seen, status, supersedes_id`；只追加份额修订 |
| `share_accepted_current` | `security_id, trade_date -> accepted_revision_id`；一日期一当前有效版本，不删除历史 |
| `fund_corporate_action` | `security_id, effective_date, action_type, ratio, evidence_id, review_status`；基金折算、合并等明确证据 |
| `nav_revision` | `security_id, nav_date, nav, fetch_id, first_seen, version, status`；独立的同日NAV数据 |
| `metric_revision` | `metric_id, security_id, trade_date, window_days, input_fingerprint, algorithm_version, result, state, calculated_at`；存可重建的输入指纹，允许查询当时有效数据版本 |
| `metric_dependency`（后置） | **一期不建依赖DAG表**：份额/NAV修订按`TradeDate`和最大窗口20交易日确定性失效与重算；未来算法无法按窗口追溯时再建立关系表 |
| `sync_checkpoint` | `provider, list_id, range/page/date, status, last_attempt, retry_count, version`；业务补采状态，**唯一Cron定义存在Service的SQLCipher scheduler-config.db，Quartz RAM触发器启动重建**；不要重复维护可写Cron权威配置；不以下载成功代替Accepted |
| `quality_issue` | `rule_id, rule_version, target_id, severity, status, evidence_id, reviewer, decision_time`；人工审核留痕 |
| `institution_security_holding`（二期） | `institution_id, security_id, holding_type, shares_class, shares_count, percent_basis, report_as_of, evidence_id, first_seen`；已披露持仓快照，不是每日交易流水 |
| `holding_change_event`（二期） | `from_snapshot_id, to_snapshot_id, raw_delta, adjusted_delta, corporate_action_ref, state`；只在可比口径且公告已知时生成 |
| `plugin_schema_migration` | `schema_version, applied_at, plugin_package_version, integrity_check`；插件版本/数据库版本可追溯 |

**MVP建表策略**：只创建ETF采集、修订、有效指针、Raw证据、质量问题、checkpoint、必要主数据和Schema记录。`institution`、`disclosure_evidence`只保留满足当前机构标签/证据核验的最小字段；`institution_security_holding`、`holding_change_event`等股票持仓事实表属于**二期迁移**，一期既不建空表也不实现股票解析器。指标表与窗口重算可简化为按需派生缓存，不能牺牲可复算性。

### 7.4 修订传播与幂等算法

```text
原始来源有更新
   -> 新原始哈希保留
   -> 新候选ShareRevision（与当前Accepted比较）
   -> 同值：不新增事实；不同值：质量规则/必要人工核对
   -> 接受新修订并替换accepted_current指针
   -> 按确定性受影响日期范围将已有派生缓存标记Stale（一期不建DAG）
   -> 重算：当日、下一个有效交易日及含该交易日的全部5/20日滚动窗口
   -> 新MetricRevision记录输入指纹和算法版本；必要时从事实修订链重建明细
```

- 数据断裂跨多天时不可简单计算为“一日净申赎”；待填补中间缺口后重算。
- 修订切换前后数据都可用于追踪和审计，但UI只默认展示最新Accepted版本，并显示`Revised`标签。**NAV修订、份额折算因子或计算算法版本变化也必须触发受影响窗口失效**，不能只监听份额修订。
- 原始证据丢失/哈希不符时该修订标记`Critical`，派生指标不可继续声称可复算。

## 8. 数据质量规则与异常处理

| 规则 | 触发条件 | 结果 |
|---|---|---|
| Q01代码/交易所 | 非当前监控池（一期初始5只、正式池≤20只）或代码不匹配请求 | Reject |
| Q02交易日 | 日期无效、尚未上市，或未来日期 | Reject/NotApplicable |
| Q03数值 | 份额为负、空、NaN、溢出、不合法格式 | Reject |
| Q04单位 | 未知单位或单位与来源规范矛盾 | Quarantine |
| Q05重复 | 同一来源同一日同值重复 | 去重保留抓取证据，不生成新事实修订 |
| Q06冲突 | 同一日新版本与已接受版本不同 | 创建待复核修订；高影响时暂停衍生指标 |
| Q07连续性 | 应有数据的交易日缺失 | Missing + 重试，不补零 |
| Q08异常跳变 | 日变化比例超过基于历史分位的阈值（默认POC先用绝对20%作人工检查） | Flag；不武断Reject |
| Q09折算/合并 | 官网公告存在份额折算，或变化异常且待核 | 计算暂停，要求补齐事件和因子 |
| Q10净值日期 | 净值与份额日期不一致、净值未发布 | 份额可展示，估算资金流为Unavailable |
| Q11发布延迟 | 当日尚无数据 | NotPublished，非Zero |
| Q12请求受限 | 403/429/验证码/许可限制 | AccessBlocked，停止自动采集并提示官方手动导入 |
| Q13格式变更 | JSON/HTML字段缺失或单位发生变化 | ParseError，保留原始证据并拒绝静默映射 |
| Q14跨源差异 | 官方下载与网页数据不一致 | 记录版本、来源时间与待确认结论 |
| Q15证据完整性 | RawBlob SHA256与保存时哈希不匹配 | Critical，不参与计算 |

所有规则有`RuleId`、`Version`、`Severity`、`AutoAction`，阈值可配置但默认需审批后生效。质量问题不得因刷新UI而自动消失。

## 9. 指标口径与开源实现差异

1. 份额净变化：`DeltaShares(t) = Shares(t) − Shares(previous EXCHANGE trading date)`，**两个相邻交易日**必须都有`Accepted`记录；缺某一日时不能跳过去用更早有数据的一日冒充日变动。份额单位统一为“份”。
2. 按T日**当日官方基金单位净值**计算：`EstimatedPrimaryNetFlow(t) = DeltaShares(t) × NAV(t)`；这是**一级市场ETF净申赎金额代理**，不是中央汇金的买卖，更不是二级市场真实成交净额。
3. `NAV(t)`尚未获取 → `EstimatedPrimaryNetFlow=Unavailable`；绝不自动沿用前日或最近净值，因为这会造成金额口径失真。可显示独立标识的收盘市价替代指标，但不得与NAV序列混算。
4. 折算前后如有官方公告及确定换算因子，必须先定义折算日前后可比份额及净值的调整口径，再计算或明确标记不可比。即使公告已知，**折算日同时发生真实申赎时也不能把流量武断设成0**；若只有“份额×NAV大致守恒”的经验特征，仅产生`CorporateActionSuspected`人工复核。
5. 按指数归集，先逐ETF计算有效`EstimatedPrimaryNetFlow(t)`再求和；**不能把不同净值的ETF份额简单相加**。跨沪深汇总必须区分`CompleteAggregate`与`PartialAggregate`；部分合计仅用于数据研究视图，**不得进入全池总资金变化KPI或提醒**。NAV尚未具备合法官方来源的阶段只做份额变化，不显示金额曲线。
6. 1/5/20交易日估算资金总量必须按有效**每日金额求和**，不能将N日期末份额差乘期末NAV伪装成区间总流量。
7. 数据缺失、SourceBlocked、QualityRejected、PendingReview与无申赎（真实0）是不同状态。存在有影响的份额/NAV/折算因子修订或算法升级时置`Stale`直至重算。
8. A/B/C证据标签：`ConfirmedInstitutionHolding`=机构经公告确认的ETF持有，`OfficialEtfShares`=全市场ETF份额，`SuspectedPolicyFlow`=研究假设，后一项一期不得展示为确定信号。

### 9.1 单ETF与同类指数归集

- `IndexGroup`维护跟踪指数的版本化ID，不依赖基金中文简称匹配。
- 默认展示：单ETF / 沪深300ETF组 / 上证50ETF组 / 中证500ETF组 / 中证1000ETF组 / 科创50ETF组 / 创业板ETF组（实际是否启用取决于入池ETF）。
- 同一证券在一个指数归集周期只计一次；不同ETF代表不同基金份额，可以按人民币金额聚合，**但聚合值不代表某特定股东交易**。
- 由于最多20只，暂不建设复杂因子库和策略DSL，只实现一组可测试、版本化的计算函数。

## 10. WPF功能需求与后续直接股票持股研究

### 10.1 一期WPF界面（真正可交付）

首页导航：「国家队观察」→「ETF监控」，包含：

- **数据源健康**：SSE/SZSE当前可用状态、已核验最新`TradeDate`、首次观察时间、与交易日历差距、失败原因、导入官方文件入口。
- **20只以内池管理**：当前数量/20、数据验证池与已确认机构持有池两个标签、加入与移除的证据/审批记录。
- **ETF总览**：代码、名称、跟踪指数、市场、最新总份额、1/5/20日变化、当日NAV是否齐全、官方数据截至日、状态及完整率。
- **指数汇总**：沪深300/上证50/中证500/中证1000/科创50等，只有同日有效官方NAV才计算人民币金额代理；否则只展示份额方向及覆盖状态，不做“全池资金合计”。显示`CoveredCount / ExpectedCount / Completeness`，部分数据不当作完整结果。
- **ETF详情与证据**：历史曲线、跳变提示、份额折算、修订差异、原始证据Hash/采集时间/官方来源链接、输入和公式回放。
- **同步与维护**：首期历史回补、指定时间段补采、人工导入、取消、限速/拒绝、断网恢复、错误队列及DataPilot状态。

基于现有WPF与LiveCharts；网络/解析/计算在后台任务，UI只读取轻量化查询DTO，任何UI异常都不应自动触发全量重新抓取。

### 10.2 二期：同一插件加入「机构直接持股」

**目标**：看得出“哪个机构在什么报告期、以什么身份持有哪些上市公司股票”，并发现官方报告的新增/减持/增持变化；**不承诺每日国家队实际交易数据**。

第一优先机构：中央汇金投资、中央汇金资产管理及各自经独立验证的实际持有主体；**不要将所有国有股东、北向资金或ETF持仓的成分股自动称作中央汇金直接买入**。

业务界面：

| 页面 | 数据 | 关键提醒 |
|---|---|---|
| 机构→股票矩阵 | 机构法人、股票代码、持有股数、股份类别、报告期、公告时间 | 机构是否直接登记为股东 |
| 股票→机构关系 | 某股票的实际持有人、控股/战略持有与二级市场披露性质 | 长期控股不等于二级市场买入 |
| 机构持股变化 | 同法人、同证券、同股类、可比报告口径的变化 | 报告期末→首次披露日不能混淆 |
| 公告事件台账 | 增减持公告、权益变动报告、定期报告、基金持有人披露 | 证据等级、原文链接、发布日期 |
| 持仓验证状态 | Confirmed / Stale / Missing / NotComparable | 未出现前十大股东不能解释为清仓 |

**数据源路径**：上市公司定期报告、临时公告及交易所官方披露系统、基金管理人官方年报/中报；优先由用户下载官方PDF/表格导入、人工核对结构化结果，再评估合法的自动化获取及高质量文本解析。不应承诺股票十大股东全历史免费结构化API必然存在。

**业务规则**：

1. 区分 `DirectEquityHolding`（股东名册直接持有）、`StrategicControl`（战略控股）、`PublicChangeAnnouncement`（官方增减持行为）、`FundHoldings`（ETF份额持仓）、`ViaEtfExposure`（穿透ETF的间接暴露）。只把第一、第三项写成“直接持股/增减持公告”。
2. 主体对齐用统一`institution_id`；不能把汇金投资与汇金资管一股脑合成一个法律主体。可以在UI另行展示可追溯的汇金体系汇总。
3. 不能把同一股票A/H股、无限售/限售、配股送转前后不经调整直接比较；分母是总股本还是流通股本必须存`PercentBasis`。
4. 持股快照来源仅列前十大时，某机构“未列示”应为`NotDisclosed/BelowThreshold/Unknown`之一（按证据），不能设置零持仓。
5. 每笔持仓快照记载`ReportAsOfDate`和`SourcePublishedAt/FirstSeenAt`，后续回测用信息公布时点而不是报告期末。
6. 初期股票清单可不限制20只：ETF≤20限制只适用于ETF监控池；股票持仓是事件与公告驱动研究，其范围另设上限。

### 10.3 第三期与其他插件关系

- 可在本插件内引入“政策性资金信号研究/验证”，但必须使用A/B/C标签、历史回测无未来函数并保留反证。
- 如未来投资绩效插件需要联动股票/ETF，优先由宿主受控只读查询接口提供研究指标快照；**不得将`NationalTeamWatch`的DB直接给绩效插件读取**。
- 国家队观察插件不是投资总账，不应存储用户券商账户、交易明细或税务结果。

## 11. Windows Service后台任务、安全与插件升级（ADR-NT-003）

### 11.1 服务端独占的业务作业

| 稳定作业键（示意） | 触发方式 | 实际职责 | 并发/幂等 |
|---|---|---|---|
| `crs.national-team:etf-share-daily` | 工作日晚间、按中国交易日校正 | 批量同步≤20只ETF当日份额、记录来源状态 | 同市场同日期单任务；写入按来源哈希和接受修订版本幂等 |
| `crs.national-team:gap-repair` | 每日上午及服务启动时 | 扫描缺失日、发布延迟，有限补采 | `provider+ETF+tradeDate`检查点，限频/遵守403/429 |
| `crs.national-team:revision-audit` | 份额新修订被接受后队列化 + 定期复核 | 失效并重算受影响日/5日/20日窗口 | 输入指纹和算法版本，重放结果相同 |
| `crs.national-team:disclosure-scan` | 每日一次，二期逐步启用 | 中央汇金及国家队机构ETF/股票持仓**公告披露检查** | 以文档哈希与发布日期去重，不宣称实时成交 |

- Quartz `JobKey`用稳定`PluginId:JobId`，运行时`JobDataMap`仅保存字符串型稳定业务ID，具体实现由Service的通用`PluginJobDispatcher`路由；SQLCipher加密调度库保存Cron及启用状态，服务启动时重建Job，不能把CLR类型序列化为持久配置。
- **Quartz首期RAMJobStore + SQLCipher加密TaskDefinition**：重启恢复Trigger，禁止第二套Cron权威源；如果以后改为SQLite AdoJobStore，必须先验证SQLCipher每连接密钥与整个JobStore加密，不允许无审批的明文SQLite。后台真实流水记录存Service审计或插件事实库。
- SCM负责服务开机启动/失败恢复；Quartz负责任务触发/误点火处理；业务Checkpoint负责已完成事实和补偿。**至少一次执行+业务幂等**，不承诺Exactly Once。
- `Quartz`误点火后不逐次补跑数月触发；重启后通过交易日历计算缺口并执行限频补偿。数据`NotPublished`/`AccessBlocked`/`ParseError`按分类退避；403/429不绕过。

### 11.2 UI/服务通信与安全

- WPF经授权Named Pipes读取状态/汇总DTO、手动触发、暂停/恢复。服务进程运行在Session 0，**禁止任何WPF依赖、MessageBox、用户交互对话框**。
- 首期Service以`LocalService`为优先候选身份；安装时配置ProgramData数据目录和Pipe ACL，并在真实设备验证官方数据网站、代理、TLS、用户网络环境的可达性。Windows管理员/经授权本地操作组可变更调度；只读状态可授予受控桌面身份。不能相信IPC请求里的`IsLoggedIn`或TOTP标志。
- 公开数据与私有税务数据库分属两个安全域。Service永不访问个人税务SQLCipher、个人投资明细和TOTP秘密。**延续CRS数据库强制加密约束**：market.db和调度配置库都使用独立SQLCipher密钥（不得复用个人税务密钥）；不支持以明文SQLite作为兼容回退。

### 11.3 升级/禁用/未安装表现

- WPF退出**不**停止后台作业。管理员禁用插件→Service停止新触发，并按有界取消等待在途作业，保存Checkpoint；UI入口禁用仅影响页面，不等价于业务插件停用。
- 后台插件升级必须进入维护状态，由升级器协调Service停写、SQLite一致备份、候选迁移、包版本切换、重启健康检查、Journal提交或受控恢复；新事实写入后不得静默用旧库覆盖。
- Service没有安装、被停止、ACL/密钥异常时，WPF显示明确`ServiceUnavailable/PermissionDenied/StorageLocked`，允许查看此前缓存但**不私自启动第二个Quartz实例**。

## 12. 验收测试计划

### 12.1 P0：接口可行性与数据许可（阻断发布）

| ID | 测试 | 通过标准 |
|---|---|---|
| POC-01 | SSE 510300历史指定日期份额获取 | 与**测试时保存的官方页面/文件哈希快照**一致；2026-10-13数字仅作为初始fixture线索，修订不得直接判失败 |
| POC-02 | SSE 510050历史指定日期份额获取 | 与已固定的**官方来源样本及单位**逐字段一致，来源若变动保留修订链 |
| POC-03 | SSE 588000当日/历史 | 能在官网确认并抓取/导入；无法得出时标记Partial，不伪造 |
| POC-04 | SZSE 159919当日/历史 | 确认官网ETF规模来源、字段单位及有效日期；不可用则官方文件导入 |
| POC-05 | SZSE 159915当日/历史 | 同上，能与官网抽样对账 |
| POC-06 | SSE/SZSE历史覆盖 | 分别记录最早可用日、最近日期、完整交易日覆盖率；不强称已有一年 |
| POC-07 | 403/429/验证码 | 不绕过，熔断并进入可恢复状态，展示明确原因 |
| POC-08 | 使用许可/分发复核 | 留存日期、官网声明、自动化限制、个人使用/公开发行的评审结论 |
| POC-09 | 250交易日真实覆盖 | 5只逐只给出可自动获取的起止日、缺口、完整率；不得伪造或前向填充 |
| POC-10 | 真实连续性验证 | SSE/SZSE至少各1自动通道连续20交易日验证，记录成功率/发布滞后/失败类型；手动导入不能替代此项 |
| POC-11 | 20只正式池容量 | 当前有效池≤20，新增第21只自动拒绝并说明更换流程 |
| POC-12 | 最新机构确认 | 5个验证标的无需假装全部Confirmed，持有主体需逐一报告证据确认 |

### 12.2 插件架构

| ID | 测试 | 通过标准 |
|---|---|---|
| PLG-01 | 未安装插件 | CRS启动、登录、税务计算、报告不受影响 |
| PLG-02 | 管理员安装Service与官方插件并重启 | WPF出现监控菜单，服务有后台入口与持久Job；两端健康状态可查 |
| PLG-03 | 管理员禁用后台插件，用户退出WPF | Service停止后续触发/有界取消在途任务，独立UI状态按配置处理，数据保留 |
| PLG-04 | 损坏/不兼容DLL | 宿主拒绝加载并告警，CRS保持运行 |
| PLG-05 | 签名/哈希不符 | 拒绝安装/执行，无动态代码执行 |
| PLG-06 | 插件升级迁移失败 | 回滚包版本，独立库备份可恢复，不破坏税务DB |
| PLG-07 | Default上下文依赖冲突 | 检测同名不同版本依赖并**拒载/提示**；不假设能在Default中并行隔离 |
| PLG-08 | 双宿主生命周期 | 退出WPF**不取消Service任务**；Service停止时取消作业、连接清理和Checkpoint落盘 |
| PLG-09 | 安全边界 | API/SDK不暴露税务库连接、TOTP种子或主密钥；安全审查承认进程内插件非沙箱 |
| PLG-10 | 根DI隔离 | 插件仅修改私有ServiceCollection，不得覆盖宿主认证、日志与税务实例 |
| PLG-11 | 主程序零静态引用 | Desktop/TaxDomain不得ProjectReference业务插件；仅从发布目录按清单加载 |
| PLG-12 | WPF真实资源加载 | 验证XAML/BAML、资源字典、MVVM/图表及页面销毁；不能只通过控制台假插件 |
| PLG-13 | 升级Journal与一致性恢复 | 切换前/中/后强杀、WAL存在、原始证据引用、已有新写入场景均恢复到明确状态，不丢失证据 |
| PLG-14 | 新增第二演示插件 | 无需修改税务Domain和业务switch，UI与Service均可独立注册且不交叉加载WPF |

### 12.3 历史采集与数据质量

| ID | 测试 | 通过标准 |
|---|---|---|
| DATA-01 | 万份单位转换 | `2,489,718.77 万份` -> `24,897,187,700 份`（decimal精确） |
| DATA-02 | 重复导入同文件 | 业务事实不重复，原始获取日志仍可审计 |
| DATA-03 | 中途网络断开 | Checkpoint保留，后续继续，已接收记录不丢失 |
| DATA-04 | 休市日与非交易日 | 不要求休市日份额，不通过填零伪造数据 |
| DATA-05 | 返回200空文档 | `NotPublished/ParseError`，份额不是0，不生成信号 |
| DATA-06 | 官方修订同一日数据 | 追加新修订并可回看旧版本；派生指标被标脏重算 |
| DATA-07 | 份额折算 | 不把折算机械解释为赎回；需公告调整后方可计算 |
| DATA-08 | NAV未发布 | 仍显示份额变动，净申赎估算为不可用 |
| DATA-09 | 原始哈希被篡改 | QA Critical且不参与计算 |
| DATA-10 | Service离线数日再启动（无需开启WPF） | 扫描并限频补采缺失交易日；关机、睡眠不算已执行 |
| DATA-11 | 交易所单位/字段改变 | ParseError并告警，不静默复用旧字段 |
| DATA-12 | 数据库连接异常 | 事务回滚，旧有效快照可读，税务功能不受影响 |
| DATA-13 | 多版本复算 | 相同输入修订+算法版本得到完全一致结果 |
| DATA-14 | 安装后离线运行 | 可查看已缓存历史，清晰显示数据过期 |
| DATA-15 | 双时间轴 | 报告期末≠披露日；查询在策略时点只看已披露/FirstSeen记录 |
| DATA-16 | NAV缺口 | 不用前次NAV替补；当日资金估算状态Unavailable |
| DATA-17 | 拆分疑似 | 只标识待复核，不因价格份额近似反向变化强行置0 |
| DATA-18 | 修订传播 | t日份额/NAV/折算因子改变后，所有依赖窗口被Stale并用确定性日期范围重算；不依赖DAG表 |
| DATA-19 | 跨沪深缺口 | 日期不齐不得发布“全池合计”；可发布显式Partial+覆盖率 |
| DATA-20 | 指数金额合计 | 同日逐ETF按NAV估算后求和，不得直接合计份额 |

### 12.4 投资语义与UI

| ID | 测试 | 通过标准 |
|---|---|---|
| UI-01 | 信息标注 | 无“国家队已买入XX亿元”等未经证实表达 |
| UI-02 | 原始证据下钻 | 任一日份额可追溯至来源、查询/抓取时间、原始字节及哈希 |
| UI-03 | 指标窗口不完整 | 5/20日指标显示Incomplete，不用缺失值拼接 |
| UI-04 | 资源占用 | 5只ETF、1年历史回补期间Service独立执行，UI保持可操作，授权暂停/取消有效 |
| UI-05 | 错误恢复 | 网络恢复后可以重试；访问禁止时不能无止境循环 |

### 12.5 机构股票持仓二期预置验收（一期只写fixture/Schema契约）

| ID | 测试 | 通过标准 |
|---|---|---|
| EQ-01 | 同名与别名 | 汇金投资/汇金资管分别映射独立主体，别名不会重复统计 |
| EQ-02 | 持股关系 | 战略控股、二级增持公告、ETF间接持仓在查询中分开 |
| EQ-03 | 财报时序 | 2026-06-30持仓在实际披露前不能作为“已知事实”参与回测 |
| EQ-04 | 股份类别与送转 | A/H与股本事件前后未经核准口径不可直接比增减 |
| EQ-05 | 未入十大股东 | 缺席股东榜不得推断已清仓 |
| EQ-06 | 公告原文 | 每条已确认持仓可回溯到官方披露与哈希证据 |

### 12.6 新增反方评审验收用例

| ID | 测试 | 必须满足 |
|---|---|---|
| ARCH-01 | CRS当前绩效P0与ETF插件并存 | 不创建第二份Performance计算、无数据重复迁移，税务与既有绩效回归通过 |
| ARCH-02 | WPF默认ALC真实加载 | WPF-UI主题、LiveCharts、BAML资源及2个插件均能加载；依赖冲突可拒载 |
| ARCH-03 | Session生命周期 | 私有DI由插件创建，Stop/Dispose有界且幂等，导航和任务注册令牌回收 |
| ARCH-04 | 断电升级 | `UpgradeJournal`每一阶段强杀重启，DB/WAL/包版本/RawStore引用一致 |
| ARCH-05 | 无官方NAV | ETF份额可展示，流量金额全为`Unavailable`，没有假设性完整指数合计 |
| ARCH-06 | 部分指数数据 | 有缺口时完整总额KPI不可用；可选研究视图标明`PartialAggregate`及覆盖率 |
| ARCH-07 | 修订边界 | 前一相邻交易日缺失不跨日计算流量；份额/NAV/折算历史变更触发重算 |

### 12.7 自动化测试层级

- **Unit**：单位转换、decimal、日期、指标计算、折算、规则引擎、版本选择；不依赖网络。
- **Contract**：保存匿名化官方样本与脱敏HTTP fixture，解析器字段契约及版本回归。
- **Integration**：临时SQLite、迁移、原子提交、并发去重、修订及回算；网络端用MockHttpMessageHandler。
- **PluginHost / Windows Service**：UI双插件加载、Service headless入口、安装/启停/升级/依赖冲突/签名错误/IPC授权、重启后Quartz持久化与单实例测试。
- **Live Smoke（手工或受控CI）**：仅在被允许的情况下低频查询官方公开源；不能让公网波动成为所有PR单测必须通过的条件。

## 13. 里程碑与发布门槛（数据POC与插件宿主并行验证）

| 里程碑 | 交付物 | 关键验收门槛 |
|---|---|---|
| M0 接口与许可POC | `DATA_SOURCE_POC.md`、5ETF样本证据、历史范围报告 | SSE/SZSE各至少一条合法自动数据路径验证，否则只发布离线研究预览 |
| M1 通用双宿主与Service | `CRS_PLUGIN_ARCHITECTURE.md`、后台专项文档、WPF插件宿主+Windows Service/Quartz+Named Pipes、假插件 | 未登录Windows仍可运行公开数据作业；SQLCipher Cron配置持久且Quartz触发器重建；无第二调度器；真实WPF加载、包验证与权限测试 |
| M2 ETF数据底座 | SSE/SZSE Source、RawStore、双时间轴、历史回补、确定性窗口重算 | 幂等、断点、可审计，不沿用旧NAV，不把缺口计零 |
| M3 ETF一期交付 | ≤20池、份额历史图、同步中心、证据下钻；金额归集仅在官方NAV通过后增量启用 | 5只POC通过；容量约束有效；无国家队交易错误归因 |
| M4 发布门槛 | 自动采集连续性验证、WPF加载、迁移回滚、税务回归 | 新增POC-09..12与核心PLG/DATA测试通过；手动版不得标自动版 |

**建议一期完成定义（DoD）**（按M0真实结果允许“自动监控版”或“离线研究预览版”分别验收）：

1. 五只ETF均有真实来源与状态；SSE/SZSE自动通道通过M0才能称自动监控，手动官方导入只能称离线研究预览。
2. 目标取得最近250个有效交易日份额；无法达到时不得伪造，并将未达项作为产品功能限制；公开报告真实覆盖率及缺失清单。
3. 所有份额变化能从界面追溯并复算；修订不覆盖原始证据，NAV与折算修订同样触发缓存重算。**如官方NAV自动渠道未通过验收，则第一版仅验收份额变化，不验收金额估算与资金曲线**。
4. 宿主不静态引用业务插件；官方插件可启用/禁用/受信升级，数据与二进制按`UpgradeJournal`受控恢复；绝不修改税务数据表。
5. 启动/登录/税务计算/既有绩效P0/历史报告的回归测试全部通过。
6. 主程序和插件发布许可与安全边界经过记录。

## 14. 工作拆解（开发Issue建议）

- `ETF-001`：官网数据源与许可POC，保留原始网页数据样本，出具`DATA_SOURCE_POC.md`。
- `PLG-001`：插件SDK、官方预装manifest可信哈希验证与版本兼容；不开发第三方任意安装器。
- `PLG-002`：CRS启动生命周期注册、导航贡献点、故障降级。
- `PLG-003`：插件私有数据目录、WAL安全快照、候选库迁移、UpgradeJournal强杀恢复、RawStore引用核对与启停管理。
- `PLG-004`：真实WPF假插件验证资源和导航，再用第二假插件验证通用性/依赖冲突拒载。
- `PLG-005`：在宿主层整理通用插件约束与CI架构依赖检查。
- `SRV-001`：Windows Service发布/安装/卸载、LocalService身份、ProgramData/ACL与服务恢复。
- `SRV-002`：Quartz RAMJobStore+SQLCipher调度定义，单实例互斥、稳态/误点火/重启补偿（备选加密AdoJobStore需独立POC）。
- `SRV-003`：Named Pipes只读与管理授权、版本化DTO、WPF状态与控制页面。
- `SRV-004`：Headless后台插件入口/Session/调度Dispatcher，禁止引用WPF；与UI插件共享Core/Data。
- `SRV-005`：Service停写、升级维护、Journal一致性恢复和Windows服务账户变更密钥预案。
- `ETF-002`：最多20只有效池、Institution/Security/DisclosureEvidence统一领域模型、Schema与不可变EvidenceStore。
- `ETF-003`：SSE数据源适配器及契约测试。
- `ETF-004`：SZSE数据源适配器及契约测试。
- `ETF-005`：官方CSV/XLSX导入的兜底Provider（按已确认格式）。
- `ETF-006`：历史回补、Checkpoint、日度增量和修订复核。
- `ETF-007`：单位、缺失、异常、折算与跨源冲突校验。
- `ETF-008`：份额变化、NAV同日约束（NAV源验证后）、指数金额归集可选、双时间轴、按日期窗口的确定性失效重算。
- `ETF-009`：WPF概览、明细、同步中心、来源健康和证据下钻。
- `ETF-010`：自动化回归、20交易日采集稳定性验证、WPF双插件兼容、崩溃升级恢复、现有绩效P0回归、数据使用条款复核及发布验收。
- `EQ-001`（二期）：上市公司/基金官方披露导入和机构主体别名审核。
- `EQ-002`（二期）：机构直接持股快照、报告期与披露日、股份类别及变动比较。
- `EQ-003`（二期）：股票→机构和机构→股票WPF查询、证据下钻。

## 15. 技术风险清单

| 风险 | 影响 | 对策/退出机制 |
|---|---|---|
| 免费网页接口变动/停用 | 无法新增数据 | 版本化适配器、官方允许文件导入、暂停受限数据源 |
| 免费历史不完整 | 无法严谨一年回测 | 展示覆盖率、声明实际起点；不伪造历史 |
| 网站服务条款变化 | 不能继续自动采集 | 停止自动请求，保留已合法取得记录，重新审查 |
| 上交所/深交所不同口径 | 横向数据不可比 | Source-specific正常化与独立质量规则 |
| ETF份额折算 | 假信号 | 公司行为证据 + 调整因子 + 重新计算 |
| 历史公开数据修正 | 旧信号失真 | 不可变修订 + 原始证据 + 计算版本 |
| 插件恶意/失控 | 可能波及整个CRS进程 | 仅第一方签名包；未来不可信插件必须进程隔离 |
| 插件WPF生命周期泄漏 | 界面卡顿/更新失败 | UI资源与后台任务分开；UI退出不停止Service |
| 私有数据与市场数据混用 | 安全隐私风险 | 独立库、独立路径，不向插件暴露个人数据访问能力 |
| 错误归因国家队交易 | 投资判断误导 | A/B/C明确证据分层，第一期只发表ETF份额事实 |
| 股票战略持股与交易混淆 | 虚假增持信号 | `DirectEquityHolding / StrategicControl / PublicActionAnnouncement`分开 |
| 插件框架过度设计 | 维护成本增加 | 仅首期能力集、一个业务DLL，公共业务能力真实需求出现后再抽取 |
| DB Schema升级只回滚DLL | 插件不能打开库甚至丢数据 | Service维护期、候选库预检，代码+数据成对恢复，不能静默覆盖新写入 |
| LocalService网络/文件ACL不适配 | 官网采集失败或存储不可写 | 服务账户真实环境POC、ProgramData ACL、Windows事件日志 |
| 多Service实例重建相同Quartz计划 | 冲突/重复触发 | SCM单服务+进程单实例锁；调试模式拒绝并发；不启用Quartz集群 |
| WPF与服务IPC越权 | 普通用户调整所有任务 | 命名管道DACL、Windows SID/组、命令白名单、审计 |
| WPF更新但Service版本旧 | 协议不兼容、UI显示错误 | DTO协议版本协商，提示升级，允许只读降级 |

---

## 参考与核实记录

- [Windows Service .NET Worker 官方实现](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
- [Quartz Hosted Services Integration](https://www.quartz-scheduler.net/documentation/quartz-4.x/packages/hosted-services-integration.html)
- [Quartz持久化SQLite单实例与限制](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html)
- [Windows Named Pipe访问控制](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)


1. CRS项目README（仓库架构、WPF与安全）：<https://github.com/sword0880/CRSTool>
2. 上交所ETF规模（2026-10-13页面数据、清算后口径）：<https://www.sse.com.cn/market/funddata/volumn/etfvolumn/>
3. 上交所基金规模与Excel导出参考：<https://etf.sse.com.cn/fundlist/scalelist/index.shtml?code=510310>
4. 上交所ETF申购赎回清单示例（588000）：<https://www.sse.com.cn/disclosure/fund/etflist/detail.shtml?fundid=588000>
5. 深交所市场数据入口（基金数据/ETF规模菜单）：<https://www.szse.cn/market/>
6. 微软官方.NET插件教程：<https://learn.microsoft.com/zh-cn/dotnet/core/tutorials/creating-app-with-plugin-support>
7. 微软`AssemblyLoadContext`说明（**不是安全沙箱**）：<https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-runtime-loader-assemblyloadcontext>
8. 参考开源研究看板（23只ETF、份额差×NAV）：<https://github.com/wwww1998/national-team-etf-flow>及其`refresh.py`
9. 中央汇金披露ETF范围的二手线索（入池以实际基金官方报告为准）：<https://finance.eastmoney.com/a/202603313690167766.html>
10. WPF XAML在ALC的兼容性议题：<https://github.com/dotnet/wpf/issues/1700>
11. SQLite在线备份与WAL：<https://www.sqlite.org/backup.html>、<https://www.sqlite.org/wal.html>
12. CRS公开项目结构与绩效当前状态：<https://raw.githubusercontent.com/sword0880/CRSTool/main/docs/architecture/PROJECT_STRUCTURE.md>
13. CRS通用插件宿主规范（本项目配套文件）：[CRS_PLUGIN_ARCHITECTURE.md](./CRS_PLUGIN_ARCHITECTURE.md)

> 核实声明：本轮还核实了CRS公开`CRS.sln`、`CRS.Desktop.Wpf.csproj`、`docs/architecture/PROJECT_STRUCTURE.md`和既有绩效规格。交易所公开页面不等于稳定正式API；**仍未验证当前两市内部数据接口可持续合法调用**，也未执行本地WPF/插件编译测试。所有HTTP参数、历史范围、发布许可与实际软件兼容性，均以M0/A-0现场POC为准。
