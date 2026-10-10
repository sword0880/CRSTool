# CRS 计算证据与确定性复算规范（CALCULATION_EVIDENCE_SPEC）

> **状态：目标契约／实施规范；非现有完整功能说明。**
>
> 版本：1.0（2026-10-10）｜适用：IBKR / 富途导入、FIFO、税额、抵免复核、历史任务、年度归并及 Excel 导出。
>
> 现状依据：`API_SPEC.md`、`CAPITAL_GAIN_FIFO_SPEC.md`、`TAX_ENGINE_SPEC.md`、`FOREIGN_CREDIT_REVIEW.md`、`KNOWN_ISSUES.md`、`SAD.md`。本文件重点承接 `AR-03`、`AR-08`、`AR-09`、`AR-10`、`AR-11`；`AR-02`、`AR-04` 中的已完成逻辑应保留，不重新定义 FIFO 算法。

## 1. 目标、非目标与核心约束

目标是让一个“年度计算结果”可回答：

1. **算了谁**：同一纳税人的哪些券商和账户、哪个税年，输入覆盖范围是什么？
2. **用了什么**：源文件字节、导入器/规范化契约、期初 LOT、计算版本、汇率与来源及人工复核材料是什么？
3. **怎么算的**：哪笔卖出匹配哪些买入批次，成本与费用、舍入余差、已排除事件如何形成？
4. **结果可信到什么程度**：数据完整性、对账、法律适用性、四维状态、问题及不确定值是什么？
5. **将来还能否验证**：原始文件和历史执行环境是否充分；若材料不足，为何只能展示冻结结果而不能复算？

**非目标**：本文件不是税法适用性批准、不是以 SHA-256 代替数字签名、不承诺只凭摘要即可恢复已遗失的原始报表，也不改变现有本地数据库是否已加密的事实。安全加密设计的唯一详细规范是 `SECURITY_DESIGN.md`；Work 正在实施的加密模块由其规范约束，本文只定义计算证据在安全存储中的使用约定。

### 1.1 不可违反的原则

- `Single Read`：同一物理输入在一次运行中只读取到受控、不可变的字节快照，**解析与 SHA-256 必须共享该快照**；不允许重新读取路径并把第二次内容的摘要绑定到第一次解析结果（`AR-08`）。
- `Immutable Run`：一次成功计算产生固定 `RunId`、输入、规则、输出和状态；复算、更正抵免、年度归并都**另存新任务并保留父子关系**，不覆盖旧任务。
- `Explicit Unknown`：未知的成本、外币折算、净可抵免税额和预计补税继续使用 `null`／问题代码，**不能补成 0**；冻结结果并不等于已经获得税法认可。
- `Bounded Inputs`：原件与规范化 JSON 必须做文件/结构/集合规模上限、防重复、版本与必填校验；不信任用户提供的哈希、清单或版本标识。
- `No Secret Export`：源文件内容、券商 Flex 令牌、数据库密码/DEK、绝对路径、完整账号和敏感凭证正文不得进入默认清单、普通日志或公开问题报告。
- `No Plaintext Migration`：发现旧**明文 SQLite** 数据库直接拒绝；**不提供明文 SQLite → SQLCipher 迁移**。本规范中的“契约升级”仅指已支持的**加密保险库**内部业务 schema / 规范化快照的明确版本演进，与明文库迁移不是一回事。

## 2. 现有能力与设计差距

| 能力 | 已有文档确认的实现 | 缺口 / 本规范新增要求 |
| --- | --- | --- |
| 带来源的结果 | 记录文件/行来源、原币事实和汇率来源；有四维状态 | 建立统一的运行证据封套和摘要 |
| FIFO 和排序 | `FIFO_TOTAL_COST_V3`，稳定 `FifoOrdering`，歧义进入 `AMBIGUOUS_FIFO_ORDER` | 固定规范化交易、LOT、匹配边及舍入余差的契约和摘要 |
| 历史结果 | 冻结显示；用户可选完整规范化快照 | 明确“仅可查看” vs “可复算”的可验证条件 |
| 历史复算 | 使用冻结汇率、配置指纹、临时状态；v2 快照比对后另存 | 封存实际执行程序集身份及持久输出摘要；严格前后校验 |
| 输入摘要 | 部分已记录 | 解析和摘要从同一份不可变输入取得（`AR-08`） |
| 规范化 JSON | `CRS.CanonicalInput.v2`（文档当前基线） | 版本化结构上限、必填字段、执行上下文及稳定摘要（`AR-09`） |
| 年度归并 | 同年度、同纳税人确认；拒绝重复、汇率冲突；保留来源关系 | 以有序不可变父任务引用、归并上下文与输出摘要冻结 |
| 导出 | `ExportAsync`、结转资格、Excel 底稿 | 取消/异常使用原子提交，杜绝半成品（`AR-11`） |

