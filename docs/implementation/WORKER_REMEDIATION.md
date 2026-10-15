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

## 第二批：数据目录权限巡检与导航整理

正式服务启动只验证安装器建立的数据目录，不自动创建丢失的正式根目录或修复其权限。根目录必须关闭父级权限继承，由SYSTEM或管理员组拥有，并保留管理员维护权限与服务SID的可继承Modify权限。树内全部文件和目录逐项核验允许主体与所有者，包括拥有独立ACL的密钥、数据库和备份子项；不允许给共享LocalService账户添加读取ACE。LocalService创建的文件可以保留其Windows所有者身份。

读取密钥前拒绝文件和祖先路径中的重解析点；数据树扫描不跟随链接，最多检查100000个子项，超限需要维护。服务启动前、生成密钥后以及每30秒调度协调前进行检查。发现路径、权限、所有者或可访问性异常就停止管道与调度、取消在途任务并故障退出；保留密钥和数据，不尝试重新生成或自动修复。SCM重启时会重新校验正式目录。

Release构建和后台65项检查通过（原56项加9项真实Windows权限检查）。验证包含普通用户读取ACE、根继承开放、受保护密钥/备份子项、目录与密钥链接，以及真实隔离子进程在运行中检测ACL漂移后退出和原密钥不变。发行包为 `artifacts/worker-acl-reviewed`。`--storage-acl-probe` 对当前安装数据树的只读核验通过；本轮未替换已安装服务的二进制。

这些检查不是文件系统原子沙箱：30秒巡检存在检测窗口，管理员和同一LocalService账户仍属于Windows身份信任边界，不承诺阻止并发恶意替换。F09的跨账户/跨机器密钥恢复封装仍未完成。

桌面导航按“投资与数据”“税务与报表”“任务与服务”“插件与扩展”分组，展开当前分类时收起其他分类；概览及设置、锁定保持直接入口。后台服务、计划任务、插件管理及插件页面都有图标和悬停标题。页面跳转自动展开所在分类。Release桌面构建0警告/0错误，隔离窗口冒烟覆盖十页跳转、任务页面、最小窗口、收窄侧栏及绑定检查，通过；人工核对截图 `artifacts/menu-groups/window-scheduled-tasks.png` 与 `window-collapsed.png`，架构门禁通过。当前“计划任务”仍管理桌面插件任务，“后台服务”仍管理独立Worker，两套执行主体尚未合并；菜单分类不代表ETF已迁移。

## 仍未关闭

F01：本轮[ETF迁移](ETF_WORKER_MIGRATION.md)已接入双入口完整包、Core/Jobs/UI分离、服务独占Schema 2加密市场库、只读查询及持久业务命令；桌面调度及写连接已移除。完整业务数据与正式发行门槛仍须独立验收。

F07/F08：真实来源等待政策、跨Job来源级阻断/限流、交易日缺口、业务检查点和公平资源预算。

F09/F10/F11：ACL漂移巡检已接入；独立恢复封装和账户迁移、完整安装/升级事务与版本回退规则、最小HostContext和真实原生依赖治理仍未完成。Schema 1快照与自动迁移不替代这些完整运维流程。

当前Worker已执行ETF公开采集，关闭WPF后仍可运行；此前心跳阶段的证据保留为历史基线，当前实施与运行范围以[ETF迁移记录](ETF_WORKER_MIGRATION.md)为准。
