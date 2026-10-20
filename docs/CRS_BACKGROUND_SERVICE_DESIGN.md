# CRS 后台任务服务架构设计：Windows Service + Quartz.NET

> 实施进度（2026-10-15）：[ETF已迁入Worker](implementation/ETF_WORKER_MIGRATION.md)，双入口包、独占加密市场库与只读桌面已接线；测试环境不迁移旧研究库。完整服务升级恢复、官方完整日历和长期数据验收仍开放，以下一期目标不是全部已完成的声明。

> 版本：**v1.0**｜日期：**2026-10-15**｜状态：**架构决定已冻结，实施/安装/网站数据源仍需POC验收**  
> ADR：**ADR-BG-001**｜适用范围：CRS全部需要无人值守执行的第一方可信插件  
> 上层规范：[CRS_PLUGIN_ARCHITECTURE.md](./CRS_PLUGIN_ARCHITECTURE.md)  
> 业务实例：[NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md](./NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md)  
> 技术基线：Windows 10/11；C# / .NET 10；WPF；Quartz.NET（实施时锁定兼容版本）；独立SQLCipher（任务与市场库）

## 1. 决策、目标与非目标

**决策**：采用可独立安装的 `CRS.Worker.Service` Windows服务，内嵌唯一Quartz Scheduler、任务插件宿主和本机Named Pipes管理端点。**数据库加密遵循现有CRS强制SQLCipher规范：首期使用Quartz RAMJobStore + 独立SQLCipher加密任务定义/检查点；服务启动后重建触发器。只有经过端到端验证的加密Quartz AdoJobStore才可作为后续可选升级。**首次安装服务需管理员权限；服务设置为自动/延迟自动启动。即使没人登录Windows、WPF关闭或TOTP未解锁，也可执行仅处理**公开数据**的计划任务。

**一期必须实现**：可靠安装与卸载；单实例；加密持久Cron定义与重启恢复；按交易日扫描漏采与幂等补偿；受信后台插件加载；任务运行历史；错误退避；WPF状态查询/手动触发（经Windows身份授权）；服务升级和数据恢复；不接触个人税务数据库。

**一期明确不做**：插件市场、任意第三方后台DLL、集群、多机同步、高可用SLA、后台读取个人账户或税务加密库、将Windows服务错误用MessageBox显示、长驻HTTP Web API端口、使用Quartz处理每一次业务级重试的全部细节。

## 2. 运行/依赖拓扑（宿主级约束）

```text
Windows Session 1+（用户桌面）                   Windows Session 0（服务，无UI）
┌───────────────────────────────┐               ┌────────────────────────────────┐
│ CRS.Desktop.Wpf                │               │ CRS.Worker.Service          │
│ 现有税务/绩效/TOTP/导航         │── Named Pipes─>│ .NET Generic Host/SCM生命周期    │
│ CRS.PluginHost（UI）            │<─授权协议/DTO──│ CRS.Worker.PluginHost       │
│ NationalTeam.Ui（WPF）          │               │ Quartz.NET（唯一Scheduler）      │
└───────────────────────────────┘               │ NationalTeam.Jobs（无WPF）       │
                                                │ NationalTeam.Core/Data           │
                                                └──────┬───────────────┬─────────┘
                                                       │               │
                                                ProgramData        官方数据来源
                                             scheduler-config.db/market.db SSE/SZSE/公告
                                               raw/日志/检查点

禁用：Service -> WPF/CRS.Security/个人SQLCipher/TOTP/税务Infrastructure。
禁用：WPF -> Quartz JobStore/Service端数据库写连接/后台任务实现DLL。
```

### 2.1 项目增量清单

