# 独立后台服务实施记录

更新：2026-10-15。当前实现范围是 [后台设计 §6.2](../CRS_BACKGROUND_SERVICE_DESIGN.md) 的 **Heartbeat 纵向 POC**，不是 ETF 服务化一期全部交付。生产发行未批准。

**Worker v2更新**：[首批整改记录](WORKER_REMEDIATION.md)为当前行为依据。下文29项、旧管道重放缓存及旧发布目录保留为首轮POC历史证据；项目现已更名且IPC、命令持久化、生命周期和分页已更新。

## 已实现的行为

- `CRS.Worker.Service` 是独立的 .NET 10 Windows 可执行程序，使用 Generic Host / AddWindowsService 和 Quartz.NET 4.1.0。宿主扩展锁定为可离线复现的 Microsoft.Extensions.Hosting / WindowsServices 8.0.0，已联合 .NET 10 构建和实际 SCM 验证。服务无 WPF、个人税务 Infrastructure/Security 或 TOTP 依赖。
- 后台纯 BCL 契约、独立插件宿主、Windows IPC 适配分项目维护。`crs.heartbeat` 是真实加载的无界面插件，不静态链接进宿主；UI-only 清单可跳过后台入口。宿主在加载前验证管理员目录中外置信任索引覆盖的完整发行文件；拒绝篡改、路径逃逸、链接和后台依赖 WPF/私人税务/独立 Quartz 实现。此边界约束第一方受信插件，不构成任意第三方代码沙箱。
- `scheduler-config.db` 由 SQLCipher 加密，是 Cron、时区、启用状态、修订号及退避状态的唯一权威。Quartz 使用 RAMJobStore，启动重建稳定 JobKey，禁止同任务并发，Misfire 为 DoNothing。先保存配置再应用触发器，失败保留 NeedsReconcile，30 秒重建检查或下次启动重试。
- 服务密钥独立生成，使用机器 DPAPI 封装，结合服务 SID 目录 ACL；封装记录服务账户 SID。更换账户、数据库存在却缺失密钥、旧明文库、错误密钥或外来数据库均拒绝启动，不生成新库替代已有现场。不引用或迁移原桌面明文市场库。
- 运行开始/结束、最多 500 条执行历史和最多 2000 条命令审计保存在加密库。上次未结束的记录重启标为 Interrupted；失败指数退避最高 6 小时，AccessBlocked/SourceSchemaChanged 冷却一天，不规避网站限制。插件执行有 15 分钟取消期限，停止有 20 秒等待；不合作的插件最终终止进程保护存储。
- 本机 Named Pipes 实际 Windows 令牌授权：认证用户可读，管理员可管理。网络 SID 被拒绝，普通用户没有创建管道实例权限；默认客户端在发送请求前校验管道服务端 PID 与 SCM 当前运行服务一致。固定命令白名单、消息长度/超时、协议版本、请求 ID、编辑修订和重放保护均在服务端校验。修改请求先在加密审计中占位；读请求的重放窗口为 10 分钟、最多 2048 个 ID。
- WPF 新增“后台服务”入口，查询健康、计划和历史，以及执行/取消/暂停/恢复/Cron 修改。服务不可用时显示原因并保留带采样时间的内存快照；不启动替代后台调度器。现有税务与绩效功能不依赖服务安装。
- 诊断文件位于服务受限目录 logs，固定结构化字段，不记异常消息、密钥或业务原文；每天上限 5 MiB，最多保留 14 个文件。完整任务事实以加密执行历史为准。

## 发布、安装与卸载

在仓库根目录用 PowerShell 7 执行，先还原依赖：

```powershell
dotnet restore CRS.sln -m:1 -p:NuGetAudit=false --source .nuget-feed --source C:/Users/Administrator/.nuget/packages
./tests/Publish-BackgroundService.ps1 -OutputDirectory artifacts/background-service-new -Configuration Release
```

发布目录必须不存在。产物框架依赖，不单文件、不裁剪、不 AOT；安装机需 .NET 10 x64 运行时。开发发行信任索引是宿主拥有的哈希索引，不是生产证书签名。

管理员独立安装开发验收包：

