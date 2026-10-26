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

## 后续源码结构：按业务纵向、按层横向

已有根目录平铺的生产文件**本 PR 暂不批量迁移**，避免同一时期大量路径变动干扰正在执行的金融来源回补及回归。新增或重构功能统一按下述目标结构逐项迁移，类型命名空间优先保持不变以减少 ABI 变化：

```text
src/Plugins/
  CRS.Plugins.NationalTeam.Core/
    Domain/
      Etf/                 # 份额、NAV与市场活动规则
      Equity/              # 机构持股、披露与机构归属
      Industry/            # PE/PB、估值和周期行业差异
      MarketCycle/         # 高低位研究规则，禁止直接指令化
      Alerts/              # 减持信号与证据门槛
    Application/           # 场景用例与端口，按以上业务分别聚合
  CRS.Plugins.NationalTeam.Jobs/
    Application/           # Worker用例，按业务目录
    Infrastructure/        # 原件、官方适配、仓储与修订；禁止 WPF 依赖
  CRS.Plugins.NationalTeamWatch/
    NationalTeamPluginEntry.cs   # 只组合分类导航和各页面ViewModel
    Presentation/
      Navigation/          # 唯一分类注册表和按需加载工作台（已实现）
      Etf/                 # 待逐项搬迁
      Equity/              # 待逐项搬迁
      Industry/            # 待逐项搬迁
      MarketCycle/         # 待完成可用性门槛后新增
      Alerts/              # 待完成可用性门槛后新增
    WorkerEtfWorkspace.*.cs # IPC按业务拆分；不得在UI绕过Worker读取库
```

**单职责要求**：View和ViewModel不得计算投资事实；Domain不得依赖WPF或网络；Jobs拥有来源与加密库；导航只管理分类与页面生命周期；宿主不得枚举“国证”“汇金”等业务专属分类来分组。

同一子模块超过约300行时优先按查询、命令、图表投影及证据查看职责拆小文件，而不是机械按每100行分割。禁止新增一个跨行业、ETF和机构持股的巨型 `NationalTeamViewModel`。新增业务分类先加目录/证据边界，再加页面/测试；**不能仅仅改菜单名称就算完成重构**。

## 反方审查

| 潜在问题 | 本次防线 | 后续验收 |
| --- | --- | --- |
| 改路由导致用户找不到原功能 | 单主入口+分组菜单；原三路由身份保留 | 真实插件宿主安装包切换及导航截图 |
| 导航页启动引发大量Worker IPC查询 | 根页面懒加载，未点击不创建子页 | Windows界面隔离测试；实机时间线 |
| 点击视图构造失败后清空旧内容 | 先成功创建新页再替换，失败不更新路由 | 注入失败工厂测试 |
| 每次切换累积UI图表和控件 | 只保留一个活跃 `Content`；模型由Session管理 | 长期切换内存与取消/离开期间回写测试 |
| 新分类只有菜单没有已上线真实资料 | 不展示空分类，不提前列假模块 | 分类目录校验与真实来源/权限验收 |
| 客户端共享契约更改造成 MissingMethodException | 不改变已有构造器，新增属性采用非必填；正式打包必须成对升级插件+宿主共享DLL | 旧/新发行 ABI、可信包哈希、重启与真机装包验证 |
| 分类误导金融结论 | 页面明确写出证据限制，全行业PE与样本指数PB不拼接 | 真实来源原件、PIT、组合映射长期核验 |
| 隐藏旧菜单导致直达不可达 | 兼容路由仍注册；新首页单独暴露功能 | 非菜单旧路由的API级启动/导航回归 |

## 验收命令

```powershell
dotnet run --project tests/CRS.Tests.SectorUi -c Release -- --navigation
dotnet run --project tests/CRS.Tests.IndustryPe -c Release -- --output artifacts/industry-pe-checks
dotnet run --project tests/CRS.Tests.SectorUi -c Release -- --industry artifacts/industry-pe-checks/synthetic-industry-ui-report.json artifacts/industry-pe-checks/synthetic-industry-ui.png
dotnet build CRS.sln -c Release
```

CI 里这些是**合成分类/UI与旧行业回归测试**，不能代替正式插件包可信加载、升级前后密钥与Worker保全、真实市场数据或长期性能的发行验收。更新真实安装包前必须运行相应发行门禁并保持 PR 草稿至完成验证。
