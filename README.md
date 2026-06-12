# 海外证券个税助手（中国税务居民版）

支持富途 Excel/PDF 和 IBKR Activity Flex XML，在本地计算年度境外所得个人所得税辅助测算结果。

## 环境要求

- Python >= 3.9
- Windows 10/11

## 安装

```bash
pip install -r requirements.txt
```

## 运行

```bash
streamlit run app.py
```

启动后浏览器自动打开 `http://localhost:8501`。

## 使用方法

先选择券商。以下为富途操作；IBKR 操作见下方专节。

1. 上传**股息税表**（如 `2021股息.xlsx`）
2. 上传**年度交易流水**（如 `2021_717110.xlsx`）
3. 点击「开始计算」
4. 查看测算结果
5. 导出单份报告：当前支持范围内无待复核项时为 `Tax_Report_<年度>.xlsx`，有数据缺口时为 `Partial_Review_<年度>.xlsx`

页面中的税款年度可以留空，由收入表或文件名识别；无法识别或含多个年度时需手动指定。更换文件或年度会清除旧结果。

报告包含计算说明、税务汇总、收入明细，以及有数据的交易、入金、分红和待复核记录。提示与不完整状态会一并写入 Excel。

**期初持仓表中的价格不再自动作为历史买入成本。** 成本缺失、未支持的基金／转仓／公司行动等情况会生成待复核项。页面展示的是已计算部分，不能将部分金额视为完整年度结果。后续需要补充可靠历史成本及事件处理能力。

## 优化实施与 IBKR 规划

详见 [项目优化建议与 IBKR 接入规划](docs/项目优化建议与IBKR接入规划.md)。

富途仍上传现有的股息税表和交易流水，无需寻找或制作“数据包”。程序内部由富途适配器产生 `BrokerImportResult`，再交给统一计算服务。IBKR Activity Flex XML 解析器已实现；已完成三份真实导出文件的部分成交与现金验收，完整年度验收及自动下载尚未完成。

## IBKR 导入

1. 券商选择 **IBKR**，上传年度或多个连续月份的 Activity Flex XML。
2. 如有期初持仓，另外上传上一年 12 月 31 日的 Open Positions LOT 批次报告；确实无期初持仓时可勾选确认。
3. 核对导出未按账户、证券或现金类型过滤，并确认报告范围；未确认时结果列为待复核。
4. 选择年度并计算。资料不完整时下载待复核底稿，原始现金事件与来源文件指纹一并保留。

如需逐笔核对本系统收益与券商收益，请在 Trades / Executions 中导出 Realized PNL（`fifoPnlRealized`）。页面和 Excel 会显示每笔差额，差异不会自动覆盖测算金额。

完整导出字段、支持范围及限制见 [IBKR 导入说明](docs/IBKR_IMPORT_GUIDE.md)。可使用 [合成 XML 样例](tests/fixtures/ibkr_activity.xml) 演示；该文件不含真实账户数据；另有 [盈亏对账样例](tests/fixtures/ibkr_reconciliation.xml)。

首版支持普通多头股票／ETF、股息、收到的利息、预扣税及退回；衍生品、转仓、公司行动等仍需复核。CSV、PDF 对账单与 Flex 自动下载尚未支持。

## 测试

```bash
pip install -r requirements-dev.txt
python -B -m pytest -q -p no:cacheprovider
```

现有样例测试依赖本地 `samples` 中的文件；新增边界测试使用程序生成的合成数据，可独立运行：

```bash
python -B -m pytest tests/unit/test_accuracy_boundaries.py -q -p no:cacheprovider
```

新增测试覆盖账户及币种隔离、年度筛选、成交顺序、缺失成本、扣税冲正、导出完整性和 Streamlit 页面状态。

## 数据安全

所有数据仅在本地处理，不上传任何服务器，不联网。

## 汇率配置

汇率配置文件位于 `config/exchange_rate.json`，采用年度平均汇率。

如需使用其他汇率，可直接编辑该文件：

```json
{
  "2021": {
    "USD": 6.4515,
    "HKD": 0.8291,
    "CNY": 1.0000
  }
}
```

- `USD` / `HKD`：对应币种兑人民币的年度平均汇率
- `CNY`：固定为 1.0000（人民币无需换算）

若配置文件中缺少对应年份或币种，系统将报错提示。

## 项目结构

```
CRS/
├── app.py                          # Streamlit 主入口
├── requirements.txt                # Python 依赖
├── README.md
│
├── application/                    # 应用服务层
│   ├── __init__.py
│   └── tax_service.py              # 税务计算编排服务
│
├── domain/                         # 领域模型与核心计算
│   ├── __init__.py
│   ├── models/                     # 数据模型
│   │   ├── __init__.py
│   │   ├── dividend_income.py      # DividendIncomeRecord
│   │   ├── trade_record.py         # TradeRecord
│   │   ├── position_record.py      # PositionRecord
│   │   ├── asset_transfer.py       # AssetTransferRecord
│   │   ├── match_record.py         # MatchRecord
│   │   └── tax_summary.py          # TaxSummary
│   └── services/                   # 领域服务
│       ├── __init__.py
│       ├── engine.py               # FIFO 成本匹配引擎
│       └── tax_engine.py           # 个税测算引擎
│
├── infrastructure/                 # 基础设施层
│   ├── __init__.py
│   ├── adapters/                   # 券商文件到标准化导入结果
│   │   ├── futu_adapter.py         # 富途现有文件适配
│   │   └── ibkr_adapter.py         # IBKR Activity Flex XML
│   ├── parsers/                    # Excel/PDF 解析器
│   │   ├── __init__.py
│   │   ├── dividend_parser.py      # 股息税表解析
│   │   └── trade_parser.py         # 交易流水解析
│   ├── exporters/                  # Excel 导出器
│   │   ├── __init__.py
│   │   ├── excel_exporter.py       # 当前主流程：单份报告与待复核底稿
│   │   ├── summary_exporter.py     # 旧版独立汇总导出器
│   │   └── gain_exporter.py        # 旧版独立明细导出器
│   └── config/                     # 配置加载
│       ├── __init__.py
│       └── exchange_rate.json      # 年度汇率配置
│
├── config/                         # 配置文件目录（供用户编辑）
│   └── exchange_rate.json
│
├── samples/                        # 示例数据文件
│   ├── 2021股息.xlsx               # 示例股息税表
│   └── 2021_717110.xlsx            # 示例交易流水
│
├── tests/                          # 测试
│   ├── __init__.py
│   ├── unit/                       # 单元测试
│   │   └── __init__.py
│   └── integration/                # 集成测试
│       └── __init__.py
│
└── docs/                           # 需求与设计文档
    ├── PRD.md                      # 产品需求文档
    ├── SAD.md                      # 系统架构设计
    ├── API_SPEC.md                 # API 接口规范
    ├── TAX_ENGINE_SPEC.md          # 税务计算规则
    ├── CAPITAL_GAIN_FIFO_SPEC.md   # FIFO 成本法规范
    ├── FUTU_FIELD_MAPPING.md       # 字段映射规范
    └── TEST_SPEC.md                # 测试规范
```

## 免责声明

本工具仅用于税务辅助测算，不构成税务申报建议。实际申报请以税务机关要求为准。
