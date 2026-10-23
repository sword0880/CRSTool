# CRS.Worker.Service 后台任务反方审查

审查日期：2026-10-23，北京时间。范围包括任务调度、持久命令、取消、故障健康查询及正式 Worker 的只读状态。本轮没有修改产品代码、正式服务、业务数据库或计划，也没有触发正式采集。

后续源码修复及多任务并发验证见 [Worker 取消边界与多任务并发验证](../implementation/WORKER_CANCELLATION_20261023.md)。下文保留修复前的审查证据，不代表已安装服务完成修复。

结论：当前源码存在 4 项经隔离复现的问题，其中 3 项建议按 P1 处理，1 项为 P2。隔离复现使用当前源码构建的真实 BackgroundRuntime、Quartz 和加密任务仓储；插件行为及存储故障由合成夹具控制。这些结果证明宿主边界缺陷，不证明正式服务已经发生同样的事故。

## F01 / P1：已接受取消的手动请求可以重新进入重试

触发时序：插件已返回 RetryDeferred，执行器等待全局门闩提交结果；CancelRun 先取得门闩并返回 Accepted；随后执行器提交 RetryDeferred。

- `src/CRS.Worker.Service/Runtime/BackgroundRuntime.cs:311` 的 CancelAsync 取消待执行命令并向运行任务发送取消信号，但没有持久记录运行中命令的取消意图。
- `src/CRS.Worker.Service/Persistence/SqlCipher/EncryptedTaskRepository.cs:340` 的 CancelPending 只处理 Pending，运行中的原命令仍为 Running。
- `src/CRS.Worker.Service/Persistence/SqlCipher/EncryptedTaskRepository.cs:187` 无条件将 RetryDeferred 的原命令重新置为 Pending；`BackgroundRuntime.cs:386` 随即协调恢复。

隔离结果：取消响应为 Accepted，原请求最后为 `State=Pending, Code=RetryDeferred`，待执行数为 1。夹具任务处于禁用计划，仍会经手动命令恢复路径重新调度。

影响：用户取消的历史采集或其他参数化操作可能再次执行。应将运行中命令的取消意图持久化，并在结果提交事务与恢复路径共同检查，防止 RetryDeferred 覆盖取消。

## F02 / P1：在全局门闩内同步取消插件，可卡住整个控制面

`BackgroundRuntime.cs:317` 的 `source.Cancel()` 在 HandleAsync 持有全局 gate 时执行；故障处理 `BackgroundRuntime.cs:41` 和维护 `BackgroundRuntime.cs:407` 也在门闩内执行取消。CancellationToken 的回调会同步运行，插件回调若等待需要该门闩的工作，就会形成锁等待环。

隔离夹具让取消回调等待同宿主的 GetHealth。观察到 CancelRun 和另一条 GetHealth 同时阻塞；测试通过独立逃生信号释放回调后才恢复。测试结束时没有保留阻塞工作。该夹具验证的是允许的插件取消回调边界，尚未证明现有 NationalTeam 插件具有同一回调。

影响：一个插件的清理回调可以拖住健康查询、其他任务进度与管理操作；执行超时保护本身也要取得 gate，无法可靠解围。应在门闩内登记和截取取消对象，在门闩外触发回调，并处理回调异常及执行期限。

## F03 / P1：取消宽限期内返回的成功结果被丢弃

`BackgroundRuntime.cs:357` 等待任务时收到取消后，`BackgroundRuntime.cs:360` 继续等待插件最多两秒，但忽略其返回值，最终在第 370 行统一设置 Cancelled。

隔离夹具模拟插件在取消收尾阶段完成业务操作并返回 Succeeded；宿主持久执行历史与原命令回执均被记为 Cancelled。当前 API 没有保证取消会回滚插件已经提交的业务事务，宿主不能由令牌状态推导业务没有生效。

