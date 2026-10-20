# NationalTeam / Worker 架构边界专项实施记录（2026-10-20）

> 状态：代码已提交到独立评审分支，**未完成 Windows Release 构建与动态回归**，不可将本记录作为正式验收通过证明。  
> 分支：`refactor/nationalteam-worker-boundaries-20261020`  
> 需求来源：[架构整改计划](../architecture/ARCHITECTURE_REMEDIATION_PLAN.md) ARCH-201～204。  
> 代码基线：`987cb8834d74e028256341beb0b25f2db68ed988`。

## 已实施内容及证据状态

| 专项 | 源码变更 | 测试/待核验 |
| --- | --- | --- |
| ARCH-201 | 将原 `CRS.Plugins.NationalTeamWatch/Domain/*.cs` 与 Core 依赖的 Application 纯契约/模型移动至 `CRS.Plugins.NationalTeam.Core`；其余业务 Application、Infrastructure 移至 `CRS.Plugins.NationalTeam.Jobs`。总计 40 个 `.cs` 文件实体迁移；三项目文件移除跨目录 `Compile Include` / `Compile Remove`。保留原命名空间和业务实现，降低序列化/程序集解析以外的变更风险。 | 必须运行全部项目构建、双宿主签名打包、ETF 服务化/页面回归；检查任何脚本、文档或测试是否仍引用旧源码绝对路径。 |
| ARCH-202 | 删除桌面 `QuartzTaskScheduler.cs`、`TaskPlanStore.cs`、旧 `Scheduling.cs` 接口与依赖旧实现的测试，删除 `PluginContext` 的计划网关、`WorkerOwnedSchedules` 占位实现。保留 `CronEditor`（仅生成/解析/预览 Quartz Cron，不参与实际调度）。后台 Worker 仍为计划唯一权威。 | 执行桌面插件宿主、Worker 调度、Cron 编辑器回归；旧契约的外部插件兼容性必须单独确认。被删除的旧 Quartz 运行行为由 Worker 端既有专门测试覆盖，不以删除测试替代验证。 |
| ARCH-203 | `BackgroundPluginCatalog` 新增独立查询注册表；会话 Start/DescribeJobs 成功后注册，查询不再从 `Jobs` 查找；隔离、初始化失败和 Dispose 时清理。新增 `QueryOnlyEntry` 零 Job 夹具，以及加载、查询、隔离回归测试。 | 执行 `CRS.Tests.Background`；核验零任务、单任务、多能力、失败隔离，以及真实 IPC 查询；本轮没有在 Windows 执行。 |
| ARCH-204 | `MarketRepository.ValidateEvidence` 改为纯校验、遇损坏原件立即拒绝返回金融结果；`ReconcileEvidenceIntegrity` 单独扫描、对状态变化执行事务性的 Q15 与完整性事件更新，幂等记录损坏和恢复。`NationalTeamBackgroundSession` 增设定时 `evidence-integrity` 作业，并在已有 `alert-evaluate` 作业评价前协调完整性。更新 `ScopedEvidenceChecks` 中损坏、恢复和日历受控写入测试。 | 必须验证损坏时 UI/IPC 显示明确错误、不显示旧可信指标；原件恢复后在完整性作业下一轮可重新查询；事件只新增一次；重复作业不制造写放大；数据库事务中断不产生半状态。需在 SQLCipher 真实加密库和服务调度下测试。 |

## 风险与验收边界

1. **读请求变为失败关闭**：损坏原件在后台完整性任务处理前，读请求直接拒绝而不再“查询过程中修复/持久化 Q15”。这是有意加强读写隔离的行为变化，客户端错误提示与是否需要单独只读完整性状态接口必须在真实 WPF/IPC 测试中确认。完整性作业的完成不可依赖某次前台查询。
2. **作业注册顺序**：新 `evidence-integrity` 作业设置为启用且可在 Worker 启动后运行；现有 `alert-evaluate` 仍每 5 分钟执行并先协调完整性，不增加另一个调度器。核验故障退避、互斥、重启与任务清单兼容性。
3. **序列化与升级**：维持原 namespace、插件 ID、Job ID（既有任务不变）、市场库 Schema、Windows 服务身份、SQLCipher 密钥与 IPC Host 协议。新增稳定 Job ID：`evidence-integrity`。不得将程序集源码移动等同于数据迁移。
4. **测试约束**：本环境无法取得/执行完整仓库 Windows .NET 10 Release 构建。代码修改属于待审查提案；并未执行完整测试，不应合并/部署到正式服务。
5. **文档状态**：本文件记录整改分支候选实现。验收通过后再更新 [当前问题清单](../KNOWN_ISSUES.md) 为关闭，并在此补充实机命令/退出码、测试输出和提交 SHA。

## Windows 端验收清单

- [ ] 从最新 main 合并/变基后检查 40 个文件归属、命名空间及所有包工程依赖
- [ ] `dotnet restore CRS.sln --locked-mode`、`dotnet build CRS.sln -c Release --no-restore`
- [ ] 执行 `tests/Verify-Architecture.ps1` 与 `tests/Verify-ProjectGraph.ps1`
- [ ] 执行 `CRS.Tests.PluginHost`、`CRS.Tests.Background`、`CRS.Tests.NationalTeamWatch`、`CRS.Tests.WorkerEtf` 专项与相关发行回归
- [ ] 从真实 LocalService 启动 Worker；校验新任务正常执行、跨会话查询、重启不重复写完整性事件
- [ ] 人工损坏/锁定原件并测试 UI 显示、Q15 写入入口、恢复事件和重复运行幂等
- [ ] 检查插件包哈希、旧/新 DTO 兼容、旧桌面计划能力的拒绝/弃用策略
- [ ] 通过后补充自动化测试输出/签认记录；否则保留待整改状态

## 回滚与发布纪律

本分支暂不修改正式数据库 Schema、密钥封装或服务 SID。需要回退代码时，必须先停服务并按已存在的维护流程备份/核对事实，不能让旧二进制自动回写新事实；对于任何发行升级，仍按 [Worker 升级与恢复规范](../deployment/WORKER_UPGRADE_AND_RECOVERY.md) 单独验收。