| 项目 | 目标框架与责任 | 约束 |
|---|---|---|
| `CRS.Worker.Abstractions` | `net10.0`：后台插件入口/任务声明、DTO、状态定义 | 不依赖WPF、Quartz实现与税务Domain |
| `CRS.Worker.PluginHost` | `net10.0`：后台入口发现、签名和版本检查、会话生命周期、DI与JobDispatcher | 不静态引用任何业务插件实现 |
| `CRS.Worker.Service` | `net10.0-windows`：Windows服务宿主、Quartz、日志、SQLite JobStore、任务命令执行 | 不引用Presentation/WPF；`AddWindowsService` |
| `CRS.Worker.Ipc` | `net10.0`：协议DTO、Named Pipes客户端/服务端适配、Windows授权 | 可拆为Contracts+Client/Server；不可引入业务依赖 |
| `CRS.Plugins.NationalTeam.Core` | `net10.0`：ETF/机构模型、时间轴、数据计算 | 桌面UI/服务端复用纯逻辑 |
| `CRS.Plugins.NationalTeam.Jobs` | `net10.0`：SSE/SZSE采集、质量处理、披露任务、后台入口 | 仅由Service加载；依赖Core/Data，不依赖WPF |
| `CRS.Plugins.NationalTeam.Ui` | `net10.0-windows`：页面和UI入口 | 仅由Desktop加载；通过IPC读取状态和结果 |

`NationalTeam.Core/Jobs/Ui`属于**同一个PluginId、同一个可信发行包**，不是三个独立可安装插件。可合并Data层以避免工程过度拆分，但不得让后台作业组件传递依赖到WPF程序集。现有CRS税务/绩效P0保持原样。

### 2.2 插件清单增加双入口（结构示意，尚未实施）

```json
{
  "id": "crs.national-team",
  "version": "1.0.0",
  "hostContract": "1.0",
  "uiEntry": {
    "assembly": "CRS.Plugins.NationalTeam.Ui.dll",
    "type": "Crs.Plugins.NationalTeam.Ui.NationalTeamUiEntry"
  },
  "backgroundEntry": {
    "assembly": "CRS.Plugins.NationalTeam.Jobs.dll",
    "type": "Crs.Plugins.NationalTeam.Jobs.NationalTeamBackgroundEntry"
  },
  "capabilities": ["ui", "public-data-jobs", "official-http", "public-data-storage"]
}
```

双宿主都要从由可信发行链绑定的完整文件清单验签/验哈希，**不能单靠manifest中自带哈希自证可信**。UI-only插件可不声明`backgroundEntry`，Headless-only插件可不声明`uiEntry`。两个宿主共享不可变发行包，但各自校验、启动并管理Session。

## 3. 后台插件契约和Quartz的边界

```csharp
// CRS.Worker.Abstractions：示意契约，具体名称与类型在POC中冻结
public interface IBackgroundPluginEntry
{
    IBackgroundPluginSession CreateSession(IBackgroundHostContext context);
}

public interface IBackgroundPluginSession : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    IReadOnlyList<BackgroundJobDescriptor> DescribeJobs();
}

public sealed record BackgroundJobDescriptor(
    string JobId,
    string DefaultCron,
    string TimeZoneId,
    bool EnabledByDefault);

public interface IBackgroundHostContext
{
    string PluginId { get; }
    IPluginStoragePaths Paths { get; }
    IPluginDiagnostics Diagnostics { get; }
    IOfficialHttpGateway Http { get; }
}
```

- **插件只声明业务Job及处理实现**：服务持有Quartz `IScheduler`，业务插件不得接触宿主根ServiceCollection、不得拥有独立Scheduler实例；Quartz与业务适配通过`PluginJobDispatcher`（或可验证的持久Job工厂）实现。
- **初期以SQLCipher `scheduler-config.db`保存稳定`PluginId/JobId/cron/timezone/enabled/version`并在服务启动后重建Quartz JobDetail/Trigger**；不能把插件实现类型、`IServiceProvider`或复杂对象序列化进任何调度存储。若以后启用Quartz持久AdoJobStore，也只能保存稳定字符串参数并经过SQLCipher加密兼容验证。
- `JobKey`稳定命名`PluginId:JobId`，同一Job按Quartz支持的非并发执行策略和业务唯一键串行化；不同插件可以按全局资源预算有限并发。代码和配置中须明确时区、DST、Misfire策略及取消语义。
- 业务作业内部接受`CancellationToken`，并保存`(provider,ETF,tradeDate,operation)`检查点、输入哈希与修订版本，采用**至少一次运行 + 幂等写入**；不承诺Exactly Once。

