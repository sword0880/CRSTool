# 在应用内获取 IBKR Activity Flex XML

Python 与 WinForms 前台已移除。使用 WPF 的“数据导入”页面：

```powershell
dotnet run --project src/CRS.Desktop.Wpf
```

在 IBKR Client Portal 创建 XML 格式的 Activity Flex 查询，并取得查询 ID 和 Flex 服务令牌。查询 ID 可以写入 `config/ibkr_flex.json`；令牌只在前台密码框输入，不写入配置、报告或日志。

1. 在“数据导入”选择 IBKR 及测算年度，右侧填写“IBKR 自动下载”。
2. 输入年度查询 ID、服务令牌、起止日期。
3. 点击“下载并选用报告”，选择本地 XML 保存位置；成功后该文件自动成为本次活动报告。
4. 另外选择上一年末 LOT XML 或 C# 版结转；确实无期初持仓时勾选确认。自动下载目前不串联第二个期初查询。
5. 核对报告范围并勾选确认，再开始计算。

下载可取消，使用后台 FlexClient 的官方固定 HTTPS 地址和证书校验；报告尚未生成时按有限次数轮询。年度和日期必须与模板、账户权限一致，历史年度能否获取取决于 IBKR。普通手动文件导入继续可用。

下载文件保存到所选本地路径。令牌只在当前窗口会话保留，关闭窗口后释放。详细模板字段和期初限制见 [IBKR 导入指南](IBKR_IMPORT_GUIDE.md)。
