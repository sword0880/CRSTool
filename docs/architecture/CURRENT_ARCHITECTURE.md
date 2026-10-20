# 当前架构入口

状态：Current（2026-10-20 Windows Release构建与依赖门禁已复核，生产发布门槛另行验收）；源码结构核对日期：2026-10-20。本文描述已接线的结构；设计提案和旧实施记录不能替代本文及源码。新增能力先核对本文、项目文件及启动入口，再参考旧方案。

## 解决方案生产项目与直接项目依赖

| 项目 | 直接项目依赖 | 职责 |
| --- | --- | --- |
| CRS.Domain | 无 | 交易事实、FIFO、税务及绩效规则 |
| CRS.Application | CRS.Domain | 用例、端口、冻结结果、DesktopWorkflow |
| CRS.Security | 无 | 密码学、认证和保险库机制 |
| CRS.Infrastructure | CRS.Domain, CRS.Application, CRS.Security | 券商解析、持久化、汇率、导出、装配 |
| CRS.Desktop.Wpf | CRS.Application, CRS.Infrastructure, CRS.PluginHost, CRS.Worker.Ipc | 桌面进程，具体基础设施仅由 Composition 装配 |
| CRS.Plugin.Abstractions | 无 | 桌面插件的 BCL 契约 |
| CRS.Plugin.Wpf | CRS.Plugin.Abstractions | WPF 页面贡献契约 |
| CRS.PluginHost | CRS.Plugin.Wpf | 桌面可信包、页面及生命周期 |
| CRS.Worker.Abstractions | 无 | 后台会话、任务、查询及存储上下文契约 |
| CRS.Worker.PluginHost | CRS.Worker.Abstractions | 后台包验证及会话加载 |
| CRS.Worker.Ipc | CRS.Worker.Abstractions | Named Pipes、Windows 身份授权及请求协议 |
| CRS.Notifications | 无 | 公共多渠道通知契约、渠道注册表与邮件适配器；不依赖业务插件或Worker |
| CRS.Worker.Service | CRS.Worker.PluginHost, CRS.Worker.Ipc, CRS.Notifications | Windows Service、唯一 Quartz 调度和加密任务库；仅装配和授权调用公共通知组件 |
| CRS.Plugins.Demo | CRS.Plugin.Wpf | 桌面页面验证插件 |
| CRS.Plugins.Heartbeat | CRS.Worker.Abstractions | 后台心跳插件 |
| CRS.Plugins.NationalTeam.Core | 无 | ETF 领域与只读契约文件实体归属 Core；无跨项目源码链接 |
| CRS.Plugins.NationalTeam.Jobs | CRS.Plugins.NationalTeam.Core, CRS.Worker.Abstractions, CRS.Plugin.Abstractions | 后台采集、市场库、来源策略及查询 |
| CRS.Plugins.NationalTeamWatch | CRS.Plugins.NationalTeam.Core, CRS.Worker.Ipc, CRS.Plugin.Wpf | ETF 桌面展示，经 IPC 读取及提交管理命令 |

## 解决方案测试与夹具项目

以下清单与 `CRS.sln` 一起由 `tests/Verify-Architecture.ps1` 核验，新增/删除项目必须同步更新本文。

| 项目 | 用途 |
| --- | --- |
| CRS.Tests.Domain | 领域和复杂度门禁 |
| CRS.Tests.Application | 用例和历史复算 |
| CRS.Tests.Infrastructure | 适配器和持久化 |
| CRS.Tests.Security | 密码学与认证 |
| CRS.Tests.Wpf | 绑定、交互和窗口 |
| CRS.Tests.Verification | 样本及发布验证 |
| CRS.Tests.PluginHost | 桌面插件宿主 |
| CRS.Tests.PluginRecovery | 插件升级恢复 |
| CRS.Tests.NationalTeamWatch | ETF 业务与来源 |
| CRS.Tests.Background | Worker、管道、权限和调度 |
| CRS.Tests.WorkerEtf | ETF 双入口及后台存储 |
| CRS.Worker.FixturePlugin | 后台故障与慢查询夹具，不进入发行包 |
| CRS.Worker.EtfFixture | 隔离 ETF 服务夹具 |

