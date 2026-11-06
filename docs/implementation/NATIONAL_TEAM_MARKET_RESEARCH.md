# 行业、市场周期与联合研究

<a id="market-cycle-data-source-feasibility"></a>
<a id="market-cycle-p2-ingestion-design"></a>
<a id="market-cycle-signal-design"></a>
<a id="national-team-breadth-workflow"></a>
<a id="national-team-index-series"></a>
<a id="national-team-industry-pe"></a>
<a id="national-team-industry-quality-gates"></a>
<a id="national-team-industry-undervalue-review"></a>
<a id="national-team-joint-exit-study"></a>
<a id="national-team-market-activity-knowledge"></a>
<a id="national-team-market-cycle-study"></a>
<a id="national-team-research-development"></a>
<a id="national-team-sector-research"></a>

本文件说明研究输入、计算口径和证据门槛。已实现的查询与审核流程不等于研究模型有效；统一保持`TimingProof=NotProven`，不据此自动交易或发送金融预警。ETF份额、具名披露、股票方向分别见[ETF](NATIONAL_TEAM_ETF.md)、[披露证据](NATIONAL_TEAM_EVIDENCE.md)和[股票持仓](NATIONAL_TEAM_EQUITY.md)。

2026-11-06起，桌面工作台移除“市场周期观察”“联合退出观察”入口及“周期与风险”分类，仅保留“资金与持仓”“行业与估值”两类。研究页面源码、后台查询、底层数据及专项验证仍保留；桌面会话不再构造这两个研究模型。以下周期、广度及联合研究说明记录保留能力，不表示当前有可见菜单入口。

## 行业公布PE与历史位置

