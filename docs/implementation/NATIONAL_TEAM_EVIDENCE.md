# 披露证据、历史原文与预警

<a id="national-team-abc-disclosure"></a>
<a id="national-team-etf-document-integrity"></a>
<a id="national-team-etf-holder-candidates"></a>
<a id="national-team-etf-holdings-removal"></a>
<a id="national-team-etf-report-index"></a>
<a id="national-team-exit-alert-implementation"></a>
<a id="national-team-gongniu-disclosure"></a>
<a id="national-team-historical-pdf"></a>
<a id="national-team-named-etf-history"></a>
<a id="national-team-period-comparison"></a>

## 当前入口与事实层次

机构ETF持仓独立页面及专用预览已于2026-11-04移除；后台目录、原件、具名多期规则、审核资料及NAV/预警依赖保留。本文不提供已删除页面的操作说明。直接股票持仓入口见[直接持股](NATIONAL_TEAM_EQUITY.md)。

| 状态/证据 | 实际含义 |
| --- | --- |
| NotSearched | 未检索，保持未知 |
| IndexedUnreviewed | 有目录线索，不等于PDF已下载或持仓已核 |
| NeedsReview | 待核候选，不是正式具名持仓 |
| NotDisclosed | 原文没有具名披露，不是零持仓或退出 |
| UnsupportedTemplate | 当前模板不支持，不是无持有人 |
| EvidenceUnavailable / Q15未解除 | 保留历史但当前证据不可用，不回退旧值绕过 |

