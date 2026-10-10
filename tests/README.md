# CRS 验证项目

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

WinForms 项目已删除。当前解决方案只有 WPF 前台、三个后台项目及五个验证项目。

发布验收、合成性能与许可证清单请运行 ./tests/Run-ReleaseAcceptance.ps1 -Smoke，具体门槛见 [发布验收](../docs/RELEASE_ACCEPTANCE.md)。抵免证据链与界面检查已加入 Infrastructure / WPF 验证项目。
