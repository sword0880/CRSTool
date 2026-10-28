# 当前架构状态

更新：2026-10-28。本文件是当前模块边界、未关闭高风险、正式部署及证据的唯一状态索引。实施文档和验收日志保留历史时点；冲突时先核对现场回执，不能按文件日期推定已部署。

## 模块边界

| 模块 | 当前职责与约束 |
| --- | --- |
| CRS.Common | 时间、指纹、有界内容、路径防护、认证文件、文件暂存及读取准入；不包含国家队主体或金融规则 |
| CRS.Documents | 文件格式和文档适配；业务归一、来源审批、金额计算归所属领域 |
| Worker.Abstractions / Ipc / PluginHost / PluginRunner | 后台契约、授权通信、可信包及独立进程生命周期 |
| Worker.Service | 服务密钥、计划/执行/命令持久化、来源预算、维护、文件入口及健康；默认全局任务并发8 |
| NationalTeam.Core | ETF、成交冷热、机构目录、直接持股、行业、NAV、周期及退出规则；无数据库或WPF依赖 |
| NationalTeam.Jobs | Etf、EtfHoldings、Equity、Nav、IndustryValuation、MarketCycle、ExitAlerts、Evidence分目录；功能Store及同事务组合读；后台执行金融写入 |
| NationalTeamWatch / Plugin.Wpf | 版本化客户端、展示模型和页面；不直接开库或计算正式金额 |
| 收入/报税核心 | 独立个人密钥域及加密存储；不纳入国家队迁移；税务P0正确性按用户要求最后处理 |

国家队业务库和来源策略库已使用普通SQLite，审批认证仍需派生密钥；Worker调度管理库保留加密。`MarketDatabaseSession`拥有连接及只读事务，各功能Store拥有初始化及持久化，`MarketRepository`组合窄端口。

## 未关闭高风险

| 项目 | 当前缺口与关闭证据 |
| --- | --- |
| 查询串行化 | 正式宿主、插件和客户端仍有每插件单查询门闩。须贯通短读、重型研究读、写入、后台采集四类独立并发与有限等待、取消及维护排空；后台并发8及原件门槛保留 |
| 长研究快照 | 联合研究曾在15秒取消。需异步提交、状态、取消及快照ID分页；验证有限队列、身份绑定、失败/过期及重启边界，不能靠扩大所有超时关闭 |
| 批量验收 | 工作区已开发能力版本及全池客户端入口，待完整回归和正式部署；正式端原有全池服务能力不能证明桌面逐只往返已消除 |
| 中央入口扩展 | 49个既有查询及25个任务已移入8个模块的注册处理器，新增批量/能力处理器由模块声明；待编译、注册一致性、真实Runner及回归验收 |
| 持仓及方向 | 正式24只ETF未读到已核验具名持仓；直接持股仅部分样本。需主体、原件、报告期、行业/价格及完整组合分母，缺失不补零 |
| 估值/周期/退出有效性 | 行业公布PE不等于完整PE/PB估值，NAV不等于周期。完整行情/广度、历史知识时点、样本外及影子验证开放；联合观察不发正式退出通知 |
| 发行和恢复 | 当前为开发包显式豁免；签名发行、第三方权限隔离及完整跨机灾备未由本机Committed关闭 |

## 正式部署及证据

| 范围 | 当前状态 | 对应证据 |
| --- | --- | --- |
| Worker | LocalService / Running，维护后PID46520，代次`73215e55abac4261875ec22a9f6bb86b`已Committed | `artifacts/storage-maintenance-installed-20261028/storage-acceptance.json` |
| 双库保全 | 两库普通SQLite；5959份快照、两份原库归档、182项当前/160项旧发行及原密钥摘要通过 | 同上；[迁移记录](../implementation/NATIONAL_TEAM_STORAGE_MIGRATION_20261028.md) |
| 原资料 | 7项计划、24只ETF配置、29条股票投影及10条执行记录保留 | `artifacts/storage-maintenance-installed-20261028/after/baseline-comparison.json` |
| 功能抽检 | 2026-10-27闭日：持仓历史、周期、广度可用；预警总览及联合研究两项Unavailable；前后健康/PID一致 | `artifacts/storage-maintenance-installed-20261028/after/feature-audit.json` |
| 查询基线 | 全池9406ms，其中5931份原件7175ms、采集SQL1746ms；单只512100为1315ms。仅本次采样，缓存/自然负载未受控 | `artifacts/research-query-baseline-20261028/acceptance-all/profile.json`及`acceptance-512100/profile.json` |
| 工作区增量 | 同只读事务原件复用15项隔离反例通过；注册分派及批量客户端待回归、未安装；本轮未验收桌面真实交互 | `artifacts/research-snapshot-verification-20261028/acceptance.json` |

整个NationalTeam目标仍未完成。源码、隔离机制、候选包、正式服务和金融数据/模型证据分别闭合。
