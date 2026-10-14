# CRS 插件化总体架构设计（宿主级规范）

> 版本：**v2.1 / 反方架构评审修订**｜日期：2026-10-14｜状态：**有条件通过，须完成P0验证**  
> 技术基线：C# / .NET 10 / WPF；Windows 10 19041+；本地优先  
> 适用：**今后新增且边界独立的业务能力优先插件化**；现有税务及已落地绩效功能不强制搬迁  
> 配套文档：[NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md](./NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md)

## 0. 反方评审结论与强制整改（ADR-PLG-002）

**裁决：采用“可信第一方、重启生效、业务逻辑隔离、尽量薄的宿主”，但不批准直接按照上一版的“自定义ALC + WPF + 私有DI容器”示例开工。** 原设计方向合理，存在以下实施级缺口。

| 级别 | 上版问题 | 修订后的决定 | 验收依据 |
|---|---|---|---|
| P0 | 默认自定义`AssemblyLoadContext`加载WPF XAML，存在资源解析兼容风险 | **第一期默认上下文加载可信WPF插件**；隔离加载上下文仅在真实WPF POC通过后可选 | WPF XAML/BAML、第三方控件、两个插件共存测试 |
| P0 | `ConfigureServices`构建私有DI后，生命周期对象如何获得私有Provider没有闭环 | 契约改为**插件入口创建自己的Session**，由Session持有并释放其私有DI | 从构建、启动、导航到停止完整演示 |
| P0 | 静态“私有目录”与`IPluginHttpClientProvider`容易被理解为强制隔离 | 明确它们仅是**受信代码约定**；同进程插件不可作为数据保密边界 | 威胁模型与发行包审查 |
| P0 | 数据库副本迁移、指针切换、原始证据文件更新缺少崩溃恢复顺序 | 增加**升级日志/状态机、写入冻结、快照验证、崩溃重入恢复** | 在每个切换点断电/强杀测试 |
| P0 | 上版称绩效“未来插件”，与现有仓库不符 | **已落地绩效P0维持原分层**，若要插件化须另立迁移ADR和数据兼容测试 | 仓库现有Solution/文档对账 |
| P1 | 插件平台一期包括签名下载安装、全局HTTP策略等，超过首个业务所需 | 一期**预装官方插件 + manifest + 受信发行包完整性校验 + 启停**；通用在线安装器后置 | 离线无插件和官方插件场景 |
| P1 | 所有插件强行采用独立数据库，未来跨模块个人数据有重复/越权风险 | 公开ETF数据独立；涉及私人账本只经授权数据快照，不复制敏感原库；所有权优先 | 隐私与数据所有权评审 |
| P1 | 插件通过任意`FrameworkElement`创建可能带来UI线程/导航资源泄漏 | 统一WPF UI适配接口、Dispatcher调用、导航Token释放、错误页面兜底 | 界面反复进入/退出和禁用重启 |

上表是**本规范最新决策**。旧版本“必须每插件单独ALC”“必须所有新功能从现有核心迁出”等说法如有冲突，以本节为准。

## 1. 对照现有CRS仓库（已查证与未查证）

截至2026-10-14，公开`CRS.sln`可核验生产项目：`CRS.Domain`、`CRS.Application`、`CRS.Infrastructure`、`CRS.Security`、`CRS.Desktop.Wpf`；现有六组测试项目。WPF项目目标框架为`net10.0-windows10.0.19041.0`，使用WPF-UI、CommunityToolkit.Mvvm、DataGridExtensions、LiveCharts。现有`docs/architecture/PROJECT_STRUCTURE.md`说明`Performance` P0已纳入Domain/Application/Infrastructure及WPF页面。**这些是当前公开文件所描述的事实**，不代表本次已逐文件审查所有实现。

