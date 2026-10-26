# NationalTeamWatch 架构调整后的反方复核（2026-10-26）

## 复核范围

按用户最新说明核实**已有实现是否充分**，不依据旧进度表直接重建功能。当前口径：

- 国家队持仓与资金方向围绕Worker当前启用的ETF清单；关注汇金、证金、外汇储备相关投资法人、社保及国家大基金等具体具名主体。集团、管理机构、投资法人、基金代次和账户不能凭名称混同。
- 成交量/成交额观察市场整体冷热。现有来源为沪深股票统计，覆盖范围明确披露；它不用于推断某主体的成交。
- 直接持股、行业估值、市场周期/NAV及退出预警逐项核对代码、运行链路和数据，区分“实现已存在”“正式数据缺失”“算法尚未接线”和“查询不可用”。

本轮没有替换已安装Worker、触发采集、审核金融事实、改变来源准入或发送通知。

## 当前现场证据

只读报告：`artifacts/national-team-research-feature-audit-20261026/feature-audit.json`，记录于北京时间 **2026-10-26 15:14:36**。健康核查前后为同一Running进程；有一项查询不可用，报告 `AllQueriesAvailable=false`，操作工具退出码2，不能写成全量通过。

| 项目 | 本轮实际读取 | 能够证明 / 不能证明 |
| --- | --- | --- |
| 已设置ETF | 当前启用24只；逐只具名ETF持仓查询均成功返回0条登记记录 | 证明这24只中尚无该表的已登记具名持仓；**不是证明这些机构实际零持仓** |
| ETF可比资料 | 4只具有已确认可比元数据 | 其余不能按未经确认的折算口径计算正式期间信号 |
| 市场成交 | 返回664条，最近完整日为2026-10-23；当日2026-10-26为Incomplete，量额保持null | 市场热度链已存在；不能把尚未取得完整当日资料认定为零成交或来源故障 |
| 直接持股 | 29行，其中15条已核披露快照，覆盖4只股票 | 已实现具名披露及逐账户核验；样本量不代表完整国家队组合 |
| 行业公布PE | 11行业返回，已有历史样本；以当日为截止时当前日显示Missing | 历史事实链已实现；当日Missing不能推成整条链未实现，另按已结束区间验证 |
| 指数/PB研究 | 11个研究条目返回，资料不足及来源准入保持明确状态 | 查询/门槛已接通；不能以11个条目冒充11份完整历史行情 |
| NAV | SZSE 159915返回260条，最近2026-10-23单位净值2.7018 | 单只基金正式NAV链已实现；不能外推为整个ETF池NAV完整或市场顶底已识别 |
| 退出事件 | 432条现有事件可读 | 证明事件链及查询存在，不证明融合了全部五类信号或具有预测有效性 |
| 预警总览 | `RequestTimedOut`，未返回可验收的门槛状态 | 本轮 `AlertDataAcceptance=null`；不能解释为false、零覆盖或Worker退出 |

当前日检查用于核验缺失处理，不能替代已结束交易日的估值研究。行业真实窗口补验使用2025-09-19至2026-10-23；指数窗口补验使用2026-10-20至2026-10-23。结果及截图保留在 `artifacts/national-team-review-20261026`，不能将截图显示的资料不足称为已完成行情接入。

## 反方发现

### P1：周期评估算法存在，但当前源码运行链路没有调用它

[MarketCycleEvaluator.Evaluate](../../src/Plugins/CRS.Plugins.NationalTeam.Core/Domain/MarketCycle/MarketCycleResearch.cs) 已有顶部/底部研究规则和反例测试。当前生产源码搜索该评估器/输入/输出类型，仅命中领域定义文件；应用层、Worker查询/任务与页面没有对应评估调用。现有指数研究入口不能作为该周期模型已接通的证据。

关闭条件：以真实且有来源的价格/广度/成交输入构造冻结评估，接入应用与Worker查询，再验证缺失、修订和知识截止；保留NotProven及模型有效性边界。

### P1：退出预警已有完整事件机制，输入范围仍是份额规则及具名最低减少证据

[AlertFundInput与ExitAlertEvaluator](../../src/Plugins/CRS.Plugins.NationalTeam.Core/Domain/ExitAlerts/ExitAlerts.cs) 及 [AlertStore.Evaluation](../../src/Plugins/CRS.Plugins.NationalTeam.Jobs/Infrastructure/ExitAlerts/AlertStore.Evaluation.cs) 的现行流程负责份额窗口、样本集群、数据质量与具名持仓最低净减少。它没有读取行业研究、周期评估或NAV研究结果。

