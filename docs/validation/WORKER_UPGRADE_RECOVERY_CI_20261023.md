# Worker 升级与恢复自动验收补强（2026-10-23）

## 目的

当前维护事务已有独立的 C# 启动日志门禁、PowerShell 文件事务与管理员 SCM 测试，但原有常规 PR 工作流没有自动执行维护模块和完整事务协调器的故障矩阵。本专项增加独立 Windows CI 工作流，降低后续升级改动绕过恢复测试的风险。

## 新增 CI 覆盖

工作流：`.github/workflows/worker-upgrade-recovery-checks.yml`

1. 构建 `tests/CRS.Tests.Background`，在 Windows 环境验证真实 .NET Worker 依赖。
2. 执行 `tests/Test-WorkerMaintenance.ps1 -CheckStartupInterop`：合成发行包校验、路径/链接拒绝、数据快照及 SHA256、人工原件保全、目录切换中断恢复，并验证 PowerShell 维护日志能被 C# 启动门禁读取。
3. 执行 `tests/Test-WorkerUpgradeTransactions.ps1`：通过合成包与模拟 SCM 操作调用正式维护事务协调器，覆盖候选包哈希失败、兼容版本声明错误、Prepared/Stopped/BackupCopied/BackedUp/Staged/PreviousMoved/CandidateMoved/Switched/CandidateHealthy 等阶段中断、候选启动失败、健康探针失败、篡改备份拒绝恢复、禁止覆盖可能含新事实的数据、RetryCandidate 和正常 Commit。核对无新事实回退时数据库、机器密钥文件和来源原件不变。
4. 准备位于 `RUNNER_TEMP` 的合成 Worker 进程夹具，执行 `--upgrade-only`：验证真实 Worker 进程、SQLCipher/DPAPI、Named Pipe 健康响应与启动日志门禁的交互。

## 安全边界

- 测试仅使用随机唯一临时目录、合成发行清单和合成数据；不创建 Windows 服务，不访问或修改已安装的 `CRSBackgroundService`，不读取正式 ProgramData 数据。
- CI 里的 `release.json` 是专供 C# 日志门禁测试的合成文件，**不是签名发行包**，不证明 CMS 发布者信任链、生产安装包或实际版本兼容。
- 模拟服务回调覆盖维护事务状态机，不等于真实 SCM 的服务身份、ACL、启动策略、服务故障恢复行为已全部验收。
- 此工作流必须以 Windows Actions 结果为准；代码/文档已提交不代表测试已通过。

## 增量整改：恢复旧代次前核验活动数据（候选实现）

源码复核确认原来的 `Restore-WorkerPrevious` 仅检查维护日志中的 `NewFactsPossible=false` 和程序发行哈希，**不会比较已经备份的活动数据与快照**。候选可能还没启动，但维护后发生磁盘损坏、文件被误删、恶意或非预期修改时，旧程序仍可能被切换回来。该情形不能仅凭日志阶段证明恢复安全。

本次在 `tools/background/WorkerMaintenance.psm1` 增加 `Assert-WorkerLiveDataMatchesSnapshot`：当快照清单哈希已经持久化时，先核验快照清单与所有快照文件 SHA256，再将当前业务数据文件集合、文件数量、每个文件完整 SHA256 与备份逐一比对；忽略本次维护元数据、`owner.lock` 与 `maintenance.lock`。任意文件新增、删除或变化均失败关闭并保留当前程序及数据现场；不复制快照回活动库，不自动覆盖数据库。没有备份的最初 `Prepared` 阶段仍沿原规则恢复，因候选尚未切换且快照未完成，不冒充有已核验备份。

在 `tests/Test-WorkerUpgradeTransactions.ps1` 新增三组可重复故障注入：`Switched` 后候选尚未启动时数据库被追加、人工审核原件被删除、目录多出未登记原件；都必须拒绝旧代次恢复、保持服务关闭、保留已切换的候选程序；人工复原数据与快照一致后才允许恢复旧程序。该测试是合成模拟，不替代真实 Windows 文件系统断电或磁盘满演练。

## 仍然开放的 P0 发布门槛

1. 管理员隔离测试 `tests/Test-WorkerScmUpgrade.ps1` 在干净 Windows 主机上的完整执行与日志留档；它必须只创建唯一命名的测试服务，不能改变正式服务。
2. 安装、升级、卸载保留数据、同一发行重装的完整生命周期。
3. 真实旧/新发行与全部插件数据库 Schema 的兼容矩阵；当前 SCM 夹具使用相同二进制构建、不同合成版本清单，不能当作 Schema 升级证据。
4. 硬断电、系统重启、磁盘满、快照文件缺失/额外文件、发布签名成功路径及失败路径的实机故障注入。
5. ServiceKey 当前依赖 LocalMachine DPAPI 和服务身份；独立恢复凭据、跨机器恢复包、干净机器解密与完整事实校验仍未完成。
6. 候选仍在原数据目录打开数据，不具备独立候选数据代次/只读健康模式。若日志不能证明没有新事实，必须继续拒绝旧库回退，不能以自动恢复便利性牺牲数据完整性。

## 合并条件

- 新增 Windows CI 的构建、PowerShell 事务矩阵、C# 启动互操作及真实隔离 Worker 进程步骤全部成功。
- 若失败，先修复测试或生产代码，禁止跳过失败步骤、放宽门禁或把未执行的步骤记为通过。
- 本专项只提高可重复的自动验收覆盖，不关闭上述实机发布门槛。
