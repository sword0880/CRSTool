"""Export Capital_Gain_Detail.xlsx."""

import io
from typing import List

import pandas as pd

from domain.models.match_record import MatchRecord


class GainExporter:
    """Generate Capital_Gain_Detail.xlsx from MatchRecords."""

    def build_gain_detail(self, matches: List[MatchRecord]) -> bytes:
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

        buf = io.BytesIO()
        with pd.ExcelWriter(buf, engine="openpyxl") as writer:
            df.to_excel(writer, sheet_name="资本利得明细", index=False)
        return buf.getvalue()