| 事项 | 当前状态 | 本次做法 |
|---|---|---|
| 税务Domain、FIFO、汇率、安全 | 现有核心 | 不迁移、不改业务规则 |
| 绩效P0 | **已接入原项目分层**（另有未完成验收项） | 本轮不创建重复绩效插件、不删除现有代码 |
| 国家队ETF监控 | 新增独立业务 | 作为首个标准插件实现 |
| 今后的行业监控、新研究功能 | 尚未落地 | 优先独立插件；轻量视图、基础设施修复不必插件化 |
| 未来将绩效迁移为插件 | 仅候选议题 | 单独评估其敏感数据、现有UI/服务依赖、测试与数据迁移 |

正式改造前仍需检查最新代码的`App`/Composition、NavigationView、发布方式、目录/数据库配置与既有架构门禁；由开发实施生成`CURRENT_ARCHITECTURE_BASELINE.md`，列出实际文件位置和最小修改清单。不得重命名现有项目来迁就本设计。

## 2. 两层架构：稳定宿主 + 可选业务插件

```text
                                  ┌────────────────────────────────┐
                                  │ CRS.Desktop.Wpf（现有程序）      │
                                  │ 认证 / 税务 / 已落地绩效P0 / UI   │
                                  │ 插件入口 / 插件状态 / 导航挂载点  │
                                  └──────────────┬─────────────────┘
                                                 │ 只依赖宿主契约
                                  ┌──────────────▼─────────────────┐
                                  │ CRS.PluginHost                 │
                                  │ 清单发现/版本检查/启动/停止      │
                                  │ 导航桥接/作业调度/升级恢复        │
                                  └──────────────┬─────────────────┘
                                                 │ 受控能力契约
                          ┌──────────────────────▼──────────────────────┐
                          │ CRS.Plugin.Abstractions（net10.0）           │
                          │ CRS.Plugin.Wpf（net10.0-windows...）         │
                          └──────────────────────┬──────────────────────┘
                                                 │
                        ┌────────────────────────┴──────────────────┐
                        │                                           │
              ┌─────────▼──────────┐                    ┌──────────▼─────────┐
              │ NationalTeamWatch  │                    │ 未来行业研究等插件 │
              │ ETF≤20 / 未来持股  │                    │ 独立领域与数据     │
              │ 公共市场SQLite      │                    │ 视具体隐私需求     │
              └────────────────────┘                    └────────────────────┘

              私人税务SQLCipher、认证材料不在任何研究插件的对外契约中
```

### 2.1 项目与文件结构（目标增量，不破坏当前项目）

```text
src/
  CRS.Domain/                  # 原有
  CRS.Application/             # 原有，含当前绩效P0
  CRS.Infrastructure/          # 原有
  CRS.Security/                # 原有
  CRS.Desktop.Wpf/             # 原有，增插件导航插槽与宿主启动适配
  CRS.Plugin.Abstractions/     # 新增：BCL型契约、数据传输对象、能力抽象
  CRS.Plugin.Wpf/              # 新增：WPF导航/页面贡献小契约
  CRS.PluginHost/              # 新增：只认识manifest+契约
  Plugins/
    CRS.Plugins.NationalTeamWatch/  # 新增：首个业务插件
      Domain/ Application/ Infrastructure/ Presentation/
tests/
  CRS.Tests.PluginHost/
  CRS.Tests.PluginWpf/
  CRS.Tests.NationalTeamWatch/
docs/
  CRS_PLUGIN_ARCHITECTURE.md
  NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md
```

**第一期一个业务DLL + 逻辑文件夹分层即可**。`PluginHost`/契约拆为独立项目是为了编译依赖；Domain/Application等是否拆DLL由真实复用决定。插件发行包应单独生成，宿主源码不能对业务插件使用`ProjectReference`或硬编码类型名。

### 2.2 依赖门禁

```text
Desktop -> PluginHost -> Plugin.Abstractions
PluginHost -> Plugin.Wpf -> Plugin.Abstractions
NationalTeamWatch -> Plugin.Abstractions + Plugin.Wpf

禁止：税务Domain/Security -> 任何PluginHost或业务插件
禁止：业务插件 -> CRS.Desktop.Wpf/CRS.PluginHost/税务Infrastructure
禁止：业务插件A -> 业务插件B实现DLL或B私有数据库
禁止：宿主对插件业务ID编写特殊逻辑switch（通用清单/启停状态除外）
```

