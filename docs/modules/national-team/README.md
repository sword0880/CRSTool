# 国家队与市场研究

本目录维护国家队 ETF、机构直接持股和大规模减持预警的业务设计入口。它不是实施状态的第二份台账：**当前实现、部署和验收必须回到下列权威文档核对**。

## 当前入口

| 主题 | 当前权威文档 | 用途 |
| --- | --- | --- |
| 模块功能边界 | [功能分类与增长治理](../../architecture/NATIONAL_TEAM_FEATURE_CLASSIFICATION_20261026.md) | 当前模块分类、功能归属和扩展规则 |
| 架构与部署状态 | [架构状态](../../architecture/ARCHITECTURE_STATUS.md) | 当前边界、正式部署证据和未关闭风险 |
| 总体实现 | [国家队模块架构](../../implementation/NATIONAL_TEAM_ARCHITECTURE.md) | Core/Jobs/UI拆分、存储和模块演进 |
| ETF与披露证据 | [披露证据与通知](../../implementation/NATIONAL_TEAM_EVIDENCE.md) | 原始报告、审核、具名持仓、证据链与预警 |
| 直接持股 | [机构直接持股](../../implementation/NATIONAL_TEAM_EQUITY.md) | 股票观察池、股东快照和披露核验 |
| NAV | [NAV研究](../../implementation/NATIONAL_TEAM_NAV.md) | NAV事实、修订和存储边界 |
| 市场研究 | [市场研究](../../implementation/NATIONAL_TEAM_MARKET_RESEARCH.md) | 行业、市场周期、估值和退出研究 |
| 南向资金 | [南向研究](../../implementation/SOUTHBOUND.md) | 南向资金事实与年度累计比较 |
| 待办与发布门槛 | [问题清单](../../KNOWN_ISSUES.md) · [发布验收](../../validation/RELEASE_ACCEPTANCE.md) | 未关闭工作与发布判断 |

## 专项设计

- [大规模减持预警设计](DRAWDOWN_ALERT_DESIGN.md) — 预警车道、证据等级、事件生命周期、回测和验收要求

## 历史资料

- [历史一期设计与评审归档](../../archive/national-team/README.md)
- [历史数据源POC](../../validation/national-team/DATA_SOURCE_POC_20261014.md) — 2026-10-14技术验证快照，不代表当前生产验收

## 维护规则

- 当前实现进度只更新 implementation/NATIONAL_TEAM_*.md 对应主题，不在本目录追加日期化进度副本。
- 新规则必须标明证据等级、可用输入、未知状态和失败关闭行为；不能将ETF全市场份额直接解释为机构持仓或真实成交。
- 历史回放、公开数据POC和正式连续采集是不同证据类型，不得相互替代。
