# 架构整改续记（2026-10-20）

基线：`01abcf9a9713e9bb7bb994704bc2d0628947106a`。对应[整改计划](../architecture/ARCHITECTURE_REMEDIATION_PLAN.md) ARCH-201～205；延续已合并的边界整改，不重新迁移已归属 Core/Jobs/UI 的源码。当前记录对应工作区改动，尚未提交。本轮不修改税务/FIFO/汇率算法。

## 实施范围

- **ARCH-201/203**：核验已合并源码归属和独立查询注册。生产项目不再跨目录链接国家队业务源码；纯查询插件回归进入宿主专项。
- **ARCH-202**：首次全量构建发现5处旧调度类型无法解析。搜索确认实际任务中心使用 `BackgroundServicePage` / Worker；删除无生产调用的 `ScheduledTasksViewModel`、旧页面及其旧调度器测试入口。保留当前任务中心、Cron编辑器及Worker调度测试。
- **ARCH-204**：补齐尚依赖“查询自动持久修复”的批查询、市场温度、股票原件和报告目录测试。损坏首次查询失败关闭，后台巡检明确登记Q15；恢复必须经受控巡检提交。增加真实SQLCipher连接的 `total_changes()` 检查：损坏/恢复查询不写库，重复损坏/恢复巡检不重复写入；通过临时SQLite触发器注入状态写入故障，验证同事务事件回滚且重跑恢复。
- **ARCH-205**：应用层新增账户、事件、净值及覆盖确认4个类型化用例，持有构造、完整性/来源/金额校验和JSON转换；净值币种从当前存储账户读取，不信任界面旧副本。WPF只映射表单和呈现用例错误。仍复用既有手工导入归档、数据校验与保存路径，手工质量保持Estimated，不改变历史冻结结果格式。
- 运维读工具：补采状态遇限流/繁忙有界等待；失败响应不覆盖最终验收文件。12:07回读原单日请求为 `Completed/Succeeded`；执行完成与覆盖验收分开。

## 验证与边界

Windows / .NET 10，采用全项目Release串行构建、隔离SQLCipher库、真实Named Pipes及独立Worker进程；测试来源为合成响应。受限环境首次文件/恢复测试失败后，在正常Windows权限下复验，不把环境失败写成业务结论。

| 验证入口 | 证据 |
| --- | --- |
| `dotnet build CRS.sln -c Release --no-restore -m:1` | `artifacts/architecture-20261020-build-verified.log`：0警告/0错误；随后测试调整各专项另行构建 |
| `tests/Verify-Architecture.ps1` | `artifacts/architecture-boundaries.log`：31项目清单、无循环依赖、层边界及绩效复杂度通过；新增UI录入业务规则回流门禁 |
| `CRS.Tests.Domain` | `artifacts/architecture-domain-regression.log`：绩效32组冻结结果逐字段一致 |
| `CRS.Tests.Infrastructure --performance-only` | `artifacts/architecture-performance-regression.log`：新手工用例、当前币种、拒绝输入无写入、取消、原件归档、冻结重放及备份恢复通过 |
| `CRS.Tests.Wpf`、`--cron` | `artifacts/architecture-wpf-regression.log`、`architecture-cron.log`：表单映射、应用拒绝呈现、成功清确认与既有界面/Cron回归通过 |
| `CRS.Tests.PluginHost` | `artifacts/architecture-pluginhost.log`：宿主/恢复隔离检查通过；本命令不含双插件页面加载验收 |
| `CRS.Tests.NationalTeamWatch` | `artifacts/architecture-etf-regression-windows.log`：258条PASS，含无写查询、幂等巡检、事务故障回滚及既有ETF/股票/市场温度保护；最终专项构建0警告/0错误 |
| `CRS.Tests.Background artifacts/worker-architecture-heartbeat-20261020` | `artifacts/architecture-background-regression.log`：145条PASS，含纯查询插件、超时/取消、22项公平调度、21项升级启动及9项权限专项；仅隔离环境 |
| `CRS.Tests.WorkerEtf artifacts/worker-architecture-20261020` | `artifacts/architecture-worker-etf.log`：45项真实独立进程/IPC合成来源检查通过 |

