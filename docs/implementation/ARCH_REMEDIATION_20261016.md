# 2026-10-16 架构整改实施记录

历史基线为已移除的20261016整改建议；现行清单见[架构整改单](../architecture/ARCHITECTURE_REMEDIATION_PLAN.md)。按用户要求，ARCH-001（P0 税务正确性、独立金额 Golden Dataset）最后处理；ARCH-002 同属 P0，仍优先推进。该排序不降低任何发布门槛。

升级与跨机恢复的边界见[维护规范](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)。第二批已接入Worker维护代码与隔离检查；跨机恢复仍为Proposed，完整发布门槛继续保留。

## 顺序与关闭条件

| 顺序 | 编号 | 当前状态与代码依据 | 下一步与不可替代的验收 |
| --- | --- | --- | --- |
| 1 | ARCH-003 | 本批代码与自动验收完成：移出全局锁；插件级查询准入、异常隔离、取消和维护排空 | 真实15秒查询、控制面2秒SLA、取消及重复维护回归通过；不代表所有数据库操作均已移出管理事务锁 |
| 2 | ARCH-009 | 本批文档与自动门禁完成：当前架构入口、30项目和直接依赖清单、循环检测 | 全解决方案构建、现有架构门禁及四种负例通过；仓库尚无独立托管CI，沿用Run-ReleaseAcceptance入口 |
| 3 | ARCH-002 | 已接入维护事务与启动保护；第三批增加隔离LocalService真实SCM升级/恢复10项检查，部分实现，未关闭 | 仍缺安装/卸载/重装全生命周期、全部真实插件Schema迁移矩阵、硬断电/磁盘满及生产签名成功路径；隔离SCM测试不等于完整发布验收 |
| 4 | ARCH-005 | 未关闭。ServiceKey仍绑定LocalMachine DPAPI及服务身份 | 先接入服务身份内的恢复凭据封装与一致性备份，再于干净Windows验证。管理员直接运行ServiceKey.Open不能冒充原服务身份；禁止通过放松SID校验解决 |
| 5 | ARCH-004 | 当前第一方边界已明确：受信任同进程代码，不是沙箱；生产安装已有CMS发布者验证，运行时依赖管理员索引和包哈希 | 签名/证书轮换、发布链和依赖供应链验证未关闭；第三方独立进程与低权限身份另行评审 |
| 6 | ARCH-007 | 未关闭。现有来源政策、修订审核和原件保全继续沿用；不得把ETF份额变化当机构交易 | 权威完整交易日历、随机缺口和修订、连续20交易日及长期来源验收。不能在单轮合成测试中宣称完成20日验收 |
| 7 | ARCH-006 | 未关闭。已有封存、冻结结果读取、原件核对、同构建重放及输出比对 | 尚需能力分级与完整依赖/DTO兼容包、多历史版本试验；程序集摘要一致不等于完整历史环境已保全 |
| 8 | ARCH-008 | 第三批已实现非阻塞准入、每插件有界FIFO、重复触发合并及可配置全局并发；会话显式声明安全并行后受宿主预算约束 | 慢插件不再靠等待写锁占满Quartz名额；跨插件共享来源预算、长期超载/重试风暴及真实业务容量验收仍待完成，未全部关闭 |
| 9（最后） | ARCH-001 | 按要求暂缓，未修改税额、FIFO、导入映射或汇率口径 | 定义并接入独立样本规范、至少3–5组真实年度独立签认及CI；现有程序结果不得倒填黄金预期 |

## 本批实现

`BackgroundRuntime.HandleAsync` 将QueryPlugin分派到独立执行路径。全局gate仅用于查询准入及完成登记，不跨插件异步调用持有。同插件仅允许一个在途查询，额外请求立即返回PluginBusy；不同插件各用独立额度。协议版本保持不变，非Ok结果仍由客户端已有错误处理呈现。

普通查询异常返回PluginQueryFailed，只记录插件ID和异常类型；插件IOException不再误触任务仓储全局StorageFault。入参、JSON及响应上限仍沿用原规则。不合作查询沿用catalog超时与正式宿主进程终止策略，不能假装被取消后直接卸载。

维护先阻止新查询，锁外触发查询取消，再等待在途查询退出后关闭调度器。正式宿主原有停止期限仍约束整个过程。重复维护跳过已经Shutdown的Quartz，支持显式维护后正常Dispose。任务仓储写事务、计划修订与持久命令幂等边界保持原实现。

新增[当前架构](../architecture/CURRENT_ARCHITECTURE.md)列出实际17个生产项目、11个检查项目与2个夹具项目、直接依赖、启动入口、进程/IPC、SQLCipher/密钥域与插件生命周期。旧PROJECT_STRUCTURE、SAD明确为补充说明；开发规范要求先查源码和当前入口。

`Verify-ProjectGraph.ps1`接入`Verify-Architecture.ps1`，由已有发布验收脚本调用：检查解决方案引用、生产项目遗漏、循环依赖、文档项目清单及生产项目直接依赖。负例在独立目录中注入循环、过期项目名、过期直接依赖和未登记生产项目，不修改实际项目。