### 3.1 Quartz存储选择：加密规范优先（P0安全门槛）

**首期推荐：`RAMJobStore + SQLCipher持久任务定义`**。需要区分：Quartz引擎提供Cron触发；持久的是我们自己的`TaskDefinition`（作业标识、Cron、时区、启用状态、修订号）和业务检查点。Windows服务启动后从加密库读取已批准任务定义，验证插件是否存在/版本兼容，再使用稳定`JobKey`重建Quartz触发器，并扫描业务缺口执行有限补偿。这是单机少量任务的可行方案，**但不能宣传为Quartz自身的AdoJobStore持久化**。

```csharp
// 接近官方Quartz Hosted Service API的示意；实施按锁定版本编译
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "CRSBackgroundService");

builder.Services.AddQuartz(q =>
{
    q.UseInMemoryStore(); // Quartz内存触发器；SQLCipher保存真正的TaskDefinition
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

// 另注册：SQLCipher TaskDefinitionRepository、启动时Quartz Reconciler、
// 可信插件宿主、业务Checkpoint/幂等、Named Pipes和健康监控。
await builder.Build().RunAsync();
```

调度配置变更流程：先在SQLCipher中事务提交新`TaskDefinition`修订号，再更新Quartz运行态；若运行态更新失败，标记`NeedsReconcile`，服务下一轮或重启后以加密库为准重新应用。**不得**把Cron同时持久修改到两个权威数据库。关闭、崩溃或断电后Quartz触发器本身丢失，但服务恢复时重建，并通过业务缺口扫描弥补关机期间未执行的日期。

**备选：Quartz `AdoJobStore + SQLite/SQLCipher`**。Quartz官方提供SQLite持久化驱动，但这**不等于它开箱支持SQLCipher加密**。若选此路线，必须先证明每个由Quartz创建的连接都正确设置密钥、Schema升级与备份受保护、服务账号可解密、WAL与密钥迁移正确，且无明文`.db`落盘；否则不得上线。也可以将任务定义迁移回推荐路线，不强制引入SQL Server。

实际`market.db`和`scheduler-config.db`统一采用服务独立SQLCipher密钥（非个人税务主密钥）。如项目最终批准公开/非敏感调度数据使用非加密SQLite例外，须另立安全ADR；**本版未批准该例外，也不接受将已有明文SQLite迁移为受支持格式**。Quartz版本须按`.NET 10 + Windows Service`测试锁定，不混用3.x/4.x API。

### 3.2 调度、误点火与补采不是一回事

| 层 | 责任 | 不负责 |
|---|---|---|
| Windows SCM | 开机启动、启动类型、服务恢复、运行身份 | 交易日/ETF缺口判断 |
| Quartz RAMJobStore | 当前进程内Cron触发、Misfire策略、任务池 | 持久化任务定义、关机期间逐条重放触发 |
| SQLCipher调度仓库 | 持久化Cron/启用状态/版本、启动时重建Quartz触发器 | 在运行时形成第二个Quartz实例 |
| 插件业务调度器 | 基于日历/证据/Checkpoint扫描缺口并补采 | 再维护一份冲突的Cron真相源 |
| 存储层 | 不可变证据、接受修订、幂等事务与执行日志 | 解释市场资金归因 |

正常日由Quartz触发；重启后先读取上一完成状态，并按最近N个交易日缺口做**有限速率**补偿。关机/睡眠期间不执行任务，不能宣称24小时无间断服务。429/403/验证码阻断后停止受限源的自动请求，按政策审核/官方文件兜底，而不是切换IP规避限制。

### 3.3 服务实例唯一性

