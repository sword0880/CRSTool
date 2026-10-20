# 管道断连与主窗口品牌区调整

日期：2026-10-20。用户报告ETF概览的EndOfStreamException，同时要求美化主窗口左上品牌区。

## 复现与修复

只读查询已安装CRSBackgroundService：服务Running，概览查询在8199ms发生EndOfStreamException。旧服务把请求读取、插件查询和响应写入共用8秒令牌，取消后吞掉异常并关闭连接，客户端ReadExactlyAsync读不到响应头。该复现不说明数据库或原始证据损坏。

IPC改为分段期限：收帧5秒、控制层处理默认20秒、回响应3秒；客户端连接3秒、总等待32秒。插件原15秒生命周期及不合作时的退出保护保留。超时返回RequestTimedOut；写请求超时返回CommandOutcomeUnknown，客户端抛出带原RequestId和OutcomeUnknown标志的可展示异常，禁止凭断连认定未受理或自动创建新命令。

帧读取显式识别响应头/正文截断，不再向界面泄露原始EndOfStreamException。无效长度仅隔离当前连接，不再因InvalidDataException漏捕获而终止监听。ETF操作提示可展示受控通信错误文本，不展示第三方异常正文。

保留BackgroundPipeServer二参数构造器，以及新增来源健康字段之前的BackgroundHealth五参数构造器/解构方法；明确JsonConstructor，避免多个公开构造器导致反序列化歧义。契约与IPC相对已安装版本的33个公开类型检查通过，无原方法/构造器/常量移除，无新增抽象实现要求。

11项真实隔离管道检查通过：9秒查询完成、畸形帧后的健康响应、控制层IO错误、查询超时、写操作受理未知、诊断故障、三处截断、单字节分片及用户取消。旧宿主搭配新通信契约/IPC的隔离启动通过，使用空插件和新测试目录，不联网。

证据：artifacts/pipe-eof-installed-overview-before.log、pipe-eof-boundaries.log、pipe-eof-compatibility.log、pipe-eof-binary-api.json。

## 实际服务更新

第一阶段候选包基于原已安装程序，仅替换CRS.Worker.Ipc、CRS.Worker.Abstractions及对应PDB，重新生成发行文件哈希。原宿主实现、业务插件、数据库提供方与算法不变。沿既有开发发行维护流程建立一致性备份、保留旧程序、更新维护代次并重启；不直接覆盖活动目录或绕过维护日志。

该阶段维护代次91bc1259afcc494fb1b2d524fc9df9c2已Committed。初次启动核验3904份快照文件超过原40秒健康等待，按原流程停止并保留NeedsManualRepair现场；将维护等待扩为180秒后执行RetryCandidate恢复，未回退或覆盖数据库。实际文件差异仅两份通信程序集及对应PDB，33项公开类型二进制兼容检查通过。

恢复后实际概览查询在15344ms返回业务失败，已不再是断流，但仍未满足概览验收。随后继续定位仓储查询。

### 第二阶段：概览质量状态索引

日历查询对每个交易日执行Q15质量状态的NOT EXISTS；缺少evidence_id/rule_id/status索引会反复全表扫描。仅在SQLCipher仓储适配器增加ix_quality_evidence_rule_status覆盖索引，不向业务端口或WPF暴露SQL，不改表字段、Schema版本或财务口径。

隔离执行计划确认SCAN q改为SEARCH ... USING COVERING INDEX；2万条合成质量问题、260个日期下相同查询从544ms降至1ms，前后结果完全一致。损坏原件仍拒绝，下一次读取仍重新核验修复原件，未引入跨查询缓存。五项检查见artifacts/overview-index-checks.log；该耗时不是实际数据验收。

第二阶段后台插件仓储版本已更新；原宿主与新插件包在隔离目录验证Started及真实概览查询通过（overview-plugin-compatibility.log）。当前宿主未实现的共享来源预算仍不能视为已部署；数据库提供方仍为SQLCipher，未改为其它类型。

实际维护代次fbeef372a2d24f59a522c5245df53675已Committed；文件差异审计为插件目录及信任索引15项，原宿主实现未变（overview-index-installed-scope.json）。完整已安装服务概览只读查询成功返回24只ETF，耗时11460ms，证据overview-index-installed-acceptance.log。该耗时是含原件重新校验的实际查询，不以合成1ms数字替代。

最终实际页面刷新链路亦通过：概览24只/8318ms、配置24项、历史434行、成交统计38行，整链路41034ms（overview-installed-refresh-acceptance.log）。没有再断流或触发15秒单查询期限；整页仍需约41秒，不能称为即时刷新。全过程为只读，不触发采集、写入或测试通知；行数不代表覆盖完整或机构持仓已确认。

## 2026-10-21：插件启动取消与页面刷新分离

