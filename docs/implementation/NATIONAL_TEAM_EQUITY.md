# 机构直接持股

<a id="direct-equity-implementation"></a>
<a id="direct-equity-tracking-design"></a>
<a id="equity-charts"></a>
<a id="equity-report-lookup"></a>
<a id="etf-equity-deployment"></a>
<a id="etf-equity-followup"></a>
<a id="etf-equity-p0-remediation"></a>
<a id="etf-equity-p0-source-and-golden"></a>
<a id="national-team-equity-coverage-audit"></a>
<a id="national-team-equity-direction"></a>
<a id="national-team-equity-expansion"></a>
<a id="national-team-equity-reviewed-subsets"></a>
<a id="national-team-equity-stock-list"></a>
<a id="national-team-holdings-usability"></a>

## 股票清单与按需详情

工作台“资金与持仓 → 机构直接持股”先按市场和代码显示唯一股票行，默认仅代码、名称、持有机构、账户数、报告期；点击股票查看具体持有人、股数、原披露比例、报告期及原文。

搜索、机构筛选和刷新保留在首页。矩阵、方向、历史补入、观察配置、性质及其他披露收进高级工具；图表、历次变动和人工核验按需读取。股票切换、空结果、失败或取消清除旧详情并拒绝迟到响应，刷新保留仍有效的选择。

正式清单仅纳入已核持仓、已确认主体、正股数；待核验、未列示、冲突和异常独立显示，不能挤掉有效快照。账户数只反映当前筛选样本；不跨账户或证券相加股数、百分比，也不将多个报告期混成同一时点。

## 主体、性质与期间变化

主体按官方法律名称及具体账户登记：汇金投资、汇金资管、证金、社保/养老组合及其他已核目录主体。简称、受托管理人、集团关系或关键词匹配不自动证明持有人身份；A/H、优先股/普通股和复合账户必须分开。

| 证据情况 | 可表达的结论 |
| --- | --- |
| 首次列示、前期未进前十 | 前值未知，不是首次买入 |
| 本期未列示 | 当前持仓未知，不是清仓或确认减持 |
| 两期已核且主体/股类/口径/公司行为一致 | 报告期股数变化，不是每日交易 |
| 股数不变、比例下降 | 股数持平，不把分母变化当卖出 |
| Nature=Unknown或可比性不足 | 性质未知、Delta为空，不认定投资目的 |
| 明确零股数或完整退出证据 | 仅对具体主体与证券保留对应结论和原文 |

可比审核必须绑定具体前期披露ID。后补前期、更正或相邻基准变化后，保留已核快照但差额回到NotComparable，不能沿用旧布尔批准。后台写事务复核界面所见基线，陈旧请求拒绝；新客户端不降级为缺少基线证明的审核。

图表只画离散报告期点和已核可比变化，不前向填充每日持仓、不把比例差当成交量。报告期、公告日、首次发现及审核时间各自保留；候选来源日期不能冒充报告截止日。

## 来源与核验

自动结构化股东来源用于发现NeedsReview候选，正式PDF仍需核对主体、报告期、股类、单位、数量、总股本分母及页码。报告目录、PDF取得、宿主Succeeded和金融审核是不同阶段。

现有来源按明确市场、证券、报告期、完整URL和原件摘要控制，精确发行人/巨潮报告不是任意目录许可；官网/CDN共用相应来源预算。冷却、未知结果和原请求ID不能由研究副本、换域名或重复登记绕过。公牛维持原固定来源边界。

固定A股已核子集使用`ReviewedAShareDisclosedSubset`，不冒充完整十大股东。无目录匹配账户可保留已核空子集，但不产生真实零持仓；未披露比例保留null。完全匹配的旧候选可经同事务审计Superseded，原始账户、原件和数量仍保留。

## 持仓方向

`EquityDirectionProjection`按同一报告期、全部启用观察池读取，不能由页面筛选偷偷改变分母或混用各股票最新期。行业/风格审核绑定披露、原件、分类标准版本及页码；缺少匹配期末价或行业不计算权重，不用当前股价补历史。

只有同机构、同性质的全部已核样本具备一致行业与价格才形成该样本的权重；战略、证券投资和未知性质分别计算。损坏、冲突或部分账户未核不能剔除后重新归一到100%。`WholePortfolioProven=false`，样本权重不代表真实完整组合。

## 已取得的固定原文证据与开放项

2026-11-04的升级前只读核查为13只观察股票、4只已核股票/15账户；它解释了当时清单较少，不是界面4条上限。同日固定原文补齐后，18份报告登记完成，正式回读为14只观察、10只已核股票/26个当前账户；9只报告期为2026-09-30，公牛保持2026-06-30。日期限定这一历史截面，不作为未来的实时数量。

特别口径包括：建行汇金A股不混H股；工行社保A股不计H股脚注；华鲁恒升转增不当买入；三元注销与最新未列示分别保留；建发未列示社保组合不当清仓；中微大基金复合账户不重复计受托管理人。新登记性质Unknown、Comparable=false、Delta=null。

固定来源定义见[Core来源资源](../../src/Plugins/CRS.Plugins.NationalTeam.Core/Domain/Equity/ReviewedEquitySources.json)及[Jobs摘录资源](../../src/Plugins/CRS.Plugins.NationalTeam.Jobs/Resources/equity-reviewed-subsets-20261104.json)。现场凭据为`artifacts/equity-remediation-20261104/final-acceptance.json`及其中的原文、逐份回执和保全比较。固定原文登记不证明全市场自动发现、通用PDF自动审核、完整组合或模型有效性已验收。

## 验证入口

NationalTeam的`--equity-reviewed-checks`、`--equity-direction`及默认持股/修订回归；WorkerEtf的`--equity-direction-pipe`、`--equity-read-live`；PluginRecovery的`--equity-direction-visual-preview`。带live的读取或审核必须按[测试说明](../../tests/README.md)区分只读和真实写入，使用稳定请求逐份核对回执。

具体历史PDF与来源反例见[披露证据](NATIONAL_TEAM_EVIDENCE.md)，当前缺口以[架构状态](../architecture/ARCHITECTURE_STATUS.md)为准。
