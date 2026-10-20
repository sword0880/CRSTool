# 小财牛文档导航

这里是**入口索引**，不再逐一复制每轮开发日志。源码和实测结果优先于历史描述；未完成项看 [KNOWN_ISSUES](KNOWN_ISSUES.md)，发布门槛看 [发布验收](validation/RELEASE_ACCEPTANCE.md)。

## 最常用

| 任务 | 从这里开始 |
| --- | --- |
| 运行和使用 | [项目首页](../README.md) · [使用指南](guides/README.md) |
| 了解当前架构 | [当前架构](architecture/CURRENT_ARCHITECTURE.md) · [项目目录](architecture/PROJECT_STRUCTURE.md) |
| 继续专项整改 | [架构整改计划](architecture/ARCHITECTURE_REMEDIATION_PLAN_20261020.md) · [整改续记](implementation/ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md) |
| 插件与后台 | [插件架构](CRS_PLUGIN_ARCHITECTURE.md) · [后台设计](CRS_BACKGROUND_SERVICE_DESIGN.md) |
| 国家队监控 | [ETF 一期设计](NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md) · [实施进展](implementation/NATIONAL_TEAM_PLUGIN_PROGRESS.md) |
| 统一界面风格 | [全局品牌与控件样式](implementation/BRAND_VISUAL_SYSTEM.md) |
| 测试及发布 | [测试命令](../tests/README.md) · [发布验收](validation/RELEASE_ACCEPTANCE.md) |

## 文档目录

- [guides](guides/README.md)：操作、导入、认证与设置。
- [architecture](architecture/README.md)：当前分层、依赖、架构计划。
- [implementation](implementation/README.md)：实现边界与专项技术记录。
- [specifications](specifications/README.md)：需求和设计目标，**不等于已实现**。
- [validation](validation/README.md)：测试范围、真实资料与验收结果。
- [archive](archive/README.md)：旧方案、历史评审、历史验收依据。
- [examples](examples/README.md)：模板与脱敏示例。

## 维护规则

1. 根目录 `README.md` 只保留启动与入口，详细功能放对应目录；每一主题只维护一个**当前**入口。
2. 有日期的评审、验证与实施记录属于历史事实，不复制到多个目录首页；旧材料保留供追溯，但不作为当前状态。
3. 状态以 `KNOWN_ISSUES.md` 和 `validation/RELEASE_ACCEPTANCE.md` 为主，不在文档导航重复写测试通过数字。
4. Visual Studio 解决方案仅展示日常常用文档；**不在解决方案中显示，不代表文件被删除**。完整资料直接查看 `docs/` 文件夹。
5. 修改文件名、删除文件或变更引用时，核验 Markdown 相对链接、`CRS.sln` 与相关测试脚本。
