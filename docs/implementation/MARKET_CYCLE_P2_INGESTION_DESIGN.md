# 市场周期 P2 数据接入与持久化设计（2026-10-25）

- 版本：0.1
- 状态：架构与来源门禁阶段；**未批准真实自动采集/历史回填/正式预警**
- 前置文档：[市场周期信号设计](MARKET_CYCLE_SIGNAL_DESIGN.md)、[数据源可行性验证](MARKET_CYCLE_DATA_SOURCE_FEASIBILITY_20261025.md)、[反方审查](../architecture/MARKET_CYCLE_SIGNAL_ADVERSARIAL_REVIEW_20261024.md)
- 原则：复用 NationalTeam Core / Jobs / Worker 的既有边界、请求预算、原件存证和市场库；不创建第二个 Worker、第二套 Quartz 或第二个市场数据数据库。

## 1. P2 目标与非目标

### 目标
1. 将数据使用权限变成机器可检查的显式准入条件。
2. 为指数日线、市场成交额/量、市场广度和历史证券宇宙建立独立来源契约。
3. 复用现有 source_fetch 与 RawEvidenceStore，保留原件、哈希、获取时间、解析器版本、修订及人工审核状态。
4. 对历史最终值和严格点时数据作出明确区分。
5. 先实现可离线测试的来源门禁和契约，再在获得授权与真实黄金样本后逐个启用来源。

### 非目标
- 本阶段不猜测或新增未验证的 SSE/CSIndex/SZSE API 端点。
- 不绕过 403/429、不伪装浏览器身份、不切换未批准的数据供应商。
- 不把当前 security_master、ETF 观察池或当前上市名单当作历史全市场证券宇宙。
- 不把 ETF 份额变化或市场成交额归因为国家队交易。
- 不修改 CRS 税务数据库、认证、生产 Worker 配置或正式通知开关。

## 2. 组件与职责

| 层 | 组件/职责 | 禁止事项 |
|---|---|---|
| Core / Domain | MarketCycleEvaluator、规则版本、独立事实类型、确定性评分和解释 | 不联网、不查系统时钟、不写数据库、不发通知 |
| Core / Application | 来源使用权限模型、MarketCycleSourceAdmission 门禁、采集/存储端口契约 | 不把网页公开性当授权；不内置已批准的来源白名单 |
| Jobs / Infrastructure | 复用 OfficialHttpGateway、SourceRequestPolicy、RawEvidenceStore、MarketRepository；经批准后实现具体适配器、仓储与迁移 | 不新增不受控 HttpClient；不静默回退；不覆盖旧原件 |
| Worker / Scheduler | 使用现有插件作业注册与 Worker/Quartz 生命周期；按来源预算、可取消、幂等执行 | 不新增第二个调度宿主；未过准入门槛不注册采集作业 |
| Tests | 合成响应、真实黄金样本、来源授权门禁、迁移升级、点时/冲突/恢复 | CI 不调用真实交易所；合成响应不冒充官方黄金样本 |
| UI | 后续只读展示来源许可状态、数据新鲜度、覆盖、阻断原因和研究结果 | 不自行计算金融事实；不在本阶段新增自动告警 |

## 3. 来源权限与契约

### 3.1 四种用途必须分开批准

每个来源的人工审批记录必须包含：稳定 SourceId、HTTPS 来源地址、数据集类型、来源契约版本、解析器版本、许可/条款证据 SHA-256、审核人、审核时间、审批策略版本、审批状态和批准用途。

| 用途 | 必需权限 |
|---|---|
| 自动轮询/日常采集 | AutomatedCollection + PersistentStorage |
| 历史批量回填 | HistoricalBackfill + PersistentStorage |
| 对外再分发 | Redistribution |
| 仅网页可访问/手工检查 | 不因此自动获得上述任一权限 |

审批状态不是 Approved、条款证据哈希缺失/格式错误、审核人/版本缺失、HTTPS 来源地址无效、审批尚未生效或已过期，均必须失败关闭。准入函数必须由调用方传入评估时点，禁止在领域层读取当前系统时间，以便测试和回放可复现。

当前仓库不应预置任何“已批准”的市场周期来源。测试可以构造合成审批对象；真实来源批准必须由人工审阅实际条款/合同证据后提供。

**当前代码边界：** MarketCycleSourceAdmission 是输入完整性与用途权限门禁，不负责认证审核人身份，也不防止任意调用方伪造审批对象。当前 PR 尚未实现可信审批注册表、受控审批写入、签名或持久化审计，因此不能把此类记录当作生产授权。后续真实采集/回填作业必须先从受 ACL 保护的注册表加载审批，校验记录与条款证据的绑定及变更审计；该工作完成前禁止注册真实作业。

### 3.2 数据集和契约边界

