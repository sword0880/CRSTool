\# API 设计说明（API\_SPEC）V1.0



\## 1. 文档目的



定义系统内部 Service 接口。



即使 V1 为本地 Streamlit 工具，也要求采用接口设计，避免 UI 与计算逻辑耦合。



\---



\# 2. TaxCalculationService



职责：



统一编排整个计算流程。



接口：



calculate(

dividend\_file,

trade\_file,

tax\_year

) -> ExportBundle



流程：



解析股息文件

↓

解析交易文件

↓

FIFO计算

↓

税务计算

↓

生成导出对象



异常：



TaxException



\---



\# 3. DividendParser



parse(file\_path)



返回：



List\[DividendRecord]



异常：



ParseException



\---



\# 4. TradeParser



parse(file\_path)



返回：



List\[TradeRecord]



异常：



ParseException



\---



\# 5. FIFOEngine



calculate(trades)



返回：



List\[MatchRecord]



异常：



InventoryException



\---



\# 6. TaxEngine



calculate(

dividends,

matches,

tax\_year

)



返回：



TaxSummary



\---



\# 7. ExchangeRateRepository



get\_rate(

tax\_year,

currency

)



返回：



Decimal



异常：



UnsupportedCurrencyException



\---



\# 8. ExportService



build\_summary(summary)



build\_gain\_detail(matches)



返回：



Excel 文件对象。



\---



\# 9. UI 调用流程



Streamlit



↓



TaxCalculationService



↓



ExportBundle



↓



下载结果。



