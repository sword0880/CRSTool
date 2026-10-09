# CRS Tax Manager

WPF 是唯一前台，采用左侧导航和页面级 MVVM，通过 Application 用例调用共享后台。WinForms 和 Python 程序已移除。

用支持 .NET 10 的 Visual Studio 打开根目录 **CRS.sln**，将 CRS.Desktop.Wpf 设为启动项目。WPF 最低支持 Windows 10 2004（build 19041）。

```powershell
dotnet run --project src/CRS.Desktop.Wpf
```

| 位置 | 用途 |
| --- | --- |
| src/CRS.Desktop.Wpf | 导航壳、概览、导入、交易、FIFO、汇率、税务、核对、导出及历史页面 |
| src/CRS.Application | 导入与核算编排、复核、报告投影及接口 |
| src/CRS.Domain | 证券交易、FIFO、汇率事实和税务规则 |
| src/CRS.Infrastructure | 券商解析、SQLite、汇率配置、PDF 读取、Excel 导出和网络 |
| tests | 分层验证、原有综合验证和架构检查 |
| config | WPF 配置源，构建时复制到运行目录 |
| docs | 需求、设计、券商指南及迁移记录 |

依赖版本统一在 Directory.Packages.props 管理；使用 WPF UI 4.3.0、CommunityToolkit.Mvvm、DataGridExtensions 和 LiveChartsCore.SkiaSharpView.WPF。

WPF 继续读取 %LOCALAPPDATA%/CRS.WinForms/crs.db，旧目录名仅用于保留已有历史，不代表保留 WinForms 项目。默认仅保存交易笔数和结果；交易明细留在当前会话，选择“保存可重放规范化快照”后才能从历史恢复。旧结果没有交易快照时，交易页会提示重新导入。规范化快照不等于严格历史复算已完成，剩余限制见架构优化记录。

原 Python 的本地结转与复核资料归档于 storage/python-legacy，不参与新版计算、不提交到 Git；原源码仍可从 Git 历史恢复。samples、运行缓存及依赖缓存也不提交到 Git。

[项目结构](docs/PROJECT_STRUCTURE.md) · [文档复审](docs/文档复审_20261010.md) · [迁移记录](docs/WPF迁移与项目分层评估.md) · [架构优化记录](docs/架构优化记录_20261010.md) · [验证说明](tests/README.md)
