# 小财牛文档中心

这里是 CRSTool（小财牛）的统一文档入口。文档按**架构、业务模块、使用指南、实现记录、规范、部署、验证和归档**分类；本页只负责导航，不复制开发日志或测试数字。

## 最常用入口

| 目标 | 权威入口 | 说明 |
| --- | --- | --- |
| 了解项目 | [项目首页](../README.md) | 产品范围、启动与开发入口 |
| 当前架构 | [当前架构](architecture/CURRENT_ARCHITECTURE.md) | 以现行源码为基线的架构说明 |
| 当前架构状态 | [架构状态](architecture/ARCHITECTURE_STATUS.md) | 当前模块边界、正式部署证据和未关闭高风险 |
| 待处理问题 | [问题与验收缺口](KNOWN_ISSUES.md) | 集中跟踪仍未关闭的问题 |
| 发布门槛 | [发布验收](validation/RELEASE_ACCEPTANCE.md) | 发布是否通过的唯一验收入口 |
| 开发约定 | [开发规范](architecture/CRS开发规范.md) | 编码、边界和协作约束 |
| 架构整改 | [唯一整改主计划](architecture/ARCHITECTURE_REMEDIATION_PLAN.md) | ARCH 编号、整改顺序和关闭条件 |
| 安全与认证 | [安全规范](specifications/security/README.md) | 安全架构、TOTP 与 Hello 边界 |
| 税务规则 | [税务规范](specifications/tax/README.md) | 法律适用性、计算实现与证据链 |
| 使用与账户导入 | [使用指南](guides/README.md) | 券商导入、账户、保险库和隐私 |
| 后台服务部署 | [Worker升级与恢复](deployment/WORKER_UPGRADE_AND_RECOVERY.md) | 安装、升级、恢复与部署操作 |
| AI 接入方案 | [AI Gateway / MCP 双入口架构](architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) | API/MCP 双入口、套餐、网络、安全及分阶段验收（未实现） |
| 国家队研究 | [国家队模块文档](modules/national-team/README.md) | ETF、直接持股、披露证据、NAV、预警和南向研究 |
| AI / MCP 接入方案 | [AI / MCP 只读数据接入](architecture/AI_MCP_INTEGRATION.md) | ChatGPT/Claude 数据查询、授权边界、实施计划和验收标准（待实施） |

## 文档地图

| 目录 | 内容 | 文档性质 |
| --- | --- | --- |
| [architecture/](architecture/README.md) | 总体架构、插件宿主、Worker、接口、开发规范和架构决策 | 当前架构与设计约束 |
| [modules/](modules/README.md) | 按业务模块组织的需求、专项设计和模块入口 | 业务域入口 |
| [guides/](guides/README.md) | 使用、账户导入、认证、隐私和设置 | 用户操作指南 |
| [implementation/](implementation/README.md) | 模块实现边界、变更记录、数据与运行时细节 | 实施事实与进度记录 |
| [specifications/](specifications/README.md) | 税务、绩效、安全和发布等目标规格 | 需求与规范 |
| [deployment/](deployment/WORKER_UPGRADE_AND_RECOVERY.md) | 后台服务部署、升级和恢复 | 运维操作 |
| [validation/](validation/README.md) | 测试范围、发布验收和专项验证记录 | 验证证据 |
| [archive/](archive/README.md) | 已被后续设计或实现替代的历史方案、评审和验收依据 | 只供追溯，不代表当前状态 |
| [examples/](examples/README.md) | 示例与演示资料 | 参考材料 |

## 文档权威性与阅读顺序

当文档之间出现冲突时，按以下顺序判断，不以较新的文件名或单次聊天结论替代证据：

1. **当前源码、自动化测试和可复核的正式环境回执**：用于确认实际行为。
2. **[架构状态](architecture/ARCHITECTURE_STATUS.md)**：当前模块边界、部署状态和未关闭风险的汇总入口。
3. **[问题与验收缺口](KNOWN_ISSUES.md) 与 [发布验收](validation/RELEASE_ACCEPTANCE.md)**：前者跟踪待办，后者判断发布门槛；两者职责不同。
4. **implementation/**：记录具体模块的实现、部署和证据，不把历史记录自动升级为当前事实。
5. **architecture/、modules/、specifications/**：解释架构约束、业务设计和目标规格；设计不等于实现。
6. **archive/**：保留历史决策背景，不作为当前实现依据。

## 文档维护规则

- 每个主题只设一个当前权威入口；其他文档应链接到该入口，不复制维护同一份状态清单。
- 区分**设计、实现、验证、运维和历史归档**。状态至少明确“已实现”“已验证”“待验证”或“已归档”，不能把代码存在等同于业务验收通过。
- 日期后缀只用于有明确时间边界的审查、POC、验收快照；持续维护的当前文档使用稳定文件名。
- 新增内部链接优先使用相对路径；移动或重命名 Markdown 文件时，必须同步更新所有入链并检查目标文件存在。
- 不因整理而删除唯一的技术约束、反方意见、历史证据或失败记录。失效内容移入 archive，并在归档索引中说明替代入口。
- 代码、测试或正式环境状态变化时，更新对应主题的实施/验证文档和索引；不要只追加新的平行版本。
