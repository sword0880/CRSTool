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
