# 2026-10-16 架构整改实施记录

基线：[整改建议](../20261016整改建议.md)。按用户要求，ARCH-001（P0 税务正确性、独立金额 Golden Dataset）最后处理；ARCH-002 同属 P0，仍优先推进。该排序不降低任何发布门槛。

升级与跨机恢复的边界见[维护规范](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)。第二批已接入Worker维护代码与隔离检查；跨机恢复仍为Proposed，完整发布门槛继续保留。

## 顺序与关闭条件

| 顺序 | 编号 | 当前状态与代码依据 | 下一步与不可替代的验收 |
| --- | --- | --- | --- |
| 1 | ARCH-003 | 本批代码与自动验收完成：移出全局锁；插件级查询准入、异常隔离、取消和维护排空 | 真实15秒查询、控制面2秒SLA、取消及重复维护回归通过；不代表所有数据库操作均已移出管理事务锁 |
| 2 | ARCH-009 | 本批文档与自动门禁完成：当前架构入口、30项目和直接依赖清单、循环检测 | 全解决方案构建、现有架构门禁及四种负例通过；仓库尚无独立托管CI，沿用Run-ReleaseAcceptance入口 |
| 3 | ARCH-002 | 第二批接入Worker独立维护日志、快照/暂存/切换/恢复入口及启动前风险保护，部分实现，未关闭 | 仍缺真实SCM/安装重装全生命周期、全部插件Schema迁移矩阵、硬断电/磁盘满及生产签名成功路径；不把隔离进程测试称为完整发布验收 |
| 4 | ARCH-005 | 未关闭。ServiceKey仍绑定LocalMachine DPAPI及服务身份 | 先接入服务身份内的恢复凭据封装与一致性备份，再于干净Windows验证。管理员直接运行ServiceKey.Open不能冒充原服务身份；禁止通过放松SID校验解决 |
| 5 | ARCH-004 | 当前第一方边界已明确：受信任同进程代码，不是沙箱；生产安装已有CMS发布者验证，运行时依赖管理员索引和包哈希 | 签名/证书轮换、发布链和依赖供应链验证未关闭；第三方独立进程与低权限身份另行评审 |
| 6 | ARCH-007 | 未关闭。现有来源政策、修订审核和原件保全继续沿用；不得把ETF份额变化当机构交易 | 权威完整交易日历、随机缺口和修订、连续20交易日及长期来源验收。不能在单轮合成测试中宣称完成20日验收 |
| 7 | ARCH-006 | 未关闭。已有封存、冻结结果读取、原件核对、同构建重放及输出比对 | 尚需能力分级与完整依赖/DTO兼容包、多历史版本试验；程序集摘要一致不等于完整历史环境已保全 |
| 8 | ARCH-008 | 未关闭。本批限制同插件查询在途数量；写作业仍用每插件SemaphoreSlim与Quartz全局4线程 | 多个同插件Job可能占据Quartz等待线程，不能把查询限额当调度公平性。仍需有界队列/非占线程等待、跨插件来源预算和积压压力验收 |
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