## 验证记录

测试只操作`.test_tmp-arch-20261016`及测试自有子进程。正式安装服务、已有业务数据、DPAPI密钥及用户此前未提交的页面/Cron改动均未替换。

受限环境首次执行：默认临时目录SQLCipher打开失败；改用工作区隔离临时目录后，Windows管道创建/连接权限不足。完整Windows身份/ACL用例使用已获自动批准的非受限隔离测试运行。首次完整运行发现重复维护Shutdown异常，已修复并要求重跑；不能以此前部分通过作为最终通过。

最终验证（2026-10-16）：

- 全解决方案Release构建：0警告、0错误。
- Verify-Architecture：业务分层、插件及Worker边界、ETF双入口、绩效复杂度门禁通过。
- Verify-ProjectGraph及Test-ProjectGraph：30项目与直接依赖一致，无循环；四种结构回退全部拒绝。
- 完整后台回归：95条PASS，包含主检查29项、整改46项、权限9项、阶段日志11项；本次新增查询隔离8项均通过。
- git diff --check通过；新增文档本地Markdown链接有效。

完整后台重跑中曾在既有的第二服务子进程10秒启动/退出等待处超时，随后重新完整执行通过，未通过放宽超时或跳过用例消除失败。最初重复维护异常已在最终代码修复；Quartz4.1使用Status/Shutdown状态核验。以上过程保留以便后续追踪环境或时序稳定性，不宣称长期压力验收完成。

最终不可复用开发包：`artifacts/worker-arch-20261016-reviewed`；成功完整日志：`artifacts/worker-arch-20261016-background-retry.log`。首个`worker-arch-20261016`包及未通过日志仅作故障定位，不能部署。公开发布保持未批准，最终开发包也未部署到本机服务。

复现：

```powershell
dotnet build CRS.sln -c Release --no-restore -m:1 -p:NuGetAudit=false
./tests/Verify-Architecture.ps1
./tests/Test-ProjectGraph.ps1
# 使用新的发行路径；完整后台套件需要真实Windows管道/ACL测试权限。
./tests/Publish-BackgroundService.ps1 -OutputDirectory artifacts/worker-arch-new -Configuration Release -HeartbeatOnly
$env:TEMP=(Resolve-Path .test_tmp-arch-20261016).Path
$env:TMP=$env:TEMP
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- artifacts/worker-arch-new
```

## 第二批：Worker维护事务与启动保护

本批仍优先ARCH-002，不改动税务/FIFO/导入映射/汇率规则；ARCH-001保持最后处理。未修改或部署已安装服务，测试只使用新建目录、测试自有进程及合成原件。

新增`WorkerUpgradeJournal`及`WorkerUpgradeRecord`，与桌面PluginHost日志独立。ServiceWorker在Generic Host接入SCM后、打开ServiceKey/仓储/插件前检查维护阶段和发行清单SHA256。候选还核验整份快照清单、每个数据库/密钥/原件文件哈希；随后Flush并原子提交Starting/NewFactsPossible=true，才允许打开数据。Healthy先于健康端点开放，但不自动Commit。Prepared、BackedUp、Staged、Starting、Healthy、RollingBack和NeedsManualRepair都不能靠普通服务重启绕过。已确认或已安全回退的版本必须与发行指纹一致。

新增维护模块及Upgrade-Service/Recover-Service脚本。正式路径和身份固定；维护租约跨整个事务保持，数据租约跨停机快照/程序切换保持。先记录Prepared，再禁用自动启动并停止服务；整树备份保留WAL/SHM、人工原件和配置，排除租约及维护目录；校验副本和源文件未变化。候选旁路写入、按既有程序ACL保护、重新校验；两次目录改名保留旧程序。成功应用健康后显式Commit；失败重新读取宿主最新标记，保持禁用/维护状态，不用启动前缓存的false覆盖新事实风险。

恢复入口可查看状态、继续原候选或在无新事实时恢复旧程序。RestorePrevious只恢复程序代次，不恢复旧数据库；有新事实可能性一律拒绝。第一次目录改名后断电、活动目录暂缺，可恢复原程序或完成经校验的候选切换。失败候选和旧程序不删除；已结束日志在下次维护归档。状态读取使用共享删除句柄，不阻塞原子日志发布。

Install-Service与维护共用发行包验证并保留CMS签名；未解决日志阻止卸载/重装绕过。保留数据重装仅允许同一已确认发行指纹。生产候选要求版本递增、维护协议和来源版本白名单；开发模式才显式允许同版本不同构建用于隔离验收。所有新脚本/模块和兼容声明均进入发行文件哈希索引。

实际安全边界：当前候选在原数据目录运行，不具备独立候选数据库代次或只读健康模式；因此访问数据即记可能有新事实，比精确判断首次领域写入更保守。备份哈希证明文件完整，不证明跨机可解密。SCM实际升级/卸载/同发行重装、所有真实插件Schema、生产签名成功路径、硬断电及磁盘满尚待专门环境；ARCH-002/005仍未关闭，不把代码接线或隔离强杀当作完整发布验收。