- IndexDaily：IndexCode + TradeDate + Close，还需来源发布时间、首次发现/获取时间、原件哈希、解析器版本和审核状态。收盘价必须是指数本身的官方口径，不能用 ETF 价格代替。
- MarketActivityDaily：复用现有 market_activity_observation；金额和成交量保持独立字段，单位统一为人民币元/原始股数。每个交易所先单独验收，只有两市数据均完整且统计范围一致时才允许计算合计。
- MarketBreadthDaily：单独存储站上 MA20/MA60 数量、创新低数量、证券宇宙分母及 UniverseSnapshotId/Hash/DefinitionVersion。缺一个关键分母即阻断。
- SecurityUniverseSnapshot：以交易日为版本，保存纳入/排除的证券及理由、证券类别、上市/退市生效区间和快照哈希。必须保留历史退市证券，不可只从当前证券名录倒推。
- EtfQuote/Nav/TotalShares：继续沿用现有 ETF 证据轴，不能与市场周期事实混表或共用资金流语义。

每个来源响应只对应自己的原件和 source_fetch 记录。多个指数可以引用同一条市场级成交/广度事实，但不可复制为每个指数一条独立市场成交额后再汇总。

## 4. 建议的数据流

1. **准入检查**：先用 MarketCycleSourceAdmission 验证审批状态、用途权限、条款证据和评估时点；失败时不发 HTTP 请求。
2. **请求**：通过现有 IPluginHttpGateway / OfficialHttpGateway 和持久化 SourceRequestPolicy。不要自动跟随重定向，不绕过 403/429，取消必须传递到底层网络操作。
3. **原件存证**：成功响应先通过 RawEvidenceStore.Save 保存原始字节；记录来源 URL、获取时间、来源发布时间（没有就保留 null）、原件哈希和解析器版本。
4. **合同解析**：校验内容类型、响应结构、来源 ID、目标日期、市场/指数代码、字段名、单位、记录数、分页完整性及数值范围。HTTP 200 或非空页面本身不是成功验收。
5. **质量分类**：Accepted / NeedsReview / Critical / Rejected。开放交易日返回空数据不等于零成交；它是缺失/待核验，不能作为正常零值参与模型。
6. **事实持久化**：追加修订，不覆盖原始事实；接受某修订必须保留审核人、时间、理由及被取代版本。相同原件可幂等去重，不同值不能按最后写入胜出。
7. **点时快照**：只使用评估截止时点可见且已接受的修订。当前批量回填的历史最终值标为 HistoricalFinalOnly，不得伪造过去的 FirstSeenAt。
8. **研究评估**：仅在指数、成交、广度和交易日历各自通过完整性门槛后调用 MarketCycleEvaluator；将规则版本、输入指纹、所有来源修订 ID、数据状态、计算指标和阻断原因保存为不可变结果快照。
9. **只读呈现**：本阶段不外发通知、不下单。数据源失败/过期时展示数据不可用，不显示风险解除或市场安全。

## 5. 存储与迁移设计

现有市场库由 MarketRepository 管理，当前 schema 为 v2。P2 需要**增量迁移到 v3**，不允许删除数据库重建或覆盖原 ETF/市场成交数据。

建议新增表（最终字段须在实现前对照当前 source_fetch 和质量审核约束审查）：

### market_cycle_index_observation
- revision_id 主键；index_code、trade_date、close_value、fetch_id 外键。
- revision_no、status、first_seen_utc、accepted_at、supersedes_id、parser_version。
- 同一指数同一日期可以保留多份原件/修订；当前接受指针不能删除历史修订。

### security_universe_snapshot
- snapshot_id、trade_date、definition_version、snapshot_hash、fetch_id、status、首次发现/审核时间。
- 同一日期的冲突快照不可同时被标为可用；新定义版本不得覆盖旧版本。

### security_universe_member
- snapshot_id、market、ticker、证券类别、纳入/排除状态、原因代码、上市/退市生效日。
- 保留历史退市证券；不以当前活跃名单删除历史成员。

### market_cycle_breadth_observation
- revision_id、trade_date、snapshot_id、MA20/MA60 站上数量、创新低数量、有效分母、fetch_id、审核状态。
- snapshot_id 必须引用通过审核的同日证券宇宙快照；分母必须等于该快照按定义计算出的有效成员数量。

### market_cycle_evaluation_snapshot 与 market_cycle_evaluation_evidence
- 保存 as_of_date、evaluation_cutoff、rule_version、input_fingerprint、data_status、顶部/底部结果、输出 JSON/规范化指标、创建时间。
- 单独关联输入的每个指数/成交/广度修订 ID 和交易日历证据 ID，支持从结果追溯到原件。
- 不允许更新已完成快照；重算生成新版本并记录原因。

已有 market_activity_observation 与 source_fetch 应复用，不新增重复的 market turnover 表。source_fetch 若缺少某个字段，应先评估向后兼容的增量扩展；不得把所有事实的来源元数据复制到一个含义模糊的通用字段。

