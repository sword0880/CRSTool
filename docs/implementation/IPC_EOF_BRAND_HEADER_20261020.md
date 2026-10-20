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

## 左上品牌区

保留交付说明指定的现有图标、产品名“小财牛”及口号“财富有数，投资有度”。品牌区与206px展开侧栏对齐，34px图标旁使用名称18px/口号10.5px的两行布局，移除口号跨至内容区的绝对偏移。标题栏拖动、窗口控制及导航行为不变。

使用真实主窗口XAML脱离窗口生命周期渲染，覆盖1280px及1040px宽度，不启动插件、连接服务或访问个人保险库；预览位于artifacts/brand-header-preview/。该预览验证布局，不冒充已运行窗口热更新。

沿工作区BrandPalette资源使用名称主色、口号次色及暖底色，不改图标源文件。Debug/Release编译及交互验证通过；开发包位于artifacts/pipe-brand-desktop-final，两个开发输出插件目录52个文件按可信索引逐项哈希一致。已运行桌面需重启载入新界面资源。