`CRS.Plugin.Abstractions`不引入WPF类型、`Microsoft.Extensions.DependencyInjection`等实现型依赖；生命周期契约仅用BCL和少量独立DTO。UI契约独立在`CRS.Plugin.Wpf`中，目标框架对齐当前WPF项目的Windows兼容范围；不能使宿主为此降级或升级目标Windows SDK基线。

## 3. 入口、私有容器与宿主能力（解决旧版生命周期断点）

### 3.1 建议公共接口（示意）

```csharp
// CRS.Plugin.Abstractions：不引用 DI 容器或 WPF
public interface ICrsPluginEntry
{
    IPluginSession CreateSession(IPluginHostContext context);
}

public interface IPluginSession : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IPluginHostContext
{
    string PluginId { get; }
    IPluginJobScheduler Jobs { get; }
    IPluginDataPaths DataPaths { get; }
    IPluginHttpGateway Http { get; }
    IPluginDiagnostics Diagnostics { get; }
}
```

- `ICrsPluginEntry`是由manifest声明并经校验的**单个入口类型**，必须实现无副作用构造/显式工厂；宿主从manifest和可信包定位，不静态引用业务DLL。
- `CreateSession`由插件**自己**创建私有容器（若使用DI）、处理自己的依赖注入和数据库对象，并返回Session。宿主既不提供根`IServiceProvider`，也不负责构建插件私有DI，因此不会出现“创建了ServiceProvider但插件无法拿到”的矛盾。
- `StartAsync`只注册宿主授予的作业；宿主启动成功后从可选`IPluginWpfSession`取得页面贡献并注册路由。`StopAsync`先取消作业、等待有界停止，宿主撤销导航注册；`DisposeAsync`释放插件拥有的容器和资源。状态由宿主维护`Discovered→Verified→Loaded→Started→Stopping→Stopped/Failed`。
- Session不可缓存来自宿主的长生命周期UI引用；所有导航贡献、任务句柄使用`IDisposable/IAsyncDisposable`注册令牌，并在退出前反注册。Stop失败只能隔离**后续调度与UI贡献**，不能保证强杀同进程恶意线程。
- 插件的私有容器可以选用`Microsoft.Extensions.DependencyInjection`，但**该依赖只存在插件实现层**。如选用，与宿主共用版本需统一锁定；不把`IServiceCollection`写入跨ALC的公共入口接口。

### 3.2 WPF可选扩展接口（不污染BCL插件契约）

```csharp
// 定义在 CRS.Plugin.Wpf；宿主与可信WPF插件共同引用
public interface IPluginWpfSession
{
    IReadOnlyList<PluginPageContribution> GetPages();
}
public sealed record PluginPageContribution(
    string RouteKey, string Title, Func<FrameworkElement> CreateView);
```

宿主在`StartAsync`成功后检测`session is IPluginWpfSession`，为每个`RouteKey`注册路由；`CreateView`必须通过WPF Dispatcher执行。宿主保留每条导航注册返回的`IDisposable`令牌，退出/失败时先撤销路由再停止Session，防止旧页面工厂和程序集被不必要地持有。一个**无UI的插件不必依赖WPF程序集**。`IPluginWpfSession`在WPF扩展契约中，具体类型与UI控件不是税务应用层的依赖。

### 3.3 能力是规范契约而非进程沙箱

