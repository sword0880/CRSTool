# 国家队行业估值：独立基本面质量闸门与反方复核（2026-10-26）

## 目的及重要边界

本轮仅实现 **Domain/Industry** 的纯研究质量闸门（`IndustryQualityGate`），并把旧 `SectorResearch` 的单项利润增长直接进入 `ResearchWatch` 的逻辑降级为更保守的状态。没有接入新外网、假造财务数字、修改正式市场数据库、自动调度、交易或账户资料。真实行业独立财务指标及来源授权**尚未验收**，因此主产品不能宣称提供真实跨行业低估名单。

### 反方发现：原模型不充分

现有 SectorResearch 在“自身 PE、PB 双低、指数价格无缺口、利润同比非负”时可能标识 `ResearchWatch`，但完全没有现金流、杠杆或行业周期证据。对周期行业，利润高点的低 PE 更可能是危险信号。对银行，单一利润增长无法解释资产质量；对创新医药，PE 可能本身不适用。若继续扩充一个单一综合打分模型，会产生**貌似精确但不可验证**的低估排序。

本轮新行为：以前直接产生 `ResearchWatch` 的情形，当独立行业质量资料缺失、来源与公司池不一致或证据不合格时，改为 `IndependentQualityEvidenceUnavailable`；即使全部审核齐备，也仅为 `NeedsAdversarialAssessment`，表示**人工审查所需材料到齐**，而不是“业务质量很好”“已低估”或“建议买入”。原有盈利恶化 `ValueTrapRisk` 等保守路径继续保留。

## 业务模块分类

只在 `CRS.Plugins.NationalTeam.Core/Domain/Industry/IndustryQualityGate.cs` 增加新的模型及纯规则。数据持久化和来源准入的未来适配器属于 `NationalTeam.Jobs`；UI 只消费由业务服务提供的审核状态，不能从页面计算现金流或报表数值。这遵循[NationalTeam 功能分类](../architecture/NATIONAL_TEAM_FEATURE_CLASSIFICATION_20261026.md)，避免把所有新内容堆入单一 Page/ViewModel 或共享巨类。

研究规则为以下 **领域侧必需指标完整性**（指标明确，但数值本身不在本阶段按简单阈值打分）：

| 研究经营模式 Profile | 必需可审计反证指标（示例口径） | 为什么不能与其他行业通用 |
| --- | --- | --- |
| Financial | ROE、资产质量风险、资本充足率 | 金融机构不能用一般工业现金流转换率衡量 |
| Cyclical | 中周期利润、现金转换、资产负债杠杆 | 现价利润可能处在商品或供需周期顶部 |
| Manufacturing | ROIC、现金转换、杠杆 | 需要防止库存/产能扩张吞噬利润 |
| Innovation | 研发资金覆盖、现金可续航月数、FCF Margin | 创新医药/科技经常不适用正 PE |
| Platform | FCF Margin、ROIC、留存指标 | 需要核对获客费用、可持续复购与现金流 |
| General | ROIC、FCF Margin、杠杆 | 暂无更专门行业框架时的保守资料清单 |

这些分类本身不能由名称推断。质量输入新增 `ProfileRuleSet=industry-profile-v1` 与 `ProfileMappingEvidence`，未审核或审核时间超出知识截止的经营模式映射，必须标为 `UnverifiedProfileMapping`。当前尚未有可审计的**正式行业映射事实仓**，不能把测试项目的合成授权对象当作真实来源审核。

每个 `IndustryQualityObservation` 显式包含指标、单位、报告期、市场价值覆盖率、公式/方法、行业成分股宇宙与快照指纹、来源ID、是否获准用于研究、是否从估值值反推以及审核证据。闸门要求：

- 每项必需指标非缺失；市值覆盖率不低于 80%，且不高于 100%；需同一行业分类、同一证券池定义与已核验快照；`ResearchUseAllowed` 仅应由可信后台准入层负责确认，UI/用户输入不能替代许可核验。
- 报表期末不可晚于所研究的当日，也不可早于当日 180 天以上；公布时间必须在期末之后、不能晚于来源首次获得时间或知识截止；首次获取、首次登记、审核链须有序，状态必须是 Accepted 且原件哈希和修订 ID 有效。
- 百分比统一小数值，如 8% 为 `0.08`；倍数和可续航月数是不同单位。超出保守合理数值范围（需要人工确定单位）的记录不直接进入 Ready，不等于该公司的经营数据必然错误。
- 重复指标拒绝整次判断，不从冲突版本任意择优；输入冻结并用归一化证据/查询时点生成 SHA256 指纹，资料更新后必须重新评审。
- 不在此阶段设“银行 ROE 高于 X 即好”“制造业 ROIC 大于 WACC 即买”的**任意数值阈值**。真实估值必须考虑行业资金成本、周期归一、中期盈利、持续竞争优势、资本结构、估值安全边际及反例。