SCM只启动已注册服务，但开发控制台模式、手工双击或误装两个不同服务仍可能形成双实例。Service需基于固定安装实例ID取得跨进程独占运行锁/系统Mutex，并用数据库身份校验防止多个进程同时从同一加密任务定义恢复并执行。单实例锁仍必须存在，防止两个进程从同一加密任务仓库恢复并重复执行业务。若采用备选SQLite AdoJobStore，**也不允许Quartz clustering**。启动发现另一实例时直接拒绝，而不是尝试共享库。

## 4. Windows服务账户、文件存储与安全

### 4.1 推荐物理目录

```text
%ProgramFiles%\CRS\
  CRS.Desktop.Wpf.exe
  Background\CRS.Worker.Service.exe
  Plugins\crs.national-team\1.0.0\{Core,Jobs,Ui,...}

%ProgramData%\CRS\
  Background\scheduler-config.db  # SQLCipher加密配置/执行记录
  Background\logs\
  Background\upgrade-journal.json
  Plugins\crs.national-team\market.db # SQLCipher加密市场数据
  Plugins\crs.national-team\raw\
  Plugins\crs.national-team\checkpoints\
  Plugins\crs.national-team\backup\
```

普通用户对`Program Files`可执行目录**仅只读和执行**，不能修改DLL/manifest；服务数据目录由安装器创建并授予服务身份和管理员必要权限。WPF不可直接写服务数据；查询经IPC返回适量已校验DTO，服务不可用时WPF只显示自己之前的**公开信息只读缓存**并注明时间戳。TOTP密钥和个人税务数据库继续由原安全模块维护，不复制到`ProgramData`。

### 4.2 服务身份选择

**优先评估**：`NT AUTHORITY\LocalService`，使用低权限网络访问公开交易所HTTPS；需在企业代理、VPN或认证网络环境下验证它是否真能连接交易所。LocalService无法继承桌面用户的PAC、代理凭据及用户证书，必要时由管理员配置受控服务代理/网络权限；不能默认为换成高权限LocalSystem。若必须使用专用服务账户，应书面记录其目录ACL、网络访问、密钥迁移和卸载权限。

独立服务SQLCipher（强制）的密钥在服务身份下创建，使用适当的DPAPI作用域或系统保护机制密封；用户CurrentUser密文**不能假设LocalService可以解封**。更换服务账号时必须走迁移/重封装，不允许静默生成新密钥造成历史库打不开。本版不为明文SQLite提供通行例外；旧明文数据库不支持直接接入，如确实出现则依照CRS既有策略从可信公开来源重新建立加密库，不做隐式迁移。

### 4.3 Named Pipes权限（Session 0↔用户会话）

- 服务端声明固定命名的本机Pipe，使用Windows安全描述符显式限制接入SID/组，拒绝不需要的网络访问；不使用默认宽松ACL。
- 命令分级：`GetHealth/GetJobs/GetHistory/GetSummary`只读；`RunNow/Pause/Resume/ChangeSchedule`需管理员或明确授权的本机操作组；`Install/Uninstall/ChangeServiceAccount`**不通过IPC**，只经管理员安装器/OS服务管理。
- 不信任客户端传入`IsAuthenticated`、`TOTPValid`、`IsAdmin`字段，按Windows实际连接令牌/ACL、命令白名单和参数校验决定；TOTP通过仅表示WPF应用层登录成功，并非服务端已验证身份。
- DTO包含`ProtocolVersion`、`RequestId`、`Command`和审计字段；版本不兼容时可只读降级或提示升级。应防止请求重放、任意文件路径和任意类型名/程序集加载命令。
- 不开放公共HTTP监听端口，避免本机桌面工具无必要的网络攻击面。

## 5. 安装、启动、升级和卸载

### 5.1 首期发行

- 服务随CRS官方安装包分发；安装器在管理员同意后复制受信发行文件、创建数据目录、设置ACL、校验`.NET`运行方式、注册SCM服务（建议`Delayed Automatic`）及恢复策略。
- 开发阶段允许使用以下命令**作为人工管理员验证样例**。其中路径应按实际发布产物调整；生产安装器必须按平台安全策略正确处理引号、账户、ACL、事件日志源和回滚：

