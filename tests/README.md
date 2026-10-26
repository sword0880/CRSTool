## UI-ARCH 统一视觉回归（Windows）

行业研究采用独立测试项目。`CRS.Tests.SectorResearch`运行完整模型领域反例；`CRS.Tests.SectorUi`默认渲染明确标记的合成曲线，`--industry-installed`使用真实Worker业务管道读取已登记公布PE。国证公布PE已接通独立后台事实链和第三页导航；完整行业价格/PB/覆盖/基本面及机会门槛仍开放。命令、口径及证据见[行业研究实施](../docs/implementation/NATIONAL_TEAM_SECTOR_RESEARCH_20261025.md)和[公布PE事实链](../docs/implementation/NATIONAL_TEAM_INDUSTRY_PE_20261025.md)。

NAV来源入口专项：`CRS.Tests.NationalTeamWatch --nav-source-checks`验证固定地址、持久冷却、真实插件同步/审核/查询、提交后重放和待审不升级；HTTP均为替身。宿主预算以`CRS.Tests.WorkerEtf --source-budget`另验基金双域共享及与其他来源独立。范围见[来源接入实施](../docs/implementation/NATIONAL_TEAM_NAV_SOURCE_20261024.md)。

NAV存储新增：`CRS.Tests.NationalTeamWatch --nav-storage-checks`检查独立净值修订、知识时点、冲突/审核/幂等、完整性、重启和取消。`--nav-storage-sample <已核验NAV快照目录> <新目录>`用真实公开原件验证隔离SQLCipher入库及重启回读，使用测试密钥，不连接正式Worker。见[NAV存储实施](../docs/implementation/NATIONAL_TEAM_NAV_STORAGE_20261024.md)。

国家队研究新增：`CRS.Tests.NationalTeamWatch --nav-research-checks`运行官方NAV解析及同日份额/NAV计算反例，也纳入默认回归。`--nav-research-snapshot <原件目录> <新目录>`离线导出单只159915研究样本；`--nav-research-verify <样本目录>`按冻结时间核验原件并复算完整JSON。`--nav-share-study-live <样本目录> <开始日期> <截止日期> <新目录>`只读正式Worker的份额/证据/日历/可比性，生成RetrospectiveOnly报告；`--nav-share-study-verify <报告目录>`重新核验NAV原件并复算。缺日、待审、可比性未知时保持不可计算，不写正式市场库或通知。见[开发清单](../docs/implementation/NATIONAL_TEAM_RESEARCH_DEVELOPMENT_20261024.md)及[NAV来源约束](../docs/implementation/NATIONAL_TEAM_NAV_RESEARCH_20261024.md)。

本机配套安装留证：`CRS.Tests.NationalTeamWatch --deployment-state-live <目录>`只读保存健康、计划、ETF配置和股票记录，并要求读取前后同一Running进程。`--equity-report-sync-live <目录>`仅为600153创建一个稳定报告请求ID，文件存在时沿用；`--equity-report-status-live <目录>`只查询该ID。目录读取失败前先保存独立命令终态，超时不冒充空目录。`--p0-review-live --human-confirmed`仅用于已确认固定原件的五账户，先跑真实黄金样本；已成功账户跳过，仅正式执行证明正文前超时/冷却的终态失败才另记尝试，未知结果和业务拒绝不换ID。见[正式回执与边界](../docs/implementation/ETF_EQUITY_DEPLOYMENT_20261024.md)。

ETF/股票2026-10-24续验：`CRS.Tests.NationalTeamWatch --evidence-adversarial`为16项隔离反例，验证股票历史插入/更正不继承旧比较基线、旧页面确认拒绝、旧库缺基线保留A类快照但无差额，以及ETF原件接收/接受阶段的北京时间窗口；已加入默认回归和Windows CI。`--business-contracts`覆盖基线序列化、新绑定操作及旧服务拒绝后不降级。`--equity-ui`检查实际比较前期展示及修改报告期清除可比勾选。新增`--equity-status-live`只读正式持仓/观察池/样本目录/计划和持久历史，失败不保存完整结果；不采集、不审核、不发消息。真实数据与未关闭门槛见[续项记录](../docs/implementation/ETF_EQUITY_FOLLOWUP_20261024.md)。

```powershell
./tests/Verify-WpfDesignSystem.ps1
dotnet run --project tests/CRS.Tests.Wpf -c Release -- --shell-brand-preview artifacts/ui-preview/main
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release -- --ui-visual-preview artifacts/ui-preview/plugins
```

第一条检查 XAML 完整性、共享品牌/语义资源是否存在、旧页面硬编码颜色总数不能增长、插件不能反向引用桌面项目；现存遗留次数见 `tests/wpf-color-baseline.json`。后两条对 1040/1280/1440 宽度与 96/120/144 DPI 位图进行**合成布局**渲染。插件预览不会发起 Worker 查询，也不代表真正的数据状态或 Windows per-monitor DPI 验收。PR 的 Windows Actions 截图存档供人工复核；真实键盘、焦点、拖拽和锁屏凭证生命周期仍须实测。设计与待办见 `docs/implementation/UI_ARCH_REMEDIATION_20261021.md`。

# CRS 验证项目

2026-10-19减持预警增量：先完整构建，再运行NationalTeamWatch的 `--exit-alerts`（纯规则、加密事件/Outbox事务、进程重建、历史修订回算及真实后台入口的假渠道投递）及 `--exit-alert-ui`（真实WPF窗口和合成截图）。Background的 `--notifications` 验证公共渠道、受保护邮件配置及管理授权。完整WorkerEtf隔离包套件增加真实命名管道的预警、证据、订阅和默认停用渠道检查。这些均不连接真实收件人，不能替代真实SMTP、ETF公告黄金样本、历史回测、250日覆盖或连续20日采集验收；详见[实施记录](../docs/implementation/NATIONAL_TEAM_EXIT_ALERT_IMPLEMENTATION_20261019.md)。

