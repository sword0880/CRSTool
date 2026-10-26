# NationalTeamWatch 分层重构续项（2026-10-26）

状态：进行中。本文是本轮源码重构记录，不能代替已安装 Worker、真实数据或模型有效性的验收。

## 当前目标与约束

按 ETF、直接持股、行业估值、市场周期、NAV 和退出预警分目录，保留 Domain / Application / Infrastructure / Presentation 的依赖方向。跨插件复用的基础机制归属外层 CRS 工程，业务规则、来源许可和私有数据库仍归属插件。

用户已明确：项目尚未正式上线，接口需要调整时直接修改，**不保留旧版二进制或接口兼容层**。旧文档中要求保留 `IEtfWorkspace`、过渡 UI 适配器及 `CRS.Time.dll` 的限制由此废止。持久数据的事务、原件和取消语义仍须验证。

## 已实施的结构调整

| 调整 | 实现与职责 | 行为边界 |
| --- | --- | --- |
| 独立直接持股模块 | `Infrastructure/Equity/EquityStore`，按持股、公告索引、历史原文及会话分文件 | 披露审核、逐账户身份、可比基线、原件与命令回执沿用同一事务；原聚合入口只转发 |
| 独立预警模块 | `Infrastructure/ExitAlerts/AlertStore`，按评价、原文、历史回算、事件生命周期、领取和会话分文件 | 模块装配 Event / Outbox / Delivery Store；事件、冻结输入和通知一起提交；在途未知结果不自动重发 |
| 预警跨模块端口 | `IAlertMarketFacts`，组合根通过私有适配器提供 ETF 事实 | 不注入整个总仓储；复用原读取快照、原核验时机与事务，不重复打开数据库 |
| 清理旧工作区接口 | `INationalTeamWorkspace` 仅组合六个功能端口；请求 DTO 独立为 `NationalTeamProtocol.cs` | 删除重复 `IEtfWorkspace` 与三套 Legacy UI 适配器；页面继续使用各自功能端口 |
| 正文容量机制外移 | `CRS.Common/Net/BoundedContentReader` | 每块检查真实字节数，取消沿用调用方令牌；不包含国家队域名、Referer、预算或重试策略 |
| 统一公共时间依赖 | Application、Plugin/Worker Abstractions、Notifications、NationalTeam.Core 直接引用 `CRS.Common` | 移除 `CRS.Time` 项目和类型转发；Runner清单移除旧DLL，保留北京时间与JSON偏移语义 |

`OfficialHttpGateway` 继续持有来源白名单、同源预算、20秒期限和单份原件容量上限，仅将正文读取委托到公共组件。响应头缺失或低报长度不允许绕过真实字节限制。

重构开始时已有依赖锁文件变更。原锁文件保全在 `artifacts/national-team-refactor-preexisting-locks-20261026`；随后按新的直接依赖重新恢复，不通过恢复旧锁文件丢弃已有工作。

## 本轮验证

验证日志统一位于 `artifacts/national-team-refactor-*-20261026.log`。首次系统临时目录写入受到权限限制；随后使用工作区 `artifacts/refactor-isolated-temp`，不修改正式数据目录或生产 ACL。完整套件还包含 Windows 命名管道权限检查，应区分环境拒绝和业务断言失败。

当前已取得：公共正文读取 8 项通过；工作区隔离的 ETF 加载专项通过；时间依赖调整前的模块构建为零警告、零错误，目录及架构门禁通过。时间项目删除后的最终全解决方案构建、完整回归及打包结果须补入本文，未完成前不得将中间结果作为最终验收。

## 五项业务目标仍按各自证据推进

| 功能 | 当前可复用基础 | 尚待关闭的门槛 |
| --- | --- | --- |
| ETF成交量跟踪 | ETF份额、全市场成交活动、原件、修订和覆盖状态 | ETF本身成交与全市场成交分别建模；具名国家队成交需独立合法证据，不能由份额或市场总量反推 |
| 直接持股主要方向 | 多主体逐账户披露、历史原文、可比期间变化 | 有效期行业/风格映射、同报告期匹配市值、覆盖分母与战略持股去重；当前样本不得冒充整体组合 |
| 行业低估研究 | 行业公布PE和样本指数/PB研究链 | 长期可比历史、覆盖、盈利质量及行业供需反证；低PE标签不等于低估结论 |
| 顶部/底部与NAV | 独立NAV事实链、指数及周期领域研究 | 完整价格/广度输入、冻结规则和知识时点、样本外与影子验证；基金NAV本身不代表市场周期 |
| 退出预警 | 已拆分的事件、冻结证据、修订回算、Outbox与投递状态 | 完整且独立的数据/模型门槛；研究预览不能自动升级正式金融提醒 |

## 下一步架构工作

1. 将 ETF 核心与原件完整性实现从 `MarketRepository` 分离，最后迁移 SQLCipher 连接和初始化所有权。当前聚合类仍承担这些职责，不能称全插件重构完成。
2. 将预警用例协调与存储协议进一步分离；模块化的 `AlertStore` 已收拢原实现，但不应宣称领域算法、应用协调与SQL已全部独立。
3. 清理其余默认不支持实现和旧命名，改为功能能力契约；缺少采集记录不得返回假空集合。
4. 在完整重构回归后再生成新包并通过正式维护流程验证；本轮没有替换已安装服务或改变其正常计划。

架构入口见 [当前架构](../architecture/CURRENT_ARCHITECTURE.md)，业务及真实数据门槛见 [研究开发记录](NATIONAL_TEAM_RESEARCH_DEVELOPMENT_20261024.md) 和 [周期需求](../architecture/NATIONAL_TEAM_MARKET_REGIME_REQUIREMENTS_20261024.md)。
