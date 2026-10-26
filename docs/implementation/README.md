# 实施文档

本目录包含**当前实现说明和有日期的专项工作记录**。先看[当前架构](../architecture/CURRENT_ARCHITECTURE.md)，再查需要的专题；历史“已通过”只代表原记录当时的测试，不等于当前发布验收。

## 常用实现规范

- [全局品牌主题](BRAND_VISUAL_SYSTEM.md)
- [FIFO 核算](CAPITAL_GAIN_FIFO_SPEC.md) · [金额显示](AMOUNT_PRESENTATION.md) · [富途字段](FUTU_FIELD_MAPPING.md)
- [数据库升级](DATABASE_UPGRADE.md) · [备份容量](BACKUP_CAPACITY.md) · [锁定中断](LOCK_INTERRUPTION.md)
- [计划任务](SCHEDULED_TASKS.md) · [后台服务](BACKGROUND_SERVICE.md)
- [ETF Worker 迁移](ETF_WORKER_MIGRATION.md) · [国家队分层重构](NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)
- [行业估值反方审查与后续来源门槛](NATIONAL_TEAM_INDUSTRY_UNDERVALUE_REVIEW_20261026.md) · [行业基本面证据闸门与反方测试](NATIONAL_TEAM_INDUSTRY_QUALITY_GATES_20261026.md) · [国家队插件功能分类](../architecture/NATIONAL_TEAM_FEATURE_CLASSIFICATION_20261026.md)
- [投资绩效](PERFORMANCE_P0.md)

## 整改与历史记录

- [2026-10-20 架构整改续记](ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md)
- [2026-10-16 整改记录](ARCH_REMEDIATION_20261016.md)
- [旧品牌变更记录](PRODUCT_BRAND_20261020.md)
- [过往设置导航实现](SETTINGS_NAVIGATION_20261019.md) · [插件导航调整](PLUGIN_NAVIGATION_UI.md)

其余细分专题按文件名在此目录查找，不重复维护第二份完整文件列表。进行发布判断，请查看 [待办](../KNOWN_ISSUES.md) 和 [发布验收](../validation/RELEASE_ACCEPTANCE.md)。

[返回文档首页](../README.md)

- [NationalTeamWatch分层重构续项](NATIONAL_TEAM_MODULAR_REFACTOR_20261026.md)：独立持股/预警模块、按功能端口、公共正文读取与旧接口退役。
