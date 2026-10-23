# NationalTeam 分组并发：本机 Worker 更新与现场验收

2026-10-23，北京时间。用户在源码整改和隔离回归完成后要求继续，按正式维护入口更新既有 `CRSBackgroundService`。未清空数据、替换密钥、修改计划或手动触发业务任务。

## 已完成的更新

- 入口：`tools/background/Upgrade-Service.ps1 -AllowDevelopmentPackage`，完整配套包为 `artifacts/national-team-concurrency-20261023/worker-final/`。这是本机开发包更新，不作为签名生产发行验收。
- 维护代次：`28a085fc4006480cb2806ae77c7b64f0`；14:49:46 到达 `Committed`，非暂存、启动中或待确认状态。
- 活动发行摘要：`34F4E14DD55B826F134DB31D495768C57B5624CFB2DE48C2C23084E043D5432B`。全部 153 个发行文件校验通过。
- 服务恢复自动启动；验收期间健康 `Running`，PID `38076`，两个插件均 `Started`。NationalTeam 正式会话上限从 1 变为 4，全局上限仍为 8。
- 原服务密钥指纹一致；5611 个文件的一致性快照及旧程序目录保留。7 个既有任务计划的启用状态、Cron 和时区一致；抽样的 9 条既有已完成执行经正式 IPC 精确回读一致。
- 启动前完成权限树和快照校验，未绕过检查或延长插件生命周期期限。健康就绪前出现的探针连接失败保留在更新日志中，不记为已就绪。

## 正式服务实际并发

以下任务由既有启动调度自然执行，证据来自正式 Worker 的任务活动与持久执行记录：

| 任务 | 执行 ID | 开始 | 结束 | 结果 |
| --- | --- | --- | --- | --- |
| 成交采集 | `36024e0dc8134e359167e159ad5e061c` | 14:49:45.884 | 14:51:17.410 | `RetryDeferred`，本轮提交 6 条，补采未全部完成 |
| 预警评价 | `9960d1ef09ea4c52806af91fe20f6a8e` | 14:49:47.662 | 14:51:14.183 | `Succeeded`；`notificationAttempts=3` 仅表示尝试次数，不是投递成功证明 |
| 完整性巡检 | `15b1a73cdbae48799332ed821eee1a05` | 14:49:47.764 | 14:51:14.075 | `Succeeded`，`integrity=Reconciled` |

三者实际重叠 **86.311 秒**，预警和巡检在成交采集结束前完成。活动采样同时记录了两个采集组执行与一个预警组执行，采集额度用满后另有采集等待；不把等待任务计为同时运行。

本次现场未写入测试配置来占用管理组；“两个采集阻塞时管理组仍能完成”的证明来自此前隔离真实 Worker 回归。不同来源并行仍受来源额度限制。SSE 冷却至当日 18:34:33.498 的状态在更新后保持，未重置或绕过；成交执行检查点为 `days=9;sourceCalls=8;httpStarted=7;rows=6;firstFailure=SourceCooldown;hasMore=True`，不能据此称历史补采全部成功。

## 查询限制与取消隔离

并发负载期间，完整 dashboard 只读探针在约 15.6 秒返回 `RequestTimedOut / Unavailable`，**该项未通过**，未用空结果代替真实数据。正式诊断为 `Query.dashboard.TimeoutCanceled`；其后同一 PID 仍健康，巡检、预警和成交任务继续完成上述提交，未出现取消排空超限或服务重启。

因此，本轮确认了本机配套更新、数据保留与真实多任务并发；完整页面查询的响应时间仍需独立优化，官网历史数据的完整覆盖也未因本次更新自动通过验收。

## 证据

目录：`artifacts/national-team-concurrency-20261023/installed/`。

- `before.json` / `before-probe.jsonl`：更新前服务、密钥指纹、维护状态、计划和近期执行基线。
- `upgrade.log`：正式更新入口退出码 0，维护事务已提交。
- `Verify-Installed.ps1` / `acceptance.json`：只读复核脚本及验收结果，退出码 0。
- `previous-executions.json` / `after-jobs.json`：旧执行事实与计划回读。
- `health-samples.jsonl` / `activity-samples.jsonl`：正式服务健康及多任务活动采样，采样退出码 0。
- `parallel-executions.json` / `concurrency-summary.json`：执行最终状态与精确重叠时间。
- `dashboard.log` / `health-after-dashboard.json`：整页查询未通过，及超时后同一 PID 的健康证据。
- `final-activity.jsonl`：14:54:23 份额任务仍由正式 Worker 运行，未停止采集以完成验收。

最初启动采样器把停机备份时间也计入七分钟等待，先于新服务就绪退出；更新入口随后正常到达 `Committed`。最终并发证据来自就绪后的独立采样和已完成执行回读，不使用该次未观察到健康的等待作为通过证明。
