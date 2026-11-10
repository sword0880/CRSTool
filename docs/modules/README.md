# 业务模块文档

本目录按业务域组织需求、专项设计和模块入口。具体实现进度仍维护在 [implementation/](../implementation/README.md)，当前架构与发布风险分别以 [架构状态](../architecture/ARCHITECTURE_STATUS.md) 和 [问题清单](../KNOWN_ISSUES.md) 为准。

## 模块

- [国家队与市场研究](national-team/README.md) — ETF份额、机构直接持股、披露证据、NAV、减持预警和南向研究

新增模块前，先确认它是否拥有独立的业务模型、数据边界、生命周期或验收标准；纯公共能力和小型功能不应为了目录整齐而机械拆成模块。
