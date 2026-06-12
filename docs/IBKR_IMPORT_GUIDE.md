# IBKR Activity Flex XML 导入说明

实现日期：2026-09-22。当前为本地 XML 导入首版；已实现逐笔已实现盈亏对账。2026-09-30 已用三份真实导出文件做部分结构与成交验收，完整年度验收仍待进行。

## 0. 手机下载的 CSV／PDF 与这里的 XML 有什么区别

IBKR 的税务文件、普通活动报表和 Flex 查询是不同入口。手机某份报告只提供 CSV／PDF，并不表示遗漏了 XML 选项，也不能从扩展名判断是哪种报告。

- **Tax Documents／税务文件**：例如 Dividend Report、1042-S 等，内容和用途取决于具体表单；不是本解析器接收的 Activity Flex XML。
- **Activity Statement／活动报表**：与自定义 Activity Flex Query 区分；同样是 CSV，区段和字段也可能不同。
- **Activity Flex Query／活动 Flex 查询**：在网页 Client Portal 的 Performance & Reports → Flex Queries 创建，选择所需字段和 XML 格式。官方 Flex 也可导出 CSV；本项目当前仅实现其中的 XML 输入。

因此，不要把手机 CSV 改名为 XML，也不要把“首版仅支持 XML”理解为“IBKR 只提供 XML”。如已有 CSV，后续适配应先确认报告名称及实际字段；只有股息／扣税汇总的报告不能补出缺失的买卖和历史成本。