```powershell
# 在提升权限的PowerShell中运行，示例服务二进制已存在
sc.exe create CRSBackgroundService binPath= '"C:\Program Files\CRS\Background\CRS.Worker.Service.exe"' start= delayed-auto obj= 'NT AUTHORITY\LocalService' DisplayName= '小财牛后台服务'
sc.exe description CRSBackgroundService 'CRS plugin background scheduler and public market data monitor'
sc.exe sidtype CRSBackgroundService unrestricted
sc.exe failure CRSBackgroundService reset= 86400 actions= restart/60000/restart/300000/none/0
sc.exe start CRSBackgroundService
sc.exe query CRSBackgroundService
```

- 服务注册和程序启动前必须创建`ProgramData`路径并设置最小必要ACL；服务启动后向Windows事件日志/结构化日志报告状态；普通WPF客户端不能代替管理员执行上述命令。
- 首期优先**框架依赖发布**并让安装器检查.NET 10运行时，或选自包含发布并验证依赖DLL布局；对于依赖多程序集的插件，**不能无验证地采用整套SingleFile/AOT/裁剪发布**，以免动态加载和程序集定位异常。允许开发调试以Console模式启动，但同一安装实例只允许一个运行模式占用JobStore。

### 5.2 官方插件更新/服务更新顺序

1. 校验官方签名发行包、宿主/插件/API/Schema版本兼容性及剩余磁盘空间。
2. 宣告维护状态，暂停计划触发，阻止新的手动Job，等待在途任务有界完成或取消并落盘Checkpoint。
3. 停止Windows服务（必要时退出WPF以释放UI插件DLL），生成**SQLCipher加密调度配置库**、市场数据库的一致性快照并校验原始证据引用；不得只复制仍处于WAL写入的主db文件。
4. 迁移候选数据库与新二进制到Staging/版本目录，验证Schema与读写冒烟，写入可重入的`UpgradeJournal`。
5. 按Journal切换新版本引用，启动服务，验证后台插件加载、调度定义加密库读取和Quartz触发器重建、关键查询；启动WPF再验证UI协议、导航。
6. 如果启动失败且新版本没有写入新事实，可受控恢复旧版本和快照；已产生新事实，则保全新旧两套数据并进入人工核验/受控恢复，**不得静默覆盖**。

插件后台禁用需同时移除/暂停对应Quartz触发器并有界终止在途任务；单纯隐藏WPF菜单不等于禁用后台任务。卸载服务默认保留用户/公共研究数据和诊断证据，删除数据必须是独立的明确用户选择，并说明备份/回退路径。

## 6. 状态机、监控与质量保证

运行执行日志使用`ExecutionId/PluginId/JobId/ScheduledAt/StartedAt/FinishedAt/Attempt/Result/Checkpoint/ErrorCategory`。一期没有`QRTZ_*`持久表：SQLCipher `TaskDefinition`是唯一Cron权威源；Quartz RAMJobStore只维护当前进程中的触发器。执行审计与来源证据使用独立记录。只有将来经安全验收切换Quartz AdoJobStore时，才迁移调度权威源，不允许两个可写Cron配置源并存。

作业状态示例：`Registered → Scheduled → Running → Completed`；失败类：`RetryDeferred/NotPublished/AccessBlocked/SourceSchemaChanged/Cancelled/Failed`。交易所未发布属于待观察，不等于异常；401/403/429和结构变化时严格退避，不重试风暴。提醒必须去重，任务执行多次不等于发送多次通知。

### 6.1 首期P0集成验收

