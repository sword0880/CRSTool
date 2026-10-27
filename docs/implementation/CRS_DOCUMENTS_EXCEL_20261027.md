# Excel通用机制归入CRS.Documents

日期：2026-10-27。此前只沿ETF报告工作迁移了PDF，Excel读取和工作簿生成仍混在Infrastructure，确实未完成文档层收拢。

## 实现边界

新增`Excel/ExcelReader`、`ExcelRows`和`ExcelWriter`。读取提供工作表名称、物理行号、列位置、类型、原始文字与显示文字；支持XLS/XLSX，不执行公式、宏或外部链接。上限、取消及第三方建模限制见[公共入口说明](../../src/CRS.Documents/README.md)。

富途读取复用共同机制；字段、重复/缺列、公式/错误拒绝和金额/年度/币种规则仍在业务层。税务`ExcelReports`、绩效`PerformanceFiles`保留各自报表内容，改用公共生成器。原金额格式、整数类型、表头、列宽、冻结行及空值/零语义保留。NPOI及已有SkiaSharp/Xml运行依赖由Documents拥有。

Documents不引用业务、数据库、安全会话或WPF。敏感导出同意、锁定、原子提交、加密容器和内存清理仍由基础设施协调。国家队仍普通SQLite，收入/报税仍SQLCipher。

## 本地验证

- 迁移前程序集及摘要保留在`artifacts/crs-documents-excel-baseline-20261027/`，未覆盖基线。
- `--compare-excel-before`31项：实际XLS/XLSX、原值精度/显示舍入、日期到秒、稀疏行/空值、公式/错误、文字等号、样式、取消及限额。两份合成税务底稿与旧构建逐单元格值、类型、格式及样式一致。
- `--compare-futu-tax-before`19项：三组Excel/PDF/交易输入与旧构建逐业务字段一致，另验证隔离SQLCipher历史、重放和备份恢复。
- Infrastructure完整默认回归退出0，包含绩效、敏感导出、取消/锁定、加密恢复、结构升级及资源边界。
- 38项目Release构建0警告0错误；架构/功能目录门禁及NationalTeam完整回归退出0。日志前缀`artifacts/crs-documents-`。
- 新独立候选`crs-documents-header-worker-20261027`完整发行182份文件与索引逐字节匹配；真实Worker隔离48项、并发11场景、保存公开PDF的Runner候选管道14项通过，后台不加载WPF或私人税务程序集。开发桌面包五个工作台页面创建与布局通过。

合成一致性不代表独立税务签认。未升级已安装Worker、转换真实数据或发送通知；CI检查已登记，远端执行另验。
