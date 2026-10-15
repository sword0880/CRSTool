# Worker v2 首批整改

更新：2026-10-15。对应 [反方审查 F01—F11](../validation/WORKER_ARCHITECTURE_REVIEW.md)。本轮优先修复 Worker 宿主可靠性，不代表 ETF 服务化一期全部完成。

## 名称和协议

四个项目、程序集及命名空间统一为 `CRS.Worker.Abstractions`、`CRS.Worker.PluginHost`、`CRS.Worker.Ipc`、`CRS.Worker.Service`，版本为2.0.0。插件后台 HostContract 升为2.0，心跳发行包为 `crs.heartbeat/2.0.0`。旧契约插件不会作为新版后台入口加载。

用户确认后，查询管道改为 `CRS.Worker.Service.v2`，管理员控制管道为其 `.control` 后缀；双方 `ProtocolVersion=2`。普通查询使用四个监听实例，管理独占两个实例和管理员 ACL。仍校验服务端 PID 与 SCM 一致，以及实际 Windows 调用令牌。旧协议明确拒绝，旧管道没有降级执行。

SCM名 `CRSBackgroundService`、LocalService账户、服务 SID，以及 `%ProgramFiles%/CRS/Background`、`%ProgramData%/CRS/Background` 保留。可执行文件改为 `CRS.Worker.Service.exe`。`ServiceKey` 的 `CRS.Background.Service/key/v1` 常量是已持久密钥的保护参数，明确保留原值。

## 本轮行为

| 审查项 | 整改行为 | 验收边界 |
| --- | --- | --- |
| F02 | Begin/End/命令审计写入异常进入 StorageFault，停止触发并取消在途工作；健康查询不再依赖故障仓储；WPF显示实际健康状态 | 仓储边界注入 IOException，覆盖 Begin/End/Audit；不是实际磁盘满/掉电 |
| F03 | Schema 2新增持久 command_request，接受手动执行前提交待执行项；开始/结束与命令状态同事务；配置与命令完成同事务；同ID、同SID、同参数返回原状态，参数改变拒绝；审计追加保存 | 真正子进程在首任务占有插件写入门槛时接受第二任务，在第二任务 Begin 前强杀并重启，原ID可查最终结果 |
| F04 | 单插件初始化故障隔离并暴露状态；Create/Start/Describe/Stop/Dispose使用宿主等待期限；逐插件清理汇总异常；不响应取消时发出终止宿主信号，实际服务直接退出保护存储 | 验证初始化异常、停止异常及不合作启动/执行；同进程插件不构成OS沙箱 |
| F05 | 读写独立管道、容量与SID限流；只读不占全局重放缓存；超配额请求不产生逐条写入审计风暴 | 四个无消息查询连接占满读通道时，管理员仍可暂停；查询额度耗尽后控制通道仍可恢复 |
| F06 | Jobs/History游标分页，每页1—10条；历史游标使用数据库rowid保证同时间记录不重漏；超大响应返回PayloadTooLarge；WPF提供更多计划/更多历史 | 100条1024中文字符检查点通过真实管道分页，无重复遗漏 |
| F07部分 | NotPublished/RetryDeferred不计故障次数；持久等待时点重建一次性Quartz触发器；RunOnStartup必须显式声明，默认关闭 | 验证真实Quartz按已存等待时点唤醒；官方源级阻断、交易日缺口、抖动预算待业务接入 |
| F08部分 | 同插件多个Job共用Session写入门槛，不同插件使用不同门槛 | 真实双Job夹具验证最大并发为1；来源级限流、公平性及真实ETF事务待验证 |
| F10部分 | Schema 1迁移前自动生成SQLCipher Backup API一致快照，Schema迁移及运行中恢复同事务；每库最多保留三次未成功迁移快照；安装成功增加应用健康探测 | 旧Schema事实保留、迁移快照可解密且仍为Schema 1；尚未形成完整安装事务、版本切换及新事实禁止回退流程 |

手动命令重启后采用至少一次执行：遗留Running执行标记Interrupted，其持久手动命令重新进入Pending，分配新的执行ID。业务层仍须以稳定业务键保证幂等。取消计划会原子取消尚未开始的手动命令，运行中的工作接受取消令牌。每库最多保存100000条幂等命令记录；达到容量需维护，当前不静默删除去重凭据。审计最多2000条、已结束执行历史最多500条。

WPF响应不确定时保留原请求，阻止将另一个操作误作重试；“确认上次请求”重发原标识和原参数。服务端返回原命令状态，避免额外创建一次执行。健康异常不会再显示“后台服务正常”。

## 验证与产物

Debug、Release各通过56项后台检查（原29项及新增27项），覆盖故障、生命周期、并发、管道容量、持久命令强杀恢复、Schema迁移、真实重试、无效默认计划隔离和执行超时场景。对应不可复用发行目录为 `artifacts/worker-v2-reviewed-debug`、`artifacts/worker-v2-reviewed`。Release全解决方案构建、WPF交互套件、架构门禁及代码差异空白检查通过。测试夹具位于 `tests/fixtures/CRS.Worker.FixturePlugin`，不会进入正式心跳插件包。

本机已完成停止旧服务、校验备份、卸载旧程序并保留数据、安装Worker v2的开发包切换。原LocalService账户启动成功，DPAPI密钥文件摘要与升级前一致，旧历史记录和新心跳同时可查询；默认生产管道健康为Running。移除管理员组的真实线程令牌验证查询成功、修改拒绝。真实安装端点的WPF窗口绑定及首屏操作检查通过，截图为 `artifacts/worker-v2-ui/window.png`，已人工查看。备份位置记录在 `artifacts/worker-v2-preinstall-backup.txt`。这次人工可核验切换不等于完整自动升级事务或生产签名验收。

```powershell
dotnet restore CRS.sln --force -m:1 -p:NuGetAudit=false --source .nuget-feed --source C:/Users/Administrator/.nuget/packages
dotnet build CRS.sln -c Release --no-restore -m:1 -p:NuGetAudit=false
./tests/Publish-BackgroundService.ps1 -OutputDirectory artifacts/worker-new -Configuration Release
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- artifacts/worker-new
```

窗口可显式连接隔离控制台管道：`dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --background-ui artifacts/worker-ui/window.png <隔离管道名>`。未传管道时验证真实安装端点和SCM PID。

正式启动不接受自定义路径/信任索引；开发控制台显式 `--console` 后可用 `--data/--plugins/--pipe/--trust` 隔离测试。服务程序 `--health-probe` 只读查询实际安装服务，插件和仓储健康为Running才返回0，不占用数据库租约。

离线复现构建显式关闭NuGetAudit；不代表已处理原有依赖安全告警。生产签名、系统代理和官方来源许可仍按原门槛验收。

## 仍未关闭

F01：统一UI/后台发行清单、ETF Core/Jobs/Ui分离、服务独占加密市场库、取消旧桌面调度及写连接。

F07/F08：真实来源等待政策、跨Job来源级阻断/限流、交易日缺口、业务检查点和公平资源预算。

F09/F10/F11：ACL漂移自检、独立恢复封装和账户迁移、完整安装/升级事务与版本回退规则、最小HostContext和真实原生依赖治理。Schema 1快照与自动迁移不替代这些完整运维流程。

在上述业务接入完成前，Worker仍执行公开心跳；关闭WPF后自动采集ETF的目标尚未完成。