### 迁移要求
- 显式支持当前 v2 → v3 的幂等增量迁移；新建空库时按顺序创建基线结构与 v3。
- 迁移前后核验 user_version、行数、外键、索引、WAL/备份行为及原件完整性。
- 未知版本、部分迁移、缺表或加密密钥错误时启动失败关闭；不得自动删除/重建用户数据。
- 迁移回滚通过备份恢复，而不是在原库上做破坏性降级。
- 在真实库只读检查与离线副本迁移通过前，不运行任何真实数据写入或回填。

## 6. 后台作业与幂等

| 作业 | 触发方式 | 约束 |
|---|---|---|
| MarketCycleSourceContractAudit | 人工/发布前 | 检查来源审批、契约版本、许可有效期、真实黄金样本状态；失败时不注册采集作业 |
| MarketCycleDailyCollection | 正式批准后，收盘后按来源节奏 | 先日历再请求；每个来源独立状态；同一原件幂等；保留失败与缺失原因 |
| MarketCycleHistoricalBackfill | 显式手工发起、给定日期区间 | 必须单独拥有历史回填权限；限速、可取消、可续跑；不伪造点时可见时间 |
| MarketCycleEvaluate | 事实审核/修订通过后 | 只评估完整的同一时点输入；输出不可变快照；不发通知 |
| MarketCycleIntegrityAudit | 复用既有完整性维护周期 | 检查原件哈希、外键、修订冲突、证券宇宙快照和指纹 |

所有作业必须注册在现有 Worker/Quartz 插件生命周期中。来源预算沿用 OfficialHttpGateway 与 SourceRequestPolicy，不允许通过新增作业绕过请求节流。没有获批的来源不应注册真实采集作业；本阶段仅测试门禁，不运行真实联网任务。

## 7. 分阶段实施与验收

### P2.0（本次）
- [x] 公开官方来源可行性初步核验并记录来源/许可缺口。
- [x] 实现并测试来源用途权限输入门禁，要求审批记录必须与可信注册表的当前记录逐字段匹配。
- [x] 新增目标绑定门禁 `RequireTarget`：每次请求还必须匹配实际 SourceId、DatasetId、HTTPS 主机/端口和路径边界，不能只验证审批对象后请求任意 URL。
- [x] 复用现有 SQLCipher 加密 market.db，新增追加式来源审批注册表、HMAC 审计链、命令 ID/nonce 去重及撤销状态。
- [x] 无 IMarketCycleApprovalWriteAuthority 时审批写入默认拒绝；测试授权器仅用于离线测试，不是生产授权实现。
- [x] 明确复用现有 HTTP、请求预算、原件存证和市场库；未新增真实行情网络调用。
- [ ] 生产管理员身份/权限授权器尚未接入桌面/Worker 可信组合根，故生产审批写入仍关闭。
- [x] 增加与事件链分离、由数据库密钥派生 MAC 保护的链头锚点，能够检测未同步修改锚点的尾部截断。
- [ ] 事件链与链头锚点仍在同一 market.db；协调一致的旧快照回滚仍无法被单库内检测，生产前若需防此类攻击，必须增加独立受保护的单调性锚点。
- [ ] 真实官方黄金样本与数据许可仍未取得。

### P2.1（获得来源许可后）
- [ ] 每个来源登记 SourceId、合同版本、解析器版本、真实样本哈希和允许用途。
- [ ] 以真实原件完成解析、单位、日期、分页、空数据与错误响应测试。
- [ ] 先只接一个官方基准指数的日终收盘，不同时扩展所有指数。

### P2.2（真实历史和证券宇宙可用后）
- [ ] 市场成交额/量两市独立验收并验证合计口径。
- [ ] 实现历史证券宇宙快照、公司行为与个股历史数据接入。
- [ ] 用独立黄金样本复算 MA20/MA60 广度与创新低比例。

### P2.3（点时证据闭环后）
- [ ] 实现 v2 → v3 迁移、修订审核、输入证据关联与不可变评估快照。
- [ ] 完成历史覆盖、缺失日、修订冲突、数据损坏、取消和恢复测试。
- [ ] 历史最终值与严格点时数据在 UI/API 中明确区分。

### P2.4（独立验收后）
- [ ] 再进行滚动样本外回测；报告误报、漏报、提前/滞后及基准比较。
- [ ] 另行批准 UI/Worker/通知接线；不得从 P2 自动升级为生产预警。

## 8. 本轮反方结论

本轮不应直接编写新的生产行情抓取器。真正的 P0 阻塞不是缺少 HTTP 代码，而是来源许可、未文档化接口、历史点时证据和历史证券宇宙尚未形成闭环。先把这些约束做成机器可检查的门禁，才可以安全地逐个接入真实来源。
