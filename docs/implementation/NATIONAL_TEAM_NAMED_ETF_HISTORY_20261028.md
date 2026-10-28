# 具名ETF多期披露与主体目录

## 发现及处理

联合观察此前将所有报告期一起传入要求单一基准的下界评估，正常积累多期披露后会一直无法判断。现按最新披露子集选基准，保留全部归一输入、逐期变化、未列示账户及每条排除原因。规则升级为`joint-exit-candidate-v1.1`、格式`joint-exit-study-v2`；旧格式不伪称按新规则复算。

名称分类此前只有汇金投资/资管、证金、社保和养老五组，股票人工审核也将允许值写死。现以Core的`Domain/Institutions/InstitutionAccounts`作为ETF及股票共用的业务目录，增加梧桐树具名平台、国家集成电路产业投资基金一期/二期/三期三个独立法人。股票筛选、审核及矩阵增加对应类别，性质仍由原文人工核验，绝不因“大基金”名称自动批准战略持有或减持。

该目录是国家队业务知识，保留在插件Core；完整JSON分块、指纹和时间继续复用外层CRS公共机制，没有把业务主体名单塞入通用Common。

## 变化与基准规则

- 仅对同ETF、同法人ID和账户、相邻**已登记披露期**比较，且需要原件、独立持有及覆盖两期的可比性依据。中间没有登记任何报告，仍不能证明该期间没有变化。
- 某期只登记其他账户而该账户未列示时，当前份额与差额均未知；之后重现不跨这段缺口比较。法人名称变更也保持不可比。
- 同期同账户同份额的多原件保留全部引用，只计一次。不同份额/法人名称、同名同账户两个法人ID，或同一ID对应不同法人名称，均关闭最新基准。首尾空格仅用于身份比较，不改写输入原文。
- 最新期原件损坏或有冲突时，不挑选其中一个值，也不回退较早期。未来报告期及晚于快照首次取得/公布的记录列明排除原因。
- 最新有效基准叫`LatestReviewedDisclosedSubset`，只代表已登记账户子集。未列示旧账户不带入最新期，期间份额差不能称当日成交、现金流或清仓。
- 具名记录不自动等于国家队身份；目录外主体保留在历史及排除项，不计入国家队基准。最新期只有未识别主体时保持`NoRecognizedInstitutionBaseline`，不回退旧期寻找可用数值。
- 五类主体覆盖表分别列中央汇金、证金、外汇局具名平台、社保及国家大基金。账户数为当前已核披露记录数；没有记录的持仓仍未知，没有真实组合分母，禁止给出覆盖率。

原ETF金融预警与历史回放保持原有审核及冻结流程，本轮未改为自动使用新的联合研究结果。联合观察使用新基准，固定研究模式且不发退出通知。

## 功能分层及展示

Core拥有具名披露历史及名称目录；Jobs的`EtfHoldings/EtfHoldingHistoryStore`通过类型化端口，在同一独立只读事务核验当前配置证券、可比性及具名原件，再调用领域规则；Watch的`etf-holding-history`客户端复用完整分块并重算输入。没有新增数据库表、后台采集任务或桌面SQL。

机构ETF页面改为紧凑工具栏和“已核持仓变化／官方报告目录”页签。历史手动读取；条件改变、取消、查询失败时清空旧结论，迟到结果不覆盖新条件。报告登记与人工批准仍分开。主体目录不能取代原件核验，目录检索不会批准持仓。

当前模式为`CurrentSnapshotResearch`，没有历史审核知识时间轴；选择旧统计日也只能研究当前已核版本。API拒绝传入历史`KnowledgeCutoff`，报告固定`HistoricalKnowledgeProof=NotProven`、`WholePortfolioProven=false`、`DailyTradingEvidence=NotObserved`。

## 名称参考及未完成项