新增`Test-WorkerMaintenance.ps1 -CheckStartupInterop`与`WorkerUpgradeChecks`（也接入完整后台套件）。前者在隔离合成文件中覆盖未签名正式包拒绝、包篡改、越界/备用流、快照/WAL/原件、程序切换、危险回退、两次改名间崩溃及PowerShell/C#日志互操作；后者使用真实SQLCipher/DPAPI验证各阶段阻断、旧库/密钥不变、备份篡改拒绝、风险标记、显式确认、回退限制，以及测试自有Worker进程真实强杀重启。

第二批最终验证（2026-10-16）：

- 全解决方案Release构建：0警告、0错误。
- 完整后台套件116条PASS：主检查29、整改46、升级启动21、ACL9、阶段日志11；退出码0。
- 维护文件事务15项通过，另有C#互操作探测确认PowerShell生产者与Worker启动门禁契约一致。
- Verify-Architecture全部通过；维护脚本语法、git diff --check及相关文档本地链接检查通过。

最终不可复用开发包：`artifacts/worker-upgrade-20261016-reviewed`；完整后台日志`artifacts/worker-upgrade-background-final.log`，文件事务/互操作日志`artifacts/worker-upgrade-maintenance-final.log`。初始`worker-upgrade-20261016`仅用于前期启动检查，不作为最终交付包。最终包未替换当前安装服务。

复现时先使用新的发行目录构建包，再运行：

```powershell
./tests/Test-WorkerMaintenance.ps1 -CheckStartupInterop
$env:TEMP=(Resolve-Path .test_tmp-worker-upgrade).Path
$env:TMP=$env:TEMP
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- artifacts/worker-upgrade-20261016-reviewed
```

下一步继续ARCH-002真实SCM及全部库迁移验收，再推进ARCH-005独立恢复凭据与跨机恢复，保持ARCH-001最后处理。详细操作入口及未完成目标见[Worker维护边界](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)。

## 第三批：按用户要求优先处理ARCH-008

此前全局4来自项目硬编码，不是Quartz硬限制，也不是4个专属物理线程。仅增大该值不能消除同插件锁等待占用执行额度的问题。本批改为非阻塞准入：不能取得插件额度的Job立即返回，保存在每插件有界FIFO，释放额度后以Quartz触发器唤醒队首。没有脱离调度器生命周期的执行；维护仍取消并等待实际任务退出。相同Job的自动重复触发合并，手动请求优先，自动超额计入容量延后指标；手动超额在接受和落盘前返回PluginBusy。已接受命令不丢失，继续复用原请求ID及恢复流程。

全局并发改为管理员私有配置，按用户后续要求缺省固定为8，可显式1–32（显式0保留按CPU取4–16模式）；插件等待预算缺省16，可设1–32。旧会话保持单写；会话实现`IConcurrentBackgroundSession`声明安全并行后，仍受不超过全局一半的宿主额度约束。健康响应增加运行/等待、持久待执行手动命令、插件额度和合并/容量计数。具体配置见[当前架构](../architecture/CURRENT_ARCHITECTURE.md)。任务抵达速度持续超过处理能力时，任何有限并发都不能保证零积压；本批解决锁等待挤占与无限重复排队，不宣称跨来源预算或长期负载验收完成。

配套SCM检查复用生产事务模块，发现并修复旧程序回退两次改名之间中断无法续接，以及已结束事务启动失败缺少安全重试入口的问题。新增ResumeConfirmed只接受日志确认的同一完整发行，不回退数据库。隔离测试使用唯一命名LocalService服务和合成发行版本，实际心跳写入后核验禁止回退、继续候选不丢执行事实；正式安装服务的路径/进程/状态保持不变，测试服务注册已清理。它不替代真实新旧Schema、重装、生产签名或硬断电验收。

专项证据（2026-10-16）：

- 全解决方案Release构建0警告、0错误；Verify-Architecture通过。
- 调度专项22项通过：一个运行中的慢插件加四个等待Job时，另一插件在2秒内成功；重复触发合并、容量拒绝、暂停、幂等、顺序、等待时间与排空通过；声明并行的夹具实际观察到两个重叠执行。
- 维护文件事务23项及C#启动互操作通过；隔离真实LocalService SCM事务10项通过（`artifacts/worker-scm-upgrade.log`）。SCM测试两份发行来自相同构建，只有版本清单不同。
- 最终开发包完整后台回归138条PASS，退出码0：主检查29、整改46、调度22、升级启动21、权限9、阶段日志11。日志`artifacts/worker-fair-scheduling-final.log`；测试仅操作隔离目录与自有进程。
- 修改脚本语法、文档本地链接和git diff --check通过。

开发包`artifacts/worker-fair-scheduling-20261016`未签署生产发行签名，未部署到已安装服务。ARCH-008跨插件来源预算和长期压力验收仍开放，ARCH-001税务正确性继续最后处理。

后续按用户要求将缺省并发固定为8，后台项目Release构建0警告/0错误，22项调度检查重跑通过；此默认值调整尚未重新发布开发包或部署正式服务，上述138项日志对应调整前的发行包。
