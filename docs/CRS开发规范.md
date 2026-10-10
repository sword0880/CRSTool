# CRS C# 开发与迁移规范

## 1. 产品与支持范围

WPF 是唯一前台。支持 IBKR Activity Flex XML 和当前富途 XLSX／文本 PDF 布局。复杂证券事件、旧结转迁移、严格历史复算及完整抵免证据继续按 AR 清单推进。当前支持范围见 PRD 和券商指南。

## 2. 技术基线

.NET 10；WPF 目标 net10.0-windows10.0.19041.0。WPF UI 4.3.x、CommunityToolkit.Mvvm、DataGridExtensions、LiveChartsCore.SkiaSharpView.WPF。后台使用 NPOI、PdfPig、SQLite + Dapper、NLog；版本由根 Directory.Packages.props 管理，并保留包锁文件。

## 3. 分层与实现约束

Domain 不引用外层；Application 只引用 Domain；Infrastructure 实现应用端口。WPF 的 Composition 可引用具体实现，Views／ViewModels／Controls 只调用应用用例。完整约定见 PROJECT_STRUCTURE。

XAML 负责布局与样式，ViewModel 管理可观察展示状态及命令。code-behind 只承载视图行为，不解析文件或计算税额。不保留 Designer.cs 或 AntdUI 规则。

## 4. 金额、身份与来源

使用 decimal、明确成本口径、完整 SecurityKey 和可空未知值。原始成交总额与净现金保留，不能通过单价重算覆盖原事实；含费成本不双扣。文件与记录来源、汇率来源及规则版本进入底稿。

同一记录冲突不能静默合并；富途缺稳定 ID 时拒绝重叠年度主报告。持仓市价不当历史成本，未知事件不自动转换。详见 FIFO 和字段约定。

## 5. IBKR 输入

活动 XML、期初 LOT、范围确认、现金／收益／数量核对及安全大小限制见 IBKR_IMPORT_GUIDE。Flex 令牌仅当前窗口保留。CSV、PDF、1042-S 等不是已实现解析入口。

## 6. 富途输入

NPOI 读取交易及收入 XLSX，PdfPig 读取已支持的文本收入 PDF。年度收入主表优先，资金派息不重复计税；真实成本、缺费用、期初和账户绑定各有门槛。详见 FUTU_CSHARP_IMPORT_GUIDE 和 FUTU_FIELD_MAPPING。

## 7. 结果与抵免边界

四维状态为 CalculationStatus、DataCompleteness、ReconciliationStatus、UsageLabel，页面和导出沿用后台判定。抵免资格证据尚未形成完整门槛，不能将当前辅助算法当成法规核验结论。

## 8. 历史与结转

默认存储结果与笔数，SessionTrades 不持久化。完整快照由用户选择。历史页面只恢复冻结结果；后台 Replay 使用历史汇率、指纹及临时状态，校验规则／算法标识。结转确认、同秒排序和完整输出一致性仍待完成。

旧结转需要显式版本转换及验收；不能把同名版本当成兼容证明。数据库与日志位置由本机设置管理；默认使用 CRS/data，不自动识别旧版本目录。保存后下次启动生效，不自动搬移数据库。

## 9. 验收契约

构建、架构检查、金额检查及隔离窗口流程必须与改动相应执行。检查数量不能替代真实全年、抵免证据、覆盖率或性能验收。未完成的契约集中维护在 KNOWN_ISSUES.md。

## 10. WPF 交互

导航与页面职责分离，共享忙碌门槛；输入变更清除旧结果、图表和导出资格。后台取消后提交前再次检查；前台只格式化金额。历史交易缺完整快照时提示重新导入，不虚构明细。人工依据不自动解除问题。

## 11. 开发入口

在根目录打开 CRS.sln，启动 CRS.Desktop.Wpf。命令、验证项目及窗口检查见 [tests/README](../tests/README.md)。当前源代码路径和未完成项见 [文档索引](README.md)。

执行程序集封存、规范化契约、来源一次读取、历史分页和真实业务签认继续维护在 [问题清单](KNOWN_ISSUES.md)。