2026-10-16架构整改：`./tests/Verify-ProjectGraph.ps1`核对当前架构清单、直接依赖与循环；已接入`Verify-Architecture.ps1`。`./tests/Test-ProjectGraph.ps1`验证四种结构回退均被拒绝。后台`WorkerRemediationChecks`新增真实15秒插件查询、2秒控制面SLA、插件级限额、异常隔离、取消与维护排空；完整后台测试需真实Windows管道/ACL权限并使用独立TEMP目录，见[实施记录](../docs/implementation/ARCH_REMEDIATION_20261016.md)。

Worker升级专项：先构建当前Release并发布新的HeartbeatOnly测试包；`./tests/Test-WorkerMaintenance.ps1 -CheckStartupInterop`使用隔离合成文件验证包核验、快照/原件、程序切换、禁止有新事实回退和PowerShell/C#互操作，不操作SCM。`dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --upgrade-only <全新Worker测试包目录>`使用真实SQLCipher/DPAPI与测试自有进程验证升级阶段及强杀；已接入完整后台套件。需独立TEMP目录及Windows管道测试权限，不能替代真实安装服务升级或跨机恢复验收。

插件/ETF开发预览：说明与未完成门槛见[实施记录](../docs/implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)。新增验证入口：`dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release`；`dotnet run --project tests/CRS.Tests.PluginHost -c Release -- artifacts/plugin-preview/Plugins`。双插件测试前用 `./tests/Publish-Plugins.ps1 -IncludeDemo` 生成全新发布目录。测试仅用合成研究数据和独立临时库，不把状态恢复测试称为断电验收。

桌面日常开发：编译解决方案或 `CRS.Desktop.Wpf`（Debug/Release、VS F5 均适用）会先构建国家队双入口和 Demo 插件，再由 `Build-DevelopmentPlugins.ps1` 把完整输出、清单和 SHA256 可信索引同步到桌面输出的 `Plugins/<id>/<version>`。无需手动复制 DLL，也无需运行发布/同步脚本。纯后台 Heartbeat 不进入桌面包。更新已加载 DLL 前需停止桌面调试；复制失败会使构建失败。可用 `-p:SyncDevelopmentPlugins=false` 关闭开发同步；`Publish-Plugins.ps1` 已显式关闭此步骤并继续独立生成发行开发包。新增桌面插件时需在 WPF 项目登记同样的仅构建引用；Core 等依赖随入口输出复制。此开发哈希索引不替代生产签名发行链。

覆盖率与原件导出回归已加入ETF验证：交易日历损坏不跳日、缺口分母、无日历/超界不冒充完整、待审核/拒绝分列、人工日历证据替换、同值拒绝后再提交，以及精确原字节导出、取消/损坏不覆盖目的地、私有目录保护。宿主窗口检查会实际切换所有插件页签并捕获绑定错误。

敏感导出专项：`dotnet run --project tests/CRS.Tests.Infrastructure -c Release -- --sensitive-exports-only`。应用与界面回归同时覆盖本次明文声明、恢复确认及锁定取消；生产界面不开放临时目录明文特许，合成验收明确授权其隔离临时目的地。说明见 [敏感导出](../docs/implementation/SENSITIVE_EXPORT.md)。

2026-10-13 P0 续修：应用 `AccountPeriodChecks`、基础设施 `AccountPeriodFileChecks` 与 `LockInterruptionChecks`、界面 `ImportInteractionChecks` 增加有凭证的生命周期、快照重放、严格凭证清单和长备份锁定回归。中断可单独执行 `dotnet run --project tests/CRS.Tests.Infrastructure -c Release -- --lock-interruption-only`。独立税额记录未提供，人工金额验收仍未通过。

最新整体验收：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory Sample/FUTU -IbkrRealDirectory Sample/IBKR -OutputDirectory artifacts/resource-protection-acceptance`。资源专项单独入口：`dotnet run --project tests/CRS.Tests.Infrastructure -c Release -- --resource-limits-only`。构造超过 128 MiB 的隔离 SQLCipher 库验证分块恢复；不读取或修改用户现有数据库。此前结构升级和金额展示回归继续执行，以下旧产物路径保留。

当前整体验收：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory Sample/FUTU -IbkrRealDirectory Sample/IBKR -OutputDirectory artifacts/schema-protection-acceptance`。`SchemaUpgradeChecks` 覆盖升级前加密备份、失败回滚、恢复与程序集归档；`TaxPresentationChecks`、`TaxAmountViewChecks`、`ExportChecks` 覆盖金额展示。单独运行 IBKR 技术验证：`dotnet run --project tests/CRS.Tests.Verification -c Release -- --verify-ibkr-real Sample/IBKR src/CRS.Desktop.Wpf/config artifacts/ibkr-real-acceptance`。报告仅含期间、计数、问题代码及原件摘要；独立金额签认仍待完成。

加密底稿与新目录恢复专项：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory sample/FUTU -OutputDirectory artifacts/encrypted-export-acceptance`。独立密码机制、容器与实际锁定检查、前台密码清理分别在 Security／Infrastructure／Wpf 项目；原加密备份用例仍执行以确认用途隔离兼容。

冻结专项入口：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory sample/FUTU -OutputDirectory artifacts/frozen-evidence-acceptance`。真实私有原件仅读取，报告不含身份文件名或交易金额；该技术检查不替代 `-RealManifest` 的独立金额验收。IBKR 真实样本现已加入单独技术验证。冻结与证据测试见 `CRS.Tests.Application/FrozenEvidenceChecks.cs`。

