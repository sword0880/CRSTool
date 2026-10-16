# Worker 升级与恢复约束

状态：部分实现（2026-10-16）。ARCH-002已接入离线升级/恢复脚本及Worker启动保护，完整SCM/生产签名/断电矩阵仍未关闭；ARCH-005跨机恢复部分仍为Proposed。代码与证据见[当前架构](../architecture/CURRENT_ARCHITECTURE.md)及[整改记录](../implementation/ARCH_REMEDIATION_20261016.md)。

## 当前可执行边界

`tools/background/Install-Service.ps1`只安装到不存在的程序目录，并拒绝覆盖已有服务。正式包使用外部指定的发布者指纹验证release.json的CMS分离签名，再验证所有登记文件；开发模式必须显式指定AllowDevelopmentPackage。卸载保留数据不等于支持任意二进制重装或回退。

已接入的升级入口为`tools/background/Upgrade-Service.ps1`，恢复入口为`Recover-Service.ps1`，共享`WorkerUpgradeTransaction.psm1`和`WorkerMaintenance.psm1`。入口与模块随新发行包提供；维护入口需要PowerShell7和管理员，固定服务名称、ProgramFiles程序路径、ProgramData数据路径及LocalService身份，不能自定义正式目标目录。正式维护使用外部指定发布者验证CMS，开发包必须显式豁免；生产版本须递增，候选还必须声明maintenanceFormat=1及允许的upgradeFromVersions。安装、升级与恢复共用发行包验证，拒绝越界/链接/备用数据流、重复目标、未登记文件及哈希变化。

实际顺序：独占maintenance.lock → 持久Prepared → 禁用SCM自动启动 → 停止服务 → 独占owner.lock → 整棵数据快照及逐文件SHA256核验 → 同级目录暂存并复核包 → 原程序改名保留、候选移入固定活动目录 → Switched → 恢复延迟自动启动并启动候选 → Worker先核验原件/库/密钥备份哈希，再持久Starting/NewFactsPossible=true → 打开密钥/数据库和插件 → Healthy → 管理脚本验证应用健康并显式Committed。

维护日志位于`maintenance/upgrade.json`，快照及清单位于`maintenance/<事务ID>/snapshot`和`snapshot.json`；包含数据库、WAL/SHM、来源原件、审核及配置文件，排除运行租约、维护租约和维护目录。所有内容留在原有私有ACL树内。旧程序位于固定程序目录同级的`.previous-<事务ID>`目录；失败候选以`.failed-<事务ID>`保留，脚本不递归删除任何代次。下一次维护归档已结束日志，禁止覆盖未解决事务。

**当前实现使用原数据目录执行候选迁移，不是候选数据代次副本。** 因此只要候选准备打开数据，就保守视为可能有新事实，不提供候选启动后的旧库自动恢复。任务/插件自身迁移仍须事务化；日志中的Healthy也不代表已经Commit。备份的逐文件验证证明复制完整，不代表备份已完成跨机解密或全部业务Schema独立验收。

恢复操作（先查看Status，按日志选择）：

```powershell
./Recover-Service.ps1 -Action Status
# 继续同一个候选，保留现有全部数据及NewFactsPossible标记；必须先排除原故障。
./Recover-Service.ps1 -Action RetryCandidate -TrustedPublisherThumbprint '<已核验发布者指纹>'
# 仅NewFactsPossible=false时允许，核验原发行后只恢复程序，绝不覆盖现有数据库。
./Recover-Service.ps1 -Action RestorePrevious -TrustedPublisherThumbprint '<已核验发布者指纹>'
# 已Committed或RolledBack，但启动失败：重新校验已确认发行并恢复服务启动。
./Recover-Service.ps1 -Action ResumeConfirmed -TrustedPublisherThumbprint '<已核验发布者指纹>'
```

开发验收可显式用`-AllowDevelopmentPackage`代替生产签名参数。任何失败保持禁用/维护状态并保留现场，不自动覆盖旧库。RetryCandidate能处理第一次目录改名后活动目录暂时不存在的情况；仍不能修复任意业务Schema或替换为另一份候选发行。需要兼容回迁、增量导出或修复包时保持NeedsManualRepair。

RestorePrevious在候选移往failed之后或旧程序移回活动目录之后中断，均可核验目录指纹后重试；模糊目录组合必须拒绝。ResumeConfirmed仅用于结束事务，完整包和发行指纹必须与日志一致，不能借此启动另一发行或覆盖数据库。SCM操作还核验实际服务程序路径、LocalService身份及需要时的自动启动方式。

未解决事务不能卸载或以重装绕过。已结束事务允许卸载保留数据，但重装必须使用日志记录的相同已确认发行指纹；换版本必须走升级入口。新安装保留CMS签名文件，后续维护才能重新校验同一发行来源。

