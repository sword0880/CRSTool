# Worker运行、调度与维护

<a id="background-service"></a>
<a id="etf-worker-migration"></a>
<a id="monitor-readiness"></a>
<a id="scheduled-tasks"></a>
<a id="worker-cancellation"></a>
<a id="worker-remediation"></a>

后台服务是公开采集任务的唯一执行者。桌面关闭或锁定不会停止公开采集；桌面通过本机授权IPC读取和提交管理命令，不创建备用调度器或市场库。当前部署和发布缺口以[架构状态](../architecture/ARCHITECTURE_STATUS.md)、[发布验收](../validation/RELEASE_ACCEPTANCE.md)为准。

## 进程、存储与身份

- `CRS.Worker.Service`拥有Quartz调度、计划/执行/命令持久化、来源预算、维护和健康；受信插件在独立`CRS.Worker.PluginRunner`中执行。插件隔离和可信包检查不构成任意第三方代码的安全沙箱。
- 服务名`CRSBackgroundService`，账户`NT AUTHORITY\LocalService`；程序目录`%ProgramFiles%\CRS\Background`，资料目录`%ProgramData%\CRS\Background`。
- 管理存储继续加密，服务密钥由机器DPAPI、服务SID及私有目录ACL保护。已有库缺失密钥、身份不符、错误密钥或格式不符时停止启动，不能创建新库替换现场。国家队研究库已使用普通SQLite，见[国家队存储与分层](NATIONAL_TEAM_ARCHITECTURE.md)。
- `CRS.Worker.Service.v2`提供查询，其`.control`端点处理管理员控制。双方协议版本为2；认证用户可读、管理员可管理，服务端按实际Windows令牌授权，默认客户端核对服务端PID与SCM。旧协议不降级执行。

## 调度、请求和取消

计划持久状态是权威，Quartz启动后据此重建触发器。相同任务不重叠执行，Misfire采用DoNothing；配置先保存再应用，失败保留可协调状态。桌面不直接修改任务库。

全局默认并发8。同插件默认串行，只有显式声明并发能力、任务组及并发安全的插件才获得受限名额；单插件仍受全局公平上限约束。国家队的执行组与连接边界见[国家队架构](NATIONAL_TEAM_ARCHITECTURE.md)。

联网采集采用有上限的异步并行，按独立来源和可独立校验的日期划分工作，不为每个日期创建专用线程。网络等待不占数据库写门槛；下载结果通过有界队列交给短事务提交，取消或生产/消费故障须撤销并排空在途工作。不能用全局锁覆盖HTTP，也不能一次启动整段历史的全部请求。支持区间接口时优先完整批量读取，避免拆成重复逐日请求。同源在途数、开始间隔、共享冷却及完整分页仍由来源策略约束，线程数量不是源站吞吐保证。市场成交日期流水线见[ETF与成交](NATIONAL_TEAM_ETF.md)，绩效指数/汇率并行见[投资绩效](PERFORMANCE_ANALYTICS.md)。

手动操作使用稳定请求ID、调用SID和参数。受理前持久记录，事务内提交业务结果与命令回执；断线、超时或结果未知时保留原ID查询，不能换ID重复提交。业务需要幂等，`Interrupted`不证明业务没有生效。

取消待执行请求可直接终结；取消运行中请求先记录取消意图，待插件实际结果落盘再确定终态。取消不覆盖已成功的真实结果；重启、退避及取消收尾不能丢失原意图。回调与任务不合作时保留监督进程的失控退出保护。

任务存储故障进入`StorageFault`，取消在途工作并停止触发。健康端点仍可报告故障；无法读取调度信息时保留未知，不能补零或仅因SCM显示Running便声称采集正常。

## 发布与维护

```powershell
dotnet build CRS.sln -c Release -m:1 -nr:false
./tests/Publish-BackgroundService.ps1 -OutputDirectory artifacts/worker-candidate -Configuration Release
```

输出目录必须不存在。开发可信索引不是正式发行签名；正式包还需签名、发布者和完整文件核验。安装、升级、恢复分别使用专用维护入口，已有服务不能通过首次安装器覆盖。升级必须保留原密钥、计划、原程序、一致性快照和业务事实，并独立复核Committed及应用健康。

卸载默认保留资料；不要删除密钥或还原旧数据库来处理普通查询错误。跨机器恢复、服务身份更换、新事实产生后的回退限制统一见[维护与恢复指南](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)和[服务密钥恢复设计](../architecture/WORKER_SERVICE_KEY_RECOVERY.md)。

## 验证和开放事项

后台隔离验证由`CRS.Tests.Background`执行，重点入口包括`--cancellation-boundaries`、`--scheduling-only`、`--history-result-filter`及完整候选包联调。命令与环境要求统一维护在[测试说明](../../tests/README.md)。心跳专用包只能验证宿主可靠性，不能代替国家队完整发行。

源码、隔离进程、正式服务、真实来源覆盖和金融有效性分别验收。生产签名、干净Windows安装、灾备及长期负载等门槛仍以[发布验收](../validation/RELEASE_ACCEPTANCE.md)为准；历史某轮构建通过和维护Committed不自动关闭这些门槛。
