# IBKR Activity Flex XML 导入说明

当前 WPF 版本支持 Activity Flex XML 本地导入和 Flex 下载；不支持普通 IBKR CSV／PDF 或税务表单。

## 1. WPF 页面使用

1. 在“数据导入”页面选择 IBKR 和测算年度。年度必须明确填写，当前范围为 2000—2100，不自动从文件推断。
2. 点击“选择年度报表”，选择一份年度 Activity Flex XML，或多份覆盖连续月份的 XML。多个账户独立处理库存。
3. 年初有持仓时，点击“选择期初资料”提供上一年 12 月 31 日 LOT XML；如用 C# 结转，先勾选“使用 C# LOT 结转 JSON”。
4. 只有实际无持仓才勾选“确认没有期初持仓”；期初来源与无持仓确认不能并用。
5. 勾选“确认报告未过滤账户、证券或现金类型”。用户确认不能替代券商对账。
6. 可选择保存含敏感数据的完整规范化快照，默认不持久化交易明细。
7. 点击“导入并开始计算”，在税务和核对页面查看结果，从“报表导出”保存 Excel。更改文件、年度、券商或确认项会清除旧结果。

富途使用同一个导入页面，根据券商选择对应文件。历史页恢复冻结结果，尚无历史复算按钮。

## 2. 在 IBKR 导出什么

