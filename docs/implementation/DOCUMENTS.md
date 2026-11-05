# 公共文档读取与生成

`CRS.Documents`拥有通用PDF、Excel和密码文档机制，不引用业务、数据库、安全会话或WPF。公共类型、资源上限及第三方库限制以[模块说明](../../src/CRS.Documents/README.md)为准；业务解析器只消费文档结构，保留各自的字段、金额和证据规则。

## PDF与Excel边界

PDF读取提供页码和文本等通用信息，不根据文字直接决定证券身份、持仓性质或审核结论。国家队公开报告的固定模板、页码、原件摘要与具名证据由业务模块校验，见[披露证据](NATIONAL_TEAM_EVIDENCE.md)。税务原件的加密与授权仍遵守原有入口。

`ExcelReader / ExcelRows / ExcelWriter`提供工作表名称、物理行号、列位置、类型、原始与显示文字，支持XLS/XLSX，不执行公式、宏或外部链接。富途读取复用通用机制，字段、重复/缺列、公式/错误拒绝、金额/年度/币种仍在业务层。

税务`ExcelReports`和绩效`PerformanceFiles`拥有各自内容，通过公共生成器生成工作簿；金额格式、整数类型、表头、列宽、冻结行以及空值与零的语义保留。NPOI及相关SkiaSharp/Xml运行依赖由Documents拥有。

敏感导出确认、锁定取消、原子提交、加密容器及内存清理由基础设施协调。公共文档机制不能自行决定明文导出或数据存储策略；国家队普通SQLite与收入/报税SQLCipher仍各自独立。

## 验证入口与证据

2026-10-27迁移保留了旧程序集基线`artifacts/crs-documents-excel-baseline-20261027/`，用`--compare-excel-before`比较真实XLS/XLSX结构、精度与显示、日期、稀疏行、空值、公式/错误、样式、取消及限额；`--compare-futu-tax-before`逐字段比较合成券商输入及隔离历史、重放、备份恢复。命令参数见[测试说明](../../tests/README.md)。

当轮另验证完整Infrastructure回归、项目分层与功能目录、完整Worker候选及Runner公开PDF读取；日志前缀`artifacts/crs-documents-`。这些记录证明当轮合成一致性，不代表独立真实税务签认、已安装Worker升级或正式发布验收。
