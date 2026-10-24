# 国家队监测M1：官方NAV研究样本（2026-10-24）

## 本次范围

为[市场区域与持仓方向需求](../architecture/NATIONAL_TEAM_MARKET_REGIME_REQUIREMENTS_20261024.md)补充第一条经过原件核对的单位净值来源。后台新增`EfundsNavResearch`纯解析器，读取已下载的四份固定官方原件；研究工具导出独立目录内的JSON、CSV、说明和原件。

本次是单只ETF的离线研究数据链，未接正式市场事实库、自动网络采集、界面金额计算或通知。正式服务的来源白名单和调度计划未因它改变。价格、成交量、广度、估值、机构方向及样本外有效性仍待实施。

## 已核对的官方来源

| 原件 | 用途 | 本次取得时间（北京时间） |
| --- | --- | --- |
| [159915产品页](https://www.efunds.com.cn/fund/159915.shtml) | 基金身份、最近10条净值、历史总数与外部文件链接 | 2026-10-24 20:34:20 |
| [历史文件](https://cdn.efunds.com.cn/market/2.0/his/159915_all.js) | 较老历史，3442条至2025-12-31 | 2026-10-24 20:34:53 |
| [最近一年文件](https://cdn.efunds.com.cn/market/2.0/159915_1y.js) | 243条至2026-10-23 | 2026-10-24 20:38:24 |
| [官方显示脚本](https://www.efunds.com.cn/js/fund_details/product-detail-overview.js?7905e4a6) | 确认字段编号对应日期、累计收益、单位净值、累计净值、日涨跌 | 2026-10-24 20:34:53 |

来源限定SZSE/159915（易方达创业板ETF），没有推断其他管理人或代码也适用。只读取公开网页及其明确引用的静态资源，不执行脚本、不猜测隐藏接口。本次本地个人研究不代表持续自动采集、商业使用或再分发许可已验收；正式来源许可仍是后续接入门槛。

两份净值文件去重后为3635条，日期2011-09-20至2026-10-23。合并数与产品页`data-total`一致，重叠日期的单位/累计净值必须逐项一致；另外核对最近10条及最新日期。2026-10-23单位净值为2.7018元、累计净值3.0951元。页面其他位置的规模和申赎清单前一日净值不参与解析或金额推导。

## 反方审查及实现约束

| 可能的错误结论 | 处理 |
| --- | --- |
| 累计净值或IOPV被当成单位净值 | 验证显示脚本全部字段映射、数据列编号及页面元单位；单位与累计字段独立 |
| HTTP200便认为有效 | 固定URL、回执状态/长度、SHA256、严格UTF-8、纯数据赋值外壳全部检查；不执行JavaScript |
| 新旧文件冲突时悄悄选一个 | 单文件重复/乱序拒绝；跨文件冲突拒绝整份数据，不覆盖择优 |
| 静态文件未更新却宣称覆盖当天 | 最近日期及合并总数与页面一致，最近10条交叉核对 |
| 多年净值等于逐交易日完整 | 官方存在周末期末估值；不生成日历、不补零、不外推缺日，`CalendarState=NotVerified` |
| 回填日期被当成系统当时已知 | 保存本次取得/首见时间，逐日PublishedAt为null；固定`RetrospectiveOnly`、`TimingProof=NotProven` |
| 相同数值忽略来源与知识时间变化 | 指纹包含解析版本、证券身份、四份原件哈希与取得时间、验证时间 |
| 导出中断留下貌似完成的报告 | 原件和报告全部写好后才公开最终目录；已有目录拒绝覆盖 |
| 修改报告中的数字却保留原指纹 | 复核入口从原件重新构建完整JSON，核对数值、边界标记与指纹 |
| 解析通过便宣称资金流/择时有效 | 不写正式市场库、不填`SharePoint.Nav`；研究结果不证明具名机构成交或预测效果 |

## 复算与验收入口

```powershell
dotnet build tests/CRS.Tests.NationalTeamWatch -c Release -m:1 -nr:false
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-research-checks
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-research-snapshot artifacts/national-team-marketdata-20261024 artifacts/national-team-marketdata-20261024/research-snapshot
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-research-verify artifacts/national-team-marketdata-20261024/research-snapshot
```

后三个入口均不联网或请求已安装Worker。导出输入目录需包含四份原件及`efunds-159915-receipt.json`、`efunds-assets-receipts.json`、`efunds-1y-receipt.json`；回执保存URL、HTTP状态、取得时间、SHA256和字节数。每次导出使用新目录。复核以冻结验证时间重建，不用当前日期改写原研究身份。

源码：`src/Plugins/CRS.Plugins.NationalTeam.Jobs/Infrastructure/EfundsNavResearch.cs`。专项和导出入口位于`tests/CRS.Tests.NationalTeamWatch/NavResearchChecks.cs`及`NavResearchTool.cs`。它们与个人税务/绩效账户数据独立。

同日对齐计算进入Core应用层`EtfNavStudy`：按正式已登记日历的相邻1/5/20日计算，使用每一天单位NAV乘每一天可比份额变化后求和。覆盖状态、份额修订ID/数量/哈希/取得审核时间、可比区间均须匹配；未核验日历、待审、缺份额或未知可比性时不出差额/金额，缺NAV保留可比份额差额而金额未知。输出固定RetrospectiveOnly/NotProven/NotObserved，不推导国家队日成交。

只读正式样本与文件复算入口（分别要求已安装服务、仅本地文件）：

```powershell
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-share-study-live artifacts/national-team-marketdata-20261024/research-snapshot 2026-09-01 2026-10-23 artifacts/national-team-marketdata-20261024/share-study
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-share-study-verify artifacts/national-team-marketdata-20261024/share-study
```

正式读取前后必须同一Running进程，覆盖快照不得变化；报告冻结全部计算输入和份额证据身份。NAV四份原件随报告复制并再核验。份额原件仍由正式原件库保存、查询时经Worker健康校验，报告目录不冒充含所有份额原件的跨机器验收包。

## 尚未关闭的门槛

本轮最终验证：408项完整国家队回归通过，包含34项解析反例、24项同日对齐反例；另6项文件篡改/覆盖拒绝检查通过。最终研究快照复算成功，但真实99个窗口的金额可计算数为0：96个缺可比性依据、3个涉及10月23日份额缺失。这证明拒绝条件在真实输入中生效，不能写成资金流计算已上线或数据已齐全。详细时点与日志见[开发修正清单](NATIONAL_TEAM_RESEARCH_DEVELOPMENT_20261024.md)。

正式NAV接入仍需来源许可与持续采集预算、窄域白名单、原件和独立净值修订表、冲突审核、逐修订知识时间、原件损坏时读取失败关闭，以及金额指纹绑定NAV修订。不能只在现有`SharePoint.Nav`填一个数便上线。

单位净值还不能满足区域判断。价格及复权口径、ETF二级成交、市场广度/估值、跨周期的冻结样本与时间样本外对照仍开放。持仓方向需要扩大真实披露、日期匹配市值、行业/风格映射及明确样本分母；单只600153的五个账户快照不能代表国家队全局方向。