影响：成功提交的操作可能被界面显示为“已取消”，用户随后重新发起操作时会形成误导。应保留宽限期内取得的实际结果；对于无法确认是否生效的情况，表达结果不确定，并依据命令 ID 和业务幂等记录核验。

## F04 / P2：任务仓储不可读时，健康查询丢失结构化故障信息

`BackgroundRuntime.cs:204` 在非故障分支读取 Plans；即使 fault 已置位，`BackgroundRuntime.Scheduling.cs:18` 的 SchedulingSnapshot 仍必读 PendingRuns。若任务仓储持续不可读，GetHealth 会再次抛存储异常，并由 HandleAsync 返回只有 `Code=StorageFault`、`Health=null` 的响应。

隔离结果与上述一致。桌面的 `src/CRS.Desktop.Wpf/ViewModels/BackgroundServiceViewModel.cs:107` 只从 Health.State 取状态，此时会得到 Unknown，失去 StorageFault 对应的明确恢复提示。既有文档所述“健康查询不再依赖故障仓储”在当前实现中不完全成立。

应让故障健康响应依赖内存故障状态；不可读取的调度字段表达为未知，不填零。此项与 StorageFault 需要修复后重启的既有策略分开：本轮不建议自动清除存储故障继续写入。

## 验证结果与正式服务观察

- 现有 `CRS.Tests.Background --scheduling-only` 通过：调度主检查 25 项，另含手动立即执行检查；合计日志中 38 条 PASS。它们未覆盖本轮上述边界。
- 本轮隔离 Probe 构建成功，0 警告、0 错误；复现日志记录 F01—F04，并记录存储恢复后故障仍保持锁存的既有行为。
- 初次构建受编译器管道权限影响；重跑成功。一次重叠构建产生测试输出文件占用，不作为产品问题。最终 Probe 使用关闭共享编译器的串行构建。
- 正式只读接口在沙箱内返回 UnauthorizedAccessException；提升只读访问后核验成功，没有改变正式运行状态。
- 正式服务在 **2026-10-23 09:31:36 +08:00** 返回 Running，PID 35372，配置最大并发 8，运行、等待及待执行手动命令均为 0。两个插件均为 Started，当前任务快照也为空。
- SSE 来源当时处于 Cooldown，冷却期限为 **2026-10-23 12:32:24 +08:00**；SZSE 为 Ready。来源冷却本身不等于宿主故障。
- 最近成交统计执行 `c6dee0cef3cd4f1aa894bbc0aa5eb995` 为 RetryDeferred，检查点记录 `httpStarted=8, sourceIssues=2, rows=6, hasMore=True`，下次重试时间为 09:33:30。该次采集未完成，不能因宿主 Running 就报告采集成功，也不能仅凭 HttpRequest 分类认定官方接口故障。
- 已安装 Service 与 PluginHost 的文件摘要均与本轮源码构建产物不同。上述代码复现不可直接当作已安装二进制的缺陷验收。

复现材料位于工作区忽略目录 `artifacts/worker-adversarial-review-20261023/`：

| 文件 | 用途 |
| --- | --- |
| `Program.cs`、`Probe.csproj` | 合成插件时序、故障注入及真实宿主/仓储复现 |
| `probe-build.log` | 最终 Probe 构建结果 |
| `probe-results.jsonl`、`probe-repeat-1.jsonl` 至 `probe-repeat-3.jsonl` | 边界问题复现及重复验证 |
| `existing-scheduling.log` | 现有调度专项结果 |
| `installed-probe.jsonl`、`installed-activity.jsonl` | 正式健康、计划、历史与当前任务只读快照 |
| `installed-market-history.jsonl` | 最近成交统计执行及其阶段日志 |
| `binary-hashes.json` | 正式与源码构建程序集摘要 |

建议先处理 F01 和 F02，再修正 F03 的结果语义，随后补齐 F04。修复后应新增对应时序回归，并单独完成候选包与正式 Worker 的验收；本轮审查没有替代该验收。
