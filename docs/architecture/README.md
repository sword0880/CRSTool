# 架构与开发约定

**当前架构的唯一入口**：[CURRENT_ARCHITECTURE.md](CURRENT_ARCHITECTURE.md)。当前模块状态、部署证据和未关闭高风险见 [ARCHITECTURE_STATUS.md](ARCHITECTURE_STATUS.md)。

## 总体架构

- [当前架构](CURRENT_ARCHITECTURE.md) — 以现行源码为准的模块与运行边界
- [架构状态](ARCHITECTURE_STATUS.md) — 当前部署、证据和开放风险
- [项目目录](PROJECT_STRUCTURE.md) — Solution、项目和源码布局
- [插件化总体架构](PLUGIN_ARCHITECTURE.md) — UI/Worker 双宿主、插件契约与生命周期
- [后台任务服务设计](BACKGROUND_SERVICE_DESIGN.md) — Windows Service、Quartz.NET、IPC 和恢复边界
- [AI Gateway / MCP 双入口架构](AI_GATEWAY_MCP_ARCHITECTURE.md) — 目标架构、套餐、网络与隐私边界；当前实现状态见实施文档
- [AI / MCP 只读数据接入方案](AI_MCP_INTEGRATION.md) — 原始目标设计、工具权限、证据和远程接入门槛
- [AI 双入口实现记录](../implementation/AI_DUAL_ENTRY_POC.md) — 当前代码结构、本机 Gateway/MCP/Responses API 与验证证据
- [AI 附件处理](../implementation/AI_ATTACHMENTS.md) — WPF/MCP 统一附件解析、内存会话与清理规则

## 设计约束与专项决策

- [开发规范](CRS开发规范.md)
- [应用接口](API_SPEC.md)
- [数据库提供方边界](DATABASE_PORTABILITY.md)
- [共享来源预算](SHARED_SOURCE_BUDGETS.md)
- [后台插件进程隔离](WORKER_PLUGIN_PROCESS_ISOLATION.md)
- [Worker服务密钥恢复](WORKER_SERVICE_KEY_RECOVERY.md)
- [国家队功能分类与增长治理](NATIONAL_TEAM_FEATURE_CLASSIFICATION_20261026.md)
- [国家队市场周期研究需求](NATIONAL_TEAM_MARKET_REGIME_REQUIREMENTS_20261024.md)
- [架构整改计划（唯一主计划）](ARCHITECTURE_REMEDIATION_PLAN.md)
- [当前未关闭问题](../KNOWN_ISSUES.md)

PRD 与 SAD 属于产品和系统设计背景；当前整改任务只维护在 [唯一主计划](ARCHITECTURE_REMEDIATION_PLAN.md)，原日期化计划已移入 [历史归档](../archive/architecture/README.md)。安全、税务和计算证据规范按领域维护在 [规范目录](../specifications/README.md)。它们与当前源码或 [ARCHITECTURE_STATUS.md](ARCHITECTURE_STATUS.md) 冲突时，不得直接作为当前事实。专项验收证据见 [验证目录](../validation/README.md)。

[返回文档首页](../README.md)
