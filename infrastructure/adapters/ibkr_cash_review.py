"""按成交日原币余额核对现金闭环；不使用已结算余额或基础币种折算。"""
from collections import defaultdict
from decimal import Decimal


def reconcile_statement(statement, account, start, end, filename):
    from infrastructure.adapters.ibkr_adapter import number, required, flex_date
    from domain.models.exceptions import ParseException

    movements = defaultdict(lambda: Decimal(0))
    reasons = []
    # 每份报告独立去重，年度与月度报告重叠时不重复相加核对余额。
    seen = set()
    for element in statement.findall("./CashTransactions/CashTransaction"):
        a = element.attrib
        if a.get("levelOfDetail", "DETAIL") != "DETAIL":
            reasons.append("现金记录不是 DETAIL")
            continue
        key = ("cash", required(a, "transactionID"))
        if key not in seen:
            movements[required(a, "currency")] += number(a, "amount")
            seen.add(key)
    for element in statement.findall("./Trades/Trade"):
        a = element.attrib
        if a.get("levelOfDetail") != "EXECUTION":
            continue
        key = ("trade", required(a, "tradeID"))
        if key in seen:
            continue
        seen.add(key)
        if a.get("assetCategory") not in {"STK", "ETF", "CASH"} or a.get("origTradeID", "") not in {"", "0"} or any(
                n in {"Ca", "Co"} for n in a.get("notes", "").replace(";", ",").split(",")):
            reasons.append("存在未支持或更正成交")
            continue
        currency = required(a, "currency")
        # 佣金按实际扣费币种入账，不能假定与报价币种一致。
        fee_currency = required(a, "ibCommissionCurrency")
        if a.get("assetCategory") in {"STK", "ETF"} and fee_currency == currency:
            # 股票同币佣金已包含在原始净现金中，不能再扣一次。
            movements[currency] += number(a, "netCash")
        else:
            movements[currency] += number(a, "proceeds")
            movements[fee_currency] += number(a, "ibCommission")
        tax = number(a, "taxes")
        if tax:
            reasons.append("存在交易税，需确认现金方向")
        if a.get("assetCategory") == "CASH":
            pair = required(a, "symbol").split(".")
            if len(pair) != 2 or pair[1] != currency or pair[0] == pair[1]:
                reasons.append("换汇币种对不明确")
            else:
                # 普通现货换汇：基础币种数量与报价币种 proceeds 是两个现金腿。
                movements[pair[0]] += number(a, "quantity")
    for section in ("CorporateActions", "Transfers", "TradeTransfers", "Adjustments", "TransactionTaxes",
                    "UnbookedTrades", "OptionEAE", "UnsettledTransfers", "StockGrantActivities"):
        if statement.findall(f"./{section}/*"):
            reasons.append(f"{section} 现金影响尚未支持")
    for section in ("Trades", "CashTransactions"):
        if statement.find(section) is None:
            reasons.append(f"缺少 {section}")
    reported = {}
    for element in statement.findall("./CashReport/CashReportCurrency"):
        a = element.attrib
        if a.get("levelOfDetail") != "Currency":
            continue
        currency = required(a, "currency")
        if currency in reported:
            raise ParseException("CashReport 同一币种存在重复的原币汇总")
        reported[currency] = a
    rows = []
    for currency in sorted(set(movements) | set(reported)) or [""]:
        a = reported.get(currency, {})
        missing = list(reasons)
        for field in ("startingCash", "endingCash", "fromDate", "toDate"):
            if not a.get(field, "").strip():
                missing.append(f"缺少 {field}")
        if ((a.get("fromDate") and flex_date(a["fromDate"]) != start)
                or (a.get("toDate") and flex_date(a["toDate"]) != end)):
            missing.append("现金报告期间不一致")
        beginning = number(a, "startingCash") if a.get("startingCash", "").strip() else None
        ending = number(a, "endingCash") if a.get("endingCash", "").strip() else None
        calculated = beginning + movements[currency] if beginning is not None and not missing else None
        difference = calculated - ending if calculated is not None and ending is not None else None
        status = "无法核对" if missing else "一致" if abs(difference) <= Decimal("0.02") else "存在差异"
        rows.append({"账户": account, "币种": currency, "期间开始": str(start), "期间结束": str(end),
                     "期初现金": str(beginning) if beginning is not None else "",
                     "明细净变动": str(movements[currency]), "计算期末现金": str(calculated) if calculated is not None else "",
                     "报告期末现金": str(ending) if ending is not None else "",
                     "差额": str(difference) if difference is not None else "", "状态": status,
                     "说明": "；".join(sorted(set(missing))) if missing else "成交日原币余额；差额容差 0.02",
                     "来源文件": filename})
    return rows