完整开发包位于 `artifacts/worker-architecture-20261020`；专用宿主回归使用 `artifacts/worker-architecture-heartbeat-20261020`。完整ETF包需要存储上下文，不能替代宿主无业务上下文的心跳夹具。

## 兼容、回滚与未关闭事项

新增应用层表单DTO和接口方法，桌面与Application应配套构建发布；不是Worker IPC协议变更。未改数据库Schema、服务名/SID、管道名、插件ID、密钥保护常量、既有业务DTO或金额计算。新增完整性作业仍沿前轮实现。

本轮未替换已安装服务，也未同步Debug/Release安装插件信任索引；新候选包通过隔离验证不等于实机升级。部署必须沿既有快照、Journal和恢复协议；不能直接覆盖正式运行目录。代码回退仍需配套还原应用层接口与调用方，不能回退已提交金融事实。

ARCH-101/102候选数据代次、跨机密钥恢复及灾备仍需实机演练；ARCH-103监控覆盖/连续日、具名黄金样本等独立验收仍开放；ARCH-104税务独立签认按既有用户顺序最后推进。ARCH-208～209券商共享事实及来源预算，以及ARCH-301 CI仍待继续。源码/测试级核验不等于这些发布门槛关闭，`ReleaseApproved=false`。


## ARCH-206续项：插件业务版本

已实施独立V1业务版本及兼容编解码器，适用于ETF/股票/预警请求、分页和导出块；Host IPC仍为2、包加载契约2.0、市场Schema2，互不代替。缺版本固定表示历史V1；未知版本拒绝且不自动降级。写任务返回Rejected/contract错误，查询返回明确业务契约错误，桌面不把错误/缺必填响应当成空成功。请求校验4096个UTF-8字节、32层深度、必填Operation和重复属性；同版本可选新字段允许忽略。详见[版本兼容矩阵](../architecture/NATIONALTEAM_BUSINESS_CONTRACT.md)。

本次修改EtfRequest/EtfQueryPage/EvidenceChunk构造签名，包内Core/Jobs/UI必须配套重建；不能以线协议兼容为由混用旧DLL。原已排队无版本命令仍按V1解析。监控审计工具逐只验收也校验业务响应版本。

Release全项目构建0警告/0错误：`artifacts/contract-build-final.log`；架构门禁通过：`contract-architecture.log`。独立契约专项21条PASS：`contract-checks.log`，包含真实Named Pipes旧响应兼容、未知版本拒绝及不兼容存储Schema拒绝打开。候选开发包为`artifacts/worker-contract-verified-20261020`；初期包`worker-contract-v1-20261020`保留其构建时点，不作为最终源码验收包。

状态为代码/隔离验证已实施；尚未部署到已安装服务。当前测试用已发布V1响应形状模拟旧服务，未覆盖全部历史发行二进制、SCM升级或生产签名路径，不将ARCH-206整体验收或任何P0发布门槛标Closed。


ARCH-206最终ETF默认回归：279条PASS（含21条契约专项），`artifacts/contract-etf-regression.log`。最终开发包Core/Jobs/UI三程序集哈希已分别与最终Release构建比对一致；未替换已安装服务。旧客户端/服务兼容证据为真实插件会话与真实管道V1形状夹具，不能写成所有历史二进制升级已通过。


服务化首轮在阻塞采集定位处超时（`contract-worker-etf.log`），未计为通过。夹具由“最近一页历史中的任意同Job执行”改为跟踪本次RunNow的持久RequestId及ExecutionId，并校验受理/提前结束状态，不放宽等待期限；避免翻页或先前执行影响目标定位。对应测试项目构建0警告/0错误：`contract-worker-build.log`；最终复验输出单列`contract-worker-etf-final.log`。


最终新包服务化复验45项通过（退出码0）：`artifacts/contract-worker-etf-final.log`，包括真实独立Worker、分页、导出、重启/命令去重、精确执行进展、采集期间配置排队与真实WPF配置界面。该测试仅使用隔离合成来源，不代表官网长期采集或正式服务已升级。


## ARCH-207续项：生命周期失控边界