## 进程与数据边界

```mermaid
flowchart LR
    UI[CRS.Desktop.Wpf / App.xaml.cs] --> Workflow[DesktopWorkflow / Application]
    Workflow --> Private[个人税务库 / 用户保险库密钥]
    UI --> Views[桌面 PluginHost / UI Session]
    Views --> IPC[Worker.Ipc / Windows 身份认证]
    UI --> IPC
    IPC --> Service[CRS.Worker.Service / Program.cs]
    Service --> Quartz[Quartz / RAMJobStore]
    Service --> Tasks[SQLCipher 任务库]
    Quartz --> Jobs[后台 PluginHost / Jobs Session]
    Jobs --> Market[独立 SQLCipher 市场库 / 原始来源证据]
```

WPF 负责展示与交互；Views/ViewModels/Controls 通过应用入口调用，不能解析文件、计算金额或直接连接数据库。后台没有 WPF、私人税务或保险库依赖。国家队领域模型与公共查询 DTO 实体位于 Core；采集、应用写用例、市场仓储及证据完整性作业位于 Jobs；UI 目录仅保留页面、ViewModel 与 IPC 客户端。桌面旧 Quartz 实现与桌面计划契约已从本整改分支移除，Quartz Cron 表达式编辑器仅负责解析和预览。WPF 退出不停止已安装服务。主程序没有静态引用业务插件；UI/Core/Jobs 的运行会话与容器不跨进程共享。

通知经独立公共组件CRS.Notifications的渠道契约与注册表发送；Worker组合根注册邮件适配器，插件只得到IBackgroundNotificationContext，不取得提供商凭据或收件目标。系统邮件设置经DesktopWorkflow和管理IPC配置。预警事件、输入快照、业务Outbox及提交历史留在国家队插件的同一加密市场库，事件与Outbox同事务；既有Quartz每5分钟驱动恢复评价和有界投递；独立 evidence-integrity 后台任务协调原件损坏/恢复状态（只读查询核验发现损坏立即拒绝返回，不持久化变更），采集/审核成功也触发评价，不另建调度器、数据库或服务。金融预警仍受真实数据门槛阻断，数据核查独立展示。详情见[通知边界](NOTIFICATION_CHANNELS.md)及[实施验收](../implementation/NATIONAL_TEAM_EXIT_ALERT_IMPLEMENTATION_20261019.md)。

正式 Worker 由 `src/CRS.Worker.Service/Program.cs` 固定组合，数据位于 `%ProgramData%/CRS/Background`，独占 `owner.lock`；服务 SID 和管理员 ACL 限制访问。调度库使用机器 DPAPI 主密钥，插件使用 HKDF 按插件 ID 派生的独立密钥与私有目录。个人税务库采用独立用户密钥域，默认 CRS/data。DPAPI 文件与数据库拷贝尚不构成跨机恢复方案。

Worker维护由`tools/background/Upgrade-Service.ps1`、`Recover-Service.ps1`及共用的`WorkerUpgradeTransaction.psm1`/`WorkerMaintenance.psm1`协调，另持maintenance.lock防止多个维护者争用；快照留在原私有数据树。ServiceWorker在Generic Host接入SCM后、打开密钥和仓储前执行`WorkerUpgradeJournal`，只接受已确认代次或经过备份校验的Switched候选。候选先持久标记可能有新事实，再打开数据；未提交即崩溃的候选不会自动再次打开数据。已结束事务的启动失败可显式ResumeConfirmed，必须匹配已确认发行指纹。详见[实际流程与剩余目标](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)。

## IPC 与插件生命周期

协议真相位于 `src/CRS.Worker.Ipc`：只读和管理管道分离；读取经过 Windows 身份认证，修改要求管理员。版本不兼容、未知命令和不支持的查询必须显式失败。管理命令使用持久请求 ID 和修订号；响应不确定时重发原请求。Jobs/History 为有界游标分页，历史筛选在分页之前执行。