| ID | 场景 | 通过标准 |
|---|---|---|
| SRV-01 | 管理员安装服务 | SCM可见，Delayed Auto启动、正确运行身份、普通用户不能改安装文件 |
| SRV-02 | WPF完全关闭仍执行 | Quartz如期触发公开数据Job，执行记录可后续查询 |
| SRV-03 | 未登录Windows | 开机后服务自启，无用户会话仍能访问允许的免费数据源 |
| SRV-04 | 机器重启/睡眠漏跑 | 不补发无限次Cron，按缺失交易日检查点有限补采 |
| SRV-05 | Cron持久化与恢复 | SQLCipher保留任务定义、启用状态和版本；重启后正确恢复Quartz RAM触发器，不重复注册/重复执行 |
| SRV-06 | 双实例/调试Console并发 | 拒绝第二实例，不从同一加密调度定义重建第二套触发器 |
| SRV-07 | WPF↔Service跨Session IPC | 普通用户可按授权读状态；未经授权无法修改计划/禁用插件 |
| SRV-08 | TOTP锁定或个人SQLCipher不可读 | 公开数据仍同步，私有库与密钥绝不暴露给Service |
| SRV-09 | LocalService公开数据访问 | 官网连通/代理/TLS配置可复现，不依赖桌面用户网络凭据 |
| SRV-10 | Service更新时正在采集 | 有界停止、Checkpoint持久化、升级后正确补采 |
| SRV-11 | 强杀/断电/损坏加密调度配置库 | 服务可诊断/从一致快照恢复，绝不把损坏文件作为明文库重新打开，不误删已接受市场事实 |
| SRV-12 | 插件Ui-only / Jobs-only / 双入口 | 任一种组合都可安装、独立启动，Service不加载WPF程序集 |
| SRV-13 | 插件禁用 | Service停止该插件新任务，其它插件正常执行、数据保留 |
| SRV-14 | 服务未安装/被停止 | WPF核心税务与绩效不受影响；插件显示后台不可用和缓存时点 |
| SRV-15 | 旧WPF连接新Service（及反向） | 协议不兼容时明确拒绝或只读降级，无未授权操作 |
| SRV-16 | 插件签名/哈希验证失败 | Service不加载后台DLL、不运行预期Job；写出可诊断原因 |
| SRV-17 | 卸载及用户显式删除 | 默认仅卸载程序并保留数据；明确选择删除才按审计机制清理 |

### 6.2 建议先完成的垂直POC

先实现一个不依赖ETF业务的`HeartbeatJob`：管理员安装服务→SQLCipher加密保存Cron→Quartz内存触发→后台日志→WPF Named Pipes查看最近执行→强杀/重启恢复→禁用/升级与服务账户ACL测试。**这个POC通过后再接入SSE/SZSE实际业务**，避免将Windows服务、插件宿主和官方数据源失败混成一个难以定位的问题。

## 7. 需锁定的实现事项与依据

1. 当前GitHub仓库的真实程序集布局、Windows发布产物和CRS加密数据库位置须在实施时核对，**本文件是目标架构，非已部署结果**。
2. Quartz版本采用已通过`.NET 10 + Windows Service`回归的锁定版本。首期RAMJobStore + SQLCipher持久TaskDefinition；若改为SQLite AdoJobStore需重新进行SQLCipher加密兼容P0测试；不得混用3.x/4.x配置或启用集群。
3. 自动抓取交易所官网是否允许、是否连贯可用、是否有足够历史深度，按ETF文档的M0独立验收；服务能运行不代表数据接口可用。
4. 对.NET服务运行账户相关的防火墙、系统代理、企业网络、证书、DPAPI、安装权限做实体机测试；不做“开发者账户可访问=LocalService可访问”的假设。

### 官方参考

- [.NET Windows Service Worker教程](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
- [Windows服务启动类型](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-config)
- [Windows LocalService账户](https://learn.microsoft.com/en-us/windows/win32/services/localservice-account)
- [Quartz.NET Hosted Services](https://www.quartz-scheduler.net/documentation/quartz-4.x/packages/hosted-services-integration.html)
- [Quartz.NET SQLite持久化（仅备选，需SQLCipher兼容验证）及不支持集群的说明](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html)
- [Named Pipe安全与访问权限](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [SQLite在线备份](https://www.sqlite.org/backup.html)
- [SQLite WAL文件](https://www.sqlite.org/wal.html)
