# 系统架构

## 四个生产项目

Domain 保存交易事实、FIFO 与税额／状态规则，不引用外层。Application 仅引用 Domain，编排用例并声明端口。Infrastructure 实现解析、SQLite、日志、设置、配置、Flex 和输出。Desktop.Wpf 负责展示、交互及启动装配。

Views／ViewModels／Controls 不能访问具体基础设施；Composition 可以创建适配器。完整目录见 [项目结构](PROJECT_STRUCTURE.md)。

## 执行与状态

DesktopWorkflow 执行后台计算、保存和导出；AnnualInputPreparation 检查期初与报告；AnnualReconciliation 核对持仓、收益与现金。CalculationService 封装结果，FifoEngine 和 TaxEngine 完成金额运算。

WorkspaceState 管理跨页面忙碌状态和结果失效。富途收入与交易选择分别存于 ImportViewModel，提交时形成同一个请求。后台运行时不可改输入。历史页只恢复冻结结果。

## 本机设置

IDesktopSettingsService 由 DesktopSettingsStore 实现，持久化、路径校验和原子写入在 Infrastructure。SettingsViewModel 只收集选项。启动组合根读取设置后创建 LocalStore 并配置 NLog；保存后下次启动使用新目录，当前数据库不移动。

SQLite + Dapper，user_version=1；NPOI 处理 Excel，PdfPig 处理已支持文本 PDF，NLog 输出脱敏日志。设置及默认路径见 [设置说明](SETTINGS.md)。

## 重放与限制

ReplayExecutionContext 校验历史口径，禁止缺失冻结汇率时回退当前配置，保留原临时状态和指纹。TimeProvider 支持跨年验证。严格执行版本、结转语义和完整输出一致性尚未闭环，见 [当前问题](KNOWN_ISSUES.md)。

只在用户选择 Flex 下载时执行该联网用例；文件计算本地执行。界面测试使用隔离数据库。虚拟化和卖出索引不等于已完成真实性能基准。