参考：[Client Portal Flex 入口](https://www.ibkrguides.com/clientportal/performanceandstatements/flex.htm)、[Flex 格式配置](https://www.ibkrguides.com/clientportal/performanceandstatements/activityflex.htm)、[税务文件入口](https://www.ibkrguides.com/brokerportal/performanceandstatements/taxform.htm)。具体手机菜单及可用格式随报告和账户而异，本说明不声称已在用户手机上验证。

### 0.1 已确认的手机税务文件入口

用户反馈路径：下载税表 → 税务文件 → 2025 税表，下面列出 1042-S 表格、外汇收入工作表、股息报告。此处是税务文件入口，不能要求在这些表单下寻找 Activity Flex XML。

| 手机显示名称 | 数据用途及后续接入方式 | 当前支持状态 |
| --- | --- | --- |
| 股息报告 / Dividend Report | 优先取得实际 CSV，映射股息、代替股息支付及对应预扣税；与现金交易交叉核对，重叠金额不重复相加 | 尚未实现 CSV 解析，待真实格式样例 |
| 1042-S 表格 | 核对表内列示的收入类别、收入及美国预扣税；按表单范围与股息等明细关联，不作为另一笔收入重复计入 | 尚未实现解析 |
| 外汇收入工作表 / Forex Income Worksheet | 外币取得与处置形成的汇兑损益；与证券本身的买卖收益分别保存和核对 | 尚未实现解析，不可直接并入股票收益 |
| 年度活动报表 / Annual Activity Statement（另一个入口） | 股票买卖明细、费用及券商收益；FIFO 还需对应历史成交或可靠期初成本批次 | 普通报表 CSV／PDF 尚未支持；目前已支持自定义 Activity Flex XML |

官方说明指出 Dividend Report 包括股息、代替股息支付和预扣税，并以账户基础币种报告。实际接入须保留报告币种及原币字段（如果提供），不能把基础币种金额误当证券原币再换算；不能因文件名含“股息”就把所有收入类型统一按股息处理。1042-S、股息报告与活动报告存在内容交集，导入流程必须明确主数据和核对资料的关系。

下一步需用户提供 2025 年股息报告 CSV（若该项可下载 CSV；否则先提供其 PDF）来确认字段、区段、编码、汇总行及金额口径。当前提供的真实 Activity Flex XML 不含该税务文件，故尚未按猜测的列名实现股息报告 CSV 解析器。可以隐藏姓名、地址、税号；保留原始表头、币种、日期及金额列，账户若替换则各文件保持一致。完整年度成本测算还需活动报表及历史成本，不以这三份税务文件自动认定数据完整。

官方参考：[年末活动报表与股息报告](https://www.interactivebrokers.com/en/support/tax-nonus-reports.php)、[外汇收入工作表](https://www.interactivebrokers.com/en/support/tax-fxpl.php)、[1042-S 等年末税表](https://www.interactivebrokers.com/en/support/tax-nonus-forms.php)。

## 1. 页面使用

1. 启动应用，券商选择 **IBKR**。
2. 在“上传 IBKR 活动报告”中选择一份年度 Activity Flex XML，或多份覆盖连续月份的 XML。可以包含多个账户，每个账户独立处理库存。
3. 如年初持有股票，另外上传截至上一年 **12 月 31 日** 的持仓批次 XML；在该查询中选择 Open Positions 的 **Lots**。
4. 只有实际确认所选年度全部导入账户期初均无持仓时，才勾选“我确认所选年度所有导入账户的期初均无持仓”。与期初文件二选一；两者都没有时，系统仍可生成待复核底稿。
5. 核对 Flex 查询未按账户、证券或现金类型过滤，并勾选“我确认导出的报告覆盖目标年度全部相关账户、证券成交和现金明细，且未设置记录过滤”。未确认时只生成待复核底稿；勾选仅记录用户确认，不能替代券商对账。
6. 选择测算年度。留空时使用活动报告覆盖的最新年度，期初文件不参与该年度自动选择。
7. 点击“开始计算”，查看金额及待复核项，下载 Excel。更换券商、文件、年度或任一确认选项后，旧结果和下载入口会失效。

富途入口继续使用现有股息表与交易流水，无需为 IBKR 功能改变富途文件格式。

## 2. 在 IBKR 导出什么

官网入口：[IBKR Client Portal](https://portal.interactivebrokers.com/en/trading/client-portal.php?menu=B)。在网页端登录后，进入 **Performance & Reports → Flex Queries** 创建 Activity Flex Query。

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

若 Activity Flex 同时包含 CashReport / Currency，程序会按原币核对股息、券商利息及预扣税与 CashTransactions / Detail 的合计。差异超过 0.02 原币单位、相关字段或币种汇总缺失时生成待复核项。BaseCurrency 汇总不直接与原币明细比较；此项尚不核对现金余额。

仍沿用项目现有 `V1-2026-09` 税额测算规则与 `config/exchange_rate.json`。底稿记录规则版本及适用边界；现有境外扣税按年度和币种合计，尚未按所得项目及国家／地区核对抵免关系。IBKR 的 FX Rate to Base 不被当成兑人民币汇率。需要的年度或币种缺少配置时明确报错；本轮未新增或确认税务申报口径。

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
- 成本未确认或匹配数量不完整显示“无法核对”；未支持的资产类别显示“未支持资产”。两者的本系统收益及差额均留空，不当作零。
- 同账户同 tradeID 的重复报告若包含不同券商收益，或一份有值另一份缺失，会停止并提示冲突；使用同一导出模板的完整、一致版本重新导入。
- 页面显示股票／ETF 的一致笔数／可核对卖出笔数，并单列未支持资产数量；Excel 增加“已实现盈亏对账”工作表，包含资产类别、计算收益、券商收益、差额、状态、说明、成交 ID、来源文件和区段记录序号。

IBKR 官方说明指出交易已实现盈亏考虑佣金影响，具体定义见 [Trades 字段说明](https://www.ibkrguides.com/reportingreference/reportguide/tradesfq.htm)。系统只把券商值作为核对依据，不用它替换自行计算的收益，也不把数值一致当作税务申报口径确认。成本法、公司行动、调整或费用资料差异仍需逐项解释。

此功能仅检查导入且可匹配的卖出成交。即使所有显示的成交一致，仍不代表已完成现金余额、未支持资产、全账户收益或真实模板验收。

## 4. 导出与来源追踪

IBKR 报告增加“导入来源”和“现金事件明细”工作表，保留文件名、SHA256、账户、报告期间、文件用途、现金类型及记录 ID。“计算说明”写入导出范围确认、规则版本及抵免边界。成本匹配明细保存买入／卖出文件和对应记录 ID；问题记录保留来源区段及记录序号。

XML 中的“记录序号”是所属区段的记录位置，不是文本物理行号。未完整覆盖的结果继续使用 `Partial_Review_<年度>.xlsx`，不能把已计算部分当作完整年度申报金额。

## 5. 本轮范围和限制

已实现本地 Activity Flex XML 导入、多个文件和多个账户、ID 去重与冲突检查、现金分类、可选期初批次、期末数量核对、逐笔已实现盈亏对账、页面选择与导出来源。

尚未实现：CSV／PDF IBKR 解析、自动联网下载、交易更正自动合并、做空、期权／期货等衍生品、异币佣金转换、非零交易税处理、转仓／拆并股成本变换、账户模型分区、国家／地区税额抵免策略和完整券商盈亏对账。

XML 每份最多 25 MB，一次最多 36 份活动报告；不接受 DTD／实体声明。原始报表放入 `samples` 时，XML 与现有 Excel／PDF 一样被 Git 忽略；不要将真实账户数据替换到测试夹具中。

## 6. 验证与合成样例

- 合成样例：[ibkr_activity.xml](../tests/fixtures/ibkr_activity.xml)。账户 `UTEST001`、证券 `DEMO` 均为测试数据。
- 盈亏对账样例：[ibkr_reconciliation.xml](../tests/fixtures/ibkr_reconciliation.xml)，合成卖出收益 496，与本系统一致；此样例无股息或利息。
- 页面可选择 IBKR，上传任一样例，勾选确认期初无持仓及导出范围，使用 2025 年运行完整流程。实际显示按项目配置中的 2025 年 USD 汇率换算。
- 自动化测试使用固定汇率 1 校验：股票收益 496、股息 100、利息 20、净扣税 6，按现有规则得到合计补税 117.20；这不是实际账户税额。
- 测试另覆盖两批期初成本、缺少资料、季度／月度覆盖、重复及冲突 ID、不支持事件、异常 XML 和页面结果失效。
- 运行：`python -B -m pytest tests/unit/test_ibkr.py tests/unit/test_ibkr_reconciliation.py -q -p no:cacheprovider`。

合成测试只能证明实现与预设契约一致；真实报告的部分验收结果和剩余资料见下一节。

## 7. 真实导出文件的部分验收（2026-09-30）

用户提供的 `samples/IBKR/CRS.xml` 覆盖 **2026-01-01 至 2026-09-28**。本节只记录不含账户号、证券代码和实际收益金额的核对结论；原始文件位于被 Git 忽略的 `samples` 目录，不复制到测试夹具。

| 检查项 | 结果 |
| --- | --- |
| XML 结构 | 1 份 FlexStatement，当前解析器可读取；包含 Trades 和 ChangeInDividendAccruals |
| 股票成交 | 21 条 STK Execution 均导入；数量×价格、proceeds、netCash、佣金与税额的逐笔差额均为 0 |
| 外汇成交 | 63 条 CASH Execution 按现有范围标为未支持；不并入股票 FIFO |
| 股票卖出与成本 | 4 笔卖出中 3 笔完成 FIFO 匹配，1 笔缺少可确认的期初成本 |
| 券商已实现盈亏 | 已匹配的 3 笔均在 0.02 原币容差内；最大绝对差额为 0.007368 USD |
| 收入与持仓 | 文件缺少 CashTransactions 和 OpenPositions；ChangeInDividendAccruals 是应计变动，不能作为已到账收入 |

因此，这份文件只能验收所含股票成交的字段兼容性与部分金额核对。正常完整计算因缺少现金收入数据而停止；不把诊断时绕过该检查所得的数值作为年度结果。2026 年报告尚未覆盖全年，配置文件也尚无 2026 年兑人民币汇率。

要完成目标年度验收，需补充覆盖该年度的 Activity Flex XML：包含 Trades / Executions、Cash Transactions / Detail、年度末 Open Positions / Summary；若年初已有持仓，还需上一年 12 月 31 日的 Open Positions / LOT 成本批次。取得后再核对未匹配卖出、现金和期末数量，并逐项解释券商收益差异。

### 7.1 补充滚动报告后的核对

另收到 `samples/IBKR/CRS-L.xml`，期间为 **2025-09-29 至 2026-09-28**。它仍是 Activity Flex 成交报告，不含 OpenPositions，不能放入“期初成本报告”上传位；解析器会以“期初成本文件缺少 OpenPositions”拒绝。它同样缺少 CashTransactions，仅有 Trades 和 ChangeInDividendAccruals。

单独读取补充文件可导入 45 条股票成交；与第一份合并读取时，84 条重复成交按账户和 ID 去重，股票成交仍为 45 条。两份报告中出现的股票成交金额及净现金校验均无超出 0.02 原币的差异。补入较早成交后，**2026 年 4 笔股票卖出均形成 FIFO 匹配，且逐笔券商收益差异均在 0.02 原币容差内**（最大绝对差额 0.007368 USD）。另有 **1 笔 2025 年卖出**仍缺少此前的成本；2026 年的期初持仓范围也未获确认，因此当前匹配不能替代期初 LOT 成本验收。

两份报告都截止于 2026-09-28，不构成 2026 全年覆盖；仍需目标年度的 CashTransactions / Detail、年度末 OpenPositions / Summary，以及有期初持仓时的上一年 12 月 31 日 OpenPositions / LOT。2026 年兑人民币汇率尚未配置，不得以推测汇率生成完整人民币结果。

### 7.2 补充现金与持仓报告后的核对

第三份真实文件 `samples/IBKR/CRS-M.xml` 同样覆盖 **2025-09-29 至 2026-09-28**，含 141 条 Trades / Execution、35 条 CashTransactions / Detail、7 条 OpenPositions / Summary，并附 CashReport。当前解析器导入 45 条股票成交、35 条现金事件和 7 条持仓记录；96 条 CASH 外汇成交保持未支持状态。现金明细中另有 1 条 Payment In Lieu Of Dividends 和 4 条 Other Fees，均保留为待复核，不混入已支持的股息或费用。

以原币分项核对 CashReport / Currency 与 CashTransactions，HKD、USD 的股息、券商利息和预扣税合计均一致。程序现已自动执行上述分项核对；尚未核对现金余额，BaseCurrency 汇总不与原币明细直接比较。

按 2026 年测算时，程序会因配置文件缺少 **2026 年兑人民币汇率**而停止。仅用诊断用固定汇率 1 验证解析、状态与 FIFO：4 笔 2026 年股票卖出完成匹配且券商收益差异均不超过 0.02 原币；结果仍有期间不完整、期初未确认、缺少年末持仓、未支持外汇／现金类型及 1 笔 2025 年成本缺口，不能作为税额结果。文件中的 OpenPositions 是 **2026-09-28 期末 SUMMARY**，不能代替 **2025-12-31 期初 LOT** 或 **2026-12-31 年末 SUMMARY**。
