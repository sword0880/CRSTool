# 项目文档索引

更新日期：2026-10-10。当前唯一前台为 WPF；Python 与 WinForms 项目已删除。下列当前说明依据源码修订，历史记录保留当时证据，不作为现有功能承诺。

## 使用与实现

- [启动说明](../README.md)
- [产品范围](PRD.md)
- [系统架构](SAD.md) · [目录与依赖](PROJECT_STRUCTURE.md)
- [应用接口](API_SPEC.md)
- [C# 开发规范](CRS_CSharp_项目迁移与开发规范_V1.1.md)
- [FIFO 实现约定](CAPITAL_GAIN_FIFO_SPEC.md) · [税额算法与边界](TAX_ENGINE_SPEC.md)
- [测试与验收](TEST_SPEC.md) · [执行命令](../tests/README.md)

## 券商与配置

- [IBKR 导入指南](IBKR_IMPORT_GUIDE.md)
- [IBKR Flex 下载](IBKR_FLEX_AUTO_DOWNLOAD.md)
- [富途字段映射](FUTU_FIELD_MAPPING.md)
- [富途导入与验收](FUTU_CSHARP_IMPORT_GUIDE.md)
- [汇率来源](EXCHANGE_RATE_SOURCES.md)：记录配置截止期间，不是自动更新服务

## 评审与进度

- [本次文档复审](文档复审_20261010.md)：逐文档处理与仍存问题
- [WPF 迁移评估](WPF迁移与项目分层评估.md)
- [架构优化记录](架构优化记录_20261010.md)
- [反方复审与 AR 清单](反方架构复审与开发建议_20261009.md)：问题原证据和后续更新
- [V1.1 整改记录](V1.1整改记录.md)：有日期的历史实施记录
- [项目优化与 IBKR 历史规划](项目优化建议与IBKR接入规划.md)：Python 阶段记录
- [原始设计归档](archive/README.md)：保留修订前需求、规则、示例和目标契约

当前实现说明优先用于定位源码和运行程序；未完成的业务／版本化验收要求继续依据 AR 清单与原 V1.1 目标推进。旧文件中的税务表述、测试数和性能目标不表示本次已重新核验法规或达标。
