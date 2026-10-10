# CRS Tax Manager

.NET 10 WPF 本地证券所得辅助测算工具。WPF UI 4.3.x、CommunityToolkit.Mvvm、DataGridExtensions 和 LiveCharts 提供前台，Domain／Application／Infrastructure 负责业务。Python 与 WinForms 已移除。

打开 CRS.sln，将 CRS.Desktop.Wpf 设为启动项目；最低支持 Windows 10 build 19041。

```powershell
dotnet run --project src/CRS.Desktop.Wpf
```

左侧菜单可折叠为图标，底部提供历史任务与设置入口。日志和数据库设置下次启动生效。富途收入与交易明细可独立选择、替换或清除。IBKR 接收 Activity Flex XML，可选 Flex 下载。

默认只保存结果和交易笔数；完整规范化快照由用户选择。数据库默认在 %LOCALAPPDATA%/CRS/data，不自动识别旧版本目录；改目录不自动迁移。令牌只保留当前窗口会话。

历史任务支持分页、报告原件校验及“复算并另存”。复算使用冻结汇率，输出一致后保留父任务并新建运行；旧版本快照仍可查看，需重新导入才能按新版口径复算。同秒成交无法证明先后时列为待复核，不猜测成本顺序。

年度汇总：在历史任务页勾选同年度的账户任务（可跨页），确认均属于同一纳税人后点击“汇总并另存”。后台合并原币事实统一计税，汇总结果和 Excel 保留全部来源及券商账户关系，原任务不变。同券商重复账户、不同年度或冲突汇率会拒绝；资料不足保持待复核及未知预计补税。

[使用与开发文档](docs/README.md) · [设置说明](docs/SETTINGS.md) · [未完成问题](docs/KNOWN_ISSUES.md) · [验证](tests/README.md)

境外抵免支持历史任务导入国家、所得项目与凭证明细，说明见 [抵免复核](docs/FOREIGN_CREDIT_REVIEW.md)。发布验证及未完成门槛见 [发布验收](docs/RELEASE_ACCEPTANCE.md)，数据保存范围见 [隐私说明](docs/PRIVACY.md)。
