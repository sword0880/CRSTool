# 国家队双库保全迁移与维护健康门槛

日期：2026-10-28。状态：独立维护入口、服务接线及隔离LocalService验证已完成；正式维护事务已启动，其最终状态须以文末现场回执为准。国家队研究库使用普通SQLite，收入/报税保险库及Worker管理存储的加密边界不变。

## 正式环境只读检查

本轮通过已安装Worker的公开查询及文件元数据检查取得记录，未停止服务、解开真实密钥或删除资料：

| 项目 | 当次观察 |
| --- | --- |
| 服务 | `CRSBackgroundService`，LocalService，Running，PID 40640 |
| 维护代次 | `f6aab6806f824ee3ac9ccef6ebfaeb66`，Committed，2026-10-26 09:39:09 北京时间 |
| 公开配置 | 7项计划、24只ETF配置、29条股票记录；记录数不代表完整持仓覆盖 |
| 两库文件 | `market.db` 83,390,464字节，WAL/SHM存在；`source-policy.db` 12,288字节 |
| 文件格式 | 两库都没有普通SQLite文件头，符合旧加密库的预期；仅凭文件头不证明密钥、Schema或内容有效 |
| 数据门槛 | 截至2026-10-27，24只活动ETF的250日历史及连续20日门槛均未通过；这不等于没有数据 |

文件检查时刻为2026-10-28 08:23:35北京时间。服务继续运行，文件大小随后可能变化。这些记录不是停机快照，不能直接用作迁移输入。回执位于`artifacts/national-team-installed-state-20261028/`的`state.json`、`storage-preflight.json`和`acceptance-captured-20261028.json`。

候选版本正常启动会拒绝旧加密库，不会用空库代替。因此，不能将源码构建或隔离包启动当作正式数据已迁移，也不能直接覆盖安装目录解决格式差异。

## 分层与复用

- `CRS.Common/IO/PhysicalPathGuard`提供逐级拒绝重解析点、悬空链接、备用数据流和越界相对路径的基础机制。既有`StagedDocumentStore`复用它。
- `CRS.Common/IO/AuthenticatedJsonFile`提供明确用途、32字节已有密钥、有界严格JSON、HMAC核对及原子替换；不生成密钥、不授权迁移，也不加密研究资料。
- `LegacyNationalTeamExport`拥有国家队两个固定库的离线解密导出、版本/身份检查及逻辑摘要。结构及逐行摘要包含`sqlite_sequence`，防止数据行相同而自增高水位丢失。
- `NationalTeamPlaintextMigration`及其`Activation`文件拥有国家队双库协调和恢复规则；固定操作只涉及`data/market.db`及`data/source-policy.db`，不接触收入/税务库。
- `WorkerUpgradeJournal`拥有通用维护启动与健康确认；Worker不直接引用国家队Jobs或加载其业务实现。

## 双库迁移协议

调用方必须先完成正式停机、全量快照及维护授权。当前组件只接受独立的活动插件根、快照插件根、非空操作ID及国家队专属密钥；它不能代替上述前置条件。

1. 整段持有插件目录`runner.lock`独占租约，正常Runner仍占用时拒绝迁移。
2. 用固定文件集合与摘要核对活动资料和快照。拒绝未知回滚日志，不忽略或清理未支持的`-journal`/superjournal。
3. 写入绑定操作ID、两个根目录和原始文件集合的认证日志，状态为`Preparing`。准备尝试号先持久化，最多八次；每次使用新的准备目录，保留旧现场。
4. 两库均在副本上解密/恢复WAL、检查完整性、外键、版本和逐行逻辑摘要。已有普通库也只检查工作副本，不进行无意义的格式重写。
5. 全组准备通过后写入`Prepared`。进入替换前再次核对整组，第二库有变化时不能提前替换第一库。
6. 将旧WAL/SHM和主库依次移动到本操作的`original`目录；再将已验证的新库移到活动路径。每一步均依据已认证日志和文件摘要恢复，保留全量快照及旧库。
7. 发布后再次核对逻辑摘要，最后写入`Completed`。同一操作完成后重放返回`AlreadyCompleted`；此时允许合法新增数据，不拿旧数据库覆盖新事实。

当前不提供任意路径、任意库或覆盖目标的入口。日志认证失败、快照改变、准备文件篡改或原始文件集合出现歧义时保留现场并拒绝继续。

