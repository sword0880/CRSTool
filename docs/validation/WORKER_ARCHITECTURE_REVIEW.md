# 插件与后台任务反方架构审查

日期：2026-10-15；源码基线：`844aca0`。依据插件总体设计、后台服务设计、ETF一期设计及实际源码。本报告提出整改建议，不代表已完成整改或生产发行批准。

## 判断与命名建议

保留独立 Windows 服务、Quartz RAMJobStore、SQLCipher 唯一任务权威及本机 Windows 令牌授权管道。这套技术选择适合当前单机、少量第一方插件。当前实现可以作为心跳 POC；业务所有权、故障处理和可恢复命令尚不足以支撑 ETF 一期长期运行。

建议把项目和程序集统一命名为 `CRS.Worker.Abstractions`、`CRS.Worker.PluginHost`、`CRS.Worker.Ipc`、`CRS.Worker.Service`。Worker 表达独立任务执行进程；Service 明确其 Windows 服务交付形式。保留 `CRS.PluginHost` 作为桌面 UI 插件宿主，二者职责清晰。

本轮只有命名建议，未执行重命名。后续应分两类处理：

- 代码名称：项目、程序集、命名空间、项目引用、解决方案、脚本及测试路径一起更改。
- 已持久化标识：SCM 服务名及其服务 SID、Named Pipe、数据目录、密钥保护常量、插件 ID/Job ID 和发行信任索引分别制定变更方案。尤其 `ServiceKey.cs:11` 的 DPAPI 附加熵字符串属于密钥封装协议，机械替换会使已有密钥无法解封。程序集改名不要求同时改变这些运行标识。

### 整改进度更新（2026-10-15）

上述结论及以下 F01—F11 保留为 `844aca0` 的审查基线。现已开始整改，当前结果见 [Worker v2 整改实施](../implementation/WORKER_REMEDIATION.md)：四个项目已重命名；用户进一步确认将管道一并改为 `CRS.Worker.Service.v2`。密钥保护常量、SCM 服务名/服务 SID 和既有数据目录保留。F02—F06 的首批代码已整改；F07/F08 仅完成基础重试与插件写入门槛，其余缺口继续保留。

## 已确认问题与整改建议

优先级：P1 表示接入真实 ETF 长期任务前应整改；P2 表示应在扩大插件范围或正式发行前完成。本文没有以尚未验证的攻击假设宣称存在 P0 安全漏洞。

### F01 / P1：业务调度权威尚未迁移，双宿主发行协议也未统一

证据：`src/CRS.PluginHost/PluginRuntime.cs:29` 仍创建桌面 Quartz；`src/Plugins/CRS.Plugins.NationalTeamWatch/NationalTeamPluginEntry.cs:38` 仍注册 ETF 同步；`Infrastructure/MarketRepository.cs:20` 仍打开桌面明文市场库。新服务只运行心跳，关闭 WPF 后执行 ETF 的目标尚未实现。

桌面 `PackageVerifier.cs:11` 识别旧的 EntryAssembly/EntryType/ContractApi 清单；后台 `BackgroundPluginCatalog.cs:16` 识别 BackgroundEntry/HostContract。两个宿主未共用同一套清单和兼容性验证器，后台缺少与桌面等价的宿主版本、数据 Schema、运行时和能力校验。

整改：先冻结纯 BCL 发行清单模型和两个入口的兼容规则，再拆 Core/Jobs/Ui。为插件持久化明确的数据写入所有者，服务启用成功后关闭旧桌面注册与写连接；UI 经 IPC 查询和发命令。默认不把旧明文市场库接入新服务，按既定规范从可信公开来源新建加密库。迁移过程中明确谁负责采集，避免双调度和双写。

验收：同一真实发行包分别由两个宿主加载；服务不可用时 UI 正确报告；WPF 关闭/锁定后服务同步；同时启动 WPF 与服务只能有一个 ETF 写入方。

### F02 / P1：执行审计写入失败后，服务可能继续调度并报告健康

