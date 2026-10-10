# 项目文档

本文档集只保留当前 WPF 版本的使用、实现和验收说明。旧 Python／WinForms 设计、重复迁移记录和原始归档已移出 docs；未解决的问题集中维护，避免丢失待办。

## 使用

- [启动说明](../README.md)
- [产品与页面](PRD.md)
- [日志与数据库设置](SETTINGS.md)
- [IBKR 导入](IBKR_IMPORT_GUIDE.md) · [Flex 下载](IBKR_FLEX_AUTO_DOWNLOAD.md)
- [富途导入](FUTU_CSHARP_IMPORT_GUIDE.md)
- [汇率来源与截止期间](EXCHANGE_RATE_SOURCES.md)

## 开发与验证

- [系统架构](SAD.md) · [项目结构](PROJECT_STRUCTURE.md)
- [应用接口](API_SPEC.md) · [开发约定](CRS开发规范.md)
- [FIFO 实现](CAPITAL_GAIN_FIFO_SPEC.md) · [税额算法](TAX_ENGINE_SPEC.md)
- [富途字段映射](FUTU_FIELD_MAPPING.md)
- [测试规范](TEST_SPEC.md) · [验证命令](../tests/README.md)
- [当前问题与验收缺口](KNOWN_ISSUES.md)

规范描述现有辅助算法和产品边界，不把未完成业务签认、覆盖率或性能目标写成已实现。代码变化时同步更新对应说明及问题清单。