用户随后报告等待管道响应时的TaskCanceledException。源码确认：NationalTeamUiSession.StartAsync等待完整刷新，而桌面PluginRuntime只允许15秒启动；此前41034ms验收直接调用workspace，没有经过桌面插件启动，不能证明桌面启动链路通过。启动超时后宿主清理会取消会话，传播到Framing.ReadFrameAsync。该堆栈也可能来自手动取消或锁定，单凭异常类型不能确定实际触发源。

改为StartAsync只建立模型和注册页面，首次数据查询由EtfPage.Loaded触发；同一模型共享首次刷新任务，重复导航不会重复发起初始查询，失败后仍可手动重试。未延长宿主启动期限或通信期限，后台查询及原件核验保持原有要求。Visual Studio在异常“引发时”可能先停在底层读取，随后界面按既有取消分支处理；不通过全局吞异常或关闭诊断来修复。

新增`CRS.Tests.WorkerEtf --startup-ui`验证真实会话不等待后台启动、导航注册、首次加载共享任务，以及受控查询超过15秒后仍未被启动超时取消、失败提示和手动重试。`--ipc-boundaries`增加“请求已发送、等待响应期间取消”检查。测试使用隔离替身，不代表真实数据刷新耗时已经缩短，41秒的性能续项仍开放。

本轮7项启动回归、12项管道边界与架构门禁通过，Release构建零警告/错误。开发包`artifacts/etf-startup-desktop-20261021`已同步Debug/Release，每份52个插件文件与信任清单逐项哈希一致；重启桌面后加载。本轮仅更新桌面开发输出，未重新部署后台服务。

实际只读后台复核首轮概览在15364ms失败（`artifacts/etf-startup-installed-refresh.log`），说明后台慢查询仍可能碰到独立查询期限，不能用启动修复宣称后台性能问题已关闭。探针另增加各数据集计时与安全错误码，继续保留失败证据。启动与管道回归日志分别为`etf-startup-checks.log`、`etf-startup-pipe-checks.log`。

分阶段复核第二轮概览24只/10582ms、配置24项/13ms成功；历史读取约15027ms后返回PluginQueryFailed，总耗时25622ms（`etf-startup-installed-refresh-detailed.log`）。现有后台宿主会将插件内部取消归并为该通用类别，约15秒与插件调用期限相符，但该探针未提供后台内部阶段耗时，尚不能把根因确定为原件扫描、SQL或历史计算。两次失败均保留，未自动重试掩盖问题。

## 2026-10-21：ETF插件加载专项复核

用户继续报告ETF插件异常无法加载。新增独立进程`CRS.Tests.PluginHost --load-probe`，通过真实PluginRuntime读取工作区Debug/Release的可信插件包，并依次完成包校验、默认上下文加载、会话启动、导航注册及两张页面的XAML构造/布局；两种配置均显示crs.national-team 2.0.0为Started。预载对应桌面输出CRS.Worker.Abstractions/CRS.Worker.Ipc后再次核验，Debug/Release均通过。所有存储为新建隔离目录，不打开真实保险库，不显示窗口，不触发Loaded或后台查询。

本机插件disabled.json为[]；现有桌面日志未记录本次加载失败类别，进程清单未发现正在运行的CRS.Desktop.Wpf.exe。因此当前磁盘包未复现加载故障，不能在缺少用户界面错误类别时断言根因，也不能把后台历史读取失败归为插件包加载失败。前述15秒启动期限修复已在开发输出中，重新启动桌面后加载。验证日志见`artifacts/etf-plugin-load-debug.log`、`etf-plugin-load-release.log`、`etf-plugin-load-debug-host.log`、`etf-plugin-load-release-host.log`。

## 左上品牌区（布局与交付）

2026-10-21后续：ETF清单先读取、24只初始化目录及原件读取优化已完成实际维护更新，完整刷新当前实测24187ms；详细基准、目录重建和部署证据见[目录与份额加载](ETF_DIRECTORY_AND_SHARE_LOADING_20261021.md)。以上41秒及15秒失败记录保留为修复前现场，不代表当前新版本本轮验收状态。

保留交付说明指定的现有图标、产品名“小财牛”及口号“财富有数，投资有度”。品牌区与206px展开侧栏对齐，34px图标旁使用名称18px/口号10.5px的两行布局，移除口号跨至内容区的绝对偏移。标题栏拖动、窗口控制及导航行为不变。

使用真实主窗口XAML脱离窗口生命周期渲染，覆盖1280px及1040px宽度，不启动插件、连接服务或访问个人保险库；预览位于artifacts/brand-header-preview/。该预览验证布局，不冒充已运行窗口热更新。

沿工作区BrandPalette资源使用名称主色、口号次色及暖底色，不改图标源文件。Debug/Release编译及交互验证通过；开发包位于artifacts/pipe-brand-desktop-final，两个开发输出插件目录52个文件按可信索引逐项哈希一致。已运行桌面需重启载入新界面资源。