国证[行业市盈率页面](https://www.cnindex.com.cn/zh_analytics/sector_pe/index.html)的既有接入限定group 4、行业体系008200、C01—C11；保留原始`dwer/dmer/swer/smer`，分别为滚动加权、滚动中位、静态加权、静态中位PE。`NA`为未知；亏损样本的排除口径见[官方计算说明](https://www.cnindex.com.cn/files/syljssm.pdf)。公布PE不是全体公司实时TTM、PB或公允价值，样本公司数量也不是市值覆盖率。

滚动加权与中位PE分别计算历史位置：仅使用当前观察日前的正值样本，至少252个有效样本且覆盖不少于80%的已核交易日，当前日不参与参照分布，并列值取中位秩。零、负值、未知或不足样本不计算。双低为两项均不高于20%；一项不高于20%而另一项不低于60%为分歧；其余分为单项低位、非双低或资料不足。这些是研究阈值，不是买入判断。

报告`industry-pe-report-v3`标记`CurrentKnowledgeResearch / NotProven`。历史验收中的261个交易日只说明该批样本，不能声称已有五年历史或过去当时可知。

## 指数、PB与来源许可

399317国证A指的合格A股范围含沪深北市场并按指数规则剔除ST，与沪深两市成交及下述广度范围不同；不可直接合并分母。国证1000行业的当前10行业分类也不能混入旧11行业清单。指数样本成交量、成交额不能充作全市场总量。

网页估值快照须核对请求前后同一日期表头，并独立读取同日健康收盘值。样本量0表示未知，不能推断没有成分；当日PB不能补成252日历史。价格、PB、利润与覆盖率都必须有自身来源、时期和审核状态。

来源审批分别绑定域名、数据集、解析器、用途、条款摘要与有效期；自动采集及保存、历史回填及保存、再分发是独立授权。HTTP前、逐次请求及提交前均核验；公开可读网页不自动授予这些用途。`www`与`hq`子域不可互相替代。登记机制的实现不代表生产许可齐备；原接入验收在缺少批准提供者时为`SourceApprovalRequired`且HTTP请求数为0。新来源继续遵守固定地址、来源预算和403/429退避。

## 独立基本面证据门槛

低PE/PB与利润增长不能单独升级为`ResearchWatch`。缺少或矛盾的独立质量证据返回`IndependentQualityEvidenceUnavailable`；证据齐全也只进入`NeedsAdversarialAssessment`，不输出“低估”或买入意见。

| 画像 | 必需指标 |
| --- | --- |
| 金融 | ROE、资产风险、资本充足率 |
| 周期 | 中周期盈利、现金转化、杠杆 |
| 制造 | ROIC、现金转化、杠杆 |
| 创新 | 研发资金、现金跑道月数、自由现金流率 |
| 平台 | 自由现金流率、ROIC、留存率 |
| 通用 | ROIC、自由现金流率、杠杆 |

画像映射须经过审核并在截止时点可见，与估值证据采用同一分类及范围指纹。所有必需指标按规则达到80%—100%覆盖，报告不能来自未来且距截止不超过180天；发布、取得、登记、审核时点顺序合法，状态Accepted、哈希及修订身份有效。比率使用小数，拒绝重复证据。合成门槛检查不能证明已有完整正式画像库或独立财务资料。

## 市场成交的知识时间

市场成交保存真实首次登记时间。旧事实迁移标记`LegacyObservedNow`，使用实际迁移时钟，不能回填成历史当时已知。接受新修订、拒绝竞争版本、更新当前指针、追加审核与业务命令回执在同一事务完成。

读取核对状态事件链、当前指针、原件哈希及来源；缺链或损坏明确不可用。历史截止状态与当前原件物理健康分开表达：今天能读到原件不证明过去当时已经取得，也不证明过去文件健康。

## 市场周期查询与候选规则

`market-cycle-study`在同一只读快照冻结价格、日历、成交知识链和已核广度，由后台`MarketCycleEvaluator`计算，客户端取得完整冻结输入后复算。仅主动查询；截止时点在读快照前固定，统计日期早于该截止的北京时间日期，目前不提供同日收盘截止。读取400个自然日预热，价格至少250个有效点，成交至少21个有效点、广度至少6个有效点；两份已核交易日历须一致。

当前日历仍有`CalendarReplayProof=CurrentReviewedPointerOnlyNotProven`限制；派生证明与`OriginalHash`分别记录。没有真实持久影子评估记录时不能把界面或合成输出当作历史模型验收，`NotificationsEnabled=false`。

以下为保留的候选规则，阈值尚未完成样本外验证。每类证据最多计一分，不能将同类多个条件重复加分：

| 方向 | 价格证据 | 广度证据 | 量价证据 |
| --- | --- | --- | --- |
| 高位候选 | 收盘价达到此前250日最高收盘的95% | MA20上方比例≤45%，或MA60上方比例≤55%，或MA20上方比例较5日前下降≥10个百分点 | 成交额或成交量≥此前20日均值的1.5倍，且日收益≤0、>−3% |
| 低位候选 | 相对此前250日最高收盘回撤≥20% | MA20上方比例≤20%，或创250日收盘新低比例≥20% | 成交额或成交量≥此前20日均值的1.5倍，且日收益≤−3% |

高位至少含价格证据且总分≥2才为较高候选，否则最多观察；低位同样必须含回撤证据且总分≥2。恐慌量价不再同时计入高位。低位后的修复单独观察：5日收益≥3%且MA20上方比例改善≥10个百分点，不称为确认底部。此前20日排除当日；5日收益需要6个连续有效收盘点，不能跳过失败日凑足样本。

保留的验证方案是预登记未来60日最大收盘回撤/上涨15%标签，并对10%/20%做敏感性分析；未来窗口不足的样本右删失。采用滚动训练、验证和样本外区间，与简单价格、广度及随机基线比较，检查20/60/120日收益、领先滞后、误报、相关性和消融。尚不输出校准概率或收益承诺。

## 广度档案与原件审核

原桌面入口为“市场周期 → 广度档案”，随市场周期菜单于2026-11-06移除，底层提交与审核能力保留。提交原件及标准化JSON后进入待审，管理员明确审核。Accepted、Superseded、Rejected追加记录，拒绝后不自动复活；审核绑定最新证明摘要，过期审核不能覆盖新状态。

`reviewed-breadth-v1`范围为`SSE-SZSE-Ashares-point-in-time-v1`。Members与Observations必须同日、一一完整对应，价格使用`BackAdjustedToObservationDate`；后台逐项计算`Close>MA20`、`Close>MA60`和`Close<=Prior250Low`，此前250日最低值不含当日。现有证券清单或ETF配置不能回填历史范围；上市、退市、停牌及新股都需要对应时点证据。

`CommonStagedDocumentStore`分块16KiB、单件8MiB、最多16个活动件/总128MiB；严格校验JSON、身份、长度、哈希、用途和SID。文件路径留在客户端，Worker拥有授权及暂存，插件拥有金融校验。暂存封存不等于金融审核通过。SQL事实、审核与业务回执在同一事务提交，暂存消费独立执行并可据SQL回执恢复，不宣称跨文件分布式事务。暂存目录不在运行资料清理预览范围，尚无临时件批量管理界面。提交/审核默认关闭自动运行，没有据此验收自动实时广度采集。

## 联合观察

联合观察针对当前选中的单只ETF，五部分输入来自同一读事务并完整冻结；归一输入可复算，但不嵌入各上游全部原件，因此复算范围仅到归一输入与联合结果之间。

所需输入包括：份额及NAV的5/20日完整窗口与250/20日覆盖；同一最新具名披露子集、独立持仓及可比性；周期研究；同期间完整股票方向及明确性质、行业、价格；11行业加权及中位PE的252样本/80%覆盖门槛。

同一5日或20日窗口中，份额收缩、NAV估算金额为负、具名最小减持下界>0且高位周期候选共现，才输出`ReviewCandidate`。输入齐备但没有共现为`NoJointCandidate`，输入缺失为`Unavailable`，两者都不是安全或交易判断。算法`joint-exit-candidate-v1.1`，修正最新披露子集后的报告版本为2。

仅支持`CurrentSnapshotResearch`，拒绝历史`KnowledgeCutoff`；不发送通知，`TimingProof=NotProven`、`ModelValidation=NotValidated`，默认370天失效门槛也未经校准。ETF机构持仓独立页面的移除不撤销后端证据，但不保证这些联合输入已经齐备。

## 验证入口与未完成项

- `CRS.Tests.SectorResearch`：行业算法、证据门槛和反例；`CRS.Tests.SectorUi --industry-installed <开始> <结束> <截图>`读取已安装后台的已登记公布PE，其他合成曲线须明确标记。
- `CRS.Tests.NationalTeamWatch --market-cycle-checks`、`--market-cycle-study <隔离目录>`：周期规则、审批及同快照研究。
- `--breadth-workflow <隔离目录>`：广度提交、审核、完整性和幂等；`--joint-exit-study <隔离目录>`：联合输入与共现门槛。
- `CRS.Tests.WorkerEtf --joint-exit-pipe <完整后台包> <新目录>`：真实Runner中的联合查询；缺失资料场景不证明真实完整金融覆盖。可视化及其他跨进程命令见[测试说明](../../tests/README.md)。

历史证据目录包括`artifacts/national-team-industry-*`、`artifacts/national-team-market-cycle-*`及`artifacts/national-team-joint-exit-*`，其记录只对应当轮输入。仍须取得正式来源许可、完整历史时点证券范围及行情、独立财务/PB、可重放日历版本与真实登记知识证明，完成真实影子记录、样本外标签与繁忙后台延迟验收。总体状态以[待办](../KNOWN_ISSUES.md)和[发布验收](../validation/RELEASE_ACCEPTANCE.md)为准。