1. Catalog每次插件调用明确记录插件ID及CreateSession/Start/DescribeJobs/Concurrency/Query/Stop/Dispose阶段；异常仅记录分类，不记录插件异常消息。Service组合根接入PluginLifecycle日志，保留原有不合作插件退出码2策略。诊断回调失败不能跳过退出保护。
2. 超时取消后沿用既有1秒协作收尾期限。任务仍未结束时，Catalog原子进入RequiresProcessRestart终止状态，异常携带插件/阶段；后续加载、查询和清理均拒绝，禁止对仍在运行的会话Stop/Dispose/Unload。
3. Worker健康返回PluginUnresponsive；拒绝新增查询/命令，分派、候选执行和队列唤醒检查终止状态，等待进程按既有策略退出。已在运行的其他插件仍可能随进程退出中断，不能宣称同进程故障隔离。
4. 协作取消后迟到返回不能冒充成功；已结束任务主动抛TimeoutException不能被误认为收尾等待超时。普通初始化故障仍只隔离对应插件；停止抛错仍继续尽力Dispose并汇总。

新增`--lifecycle-only`专项29条PASS：`artifacts/lifecycle-checks.log`，覆盖四阶段不合作、终止后拒绝复用、迟到成功、健康状态、诊断失败、已完成超时异常分类，以及四个测试自有进程真实退出码2并先留下分类诊断。使用300ms仅为故障夹具；生产调用默认15秒、收尾1秒没有延长。运行期Job执行的取消与事务保护沿用既有Worker实现和回归，不新增独立调度器或PluginRunner。

全项目Release初轮构建0警告/0错误（`lifecycle-brand-build-verified.log`），后续受影响后台目标构建0警告/0错误（`lifecycle-tests-build-final.log`）；架构门禁`lifecycle-architecture.log`通过。新宿主隔离开发包：`artifacts/worker-lifecycle-heartbeat-20261020`，仅用于宿主可靠性套件，不能作为完整ETF安装包。

本项为可信第一方同进程的停止/诊断保护，非OS安全沙箱。尚未部署到已安装服务；SCM自动恢复、真实崩溃/断电、全部插件Schema及金融黄金样本门槛继续开放。高风险第三方插件或独立低权限Runner没有纳入本轮。

图标按用户追加的交付说明同步接入，视觉素材/验证记录单列[品牌记录](PRODUCT_BRAND_20261020.md)，不与后台版本/运行标识混改。


ARCH-207最终后台完整回归175条PASS（含29条生命周期专项，进程退出码0）：`artifacts/lifecycle-background-regression.log`。最终全解决方案Release构建0警告/0错误：`lifecycle-brand-final-solution.log`；最终架构门禁：`lifecycle-brand-final-architecture.log`。图标Debug/Release构建与专用真实窗口预览均通过，详见品牌记录。代码、密钥、Schema及正式服务部署边界保持分开，不以本轮隔离验收关闭SCM/灾备发布门槛。


## ARCH-208续项：IBKR共享来源读取（In Progress）

已新增Application不可变来源记录和Infrastructure统一读取器；税务、现金对账、绩效与账号读取消费同一来源模型。证券流水同步绩效实际传递同一个不可变来源快照，避免重新解析；独立绩效导入复用同一读取器。富途维持既有规范事实路径。税务/FIFO/绩效算法与历史格式未改变，源头保留未知字段和记录。

合同、调用链及仍待完成的版本持久化/完整类型化事实见[共享来源事实](../architecture/BROKER_SOURCE_FACTS.md)。状态是来源层迁移已实施，不把ARCH-208全项或独立金额Golden标Closed。

验证证据：`artifacts/broker-facts-build-final.log`全项目Release0警告/0错误；`broker-source-checks.log`18条PASS；`broker-tax-before-after.log`两份IBKR税务解析与工作前Debug构建逐字段一致；`broker-tax-verification.log`既有68项验证通过；`broker-performance-regression.log`固定券商导入JSON、SQLCipher、共享导入去重、冻结/重放、取消及备份恢复通过；`broker-facts-wpf.log`交互通过；`broker-facts-architecture.log`架构门禁。

第一次复杂度检查因方法参数从XElement[]改名为BrokerSourceRecord[]未匹配既有基线键而拒绝；核对分支68、行55均未增加后，仅迁移该签名键，未捕获/提高基线，也未重新生成任何金融期望值。