| 能力 | 第一版用途 | 边界 |
|---|---|---|
| `Navigation`（WPF单独契约） | 宿主读取Session页面贡献、注册路由、返回失效令牌 | UI只在Dispatcher线程创建，路由自动加插件ID前缀 |
| `Jobs` | 任务互斥、取消、有限重试、恢复检查点 | 不允许插件私自建第二套常驻调度引擎 |
| `Http` | 官方公开数据的预设站点访问、限频、超时 | 仅规范可信插件；普通进程内代码理论上仍可自行联网 |
| `DataPaths` | 应用数据目录、缓存与磁盘占用诊断 | 只是路径配置，**不是Windows文件权限隔离** |
| `Diagnostics` | 插件日志、错误分类、健康状态 | 不写个人交易、种子、凭据及税务信息 |
| `PortfolioRead` | 未来插件按用户授权访问有限快照 | **一期无此能力**；敏感明细不得无授权复制到公开库 |

需要强制访问控制的不可信第三方插件必须在**不同低权限进程**运行并以IPC最小授权；不接受“同进程插件被沙箱保护”的宣传或验收。

## 4. WPF加载策略：默认上下文优先，ALC隔离需证明

**原设计默认每个WPF插件用自定义`AssemblyLoadContext`存在已知兼容风险。** WPF在XAML反射/资源加载过程中可能使用全进程程序集查找，微软WPF项目有尚待处理的公开问题：<https://github.com/dotnet/wpf/issues/1700>。一般.NET插件教程适用于程序集依赖解析，不代表已保证复杂WPF插件资源隔离。

**第一版推荐**：仅有可信第一方、随产品预装、重启升级的WPF业务插件，先用 **`AssemblyLoadContext.Default`** 加载（如适当的`Assembly.LoadFrom`路径及受控依赖解析）；插件及宿主使用**统一锁定**的WPF-UI、CommunityToolkit、LiveCharts和DI版本；业务DLL/资源命名唯一。Default上下文不能同时载入同名程序集的不同版本，这应通过包兼容门禁**拒绝启动**，而不是隐式替换。

**替代路线**：若真实样例证明WPF XAML、主题字典、第三方控件和二插件同时加载可靠，才允许对业务DLL采用自定义不可收集ALC + `AssemblyDependencyResolver`；共享的SDK、WPF框架及重要UI组件仍由Default加载。不能只拿控制台插件POC验收。

**一期不做动态热卸载**，所有安装、启用、禁用、升级、回滚在下次启动生效。未运行时更新安装包可保留版本目录，避免Windows正在占用DLL造成替换失败；安装路径不能落到数据源证据目录。插件名、发布目录大小写、程序集名和依赖版本全局唯一性都要测试。

## 5. 发行可信性、部署方式与生命周期

### 5.1 第一阶段只支持官方随包插件

- 主程序发行工艺先生成可信的插件包文件列表/哈希（或签名发行清单），通过经验证的主程序发布包**绑定**该清单；校验所有将执行的程序集和原生依赖。`plugin.json`中的自报哈希不是可信根。
- **一期不提供任意ZIP安装和在线插件商店**。官方插件可随宿主版本一同分发，也可由经过同等发行链验证的离线官方更新包安装。以后支持独立下载时，再补代码签名验链、公钥轮换和撤销策略。
- 仅支持与当前宿主兼容的插件：`id`永久稳定、`packageVersion`、`contractApiVersion`、`min/maxHostVersion`、`minOsVersion`、`runtimeTarget`、`schemaVersion`及数据格式版本独立记录。
- 更新包展开前验证路径规范化（`..`、绝对路径、软链接/重解析点）、大小上限、重复文件名、大小写碰撞、签名与外部依赖清单；不可把无签名任意DLL复制入加载目录。

### 5.2 状态与失败保护

```text
Installed/Disabled  --启用且下次启动-->  Verified
Verified            --加载成功--------->  Loaded
Loaded              --Start成功-------->  Started
Loaded/Started      --可预期异常------->  Failed（插件故障，宿主继续）
Started             --退出/禁用要求---->  Stopping -> Stopped -> Disposed
```

插件配置错误、资源失败、429、解析异常不会让税务流程主动失败；宿主将其标记`Failed`、取消插件任务并退出插件导航。但进程内OOM、native crash、死循环/死锁仍可能影响整个WPF主进程，**不能写成绝对的故障隔离SLA**。

