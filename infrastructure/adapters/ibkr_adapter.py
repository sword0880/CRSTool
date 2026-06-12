"""Activity Flex XML adapter. Broker totals are never treated as executions."""
from collections import defaultdict
from datetime import datetime, date
from decimal import Decimal, InvalidOperation
from hashlib import sha256
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from domain.models.broker_import import BrokerImportResult, WithholdingRecord, SourceReport, OpeningLotRecord, CashEvent
from domain.models.calculation_issue import CalculationIssue
from domain.models.dividend_income import DividendIncomeRecord
from domain.models.dividend_received import DividendReceivedRecord
from domain.models.deposit_record import DepositRecord
from domain.models.position_record import PositionRecord
from domain.models.trade_record import TradeRecord
from domain.models.exceptions import ParseException
from domain.models.pnl_reconciliation import BrokerRealizedPnl

ZERO = Decimal(0)
MAX_FILE_BYTES = 25 * 1024 * 1024


class _NoDTD(ET.TreeBuilder):
    def doctype(self, name, pubid, system):
        raise ParseException("不支持含 DTD 或实体声明的 XML，请重新导出 Flex XML")


def required(a, key):
    value = a.get(key, "").strip()
    if not value:
        raise ParseException(f"IBKR 缺少字段 {key}，请在 Flex 模板中勾选该字段")
    return value


def number(a, key):
    try:
        value = Decimal(required(a, key))
    except InvalidOperation:
        raise ParseException(f"IBKR 字段 {key} 不是有效数字")
    if not value.is_finite():
        raise ParseException(f"IBKR 字段 {key} 必须是有限数字")
    return value


def flex_date(value):
    for fmt in ("%Y%m%d", "%Y-%m-%d"):
        try:
            return datetime.strptime(value, fmt).date()
        except ValueError:
            pass
    raise ParseException("IBKR 日期格式须为 yyyyMMdd 或 yyyy-MM-dd")


def flex_time(value):
    for fmt in ("%Y%m%d;%H%M%S", "%Y%m%d;%H:%M:%S", "%Y-%m-%d;%H:%M:%S",
                "%Y-%m-%d %H:%M:%S", "%Y%m%d %H%M%S"):
        try:
            return datetime.strptime(value, fmt)
        except ValueError:
            pass
    if "T" in value:
        try:
            return datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            pass
    raise ParseException("IBKR 时间格式须包含日期和时分秒，例如 20250102;153000")


def read_xml(source):
    name = Path(source if isinstance(source, (str, Path)) else getattr(source, "name", "report.xml")).name
    if isinstance(source, (str, Path)):
        with open(source, "rb") as stream:
            raw = stream.read(MAX_FILE_BYTES + 1)
    else:
        source.seek(0)
        raw = source.read(MAX_FILE_BYTES + 1)
    if len(raw) > MAX_FILE_BYTES:
        raise ParseException("单份 IBKR XML 不能超过 25 MB，请按年度或月份拆分")
    try:
        root = ET.fromstring(raw, parser=ET.XMLParser(target=_NoDTD()))
    except ET.ParseError:
        raise ParseException(f"{name} 不是有效 XML；首版仅支持 Activity Flex XML")
    for element in root.iter():
        element.tag = element.tag.rsplit("}", 1)[-1]
    if root.tag != "FlexQueryResponse" or root.get("type", "AF") != "AF":
        raise ParseException("请上传 Activity Flex 报告内容，不能使用请求回执、Trade Confirmation 或普通对账单")
    statements = root.findall("./FlexStatements/FlexStatement")
    if not statements:
        raise ParseException("IBKR XML 中没有 FlexStatement")
    return name, sha256(raw).hexdigest(), statements


