> 历史设计归档：2026-10-10。保留修订前内容；旧技术、示例及验收数字不代表当前实现。当前说明见 [文档索引](../README.md)。

\# 系统架构设计（SAD）V1.0



\## 1. 文档目的



本文档用于描述富途海外证券个税助手 V1 的系统架构设计。



目标：



\* 明确模块边界；

\* 降低耦合；

\* 支持测试驱动开发；

\* 为后续扩展多券商提供基础。



\---



\# 2. 系统定位



系统类型：



单机本地桌面辅助工具。



运行方式：



Streamlit Web UI。



部署方式：



用户本机运行。



特点：



\* 无需数据库；

\* 无需登录；

\* 无需联网；

\* 不上传任何数据。



\---



\# 3. 总体架构



用户界面层（UI）

↓

应用服务层（Application）

↓

领域计算层（Domain）

↓

基础设施层（Infrastructure）



\---



\# 4. 分层设计



\## 4.1 UI 层



职责：



与用户交互。



技术：



Streamlit。



功能：



\* 上传股息税表；

\* 上传交易流水；

\* 显示计算结果；

\* 下载导出文件；

\* 展示错误信息。



禁止：



\* 编写税务逻辑；

\* 编写 FIFO 逻辑。



\---



\## 4.2 Application 层



职责：



编排业务流程。



模块：



TaxCalculationService



流程：



读取文件

↓

解析Excel

↓

调用计算引擎

↓

生成结果

↓

导出报告



禁止：



直接操作 Excel。



\---



\## 4.3 Domain 层



职责：



核心业务逻辑。



模块：



DividendCalculator



FIFOEngine



TaxEngine



ExchangeRateEngine



ExportAssembler



特点：



纯 Python。



无外部依赖。



可独立测试。



\---



\## 4.4 Infrastructure 层



职责：



外部资源访问。



模块：



FutuDividendParser



FutuTradeParser



ExcelExporter



ExchangeRateRepository



技术：



Pandas



OpenPyXL



JSON



\---



\# 5. 目录结构



tax\_assistant/



app.py



application/

├─ tax\_service.py



domain/

├─ models.py

├─ fifo\_engine.py

├─ dividend\_engine.py

├─ tax\_engine.py

├─ exchange\_engine.py



infrastructure/

├─ futu\_dividend\_parser.py

├─ futu\_trade\_parser.py

├─ excel\_exporter.py

├─ exchange\_rate\_repo.py



config/

└─ exchange\_rate.json



tests/



docs/



\---



\# 6. 核心数据流



股息Excel

↓

DividendParser

↓

DividendRecord



交易Excel

↓

TradeParser

↓

TradeRecord



TradeRecord

↓

FIFOEngine

↓

MatchRecord



MatchRecord + DividendRecord

↓

TaxEngine

↓

TaxSummary



TaxSummary

↓

ExcelExporter



\---



\# 7. 核心领域模型



DividendRecord



TradeRecord



Lot



MatchRecord



TaxSummary



ExportBundle



全部采用 dataclass。



不可修改原始输入数据。



\---



\# 8. 异常处理原则



解析异常：



停止计算。



显示用户可理解错误。



例如：



“交易流水缺少字段：成交价格”。



计算异常：



停止计算。



例如：



“卖出数量超过持仓数量”。



导出异常：



允许重新导出。



\---



\# 9. 性能目标



交易流水：



≤10,000 行。



股息记录：



≤5,000 行。



计算时间：



≤5 秒。



内存：



≤500MB。



\---



\# 10. 扩展原则



新增券商：



仅新增 Parser。



新增税制：



仅新增 TaxEngine。



新增导出格式：



仅新增 Exporter。



禁止修改 FIFO 核心逻辑。



\---



\# 11. 安全原则



所有计算本地执行。



不采集用户身份信息。



不保存原始 Excel。



关闭程序后仅保留导出结果。



无网络访问。