证据：`BackgroundRuntime.cs:186` 调用 repository.End，随后 finally 无条件移除 running。未把写入失败转换为宿主存储故障或停止该任务；`GetHealth` 在第84行只检查计划待重建和处理器缺失，不检查执行审计持久化失败。

如果业务事实已经提交，而磁盘满等异常导致结束记录未落盘，后续 Cron 仍可能继续执行。只看健康状态和 running 集合无法识别这段不确定状态。WPF `BackgroundServiceViewModel.cs:26` 还会在查询成功后写出“后台服务正常”，即便返回的健康 State 是 Degraded。

整改：将业务失败与宿主存储失败分开。Begin/End/命令日志写入失败进入 StorageFault 状态，停止相关新工作，暴露明确健康原因；恢复核验通过后再放行。UI 直接映射健康状态。业务事实与调度审计跨库不承诺原子提交，必须依赖业务幂等键及检查点复核。

验收：在 Begin、业务事实提交后、End 及命令审计提交处注入磁盘满/只读/数据库忙；服务不得继续显示正常并无限接受新写入。

### F03 / P1：手动命令的“已接受”缺少可恢复执行语义

证据：`BackgroundRuntime.cs:87` 先持久占用 RequestId，第99—103行只在内存 queued/RAMJobStore 中排队并返回 Accepted。执行记录直到第168行才创建。

若计划已暂停，手动命令 Accepted 后、Begin 前发生崩溃，该次排队随进程消失；恢复后相同 RequestId 又被持久审计拒绝，没有可查询或继续执行的持久工作项。客户端每次点击又创建新 RequestId，响应丢失后的重试无法关联到上一次请求。`EncryptedTaskRepository.cs:128` 对重复请求更新原审计结果，也会使原 Accepted 结果被覆盖。

整改：引入持久命令/待执行记录，记录 RequestId、调用者、参数摘要、ExecutionId 和 Pending/Dispatched/Completed 状态。接受命令与创建待执行项同事务提交，重启扫描待处理项；相同请求返回原状态/结果。审计事件追加保存。业务继续采用至少一次执行和幂等，不宣称 Exactly Once。

验收：在请求提交、入队、开始、业务提交和响应发送前分别强杀；原 RequestId 可查询，任务不无声丢失，重复请求不会新增独立业务事实。

### F04 / P1：插件生命周期没有可靠的超时和故障隔离

证据：`BackgroundPluginCatalog.cs:59` 无宿主等待期限地调用 StartAsync；第118—122行仅给 StopAsync 传15秒取消令牌，DisposeAsync 没有期限；单个会话失败还会打断后续清理。`BackgroundRuntime.cs:166` 的 CancelAfter 也只发出取消请求。

不响应令牌的插件可以阻塞加载/清理，或长期占用 Quartz 工作槽。整个加载循环没有插件级异常隔离，一个已验证插件的初始化异常会使整个服务失败并进入 SCM 恢复，而其它正常插件也不可用。collectible AssemblyLoadContext 的卸载是协作式的，不能强制终止插件线程。

整改：每插件记录状态和故障分类，正常插件可继续提供服务；宿主级信任索引损坏仍整体拒绝启动。Start/Stop/Dispose 均设置宿主等待期限，逐插件清理并汇总异常。明确执行超时后的策略：第一方协作取消、必要时终止宿主；需要只终止故障插件时再引入工作子进程。不能把 WaitAsync 超时误写成已经终止插件执行。

验收：一个插件抛异常、一个忽略取消、一个清理抛异常；验证其它插件的运行、完整清理和最终停止期限。

### F05 / P1：IPC 单连接串行处理与全局重放缓存存在可用性问题

证据：`BackgroundPipeServer.cs:18` 顺序接受并处理一个连接，第21行允许该连接占用8秒，第73行最大实例数为1。第55—59行所有只读查询也进入全局2048项/10分钟缓存。

普通已认证用户可以通过慢连接延迟管理请求，或用大量不同 ID 的只读查询填满缓存，导致其它用户和管理员获得 Busy。有限超时有助于清理单连接，但没有提供调用者之间的资源公平性。