> `TAX_ENGINE_SPEC.md` 当前记录 `PolicyVersion = V2-2026-10`；`KNOWN_ISSUES.md` 记录 `FIFO_TOTAL_COST_V3` 和 `CRS.CanonicalInput.v2`。**这些是既有文档所载的当前值，并非新建议值，也不能代替实际程序集的版本核实。**

## 3. 分层与数据模型（拟增加，不声明已有源码）

**Domain**：维持 Trade、SecurityKey、CostLot、Match、TaxRules 等业务事实；可放置与技术无关的证据语义和值对象，但不依赖 JSON、文件、数据库、WPF、SQLCipher。

**Application**：定义证据构建/冻结用例及接口（示例名 `ICalculationEvidenceRecorder`、`IImmutableImportInput`、`IReplayVerifier`，**均为建议，不代表已实现接口**）。统一组织运行标识、源覆盖范围、完整性状态、规则版本和人工复核说明。

**Infrastructure**：实现原始字节读取、SHA-256、序列化/格式校验、加密数据库中的证据持久化、密钥/文件管理及原子导出；`CRS.Security` 仅提供纯加密与密钥机制，不承载计算证据模型或数据库访问。

**Desktop.Wpf**：通过 Application 返回的视图模型展示“来源/算法版本/缺失资料/复算能力”；不能在 UI 重新算税、重写证据，也不能将 `Summary.SupplementTax` 替代受门槛保护的 `EstimatedTopUpCny`。

## 4. 证据封套的逻辑结构（目标字段）

> 字段名为拟订的跨模块**逻辑契约**；实现时可映射到当前 C# 模型，不要求立刻新建所有实体或修改现有序列化格式。

| 模块 | 必需字段（语义） | 注意事项 |
| --- | --- | --- |
| 身份 | `EvidenceSchemaVersion`、`RunId`、`RunKind`、`TaxYear`、`CreatedAtUtc`、`ParentRunIds` | `RunKind`: ImportCalculation / Replay / AnnualAggregate / ForeignCreditReview；不要以时间戳当唯一身份 |
| 范围 | `TaxpayerScopeConfirmation`、`Broker`、`AccountRef`、`StatementFrom/To`、`IsFilteredConfirmed` | AccountRef 使用内部受控代号；不得靠账户所在券商推断纳税人或来源国家 |
| 原始输入 | `SourceId`、`SourceRole`、`ByteLength`、`Sha256`、`MediaType`、`ParserId/Version`、`Coverage` | 哈希来源为真实字节；文件路径仅本地临时使用，不进入默认导出 |
| 规范化输入 | `CanonicalInputVersion`、`CanonicalInputHash`、`InputCount`、`OpeningLotSource`、`NormalizedIssueSet` | 原始与规范化输入都需独立证据；不保存来源全文也可保存最小索引和摘要 |
| 执行环境 | `DomainAssemblyId`、`ApplicationAssemblyId`、`InfrastructureAssemblyId`、`ExecutableBuildId`、`GitCommitOrBuildDigest` | `AR-03` 目标；版本号字符串不足以保证二进制相同 |
| 计算规则 | `TaxPolicyVersion`、`FifoAlgorithmVersion`、`RoundingMode`、`SecurityIdentityRuleId` | 保留 `PolicyVersion` 原值及舍入规则，不在历史重放使用当前默认值 |
| 汇率 | `RatesFingerprint`、每币种 `Rate`、`RateSource`、`EffectivePeriod`、`ObservationCutoff`、`UsageLabel` | 2026 年平均汇率为临时测算，不能标记为法定汇率 |
| 匹配与输出 | `MatchCount`、`EndingLotSummary`、`ReconciliationDigest`、`OutputDigest`、`IssuesDigest` | 摘要应覆盖金额、数量、费用、LOT、状态、来源与未知值 |
| 可用性 | `CalculationStatus`、`DataCompleteness`、`ReconciliationStatus`、`UsageLabel`、`EstimatedTopUpCny` | 展示冻结的后台判定，不由 UI 升级状态 |
| 审核/派生 | `ReviewReferences`、`CreditProofMetadata`、`ParentOutputDigests`、`OwnershipConfirmed` | 审核另存，不因备注直接解除问题；凭证正文默认不落清单 |
| 证据完整性 | `EvidenceDigestAlgorithm`、`EvidenceDigest`、`IntegrityStatus`、`ReplayEligibility` | 摘要用于无意/恶意改动检测，但**仅凭普通哈希不能证明来源可信或不可伪造** |

