# NAV与份额金额研究工作区

开始日期：2026-10-26；本轮验证完成于2026-10-27。此前NAV事实链和`EtfNavStudy`算法已存在，份额金额研究仅能经测试工具组装。本轮将它接入后台应用、只读分页和正式桌面工作台，不改变既有金额公式或来源许可。

## 用户入口与口径

顶部“资金与持仓”菜单新增“NAV与份额金额”。选择Worker已配置的ETF及日期后主动查询；页面不创建第二个数据池、不自动下载NAV，不定时轮询。

页面分别展示1/5/20交易日窗口、NAV修订及来源/冻结身份。金额沿用`EtfNavStudy`逐日相加`(Q_t-Q_{t-1})×单位NAV_t`；不用累计净值或期末净值乘整个期间差额。它是基金整体份额变化金额研究，不是国家队具名成交、精确现金净流入、市场顶底或退出信号。

当前已核对的NAV来源仍是159915；其他ETF按实际登记资料显示缺失。加入菜单不代表已经有全监控池NAV覆盖。日期和时刻以北京时间展示；更改证券/日期立即清理旧结果并取消本次读取，迟到响应不能写回当前条件。

## 同一快照的跨模块输入

`EtfNavStudyStore`经同一个`MarketDatabaseSession`只读事务取得：

- 当前证券的份额、修订及已登记日历，附90自然日前置区间。前置交易日或份额仍不足时保持不可计算，不按工作日猜补。
- `NavStore.ResearchSnapshot`按知识截止选择NAV修订、核验四类原件，待审更正阻断对应日期，不能选回旧值。
- 当前证券的可比性及原件；读取绑定证券、原文身份、Q15和当前取消令牌，不扫描其他基金的可比性原件。

功能端口`IEtfNavStudyReadRepository`只负责冻结输入，`NavUseCases`调用既有纯算法。页面不进行金额计算，未取得日历、份额、可比性或同日NAV时显示具体状态；未知金额是null。完全没有NAV事实时允许明确的空NAV数据集，其空集合指纹只是查询身份，不代表零持仓或零净值。

冻结输入额外保留NAV修订及状态，原报告仍为`RetrospectiveOnly / NotProven / NotObserved`。已存NAV原件没有记录字节长度，DTO改为可空长度，保持未知；离线原件解析仍记录实测字节数。来源、修订、日历、可比性和实际窗口均可随完整JSON复制，用于数学复算；复制或复算不代替再次核验原件。

冻结输入合同和稳定排序扩展后，研究版本升为`etf-nav-study-v2`；金额公式不变，旧v1报告不按新合同静默解释。复制JSON保存后，可用`CRS.Tests.NationalTeamWatch --nav-workspace-verify <json文件>`检查版本、完整输入指纹及全部窗口数值，篡改输出即使保留原输入指纹也拒绝。该命令只作数学复算，不联网或读取数据库。

本轮不新增数据库表、加密或自动采集计划。国家队仍用普通SQLite，收入/报税加密边界不变。

## 完整文档的传输边界

`nav-study`为只读查询；参数绑定证券、起止日期及服务端一致性快照。完整研究先冻结，再分块分页，不能每页重算。客户端只做文档组装及证券/日期身份核对。

通用组件`CRS.Common/Serialization/JsonDocumentBlocks`位于插件外层：默认文档上限1MiB，每块8KiB；序列化写入途中限制长度，接收端检查格式、连续偏移、总长、统一SHA256、完整内容及取消。缺块、乱序、混入另一份文档或超限均拒绝整份结果。宿主既有46KB单页和2MiB快照限额保留，未扩大查询超时。NAV客户端文件独立归`Application/Worker/Nav`。

## 验证与剩余门槛

入口为`CRS.Tests.NationalTeamWatch --nav-workspace`，默认回归及CI也包含。覆盖有界文档、真实仓储跨模块冻结、NAV缺失/损坏/更正、后台分页、UI旧请求隔离及数学复算。跨进程整套还检查真实Runner返回无NAV/日历的冻结结果，而不是空成功。

最终证据：

- 37项目Release全量构建零警告、零错误；`artifacts/national-team-nav-workspace-final-build-v4-20261026.log`。仅测试文件后续增加冻结样本输出及复算正例，专项项目重新编译零警告、零错误，记录于`national-team-nav-workspace-check-build-v2-20261027.log`。
- v2完整默认回归通过，含NAV工作区31项、普通存储33项、应用边界14项及既有领域/事务/取消/修订检查；`artifacts/national-team-nav-workspace-full-regression-v2-20261027.log`。独立命令行对15个窗口数学复算通过；`national-team-nav-workspace-cli-verify-20261027.log`。
- 完整后台开发候选包为`artifacts/national-team-nav-workspace-worker-candidate-v2-20261027`；48项跨进程服务检查及11项国家队并发检查通过，记录于`national-team-nav-workspace-worker-integration-v2-20261027.log`。160份索引文件SHA256一致，无测试夹具；`national-team-nav-workspace-package-hashes-v2-20261027.json`。
- 实际自动桌面开发包的五个内部页面加载通过；`national-team-nav-workspace-package-load-v2-20261027.log`。真实窗口640/1040/1280 DIP、100/125/150%密度共9张合成渲染，通过数值/状态列宽断言并查看关键视口；`national-team-nav-workspace-visual-v2-20261026.log`及仓库`design-qa.md`。
- 架构和功能门禁通过，客户端按7个功能文件归档，XAML/代码配对7页；`national-team-nav-workspace-architecture-20261026.log`和`national-team-nav-workspace-layout-20261026.log`。CI已加入专项，未声称远端已执行。

这些均为源码、候选包与隔离数据证据。未安装候选包、迁移旧加密数据、改变正式计划或发送真实通知；单基金NAV来源和实际覆盖仍须独立验收。

市场周期评估仍缺带历史证券宇宙的广度输入及运行接线；本页不将成交统计替代广度，也不将NAV低点当作熊市底部。具名ETF持仓和联合退出模型继续按各自证据门槛推进，整体目标保持进行中。
