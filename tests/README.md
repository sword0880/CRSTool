# CRS 验证项目

敏感导出专项：`dotnet run --project tests/CRS.Tests.Infrastructure -c Release -- --sensitive-exports-only`。应用与界面回归同时覆盖本次明文声明、恢复确认及锁定取消；生产界面不开放临时目录明文特许，合成验收明确授权其隔离临时目的地。说明见 [敏感导出](../docs/implementation/SENSITIVE_EXPORT.md)。

2026-10-13 P0 续修：应用 `AccountPeriodChecks`、基础设施 `AccountPeriodFileChecks` 与 `LockInterruptionChecks`、界面 `ImportInteractionChecks` 增加有凭证的生命周期、快照重放、严格凭证清单和长备份锁定回归。中断可单独执行 `dotnet run --project tests/CRS.Tests.Infrastructure -c Release -- --lock-interruption-only`。独立税额记录未提供，人工金额验收仍未通过。

最新整体验收：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory Sample/FUTU -IbkrRealDirectory Sample/IBKR -OutputDirectory artifacts/resource-protection-acceptance`。资源专项单独入口：`dotnet run --project tests/CRS.Tests.Infrastructure -c Release -- --resource-limits-only`。构造超过 128 MiB 的隔离 SQLCipher 库验证分块恢复；不读取或修改用户现有数据库。此前结构升级和金额展示回归继续执行，以下旧产物路径保留。

当前整体验收：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory Sample/FUTU -IbkrRealDirectory Sample/IBKR -OutputDirectory artifacts/schema-protection-acceptance`。`SchemaUpgradeChecks` 覆盖升级前加密备份、失败回滚、恢复与程序集归档；`TaxPresentationChecks`、`TaxAmountViewChecks`、`ExportChecks` 覆盖金额展示。单独运行 IBKR 技术验证：`dotnet run --project tests/CRS.Tests.Verification -c Release -- --verify-ibkr-real Sample/IBKR src/CRS.Desktop.Wpf/config artifacts/ibkr-real-acceptance`。报告仅含期间、计数、问题代码及原件摘要；独立金额签认仍待完成。

加密底稿与新目录恢复专项：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory sample/FUTU -OutputDirectory artifacts/encrypted-export-acceptance`。独立密码机制、容器与实际锁定检查、前台密码清理分别在 Security／Infrastructure／Wpf 项目；原加密备份用例仍执行以确认用途隔离兼容。

冻结专项入口：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory sample/FUTU -OutputDirectory artifacts/frozen-evidence-acceptance`。真实私有原件仅读取，报告不含身份文件名或交易金额；该技术检查不替代 `-RealManifest` 的独立金额验收。IBKR 真实样本现已加入单独技术验证。冻结与证据测试见 `CRS.Tests.Application/FrozenEvidenceChecks.cs`。

新增 CRS.Tests.Security 的密码学与 RFC6238 向量检查，以及 Infrastructure 的 SQLCipher/MFA/恢复/并发防重放专项。认证专项入口：`./tests/Run-ReleaseAcceptance.ps1 -Smoke -OutputDirectory artifacts/auth-acceptance`。WPF 模型与窗口冒烟使用独立日志，避免 Windows 大小写文件名覆盖；人工手机及跨电脑验收见 docs/guides/AUTH_IMPLEMENTATION.md。

分层验证保持独立控制台入口，运行时失败会返回非零退出码；没有引入仅用于改目录的测试框架替换。

| 项目 | 验证内容 |
| --- | --- |
| CRS.Tests.Domain | FIFO 含费成本守恒、稳定排序与同秒歧义隔离、税额基线、领域程序集独立性 |
| CRS.Tests.Application | 取消不保存、冻结历史导出、结转门槛、复核、依赖；年度覆盖、账户对账；历史汇率与指纹隔离、无效重放输入、执行口径、跨年状态与元数据门槛 |
| CRS.Tests.Infrastructure | 旧结果 JSON、会话交易隐私、年度汇率、设置保存与日志开关、超过百份历史分页与父运行、年度汇总持久化与来源导出 |
| CRS.Tests.Wpf | 概览、忙碌门槛、输入失效、历史分页／复算／取消、跨页年度汇总选择及归属确认、富途文件独立替换与清除 |
| CRS.Tests.Verification | 原有 68 项金额、导入、状态、SQLite、NPOI、Flex 模拟和富途综合回归 |

在仓库根目录运行：

```powershell
dotnet build CRS.sln -c Release -m:1
./tests/Verify-Architecture.ps1
dotnet run --project tests/CRS.Tests.Domain -c Release --no-build
dotnet run --project tests/CRS.Tests.Application -c Release --no-build
dotnet run --project tests/CRS.Tests.Infrastructure -c Release --no-build
dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build
dotnet run --project tests/CRS.Tests.Verification -c Release --no-build
```

窗口验证使用隔离的临时数据库，不读取真实账户历史：

```powershell
dotnet run --project src/CRS.Desktop.Wpf -c Release -- --smoke-test --fixture D:/ATXCode/Test/CRS/tests/fixtures/ibkr_activity.xml
dotnet run --project src/CRS.Desktop.Wpf -c Release -- --smoke-test --futu
```

WPF 窗口检查会导航九个页面、检查绑定错误、历史 / 复核 / Excel 导出、取消不保存及输入清理。窗口检查还验证折叠菜单、设置窗口保存和最小窗口尺寸下的历史分页栏；传入 --image 的绝对 PNG 路径可输出折叠、设置、概览、导入、税务和历史截图。

携带 IBKR fixture 的窗口检查还会执行原件摘要校验、冻结口径复算另存、父运行及取消不保存验证；汇率页面检查数值调整和手动输入后读取。

该窗口流程还会勾选 IBKR 与富途来源任务、确认归属并执行年度汇总，验证另存、清空选择、导航及来源表格；截图包括年度汇总选择和结果。

WinForms 项目已删除。当前解决方案包含 WPF 前台、Domain／Application／Security／Infrastructure 四个后台项目及六个验证项目。

发布验收、合成性能与许可证清单请运行 ./tests/Run-ReleaseAcceptance.ps1 -Smoke，具体门槛见 [发布验收](../docs/validation/RELEASE_ACCEPTANCE.md)。抵免证据链与界面检查已加入 Infrastructure / WPF 验证项目。

新增 CRS.Tests.Security（纯密码学）与保险库集成／交互验证。所有持久化回归现由 TestStores 显式创建加密库，不提供明文测试兼容通道。
