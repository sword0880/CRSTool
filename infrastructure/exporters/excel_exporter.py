"""Export Tax_Report.xlsx — single Excel with 3 sheets: 税务汇总, 资本利得汇总, 资本利得明细."""

import io
from collections import defaultdict
from typing import List

import pandas as pd

from domain.models.tax_summary import TaxSummary
from domain.models.match_record import MatchRecord
from domain.models.dividend_income import DividendIncomeRecord


class ExcelExporter:
    """Generate a single Excel file with all report sheets."""

    def build_report(
        self,
        summary: TaxSummary,
        matches: List[MatchRecord],
        dividends: List[DividendIncomeRecord],
    ) -> bytes:
        buf = io.BytesIO()
        with pd.ExcelWriter(buf, engine="openpyxl") as writer:
            # Sheet 1: 税务汇总
            self._write_summary_sheet(writer, summary)
            # Sheet 2: 资本利得汇总（按股票）
            self._write_symbol_summary_sheet(writer, matches)
            # Sheet 3: 资本利得明细
            self._write_detail_sheet(writer, matches)
        return buf.getvalue()

    def _write_summary_sheet(self, writer: pd.ExcelWriter, summary: TaxSummary):
        data = [
            ("股息收入（人民币）", float(summary.dividend_income_cny)),
            ("利息收入（人民币）", float(summary.interest_income_cny)),
            ("资本利得（人民币）", float(summary.capital_gain_cny)),
            ("股息利息应纳税额", float(summary.dividend_interest_tax)),
            ("资本利得税额", float(summary.capital_gain_tax)),
            ("境外税额抵免", float(summary.foreign_tax_credit)),
            ("预计补税", float(summary.total_supplement_tax)),
        ]
        df = pd.DataFrame(data, columns=["项目", "金额（人民币）"])
        df.to_excel(writer, sheet_name="税务汇总", index=False)

    def _write_symbol_summary_sheet(self, writer: pd.ExcelWriter, matches: List[MatchRecord]):
        if not matches:
            return
        symbol_gain = defaultdict(lambda: {"original": 0.0, "cny": 0.0, "currency": "", "count": 0})
        for m in matches:
            symbol_gain[m.symbol]["original"] += float(m.gain_original)
            symbol_gain[m.symbol]["cny"] += float(m.gain_cny)
            symbol_gain[m.symbol]["currency"] = m.currency
            symbol_gain[m.symbol]["count"] += 1

        rows = []
        for sym in sorted(symbol_gain.keys(), key=lambda s: symbol_gain[s]["cny"], reverse=True):
            g = symbol_gain[sym]
            rows.append({
                "股票代码": sym,
                "交易笔数": g["count"],
                "币种": g["currency"],
                "收益（原币）": round(g["original"], 2),
                "收益（人民币）": round(g["cny"], 2),
            })
        df = pd.DataFrame(rows)
        df.to_excel(writer, sheet_name="资本利得汇总", index=False)

    def _write_detail_sheet(self, writer: pd.ExcelWriter, matches: List[MatchRecord]):
        if not matches:
            return
        rows = []
        for m in matches:
            rows.append({
                "证券代码": m.symbol,
                "卖出日期": str(m.sell_date),
                "卖出数量": float(m.sell_quantity),
                "买入日期": str(m.buy_date),
                "匹配成本": float(m.buy_cost),
                "收入": float(m.sell_revenue),
                "买入手续费": float(m.buy_commission_alloc),
                "卖出手续费": float(m.sell_commission_alloc),
                "币种": m.currency,
                "收益（原币）": float(m.gain_original),
                "收益（人民币）": float(m.gain_cny),
            })
        df = pd.DataFrame(rows)
        df.to_excel(writer, sheet_name="资本利得明细", index=False)
