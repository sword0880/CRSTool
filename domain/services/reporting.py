"""Shared grouping for screen and workbook; amounts stay Decimal until display."""
from collections import defaultdict
from decimal import Decimal


def capital_rows(matches):
    groups = defaultdict(lambda: [0, Decimal(0), Decimal(0)])
    for m in matches:
        key = (m.broker, m.account_no or m.account_name, m.instrument_id or m.symbol, m.market, m.currency)
        g = groups[key]
        g[0] += 1
        g[1] += m.gain_original
        g[2] += m.gain_cny
    return [{"券商": k[0], "账户": k[1], "证券标识": k[2], "市场": k[3], "币种": k[4],
             "匹配笔数": g[0], "收益（原币）": float(g[1]), "收益（人民币）": float(g[2])}
            for k, g in sorted(groups.items())]


def deposit_rows(deposits):
    groups = defaultdict(lambda: [0, Decimal(0)])
    for d in deposits:
        g = groups[(d.account_no or d.account_name, d.currency)]
        g[0] += 1
        g[1] += d.amount
    return [{"账户": k[0], "币种": k[1], "入金笔数": g[0], "入金总额": float(g[1])}
            for k, g in sorted(groups.items())]


def dividend_rows(dividends):
    groups = defaultdict(lambda: [0, Decimal(0)])
    for d in dividends:
        g = groups[(d.account_no or d.account_name, d.symbol, d.currency)]
        g[0] += 1
        g[1] += d.amount
    return [{"账户": k[0], "股票/公司": k[1], "币种": k[2], "分红笔数": g[0], "分红总额": float(g[1])}
            for k, g in sorted(groups.items())]


def pnl_reconciliation_rows(reconciliations):
    def amount(value):
        return None if value is None else float(value)
    return [{"账户": r.reported.account, "资产类别": r.reported.asset_category,
             "证券": r.reported.symbol, "Conid": r.reported.instrument_id,
             "日期": str(r.reported.date), "币种": r.reported.currency, "卖出数量": float(r.reported.quantity),
             "成交ID": r.reported.record_id, "本系统收益（原币）": amount(r.calculated),
             "券商收益（原币）": amount(r.reported.amount), "差额（本系统减券商）": amount(r.difference),
             "状态": r.status, "说明": r.explanation,
             "来源文件": r.reported.source_file, "记录序号": r.reported.source_row}
            for r in reconciliations]