导出临时文件采用同目录的短随机名称。较长目录专项曾暴露原生SQLite路径限制；修复工作文件命名后在原较长测试目录通过，没有通过缩短目录或放宽断言绕过问题。初次失败日志保留，不能计入通过项。

## 维护健康误判修复

旧组合根在宿主启动后直接确认维护健康，而单个插件故障可被Catalog隔离。这样宿主可用并不能证明国家队资料已成功打开。

Catalog现在记录经过验证的后台入口集合。仅在未完成的维护事务中，`MarkHealthy`要求插件状态无失败、无重复，并与应启动集合完全一致；失败继续保留`Starting`及`NewFactsPossible=true`，不能写入Healthy。普通启动仍保持单插件故障隔离，通过公开健康端点报告Degraded，不将其冒充Running。

## 第一阶段隔离证据

验证全部使用合成数据库、独立目录或测试自有进程，不读取真实服务密钥、不控制SCM：

- 迁移专项60项：双库、错误密钥、第二库失败不切换第一库、混合格式、目录占用、取消、日志/快照/目标篡改，以及八个独立中断点的同操作恢复。
- 实际强杀的合成子进程留下已提交WAL；在WAL归档而主库尚未替换处中断后，仍能恢复，冷却事实保留。自增高水位100在删除最高行后仍保留，迁移后新增ID为101。
- 较长目录最终专项回执：`artifacts/national-team-storage-migration-long-directory-verified-20261028/acceptance.json`；`Installed=false`、`FormalServiceMigrationIntegrated=false`。
- NationalTeam完整默认回归通过，其中公开存储33项及迁移60项均实际执行；日志`artifacts/national-team-migration-full-regression-20261028.log`。进程退出码0，并非只运行默认入口的一部分。
- 真实隔离Worker升级启动27项通过：包含Prepared拒绝启动、Switched写入风险后就绪、未Commit强杀重启拒绝、Committed重启、故障插件阻止Healthy，以及普通启动保留Degraded/失败诊断/查询可用。回执`artifacts/worker-plugin-health-verified-20261028.json`及`worker-plugin-health-verified-checks-20261028.log`。前次普通启动错误预期为Running的断言失败日志单独保留，修正为既有Degraded语义后复测通过，没有放宽生产健康标准。
- 共用文件暂存26项、文件入口权限13项、维护文件事务及PowerShell/C#启动互操作29项、维护故障恢复事务145项通过。通用机制回归不替代正式金融资料迁移。
- 全解决方案38项目Release构建零警告、零错误，日志`artifacts/national-team-migration-health-build-20261028.log`。架构/依赖图、功能目录和52份XAML静态门禁通过。

汇总回执为`artifacts/national-team-storage-migration-checkpoint-20261028.json`。两个CI工作流已登记迁移专项和共用Worker夹具准备脚本，远端CI本轮未执行。相关五份文档118个本地链接、脚本语法与`git diff --check`通过；不存在将CI配置修改写为远端执行成功的结论。

## 独立维护入口与正式服务接线

上述第一阶段之后，已增加以下生产源码：

| 所在层 | 职责 |
| --- | --- |
| Worker.Abstractions | `IBackgroundStorageMigrationEntry`及有限上下文/回执；不包含国家队库名或SQL |
| Worker.PluginHost | 普通入口与维护入口共用可信索引、完整包核验和依赖隔离；`IsolatedStorageMigration`拥有专用子进程、挑战绑定、限额及终止确认 |
| Worker.PluginRunner | `--maintenance`只有一次迁移及结束确认，不建立普通会话或开放通知/调度/查询能力 |
| Worker.Service | 读取发行计划，绑定维护代次与已封存快照，以实际服务身份打开既有密钥并只交付该插件的派生副本；不引用国家队Jobs |
| NationalTeam.Jobs | 清单中的`NationalTeamStorageMigrationEntry`识别唯一`national-team-plaintext-v1`，调用原双库保全组件 |

发布时必须显式选择`Publish-BackgroundService.ps1 -OutputDirectory <新目录> -MigrateNationalTeamStorage`，使`release.json`包含`storageMigrations`计划；默认发布及日常构建不增加迁移计划。只有明确的Switched代次通过全量快照核验并先写入Starting/新事实风险后，才执行维护入口。无维护日志、Committed或RolledBack时均不调用迁移。旧加密资料遇到普通启动仍拒绝重建。

