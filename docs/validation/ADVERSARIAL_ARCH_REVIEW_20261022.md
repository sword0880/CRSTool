# CRSTool 反方架构核查与首批整改（2026-10-22）

> 基线：`main` 提交 `a8d802a79f3e13ff6203b5bb11017203fc1e7f5b`（2026-10-22）；本报告是源码与既有验证记录审查，**不等于 Windows 实机、故障注入、生产发行验收**。文中“已修改”仅指当前 PR 候选；需以 Actions 和独立复核确认。范围覆盖 Worker / Named Pipe / Quartz、插件、国家队证据采集、密钥与发布、税务及绩效。

## 1. 本次确认并修复的缺陷

### AR-20261022-01：任务快照缓存可被单一调用者耗尽（P1，已提交候选）

- **证据（源码 A）**：`src/CRS.Worker.Service/Runtime/BackgroundRuntime.Activity.cs` 的 `TaskActivityPage` 先为每个未完整翻页的请求分配缓存，旧逻辑使用全局 `activitySnapshots.Count >= 8` 直接返回 `SnapshotLimit`。快照为 1 分钟寿命，客户端重新打开、异常中断或只看第一页时无法释放；不需要调用修改权限即可耗尽 8 个槽位。
- **影响**：其他客户端看到 `SnapshotLimit`，无法刷新正在执行、等待和待执行任务状态；后台实际任务并未停止，但前台失去观测能力。故障可由普通操作触发，不应让用户误判任务不存在。
- **候选修复**：每个 SID 最多保留 2 个快照，超额时淘汰该 SID 最旧游标；全局 8 个额度满时淘汰最旧快照而非拒绝新的正常查询。被淘汰游标使用已有 `SnapshotExpired` 错误，客户端应重新从第一页读取。原任务、命令、执行证据和 SQLite schema 不变。
- **回归**：`tests/CRS.Tests.Background/ManualImmediateChecks.cs` 中模拟 12 次同一 SID 放弃分页、不同 SID 再读取、老游标过期、原客户端稳定分页；`.github/workflows/wpf-ui-design-checks.yml` 新增 `--manual-immediate` PR 门禁。
- **剩余权衡**：全局突发负载时，旧游标可能被逐出；这是显式可重试的观察一致性损失，优于静默阻断新请求。若未来多人多终端高并发，应该演进为按 SID 配额 + 客户端游标超时/重试指标，而不是扩大无界缓存。
- **验收条件**：Windows CI 编译与测试通过；验证两个并发客户端一个反复放弃分页、另一个正常刷新；过期游标不可返回混合快照；性能及内存预算无显著回退。

### AR-20261022-07：通知操作审计落盘失败未进入 Worker 存储故障状态（P1，已提交候选）

- **证据（源码 A）**：`src/CRS.Worker.Service/Runtime/BackgroundRuntime.Notifications.cs` 在通知设置保存/测试入口，操作完成后在 `finally` 调用 `repository.Audit`。旧路径在写入失败时异常上抛，但与一般任务命令路径不同，没有设置 `StorageFault` 和停调度，可能导致健康探针仍返回 `Running`。
- **影响**：客户端应把响应视为结果未知（通知或设置可能已经生效），但 Worker 不得继续以健康状态接受新的工作；否则审计链已经损坏而运行仍继续。
- **候选修复**：仅在审计持久化异常属于 `StorageError` 时调用现有 `TripFaultAsync("StorageFault")`；仍抛回原异常，使 IPC 对修改操作保持 `CommandOutcomeUnknown`，不自动重发邮件/Webhook。正常只读配置不改变审计语义。
- **回归**：`WorkerRemediationChecks.FaultsAsync` 使用两类异常（IOException 和模拟 DbException）注入通知测试操作审计失败，断言不返回伪成功、健康变 `StorageFault`、新 RunNow 拒绝。PR CI 增加 `--task-storage-boundary`。
- **验收边界**：本测试使用禁用邮件的隔离配置，不发送真实外部通知；需要独立验证真实发送后审计中断的“结果未知、不可自动重发”交互与人工补偿流程。

## 2. 仍开放的主要架构风险（本 PR 不贸然改动）

