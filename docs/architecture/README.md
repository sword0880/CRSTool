# 架构与开发约定

- [当前架构](CURRENT_ARCHITECTURE.md)：源码项目、依赖方向、进程边界和数据所有权。
- [架构状态](ARCHITECTURE_STATUS.md)：当前实现、最近合并、部署证据和未关闭风险。
- [项目目录](PROJECT_STRUCTURE.md)：Solution、项目和源码布局。
- [插件化总体架构](PLUGIN_ARCHITECTURE.md)：WPF/Worker 双宿主、插件契约、生命周期和故障边界。
- [后台任务服务设计](BACKGROUND_SERVICE_DESIGN.md)：Windows Service、Quartz.NET、IPC 和恢复边界。
- [AI Gateway / MCP 双入口架构](AI_GATEWAY_MCP_ARCHITECTURE.md)：当前 AI 项目结构、目标连接方式、安全和发布门槛。
- [AI 双入口与附件实现](../implementation/AI_DUAL_ENTRY_POC.md)：真实工具映射、Gateway、MCP、Responses API、附件和测试。

## 设计约束与专项决策

- [开发规范](CRS开发规范.md)
- [应用接口](API_SPEC.md)
- [数据库提供方边界](DATABASE_PORTABILITY.md)
- [共享来源预算](SHARED_SOURCE_BUDGETS.md)
- [后台插件进程隔离](WORKER_PLUGIN_PROCESS_ISOLATION.md)
- [Worker 服务密钥恢复](WORKER_SERVICE_KEY_RECOVERY.md)
- [国家队功能分类与增长治理](NATIONAL_TEAM_FEATURE_CLASSIFICATION_20261026.md)
- [国家队市场周期研究需求](NATIONAL_TEAM_MARKET_REGIME_REQUIREMENTS_20261024.md)
- [架构整改计划（唯一主计划）](ARCHITECTURE_REMEDIATION_PLAN.md)
- [当前未关闭问题](../KNOWN_ISSUES.md)
- [发布验收](../validation/RELEASE_ACCEPTANCE.md)

PRD 与 SAD 是产品和系统设计背景；整改任务定义以唯一主计划为准，完成状态以问题清单、架构状态和可复核证据为准。安全、税务和计算证据规范见[规范目录](../specifications/README.md)。专项验收见[验证目录](../validation/README.md)。

[返回文档首页](../README.md)