已有数据必须保留service-key.dpapi、全部数据库及WAL/SHM、plugins原始来源和人工审核证据。服务未停止或无法取得独占owner.lock时，普通文件拷贝不构成一致性备份。任务库的Snapshot/Schema1迁移快照仅覆盖任务库，不覆盖ETF市场库、来源策略库、原件及发行清单。桌面PluginHost的UpgradeCoordinator未接到Worker启动路径，不能借此宣称Worker升级已闭环。

出现迁移失败、密钥缺失或版本不兼容时，保留现场，停止任务与服务；不得删除密钥后重建，不得用旧快照覆盖包含新审核/修订事实的库。可重新采集的公开份额不能替代已经保存的来源原件及人工结论。

## 后续完整状态机目标

下表保留完整目标，不能与上面的当前顺序混同。当前尚未实现独立候选数据代次、禁止业务写入的候选健康模式及Schema完整兼容矩阵；现以更保守的访问即标记风险保护数据。

| 阶段 | 进入前必须满足 | 故障后的安全行为 |
| --- | --- | --- |
| Prepare | 安装版本明确；兼容矩阵、发布者、包文件、磁盘容量均通过 | 未改变活动文件，记录拒绝原因 |
| Stop | 控制面进入维护，停止新接收；有界取消/排空全部查询与作业 | 超时则保留旧代次，不能复制仍在写入的库 |
| Backup | 独占数据租约；一致性冻结全部库与原件；记录Schema、插件及应用版本、大小和SHA256 | 缺任何必须文件则不得继续 |
| Stage | 旁路不可变程序目录和候选数据代次；原代次只读保留 | 不能在服务运行时覆盖执行文件 |
| Validate | SQLCipher只读完整性、密钥可访问、所有文件哈希、迁移前置和兼容计划 | 验证失败不启动候选写作业 |
| Switch | 先持久记录Switch意图，再原子替换代次指针 | 重启识别日志与指针两种可能状态，禁止猜测 |
| HealthCheck | 候选先以禁止业务写入方式启动；IPC、所有库、Quartz重建和核心查询就绪 | 无新事实且兼容时受控回退；其他情况进入NeedsManualRepair |
| Commit | 健康验证通过；显式确认代次并保留恢复材料 | 此后任何新事实写入先持久标记，再执行领域事务 |

本方案先Stop再Backup，以整棵后台数据的一致性为前提；若未来改为在线备份，必须提供跨库/原件一致性协议，不能只调换步骤名称。

状态记录必须包含唯一升级ID、前后发行清单哈希、插件版本/契约、数据库代次/Schema、备份文件清单及哈希、阶段、阶段时间、NewFactsPossible和恢复结果。阶段写入须Flush并原子提交；若崩溃无法证明无新写入，按已有新事实处理。恢复入口必须持有与服务相同的数据租约，幂等重试不覆盖未解决日志。

## 跨机器恢复包

恢复包必须独立于个人税务保险库；需要服务身份内显式授权的导出流程。不能为使管理员命令能读取原服务密钥而移除ServiceKey的SID检查。

包中包含一致性数据库/原件、完整应用及插件清单、Schema和逐文件哈希、格式版本、由独立用户恢复凭据保护的可迁移后台主密钥。采用经评审的有认证加密及有界解析，主密钥、恢复凭据均不得出现在命令参数、配置、诊断或明文临时文件中；目标运行时解封后按目标机器DPAPI及实际服务身份重新封装。原机DPAPI密文不能充当可迁移密钥材料。

恢复到全新受限目录，先验证认证、文件完整性、Schema和发行兼容，再打开SQLCipher只读核验全部库；全部通过后才允许切换活动代次及启动调度。错误凭据、篡改、缺失文件、目标非空、目标链接、超大包或未知版本必须保持原数据不变。备份目的地与恢复凭据分别保管。

## 必须留下的验收证据

- 在每个阶段持久化前后强杀；重启只恢复日志声明的代次，或明确进入维护。
- Schema迁移前/中/后、首次新事实事务前/中/后失败；不静默丢弃人工审核、修订或来源原件。
- 无新事实回退同时核对程序集、插件、所有Schema和文件哈希；不能只看SCM Running。
- 错误恢复凭据、篡改包、缺文件、未知Schema、非空目标和目录链接均拒绝且原库不变。
- 在干净Windows主机、不同DPAPI状态及实际LocalService/SID下恢复，查询原有事实与审核证据，再验证调度。

已完成隔离文件事务、真实SQLCipher/DPAPI及测试自有Worker进程的阶段阻断/强杀检查。`tests/Test-WorkerScmUpgrade.ps1`另外用唯一命名LocalService测试服务复用生产事务模块，10项检查覆盖切换失败恢复、成功提交、新心跳事实后拒绝回退、RetryCandidate、ResumeConfirmed和篡改指纹拒绝；测试服务已移除，正式服务状态/路径/进程未改变。两份测试发行来自同一构建，仅版本清单不同，不能当作真实旧新Schema升级证据。安装/卸载/重装全生命周期、全部真实插件库迁移、硬断电/磁盘满和生产签名成功路径尚未完成，ARCH-002仍未关闭；ARCH-005跨机恢复包和干净机器演练也未完成。