回滚边界：本轮无Schema或历史数据迁移，仅代码和内部委托配套更新；回退应同时还原来源模型接线、回调及投影，保留原始事实与已存绩效历史，不删除或回写私人资料。未更新Debug或已安装程序，Debug旧程序集已归档作为本次对照。独立真实金额签认仍按用户既有顺序最后处理。


ARCH-208本轮最终源码全项目Release构建0警告/0错误：`artifacts/broker-facts-final-solution.log`。既有金融冻结文件`performance-imports-v1.json`未改变；复杂度预算数值未改变。共享来源层新增门禁、技术回归和文档已同步，未部署或更新已安装程序。


## ARCH-209续项：宿主共享来源预算（In Progress）

正式Worker组合根创建唯一来源注册表，通过可选能力绑定插件身份；国家队正式HTTP网关已接入租约。SSE多域名共用额度，SZSE及已审核发行人独立；每来源一个在途调用、三秒开始间隔、跨插件FIFO、32项等待与单插件8项上限。Retry-After、403/429、临时故障和内容校验反馈持久共享，排队取消不污染失败计数。

新增独立派生密钥的SQLCipher来源预算库，保留旧插件来源库。会话初始化预合并全部旧冷却，逐次申请再次保守合并；宿主先登记来源失败，插件库写失败仍保护其他插件。宿主预算读写失败锁定失败状态，不能短暂恢复后继续放行。GetHealth新增可选来源状态、期限、队列和计数，不改变IPC版本或金融数据格式。完整边界见[共享来源预算](../architecture/SHARED_SOURCE_BUDGETS.md)。

验证：`artifacts/source-budgets-solution.log`全解决方案Release构建0警告/0错误；`source-budgets-architecture.log`架构门禁通过；`source-budgets-checks-final.log`43项共享预算检查通过，含两个真实网关、不同插件上下文、真实三秒间隔和两种存储故障注入；`source-budgets-policy-regression-full.log`既有来源策略20项通过。网络均由固定响应代替，不访问官网。

首轮既有来源/后台完整回归在沙箱临时目录中失败，未计为通过：后台在设置ACL后的目录无法打开SQLite，来源原件持久化导致两市隔离断言未通过。改用正常Windows权限重跑隔离夹具后，来源20项通过，不修改生产逻辑或放宽断言。首轮预算夹具使用了未获准的发行人测试路径，后改为现有审核白名单内的报告路径，未扩大URL白名单。

新建`artifacts/worker-source-budgets-20261020`仅为宿主可靠性测试的心跳开发包，不包含ETF正式发行包，也未签署生产签名；不替换已安装服务。长期持续超载、真实服务/源站恢复、正式升级和断电/磁盘满验收仍开放，ARCH-209不标Closed。独立金融金额Golden继续最后验收。

ARCH-209最终后台完整回归：175 条PASS，进程退出码0，见 artifacts/source-budgets-background-regression-full.log（含生命周期、调度、升级启动、真实隔离进程及ACL巡检）。开发包宿主/契约与最终Release程序集哈希一致，网关测试程序集与Jobs最终Release一致，摘要见 artifacts/source-budgets-evidence.json。本项仍为源码与隔离验证阶段。



## ARCH-209后续：故障健康与有限持续负载

来源预算存储故障后，GetHealth现在保留Code=Ok及Health.State=StorageFault，Sources=null明确表示快照不可用；新增执行/插件查询继续失败关闭。启动时读取三个来源状态，拒绝负数/超大失败计数、损坏日期、未知库版本和版本1缺表，不能静默重建表后消除旧冷却。SQLCipher版本仍为1，本轮仅加强校验与诊断，不迁移金融数据或运行标识。

新增恢复注入验证已计入来源专项56项，最终日志为 artifacts/source-budgets-recovery-final-checks.log。验证了反复健康查询、写故障后仍拒绝访问、重启保留故障前已经提交的冷却，以及新插件不能绕过。首轮注入发现InvalidDataException没有被既有StorageError分类包含，记录在 source-budgets-recovery-checks.log；已显式补足并以最终56项复验通过，不将失败首轮计为验收。

新增 --source-budget-soak 90：真实时间与SQLCipher，固定8个模拟插件、120个消费者，对三个来源持续争用并中途注入429/503，保留三秒生产间隔。验证有界等待、各插件进展、拒绝后无新租约、独立来源继续推进、取消排空和重启冷却。仅租约压力，无网络请求，不冒充官网吞吐、跨日或内存泄漏验收。

