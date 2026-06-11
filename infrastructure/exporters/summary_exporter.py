"""Export Tax_Summary.xlsx."""

import io
from decimal import Decimal

import pandas as pd

from domain.models.tax_summary import TaxSummary


class SummaryExporter:
    """Generate Tax_Summary.xlsx from TaxSummary."""

    def build_summary(self, summary: TaxSummary) -> bytes:
        data = [
            ("股息收入（人民币）", summary.dividend_income_cny),
            ("利息收入（人民币）", summary.interest_income_cny),
            ("资本利得（人民币）", summary.capital_gain_cny),
            ("股息利息应纳税额", summary.dividend_interest_tax),
            ("资本利得税额", summary.capital_gain_tax),
            ("境外税额抵免", summary.foreign_tax_credit),
            ("预计补税", summary.total_supplement_tax),
        ]

        df = pd.DataFrame(data, columns=["项目", "金额（人民币）"])

        buf = io.BytesIO()
        with pd.ExcelWriter(buf, engine="openpyxl") as writer:
            df.to_excel(writer, sheet_name="税务汇总", index=False)
        return buf.getvalue()
