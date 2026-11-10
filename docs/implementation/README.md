# 实施文档

每个功能只维护一份固定文件名的说明，直接更新对应文件，不再增加日期后缀或平行版本。日期仅用于正文中的历史验收与证据标记；源码和实际回读优先于历史记录。总体状态见[当前架构](../architecture/CURRENT_ARCHITECTURE.md)、[待办](../KNOWN_ISSUES.md)和[发布验收](../validation/RELEASE_ACCEPTANCE.md)。

## 架构、后台与界面

| 功能 | 内容 |
| --- | --- |
| [架构实施](ARCHITECTURE_IMPLEMENTATION.md) | 导入事务、窄端口、读写隔离、Runner及恢复边界 |
| [后台运行](WORKER_RUNTIME.md) | 服务、Quartz计划、权限、并发、取消与部署 |
| [任务中心](TASK_CENTER.md) | 当前活动、历史筛选、手动执行与日志口径 |
| [查询性能](QUERY_PERFORMANCE.md) | 快照、完整分页、批量传输、取消与测量限制 |
| [桌面与设置](DESKTOP_UI.md) | 导航、设置草稿、标题栏与独立维护窗口 |
| [品牌视觉](BRAND_VISUAL_SYSTEM.md) | 调色板、公共样式、状态色及界面验收 |
| [北京时间](BEIJING_TIME.md) | 时间生成、计划、历史混合偏移与日期事实 |
| [公共文档](DOCUMENTS.md) | PDF/Excel读取生成与业务解析边界 |

## 数据管理与保护

| 功能 | 内容 |
| --- | --- |
| [数据库与备份管理](DATABASE_BACKUP_MANAGEMENT.md) | 独立维护窗口、当前位置、备份与恢复 |
| [运行资料清理](RUNTIME_DATA_CLEANUP.md) | 扫描及清理进度、保留范围、预览与执行 |
| [加密数据库升级](DATABASE_UPGRADE.md) | 私人业务库结构升级、升级前备份与程序集归档 |
| [备份容量](BACKUP_CAPACITY.md) | 分块加密容器、资源配额与读取保护 |
| [锁定中断](LOCK_INTERRUPTION.md) | 备份/恢复取消、旧会话隔离及提交边界 |
| [Windows Hello与手机验证](WINDOWS_HELLO.md) | 首次选择、认证凭证及恢复限制 |
| [敏感导出](SENSITIVE_EXPORT.md) | 导出确认、目的地、暂存与诊断保护 |

## 税务、来源与绩效

| 功能 | 内容 |
| --- | --- |
| [富途字段](FUTU_FIELD_MAPPING.md) | 来源结构、字段投影与未知资料 |
| [导入一致性](INPUT_CONSISTENCY.md) | 原件读取、摘要及事务一致性 |
| [规范快照](CANONICAL_VALIDATION.md) | 版本、结构、上限与损坏拒绝 |
| [冻结证据](FROZEN_EVIDENCE.md) | 不可变结果、固定编码、程序集与载荷摘要 |
| [FIFO](CAPITAL_GAIN_FIFO_SPEC.md) | 身份、排序、成本、费用与结转 |
| [税额引擎当前实现](TAX_ENGINE_SPEC.md) | 当前 PolicyVersion、公式和代码边界；法律适用性见税务规则验证规范 |
| [金额展示](AMOUNT_PRESENTATION.md) | 公式金额、预计值及未知与零的区别 |
| [投资绩效](PERFORMANCE_ANALYTICS.md) | 导入、净值/TWR/XIRR、证据、历史与导出 |

## 国家队与市场研究

| 功能 | 内容 |
| --- | --- |
| [模块架构](NATIONAL_TEAM_ARCHITECTURE.md) | Core/Jobs/Watch所有权、存储迁移与并发 |
| [ETF配置与份额](NATIONAL_TEAM_ETF.md) | 实际配置、日历、份额图、成交同步与恢复 |
| [股票持仓](NATIONAL_TEAM_EQUITY.md) | 股票列表、具名资料、期间变化与行业方向 |
| [披露证据与通知](NATIONAL_TEAM_EVIDENCE.md) | 报告目录、原件审核、具名历史与预警证据 |
| [NAV事实与金额研究](NATIONAL_TEAM_NAV.md) | 保留的后台净值、修订及复算；独立桌面页面已移除 |
| [行业、周期与联合研究](NATIONAL_TEAM_MARKET_RESEARCH.md) | 公布PE、独立质量证据、广度审核及模型门槛 |
| [南向年度累计净买入](SOUTHBOUND.md) | 金额连续性、单一年度图、缩放及版本合同 |

## 维护规则

1. 先更新所属功能文件；跨功能只引用，不重复复制实现或测试流水。
2. 保留口径、权限、数据边界、验证入口、重要证据身份与开放问题；已被后续实现取代的步骤及重复构建日志不再堆积。
3. 历史测试结果只说明当轮输入，不作为当前安装状态、真实金融覆盖或发布通过的证明。历史完整内容可从版本记录追溯。
4. 改名或合并时同步核对Markdown相对链接、片段、`CRS.sln`及脚本引用。旧专题片段保留兼容锚点，后续新链接使用功能文件与当前章节。

[返回文档首页](../README.md)