最终全解决方案Release0警告/0错误：artifacts/source-budgets-recovery-final-solution.log；架构门禁：source-budgets-recovery-final-architecture.log。新宿主可靠性心跳开发包为 artifacts/worker-source-budget-recovery-verified-20261020；仅用于隔离后台回归，未替换已安装服务，不具备生产发行签名。完整ETF发布、真实升级/源站恢复、跨日容量、物理断电/磁盘满及独立金融金额验收仍开放；ARCH-209保持In Progress。

最终复验全部退出码0：共享/恢复专项 56 项（source-budgets-recovery-final-checks.log），90秒持续负载 19 项（source-budgets-soak-90s-final.log），后台完整回归 175 项（source-budgets-recovery-final-background.log）。有限负载采样 862 次，队列峰值 32，单来源在途峰值 1，过载拒绝 27138 次、冷却拒绝 67570 次；全部消费者排空、重启保留已提交冷却。测试、候选包与最终Release宿主程序集哈希一致；证据汇总 artifacts/source-budgets-recovery-final-evidence.json，详细负载摘要 source-budgets-soak-90s.json。以上为有限隔离验证，未部署正式服务。



## ARCH-208后续：绩效版本归档与真实旧格式兼容

新增Application不可变BrokerImportProvenance V1，记录原件/批次、券商、实际读取版本、投影版本及单份导入事实摘要。IBKR独立导入仍只读取一次，共享导入复用实际来源快照；Futu保留既有共享规范事实路径。原件与版本在同一SQLCipher事务提交；版本写入故障整笔回滚，载入核对版本指向的事实摘要。新计算冻结有效来源版本，重放沿用原记录，结果指纹覆盖新非空版本集合；旧历史使用原版指纹载荷，不回填、不重签。

绩效设置JSON新写格式2、兼容读取格式1，旧格式无版本信息则保持未知；旧原件重复导入不以当前版本冒充过去的解析。金融PerformanceData版本仍为1，三张表、保险库主Schema及税务/FIFO/绩效算法不改。旧二进制严格解码不支持新增字段，不承诺反向读取新资料；回退必须保留新库并按备份/恢复流程处理，不能删字段或修改版本号冒充旧数据。

新增21项版本持久化检查纳入 --performance-only：实际版本/摘要绑定、事务失败回滚、指纹篡改/剥除拒绝、备份恢复、真实格式1缺字段读取、旧指纹、重放与关闭/重开。完整绩效回归通过，见 artifacts/broker-provenance-performance-regression.log；既有IBKR/FUTU金融冻结JSON未改写，技术税务前后对照2份通过（broker-provenance-tax-before-after.log）。应用层与WPF回归通过：broker-provenance-application.log、broker-provenance-wpf.log；既有68项验证正常Windows权限下通过：broker-provenance-verification-full.log。沙箱首轮63项通过、5项文件权限失败保留在 broker-provenance-verification.log，不计为验收。

全解决方案Release0警告/0错误：artifacts/broker-provenance-verified-solution.log；架构门禁：broker-provenance-verified-architecture.log。未增加复杂度预算或重写金额期望。首轮兼容夹具误用默认数值枚举序列化写旧历史，已改为原Codec字符串枚举格式；不放宽生产解码。首次架构检查与WPF构建并行时读到临时csproj，最终在构建结束后复核通过。

本轮关闭的是绩效版本归档与向前读取旧资料的开发缺口。税务独立历史的显式解析版本、完整类型化投资事实、跨版本投影差异及独立真实Tax/Performance金额签认仍开放；ARCH-208整项保持In Progress。未修改个人保险库或部署已安装程序。

## ARCH-209后续：真实Worker进程共享与崩溃重启

新增两个仅用于隔离验收的可信插件，不联网、不在查询中写状态。通过正式WorkerPluginContext及持久命令，插件A实际向共享预算提交模拟429，插件B申请同一来源被拒绝；另一市场仍可执行。使用真实Windows管道，以本次RequestId/ExecutionId跟踪完成结果，避免最近一页历史或Accepted被当作完成。

