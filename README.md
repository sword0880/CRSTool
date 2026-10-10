# CRS Tax Manager

.NET 10 WPF 本地证券所得辅助测算工具。WPF UI 4.3.x、CommunityToolkit.Mvvm、DataGridExtensions 和 LiveCharts 提供前台，Domain／Application／Infrastructure 负责业务。Python 与 WinForms 已移除。

打开 CRS.sln，将 CRS.Desktop.Wpf 设为启动项目；最低支持 Windows 10 build 19041。

```powershell
dotnet run --project src/CRS.Desktop.Wpf
```

左侧菜单可折叠为图标，底部提供历史任务与设置入口。日志和数据库设置下次启动生效。富途收入与交易明细可独立选择、替换或清除。IBKR 接收 Activity Flex XML，可选 Flex 下载。

默认只保存结果和交易笔数；完整规范化快照由用户选择。数据库默认在 %LOCALAPPDATA%/CRS/data，不自动识别旧版本目录；改目录不自动迁移。令牌只保留当前窗口会话。

[使用与开发文档](docs/README.md) · [设置说明](docs/SETTINGS.md) · [未完成问题](docs/KNOWN_ISSUES.md) · [验证](tests/README.md)
