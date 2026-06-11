# 富途海外证券个税助手（中国税务居民版）

导入富途证券导出的 Excel 文件，自动计算年度境外所得个人所得税辅助测算结果。

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

1. 上传**股息税表**（如 `2021股息.xlsx`）
2. 上传**年度交易流水**（如 `2021_717110.xlsx`）
3. 点击「开始计算」
4. 查看测算结果
5. 导出 `Tax_Summary.xlsx` 和 `Capital_Gain_Detail.xlsx`

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
│   └── tax_service.py              # 税务计算编排服务（待实现）
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
│       ├── fifo_engine.py          # FIFO 成本匹配引擎
│       └── tax_engine.py           # 个税测算引擎
│
├── infrastructure/                 # 基础设施层
│   ├── __init__.py
│   ├── parsers/                    # Excel 解析器
│   │   ├── __init__.py
│   │   ├── dividend_parser.py      # 股息税表解析
│   │   └── trade_parser.py         # 交易流水解析
│   ├── exporters/                  # Excel 导出器
│   │   ├── __init__.py
│   │   ├── summary_exporter.py     # Tax_Summary.xlsx
│   │   └── gain_exporter.py        # Capital_Gain_Detail.xlsx
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