因此“已有退出预警”成立；“通过全部五项做联合退出判断”还不能从当前调用链证明。现有预览、正式数据门槛和通知结果未知保护应继续保留。

### P1：股票持仓展示已经实现，主要行业/风格方向仍缺独立投影

[EquityCharts.RebuildEquityOverview](../../src/Plugins/CRS.Plugins.NationalTeamWatch/Presentation/Equity/EquityCharts.cs) 当前按市场/证券/名称生成机构矩阵；[EquityStore](../../src/Plugins/CRS.Plugins.NationalTeam.Jobs/Infrastructure/Equity/EquityStore.cs) 返回逐账户快照和可比数量变化。未见将持股映射为同报告期行业/风格、市值权重、覆盖分母及战略/组合性质的独立后台报告。

关闭条件：确认用户需要的方向口径，用有有效期和来源的映射、匹配日期市值及覆盖说明形成后台投影；不将股数跨证券加总当权重。

### P1：总览实际超时，健康正常，仍需分阶段定位

真实只读审查捕获了预警总览超时，前后Worker健康/进程一致。[AlertOverview](../../src/Plugins/CRS.Plugins.NationalTeam.Jobs/Infrastructure/ExitAlerts/AlertStore.Evaluation.cs) 依赖全池Acceptance；[ReadAcceptance](../../src/Plugins/CRS.Plugins.NationalTeam.Jobs/Infrastructure/Etf/EtfStore.Acceptance.cs) 的无证券筛选完整性核验仍会进入较宽的原件读取。

这是**源码层潜在耗时来源**，本轮没有为该具体现场查询取得分阶段耗时，因此不将其直接宣布为唯一根因。后续代码整改已将份额与采集收据批量读取，并在单次验收内复用原件核验结果；完整性实现提取为`EvidenceIntegrityStore`，全池原件范围和发送前正式门槛保留。新增`acceptance-profile`只读诊断可分别观测SQL、原件核验及投影。新包尚未替换已安装Worker，因此原现场超时保持开放。优化不能取消金融证据核验、放大查询超时或把超时改成空成功。

## 本轮已修复的复核发现

| 问题 | 修复及验证 |
| --- | --- |
| ETF页面隐式依赖主程序私有BooleanVisibility资源，独立宿主创建失败 | 页面显式声明WPF内置转换器；实际完整包内ETF、直接持股、行业PE、样本指数四页均成功创建与布局 |
| 管道权限拒绝/SCM身份核验异常逃逸，行业页首次Loaded可能令桌面退出 | 公共通信层转为带原因的BackgroundServiceException；保留RequestId和写结果未知语义；行业/指数读写状态边界处理权限及超时；行业17项和12项IPC边界通过 |
| 四个行业/指数测试工程不在主解决方案，既有全量构建漏检 | 纳入解决方案及当前架构清单，门禁覆盖生产与测试项目；37项目Release零警告/零错误，修正目录移动后的CI触发路径 |
| 旧工作区/时间兼容层与隐藏路由造成重复责任 | 按用户未正式上线的约束移除重复接口、三套适配器、Time兼容壳及三个旧宿主入口；随后改为顶部菜单，工作台内部导航24项通过 |

直接持股、预警及原件完整性模块提取、公共正文读取等结构调整见[分层重构记录](../implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)。ETF核心和连接/初始化所有权已独立，国家队公开资料按用户要求改用普通SQLite；后台与客户端工作区进一步分离，八组用例共享执行会话，界面合同移除默认实现。来源审批注册表与预警存储内部协调仍需收拢，不能称整个插件架构已经彻底解耦。

后续源码将已有NAV金额研究接到同一只读快照和桌面入口，见[NAV工作区](../implementation/NATIONAL_TEAM_NAV_WORKSPACE_20261026.md)；它不关闭上文市场周期或联合退出缺口。2026-10-27另取得510300两期具名持有人年报原件并形成待审对照，见[原件核对](NATIONAL_TEAM_ETF_HOLDER_SOURCE_REVIEW_20261027.md)，未登记正式持仓，不改变15:14现场快照。

## 本轮判断

现有基础能力较多，已经具备真实份额、市场成交、具名股票披露、行业PE、单基金NAV与预警事件链。本轮应继续围绕上面的实际缺口整改，不重复建设已存在的功能。当前尚不能把这些独立能力宣传成完整的国家队资金流向、行业主要配置与顶部/底部联合退出系统。