迁移前重新验证发行内文件摘要、插件包和独立维护入口；插件根和信任索引必须属于本次发行。国家队专属密钥仅经父子进程私有句柄传递，主密钥留在服务，不进入命令行、环境变量、文件或管理员输出。维护入口独占`runner.lock`，普通Runner在它退出后才启动。

单项维护期限为1至600秒，最多四项、总预算不超过1200秒。该候选国家队计划为600秒；普通启动确认300秒另计，普通金融查询、采集期限及并发8保持原规则。取消或失控超时后，父进程回收Job Object并等待实际退出；原生导出不能逐行取消的部分也不能无限存活。结果未知、非零结束或缺失入口均不能确认完成。

所有维护入口完成后，服务以独立派生审计密钥写入有界认证回执，绑定发行、快照清单、操作ID、插件摘要和结果。维护日志记录回执文件摘要；`MarkHealthy`同时要求完整插件启动及回执未改变。尚未提交的重启继续要求显式恢复；完成的组件重放保留后续新增事实，不从旧快照覆盖数据库。

## 本轮维护链路验证

- 38项目Release零警告、零错误，日志`artifacts/storage-maintenance-runner-final-build-20261028.log`；首次HKDF重载调用错误的编译日志保留，修正后通过。
- 维护宿主24项：真实服务/私有Runner、正确派生密钥和操作ID、无代次不自动迁移、已提交不重跑、故障/缺入口拒绝、实际进入失控入口后的超时退出和租约回收、同代次重试及新事实保留。回执`artifacts/storage-maintenance-host-verified-20261028/acceptance.json`。
- 未经修改的完整候选经生产国家队维护入口、普通插件及真实客户端12项通过：两份合成旧加密库转普通SQLite，24条配置/两只活动ETF、份额、原件及旧执行记录保留，管理库继续加密，真实密钥文件摘要不变，同代次重试可回读。回执`artifacts/storage-maintenance-native-pipe-20261028/acceptance.json`。
- 真实LocalService/SCM隔离9项：由LocalService自己创建测试密钥及两份合成旧库；调用实际维护协调器，在候选健康后注入中断，验证新事实风险、拒绝旧库覆盖、同代次恢复至Committed、原库/快照/密钥摘要保留及生产客户端回读。测试服务已移除，正式服务PID/路径/状态未变。回执`.test_tmp-storage-scm/8bae4d42c7f84939adc8295fe18d6a94/acceptance.json`；这项证明服务身份与机制，不证明真实金融资料已迁移。
- 新接线后完整国家队默认回归通过，仍实际执行公开存储33项及迁移60项，日志`artifacts/storage-maintenance-national-team-regression-20261028.log`。原进程升级27项、PowerShell/C#维护互操作36项、事务故障矩阵145项及架构/功能目录门禁通过。
- 新增独立CI工作流登记生产入口和LocalService隔离验证；远端CI本轮未运行。完整开发候选为`artifacts/storage-maintenance-worker-20261028`，显式携带迁移计划，未签署生产发行签名。

## 正式维护与剩余边界

正式维护前再次保存公开接口状态与文件摘要，回执位于`artifacts/storage-maintenance-installed-20261028/before/`。当次服务仍为PID40640/Running，7项计划、24只ETF配置、29条股票记录；两个国家队库仍非普通SQLite，原程序160项发行文件通过核验。固定正式CLI已启动代次`73215e55abac4261875ec22a9f6bb86b`的保全维护；最终Committed、密钥和快照摘要、新接口回读须另行确认。

初始认证日志尚未成功发布而目录出现孤立临时文件、超出八次准备尝试或出现未支持回滚日志时，仍需显式核对，不能猜测清理或覆盖。普通重启不会替代未完成事务的人工恢复。

即使正式格式迁移完成，真实ETF连续覆盖、具名机构组合分母、行业/价格资料核验、历史时点、样本外验证及退出模型有效性仍须分别验收；不能由格式转换、合成SCM或新查询可用推导金融结论。

关联：[存储所有权及加密边界](NATIONAL_TEAM_ETF_STORAGE_BOUNDARY_20261026.md)、[重构总记录](NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)、[当前架构](../architecture/CURRENT_ARCHITECTURE.md)。