| 编号 | 等级 | 证据性质 | 风险/反方判断 | 正确整改及关闭条件 |
|---|---|---|---|---|
| AR-02 | P0 发布阻断 | D：`docs/KNOWN_ISSUES.md`、`docs/deployment/WORKER_UPGRADE_AND_RECOVERY.md` | Worker 数据代次/升级恢复、跨机器 ServiceKey 灾备、硬断电/磁盘满和真实 SCM 恢复未形成完整生产证据。自动测试通过不能证明灾备能恢复。 | 先做隔离机器端到端：正常升级、错误候选、断电/磁盘满中断、回滚、跨用户/跨机器恢复；核验密钥/数据一致性及恢复点。 |
| AR-03 | P1 架构 | A+D：`src/CRS.Worker.PluginHost/BackgroundPluginCatalog.cs` 和 `docs/architecture/ARCHITECTURE_REMEDIATION_PLAN_20261020.md` P1-07 | 后台插件依然运行于 Worker 进程；失控插件可能迫使整个 Worker 退出。程序集白名单、能力声明不是 OS 沙箱；未来若允许非内置插件，代码还可直接访问同进程授予的操作系统资源。 | 正式限制为签名且人工审核的内置插件；扩展到第三方之前使用低权限独立进程与最小能力 IPC；验证 CPU/内存/线程失控、强杀、重启重放的故障隔离。 |
| AR-04 | P1 监控可靠性 | D：`docs/KNOWN_ISSUES.md` 最新采集记录；A：`src/Plugins/CRS.Plugins.NationalTeam.Jobs/` | 2026-10-22 的取消修复解决失控退出，不等于 15 秒查询时延达标；官方来源临时故障、原件重新核验开销与跨插件额度公平性仍未完全闭环。UI 不能把缺失数据当作 0。 | 长达数千原件的真实数据 P95/P99 查询延迟监测、只读快照缓存/失效与证据核验分层设计；构建跨插件共享预算与长期来源模拟测试；保持损坏证据失败关闭。 |
| AR-05 | P0 金融发布门槛 | D：`docs/KNOWN_ISSUES.md`、`docs/architecture/ARCHITECTURE_REMEDIATION_PLAN_20261020.md` | 税务 FIFO/汇率/券商事实与绩效 TWR/XIRR 现有隔离回归，不等于真实跨年度税务底稿与独立金额对账全部通过。任何“正确可申报”或真实国家队减持结论都需要额外证据。 | 独立税务黄金数据集、费用/企业行动/同秒成交先后、不同账户与年度结转回放；绩效真实期初市值和转仓对账；国家队证据按披露者和份额边界分级，不做未经证实的归因。 |
| AR-06 | P1 工程治理 | A：`.github/workflows/wpf-ui-design-checks.yml` | 原 PR CI 聚焦 WPF、市场同步与少量 Worker 测试，但没有把 `--manual-immediate` 任务排队与快照场景作为常规门禁，局部回归可能在 UI 构建绿色时漏掉。 | 本 PR 接入任务快照和通知审计的故障注入测试；后续独立增设 Worker 安全、灾备故障注入与国家队真实样本只读 CI 阶段。避免在普通 PR 运行有破坏性的生产数据库测试。 |

证据等级：**A** = 直接阅读当前源码可复核；**D** = 当前仓库文档明确记录待验收（不是本轮独立验证）。没有把“可能”写成已发生事故。此前已修复的旧 Quartz、Core/Jobs/UI 所有权迁移和 2026-10-22 成交日志取消问题不重复立项。

## 3. 发布门禁和优先顺序

1. **先确保数据能恢复**：AR-02 与 AR-05 为生产版阻断，不以 CI 全绿代替签核。
2. **再压缩 Worker 故障域**：AR-03；未隔离进程前不得把能力标识当作权限边界，也不开放任意第三方 DLL。
3. **保证采集与观测可信**：AR-04 和此次 AR-01；请求超时不混为任务中断，陈旧快照/空响应/未知值不可显示成成功或零。
4. **最后形成重复验证的门禁**：AR-06；测试应检查语义和异常恢复，而不只检查截图、编译。

## 4. 合并前验证

```powershell
dotnet restore CRS.sln
dotnet build CRS.sln -c Release --no-restore -m:1
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --manual-immediate
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --task-storage-boundary
./tests/Verify-Architecture.ps1
```

**本报告创建时，尚未核对当前 PR 的 Windows CI 成功状态，也没有在已安装的 Worker 或真实个人数据上测试。** 不修改加密库、ServiceKey、旧任务历史和任何正式采集数据，不自动合并 `main`。