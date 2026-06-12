"""Coverage and quantity reconciliation for normalized IBKR reports."""
from datetime import date, timedelta
from decimal import Decimal
from domain.models.calculation_issue import CalculationIssue, inventory_key
from domain.models.exceptions import TaxAssistantError


def _covers(periods, start, end):
    cursor = start
    for a, b in sorted(periods):
        if b < cursor:
            continue
        if a > cursor:
            return False
        cursor = b + timedelta(days=1)
        if cursor > end:
            return True
    return False


def check_coverage(data, year):
    start, end = date(year, 1, 1), date(year, 12, 31)
    activity = [s for s in data.source_reports if s.role == "activity"]
    accounts = {s.account for s in activity if s.start <= end and s.end >= start}
    if not accounts:
        raise TaxAssistantError("IBKR 报告未覆盖所选年度")
    issues = []
    for account in accounts:
        sources = [s for s in activity if s.account == account]
        for label, field in (("成交", "has_trades"), ("现金", "has_cash")):
            periods = [(s.start, s.end) for s in sources if getattr(s, field)]
            if not _covers(periods, start, end):
                issues.append(CalculationIssue("INCOMPLETE_PERIOD", f"{label}报告未连续覆盖完整年度，请补齐月份或核实开户期间。", account=account))
        snapshots = [s for s in data.source_reports if s.role == "opening" and s.account == account]
        if snapshots:
            if len(snapshots) != 1 or snapshots[0].end != date(year - 1, 12, 31):
                raise TaxAssistantError("每个账户的期初报告必须只有一份，截止日期须为上一年 12 月 31 日")
        elif not data.opening_zero_confirmed:
            issues.append(CalculationIssue("OPENING_UNCONFIRMED", "未提供期初批次且未确认期初无持仓；当前成本覆盖范围未确认。", account=account))
        if data.opening_zero_confirmed and any(t.account_no == account and t.trade_date < start for t in data.trades):
            raise TaxAssistantError("声明期初无持仓时不能同时导入此前历史成交，请调整文件或提供期初批次")
        if not any(s.has_positions and s.end == end for s in sources):
            issues.append(CalculationIssue("NO_END_POSITIONS", "缺少年度末 OpenPositions，无法完成期末数量核对。", account=account))
    opening_accounts = {s.account for s in data.source_reports if s.role == "opening"}
    if not opening_accounts <= accounts:
        raise TaxAssistantError("期初批次包含不在本年度活动报告中的账户")
    return issues


def reconcile_positions(data, year, engine):
    end = date(year, 12, 31)
    checked_accounts = {s.account for s in data.source_reports if s.role == "activity" and s.end == end and s.has_positions}
    reported = {inventory_key(p): p for p in data.positions if p.date == end}
    issues = []
    for key in engine.ending_quantities.keys() | reported.keys():
        if key[1] not in checked_accounts or key in engine.uncertain_keys:
            continue
        actual = engine.ending_quantities.get(key, Decimal(0))
        p = reported.get(key)
        expected = p.quantity if p else Decimal(0)
        if actual != expected:
            issues.append(CalculationIssue("POSITION_MISMATCH", f"期末数量不一致：计算 {actual}，报告 {expected}。",
                account=key[1], symbol=p.symbol if p else str(key[2]), currency=key[3], date=end,
                instrument_id=str(key[2]), quantity=expected - actual, source_sheet="OpenPositions"))
    return issues


def reconcile_realized_pnl(data, year, engine, matches):
    """Compare each execution in original currency; never substitute broker P/L."""
    from collections import defaultdict
    from domain.models.pnl_reconciliation import PnlReconciliation

    grouped = defaultdict(list)
    for m in matches:
        if m.broker == "IBKR" and m.sell_date.year == year:
            grouped[(m.account_no, m.instrument_id, m.currency, m.sell_record_id)].append(m)
    rows, issues = [], []
    for reported in sorted((r for r in data.realized_pnl if r.date.year == year),
                           key=lambda r: (r.account, r.date, r.instrument_id, r.currency, r.record_id)):
        if reported.asset_category and reported.asset_category not in {"STK", "ETF"}:
            rows.append(PnlReconciliation(reported, None, None, "未支持资产",
                "该资产类别不参与股票 FIFO 收益对账；券商原始收益仅作为参考保留。"))
            continue
        key = (reported.account, reported.instrument_id, reported.currency, reported.record_id)
        matched = grouped.get(key, [])
        uncertain = ("IBKR", reported.account, reported.instrument_id, reported.currency) in engine.uncertain_keys
        quantity = sum((m.sell_quantity for m in matched), Decimal(0))
        calculated = difference = None
        if uncertain or not matched or quantity != reported.quantity:
            status = "无法核对"
            reason = "本系统未形成数量完整且成本已确认的 FIFO 结果；请先处理成本或未支持事件。"
            if reported.amount is None:
                reason += "报告也未提供 fifoPnlRealized。"
        else:
            calculated = sum((m.gain_original for m in matched), Decimal(0))
            if reported.amount is None:
                status = "缺少券商收益"
                reason = "请在 Trades 的 Executions 层级导出 Realized PNL（fifoPnlRealized）；空值不视为零。"
            else:
                difference = calculated - reported.amount
                status = "一致" if abs(difference) <= Decimal("0.02") else "存在差异"
                reason = ("本系统减券商收益，差额在 0.02 原币单位容差内。" if status == "一致" else
                          "请核对成本批次、费用、调整及券商成本法；券商金额未覆盖本系统结果。")
                if status == "存在差异":
                    issues.append(CalculationIssue("REALIZED_PNL_MISMATCH",
                        f"已实现盈亏差异：本系统 {calculated}，IBKR {reported.amount}，差额 {difference} {reported.currency}。",
                        account=reported.account, symbol=reported.symbol, currency=reported.currency,
                        date=reported.date, source_row=reported.source_row, source_sheet="Trades",
                        quantity=reported.quantity, source_file=reported.source_file,
                        record_id=reported.record_id, instrument_id=reported.instrument_id))
        rows.append(PnlReconciliation(reported, calculated, difference, status, reason))
    missing = sum(r.reported.amount is None for r in rows if r.status != "未支持资产")
    warnings = [f"{missing} 笔卖出未提供券商已实现盈亏，无法完成这些成交的收益核对；请导出 Realized PNL 字段。"] if missing else []
    if rows:
        warnings.append("已实现盈亏对账仅比较导入的卖出成交，不代表现金余额、全账户收益或税务口径已核准。")
    return rows, issues, warnings