后台加载顺序：管理员控制的信任索引 → 完整包文件哈希/契约/依赖核验 → 加载入口 → Start → 独立注册查询会话 → DescribeJobs → 注册持久计划 → Quartz。纯查询插件允许 DescribeJobs 为空。单包初始化失败隔离；不响应取消的插件由正式宿主终止进程保护存储。停止先撤销请求、进入维护，取消并排空查询和任务，再释放会话、连接和密钥。插件查询不持 Worker 全局锁，同插件仅允许一个在途查询，超额返回 PluginBusy；普通查询异常不冒充任务仓储全局故障。

当前插件是**受信任同进程代码，不是沙箱**。ALC 和哈希不限制操作系统权限。生产安装器要求外部指定发布者并验证 CMS 发行签名；开发包需显式豁免。运行时信任索引依赖管理员保护，尚未形成完整的签名轮换及第三方隔离体系。任何第三方支持须另行评审。

## 后台并发与等待预算

Quartz仍是唯一执行生命周期所有者。`BackgroundRuntime.Scheduling.cs`在短管理门槛内用`Wait(0)`尝试插件额度，不能执行的作业立即释放Quartz名额，进入每插件有界FIFO；额度释放后唤醒队首，不启动脱离Quartz管理的工作。相同任务只留一个等待项，自动重复触发合并；自动任务超额暂不执行，手动请求在持久化前超额返回PluginBusy。已接收手动命令保持持久幂等和重启恢复，暂停/取消清除对应等待项。

全局并发来自私有数据目录的`worker-settings.json`，缺省固定为8；显式MaxConcurrency支持1–32，显式设为0时才按CPU逻辑处理器数取4–16。MaxPendingPerPlugin缺省16，支持1–32。管理员修改后重启生效，例如`{"MaxConcurrency":8,"MaxPendingPerPlugin":16}`；文件不随不可变程序包覆盖，未知字段/非法值拒绝启动。这里的数值是并行作业额度，并非专属物理线程数量。

旧插件会话默认串行；线程安全会话可实现`IConcurrentBackgroundSession`声明1–32并发，实际额度不超过全局并发的一半（至少1），给其他插件保留容量。相同Job仍不重入。健康响应增加可选Scheduling，报告全局运行/宿主等待、持久待执行手动命令、插件额度与运行/等待数、合并和容量延后计数；宿主等待数不包含Quartz内部尚未分派的触发器。协议版本仍为2。跨插件共享来源预算和持续超载验收仍待完成，不能承诺任意负载零积压。

## 系统设置展示边界

系统设置采用分组树目录、分类关键词搜索、当前位置及独立滚动页，分类集中登记在 `SettingsViewModel.Navigation.cs`。页面切换保留草稿，锁定/关闭清空临时凭证。分类导航不承接持久化、认证或通知发送；数据库、日志、保险库与公共通知仍沿既有应用端口处理。扩展约定及真实窗口验证见[设置分类导航](../implementation/SETTINGS_NAVIGATION_20261019.md)。

## 自动检查和仍未关闭项

`tests/Verify-Architecture.ps1` 核验项目存在性、循环依赖、项目清单一致性、领域及前台边界、后台无私人税务/WPF依赖、宿主无业务插件静态引用。完整构建后执行门禁，参见 `tests/README.md`。

升级事务、跨机灾备、历史完整执行环境、官方交易日历及长期来源验收、多插件公平资源预算尚待闭环，详见 [本轮整改记录](../implementation/ARCH_REMEDIATION_20261016.md)。独立税务金额验收 ARCH-001 按本轮要求最后处理；现有回归测试不能替代真实账户独立签认。

## 2026-10-20手工绩效输入边界

账户、投资事件、净值和完整性确认通过 `IPerformanceUseCases` 的类型化用例提交；Application构造业务对象、校验来源/完整性/金额并复用归档路径，WPF只映射表单。删除已无生产调用的旧桌面任务页面，正式计划继续只由Worker拥有。验证范围和部署状态见[架构整改续记](../implementation/ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md)。


NationalTeam插件业务DTO现由Core的`EtfBusinessProtocol`独立管理V1版本；未知版本明确拒绝。Host IPC 2、加载契约2.0及市场持久Schema2仍独立，兼容范围见[业务契约](NATIONALTEAM_BUSINESS_CONTRACT.md)。