新增 CRS.Tests.Security 的密码学与 RFC6238 向量检查，以及 Infrastructure 的 SQLCipher/MFA/恢复/并发防重放专项。认证专项入口：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -OutputDirectory artifacts/auth-acceptance`。WPF 模型与窗口冒烟使用独立日志，避免 Windows 大小写文件名覆盖；人工手机及跨电脑验收见 docs/guides/AUTH_IMPLEMENTATION.md。

分层验证保持独立控制台入口，运行时失败会返回非零退出码；没有引入仅用于改目录的测试框架替换。

| 项目 | 验证内容 |
| --- | --- |
| CRS.Tests.Domain | FIFO 含费成本守恒、稳定排序与同秒歧义隔离、税额基线、领域程序集独立性 |
| CRS.Tests.Application | 取消不保存、冻结历史导出、结转门槛、复核、依赖；年度覆盖、账户对账；历史汇率与指纹隔离、无效重放输入、执行口径、跨年状态与元数据门槛 |
| CRS.Tests.Infrastructure | 旧结果 JSON、会话交易隐私、年度汇率、设置保存与日志开关、超过百份历史分页与父运行、年度汇总持久化与来源导出 |
| CRS.Tests.Wpf | 概览、忙碌门槛、输入失效、历史分页／复算／取消、跨页年度汇总选择及归属确认、富途文件独立替换与清除 |
| CRS.Tests.Verification | 原有 68 项金额、导入、状态、SQLite、NPOI、Flex 模拟和富途综合回归 |

在仓库根目录运行：

Windows Hello 专项：`dotnet run --project tests/CRS.Tests.Infrastructure -- --hello-only`。端口替身只验证策略与失败路径，真实 SQLCipher、DPAPI、加密备份仍运行。首次选择和日常 Hello 界面门槛纳入 WPF 套件；真实 PIN、指纹/人脸、取消和锁屏中断见 [Hello 验收边界](../docs/implementation/WINDOWS_HELLO.md)。

```powershell
dotnet build CRS.sln -c Release -m:1
./tests/Verify-Architecture.ps1
dotnet run --project tests/CRS.Tests.Domain -c Release --no-build
dotnet run --project tests/CRS.Tests.Application -c Release --no-build
dotnet run --project tests/CRS.Tests.Infrastructure -c Release --no-build
dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build
dotnet run --project tests/CRS.Tests.Verification -c Release --no-build
```

窗口验证使用隔离的临时数据库，不读取真实账户历史：

```powershell
dotnet run --project src/CRS.Desktop.Wpf -c Release -- --smoke-test --fixture tests/fixtures/ibkr_activity.xml
dotnet run --project src/CRS.Desktop.Wpf -c Release -- --smoke-test --futu
```

WPF 窗口检查会导航十个页面、检查绑定错误、历史 / 复核 / Excel 导出、取消不保存及输入清理。窗口检查还验证折叠菜单、设置窗口保存和最小窗口尺寸下的历史分页栏；传入 --image 的绝对 PNG 路径可输出折叠、设置、概览、导入、税务和历史截图。

`dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --compact-navigation artifacts/compact-navigation` 使用合成窗口检查分类浮层、选页关闭、当前分类/页面标识、Esc/方向键、展开态菜单树、动态入口、禁用状态及锁定清理，并输出浮层与折叠主窗体截图。不加载正式插件或连接个人保险库；分类控件执行真实 OnClick，菜单项使用自动化调用，鼠标/键盘采用路由事件，不代表物理输入回放或跨显示器验收。

携带 IBKR fixture 的窗口检查还会执行原件摘要校验、冻结口径复算另存、父运行及取消不保存验证；汇率页面检查数值调整和手动输入后读取。

该窗口流程还会勾选 IBKR 与富途来源任务、确认归属并执行年度汇总，验证另存、清空选择、导航及来源表格；截图包括年度汇总选择和结果。

WinForms 项目已删除。当前解决方案保留 WPF 前台、Domain／Application／Security／Infrastructure 四个后台项目；新增三个插件契约/宿主项目、ETF与演示插件，以及三个插件验证项目，原六个验证项目保留。

插件专项先生成全新的开发预览目录，再运行；Debug同样执行并传入对应Debug发布目录，不能混用不同配置的业务程序集：

```powershell
./tests/Publish-Plugins.ps1 -OutputDirectory artifacts/etf-recovery-preview -IncludeDemo
dotnet run --project tests/CRS.Tests.PluginHost -c Release --no-build -- artifacts/etf-recovery-preview/Plugins
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- artifacts/etf-recovery-preview/Plugins
```

PluginRecovery现验证UI只申请导航、同一双入口清单、UI程序集无Jobs/SQLite引用、实际宿主加载/布局、不创建桌面任务库/市场库、退出清理及拒绝旧调度能力。旧桌面库七中断点测试已随执行归属切换退役；Worker恢复由CRS.Tests.WorkerEtf验证，不继承旧结果作为新服务升级证明。

当前双入口包见[ETF Worker迁移](../docs/implementation/ETF_WORKER_MIGRATION.md)。PluginHost保留调度辅助类的隔离时钟回归；实际桌面会话不创建调度器。NationalTeamWatch覆盖纯格式/质量/加密仓储；WorkerEtf覆盖真实独立子进程、命名管道、关闭客户端继续采集、重启恢复及业务去重。`dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- --official-live`只读实际安装服务已取得的两市结果，不创建桌面下载器或写库连接；技术验证不替代250日历史或连续20日采集。

发布验收、合成性能与许可证清单请运行 ./tests/Run-ReleaseAcceptance.ps1 -Smoke，具体门槛见 [发布验收](../docs/validation/RELEASE_ACCEPTANCE.md)。抵免证据链与界面检查已加入 Infrastructure / WPF 验证项目。

新增 CRS.Tests.Security（纯密码学）与保险库集成／交互验证。所有持久化回归现由 TestStores 显式创建加密库，不提供明文测试兼容通道。


绩效专项：先编译，再运行 dotnet run --project tests/CRS.Tests.Infrastructure --no-build -- --performance-only；真实输入专项使用 --performance-real-only，从仓库根目录只读 Sample/IBKR 与 Sample/FUTU，仅读取结构并输出计数/问题码。领域与 WPF 套件已包含绩效，场景与缺口见[绩效 P0 验收](../docs/validation/PERFORMANCE_ACCEPTANCE.md)。

计划任务专项（2026-10-15）：PluginHost的TaskScheduleChecks使用真实Quartz和独立存储，Wpf的ScheduledTasksInteractionChecks验证计划编辑与结果；窗口冒烟额外打开任务管理和执行记录。运行中日志恢复用现场重建验证，不与升级套件的独立进程强杀混用结论。当前实现及边界见[主程序计划任务](../docs/implementation/SCHEDULED_TASKS.md)。

独立后台专项（2026-10-15，Worker v2）：先用 `Publish-BackgroundService.ps1 -OutputDirectory <不存在的新目录> -HeartbeatOnly` 发布，再运行 `dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- <该发行目录>`。65项（原29项、整改27项及权限9项）涵盖真实Quartz、SQLCipher、Windows管道、存储失败停调度、持久命令强杀恢复、插件超时与隔离、查询和控制独立容量、分页、Schema迁移及重试；Debug须单独发布并使用对应配置。需要正常完整性Windows令牌，不能使用放宽生产ACL的方式兼容低完整性沙箱。测试夹具不随正式插件发布，当前证据与待办见[Worker整改记录](../docs/implementation/WORKER_REMEDIATION.md)。

实际管理员安装后，可运行 Background 项目的 `--probe` 只读查看健康/计划/历史；`--acl-probe` 用移除管理员组的真实线程令牌验证只读权限。窗口检查：`dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --background-ui artifacts/background-service-ui/window.png`，只查询公开后台任务，不解锁税务库。安装、卸载、开发签名边界和未实现的 ETF 迁移/升级恢复范围见[后台实施记录](../docs/implementation/BACKGROUND_SERVICE.md)。

Worker权限巡检续修新增9项检查，当前Release后台总计65项。需要正常Windows令牌与创建测试符号链接的权限，仅改变隔离目录和测试自有进程。`dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --storage-acl-probe` 只读检查当前已安装服务的数据树，不调整权限；它与隔离套件的结果分开记录，见[第二批整改](../docs/implementation/WORKER_REMEDIATION.md)。

ETF服务化专项：先以默认参数发布完整Worker包，再运行 `dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- artifacts/worker-new`。默认包包含ETF，不用于仅心跳的65项宿主故障套件。完整说明见[迁移记录](../docs/implementation/ETF_WORKER_MIGRATION.md)。
## 来源策略专项（固定HTTP响应，无真实官网请求）

```powershell
dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- --source-policy
```

验证持久冷却、来源间隔、Retry-After、临时故障退避、两市超时隔离、正文20秒期限、取消语义和策略库无钥/损坏拒绝，共18项。测试临时目录可设为工作区artifacts下；不代替官网长期覆盖验收。

## ETF可编辑配置

2026-10-16：生产新库默认启用24只内置观察候选，手动停用状态在重启后保留。新增`HistoryRangeChecks`覆盖深市区间分页、逐日切分、跨页重复/截断/超界拒绝及每轮缓存清理，减少全清单历史补采请求；服务故障夹具显式限制为5只合成来源。实际启用已有目录使用`CRS.Tests.WorkerEtf --enable-reference-live`，明确提交历史采集用`--backfill-live 2026-09-01 2026-10-16`，均会修改正式服务状态。

实际安装的ETF图表只读核验：`dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- --installed-chart-probe`检查单只/全池历史和成交额真实返回码，`--installed-chart-ui`逐只选择实际ETF并截取上下图窗口；两者不保存配置、不触发采集。2026-10-16复现旧服务`UnknownQuery`并完成配套后台更新后的验证见[修复记录](../docs/implementation/ETF_CHARTS_AND_MARKET_ACTIVITY.md)。

ETF自动图表与市场温度：`CRS.Tests.NationalTeamWatch` 新增选择并发、全池同日汇总、缺口/日历、官方成交格式/单位、均值/冲突/损坏检查；可加 `--market-live` 只读核验2026-10-13官网单日并写工作区隔离证据。`CRS.Tests.WorkerEtf <完整Worker包>` 覆盖独立市场任务和真实管道，`--chart-ui <完整Worker包>` 仅跑隔离服务与图表窗口检查（包含LiveCharts延迟绘制等待）。范围与发布目录见[实现说明](../docs/implementation/ETF_CHARTS_AND_MARKET_ACTIVITY.md)。

ETF配置增量：`CRS.Tests.NationalTeamWatch` 覆盖目录初始化、元数据修改、旧修订拒绝、命令去重、超过20只启用/编辑/重启、旧触发器移除及停用保留事实；`CRS.Tests.WorkerEtf <完整Worker包>` 增加真实服务配置和WPF新增/修改、第21只启用及滚动回归。20仅指表格视口，不限制监控数量。只读核验已安装目录用 `dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- --configuration-live`。配置、排队与参考目录边界见[实施记录](../docs/implementation/ETF_CONFIGURATION.md)。

## 单次执行日志

阶段增量使用 `artifacts/worker-progress-heartbeat-v3` 跑后台故障套件、`artifacts/worker-etf-progress-v3` 跑ETF服务化套件；后者包含运行期间的来源阶段查询。新增仓储阶段8项覆盖持久化、截断、重启、过滤及501次心跳后保留业务历史，故障注入覆盖进度落盘失败；来源策略增加HTTP429及冷却阶段验证，窗口专项增加3秒自动刷新、最近进展和真实写入数量分类。`--etf-progress-probe` 只读查看实际安装的最近ETF执行，详细保留策略见[执行日志实现](../docs/implementation/WORKER_EXECUTION_LOGS.md)。

`dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --execution-log-ui artifacts/execution-log-ui/log.png` 使用合成管道验证实际行双击、模态窗口、刷新、北京时间、记录清理与会话取消。已安装服务只读查询可用 `dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --execution-probe`，不触发新任务。服务的7项新增日志检查并入原65项回归，共72项，仍需显式HeartbeatOnly隔离包。边界见[执行日志实现](../docs/implementation/WORKER_EXECUTION_LOGS.md)。

## 2026-10-16并发及维护增量

先完整Release构建，再生成新的HeartbeatOnly包。完整Background套件包含查询隔离、升级启动、权限、阶段日志及调度检查；当前数量及最终日志见[本轮整改记录](../docs/implementation/ARCH_REMEDIATION_20261016.md)，前文65/72项为历史批次。

```powershell
# 22项真实Quartz调度检查：不需要已安装服务，不请求真实来源。
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --scheduling-only
# 23项隔离文件维护检查及C#启动互操作。
./tests/Test-WorkerMaintenance.ps1 -CheckStartupInterop
# 管理员专用：唯一命名的LocalService测试服务，复用生产事务模块。
./tests/Test-WorkerScmUpgrade.ps1 -PackageDirectory artifacts/worker-new-heartbeat
```

SCM检查需要管理员令牌、创建服务和私有ACL权限，只创建`CRSWorkerUpgradeTest<GUID>`测试服务并在finally移除注册，保留隔离证据；不升级或停止正式服务。测试发行使用同一构建的两份版本清单，不能替代真实插件Schema兼容矩阵。受限环境完整管道/ACL回归须在获准的正常Windows令牌下执行，不放宽生产ACL。并发配置、会话安全并行声明及健康等待指标见[当前架构](../docs/architecture/CURRENT_ARCHITECTURE.md)。

## 设置窗口分类导航

`dotnet run --project tests/CRS.Tests.Wpf --no-build -- --settings-ui artifacts/settings-redesigned` 渲染真实窗口，验证两级树选择、分类关键词搜索/空结果、草稿保留、最小尺寸保存区和锁定凭证清理。使用隔离设置与保险库替身，不保存个人配置、不发送邮件。布局与新增分类约定见[设置导航实现](../docs/implementation/SETTINGS_NAVIGATION_20261019.md)。

## 减持预警历史研究回放（2026-10-19追加）

2026-10-20新增`--monitor-audit-live`、`--monitor-backfill-live yyyy-MM-dd yyyy-MM-dd`、`--monitor-backfill-status`和`--monitor-apply-reviewed-metadata 清单路径`，分别用于真实服务只读核验、提交官方历史补采、读取原补采命令回执/执行记录、应用已逐项核验的官方原件口径。补采提交和口径应用会写入后台；口径应用先检查外发渠道和期初/期末交易所份额，下载哈希不符立即停止，缺事实或份额范围不符保留待核查。详见[核验记录](../docs/implementation/MONITOR_READINESS_20261020.md)。完整回归及`--exit-alerts`包含12项原件范围、日历依赖、执行中SQL中断和取消恢复检查；回填不计入20日连续采集。

以下命令通过`tests/CRS.Tests.NationalTeamWatch`的Release构建运行。`--exit-alert-replay-checks`验证14项合成规则及文件复算；同时包含在完整ETF回归与`--exit-alerts`中。

```powershell
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --exit-alert-replay-checks
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --exit-alert-replay inputs.json artifacts/replay-restored
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --exit-alert-replay-installed 2025-09-30 2026-06-30 artifacts/replay-real
```

前两项不需要正式服务；第三项只读当前启用池、规则、已接受份额、日历覆盖及机构证据，要求正式服务查询可用。导出`report.md`、`report.json`、`inputs.json`；服务快照另附`evidence.json`原件哈希/来源/修订。所有回放均为事后研究且时效`NotProven`，不发信、不补采、不审核。当前池不是历史无偏样本；无数据或无可计算窗口不能被解读为未发生异常。2026-10-19用户授权清理旧后台数据后，服务及预警IPC已恢复；旧历史需重新采集和核验，不能把新库可查询视为历史回测通过。


### 2026-10-20架构续验

证据与命令见[架构整改续记](../docs/implementation/ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md)。`CRS.Tests.Infrastructure --performance-only`包含类型化手工账户/事件/净值/覆盖确认与拒绝输入无写入检查；`CRS.Tests.Wpf`包含表单映射及用例错误呈现。`ScopedEvidenceChecks`验证查询无写、巡检幂等及事务故障回滚。宿主可靠性回归使用`Publish-BackgroundService.ps1 -HeartbeatOnly`生成的隔离包；ETF服务化使用完整双入口包，不能混用无上下文宿主夹具。

监控`--monitor-summary-live`仅回读逐证券250/20日验收，保存`current-acceptance.json`。状态查询限流/繁忙只在总期限内重读，不生成补采命令，失败不覆盖最终报告。


`CRS.Tests.NationalTeamWatch --business-contracts`在隔离目录验证业务V1默认/显式、可选新字段、缺字段/重复属性、UTF-8超限、未知版本拒绝、旧服务响应、真实管道客户端错误呈现和未知市场Schema拒绝。也已纳入默认ETF回归。它不操作已安装服务；生产部署与旧发行升级矩阵另外验收。


`CRS.Tests.Background --lifecycle-only`在隔离目录验证插件四阶段超时、禁止失控后清理、健康与分派拒绝、诊断异常、迟到结果及真实子进程退出。`--lifecycle-exit-child`仅供该夹具创建的测试子进程使用，不操作SCM。该专项已纳入后台默认回归。

品牌仅需预览时，可指定完整项目`src/CRS.Desktop.Wpf/CRS.Desktop.Wpf.csproj`及参数`--smoke-test --brand-icons --image <绝对PNG路径>`，只用合成隔离保险库输出主窗口与身份验证页。不要用该预览声称全部财务/设置回归通过。


`CRS.Tests.Infrastructure --broker-source-facts`验证共享来源的确定性、实际同步回调、未知字段/位置保留、取消及结构拒绝；也包含在`--performance-only`。`--compare-ibkr-before <工作前受信Infrastructure程序集绝对路径>`用于本次变化前后技术比较，不是独立金额签认，不纳入需要旧二进制的常规CI。既有金融冻结JSON不自动更新。


### 宿主共享来源预算（ARCH-209）

`dotnet run --project tests/CRS.Tests.WorkerEtf/CRS.Tests.WorkerEtf.csproj -c Release -- --source-budget`：隔离SQLCipher与固定HTTP，验证跨插件共享、域名归并、FIFO/容量/取消、Retry-After、重启恢复、旧策略预合并、存储故障锁定、真实请求间隔及健康字段。配合既有`--source-policy`检查保留原网关行为；不访问官网，不操作安装服务。不能替代长期容量与真实升级/断电验收。


`--source-budget`还覆盖来源存储故障后的重复健康查询、失败关闭与启动校验。`dotnet run --project tests/CRS.Tests.WorkerEtf/CRS.Tests.WorkerEtf.csproj -c Release -- --source-budget-soak 90`运行90秒真实时间负载（可选60～600秒），固定120个消费者/8个插件，不访问网络；检查持续容量、来源故障、三秒间隔、排空和重启冷却。成功摘要写入`artifacts/source-budgets-soak-90s.json`，与本次退出码/日志一起使用，不能拿旧摘要冒充失败运行已通过，也不能替代跨日负载或真实升级验收。


`CRS.Tests.Infrastructure --broker-provenance`验证实际读取/投影版本、原件与数据摘要绑定、历史冻结/指纹、事务回滚、备份恢复、设置1读取/写2、旧历史签名兼容及重复导入不补旧版本；纳入`--performance-only`。不重新生成金融冻结期望值。

`CRS.Tests.Background --source-budget-process <已验证宿主开发包目录>`在隔离目录启动自有真实Worker与两个插件，通过持久命令模拟429，跟踪本次RequestId/ExecutionId完成态，再强杀/重启核验来源期限、跨插件阻断、另一来源及原密钥。需要正常Windows ACL/DPAPI/管道权限，不操作安装服务，不访问网络。它是进程崩溃验收，不是SCM、真实源站或物理断电验收。

数据库替换边界：`CRS.Tests.WorkerEtf --source-budget`现含7项存储端口替换/生命周期检查与原SQLCipher专项，合计63项；内存替身只验证策略解耦，不验证其他数据库持久性。`Verify-Architecture.ps1`禁止来源预算规则和端口暴露数据库实现，设计约束见 `docs/architecture/DATABASE_PORTABILITY.md`。

`CRS.Tests.Background --task-storage-boundary`：任务仓储真实SQLCipher事务/读取故障12项，加IO及通用数据库异常的宿主保护18项，合计30项；全部隔离运行，不需安装服务发行目录。此入口验证异常/原子性边界，不代表已支持第二数据库。

可插拔数据库：`CRS.Tests.Infrastructure --vault-providers`及`CRS.Tests.WorkerEtf --storage-providers`各10项，验证未知/重复类型拒绝、配置与自定义注册接线。替身注册转发现有SQLCipher，不是第二数据库验收。配置样例 `config/storage-providers.example.json`；默认无配置仍为SQLCipher。

ARCH-208税务版本审计：`CRS.Tests.Infrastructure --tax-provenance`验证实际IBKR来源/税务投影版本、原件/批次绑定、年度/期初报告、持久化/冻结、无原件重放、修改/剥除拒绝、旧形态未知、底稿、年度汇总及加密备份恢复；也纳入默认Infrastructure回归。版本信息随现有结果JSON保存，不新增数据库提供方依赖；不代表独立税额Golden验收。

富途税务版本：`CRS.Tests.Infrastructure --futu-tax-provenance`共16项，亦纳入默认完整回归；`--compare-futu-tax-before <旧Infrastructure.dll>`增加三种输入组合的旧构建逐字段对照，共19项。采用合成Excel/PDF/交易夹具和真实隔离SQLCipher库，旧形态保持未知，不代表独立金额Golden。
`CRS.Tests.Background --notifications` 覆盖公共邮件与钉钉/个人微信HTTP适配器：DPAPI、修订冲突、签名、纯文本、明确拒绝、未知结果、UTF8上限、管理员配置隔离，全部使用假传输，不外发。`CRS.Tests.Wpf --settings-ui <隔离截图目录>` 同时渲染钉钉/个人微信设置页，验证搜索、草稿与锁定清理。
`--broker-provenance` 另含12项冻结投影比对：真实加密历史只读、变化字段、增删、行序、缺失版本、不同原件、篡改/重复身份及取消。`CRS.Tests.WorkerEtf --source-policy` 在既有20项后运行13项旧策略库故障恢复检查，仅用隔离库和网络替身。
`CRS.Tests.Background --ipc-boundaries` 覆盖11项真实隔离管道边界；`--ipc-compatibility <包>` / `--plugin-package-compatibility <包>` 核验原宿主与新通信/插件组合。`Verify-WorkerBinaryCompatibility.ps1` 比较旧/新契约及IPC公开接口。`CRS.Tests.WorkerEtf --overview-query-plan` 验证覆盖索引、结果一致和原件损坏拒绝；`--installed-refresh-probe` 仅只读核验真实概览/配置/历史/成交统计。`CRS.Tests.Wpf --shell-brand-preview <目录>` 脱离窗口生命周期渲染主窗口品牌区，检查新单行布局的展开/收起点击、插件页面偏移、图文可见性和 1280/1040 宽度，保存两种状态的 PNG；无插件启动。真正的标题栏鼠标命中、拖拽和高 DPI 仍须在 Windows 实机验证。
`CRS.Tests.WorkerEtf --startup-ui` 验证真实插件会话启动不等待数据、页面首次刷新去重、受控慢查询超过15秒、失败提示与重试。`--ipc-boundaries`已增至12项，补充等待响应期间的调用方取消。
`CRS.Tests.PluginHost --load-probe <Plugins目录> [--host-assemblies <桌面输出目录>]` 用真实PluginRuntime校验信任清单、依赖、入口、会话和页面构造/布局；可预载桌面通信依赖以排查默认上下文冲突。使用新建隔离存储，不读取真实保险库，不显示窗口、不触发页面Loaded或后台查询，不能替代数据刷新验收。
`CRS.Tests.WorkerEtf --initial-directory`验证24只初始化目录、重建、保留用户配置和缺事实保持空值；`--startup-ui`现含9项，增加慢概览不阻塞清单和失败不清空清单。`--raw-batch`验证每次重新核验、缺失/篡改和取消；`--raw-read-profile <原件目录>`只读A/B对比旧串行和新批校验，输出数量、时间和结果一致性。`--installed-directory-probe`只读核对实际后台的内置目录缺项数。

成交日志/查询取消：`CRS.Tests.Wpf --market-log`检查旧历史缺失计数、新收到/复用计数和ETF计数兼容；`CRS.Tests.WorkerEtf --cooling-progress`验证真实插件进度进入加密任务仓储、分类计数持久化及缺失/负数拒绝。`--raw-batch`另含正在等待的异步文件读取取消、句柄释放和I/O失败不返回部分结果；`--market-query-reliability`验证真实仓储查询取消释放门槛后采集继续、成交证据扫描范围、完整巡检仍发现无关损坏以及整页只读事务不抢占写锁。全部为隔离数据，不代表正式服务未发生中断。`CRS.Tests.Background --lifecycle-only`另检查操作级诊断、耗时及URL/参数拒绝，保留原有失控退出回归。

`CRS.Tests.WorkerEtf --installed-dashboard-probe`只读调用正式主页面的整页分页查询，输出概览/历史/成交行数、总时间，并要求查询前后健康Running及同一进程；失败输出Unavailable/分类，不把失败解释成空集合。繁忙时可能超时，不代表采集已中断。

## 2026-10-23架构四项整改专项

当前Release构建后可分别运行Infrastructure的--calculation-commit、NationalTeamWatch的--read-isolation、Background的--supervised-plugins与--storage-growth。均使用合成资料和隔离目录；storage-growth会创建101000个空文件，需预留目录容量和测试时间。受限环境将TEMP/TMP指向工作区独立artifacts目录。监督专项使用真实子进程、Job Object和持久回执，不操作已安装服务。范围见[实施记录](../docs/implementation/ARCH_OPTIMIZATION_20261023.md)。

架构续项验证：CRS.Tests.NationalTeamWatch --repository-contracts 检查必需方法无默认实现与启动能力门禁；CRS.Tests.WorkerEtf --storage-providers 包含正式会话拒绝残缺提供方。应用默认回归含北京时间年度跨年边界；CRS.Tests.Background --recovery-export-persistence 使用真实 SQLCipher 和自有子进程，检查持久重放索引、故障关闭及三个阶段强杀。它不是生产导出入口，也不代表跨机恢复验收。详见[实施记录](../docs/implementation/ARCH_FOLLOWUP_20261023.md)。
行业公布PE独立检查：`dotnet run --project tests/CRS.Tests.IndustryPe -c Release -- --output artifacts/industry-pe-checks-20261025`，覆盖严格解析、加密修订/知识时点、双回执、取消、原件健康和来源持久冷却；使用合成及隔离资料，不代表正式数据或机会有效性验收。

工行真实原件隔离检查：`dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release -- --icbc-golden artifacts/national-team-equity-expansion-20261025`，要求原件摘要匹配，7项覆盖A/H股、简称、未知投资目的、重启回读及严格大文件额度。正式回执另见[股票扩展证据](../docs/implementation/NATIONAL_TEAM_EQUITY_EXPANSION_20261025.md)，隔离测试不能代替实机验收。

指数事实链本地原件检查：`dotnet run --project tests/CRS.Tests.IndexResearch -c Release -- --source artifacts/national-team-index-research-20261025/source --output artifacts/national-team-index-research-20261025/isolated`。冻结原件配合显式测试时钟、日历、密钥和合成许可，覆盖解析、金融证明、修订/双回执、取消及来源默认拒绝。`CRS.Tests.SectorUi --index <ui-replay-report.json> <截图路径>`验证11条非空曲线、受理/待审/终态、改日期取消和来源未获准按钮；`CRS.Tests.WorkerEtf --index-pipe <完整候选包> <原件目录> <不存在的输出目录>`验证完整宿主/监督进程/管道，不访问正式服务或互联网。真实原件暂不提交/上传CI；远端只构建新专项，不能将其称为黄金样本运行成功。开放门槛见[指数实施记录](../docs/implementation/NATIONAL_TEAM_INDEX_SERIES_20261025.md)。

农行原件隔离：`CRS.Tests.NationalTeamWatch --abc-golden artifacts/national-team-abc-research-20261025`验证10项固定原件、A股口径、表决权不改股数、损坏回滚和重启边界。`--abc-read-live <目录>`仅查询，`--abc-review-live <目录>`会以稳定请求逐账户正式审核，不可当成普通测试运行。`CRS.Tests.SectorUi --index-installed <起日> <止日> <截图>`仅核验正式未配置新来源许可、无指数事实时的缺口和按钮状态，隐藏空图默认零刻度；有真实已核资料后应采用相应的新验收预期。

`CRS.Tests.NationalTeamWatch --cnd-comparison-golden artifacts/national-team-comparison-research-20261025`使用逐页核对的建发两期原件和正式只读摘录，验证未进前十不补零、原文增减算术及未经可比审核不输出差额。它不新增正式前期、不伪造SSE历史JSON，也不批准真实期间交易比较，见[比较缺口](../docs/implementation/NATIONAL_TEAM_PERIOD_COMPARISON_20261025.md)。

ETF超时性能专项：`CRS.Tests.WorkerEtf --adaptive-query-pages`验证V1新旧分页、46KB缩页、同快照并发写入隔离、连续游标/总行数及异常释放；`--raw-batch`验证默认8并发的完整原件校验、取消和新损坏；`--query-cancellation`及`--market-query-reliability`沿用真实隔离管道/仓储取消与采集继续检查。`--raw-parallel-profile <公开原件目录>`仅只读比较4/8/16并发完整核验；`--query-performance-live <不存在的目录>`通过正式后台测三次整页、510300历史/证据，失败也记录健康与PID，每页记录行数/逻辑字节/耗时，未受控的操作系统缓存不称严格冷盘。两项live/profile测量不在CI运行，不读取密钥或修改金融事实。见[性能实施记录](../docs/implementation/ETF_QUERY_TIMEOUT_PERFORMANCE_20261025.md)。

`--market-query-reliability`另含应用写门槛阻塞反例：真实加密仓储写用例尚未提交时独立读取可完成，提交后新读取可见；关闭等待在途读取，取消后释放，已关闭不再接收新查询。该检查针对读取准入与生命周期，不等同正式规模的延迟承诺。

`--raw-batch`还验证同一原件库跨巡检/查询共享默认八句柄、查询等待优先及每八次让后台前进一次；取消等待不打开原件，取消在途读取释放文件及额度。`--adaptive-query-pages`包含取消快照清理等待本地在途查询超过两秒的反例；清理排队期限与网络期限分开，产品查询期限保持不变。

历史PDF独立登记：默认国家队回归包含6项无需原件的目录/拒绝合同；`CRS.Tests.NationalTeamWatch --historical-pdf-checks <真实年报PDF>`核验32项真实原件和隔离事务失败边界。`CRS.Tests.WorkerEtf --historical-command-checks`检查11项稳定请求/重开客户端/双回执边界（隔离控制器，已加入CI配置，未声称远端执行）。`--historical-pipe <完整候选包> <原PDF> <不存在的输出目录>`用独立宿主、监督进程和真实管道回放公开原件，并渲染WPF登记/回查界面；源站、正式数据不参与。`--historical-live <目录>`会显式提交真实来源登记，先保存稳定请求；`--historical-status-live <原目录>`仅沿原ID回查，不能当普通测试运行。见[口径和验收边界](../docs/implementation/NATIONAL_TEAM_HISTORICAL_PDF_20261026.md)。

`CRS.Tests.WorkerEtf --equity-read-live <SSE或SZSE> <六位代码> <不存在的目录>`只读冻结指定证券候选/完整披露/历次事件/报告目录及待处理检索，不提交任务或登记持仓；同一健康PID后才生成完整取证凭据。`CRS.Tests.NationalTeamWatch --dashboard-profile-checks`检查5项诊断结果一致、新损坏和读取中取消；`CRS.Tests.WorkerEtf --dashboard-profile-live <不存在的目录>`仅在新后台支持时采样有限阶段，失败保存Unavailable与前后健康。见[分段口径](../docs/implementation/ETF_QUERY_STAGE_PROFILE_20261026.md)，不能把单次结果当性能验收或把准入/投影混合时间叫纯磁盘/IPC耗时。

`CRS.Tests.NationalTeamWatch --dashboard-batch-checks`验证7项有界批量SQL、跨市场身份、停用/缺失/待审与取消边界。`CRS.Tests.WorkerEtf --query-performance-live-load <不存在的目录>`最多等待六分钟，实际观察国家队至少两个在途任务才采样并保存任务句柄；不提交任务制造负载，超时仍是观测未满足。

`CRS.Tests.Background --runner-diagnostic-cancel`以真实隔离子进程验证8项慢诊断、取消确认、同PID后台作业、来源额度归还及诊断熔断/停止边界；不操作正式SCM、不注入正式日志延迟。见[真实事故与修正证据](../docs/implementation/ETF_RUNNER_DIAGNOSTIC_CANCELLATION_20261026.md)。旧实现失败与新实现通过须分开保留，不能替代正式安装和在途作业核验。

`CRS.Tests.WorkerEtf --query-cancel-live <不存在的目录>`只读等待两项自然在途作业，取消本客户端尚未返回的整页查询，并在两分钟内回查原执行ID；未结束或非Succeeded都保留Unconfirmed。插件PID需另用独立进程快照核对，不能用主服务健康替代，也不把客户端取消等同于所有服务端工作已确认取消。该入口不提交或重试后台采集。

`CRS.Tests.NationalTeamWatch --gongniu-golden <原件目录>`读取固定SSE原件与发行人162页PDF做8项隔离核验；`--gongniu-read-live <目录>`只回读正式状态，`--gongniu-review-live <目录>`会按已核PDF审核两个具名账户，先保存请求并回查原句柄，仅UnknownRequest才以同ID提交。正式审核必须待支持精确来源的包Committed及独立保留核验，投资性质Unknown、期间变化null。来源及页码见[公牛披露记录](../docs/implementation/NATIONAL_TEAM_GONGNIU_DISCLOSURE_20261026.md)；普通CDN路径和官网网页未获准作为原件，官网/CDN共享冷却由source-budget/source-policy专项覆盖。

## NationalTeam 分层重构续项（2026-10-26）

先构建，再运行 `--bounded-content`（8项容量/取消负例）、`--common-time`（公共时间语义）及 `--etf-loading`；完整NationalTeam默认回归和 `--exit-alerts` 覆盖持久存储、回滚、重启、修订与投递。`Verify-NationalTeamFeatureLayout.ps1` 检查按功能端口及独立持股/预警模块，禁止旧宽接口和SQL回流。环境和最终结果见[重构记录](../docs/implementation/NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)。

`CRS.Tests.NationalTeamWatch --acceptance-batch-checks`在真实隔离加密库验证超过128证券的批量验收、同次原件去重、跨请求重新核验、跨市场同代码、最新阶段/独立尝试、额外阶段原件、取消及损坏失败关闭。诊断JSON保存在该次隔离目录并打印路径；不使用磁盘耗时断言替代正式性能。`--exit-alerts`另经真实插件入口验证`acceptance-profile`与正常验收结果一致且不投递通知。两者不操作已安装Worker，也不证明正式总览超时已关闭。


## 国家队公开研究存储与连接所有权

`CRS.Tests.NationalTeamWatch --public-storage`验证普通SQLite文件/快照、无密码读取、只读写入拒绝、连接释放、旧加密库保留式离线导出、错误密钥/占用/已有目标拒绝、来源退避保留以及真实隔离子进程崩溃遗留WAL的恢复。`--public-storage-wal-fixture`仅供该专项创建并强制结束合成子进程，不连接正式服务。默认回归及CI包含专项。原市场库测试连接已同步去除密码；收入/报税保险库及Worker管理存储测试保持原加密边界。完整实现及迁移发布限制见[ETF存储拆分](../docs/implementation/NATIONAL_TEAM_ETF_STORAGE_BOUNDARY_20261026.md)。