整改：有界并行连接、每 SID 限流和连接配额、明确管理请求的可用容量。查询保留请求关联和限流即可，修改命令使用持久幂等记录；不要让普通查询耗尽管理命令的全局去重额度。拒绝原因需要有限审计及明确错误响应。

验收：慢读/慢写、单 SID 洪泛和多个正常客户端并发时，管理员仍能在约定时间读取健康及暂停任务。

### F06 / P1：历史 DTO 的合法大小与管道帧上限冲突

证据：`BackgroundRuntime.cs:178` 接受1024字符检查点，`EncryptedTaskRepository.cs:118` 返回最多100条历史，`Protocol.cs:37` 限制整个响应131072字节。没有分页或按序列化字节裁剪。超限异常被服务端按 IOException 捕获后关闭连接，客户端看到的可能只是服务不可用。

本轮做了最小序列化实验：100条、每条只含一个1024字符中文检查点的 JSON 为 **616213字节**，已超过帧限制；实际 ExecutionRecord 还包含其它字段。这是消息预算冲突的复现，不是对已安装服务注入了此类历史。

整改：GetHistory 增加游标分页、服务器页大小和序列化字节预算；列表只返回短检查点摘要，详情单独查询。GetJobs 也按最大插件/任务数量核对消息预算。保留帧上限，并返回明确 PayloadTooLarge 等协议错误。

验收：ASCII/中文/转义字符、最大任务数、最大历史数和边界字节大小；合法输入下管理页面仍能查询。

### F07 / P1：退避时间、业务等待和真正重试被混在一起

证据：`EncryptedTaskRepository.cs:105` 将 Succeeded/Cancelled 之外的所有状态都计入失败次数，包括 NotPublished 和 RetryDeferred；第109行只持久化 NotBefore。`BackgroundRuntime.cs:158` 在退避期直接跳过，未安排到期重试；每日 Cron 可能一直等到第二天才再查。服务启动时第45—48行又对每个启用 Job 统一加两秒启动执行。

整改：把正常发布等待、可重试传输失败、站点限制及结构变化分开。业务结果携带受控 RetryAfter/错误范围，待重试工作项持久化；Quartz 负责唤醒，业务检查点决定待补内容。启动补偿应由声明的缺口修复任务执行，有扫描窗口、请求预算和幂等键，不对所有 Job 无条件启动执行。来源级阻断应约束共享该来源的多个 Job。

验收：晚发布、403/429、结构变化、离线后恢复和睡眠跨交易日；验证既能有限补采，也不会重启集中请求或误把正常等待计作连续失败。

### F08 / P1：按 Job 防重入不能保证插件共享资源安全

证据：`HostedBackgroundJob` 保存共享 Session，各 Job 都调用同一 Session.ExecuteAsync；`BackgroundRuntime.cs:34` 允许两个工作槽，[DisallowConcurrentExecution] 只约束同一个 JobDetail。

ETF 的日常同步与缺口修复若是两个 JobKey，可以同时进入同一插件仓储、原始证据目录和来源限速状态。心跳只有一个 Job，无法验证这类行为。

整改：明确插件内并发模型。为同一个市场库配置单写队列/事务边界，为同一来源配置共享速率和阻断状态；独立插件可有界并行。只在真正需要的资源边界串行，避免一把全局锁覆盖所有联网和解析。

验收：同插件两个 Job、同来源不同 Job 与不同插件并发；确认无连接并发误用、重复修订、写入覆盖和限流绕过。

### F09 / P1：密钥可解封不等于具备灾难恢复能力

证据：`ServiceKey.cs:22` 使用机器作用域 DPAPI；`StorageSecurity.cs:14` 在正式模式不校验既有 ACL，依赖安装时权限。`EncryptedTaskRepository.Snapshot` 有加密快照原语，但没有运维恢复流程，当前实施记录也明确标注未完成。

LocalMachine DPAPI 的隔离依赖密文文件访问控制，封装中的 SID 字段是应用校验，不是额外密码学隔离。备份、手工恢复或权限漂移后，根目录 ACL 正常也不自动证明所有已有子文件权限正确。机器丢失时，只有加密数据库和原机器 DPAPI 文件未必能恢复。