```powershell
./tools/background/Install-Service.ps1 -PackageDirectory artifacts/background-service-new -AllowDevelopmentPackage
```

正式安装不使用开发开关，需要发行方对原始 `release.json` 生成分离 CMS 签名 `release.json.p7s`，并由管理员在安装命令外部指定 `-TrustedPublisherThumbprint`。安装器校验证书链、发布者、完整文件索引和复制后哈希。生产签名生成流程尚未验收。

安装器固定服务名 `CRSBackgroundService`，账户 `NT AUTHORITY\LocalService`，延迟自动启动、服务 SID、失败后 60 秒/300 秒恢复；程序位于 `%ProgramFiles%\CRS\Background`，数据位于 `%ProgramData%\CRS\Background`。数据仅 SYSTEM、Administrators 和该服务 SID 可访问，用户代理配置和登录态不参与密钥解封。安装器遇到已有服务或程序目录拒绝覆盖，不提供伪装成升级的强制覆盖选项。

管理员卸载：

```powershell
./tools/background/Uninstall-Service.ps1
```

默认删除已核验的服务及固定程序目录，保留全部 ProgramData 数据、历史、诊断及原密钥。没有从 IPC 删除数据的命令。重新安装会复用保留数据。不要手工删密钥来“修复”现有库。

隔离控制台调试需显式 `--console --data <独立目录> --plugins <受信发行目录/Plugins> --pipe <独立管道名>`；租约绑定数据库目录，拒绝第二个进程同时访问。控制台目录也设置私有 ACL。测试模式自定义管道不进行 SCM PID 核验，正式默认端点始终核验。

## 已取得的实测证据

- Release/Debug 后台关键路径回归：每种配置 29 项；包括 SQLCipher 拒读、密钥缺失/明文拒绝、WAL 一致加密快照、修订并发、退避、真实 Quartz、真实管道、篡改拒载、独立子进程和强杀后恢复暂停计划。
- 本机实际管理员安装、LocalService 启动并解封独立密钥，关闭桌面程序仍执行心跳；SCM 查询确认延迟自动启动和恢复配置，数据 ACL 包含服务专属 SID。
- 实际移除管理员组的受限 Windows 线程令牌访问跨进程服务：健康和计划可读，修改返回 Forbidden。未创建测试账户或改变桌面 ACL；不将其宣称为不同交互会话验收。
- 实际卸载删除程序，同时数据库存在、服务密钥 SHA-256 不变；重新安装成功解封保留数据并继续原历史。
- 实际强杀安装服务 PID 42736，SCM 在恢复窗口自动启动 PID 20816；Named Pipes 查询恢复 Running，原执行历史仍在。此证据不是断电或整机重启测试。
- 全量 Release 解决方案构建、后台依赖边界及现有绩效架构门禁通过。离线构建前关闭 NuGetAudit 以使用本机源，不能据此宣称依赖安全审计通过；原项目依赖审计告警需单独处理。
- 既有 Release WPF 交互回归通过；新增独立 WPF 窗口实际通过生产管道绑定计划/历史，检查首屏六个操作按钮，截图为 `artifacts/background-service-ui/window.png`，未打开税务保险库。

Release 开发产物：`artifacts/background-service-current`；Debug：`artifacts/background-service-current-debug`。较早的 verified/debug 目录保留为历史验收现场。验证命令：

```powershell
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- artifacts/background-service-current
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --probe
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --acl-probe
```

Windows 管道授权测试需正常完整性 Windows 令牌；低完整性执行沙箱会被 Windows 拒绝，不能通过放宽生产管道 ACL 来规避。

## 后续必须完成的范围

统一缺口登记见 [当前待办](../KNOWN_ISSUES.md)。当前心跳包不联网，也不构造 ETF 市场库。**ETF 现有桌面插件尚未迁移**，仍使用之前的桌面调度和存储；不能把安装本服务等同于 ETF 已支持关闭 WPF 后自动采集。Core/Jobs/Ui 分离、同包双宿主、服务独占 SQLCipher market.db、官方来源/交易日检查点/有限补采、摘要查询、管理整插件禁用均待后续接入。