基金目录来自[SSE基金公告](https://www.sse.com.cn/disclosure/fund/announcement/)与[SZSE基金公告](https://www.szse.cn/disclosure/fund/notice/index.html)，股票目录使用独立已核接口，不能混套协议。目录必须核对证券、分类、日期、附件、总数和全部分页；两所目录无历史冻结证明，`CoverageProof=UnversionedDirectory`、`HoldingsConfirmed=false`不能因取完页而升级。

取证和审核沿持久请求。宿主Succeeded后还需同ID业务回执；Pending、RetryDeferred、缺回执或未知受理不是完成。仅明确UnknownRequest时才按原ID/原参数重放，不能重新排队、直导研究副本或清除来源状态绕过冷却。

## 同一原件合同

ETF证据归`EtfHoldings`功能所有。证券、完整URL、provider及版本、原始/保存摘要、实际文件、登记首见及未解除Q15共同决定健康；具名首见绑定原件登记，不采信负载回填的较早时刻。

可比性、候选、持仓、NAV、历史回算和金融通知共享该门槛。失败保留历史值但撤去当前原件确认；原字节恢复仍须合法巡检解除Q15。单次查询对相同哈希去重，按有界来源引用读取；不跨查询缓存核验，不在读查询里补审核或修复记录。

PDF文字/坐标/矩形读取属于`CRS.Documents`，证券、模板、页码、列意义及来源许可属于插件。模板绑定精确原件和版本，未知字节返回UnsupportedTemplate；不能将通用PDF工具当通用金融审核器。候选不会自动填法人身份、准确公告时刻、投资目的或确认框。

## 具名多期和历史补入

仅对同ETF、同法人/账户、相邻已登记披露且两期原件与可比性充分的记录比较。未列示不填零、不跨缺口，最新期冲突、损坏或无已识别主体时不回退更早期。相同份额多原件计一次，身份或数量矛盾关闭基准。

`LatestReviewedDisclosedSubset`只代表已登记账户子集；不证明完整组合、真实覆盖率或每日交易。研究当前版本不等于历史as-known重建，`HistoricalKnowledgeProof=NotProven`、`WholePortfolioProven=false`、`DailyTradingEvidence=NotObserved`分别保留。

历史股票PDF使用独立来源类型及固定目录定义，原始PDF和规范化摘录分别绑定摘要；不能伪造SSE历史JSON、复用当前候选ID或回填取得/审核时间。完整披露、账户审核、基线及业务回执同事务；失败可能留下未引用原件，但不能留下部分金融事实或成功回执。补历史/更正使旧比较基线失效，须重新核验，历史账户不重复计入当前头寸。

## 固定原文样本

以下是逐页核对过的历史样本，日期和摘要绑定当时原件，不授权同站其他文件，也不代表最新实时持仓。完整当前来源目录另见[直接持股来源资源](../../src/Plugins/CRS.Plugins.NationalTeam.Core/Domain/Equity/ReviewedEquitySources.json)。

| 样本 | 固定官方入口 | 页码与关键边界 |
| --- | --- | --- |
| 建发2025年报 | [报告页](https://www.chinacnd.com/report-226.html) | P158—159，SHA256 `470448A5B8073540B0A6A722E1DDED1558AB9DC06FB44347D0E91D2948B72DF2` |
| 建发2026中报 | [报告页](https://www.chinacnd.com/report-227.html) | P112—113，SHA256 `6D10EE92FEB04A3EB7B066B89552DD5C3F84D57C016E0F43738AB270A8A166F9` |
| 农行2026中报 | [PDF](https://www.abchina.com/cn/aboutabc/investor_relations/report/mm/202608/P020260924668950211207.pdf) | P74及释义，SHA256 `D94E1793DFE065828107B8671A03EAAA3F57A7DCE0A6CEB0CCE6F96FE95CA1D5`；表决权委托不等于持股转移 |
| 工行2026中报 | [PDF](https://v.icbc.com.cn/userfiles/resources/icbcltd/download/2026/InterimReportA20260924.pdf) | P72，SHA256 `5E46724BF1E57C6C671350E4A4786E487F3C4F3F61C4B2A08E6E32AFA861D4F1`；A股不混脚注H股 |
| 公牛2026中报 | [PDF](https://gnstaticcdn.gongniukaiguan.net/uploadfile/2026/0831/20260831101946479.pdf) | P34—35，SHA256 `2BCCE8A397D72AD7BDB9A414A357246BDDBC842E5C1AFBE3BC384238BE4AAD8A`；期末分母不倒填7月注销后股本 |

建发两期原文算术校验：养老一零零三67,250,642→57,522,042；社保一零七34,616,620→37,412,320；社保四零三28,064,763→29,365,463；社保1106组合22,789,500→23,787,000。六零四前期未列示，前值未知，不能把本期50,705,713全当新增。算术差不替代性质、公司行为和逐账户可比审核。

农行四账户A股为汇金140,488,809,651、社保23,520,968,297、证金1,842,751,177、汇金资管1,255,434,700；工行社保A股12,331,645,186不计H股；公牛社保114为7,879,000、养老808为4,520,552。均不能由Unknown性质或原文“增减0”推断期间交易。精确文件的容量特例仅对已核URL/摘要生效，普通来源不继承例外。

510300、159919及510050年报模板的具名候选仍须人工确认；510050中报NotDisclosed不等于持仓为零。历史研究与原请求凭据位于`artifacts/china50-holders-20261031/REVIEW_PACKET.md`等功能证据目录，正式取得和审核以对应原请求回读为准。

## 预警与通知冻结

数据质量、ETF整体收缩和具名最低净减少证据分别表达。日终、可比性、日历、原件、连续窗口及M0覆盖门槛不足时关闭金融判断；缺数据不清零旧风险，也不自动解除。规则版本、成员、触发输入和更正历史冻结，后来的新披露不能替换原事件依据。

事件变更、当次通知冻结输入及Outbox同事务。解除/撤销窗口与原触发窗口分别保存，旧通知缺证据不能倒造。领取前再次核验实际冻结原件，阻断时不领取、不外发；InFlight先落盘，提供商受理、结果未知、用户送达分别记录。OutcomeUnknown不自动或人工“安全重试”，渠道也不自动互相替代。

公共渠道、受保护配置和发送适配器统一见[通知组件](../architecture/NOTIFICATION_CHANNELS.md)。预警插件不保存SMTP口令，保存设置不隐式发送测试或真实消息。联合退出研究另见[市场研究](NATIONAL_TEAM_MARKET_RESEARCH.md)，不自动转为正式退出通知。

## 验证入口与未完成边界

NationalTeam：`--etf-document-boundaries`、`--etf-holder-candidates`、`--etf-holder-golden`、`--historical-pdf-checks`、`--gongniu-golden`、`--exit-alerts`；WorkerEtf：`--historical-command-checks`、`--historical-pipe`及具名披露历史/目录专项。完整参数见[测试说明](../../tests/README.md)。真实登记或审核的live命令会修改业务事实，应按已授权范围运行、保存原ID并核对双回执。

来源范围、真实披露、独立持有关系、全池资料、通知送达和样本外模型分别验收。记录中的构建、HTTP200、完整分页或本机Committed，不证明这些门槛全部关闭。