### 4.1 不同证据的安全存储边界

- **最小运行证据**：不需要持久化 `SessionTrades`；保存非敏感化的来源索引、摘要、版本、汇率、问题及输出元信息，以支持冻结结果核验。
- **完整规范化快照**：沿用用户**主动选择**才能持久化的现行原则。完整快照含账户、交易/成本等敏感信息，只存于已解锁的加密保险库；不能因为要做证据验收而默认改成保存全部交易。
- **原始报表**：用户持有，默认不要求程序永久保存；可记录来源哈希/期初依据，在复算前要求原件重新校验。哈希不包含文件内容；原件丢失时仅凭哈希不能重新解析。
- **抵免证明**：保留现有“凭证名称、SHA-256、税务机关/编号及关联元数据冻结，不复制凭证正文和本地绝对路径”的限制。
- **加密数据库版本**：保险库加密格式和数据库 `user_version` 与本文件 `EvidenceSchemaVersion` 是**三条不同版本链**，不可混为一谈。

## 5. 输入一次读取与来源一致性（AR-08）

**目标步骤：**

1. 读取文件前验证允许的扩展名/实际内容、最大字节数、来源角色、年度及路径权限；防符号链接/路径替换等问题应在受支持平台测试。
2. 将文件读取到**不可变的有界字节快照**（可以采用受限内存或受控临时存储，避免超大文件全部常驻内存），在**同一快照**上计算 `SHA-256`。
3. 解析器只消费该快照，禁止再按原路径打开第二次；将长度、摘要、解析器 ID、覆盖期间与业务 SourceId 一并绑定。
4. 产生规范化事实后校验账户、证券、币种、时间、金额、记录上限和唯一性；把缺字段、冲突、重叠或不支持事件转成当前统一问题代码。
5. 在保存计算前验证来源清单、汇率配置指纹、取消状态和业务门槛；整个结果和证据一起原子提交。
6. 用户重新选择或替换输入时使当前计算/图表/导出资格失效；不可延用旧摘要绑定新文件。

**必须测试：** 文件读取过程中被外部改写、同路径替换、读取后再修改、解析失败、取消、重复报告和包含不同内容但相同文件名等情形。要证明“摘要绑定解析所用字节”，不以路径或文件名相同作为证据。

## 6. 确定性规范化与摘要规则（AR-09/10）

为了稳定复算，建议形成独立于数据库列顺序/JSON 属性反射顺序的**版本化确定性编码**，并写固定测试向量。不得直接对默认 `JsonSerializer` 任意输出进行 SHA-256 后宣称“跨运行时永远稳定”。

**待实现的 `EvidenceCanonicalV1` 编码约束：**

1. UTF-8，不写 BOM；对象字段按显式 schema 顺序；属性不存在与 `null` 语义分开；对非预期字段采取明确拒绝或兼容策略，不能忽略影响税额的未知字段。
2. 业务 `decimal` 用明确文化无关的十进制字符串编码（定义首尾 0、负零、scale 的规范规则）；不经过二进制浮点；输入原始精度如影响核对，应在原始事实字段另外保留。
3. UTC 时间与原始偏移/无时区状态分开编码；不得擅自把无时区的券商时间当成北京时间或 UTC。原文支持 `HasOffset` 语义。
4. 列表保持**有业务意义的顺序**（如成交先后、匹配顺序），其顺序必须由现有 `FifoOrdering`/明确行序事实决定；仅对语义无序的 Issues、账户摘要等按稳定键排序。
5. 完整身份采用 `SecurityKey` 的账户、券商、市场、证券、币种等字段，不仅用股票代码字符串。
6. 输出摘要要覆盖不确定金额的 `null`、状态四元组、问题代码/上下文、期初/末 LOT、买卖匹配、舍入余差、年度汇总父子关系、外币原始事实和汇率来源。
7. 在 schema 中定义单文件字节上限、列表元素上限、字符串上限、最大嵌套深度、哈希算法、非空必填字段；具体阈值沿用当前输入限制并通过压力测试确定，不在此臆造已通过值。