仓储提供并验证了 SQLCipher Backup API 一致快照，但**尚无运维级自动备份/恢复命令、维护升级 Journal、版本切换及禁止回退新事实的完整服务流程**。账户更换当前明确拒绝，不支持自动迁移；跨机恢复也未验收。无人登录/整机重启、睡眠恢复、不同 Session 授权、LocalService 官网网络与代理、磁盘满/物理断电、生产签名发行均未关闭验收。

API 依据：[微软 Windows Service](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)、[显式 Named Pipe ACL](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0)。实现以已锁定包编译和上述实测结果为准。

## 2026-10-16 任务中心交互

服务计划页移除常驻 Cron/时区编辑栏。双击实际计划行或按 Enter 打开独立编辑窗口，编辑副本固定任务身份和打开时的修订号；取消不写入，保存仍走 ChangeSchedule 管道及服务端管理员、Quartz 表达式、时区与并发修订校验。无权限或计划冲突时窗口保留输入。主页面空白和表头双击不应打开编辑窗口；会话取消关闭窗口。

执行日志采用 18px 常显滚动条、最小 40px 滑块；支持正文滚轮、PageUp/PageDown、Ctrl+Home/End、开头及回到最新。向上阅读暂停跟随，刷新保留视口和选区；在底部继续跟随。执行 ID 折叠，正文增加关键词搜索、异常/等待阶段筛选和复制完整日志，筛选不改变服务端历史查询及已保存内容。

后续可在独立计划编辑窗口加入可视化 Cron 生成器，参考用户提供的 https://xiongyingqi.com/cron-online/，保留手动编辑入口。生成规则需与后台 Quartz 校验一致，不能直接假定外部生成器的方言、时区或最近执行时间与服务相同。本次仅完成手动编辑窗口，未嵌入外部网页或生成器。

验收入口：CRS.Tests.Wpf --execution-log-ui <截图路径>，使用隔离模拟管道，覆盖权限拒绝、冲突保留、固定任务/修订号保存、长日志滚动与刷新位置、异常筛选和搜索零匹配；不修改实际服务计划。

滚动修正：使用 WPF Track 的最小滑块资源计算（VerticalScrollBarButtonHeightKey=80，对应最小40px），避免只设置 Thumb.MinHeight 导致末尾裁切。滚动条交给 ScrollViewer 原生命令驱动，Scroll 回调只更新跟随状态；不得再次用尚未同步的 NewValue 覆盖视口。参考：https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-customize-the-thumb-size-on-a-scrollbar 与 https://source.dot.net/PresentationFramework/System/Windows/Controls/Primitives/ScrollBar.cs.html 。验收发送完整 DragStarted/DragDelta/DragCompleted 事件链并检查实际视口位移，不再用手动 Scroll 事件冒充滑块拖动。

## 2026-10-16 可视化 Cron 生成器

独立计划编辑窗口新增频率选择：秒间隔、分钟间隔、每小时、每天、每周多选及每月指定日期。常用六字段表达式仅在精确等价时还原为选项；复杂规则/七字段表达式保留原文并进入手动模式。生成模式下表达式只读，切换到手动模式可编辑高级规则。

表达式生成、还原、Quartz校验和未来5次触发时间计算集中在 CRS.PluginHost/CronEditor.cs，WPF只负责输入与展示；不创建调度器、不直接保存或执行任务。与现有后台服务同版本 Quartz（4.1.0）。预览按填写的时区计算并显示偏移量，默认沿用计划自身时区；不是任务实际执行承诺。无效表达式/时区或未选择星期时禁用保存并清空旧预览，不能回退保存上次有效表达式。服务端权限和修订校验继续保留。

秒/分钟步长是每分钟/小时重置的Cron字段步进，界面说明该语义；每月29至31日缺失月份跳过，周选项不自动处理节假日。未来无匹配时间的有效表达式明确提示，不伪造日期。参考 Quartz Cron 文档：https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/crontriggers.html 。用户提供的网址作为功能参考，本地实现不依赖外部网页。

验证：CRS.Tests.Wpf --cron 覆盖六类频率生成还原、星期、月末/闰月、时区及高级/非法规则；--execution-log-ui 同时验证真实窗口绑定、星期多选、清空星期后禁用保存、复杂表达式保留及原有计划编辑/日志回归。截图包含 .generator.png。
