# 配置ETF池的官方报告目录

本文保留实施时点，当前模块边界、开放风险及正式部署以[当前架构状态](../architecture/ARCHITECTURE_STATUS.md)为准。

状态：源码接线与隔离验证；不代表正式Worker已采集全部ETF报告，也不代表已核实机构持仓。2026-10-27。

## 功能与证据边界

“资金与持仓 → 机构ETF持仓”从用户启用的ETF配置读取选择池。按日期区间向后台提交官方定期报告检索，显示已登记目录、日期、原时间文字、标题及官方PDF地址；明确选择后才提交原件登记。原件随后沿现有持有人候选入口解析并人工核验。

目录与持仓分开：`NotSearched`保持未知；`IndexedUnreviewed`只表示已登记目录线索；原件、来源、版本、时间、请求身份或Q15异常为`EvidenceUnavailable`，保留历史线索但禁止界面据此登记新证据。真实成功的零目录有独立检索证据，仍不能推出某机构零持仓。

两所目录均没有向调用方提供冻结快照证明。翻页声明总数/行号一致仍不能证明市场历史公告绝对完整；`CoverageProof=UnversionedDirectory`、`HoldingsConfirmed=false`固定保留。公告日期及深市原时间字符串不升级为准确公布时点、报告期或持仓批准。

## 按功能分层

| 层次 | 归属 | 职责 |
| --- | --- | --- |
| 领域与端口 | Core的`Domain/EtfHoldings`、`Application/EtfHoldings` | 目录合同、状态、类型化读写仓储及客户端端口 |
| 应用 | Jobs的`Application/EtfHoldings/EtfHoldingsUseCases` | 只访问该功能仓储端口；与预警协调分离 |
| 后台任务 | Jobs的`Background/EtfHoldings` | 固定请求、两所分页、进度、持久命令与延期状态 |
| 基础设施 | Jobs的`Infrastructure/EtfHoldings` | 官方协议解析、独立目录表和事务、原件及来源复核 |
| 客户端与展示 | Watch的`Application/Worker/EtfHoldings`、`Presentation/EtfHoldings` | 稳定请求编号、完整分块读取、配置池选择及明确原件登记；不含HTTP、SQL或PDF解析 |

后台当前九组类型化用例、七个组合客户端合同；界面依赖具体功能端口。目录表`nt_etf_report_search`和`nt_etf_report_page`共享普通SQLite连接、存证及原命令事务，不拥有已批准持仓、预警事件或通知事务。收入报税加密规则未改。

公共`IPluginJsonHttpGateway`只增加可选JSON查询能力，不把国家队许可规则外移。`OfficialHttpGateway`仍拥有官方地址、固定POST字段、Referer、来源预算、冷却及期限；GET/POST共用同一来源策略。公共`CRS.Common/Serialization/JsonDocumentBlocks`保证整份文档连续、有界及摘要一致。

## 两所真实协议