国家大基金三期法人的完整名称可在[中芯国际公告2025-038第六节](https://static.cninfo.com.cn/finalpage/2025-12-30/1224907005.PDF)核对。梧桐树与外汇局的关系参考[中国进出口银行2024年度报告](https://english.eximbank.gov.cn/News/AnnualR/2024eng/202504/P020250429570886425523.pdf)的具名公司说明；本轮检索返回该官方报告片段，但整份PDF抓取超时/传输EOF，**未保存为已核金融原件**。上述资料只是名称目录参考，不是任何ETF持仓事实。

没有因新增目录就宣称已覆盖外汇局全部平台，地方产业基金、近似简称和未列出的平台不自动归入国家队。现有历史股票PDF专用解析档案仍按已核机构及金样范围开放，未扩成所有主体的通用解析器。真实配置池各ETF的报告、账户和经济持有关系、NAV及完整行情仍须分别验收。

本轮不安装候选、不迁移真实历史库、不清理ProgramData、不访问真实收入报税数据；国家队仍为普通SQLite，收入报税核心继续加密。正式部署仍需保全维护事务及安装后回读。

## 验证入口

`CRS.Tests.NationalTeamWatch --named-etf-history <隔离目录>`；`CRS.Tests.WorkerEtf --named-etf-history-pipe <完整候选> <不存在目录>`；`CRS.Tests.PluginRecovery --named-etf-history-visual-preview <专项输出> <截图目录>`。

所有合成原件、人工确认、实际Service/Runner管道和控件渲染只验证实现，不构成真实机构持仓或预测模型验收。

2026-10-28 最终本地证据：

- 多期持仓50项通过：`artifacts/named-holdings-boundary-checks-20261028/acceptance.json`。覆盖精确名称、相邻报告期、账户消失/重现、身份重复、冲突、原件损坏、目录外主体、原始小数精度、实际普通SQLite及取消/迟到响应。
- 联合观察49项通过：`artifacts/named-holdings-boundary-joint-checks-20261028/acceptance.json`。增加正常多期子集、最新冲突、目录外巨额持仓不能制造国家队下界等检查；模式仍为研究，通知关闭。
- 完整候选、实际Service及监督Runner跨进程28项通过：`artifacts/named-holdings-boundary-pipe-20261028/acceptance.json`。其中7项核验具名历史、5项沿用联合查询、16项沿用成交审核及周期链；含真实客户端分块重算、现场损坏原件后关闭基准、不新增金融事件/通知、同一健康进程。金融数据全部是隔离合成。
- 18张最终持仓控件渲染：`artifacts/named-holdings-final-visual-20261028/layout.json`；历史/冲突/空缺、640/1040/1280 DIP、100/150%输出密度，700 DIP高时正文表格437 DIP。窄页优先显示报告期、机构/账户、状态和份额差，完整法人及原件列仍可横向读取。
- 6张已保存官方目录回放：`artifacts/named-holdings-verified-report-visual-20261028/layout.json`，760 DIP高时表格506 DIP。最初宽度门禁失败后修正正文横向内边距，保留失败日志`named-holdings-report-visual-20261028.log`；最终不放宽门禁。持仓640图、目录640图及主体缺口1040图已人工查看。
- 38项目Release最终构建零警告、零错误：`artifacts/named-holdings-boundary-build-20261028.log`；收紧主体边界后的完整默认回归退出码0：`artifacts/named-holdings-boundary-full-regression-20261028.log`。架构/依赖图、13个Worker客户端分部、14对插件XAML/后台文件、8个窄客户端合同及52个XAML视觉规范均通过。
- 五份当前文档120条本地链接有效，`git diff --check`通过。CI加入专项及渲染入口，未声称远端运行。
- 最终开发候选为`artifacts/named-holdings-boundary-worker-20261028`；182个发行文件、84个插件信任索引文件SHA256全部一致，见`artifacts/named-holdings-boundary-hashes-20261028.json`。此前`named-holdings-worker-20261028`保留为已替换候选，状态另记`artifacts/named-holdings-candidate-status-20261028.json`，不改写不可变包。所有候选均未安装，未签生产发行签名。

这些结果不关闭配置池的真实披露/账户覆盖、独立经济所有权、正式迁移及安装后运行证据，也不关闭模型有效性与历史审核知识时间轴。

相关：[联合观察](NATIONAL_TEAM_JOINT_EXIT_STUDY_20261028.md)、[ETF报告目录](NATIONAL_TEAM_ETF_REPORT_INDEX_20261027.md)、[架构](../architecture/CURRENT_ARCHITECTURE.md)、[测试](../../tests/README.md)。
