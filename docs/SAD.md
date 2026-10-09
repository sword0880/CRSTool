# 系统架构说明

更新日期：2026-10-10。依据当前源码；[原始 Python 架构](archive/SAD.md)仅作历史资料。

## 项目与依赖

| 项目 | 职责 | 引用 |
| --- | --- | --- |
| CRS.Domain | 交易事实、FIFO 成本、汇率事实、税额与状态规则 | 不引用外层或第三方包 |
| CRS.Application | 导入与计算编排、复核、历史、报表投影、端口 | Domain |
| CRS.Infrastructure | XML／XLSX／PDF、SQLite、配置、日志、Flex、Excel | Application、Domain |
| CRS.Desktop.Wpf | Views、ViewModels、交互服务、启动装配 | Application；Composition 装配 Infrastructure |

生产项目只有四个。完整目录见 [分层约定](PROJECT_STRUCTURE.md)。

Infrastructure 实现 Application 的接口，Domain 不访问文件、网络或数据库。WPF 页面调用 IDesktopUseCases；具体实现由 Composition 创建，不能从 ViewModel 取得仓储、解析器或输出适配器。

## 执行流程

DesktopWorkflow 负责后台任务和计算后保存；AnnualInputPreparation 检查年度来源与期初；FifoEngine 和 TaxEngine 计算；AnnualReconciliation 核对券商事实；CalculationService 封装结果与快照。ReviewUseCases 校验人工依据。导出使用冻结结果，不在 UI 重算金额。

共享 WorkspaceState 管理忙碌门槛与结果失效。Views 负责绑定、导航、列展示与用户交互，业务运算不进入 code-behind。历史任务恢复冻结结果；Trades 只在会话或完整快照中存在。

## 存储与配置

- SQLite + Dapper，数据库 user_version=1；位置 %LOCALAPPDATA%/CRS.WinForms/crs.db。旧目录名是兼容路径，不代表 WinForms 项目仍存在。
- 根 config 在构建时复制到 WPF 输出目录。金额、来源、配置指纹随结果记录。
- NPOI 读写 Excel，PdfPig 解析支持的文本 PDF；NLog 记录脱敏操作。
- Flex 下载是显式联网用例，其余本地文件计算不要求联网。令牌不持久化。

## 历史重放与可靠性边界

ReplayExecutionContext 校验历史年度、规则与算法标识，仅使用冻结汇率和配置指纹，保留原临时状态。缺数据时拒绝回退当前配置。TimeProvider 允许独立验证跨年状态。

当前仍没有执行程序集封存、完整输出一致性证明和结转／同秒排序闭环。历史页尚无重放入口。模型集合仍可变、历史数据结构版本演进仍需专门设计。

## 验证与性能

现有控制台检查及实际 WPF 窗口检查见 [验证说明](../tests/README.md)。卖出对账按证券身份与卖出编号索引匹配记录，DataGrid 启用虚拟化；尚无真实账户的耗时、内存或覆盖率达标报告。