- [上交所基金公告页](https://www.sse.com.cn/disclosure/fund/announcement/)使用基金专用`COMMON_PL_JJXX_JJGG_NEW_L`，平面`result`，`pageHelp.data`为镜像。校验`SECURITY_CODE`、`NUM`、报告分类、日期、分页及相对基金PDF路径，不能套用股票公告分组接口。
- [深交所基金公告页](https://www.szse.cn/disclosure/fund/notice/index.html)的历史检索使用固定JSON POST `/api/disc/announcement/annList`，`annType=fund`只用于分类读取；查询包含`seDate`、单只`stock`、`fundinfoNotice_disc`、年报/中报/季报分类、50条分页。响应为`announceCount`及`data`，校验证券数组、公告ID、时间原文和附件格式/路径。PDF来源为`disc.static.szse.cn/download`。

请求上限1100天、1000条、40页；单页2 MiB、整次8 MiB。拒绝重复JSON字段、错误证券/页码/日期、跨页重复与总数漂移。两所响应不回显完整查询条件，故额外以实际请求URL、规范POST正文摘要、来源记录及原始字节绑定本次检索。

2026-10-27保存的沪市510300、深市159915各6条响应及独立收据已加入[回归夹具](../../tests/fixtures/national-team/etf-reports-20261027/README.md)，默认测试复制到输出后校验收据摘要及TLS核验标志。它们是公开来源协议样本，不是已安装Worker的采集记录。研究下载曾遇到DNS/代理TLS错误；沪市只对同一官方URL按既有策略直连并保留证书核验，没有绕过403/429或关闭TLS。

## 事务、原件及并发复核

全部页面解析与合并通过后才开始写入；检索身份、每页来源和持久业务命令在同一事务提交。第二页取消或SQL故障不留下半批目录、成功命令或覆盖旧目录。原始文件可以因取消留下未引用字节，不因此自动删除原件。

提交前按实际完整读端合同验证1 MiB序列化上限；超限拒绝并保留旧目录，避免“检索成功但无法完整回读”。重放要求命令、证券、日期范围、页数一致；客户端待核对请求有128项上限，不静默丢弃结果未知的请求。

读取使用同一独立只读快照，核验载荷摘要、页证据、URL/正文绑定、provider/parser/status、接收与完成时点、Q15及同一份实际原件字节。未等巡检就发生的原件损坏或文件缺失也即时撤去有效状态；字节恢复而Q15仍Open时仍不放行。

## 当前验证与剩余事项

目录专项77项通过，覆盖保存的两所官方响应、解析负例、来源准入、只读、取消/故障回滚、原件异常、模式损坏和界面竞态。完整Release构建零警告、零错误。

完整候选包经真实Worker、监督Runner、实际桌面排队适配器和Windows管道的20项检查通过：两市各6条线索、同请求重放不再HTTP、明确选择原件后下载、510300年报11条待核验候选、已审核持仓仍零登记、前后同一健康宿主。采用保存的公开字节和隔离库，没有联网回填正式资料或审核金融事实。

完整NationalTeam默认隔离回归通过、退出码0。真实页面回放640/1040/1280 DIP和100/150%密度共6张渲染通过：窄窗日期区间整体换行，日期/类别/标题列分别至少110/100/260 DIP，允许横向查看后续来源列，表格保留至少484 DIP高度；不会把日期压成截断的年份。已查看640 DIP目录和1040 DIP无集中声明工作台截图。

来源样本不覆盖全部配置ETF；持有人PDF解析目前仍限定已核对的两份510300年度原件模板。具名主体及独立所有权、准确公布时点、跨期可比口径、其他ETF/报告覆盖仍需实际证据与人工核验。目录发现不能替代这些门槛，也未关闭联合退出模型、行业方向投影或周期有效性目标。

## 2026-10-28 工作区扩展

159915的深交所2024年报目录实际公告日期为2025-03-31，附件存储目录为2025-03-30；原先要求两日一致会拒绝有效线索。现只要求附件路径的日期有效，保留其原路径，同时以公布字段约束检索范围；证券数组、UUID、官方域、完整分页、来源原件及事务核验继续执行。新增保存响应与下载收据位于[目录夹具](../../tests/fixtures/national-team/etf-reports-20261028/szse-159915-annual-2025.receipt.json)，不作为金融审核。

新增159915的2024/2025年度报告固定摘要/路径档案，分别核对跨页表头与单页表，各读取11行并保留联接基金另列。两份年报百分比列自带百分号，分母为上市份额；不沿用510300的排除联接基金脚注。2026中期报告只披露持有人汇总结构，明确返回`NotDisclosed / AggregateOnlyNoNamedTable`，不把它当作零持仓、清仓或可与年报直接比较的具名披露。

正文候选保留`NeedsReview`，准确公布时刻、法人ID及独立经济持有关系不自动填充；未知字节仍为`UnsupportedTemplate`。`PdfTextLayout`迁入CRS.Documents，基金模板及金融规则留在Jobs；客户端候选入口归入EtfHoldings目录，拒绝未知版本、披露状态、缺行、错序及证券串用。该扩展不改写已审核持仓，不触发退出事件或通知，也未部署到正式服务。

## 2026-10-29 正式资料回读续项

上述159915版式与目录日期修正已包含在当前Committed发行中，当前部署以[唯一状态索引](../architecture/ARCHITECTURE_STATUS.md)为准。本轮未改业务或再次安装；通过正式后台检索当前24只配置ETF在2025-01-01至2026-10-29发布的定期报告，24条原命令均Completed/Succeeded，取得298条目录。单任务RunNow在已有执行时返回AlreadyRunning并结束新命令，不代表该基金排队；首次并行拒绝原回执保留，之后按基金串行执行。每份成功目录绑定本次稳定命令ID；未冻结的来源目录仍不能证明完整历史披露。

159915三份PDF由正式后台下载、登记并回读：2024/2025年报各11行NeedsReview（含另列联接基金），四行给出两个汇金登记法人的待审线索；2026中期为NotDisclosed，不能补零或判断清仓。既有四份其它ETF原件仍UnsupportedTemplate。510300首份年报下载连接失败，执行进度保存RequestFailed与来源退避，外层管理命令只返回Failed/OperationFailed；保留故障而未写原件、候选或审核记录，停止该来源后续下载，深市独立继续。此路径的失败分类/恢复尚未关闭。

报告覆盖82项、既有研究40项只读查询全部完成且同PID；7项计划、24只ETF配置、29条股票投影、10条旧执行及4份既有原件保留。已核验具名持仓仍0行，实际持仓未知，预警资料验收false。可复核数据、命令回执、哈希与待审表格集中在[本轮证据](../../artifacts/etf-report-coverage-20261029/acceptance.json)及[审核候选包](../../artifacts/etf-report-coverage-20261029/REVIEW_PACKET.md)，不新增并行的当前状态索引。

### 原件下载失败的恢复边界

后续工作区修正只在原件HTTP下载调用内捕获连接、超时及带原因的来源预算异常，返回DocumentConnectionFailed、DocumentTimedOut、DocumentSourceCooldown或DocumentSourceQueueFull；非200响应保留状态码。回执保留北京时间retryAt，等待取既有30分钟默认与本地/宿主来源截止中的较晚者，排队容量默认30秒；403等长冷却不缩短。调用方取消继续传播，原件内容/存储故障不进入网络异常捕获；无效PDF仍Rejected。网关无宿主时的本地冷却亦使用既有SourceBudgetUnavailableException，避免丢失原因和Until。

24项合成入口检查覆盖无写入、取消、无效原件、真实HTTP适配器403及同ID恢复；候选经真实宿主、监督Runner、加密命令库和普通研究库验证跨重启Pending/NotBefore及到期恢复。它们不证明官方源可用，也不把历史Failed命令改成成功；旧终态保留，正式再次尝试须独立稳定ID并关联旧失败。安装与最终验证状态仅看[当前状态索引](../architecture/ARCHITECTURE_STATUS.md)。

该修正已随代次`dd4ac7dd954d412a9a080921689cf82f`于2026-10-29 21:36:43正式Committed。正式510300重试`c6f5d272480b428894347483d3a27d0d`保存Pending/RetryDeferred，来源截止2026-10-30 01:43:19且没有RequestStarted；原失败终态仍保留。延后回执会清空执行ID，具体分类执行按本次唯一管理任务时间窗及同一等待截止核对，未将其描述为直接历史关联字段。实际源恢复后的下载仍未验收；22:01的82项覆盖查询确认24只/298条目录及7份原件可读，具名持仓仍待人工核验。见[正式验收](../../artifacts/etf-document-recovery-20261029/acceptance.json)。

## 2026-10-29：159919年报候选扩展

已从正式目录保留的深交所官方URL研究下载嘉实沪深300 ETF两份年报，维持TLS校验、不跟随跳转，未把研究文件直接写入正式库。2024报告公告日期2025-03-28，附件路径2025-03-27不能替代公告；2025报告公告日期2026-03-30。两个固定SHA256及完整路径约束`harvest-csi300-holders-pdf-v1`，未知原件不继承模板。

表格各11条，含另列联接基金，单位份、分母上市总份额，脚注限定场内份额。2024第11行名称跨物理第62至63页；2025续表从第66页前三行延至第67页第4至11行。使用外层CRS.Documents坐标及单元格能力，不在界面层解析或计算。独立图片摘录22行，完整原件82项检查通过；黄金报告和研究下载收据保存在[本轮来源目录](../../artifacts/harvest-holders-20261029/research-downloads.json)，后续候选及正式状态只索引[权威状态文件](../architecture/ARCHITECTURE_STATUS.md)。

汇金投资、汇金资管分别保留登记名称，四条线索均NeedsReview；原件确认和独立所有权确认均false，不生成正式具名持仓、退出判断或资金流。此扩展不代表24只ETF披露覆盖完整，也不自动认定其它国有登记持有人属于目标国家队主体。