整改：启动检查密钥/数据库/备份及必要祖先目录的有效 ACL 和所有者；设计与个人税务密钥独立的恢复封装、同机重封装和账户变更流程。恢复必须核对数据库身份、Schema、完整性与检查点。公开数据可重建的范围和不可替代的原始证据分别定义保留策略。

验收：ACL 漂移、旧备份显式宽权限、缺钥、换账户、跨机恢复；无法恢复时保留现场，不覆盖或自动重新生成钥匙。

### F10 / P1：安装失败不可重入，升级流程尚未闭环

证据：`Install-Service.ps1:45` 创建最终程序目录并改变权限，然后复制和注册服务；第40行在目录已存在时拒绝后续安装。中途失败没有安装日志/回滚。第82行只检查 SCM Running，尚未检查插件和加密库就绪。服务配置成功后，插件加载仍可能失败。

整改：预检、受保护暂存目录、安装事务日志和可重入修复；仅回滚本次创建的资源，保留既有数据及密钥。安装成功应以带期限的应用健康核验为准。正式升级使用版本目录、维护状态、加密一致快照、候选验证和切换日志；新事实提交后禁止直接回退旧库。卸载重装可用于当前开发验收，不能代替运维升级状态机。

验收：复制、ACL、事件日志、SCM 注册、启动、插件就绪各阶段失败后均能恢复或再次安装；运行中升级及切换前后强杀保留数据与审计。

### F11 / P2：后台插件契约仍不足以约束真实采集插件的组合方式

证据：`CRS.Background.Abstractions/Contracts.cs:6` 的 CreateSession 无 HostContext；后台加载器使用根目录简单程序集名解析，没有完整的依赖/RID/原生库版本治理。当前只有无依赖心跳，不能据此证明 SQLCipher、HTTP 和解析组件的真实插件包均可加载。

整改：在引入 ETF 前明确最小 HostContext：插件 ID、受限存储路径、官方 HTTP 网关、诊断、检查点/加密连接工厂。接口保持 BCL，不暴露根 DI、Quartz 或个人密钥。统一能力与来源授权；对共享契约保留单一类型身份，对私有依赖和原生库制定可验证解析策略。第一方同进程能力接口用于治理，强安全隔离需要另行采用进程/OS权限边界。

验收：真实 Jobs + Core + SQLCipher 包、两个插件私有依赖冲突、原生库缺失/版本冲突、UI-only/Jobs-only/双入口组合；服务始终不加载 WPF 或私人税务程序集。

## 整改顺序

1. 固定 Worker 命名和运行标识变更边界；统一发行契约，制定 ETF 唯一数据所有者切换规则。
2. 修复执行落盘失败、持久命令、生命周期及 IPC 消息/连接预算问题（F02—F06）。
3. 接入真实 ETF Jobs，完成等待/退避/检查点补采和插件资源并发模型（F07—F08、F11），移除旧桌面调度和写连接（F01）。
4. 完成密钥恢复、可重入安装和升级闭环（F09—F10），再进行无人登录、睡眠恢复和真实来源长期验收。

命名更改可以先做成独立机械重构，功能修复分开提交；数据库、密码学常量、SCM 和 IPC 标识按明确方案处理。

## 本轮证据与限制

本轮进行了源码/文档静态审查和消息序列化大小实验。没有修改业务实现、重命名项目、重启服务或重新执行上一轮29项心跳套件；现有心跳通过结果不覆盖本文列出的故障注入和多任务场景。当前没有证据证明发生了明文密钥泄露、实际重复 ETF 写入或第三方代码越权执行。

平台依据：

- [Microsoft：AssemblyLoadContext 卸载是协作式的](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)。
- [Microsoft：DataProtectionScope.LocalMachine 的保护范围](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope)。
- [Quartz：DisallowConcurrentExecution 按 JobDetail 约束并发](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/more-about-jobs.html)。