## 6. 插件升级：跨文件切换无法靠单个SQLite事务解决

上版“数据库迁移副本成功后原子指针切换”遗漏了：插件二进制、数据库、raw证据目录及active-version指针可能跨文件系统对象更新，**不存在天然的跨文件事务**。必须增加崩溃恢复协议。

### 6.1 第一版建议升级协议

1. **准备**：验证官方包和兼容矩阵；新版本只解压到不可执行的`staging`；枚举当前真实数据库路径、WAL状态、插件作业与打开的连接。
2. **冻结**：用户退出或重启应用，停止插件写入，释放WPF页面、作业和SQLite连接；创建`UpgradeJournal`（旧版本/库版本/状态/时间）。
3. **快照**：使用SQLite Backup API或`VACUUM INTO`创建一致数据库快照，校验`integrity_check`；**绝不在活跃WAL模式下只复制`.db`文件**。
4. **候选迁移**：复制候选库，迁移`schemaVersion`并进行真实读写冒烟；旧库与旧插件目录不变。跨插件数据私有目录中的raw证据文件保持不可变，候选库引用已有证据；新证据先存独立候选区。
5. **切换**：同一卷内通过临时文件写入、flush、rename方式更新小型`active-version`元数据，记录对应`dbGeneration`与`packageVersion`；不能把单次rename等同整个升级原子事务。新插件启动时检查Journal和版本匹配。
6. **启动确认**：校验新插件、关键查询、存储证据引用与任务注册，成功才提交`UpgradeJournal=Committed`；失败时若**尚未对新库写入新事实**，还原旧指针和旧库快照。若已有新写入，停止采集，保留两份数据并提示人工/受控恢复，**禁止静默抹掉新数据**。
7. **异常恢复**：模拟进程强杀在每一步、用户多次重启、磁盘不足、文件被杀毒软件占用；启动时必须完成/回退Journal，绝不猜测DB版本。

`source_fetch`若指向数据库外的原始证据，恢复时要做**引用一致性检查**和孤儿文件垃圾回收（延迟、安全阈值）；不能单独回滚DB却清理仍被引用的证据文件。升级和主程序备份之间的责任必须明确，避免双备份重复耗盘。

### 6.2 不要在一期做的事

不做自动下载安装任意插件、不做在线市场、自动卸载热替换、资源动态强隔离、全局消息总线或多进程RPC。需为以后扩展预留协议版本即可，不提前实现。

## 7. 数据所有权与个人安全边界

- ETF/官方公告/历史份额/研究缓存属于`crs.national-team`插件，写入独立公开数据SQLite。税务SQLCipher及TOTP材料属于既有核心，插件契约不暴露底层数据库路径、密钥及宿主根服务。
- `DataPaths`只是逻辑约定，不构成隔离：插件与主程序同Windows用户进程仍可能访问相同权限文件。风险控制依赖**只发布审计过的可信第一方DLL + 签名/哈希/源代码审查**，真实攻击隔离以后只能通过进程权限实现。
- 插件与未来绩效等业务交换研究快照：只允许宿主代理的**版本化DTO查询**，明确数据所有者、授予者、范围、时间、撤销方法；首期不建事件总线和共享交易表。
- 现有绩效P0已经使用原有领域与个人加密库，**不得**为了新框架将其敏感数据移入ETF普通SQLite；如后续迁移为插件，需单独制定敏感数据能力、安全迁移与并存策略。
- 日志/诊断默认仅公开证券、数据源状态、算法版本、异常摘要；私有证券账户信息不得落到市场数据日志。

## 8. 启动调度、资源预算、用户体验

