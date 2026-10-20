# 小财牛 · 文档导航

**财富有数，投资有度**。产品中文名统一为“小财牛”，工程标识为 CRS。

从这里进入当前文档。操作说明、现有实现、设计目标和历史记录分别维护；**未完成项目以 [当前待办](KNOWN_ISSUES.md) 为准，发布状态以 [发布验收](validation/RELEASE_ACCEPTANCE.md) 为准**。

Visual Studio 打开或重新加载 CRS.sln 后，在解决方案资源管理器的 docs 节点中可按相同目录阅读文档与示例。

## 常用入口

- 产品名称与口号：[小财牛品牌同步记录](implementation/PRODUCT_BRAND_20261020.md)。

- 开始使用：[启动](../README.md)、[IBKR 导入](guides/IBKR_IMPORT_GUIDE.md)、[富途导入](guides/FUTU_CSHARP_IMPORT_GUIDE.md)。
- 保护与恢复数据：[保险库](guides/VAULT_GUIDE.md)、[手机验证](guides/AUTH_IMPLEMENTATION.md)、[设置](guides/SETTINGS.md)、[加密底稿](guides/ENCRYPTED_EXPORT.md)。
- 核对资料：[账户期间](guides/ACCOUNT_PERIODS.md)、[汇率来源](guides/EXCHANGE_RATE_SOURCES.md)、[抵免复核](guides/FOREIGN_CREDIT_REVIEW.md)。
- 继续开发：[当前架构入口](architecture/CURRENT_ARCHITECTURE.md)、[项目结构](architecture/PROJECT_STRUCTURE.md)、[应用接口](architecture/API_SPEC.md)、[当前待办](KNOWN_ISSUES.md)。
- 插件开发：[宿主规范](CRS_PLUGIN_ARCHITECTURE.md)、[ETF一期设计](NATIONAL_TEAM_ETF_MONITOR_PHASE1_DESIGN.md)、[联网预览实施记录](implementation/NATIONAL_TEAM_PLUGIN_PROGRESS.md)、[数据通道POC](DATA_SOURCE_POC.md)。
- 主程序功能：[计划任务与执行记录](implementation/SCHEDULED_TASKS.md)。
- 独立后台：[Windows Service 设计](CRS_BACKGROUND_SERVICE_DESIGN.md)、[心跳 POC 实施与安装](implementation/BACKGROUND_SERVICE.md)。
- 架构整改：[2026-10-16实施顺序与状态](implementation/ARCH_REMEDIATION_20261016.md)、[Worker升级与恢复边界（部分实现）](deployment/WORKER_UPGRADE_AND_RECOVERY.md)。
- 查看验证：[验证范围](validation/TEST_SPEC.md)、[真实样本](validation/REAL_SAMPLE_PROGRESS.md)、[P0 修复记录](validation/P0_FIX_PROGRESS.md)、[运行命令](../tests/README.md)。

## 目录

| 目录 | 内容 | 阅读用途 |
| --- | --- | --- |
| [guides](guides/README.md) | 操作指南、资料要求与设置 | 使用产品 |
| [architecture](architecture/README.md) | 产品、分层、接口与开发约定 | 理解项目 |
| [implementation](implementation/README.md) | 当前算法、格式、限额与保护机制 | 核对现有行为 |
| [specifications](specifications/README.md) | 安全、证据、税务验证与发布目标 | 查设计要求，不代表全部已实现 |
| [validation](validation/README.md) | 测试范围、发布门槛、真实样本与 P0 记录 | 查验收证据 |
| [archive](archive/README.md) | 旧交付、审查原状与历史测量 | 追溯历史 |
| [examples](examples/README.md) | 可复制的输入清单示例 | 准备资料，必须替换占位值 |

## 维护约定

1. 新文档放入对应目录，并更新该目录导航；根目录只保留本导航和当前待办。
2. 使用指南说明操作；实现文档说明当前代码；设计规范写目标；验收文档注明日期、产物及未通过项。
3. 未完成项集中更新 KNOWN_ISSUES；发布门槛集中更新 RELEASE_ACCEPTANCE，避免在各文档复制进度表。
4. 旧轮次测量和交付记录归档，不把历史覆盖率、合成测试或技术往返一致当作最新业务签认。
5. 文件名尽量稳定，新增或移动时更新 Markdown 链接、目录索引及 CRS.sln 的解决方案文件条目；文中的 src、tests、Sample、artifacts 路径和运行命令均以仓库根目录为基准。

- 投资绩效：[P0 操作与算法](implementation/PERFORMANCE_P0.md)、[离线模板](examples/performance.example.json)、[验收记录](validation/PERFORMANCE_ACCEPTANCE.md)。