官网入口：[IBKR Client Portal](https://portal.interactivebrokers.com/en/trading/client-portal.php?menu=B)。在网页端登录后，进入 **业绩与报表 → 自主查询**（英文界面为 **Performance & Reports → Flex Queries**），创建“活动自主查询”（Activity Flex Query）。手机“下载税表”是另一个入口，不用于导出本项目所需的历史 Activity Flex XML。

### 2.1 查询历史年度（例如 2025 年）

手机“下载税表”或 Activity Flex 模板中的“过去 365 个日历日／最近 N 个日历日”是相对当前日期的期间，不能在 2026 年 10 月取得 2025 年全年。IBKR 当前 [Activity Flex 模板说明](https://www.ibkrguides.com/orgportal/performanceandstatements/activityflex.htm)列出的期间也是相对日期；不要把“过去 365 天”的文件当作 2025 年年度报告。

需要 **Activity Flex XML** 时，先在网页 Client Portal 建立或复用包含下述字段的 XML 模板，然后在 [Flex Web Service 设置](https://www.ibkrguides.com/clientportal/performanceandstatements/flex-web-service.htm)启用服务。IBKR 官方 [SendRequest 文档](https://www.interactivebrokers.com/docs/web-api/flex-web-service/using-flex-web-service/generate-the-report)允许同时传入 `fd` 和 `td` 覆盖模板的相对日期：2025 全年使用 `fd=20250101`、`td=20251231`；2025 年的期初 LOT 快照使用另一份 Open Positions / Lots 模板，日期为 `fd=20241231`、`td=20241231`。发起请求时 `q` 填模板的数字 **Query ID**，不是模板名称；请求成功会返回 **ReferenceCode**。随后按 [GetStatement 文档](https://www.interactivebrokers.com/docs/web-api/api-reference/get-statement)下载 XML，此时 `q` 改填 ReferenceCode。访问令牌和原始 XML 含敏感账户信息，不要发到聊天或代码仓库。

网页 **Performance & Reports → Statements** 另有“Annually”或“Custom Date Range”，可用来查看 2025 年普通活动报表；其 PDF／CSV／HTML 并不是本项目当前解析器所需的 Activity Flex XML。参见 IBKR 的[普通报表运行说明](https://www.ibkrguides.com/clientportal/performanceandstatements/runstatement.htm)。

使用 **Activity Flex Query**，输出选择 **XML**。不要上传普通 PDF 对账单、Trade Confirmation 报告、Flex Web Service 请求回执或手工转换的 CSV。

Activity Flex 可以配置具体字段与格式；Trades 可以包含多个明细层级。配置应包含全部相关账户、证券和现金类型，不设置符号或现金类型过滤。程序能检查文件期间和字段，但无法证明导出模板没有预先过滤记录。

官方参考：[创建 Activity Flex Query](https://www.ibkrguides.com/advisorportal/ug/activityflex.htm)、[Flex 查询说明](https://www.ibkrguides.com/complianceportal/flexqueries.htm)。

### 2.1 活动报告

| 部分 | 需要的配置 | 本系统用途 |
| --- | --- | --- |
| Trades | 选择 Executions；保留 `levelOfDetail=EXECUTION` | 作为买卖成交输入 |
| Cash Transactions | 全部类型、明细层级（DETAIL）；不要只导出股息 | 股息、利息、预扣税与退回、出入金、未支持现金事件 |
| Open Positions | Summary | 年末持仓数量核对，不用于初始化本年度买入成本 |
| Corporate Actions / Transfers 等 | 建议包含实际发生事件 | 识别尚未支持的成本变化并阻止受影响成本计算 |

未发生事件时，空的 CashTransactions 区段表示该导出范围内没有现金记录；整个区段缺失不能直接当作全年零收入。若所有输入均缺少该区段，系统会要求补充收入数据。

### 2.2 当前解析器字段要求

以下为 XML 属性名；IBKR 页面显示的字段名称可能包含空格，例如 Trade ID 对应 `tradeID`。在 Flex 模板中保留这些字段。

| 位置 | 字段 | 规则 |
| --- | --- | --- |
| FlexStatement | `accountId`、`fromDate`、`toDate` | 必须明确账户和覆盖期间 |
| Trade | `levelOfDetail`、`tradeID`、`assetCategory`、`symbol`、`conid`、`currency` | 仅 EXECUTION 参与计算；Conid 为券商内证券标识 |
| Trade 时间 | `dateTime`，或 `tradeDate` + `tradeTime` | 必须含时分秒；同时提供日期时要求日期一致 |
| Trade 数量与方向 | `buySell`、`openCloseIndicator`、`quantity`、`tradePrice`、`multiplier` | BUY 数量为正，SELL 为负；首版多头股票／ETF，乘数 1 |
| Trade 金额与费用 | `proceeds`、`netCash`、`ibCommission`、`ibCommissionCurrency`、`taxes` | 佣金支出为负；首版要求佣金与成交同币种，交易税为 0 |
| Trade 盈亏核对 | `fifoPnlRealized`（页面字段 Realized PNL） | 可选；卖出成交缺失该字段时显示未核对，不当作零收益 |
| Trade 更正识别 | 建议保留 `origTradeID`、`notes`、`transactionType` | 更正／撤销或非普通成交进入待复核；不能靠删除这些字段使事件变为受支持 |
| CashTransaction | `transactionID`、`dateTime`、`type`、`currency`、`amount` | 日期可只有年月日；有层级时必须是 DETAIL |
| OpenPosition（期末） | `levelOfDetail=SUMMARY`、`assetCategory`、`symbol`、`conid`、`currency`、`position`、`side` | 支持多头股票／ETF 数量核对；提供 reportDate 时须等于文件截止日期 |

记录中的 accountId 如果存在，必须与父级 FlexStatement 一致。首版不支持 Model 分区报告，需导出账户级数据。

官方字段参考：[Trades](https://www.ibkrguides.com/reportingreference/reportguide/tradesfq.htm)、[Cash Transactions](https://www.ibkrguides.com/reportingreference/reportguide/cash%20transactionsfq.htm)、[Open Positions](https://www.ibkrguides.com/reportingreference/reportguide/open%20positionsfq.htm)。XML 属性拼写同时参照开源解析器的[数据类型声明](https://github.com/csingley/ibflex/blob/master/ibflex/Types.py)核对，代码实现未依赖该第三方库。

### 2.3 日期与时间格式

- 日期：`yyyyMMdd` 或 `yyyy-MM-dd`。
- 推荐成交时间：`20250102;153000` 或 `2025-01-02;15:30:00`。
- 也支持 ISO 日期时间；同账户证券不能混用带时区与无时区记录。无时区时间按报表原始时间排序，不自行推定为北京时间或 UTC。
- 活动记录日期须位于对应 FlexStatement 的报告期间内。

### 2.4 期初成本文件

文件截止日期必须是目标年度上一年 12 月 31 日，账户必须在目标年度活动报告中。每个账户只接收一个期初快照；一个文件可以包含多个账户。

OpenPosition 必须提供 LOT 批次，包含：

- `assetCategory`、`side`、`symbol`、`conid`、`currency`、`multiplier`。
- `position`：剩余持仓数量，必须大于零。
- `costBasisMoney`：该批次剩余总成本，不重复追加已包含的费用。
- `openDateTime`：原始买入日期时间，用于 FIFO 排序。
- `originatingTransactionID`：批次来源交易标识，用于追溯和重复检查。

仅 SUMMARY 的平均成本不能替代 FIFO 批次。若文件同时提供 SUMMARY 与 LOT，会核对汇总数量和成本是否与批次合计一致；提供 reportDate 时须与文件截止日期一致。转仓或公司行动产生的批次可能没有来源交易 ID，首版不能直接接收，需后续实现专门的成本资料处理。

持仓的 `markPrice`、市值与未实现盈亏均不会当作买入成本。期初批次与同一证券快照日前历史成交同时导入时，会拒绝重复成本来源；可以选择完整历史活动数据，或使用期初批次加本年活动报告。单靠历史活动数据无法证明历史从零开始，因此尚未确认期初范围时仍标记为待复核。

## 3. 当前计算与校验行为

### 3.1 成交及账户

- 按 IBKR、账户、Conid 和币种隔离库存，符号用于展示。
- 只计算普通多头股票／ETF；Orders、Symbol Summary、Closed Lots 等不作为成交重复计入。只有汇总、没有成交层级时提示重新导出。
- 同账户同 tradeID 的相同成交去重；相同 ID 的关键字段冲突时停止计算。现金记录按账户和 transactionID 去重。不会仅凭日期、代码、数量、价格删除重复记录。
- 同账户同日的期末持仓快照有矛盾时停止计算。
- 检查数量 × 价格与 proceeds，以及 proceeds、佣金、交易税与 netCash；差异超过 0.02 原币单位时将受影响成本标为待复核。该容差为当前实现约定，已在一份真实文件中的 21 条股票成交上做部分验证，完整年度仍待验收。
- 未支持的公司行动、转仓、更正及空头事件会阻止受影响证券成本计算，并保留问题记录。

### 3.2 现金事件

| XML type | 当前处理 |
| --- | --- |
| Dividends | 汇总年度股息；负数作为同类型冲正保留 |
| Broker Interest Received / Bond Interest Received | 汇总年度利息收入 |
| Withholding Tax | 原始负现金金额转为正扣税，正现金金额作为退回抵减 |
| Deposits/Withdrawals 或 Deposits & Withdrawals | 正数进入入金汇总；出金保留在现金事件明细，不作为应税收入 |
| Broker Interest Paid、代替股息支付及其他类型 | 保留原始现金明细并生成待复核项，不自行与收入相抵 |

收入按账户、年度和原币汇总。应计股息、应计利息、未实现盈亏及 IBKR 的盈亏汇总不直接作为本系统应税收入。

若 Activity Flex 同时包含 CashReport / Currency，程序会按原币核对股息、券商利息及预扣税与 CashTransactions / Detail 的合计。差异超过 0.02 原币单位、相关字段或币种汇总缺失时生成待复核项。BaseCurrency 汇总不直接与原币明细比较。

自 2026-10-08 起，还会核对每份报告的“期初现金＋成交及现金明细净变动＝期末现金”。CashReport / Currency 请包含 `startingCash`、`endingCash`、`fromDate`、`toDate`；使用成交日余额，不能用 `endingSettledCash` 替代。普通换汇按基础币种数量、报价币种 proceeds 和实际佣金币种构造现金变动。缺字段或遇到未支持事件时显示“无法核对”，不会把空值视为零。详见 [官方现金报告口径](https://www.ibkrguides.com/reportingreference/reportguide/cash%20reportfq.htm)。

结果分别显示“计算状态”“数据完整性”“对账状态”“测算用途”。缺少现金闭环或券商卖出收益时仍可导出测算底稿，但不再显示为全部对账完成。当前年度或使用临时汇率的结果保持临时用途。导出新增“现金余额对账”和“计算快照”，记录实际汇率、文件摘要和计算版本；导出新计算结果前会检查当前配置指纹；配置已变化时需重新计算。

仍沿用项目现有 `V1-2026-09` 税额测算规则与根配置源 `config/exchange_rate.json`（构建后读取 WPF 输出目录的 config）。底稿记录规则版本及适用边界；现有境外扣税按年度和币种合计，尚未按所得项目及国家／地区核对抵免关系。IBKR 的 FX Rate to Base 不被当成兑人民币汇率。需要的年度或币种缺少配置时明确报错；本轮未新增或确认税务申报口径。

### 3.3 年度覆盖和期末数量

- 对每个账户分别检查 Trades、Cash Transactions 的报告区间能否连续覆盖目标年度。
- 不足全年、开户期间未确认、期初资料缺失、缺少年末持仓或数量不一致，都进入待复核清单。
- 期末只核对数量：期初批次加本年买入减卖出，应与年末 OpenPositions Summary 对应。库存已不确定的证券不强行给出数量对账结论。
- 该核对不等同于已完成全部现金余额、成本及券商已实现盈亏对账。

### 3.4 逐笔已实现盈亏对账

在 Trades 的 Executions 层级导出 **Realized PNL**，对应 XML 属性 `fifoPnlRealized`。仅使用该层级的值；Orders、Closed Lots、MTM PNL 和人民币换算金额不参与这项比较。

- 按账户、Conid、币种、卖出 tradeID 汇总本系统 FIFO 批次收益，与同一成交的券商原币收益比较。
- 差额为“本系统减券商”；绝对差额不超过 **0.02 原币单位**显示“一致”。这是本系统当前核对容差，不是税务规则。
- 超过容差显示“存在差异”，生成待复核项和待复核底稿；两笔相反差额不会通过年度净额抵消。
- 没有导出字段、空字符串或空白值显示“缺少券商收益”；本系统仍可按已支持范围测算，页面和导出持续提示未完成这些成交的收益核对。数值 0 是有效值，与缺失有区别。
- 成本未确认或匹配数量不完整显示“成本不完整”；未支持的资产类别保留问题记录。两者的本系统收益及差额均留空，不当作零。
- 同账户同 tradeID 的重复报告若包含不同券商收益，或一份有值另一份缺失，会停止并提示冲突；使用同一导出模板的完整、一致版本重新导入。
- WPF 核对页展示卖出收益核对表；Excel 的“卖出收益对账”工作表包含账户、证券、币种、成交 ID、计算收益、券商收益、差额和状态。未支持事件在“待复核”中保留。

IBKR 官方说明指出交易已实现盈亏考虑佣金影响，具体定义见 [Trades 字段说明](https://www.ibkrguides.com/reportingreference/reportguide/tradesfq.htm)。系统只把券商值作为核对依据，不用它替换自行计算的收益，也不把数值一致当作税务申报口径确认。成本法、公司行动、调整或费用资料差异仍需逐项解释。

此功能仅检查导入且可匹配的卖出成交。即使所有显示的成交一致，仍不代表已完成现金余额、未支持资产、全账户收益或真实模板验收。

## 4. 导出与来源追踪

当前 Excel 包含“来源文件”“现金余额对账”“卖出收益对账”“FIFO明细”“待复核”“汇率底稿”“计算说明”和“计算快照”等工作表。来源保留文件名、SHA256、账户、报告期间及用途；FIFO 保留买入／卖出文件与记录 ID，待复核保留来源与原始 ID。当前 IBKR 导出没有独立的“现金事件明细”工作表，不应将迁移前功能描述当作现有输出。

XML 中的“记录序号”是所属区段的记录位置，不是文本物理行号。未完整覆盖的结果继续使用 `Partial_Review_<年度>.xlsx`，不能把已计算部分当作完整年度申报金额。

## 5. 支持范围和限制

已实现本地 Activity Flex XML 导入、多个文件和多个账户、ID 去重与冲突检查、现金分类、可选期初批次、期末数量核对、逐笔已实现盈亏对账、页面选择与导出来源。

XML 自动联网获取已实现，操作见 [IBKR_FLEX_AUTO_DOWNLOAD.md](IBKR_FLEX_AUTO_DOWNLOAD.md)。尚未实现：CSV／PDF IBKR 解析、交易更正自动合并、做空、期权／期货等衍生品、股票异币佣金成本转换、非零交易税处理、转仓／拆并股成本变换、账户模型分区、国家／地区税额抵免策略和完整券商盈亏对账。现金核对支持换汇佣金按实际币种入账，并不表示上述资产的税额处理已支持。

XML 每份最多 25 MB，一次最多 36 份活动报告；不接受 DTD／实体声明。原始报表放入 `samples` 时，XML 与现有 Excel／PDF 一样被 Git 忽略；不要将真实账户数据替换到测试夹具中。

## 6. 验证与合成样例

- 合成样例：[ibkr_activity.xml](../tests/fixtures/ibkr_activity.xml)。账户 `UTEST001`、证券 `DEMO` 均为测试数据。
- 盈亏对账样例：[ibkr_reconciliation.xml](../tests/fixtures/ibkr_reconciliation.xml)，合成卖出收益 496，与本系统一致；此样例无股息或利息。
- 页面可选择 IBKR，上传任一样例，勾选确认期初无持仓及导出范围，使用 2025 年运行完整流程。实际显示按项目配置中的 2025 年 USD 汇率换算。
- 自动化测试使用固定汇率 1 校验：股票收益 496、股息 100、利息 20、净扣税 6，按现有规则得到合计补税 117.20；这不是实际账户税额。
- 测试另覆盖两批期初成本、缺少资料、季度／月度覆盖、重复及冲突 ID、不支持事件、异常 XML 和页面结果失效。
- C# 后台验证：`dotnet run --project tests/CRS.Tests.Verification -c Release`。

合成测试只能证明实现与预设契约一致；真实全年和成本来源仍需独立验收。