- 插件任务在主程序运行且认证状态满足既有策略后启动；锁屏/登出立即停止访问敏感能力，公开ETF任务是否继续由统一配置决定，但**不能绕过认证加载个人数据**。首期默认登录后同步，关闭主程序不承诺后台监控。
- `Jobs`由宿主调度唯一`PluginId:JobId`，含取消Token、串行互斥、执行时间与检查点；业务插件负责编排，不直接添加宿主级全局Quartz任务。
- UI与数据层通过轻量只读DTO连接，后台I/O不在UI线程执行，数据缺失/旧数据必须显示`AsOfDate/SourceStatus`；Stop时取消待显示的请求。
- CPU、内存、日志及原始证据大小先基准测试确定报警阈值；不对20只ETF提前引入复杂限流平台、分布式消息系统。

## 9. 里程碑与通过条件

| 阶段 | 交付 | P0门槛 |
|---|---|---|
| A-0 对账 | 从真实项目读取`.sln`/依赖图/WPF路由/现有绩效P0 | 形成“已有/新增/不改”清单 |
| A-1 插件WPF垂直切片 | 一个官方插件+第二简单插件+导航+Session私有DI | Default上下文加载XAML/图表正常、可取消、禁用需重启 |
| A-2 可信发行与恢复 | manifest校验、版本/契约拒载、UpgradeJournal、DB副本迁移 | 每个升级断点强杀后可自动识别与恢复 |
| A-3 宿主能力与回归 | Jobs、HTTP规范、数据目录、诊断；隔离审查 | 无插件可独立运行税务与绩效P0，真实数据不泄漏 |
| A-4 ETF业务接入 | 详见ETF专项M0～M4 | 与交易所数据POC相互独立验收 |

**测试矩阵**（每项在Debug与已发布Release至少各跑一次关键路径）：无插件启动、两个插件同时加载、不同DLL依赖版本冲突拒载、坏签名、生命周期异常、重复开始/停止、WPF资源多次导航、后台关闭竞争、长期无网、SQLCipher核心数据不变、DB迁移前/中/后强杀、WAL快照、缺原始证据拒发可复算指标、旧插件+新库不兼容拒载、两份数据有新写入时停止回滚。

## 10. 本次反方评审修改记录

1. 已核验**现有CRS项目名、WPF目标框架与已接入绩效P0**，从“假设现有绩效是未来模块”改为“现有不重复建设”。
2. WPF业务插件**默认Default加载上下文**，不再将`AssemblyLoadContext`隔离视为可无条件依赖；补真实WPF双插件POC。
3. 将DI契约从宿主传`IServiceCollection`改成插件**自己构建Session/容器并负责释放**。
4. 插件发行收敛为**官方受信预装**；独立在线安装与第三方执行后置。
5. 将升级设计扩充为**数据库+二进制+RawStore一致性+崩溃日志恢复**，删除“单次原子切换就安全”的暗示。
6. 明确首期`DataPaths/Http`只是受信代码约定，不会形成文件系统/网络强沙箱。

## 11. 来源与待核事项

- [CRS仓库 README](https://raw.githubusercontent.com/sword0880/CRSTool/main/README.md)
- [CRS.sln 当前项目清单](https://raw.githubusercontent.com/sword0880/CRSTool/main/CRS.sln)
- [CRS项目结构](https://raw.githubusercontent.com/sword0880/CRSTool/main/docs/architecture/PROJECT_STRUCTURE.md)
- [CRS WPF项目文件](https://raw.githubusercontent.com/sword0880/CRSTool/main/src/CRS.Desktop.Wpf/CRS.Desktop.Wpf.csproj)
- [.NET插件加载文档](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
- [WPF XAML与ALC兼容性问题](https://github.com/dotnet/wpf/issues/1700)
- [SQLite Online Backup API](https://www.sqlite.org/backup.html)
- [SQLite WAL文件与一致性](https://www.sqlite.org/wal.html)

> 本次校验仅涉及上述**公开仓库文件**，未执行本地编译、端到端WPF/插件POC、交易所真实接口测试或连续20个交易日采集；实施时以当前主分支实际版本重新校验。文档是架构决策，不是“已通过验收”的报告。