真实强杀测试自有Worker进程后，以相同目录/密钥重启，再核验来源冷却截止完全相同、插件B仍被阻断、另一来源无遗留占用，服务密钥没有重建。19项通过、退出码0：artifacts/source-budget-process-final.log。使用已验证宿主心跳开发包 artifacts/worker-source-budget-recovery-verified-20261020；本轮未改生产Worker或安装服务。夹具首轮未给ManualOnly任务参数被ParametersRequired拒绝，已提供固定空对象参数，保留宿主门槛，不将首轮失败计为验收。

该验证证明隔离正式宿主的跨插件共享及进程崩溃恢复，不代表SCM/LocalService安装升级、真实源站恢复或物理断电验收。十分钟有限负载 --source-budget-soak 600 已完成，实际602.61秒、19项通过、退出码0，5722次采样，队列峰值32、单来源在途峰值1，过载拒绝181676次、冷却拒绝440278次。验证八个插件均有进展、取消后五秒内排空、重启保留两市场冷却且未故障来源继续推进；网络请求为0。证据：artifacts/source-budgets-soak-600s.log 与 source-budgets-soak-600s.json。跨日容量与生产发布门槛仍开放。

## 数据库替换边界补强

按新增要求收拢数据库提供方耦合。共享来源预算策略改为依赖ISourceBudgetStore，由Program装配SqliteSourceBudgetStore；策略不再持有目录、密钥或创建数据库实现。存储端口以SourceBudgetState交互，Save保留原子持久提交约束，生命周期随注册表释放。运行期SQLite异常在适配器中转为IOException，内部原因保留，故障锁定与健康拒绝语义不变。Schema、密钥、事务时序和预算算法未改。

新增7项替换存储检查：无数据库健康读取、冷却写入与共享拒绝、网络前预留、生命周期释放、重建后状态复用、读取失败拒绝启动及释放；内存替身不能作为另一个生产数据库的验收。原SQLCipher故障、加密与恢复回归同时通过，--source-budget合计63项，退出码0（artifacts/database-port-source-checks.log）；架构门禁新增策略和端口禁止数据库实现泄漏检查（database-port-architecture.log）。

[数据库抽象与替换边界](../architecture/DATABASE_PORTABILITY.md)列明事务、幂等、精度、加密备份与迁移要求，以及绩效、任务和插件等剩余耦合。此次完成共享预算一处边界，未声称全项目已支持其他数据库；此前十分钟/进程恢复证据对应此前构建，未将其冒称为本次新适配器构建的复验。

本次完整解决方案Release构建0警告/0错误：artifacts/database-port-solution.log；工作区diff空白检查通过。未更换已安装服务、未变更数据库格式或迁移个人资料。

## 数据库抽象续项：Worker任务仓储异常与原子操作

ITaskRepository补充原子提交、业务冲突和不透明游标契约；EncryptedTaskRepository在18个公开操作外层转换SQLite异常为IOException，内部异常保留。原SQL/锁/事务主体不变，转换位于using事务释放之后，审计记录 artifacts/task-storage-operation-audit.json 核对18个原操作主体忽略缩进后完全一致。BackgroundRuntime移除具体SQLite引用；尚未收拢的插件持久化异常以BCL DbException兼容分类，保留原失败关闭行为，不能宣称全项目业务层均已无数据库异常依赖。

新增专项 --task-storage-boundary 共30项、退出码0：真实SQLCipher注入验证Save的计划修订/取消/回执共同回滚、Begin执行与占用共同回滚、End结果/退避/回执共同回滚，业务冲突/参数错误不误转存储故障，真实读取失败后健康仍可查询（12项）；原四类故障保护分别验证IO及通用数据库异常（18项）。日志 artifacts/task-storage-boundary-checks.log。使用隔离库和内存加载的测试插件，不联网、不修改个人资料或已安装服务；未将通用异常夹具当成其他数据库实测。

本次完整解决方案Release构建0警告/0错误：artifacts/task-storage-boundary-solution.log。仓储操作主体审计覆盖18个公开方法，不能据此替代真实数据库故障检查。

最终架构门禁通过（task-storage-boundary-architecture.log），含新增后台运行规则/任务仓储提供方边界；共享来源预算回归63项全部通过（task-storage-source-regression.log），退出码0。工作区空白检查通过。
