# 小财牛

**财富有数，投资有度**

小财牛（工程标识 CRS）是面向个人的 Windows 本地投资分析与税务辅助工具。提供券商数据导入、交易与 FIFO 成本核对、税额辅助计算、投资绩效分析，以及独立后台的国家队 ETF/直接持股研究。**计算与预警不代表税务申报或投资建议；不自动报税。**

## 启动

环境：Windows 10（build 19041）或更新版本、.NET 10 SDK。打开 `CRS.sln`，选择 `CRS.Desktop.Wpf`，或在仓库根目录执行：

```powershell
dotnet run --project src/CRS.Desktop.Wpf
```

首次使用需创建加密保险库并配置登录验证；已有数据库仅支持受支持的加密格式，旧明文 SQLite 不会自动迁移。认证、恢复与数据保护要求见[保险库与登录指南](docs/guides/VAULT_GUIDE.md)。

## 使用和开发

- [文档总入口](docs/README.md)：只列当前常用文档；其他专题保留在对应目录。
- [数据导入](docs/guides/IBKR_IMPORT_GUIDE.md)、[富途导入](docs/guides/FUTU_CSHARP_IMPORT_GUIDE.md)、[设置](docs/guides/SETTINGS.md)。
- [当前架构](docs/architecture/CURRENT_ARCHITECTURE.md)、[插件设计](docs/CRS_PLUGIN_ARCHITECTURE.md)、[全局 UI 样式规范](docs/implementation/BRAND_VISUAL_SYSTEM.md)。
- [未完成问题](docs/KNOWN_ISSUES.md)、[发布验收](docs/validation/RELEASE_ACCEPTANCE.md)、[测试命令](tests/README.md)。

`src/` 为生产代码，`tests/` 为测试与验收，`docs/` 为设计、操作和历史记录，`tools/` 为维护脚本。架构目标、历史实施记录和真实已验收结果不是同一回事，判断当前可用能力请以代码、当前问题表及发布验收为准。