## 实现点

| 文件 | 工作内容 |
| --- | --- |
| `Core/Domain/Industry/IndustryQualityGate.cs` | 经营模式、指标单位、时间/覆盖/来源/分类核验、资料齐备状态与指纹 |
| `Core/Domain/SectorResearch.cs` | 新增可选 `QualityInputs`，按行业绑定闸门；缺失质量证据停止输出 `ResearchWatch` |
| `NationalTeamWatch/Presentation/SectorResearchPage.xaml` | 展示“质量证据”状态，不从页面回推金融状态 |
| `NationalTeamWatch/Presentation/SectorResearchViewModel.cs` | 将证据不足与待人工反方评估分别明确展示 |
| `tests/CRS.Tests.SectorResearch/Program.cs` | 反例及程序不变式，所有财务值均为明确合成夹具 |

没有引入额外国家队插件宿主页面，研究原型此前未具备真实 `SectorResearch` 后台完整事实链，这次没有把原型装成正式可用的投资产品。

## 反方测试与剩余问题

| 被质疑的错误做法 | 单元测试或系统门槛 |
| --- | --- |
| 只有双低 PE/PB + 正增长就称值得观察 | 无独立证据返回 `IndependentQualityEvidenceUnavailable` |
| 混用行业或拿别的股票池的财报打补丁 | 行业分类、版本、公司池哈希严格核验 |
| 缺一项质量指标仍“资料完整” | 财报财务指标有逐项门槛，任一必需项缺失则不可进入人工Ready |
| 权益收支覆盖 79%、过期/未审核来源 | 留在 `IncompleteQualityEvidence` |
| 未核对来源许可却可显示确定判断 | `ResearchUseAllowed=false` 被拒 |
| 把 PE 衍生的变量称为独立基本面 | `DerivedFromValuation=true` 被拒 |
| 把 8% 填成 8 或倍数填成月份 | 明确单位与异常数值闸门 |
| 重复指标人工择优 | 抛出 `InvalidDataException` |
| 自由选择最宽松行业经营模式 | 映射规则号 + 事先审核的证据为强制条件 |
| 反过来猜过去当时已经拥有今天回填财报 | 历史首见、首次获得、审核时间进指纹；不声称 PIT 回测有效 |
| 金融、周期、创新企业共用一组财务公式 | 不同业务模式不同必需指标清单 |
| 质量资料齐备就给予买入建议 | 仅 `NeedsAdversarialAssessment`；全模型 `TimingProof=NotProven`，投资结论 NotEvaluated |

**未验收门槛**：

1. 官方或获授权的数据源；已验证字段口径、企业全集、行业成分调整、亏损/停牌/退市公司处理及可再分发范围。
2. 独立PB和行业真实财务历史、审计时间/更正时间、公司池映射；财务季度与交易日的可知时点。
3. 金融类别映射审核与行业细分专属规则：水泥真实产能退出/发运/库存/吨毛利；资源品中周期价格/利润；轮胎境外产能与原材料；医药创新管线和回款，银行不良/拨备/利差。
4. 保留缺失及修订证据，不强行构建历史估值分位；跨周期至少一个完整景气周期，严格滚动样本外对照与误判统计。
5. 已安装 Worker 的数据/程序代次、ABI、持久加密市场库兼容及连续运行性能实证验收。

## 验证入口

```powershell
dotnet run --project tests/CRS.Tests.SectorResearch -c Release -- --output artifacts/sector-research
dotnet run --project tests/CRS.Tests.SectorUi -c Release -- artifacts/sector-research/synthetic-input.json artifacts/sector-research/sector-ui.png
dotnet build CRS.sln -c Release
```

CI 通过仅证明合成输入/输出在当前算法下满足不变式，不表示上述真实源数据及模型预测有效性已经获得证据。不能据本阶段的规则输出具体行业建议或持仓操作。
