# 小财牛文档中心

这里是 CRSTool（小财牛）的统一文档入口。文档按架构、业务模块、使用指南、实现、规范、部署、验证和归档分类；本页只负责导航，不复制开发日志或测试流水。

## 当前最重要的入口

| 目标 | 唯一权威入口 | 职责 |
| --- | --- | --- |
| 当前代码架构 | [当前架构](architecture/CURRENT_ARCHITECTURE.md) | 项目分层、进程、依赖与数据所有权 |
| 当前实现/部署状态 | [架构状态](architecture/ARCHITECTURE_STATUS.md) | 最近合并、已验证能力、正式部署基线和证据时点 |
| 未关闭工作 | [问题清单](KNOWN_ISSUES.md) | 当前缺口、优先级、验收条件和证据入口 |
| 是否允许发布 | [发布验收](validation/RELEASE_ACCEPTANCE.md) | ReleaseApproved 与 No-Go/Go 判断 |
| 架构整改 | [唯一整改主计划](architecture/ARCHITECTURE_REMEDIATION_PLAN.md) | ARCH 编号、整改顺序和关闭条件 |
| AI 目标架构 | [AI Gateway / MCP 架构](architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) | 双入口、安全域、外部连接与目标边界 |
| AI 当前实现 | [AI 双入口与附件实现](implementation/AI_DUAL_ENTRY_POC.md) | 当前工具映射、Gateway、MCP、模型适配、附件与测试 |
| 国家队研究 | [国家队模块文档](modules/national-team/README.md) | ETF、直接持股、披露证据、NAV、市场研究与南向 |

## 文档地图

| 目录 | 内容 | 文档性质 |
| --- | --- | --- |
| [architecture/](architecture/README.md) | 总体架构、插件宿主、Worker、接口和开发规范 | 当前结构与设计约束 |
| [modules/](modules/README.md) | 业务模块需求、专项设计和入口 | 业务域入口 |
| [guides/](guides/README.md) | 使用、账户导入、认证、隐私和设置 | 用户操作指南 |
| [implementation/](implementation/README.md) | 模块实现边界、运行时和验证事实 | 实施记录 |
| [specifications/](specifications/README.md) | 税务、绩效、安全和发布规格 | 需求与规范 |
| [deployment/](deployment/WORKER_UPGRADE_AND_RECOVERY.md) | 后台服务部署、升级和恢复 | 运维操作 |
| [validation/](validation/README.md) | 测试范围、发布验收和专项验证记录 | 验证证据 |
| [archive/](archive/README.md) | 被替代的方案、评审和历史验收依据 | 只供追溯 |
| [examples/](examples/README.md) | 示例与演示资料 | 参考材料 |

## 文档权威性

发生冲突时，按以下顺序判断：

1. 当前源码、自动化测试和可复核的正式环境回执。
2. [架构状态](architecture/ARCHITECTURE_STATUS.md)：当前实现和部署证据的汇总。
3. [问题清单](KNOWN_ISSUES.md) 与 [发布验收](validation/RELEASE_ACCEPTANCE.md)：分别管理待办和发布决定。
4. implementation：具体实现、部署和历史证据。
5. architecture、modules、specifications：解释设计约束和目标，不代表已经实现。
6. archive：保留历史决策，不作为当前事实来源。

## 维护规则

- 每个主题只维护一份当前权威文档；其他入口只链接，不复制同一份状态。
- 明确区分已实现、自动化已验证、正式部署已验证、业务验收已完成。
- 当前状态文件只保留当前事实和最后确认时点；日期化过程记录放在专项实施/验证文档或归档。
- 合并文档时保留关键约束、失败记录和历史证据；对外链接需要兼容时使用短说明页指向新入口。
- 移动或重命名 Markdown 文件时检查所有相对链接与锚点。不得把单次 CI 通过写成完整发布通过。

[返回项目首页](../README.md)