**重要：** `CRS.CanonicalInput.v2` 是现有快照格式，而 `EvidenceCanonicalV1` 是**建议新增的证据摘要编码**；不能无验证地把它们当同一种版本，也不能以新规则覆盖旧历史快照。

## 7. 运行证据示例（示意，不是当前 API）

```json
{
  "evidenceSchemaVersion": "CRS.Evidence.v1",
  "runId": "sample-run-id",
  "runKind": "ImportCalculation",
  "taxYear": 2025,
  "createdAtUtc": "2026-10-10T08:00:00Z",
  "parentRunIds": [],
  "sources": [{
    "sourceId": "source-1",
    "sourceRole": "ActivityFlexXml",
    "broker": "IBKR",
    "accountRef": "masked-account-1",
    "byteLength": 12345,
    "sha256": "<actual-64-hex-digest-required>",
    "parserVersion": "<build-metadata-required>"
  }],
  "canonicalInputVersion": "CRS.CanonicalInput.v2",
  "canonicalInputDigest": "<calculated>",
  "taxPolicyVersion": "V2-2026-10",
  "fifoAlgorithmVersion": "FIFO_TOTAL_COST_V3",
  "ratesFingerprint": "<frozen-fingerprint>",
  "buildDigest": "<hash-of-real-build-artifact>",
  "statuses": {
    "calculationStatus": "<backend-value>",
    "dataCompleteness": "<backend-value>",
    "reconciliationStatus": "<backend-value>",
    "usageLabel": "<backend-value>"
  },
  "estimatedTopUpCny": null,
  "outputDigest": "<calculated>",
  "evidenceDigest": "<calculated>"
}
```

**示例值不能用作测试基准**。在实际 schema 设计中，`EvidenceDigest` 应覆盖已确定的其他字段，但在计算它自身时排除自身，以避免自引用；需要定义 hash 输入字节和签名边界。如未来提供外部可信证明或防篡改审计，需进一步考虑数字签名与密钥信任管理；单纯文件哈希不构成真实性/来源认证。

## 8. 历史查看、重放和年度归并

### 8.1 Replay 资格判断

- `FROZEN_VIEW_ONLY`：仅保存历史冻结结果或缺完整可复算快照；允许查看、受资格限制的导出，不虚构交易明细。
- `REPLAY_POSSIBLE`：完整受支持的规范化快照与历史汇率、版本、原始期初 LOT 和舍入余差等俱备；**此状态不等于已经验证复算相同**。
- `REPLAY_BLOCKED`：旧快照版本不兼容、执行环境不可恢复、输入摘要不一致、来源缺失、未支持公司行动、数据损坏等；给出具体原因。
- `REPLAY_VERIFIED`：采用冻结的历史规则/汇率/输入按既定格式执行，完整输出一致性核验通过，新复算结果**另存父子任务**。

**现有实现事实**：`ReplayAsync` 当前要求 v2 快照和 V3 排序口径，保留冻结汇率/指纹与临时状态，不一致时不保存。**尚缺**严格执行程序集封存与持久输出摘要，所以文档中不能把当前所有 Replay 称为“强审计可复现”。

### 8.2 年度多账户归并

- 只接受同年度、同纳税人且经用户确认、未重复/冲突的来源任务；排除已经归并的任务再次作为来源。
- 归并时固定 `ParentRunIds` 及其输出摘要，原币事实汇总后**只运行一次**税额算法；不能直接加总各子任务的已限额抵免税额。
- 保存新的归并 Run，原始任务/外币事实/汇率/状态不改动；缺少旧任务原币事实时应继续报告不完整。

### 8.3 外国税额抵免派生任务

- 保持复核任务保留父任务编号、原任务不改写；保存国家分配、税款/凭证名称及摘要、`TAX_CREDIT_REVIEW` 状态。
- 当前凭证复核派生任务不支持沿用旧规范化快照等价重放；标记 `REPLAY_BLOCKED`，不得悄悄调用当前规则重算。

## 9. 导出证据包与原子提交（AR-11）

**目标**：Excel/结转导出、未来加密导出都须在业务状态/指纹门槛通过后提交；失败和取消不得留下看似已完成的半成品。

1. 在目标文件所在存储卷的临时位置写入并完成数据/证据摘要验证；不要先覆盖目标文件。
2. 重新检查取消令牌、冻结结果身份、配置指纹、导出资格和目标文件权限；必要时确保文件句柄已关闭。
3. 使用平台支持的安全替换/移动方案**原子提交**；跨卷复制不能被当作原子操作。
4. 失败时清理本次临时产物且保留已有目标文件；写入记录仅保留脱敏路径和运行 ID。
5. Excel 中显示适当的“计算来源/规则版本/证据编号/临时汇率/不完整状态”摘要页，但不能默认复制所有敏感源文件或凭证正文。
6. 加密导出和容器完整性由 `SECURITY_DESIGN.md` 及 Work 进行中的实现/验收管理；本文件只规定计算内容与证据边界。

