# Worker 取消边界与多任务并发验证

2026-10-23。对应 [反方审查 F01—F04](../validation/WORKER_ADVERSARIAL_REVIEW_20261023.md)。

## 修复行为

- **F01**：取消待执行命令仍直接完成为 Cancelled；运行中的命令先持久记录 CancellationRequested，保持 Running，直到插件实际结果落盘。RetryDeferred 不能覆盖已记录的取消意图，仓储结果、退避和回执在同一事务提交。取消收尾期间进程消失，重启将执行及原请求保留为 Interrupted，禁止自动重放该请求；Interrupted 不代表业务没有生效。
- **F02**：ExecutionCancellation 独立记录宿主可观察的取消信号，以 CancelAsync 异步执行插件回调。取消、维护、故障、执行期限和调度器取消共用这一入口；runtime gate 内不等待插件回调。回调异常只记录分类，回调或执行主体超过两秒收尾期限会进入 UnresponsivePlugin 并请求安全停止。执行结束时先封闭计时器和父令牌的取消入口，再排空已有回调，避免迟到回调访问已完成会话。仍在运行的主体或回调结束前不释放执行资源；正式宿主保留停止超时终止进程的保护。
- **F03**：取消收尾完成后采用插件实际结果。返回 Succeeded 则历史和命令保持成功；真正取消保持 Cancelled；已结束的 TimeoutException 是 Failed，不误判为仍在运行的失控任务。已触发安全停止的宿主不会因插件迟到成功恢复健康。
- **F04**：GetHealth 在任务仓储读失败时仍返回 Code=Ok、Health.State=StorageFault；无法取得调度信息时 Scheduling=null，不能补零。故障锁存后不继续读取任务仓储。存储暂时恢复不会自动放开采集，仍需修复并重启核验。

上述命令状态使用现有 command_request 结构，未改变 Schema 版本、公开契约构造签名或 IPC 版本。

## 多个不同任务同时触发

全局默认 MaxConcurrency=8。不同插件可以同时执行；同一插件多个 Job 共用插件会话，默认最大并发为 1。只有插件显式实现 IConcurrentBackgroundSession，且声明的任务、连接、进度回调均支持并发时，才允许受限并行。插件额度仍受全局并发一半的上限约束，默认全局为 8 时单插件最多占 4 个名额。

上述取消边界修复阶段，NationalTeamBackgroundSession 尚未声明并行能力。后续经用户同意，已实施执行组、执行状态隔离和来源并发，当前源码策略见 [NationalTeam 分组并发整改](NATIONAL_TEAM_CONCURRENCY_20261023.md)；下述验证保留为该取消修复阶段的历史证据。

真实 Quartz 与可加载插件夹具验证：

1. 8 个不同插件的任务同时产生 Running 执行，健康快照显示 Running=8，8 个插件各有一个执行。
2. 第 9 个任务先持久接受并等待，不突破全局上限；释放前序任务后，9 个任务最终全部成功，待执行项归零。
3. 一个慢插件加多个插件内等待任务，不占满全局 Quartz 名额；另一个插件可执行。
4. 同插件默认串行，明确声明线程安全的插件两个不同 Job 实际重叠执行。
5. 重复触发合并、有界等待、暂停清理、原请求重放和延期参数恢复继续保留。

## 验证入口

`tests/CRS.Tests.Background/WorkerCancellationChecks.cs` 已接入完整后台回归，也可使用 `--cancellation-boundaries` 单独运行。`--scheduling-only` 已增加默认 8 个全局名额与第 9 个任务的真实执行验证。

本轮证据保存在 `artifacts/worker-adversarial-review-20261023/`。`artifacts/worker-cancellation-isolated-final-20261023/` 是仅含心跳插件的最终宿主可靠性测试包，不用于替换正式 NationalTeam 服务；其中 Service 程序集摘要与最终 Release 构建一致。

子进程测试统一在 30 秒启动期限内等待只读健康端点，只重试尚未连接的 ServiceUnavailable。原来的测试在创建进程后只发起一次连接，可能将客户端的三秒连接期限误当成进程启动期限；产品客户端的连接与查询期限未改变。

最终验证结果：

| 检查 | 结果及证据 |
| --- | --- |
| Release 后台构建 | 0 警告、0 错误；`fix-build.log` |
| Debug 后台构建 | 0 警告、0 错误；`fix-debug-build.log` |
| 最终完整后台回归 | 退出码 0，共 240 条 PASS；`fix-full-background.log` |
| 新增取消边界 | 完整回归内 23 条 PASS，含取消/暂停与延期结果竞态、回调重入/异常/阻塞、迟到取消封闭、收尾成功与失败、持续存储读故障、取消后重启不重放 |
| 调度与当前任务 | 完整回归内 36 条调度 PASS、13 条手动立即执行 PASS；包含默认 8 并行和第 9 个等待 |
| 仓储事务边界 | 36 条 PASS，含真实仓储事务回滚与宿主失败关闭；`fix-storage-boundary.log` |
| 架构门禁 | 项目图、依赖方向、后台无 WPF/私人税务依赖、仓储端口边界通过；`fix-architecture.log` |
| 差异检查 | `git diff --check` 通过 |

正式 Worker 的替换与安装验收不属于上述源码和隔离测试证据。本轮没有更换正式服务或触发正式采集。
