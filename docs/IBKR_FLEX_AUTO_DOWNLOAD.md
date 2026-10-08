# 在应用内获取 IBKR Activity Flex XML

## 启动应用

在项目根目录使用同一个 Python 环境安装依赖并启动 Streamlit（PowerShell）：

```powershell
cd D:\ATXCode\Test\CRS
py -3 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m streamlit run app.py
```

不要用 IDE 的“运行 Python 文件”直接执行 `app.py`；它是 Streamlit 应用入口。若虚拟环境已经存在，跳过创建步骤，继续使用该环境的 `python.exe` 安装和启动。

登录 [IBKR Client Portal](https://portal.interactivebrokers.com/en/trading/client-portal.php?menu=B)，在“业绩与报表 → 自主查询”创建格式为 **XML** 的 Activity Flex 模板，并记下数字“查询 ID”。在“自主网络服务配置”生成服务令牌。此前若把令牌发到聊天或其他公开位置，请重新生成令牌，使旧令牌失效；新令牌只在应用的密码框中输入。

经常使用时，可在 [`config/ibkr_flex.json`](../config/ibkr_flex.json) 填入 `annual_query_id` 和 `opening_lots_query_id`。应用会把这两个查询 ID 预填到表单。**不要把服务令牌写入 JSON**；它仍在每次操作时通过密码框输入。查询 ID 可以留空，直接在页面填写。

应用里选择 **IBKR**，填写税款年度，展开“从 IBKR 自动获取 Activity Flex XML”：

1. 填入年度 Activity Flex 查询 ID 和服务令牌。
2. 选报告起止日期。2025 全年为 `2025-01-01` 至 `2025-12-31`。
3. 若已有独立的期初 LOT 模板，填入其查询 ID；应用按年度报告开始日期前一日请求该模板。2025 年对应 `2024-12-31`。没有模板时留空，仍可生成待复核结果。
4. 点击“生成并获取 XML”。获取成功后可以保存原始 XML 备份，再按原有流程确认导出范围并点击“开始计算”。

若目标年度才开户、并确认该账户年初没有持仓，可勾选页面的“期初无持仓”。勾选后，已上传或自动获取的期初 XML 不参与本次计算；年度活动报告的 `OpenPositions` 通常是期末 `SUMMARY`，不能放入“上一年末持仓批次 XML”输入框。若年度内曾从其他券商转入证券，后续卖出仍须核对原始买入成本。

应用会调用 IBKR 的 [SendRequest](https://www.interactivebrokers.com/docs/web-api/api-reference/send-request)，用返回的 ReferenceCode 调用 [GetStatement](https://www.interactivebrokers.com/docs/web-api/api-reference/get-statement)。报告尚在生成时会有限次数重试。错误码可参考 [IBKR 官方错误码表](https://www.ibkrguides.com/orgportal/performanceandstatements/flex3error.htm)。指定 `fd`、`td` 起止日期时，应用不套用 IBKR 对相对期间参数 `p` 的 365 天限制；2024 闰年全年可直接选择 `2024-01-01` 至 `2024-12-31`。已下载的 XML 只保存在当前应用会话内存中，可点“清除已获取的 IBKR XML”。密码框中的令牌在表单提交后会保留，方便重复查询；切换券商或关闭页面可能使它清空。令牌不写入配置、报告或日志。

原有手动上传入口继续可用；同时提供手动文件时，以手动文件为准。应用自动获取需要本机能够访问 IBKR Flex Web Service。