## 10. 验收用例矩阵

| ID | 输入/操作 | 必须证明 | 关联事项 |
| --- | --- | --- | --- |
| `EV-T01` | 同一路径读取时原件被修改 | 解析事实与哈希始终来自相同字节，或显式失败 | AR-08 |
| `EV-T02` | 同文件名不同内容/相同内容不同路径 | 内容摘要正确；路径不影响事实身份与复算 | AR-08 |
| `EV-T03` | 规范化字段缺失、超大 JSON、过深嵌套 | 拒绝非受支持输入；不占用无上限内存 | AR-09 |
| `EV-T04` | 改变 JSON 字段顺序/文化环境 | 语义相同事实的证据摘要一致；差异可解释 | AR-09 |
| `EV-T05` | 有意义交易顺序变化/同秒歧义 | 不猜测先后；歧义阻断可信成本 | AR-04 |
| `EV-T06` | 调整手续费、汇率、舍入余差或 `null`/0 | 输出/证据摘要变化，旧历史不被覆盖 | AR-03/10 |
| `EV-T07` | 仅存冻结结果无完整快照 | `FROZEN_VIEW_ONLY`，不伪造历史交易 | 当前设计 |
| `EV-T08` | v2 完整快照、历史冻结汇率 | 严格比对金额、LOT、问题、状态、来源；新 Run 保留父任务 | AR-02/03 |
| `EV-T09` | 旧结转/不受支持快照版本 | 明确阻断；不把同名 `Version=1` 判定兼容 | AR-05 |
| `EV-T10` | 同税年跨券商归并、重复来源 | 身份和范围正确，拒绝重复，归并来源链冻结 | AR-01 |
| `EV-T11` | 抵免凭证后补/派生任务 | 父结果不变、元数据摘要固定、预计补税不越权打开 | AR-06 |
| `EV-T12` | 导出途中取消/磁盘满/目标文件已存在 | 不留下半成品、不覆盖已存在的有效产物 | AR-11 |
| `EV-T13` | 锁定保险库时计算/导出尚未提交 | 会话终止后不得再保存任务、明文导出或持久证据 | SEC-03/06 |
| `EV-T14` | 真实全年样本 + 独立人工基准 | 结果、事实、状态和输出摘要可核对；测试数据不入 Git | AR-12 |

验证工具仍以 `./tests/Run-ReleaseAcceptance.ps1 -Smoke` 和带 `-RealManifest` 的真实样本入口为准；新增测试与门槛实施后再同步修改 `TEST_SPEC.md` / `RELEASE_ACCEPTANCE.md`。**没有验收产物就不能标记为 `VERIFIED`。**

## 11. 分步实施计划

| 批次 | 目标 | 修改位置建议 | 完成证据 |
| --- | --- | --- | --- |
| E1 | 统一运行证据字段/版本，保持历史兼容 | Application DTO / Infrastructure 存储 | 新旧历史读取验证、类型/结构上限测试 |
| E2 | 一次读取/解析+哈希绑定 | BrokerImporter / 输入适配层 | EV-T01/02；失败/取消不写入 |
| E3 | 确定性编码、完整输出摘要 | Application + Infrastructure 证据适配 | 固定测试向量；文化/序列化顺序一致 |
| E4 | 冻结执行程序集、加强 Replay | ReplayExecutionContext / 发布流水线 | build digest、历史验证、缺版本阻断 |
| E5 | 导出原子提交与证据页 | IReportExporter / WPF 报告入口 | EV-T12 与真实导出复核 |
| E6 | 与已加密保险库整合验收 | Infrastructure 存储/会话门槛 | 锁屏取消、备份恢复和证据完整性 |

**不做的工作：** 改写既有金额公式、自动迁移明文 SQLite、默认保存所有原件、为了复算而绕过 `UsageLabel` 或完整性门槛、在历史缺失情况下用最新税法/汇率冒充旧版运行。

---

**职责关联：** `TAX_RULE_VALIDATION.md` 约束**税务规则能否用于目标场景**；本文件约束**某次结果用了什么输入/规则并能否复现**；`RELEASE_PLAN.md` 综合两者决定发布验收。
