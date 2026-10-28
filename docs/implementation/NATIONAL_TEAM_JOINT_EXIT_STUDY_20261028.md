# 联合退出观察：当前快照研究

续项：下文v1.0/格式v1是本轮前一候选的证据。现已修复正常多期披露混入单基准的问题，新规则v1.1／格式v2保留最新披露子集及排除原因，最新期异常仍关闭判断。参见[具名ETF多期历史](NATIONAL_TEAM_NAMED_ETF_HISTORY_20261028.md)，未提升真实模型或数据验收状态。

2026-10-28正式状态：本轮后端随[双库保全维护](NATIONAL_TEAM_STORAGE_MIGRATION_20261028.md)已安装并Committed；最终闭日抽检中`SSE:512100`联合退出研究仍RequestTimedOut，不能以隔离测试替代正式查询验收。服务健康和PID保持，后续定位查询性能；资料、历史时点、样本外验证及金融通知门槛仍开放。

此前五个研究方向各自独立，退出预警仍只消费ETF份额及具名下界。本轮在顶部“周期与风险”增加“联合退出观察”，按**当前启用配置池中的单只ETF**手动查询。没有接入自动任务、金融事件或Outbox。

## 分层与身份

Core的`Domain/ExitAlerts/JointExitStudy`拥有归一输入、分量门槛和共现规则，不读库、不发通知、不输出概率。Jobs的`Infrastructure/ExitAlerts/JointExitStudyStore`在同一独立SQLite只读事务、同一快照前捕获的截止时刻内调用五个功能；用例使用类型化读端口。Watch复用完整JSON分块合同并复算归一输入与结果，WPF仅展示。

NAV冻结与直接持股读取优先使用`ReadSnapshotCutoff`，避免查询期间时间漂移。分量携带证券、统计日/报告期、截止及上游报告摘要。完整原始价格、逐证券广度、行业曲线和所有原件**未重复嵌入**联合报告；重算范围是“归一输出到共现结果”，上游健康、解析和方法原件仍在对应模块核查。

## 五项输入门槛

| 分量 | 门槛与解释 |
| --- | --- |
| ETF份额与NAV | 同一ETF、统计日与截止；5/20日完整可比、250日覆盖和连续20日实际采集通过；同日5/20日NAV金额完整。两者同源，不重复计分 |
| 具名ETF持仓 | 独立持有及原件核验、同披露基准、份额可比，基准不过期。最低净减少下界不能定位当日交易或清仓 |
| 周期与市场成交 | 周期输入可研究、统计日及截止匹配；价格、两市量额及广度保留各自范围，合为一个周期证据族 |
| 直接持股方向 | 同报告期样本披露/分类/定价完整，无缺口及未知性质，原件有效，报告期不晚于统计日且不过期。不代表完整国家队组合 |
| 行业公布PE | 11行业代码和名称一致；当日两种PE可用，各至少252先前样本、覆盖≥80%，同一截止。不能代替PB、盈利质量或合理价值 |

五项均通过，且“周期高位风险候选、ETF份额收缩、**同一5日或20日窗口**NAV负金额、具名基准最低净减少下界为正”共现，才为`ReviewCandidate`（需核查）。直接持股与行业PE不堆叠为风险分；行业PE低位、底部及修复分别列为反向核查。

资料齐全未共现为`NoJointCandidate`，关键资料不足为`Unavailable`。**未形成候选不是安全结论。** `joint-exit-candidate-v1.0`和默认370日披露时效过滤均未校准；固定保留`NotificationsEnabled=false`、`TimingProof=NotProven`、`ModelValidation=NotValidated`。

## 尚未关闭的门槛

模式为`CurrentSnapshotResearch`：当前配置、部分审核及直接持股只提供当前版本。选择旧统计日不证明当时已知；入口拒绝历史`KnowledgeCutoff`，不能宣称历史择时表现。没有持久联合输出日志、成熟样本标签或样本外验证。

具名下界仍要求同一披露基准；混合报告期保持不足，不静默丢弃不一致账户。未证明配置池完整覆盖汇金、证金、外管局、社保、国家大基金等主体；身份、独立经济持有关系及正式披露须逐项核验。行业长历史、价格方法、完整广度、NAV及真实250/20仍开放。

本轮不替换已安装Worker、不删除ProgramData、不迁移真实历史库。普通SQLite正式部署/遗留加密库迁移仍须保全维护事务和安装后IPC回读；收入报税保持加密，密钥及Worker并发不变。

## 验证

专项入口：`CRS.Tests.NationalTeamWatch --joint-exit-study <隔离目录>`；跨进程：`CRS.Tests.WorkerEtf --joint-exit-pipe <完整候选> <不存在目录>`；控件：`CRS.Tests.PluginRecovery --joint-exit-visual-preview <专项输出> <截图目录>`。

候选分支使用**合成归一输入**，不代表上游真实数据验收。跨进程测试使用实际Service、监督Runner和真实客户端，金融数据为隔离合成成交及缺失联合分量，只证明协议和失败关闭。控件渲染是合成资料，不是用户现场首次点击验收。

2026-10-28 本地终态结果：

- 联合专项45项通过，覆盖分量缺失、250/20门槛、同窗口匹配、未来/过期披露、证券及截止错配、原件引用、取消/迟到响应、实际仓储快照与后台分块。记录：`artifacts/joint-exit-study-clock-checks-20261028/acceptance.json`及同名日志。最后增加两项只修改测试，未改变候选生产程序集。
- 实际Service／监督Runner／客户端跨进程21项通过，其中5项验证联合查询，其余复用成交审核及周期链路。缺失输入保持无法判断，不新增金融事件，宿主PID及健康一致。记录：`artifacts/joint-exit-study-pipe-20261028/acceptance.json`。
- 12张实际WPF控件渲染通过：候选/缺失、640/1040/1280 DIP、100/150%输出密度，700 DIP高时正文569 DIP。记录：`artifacts/joint-exit-study-visual-20261028/layout.json`。已人工查看640候选及1040缺失图，横向滚动保留全部证据列。
- 38项目Release零警告、零错误：`artifacts/joint-exit-study-final-build-20261028.log`。国家队完整默认回归退出码0：`artifacts/joint-exit-study-full-regression-20261028.log`，含NAV及直接持股既有专项。最终测试增补后再次构建及45项运行均为0。
- 架构、38项目依赖图及功能目录通过：13个Worker客户端分部、13对插件XAML/后台文件、8个窄客户端合同；WPF规范覆盖51个XAML。4份更新文档112条本地链接有效，`git diff --check`通过。CI已加入专项及渲染步骤，未声称远端执行。
- 不可变开发候选`artifacts/joint-exit-study-worker-20261028`完整发布；`release.json`所列182文件与插件信任索引84文件均重新计算SHA256一致，记录`artifacts/joint-exit-study-package-hashes-20261028.json`。候选未安装，未签生产发行签名。

相关：[当前架构](../architecture/CURRENT_ARCHITECTURE.md)、[周期](NATIONAL_TEAM_MARKET_CYCLE_STUDY_20261027.md)、[广度](NATIONAL_TEAM_BREADTH_WORKFLOW_20261028.md)、[模块重构](NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)、[测试](../../tests/README.md)。