class IbkrReportAdapter:
    def parse_files(self, report_files, opening_file=None, opening_zero=False):
        if not report_files:
            raise ParseException("请上传至少一份 IBKR Activity Flex XML")
        if len(report_files) > 36:
            raise ParseException("一次最多导入 36 份报告")
        if opening_file is not None and opening_zero:
            raise ParseException("已提供期初成本报告，不能同时声明期初无持仓")
        self.data = BrokerImportResult(broker="IBKR", opening_zero_confirmed=opening_zero)
        self.seen = {}
        self.duplicates = 0
        self.income = defaultdict(lambda: [ZERO, ZERO, ZERO])
        self.closing = {}
        self.closing_snapshots = {}
        self.opening_seen = set()
        for source in report_files:
            self._file(source, opening=False)
        if opening_file is not None:
            self._file(opening_file, opening=True)
        self.data.positions = list(self.closing.values())
        self.data.dividends = [DividendIncomeRecord(account, account, year, *amounts, currency)
                              for (account, year, currency), amounts in sorted(self.income.items())]
        self.data.report_years = sorted({year for s in self.data.source_reports if s.role == "activity"
                                        for year in range(s.start.year, s.end.year + 1)})
        if self.duplicates:
            self.data.warnings.append(f"按账户及记录 ID 去除了 {self.duplicates} 条重复成交／现金记录。")
        self.data.warnings.append("IBKR 报告须包含全部账户、证券和现金类型；程序无法从 XML 判断是否在导出模板中预先筛除了记录。")
        return self.data

    def _issue(self, code, message, a=None, section="", row=0, date=None):
        a = a or {}
        if date is None:
            raw_date = a.get("tradeDate") or a.get("dateTime") or a.get("date") or a.get("reportDate")
            if raw_date:
                for candidate in (raw_date[:8], raw_date[:10]):
                    try:
                        date = flex_date(candidate)
                        break
                    except ParseException:
                        pass
        self.data.issues.append(CalculationIssue(code, message, account=self.account,
            symbol=a.get("symbol", ""), currency=a.get("currency", ""), source_row=row,
            source_sheet=section, source_file=self.name, instrument_id=a.get("conid", ""),
            record_id=a.get("transactionID", a.get("tradeID", "")), date=date,
            source_start=self.start, source_end=self.end))

    def _account(self, a):
        if a.get("model", "").strip():
            raise ParseException("首版不支持按 Model 分区的报告，请导出账户级完整报告")
        if a.get("accountId", self.account) != self.account:
            raise ParseException("记录账户与 FlexStatement 账户不一致，拒绝混合计算")

    def _dedup(self, kind, record_id, canonical):
        key = (self.account, kind, record_id)
        if key in self.seen:
            if self.seen[key] != canonical:
                raise ParseException(f"同一账户的 {kind} ID {record_id} 存在冲突，可能是更正报告；请保留一致版本")
            self.duplicates += 1
            return False
        self.seen[key] = canonical
        return True

    def _file(self, source, opening):
        self.name, digest, statements = read_xml(source)
        for statement in statements:
            self.account = required(statement.attrib, "accountId")
            start = flex_date(required(statement.attrib, "fromDate"))
            end = flex_date(required(statement.attrib, "toDate"))
            if start > end or not (2000 <= start.year <= end.year <= 2100):
                raise ParseException("IBKR 报告期间无效")
            self.start, self.end = start, end
            source_report = SourceReport(self.name, digest, self.account, start, end,
                "opening" if opening else "activity", statement.find("Trades") is not None,
                statement.find("CashTransactions") is not None, statement.find("OpenPositions") is not None)
            self.data.source_reports.append(source_report)
            if opening:
                self._opening(statement)
                continue
            self.data.income_data_provided |= source_report.has_cash
            self.data.trade_file_present |= source_report.has_trades
            for section in ("Trades", "CashTransactions"):
                if statement.find(section) is None:
                    self._issue("MISSING_SECTION", f"报告缺少 {section} 部分，请补充导出。", section=section)
            self._trades(statement)
            self._cash(statement)
            self._reconcile_cash_report(statement)
            self._closing(statement)
            for section in ("CorporateActions", "Transfers", "TradeTransfers", "OptionEAE", "UnsettledTransfers", "StockGrantActivities"):
                for row, event in enumerate(statement.findall(f"./{section}/*"), 1):
                    self._account(event.attrib)
                    self._issue("UNSUPPORTED_TRANSFER", f"{section} 事件尚未处理，受影响成本需复核。", event.attrib, section, row)
            for section in ("FxTransactions", "FxPositions", "CFDCharges", "SLBActivities", "SLBFees"):
                element = statement.find(section)
                if element is not None and len(element):
                    self._issue("UNSUPPORTED_SECTION", f"{section} 尚未纳入计算，请人工复核。", section=section)
            for section in ("TransactionTaxes", "Adjustments", "UnbookedTrades"):
                element = statement.find(section)
                if element is not None and len(element):
                    self._issue("UNSUPPORTED_TRANSFER", f"{section} 未支持，账户成本需复核。", section=section)

    def _check_date(self, day):
        if not self.start <= day <= self.end:
            raise ParseException("IBKR 记录日期超出其报告期间，请核对导出文件")

    def _trades(self, statement):
        executions = 0
        other_levels = 0
        for row, element in enumerate(statement.findall("./Trades/*"), 1):
            a = element.attrib
            level = a.get("levelOfDetail", "").upper()
            if element.tag != "Trade" or level != "EXECUTION":
                if element.tag in {"Order", "SymbolSummary", "AssetSummary", "Lot", "ClosedLot", "WashSale"} or level in {"ORDER", "SYMBOL_SUMMARY", "ASSET_SUMMARY", "CLOSED_LOT", "WASH_SALE"}:
                    other_levels += 1
                    continue
                self._issue("UNKNOWN_TRADE_LEVEL", "交易层级不明确；只计算 EXECUTION。", a, "Trades", row)
                continue
            executions += 1
            self._account(a)
            record_id = required(a, "tradeID")
            stamp = flex_time(a["dateTime"]) if a.get("dateTime") else flex_time(required(a, "tradeDate") + ";" + required(a, "tradeTime"))
            self._check_date(stamp.date())
            if a.get("tradeDate") and flex_date(a["tradeDate"]) != stamp.date():
                raise ParseException("IBKR tradeDate 与 dateTime 不一致")
            fields = ("assetCategory", "conid", "symbol", "currency", "buySell", "quantity", "tradePrice", "ibCommission", "ibCommissionCurrency", "taxes", "multiplier", "proceeds", "netCash", "openCloseIndicator", "transactionType", "origTradeID", "notes")
            numeric = {"quantity", "tradePrice", "ibCommission", "taxes", "multiplier", "proceeds", "netCash"}
            reported_pnl = number(a, "fifoPnlRealized") if a.get("fifoPnlRealized", "").strip() else None
            canonical = (stamp, tuple((k, number(a, k) if k in numeric and a.get(k) else a.get(k, "")) for k in fields), reported_pnl)
            if not self._dedup("Trade", record_id, canonical):
                continue
            # 未支持资产的卖出仍保留券商收益，避免对账清单遗漏处置记录。
            if a.get("buySell") == "SELL":
                self.data.realized_pnl.append(BrokerRealizedPnl(
                    self.account, a.get("conid", ""), a.get("symbol", ""), required(a, "currency"),
                    stamp.date(), abs(number(a, "quantity")), record_id, self.name, row, reported_pnl,
                    asset_category=a.get("assetCategory", "")))
            if a.get("assetCategory") not in {"STK", "ETF"}:
                self._issue("UNSUPPORTED_ASSET", "该资产类别尚未纳入股票 FIFO；成交已单独保留待复核。", a, "Trades", row)
                continue
            unsupported = False
            unsupported |= a.get("origTradeID", "") not in {"", "0"}
            unsupported |= bool(set(re.split("[;,]", a.get("notes", ""))) & {"Ca", "Co"})
            unsupported |= a.get("transactionType", "ExchTrade") != "ExchTrade"
            unsupported |= (a.get("buySell") == "SELL" and a.get("openCloseIndicator") == "O")
            unsupported |= (a.get("buySell") == "BUY" and a.get("openCloseIndicator") == "C")
            if unsupported:
                self._issue("UNSUPPORTED_TRANSFER", "仅支持股票／ETF 多头普通成交；更正、做空或其他资产需复核。", a, "Trades", row)
                continue
            if required(a, "openCloseIndicator") not in {"O", "C"}:
                self._issue("UNSUPPORTED_TRANSFER", "开平仓标识不明确，需要复核。", a, "Trades", row)
                continue
            symbol, conid, currency = required(a, "symbol"), required(a, "conid"), required(a, "currency")
            qty, price = number(a, "quantity"), number(a, "tradePrice")
            commission, taxes, multiplier = number(a, "ibCommission"), number(a, "taxes"), number(a, "multiplier")
            side = required(a, "buySell")
            if side not in {"BUY", "SELL"} or qty == 0 or price <= 0 or (side == "BUY") != (qty > 0):
                raise ParseException("IBKR 买卖方向、成交数量符号或价格无效")
            if multiplier != 1 or taxes != 0 or commission > 0 or required(a, "ibCommissionCurrency") != currency:
                self._issue("UNSUPPORTED_TRANSFER", "存在非单位乘数、交易税、佣金返还或异币佣金，此成交成本需复核。", a, "Trades", row)
                continue
            proceeds, net = number(a, "proceeds"), number(a, "netCash")
            if abs(proceeds + qty * price) > Decimal("0.02") or abs(net - proceeds - commission - taxes) > Decimal("0.02"):
                self._issue("UNSUPPORTED_TRANSFER", "成交金额或净现金与数量、价格、费用不一致。", a, "Trades", row)
                continue
            self.data.trades.append(TradeRecord(row, stamp.date(), self.account, self.account, "证券", symbol,
                a.get("listingExchange", ""), side, flex_date(a["settleDateTarget"]) if a.get("settleDateTarget") else None,
                currency, abs(qty), price, abs(proceeds), -commission, net, broker="IBKR", instrument_id=conid,
                trade_time=stamp, record_id=record_id, source_file=self.name))
        if other_levels:
            self.data.warnings.append(f"{self.name}: 忽略 {other_levels} 条订单、汇总或平仓批次，避免重复计入成交。")
            if not executions:
                self._issue("MISSING_EXECUTIONS", "Trades 只有汇总层级，请勾选 Executions。", section="Trades")

    def _cash(self, statement):
        for row, element in enumerate(statement.findall("./CashTransactions/*"), 1):
            a = element.attrib
            if element.tag != "CashTransaction":
                self._issue("UNKNOWN_CASH", "无法识别的现金记录类型。", a, "CashTransactions", row)
                continue
            self._account(a)
            if a.get("levelOfDetail", "DETAIL") != "DETAIL":
                self._issue("UNKNOWN_CASH_LEVEL", "现金记录必须为 DETAIL 明细，汇总未计入。", a, "CashTransactions", row)
                continue
            record_id = required(a, "transactionID")
            raw_date = required(a, "dateTime")
            stamp = datetime.combine(flex_date(raw_date), datetime.min.time()) if len(raw_date) in (8, 10) else flex_time(raw_date)
            self._check_date(stamp.date())
            kind, currency, amount = required(a, "type"), required(a, "currency"), number(a, "amount")
            if not self._dedup("Cash", record_id, (stamp, kind, currency, amount, a.get("conid", ""), a.get("symbol", ""))):
                continue
            self.data.cash_events.append(CashEvent(self.account, stamp.date(), kind, currency, amount, record_id, self.name, row, a.get("symbol", "")))
            group = (self.account, stamp.year, currency)
            if kind == "Dividends":
                self.income[group][0] += amount
                self.data.dividends_received.append(DividendReceivedRecord(stamp.date(), self.account, self.account, currency, amount, a.get("symbol", ""), a.get("description", "")))
            elif kind in {"Broker Interest Received", "Bond Interest Received"}:
                self.income[group][1] += amount
            elif kind == "Withholding Tax":
                self.data.withholding.append(WithholdingRecord(stamp.date(), currency, -amount, self.account, row, a.get("description", "")))
            elif kind in {"Deposits/Withdrawals", "Deposits & Withdrawals"}:
                if amount > 0:
                    self.data.deposits.append(DepositRecord(stamp.date(), self.account, self.account, currency, amount))
            else:
                self._issue("UNSUPPORTED_CASH", f"现金类型 {kind} 未自动计税或抵扣，原始金额保留在现金明细。", a, "CashTransactions", row)

    def _reconcile_cash_report(self, statement):
        report = statement.find("CashReport")
        cash = statement.find("CashTransactions")
        if report is None or cash is None:
            return
        totals = defaultdict(lambda: defaultdict(lambda: ZERO))
        for element in cash.findall("CashTransaction"):
            a = element.attrib
            if a.get("levelOfDetail", "DETAIL") == "DETAIL":
                totals[required(a, "currency")][required(a, "type")] += number(a, "amount")
        fields = {
            "dividends": ("Dividends",),
            "brokerInterest": ("Broker Interest Received", "Broker Interest Paid"),
            "withholdingTax": ("Withholding Tax",),
        }
        seen = set()
        for row, element in enumerate(report.findall("CashReportCurrency"), 1):
            a = element.attrib
            if a.get("levelOfDetail") != "Currency":
                continue  # 基础币种汇总不是原币金额，不能与原币现金明细直接比较。
            self._account(a)
            currency = required(a, "currency")
            if currency in seen:
                raise ParseException("CashReport 同一币种存在重复的原币汇总")
            seen.add(currency)
            if (a.get("fromDate") and flex_date(a["fromDate"]) != self.start) or (
                    a.get("toDate") and flex_date(a["toDate"]) != self.end):
                self._issue("CASH_REPORT_PERIOD_MISMATCH", "CashReport 与活动报告期间不一致。",
                            a, "CashReport", row, date=self.end)
                continue
            for field, kinds in fields.items():
                detailed = sum((totals[currency][kind] for kind in kinds), ZERO)
                if not a.get(field, "").strip():
                    if detailed != ZERO:
                        self._issue("CASH_REPORT_MISSING_FIELD",
                                    f"CashReport 缺少 {field}，无法核对原币现金明细 {detailed}。",
                                    a, "CashReport", row, date=self.end)
                    continue
                reported = number(a, field)
                if abs(reported - detailed) > Decimal("0.02"):
                    self._issue("CASH_REPORT_MISMATCH",
                                f"{field} 原币汇总 {reported} 与现金明细 {detailed} 不一致。",
                                a, "CashReport", row, date=self.end)
        # 有现金明细却缺少对应原币汇总时，不得把已显示的汇总当作完整对账。
        for currency, by_type in totals.items():
            if currency not in seen and any(by_type[kind] != ZERO for kinds in fields.values() for kind in kinds):
                self._issue("CASH_REPORT_MISSING_CURRENCY",
                            f"CashReport 缺少 {currency} 原币汇总，无法核对现金明细。",
                            {"currency": currency}, "CashReport", date=self.end)

    def _closing(self, statement):
        rows = statement.findall("./OpenPositions/OpenPosition")
        snapshot = {}
        # Prefer summary; LOT records are only used by the separate opening input.
        for row, element in enumerate(rows, 1):
            a = element.attrib
            if a.get("levelOfDetail") != "SUMMARY":
                continue
            self._account(a)
            if a.get("assetCategory") not in {"STK", "ETF"}:
                self._issue("UNSUPPORTED_POSITION", "期末存在未支持资产。", a, "OpenPositions", row)
                continue
            conid, currency = required(a, "conid"), required(a, "currency")
            if a.get("reportDate") and flex_date(a["reportDate"]) != self.end:
                raise ParseException("OpenPositions 的报告日期与文件截止日期不一致")
            qty = number(a, "position")
            if required(a, "side").upper() != "LONG" or qty < 0:
                self._issue("UNSUPPORTED_TRANSFER", "期末存在空头持仓。", a, "OpenPositions", row)
                continue
            key = (self.account, conid, currency, self.end)
            record = PositionRecord("期末", self.end, "证券", self.account, self.account, required(a, "symbol"),
                a.get("listingExchange", ""), currency, qty, ZERO, ZERO, broker="IBKR", instrument_id=conid)
            if key in self.closing and self.closing[key].quantity != qty:
                raise ParseException("同账户同日的期末持仓数量冲突")
            self.closing[key] = record
            snapshot[(conid, currency)] = qty
        if rows and not any(e.get("levelOfDetail") == "SUMMARY" for e in rows):
            self._issue("MISSING_POSITION_SUMMARY", "年度报告的 OpenPositions 请包含 SUMMARY 以便期末数量核对。", section="OpenPositions")

        if statement.find("OpenPositions") is not None and (not rows or any(e.get("levelOfDetail") == "SUMMARY" for e in rows)):
            snapshot_key = (self.account, self.end)
            if snapshot_key in self.closing_snapshots and self.closing_snapshots[snapshot_key] != snapshot:
                raise ParseException("同账户同日的完整期末持仓快照冲突，请核对报告过滤或更正版本")
            self.closing_snapshots[snapshot_key] = snapshot

    def _opening(self, statement):
        section = statement.find("OpenPositions")
        if section is None:
            raise ParseException("期初成本文件缺少 OpenPositions")
        lots = section.findall("OpenPosition")
        if any(e.tag != "OpenPosition" for e in section):
            raise ParseException("期初 OpenPositions 包含无法识别的记录")
        actual_lots = [e for e in lots if e.get("levelOfDetail") == "LOT"]
        if any(e.get("levelOfDetail") not in {"LOT", "SUMMARY"} for e in lots):
            raise ParseException("期初持仓层级必须是 LOT 或 SUMMARY")
        lot_totals = defaultdict(lambda: [ZERO, ZERO])
        if lots and not actual_lots:
            raise ParseException("期初成本需 LOT 批次明细，不能使用 SUMMARY 平均成本")
        for row, element in enumerate(actual_lots, 1):
            a = element.attrib
            self._account(a)
            if a.get("assetCategory") not in {"STK", "ETF"} or required(a, "side").upper() != "LONG":
                raise ParseException("期初成本首版只支持股票／ETF 多头批次")
            if a.get("reportDate") and flex_date(a["reportDate"]) != self.end:
                raise ParseException("期初批次报告日期与快照截止日期不一致")
            qty, cost = number(a, "position"), number(a, "costBasisMoney")
            if qty <= 0 or cost < 0 or number(a, "multiplier") != 1:
                raise ParseException("期初批次数量、成本或乘数无效")
            buy_time = flex_time(required(a, "openDateTime"))
            if buy_time.date() > self.end:
                raise ParseException("期初批次买入时间晚于快照日期")
            conid, currency = required(a, "conid"), required(a, "currency")
            record_id = required(a, "originatingTransactionID")
            key = (self.account, conid, currency, record_id)
            if key in self.opening_seen:
                raise ParseException("期初批次 ID 重复，请核对报告")
            self.opening_seen.add(key)
            lot_totals[(conid, currency)][0] += qty
            lot_totals[(conid, currency)][1] += cost
            self.data.opening_lots.append(OpeningLotRecord(self.account, required(a, "symbol"), conid, currency,
                qty, cost, buy_time, self.end, row, record_id, self.name, market=a.get("listingExchange", "")))

        for element in lots:
            a = element.attrib
            if a.get("levelOfDetail") != "SUMMARY":
                continue
            self._account(a)
            totals = lot_totals[(required(a, "conid"), required(a, "currency"))]
            if totals[0] != number(a, "position") or abs(totals[1] - number(a, "costBasisMoney")) > Decimal("0.02"):
                raise ParseException("期初 LOT 批次数量或成本与 SUMMARY 汇总不一致")