可信后台插件超出协作取消收尾期后，Catalog进入需要进程重启的终止状态；不再分派或释放仍被使用的会话。Worker健康返回PluginUnresponsive并拒绝新工作，Service继续使用既有退出码2策略。生命周期日志带插件ID/阶段和异常分类，不写异常消息；此机制不构成进程隔离。详见[ARCH-207续记](../implementation/ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md)。


IBKR税务/绩效/现金对账/账号展示通过统一`IbkrSourceReader`消费Application不可变`BrokerSourceDocument`；证券流水同步绩效传递实际来源快照，不重复解析。投影与算法仍独立；绩效导入及新历史已保存读取/投影版本，设置JSON写2、读1，税务独立历史版本和完整类型化投资事实尚未关闭，见[共享来源事实](BROKER_SOURCE_FACTS.md)。


## ARCH-209宿主共享来源预算（2026-10-20）

Worker组合根唯一创建来源注册表，通过可选BCL端口绑定插件身份。正式网关已接入，SSE/SZSE/已审核发行人分别共享并发、频率、FIFO和持久退避；会话启动预合并旧策略，新增SQLCipher库与只读健康字段。详见[共享预算边界](SHARED_SOURCE_BUDGETS.md)。本次未更新已安装服务；长期负载和正式恢复验收继续开放。


ARCH-209后续：新增来源状态启动前校验；已初始化库缺表、损坏日期、失败计数越界及未知版本拒绝启动。来源存储异常后，Worker健康仍返回可读StorageFault，来源快照为null，不返回空来源或Ready；新增执行与插件查询继续失败关闭。库版本仍为1，本次不更改金融Schema、密钥域或服务运行标识。有限持续负载入口与最终证据见[共享来源预算](SHARED_SOURCE_BUDGETS.md)及[续记](../implementation/ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md)，跨日与实际部署门槛仍开放。

## 数据库替换约束

业务层通过仓储端口操作业务对象；连接、事务、SQL方言、迁移与加密备份留在具体存储实现。共享来源预算已以ISourceBudgetStore注入，Program装配SqliteSourceBudgetStore，预算规则不再创建数据库。当前仍为SQLite/SQLCipher，其他模块的剩余耦合与迁移验收见[数据库抽象与替换边界](DATABASE_PORTABILITY.md)。

Worker任务仓储的运行期SQLite异常现由适配器转为IO故障；BackgroundRuntime不再引用具体SQLite类型，插件遗留异常暂以通用DbException兼容。ITaskRepository明确原子提交与游标边界，数据库迁移仍按上述规范独立验收。


## 保险库与插件存储族选择

VaultManager现为IVaultStorageBackend转发入口，原SQLCipher会话/认证/备份实现归Storage/SqlCipher。DesktopOperations依赖存储族端口；国家队后台入口通过NationalTeamStorageProviders选择市场仓储及ISourceRequestPolicy，网关不再依赖SourceRequestPolicy具体类。桌面和Worker分别读取其运行目录config/storage-providers.json的Vault/NationalTeam字段；仅内置sqlcipher，未注册类型拒绝，不自动迁移或热切换。完整注册、配置及验收边界见[数据库可插拔规范](DATABASE_PORTABILITY.md)。未新增项目或反向项目依赖。

ARCH-208税务审计补强：IBKR新年度/期初报告的TaxImportProvenance随结果在现有仓储原子持久化并受整份结果封存保护；旧记录保持未知，重放与年度汇总沿用历史依据，底稿列出解析版本。此项不改税务/FIFO/汇率规则，富途税务三类来源版本现已接入，完整类型化事实、跨版本差异及独立金额验收仍开放。

Worker源码目录按职责收拢：运行规则在Runtime，来源预算在Sources，仓储接口在Persistence、SQLCipher实现位于Persistence/SqlCipher；宿主上下文、安全、通知、日志及升级分别归Hosting/Security/Notifications/Diagnostics/Deployment。目录调整不改变命名空间或运行标识，详见[目录说明](../../src/CRS.Worker.Service/README.md)。
2026-10-20：公共通知新增钉钉加签机器人及个人微信PushPlus适配器，经管理员IPC配置，WPF仅保留表单；见 [通知渠道边界](NOTIFICATION_CHANNELS.md)。配置默认关闭，尚无真实通道送达验收。
