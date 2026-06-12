"""Futu trade history parser — reads trade records, asset transfers, and capital flows."""

import re
from datetime import datetime, date
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from typing import List, Dict, Tuple, Optional

import pandas as pd

from domain.models.trade_record import TradeRecord
from domain.models.position_record import PositionRecord
from domain.models.asset_transfer import AssetTransferRecord
from domain.models.deposit_record import DepositRecord
from domain.models.dividend_received import DividendReceivedRecord
from domain.models.exceptions import ParseException
from domain.models.broker_import import BrokerImportResult, WithholdingRecord
from domain.models.calculation_issue import CalculationIssue


DIRECTION_MAP = {
    "买入开仓": "BUY",
    "卖出平仓": "SELL",
    "买入": "BUY",
    "卖出": "SELL",
    "BUY": "BUY",
    "SELL": "SELL",
}

SUPPORTED_CURRENCIES = {"USD", "HKD", "CNY"}


def _to_decimal(value, field_name: str) -> Decimal:
    if value is None or (isinstance(value, str) and value.strip() in ("", "-")):
        return Decimal("0")
    try:
        number = Decimal(str(value).strip().replace(",", ""))
        if not number.is_finite():
            raise ParseException(f"字段【{field_name}】必须是有限数字")
        return number
    except InvalidOperation:
        raise ParseException(f"字段【{field_name}】的值 '{value}' 无法转换为数字")


def _parse_date(value, field_name: str) -> date:
    """Parse a date value that may be datetime, string YYYY-MM-DD HH:MM:SS, or YYYYMMDD."""
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    s = str(value).strip()
    # Try YYYY-MM-DD HH:MM:SS
    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d", "%Y%m%d"):
        try:
            return datetime.strptime(s, fmt).date()
        except ValueError:
            continue
    raise ParseException(f"字段【{field_name}】的日期 '{value}' 格式无法识别")


def _parse_trade_time(value):
    if isinstance(value, datetime):
        return value
    text = str(value).strip()
    if " " not in text and "T" not in text:
        return None
    try:
        return datetime.fromisoformat(text)
    except ValueError:
        raise ParseException(f"成交时间无法识别: {text}")


class FutuTradeParser:
    """Parse the annual trade history file (e.g. 2021_717110.xlsx).

    Reads:
    - Sheet "证券-交易流水" -> List[TradeRecord]
    - Sheet "证券-持仓总览" -> List[PositionRecord]
    - Sheet "证券-资产进出" -> List[AssetTransferRecord]
    - Sheet "证券-资金进出" -> capital flows (withholding tax extraction)
    """

    def __init__(self):
        self.warnings: List[str] = []
        self.issues: List[CalculationIssue] = []
        self._symbol_map: Dict[str, str] = {}  # old -> new symbol mapping

    def _open_excel(self, file_path) -> pd.ExcelFile:
        """Open Excel file from path or file-like object."""
        if isinstance(file_path, (str, Path)):
            path = Path(file_path)
            if not path.exists():
                raise ParseException(f"文件不存在: {path}")
            source = path
        else:
            source = file_path
        try:
            return pd.ExcelFile(source, engine="openpyxl")
        except Exception as e:
            raise ParseException(f"无法打开 Excel 文件: {e}")

    def _read_sheets(self, file_path):
        with self._open_excel(file_path) as xls:
            return {name: pd.read_excel(xls, sheet_name=name, dtype=str).fillna("")
                    for name in xls.sheet_names}

    def parse_all(self, file_path) -> BrokerImportResult:
        self.warnings = []
        self.issues = []
        sheets = self._read_sheets(file_path)
        self._symbol_map = self._parse_asset_transfers(sheets)
        report_years = []
        account_sheet = self._find_sheet(sheets, "账户信息")
        if account_sheet is not None and "年份" in sheets[account_sheet]:
            report_years = sorted({int(y) for y in sheets[account_sheet]["年份"] if str(y).isdigit()})
        trades = self._parse_trades(sheets)
        positions = self._parse_positions(sheets)
        withholding = self._parse_withholding_records(sheets)
        if self._find_sheet(sheets, "资金进出") is None:
            self.issues.append(CalculationIssue("MISSING_CASH_FLOWS", "缺少资金进出表，无法确认预扣税及现金收入覆盖范围。"))
        return BrokerImportResult(
            trades=trades, positions=positions, withholding=withholding,
            deposits=self._parse_deposits(sheets),
            dividends_received=self._parse_dividends_received(sheets),
            warnings=list(self.warnings), issues=list(self.issues),
            trade_file_present=True, report_years=report_years,
        )

    def parse(self, file_path) -> List[TradeRecord]:
        self.warnings = []
        self.issues = []
        sheets = self._read_sheets(file_path)
        self._symbol_map = self._parse_asset_transfers(sheets)
        return self._parse_trades(sheets)

    def parse_positions(self, file_path) -> List[PositionRecord]:
        return self._parse_positions(self._read_sheets(file_path))

    def parse_capital_flows(self, file_path, tax_year=None) -> Dict[str, Decimal]:
        result = {}
        for record in self._parse_withholding_records(self._read_sheets(file_path)):
            if tax_year is None or record.date.year == tax_year:
                result[record.currency] = result.get(record.currency, Decimal("0")) + record.amount
        return result

    def parse_deposits(self, file_path) -> List[DepositRecord]:
        return self._parse_deposits(self._read_sheets(file_path))

    def parse_dividends_received(self, file_path) -> List[DividendReceivedRecord]:
        return self._parse_dividends_received(self._read_sheets(file_path))

    def _find_sheet(self, sheets, keyword):
        names = [name for name in sheets if keyword in name]
        if len(names) > 1:
            raise ParseException(f"存在多个包含 {keyword} 的工作表，请明确文件范围")
        return names[0] if names else None

    def _parse_trades(self, xls: pd.ExcelFile) -> List[TradeRecord]:
        sheet_name = self._find_sheet(xls, "交易流水")
        if sheet_name is None:
            raise ParseException("未找到'交易流水' Sheet")

        df = xls[sheet_name]
        if df.empty:
            return []

        required_cols = ["成交时间", "品类", "代码名称", "方向", "数量/面值", "价格", "币种"]
        missing = [c for c in required_cols if c not in df.columns]
        if missing:
            raise ParseException(f"交易流水缺少字段: {', '.join(missing)}")

        records: List[TradeRecord] = []
        skipped_fund = 0

        for idx, row in df.iterrows():
            excel_row = idx + 2  # 1-based + header row

            # Category filter
            category = str(row.get("品类", "")).strip()
            if category == "基金":
                skipped_fund += 1
                self.issues.append(CalculationIssue("UNSUPPORTED_ASSET", f"行 {excel_row}: 基金记录不在当前计算范围。",
                    source_row=excel_row, date=_parse_date(row["成交时间"], "成交时间"), source_sheet="交易流水",
                    account=str(row.get("账户号码", "")), symbol=str(row.get("代码名称", "")), currency=str(row.get("币种", ""))))
                continue
            if category != "证券":
                self.issues.append(CalculationIssue("UNSUPPORTED_ASSET", f"行 {excel_row}: 未支持品类 {category}。",
                    source_row=excel_row, date=_parse_date(row["成交时间"], "成交时间"), source_sheet="交易流水",
                    account=str(row.get("账户号码", "")), symbol=str(row.get("代码名称", "")), currency=str(row.get("币种", ""))))
                continue

            # Direction mapping
            raw_direction = str(row["方向"]).strip()
            side = DIRECTION_MAP.get(raw_direction)
            if side is None:
                raise ParseException(f"行 {excel_row}: 无法识别的买卖方向 '{raw_direction}'")

            # Symbol rename
            symbol = str(row["代码名称"]).strip().upper()
            symbol = self._symbol_map.get(symbol, symbol)

            # Quantity: take absolute value
            raw_qty = _to_decimal(row["数量/面值"], "数量/面值")
            quantity = abs(raw_qty)
            if quantity == 0:
                self.issues.append(CalculationIssue("ZERO_QUANTITY", f"行 {excel_row}: 数量为零或缺失。",
                    source_row=excel_row, date=_parse_date(row["成交时间"], "成交时间"), source_sheet="交易流水",
                    account=str(row.get("账户号码", "")), symbol=str(row.get("代码名称", "")), currency=str(row.get("币种", ""))))
                continue

            price = _to_decimal(row["价格"], "价格")
            if price <= 0:
                raise ParseException(f"行 {excel_row}: 成交价格必须大于 0")

            # Optional fields
            account_name = str(row.get("账户名称", "")).strip()
            account_no = str(row.get("账户号码", "")).strip()
            market = str(row.get("交易所/市场", "")).strip()
            currency = str(row["币种"]).strip().upper()
            if currency not in SUPPORTED_CURRENCIES:
                raise ParseException(f"行 {excel_row}: 暂不支持币种 {currency}")

            settle_date = None
            raw_settle = row.get("交收日期")
            if raw_settle is not None and str(raw_settle).strip() not in ("", "-"):
                settle_date = _parse_date(raw_settle, "交收日期")

            commission = Decimal("0")
            raw_comm = row.get("总费用")
            if raw_comm is not None and str(raw_comm).strip() not in ("", "-"):
                commission = abs(_to_decimal(raw_comm, "总费用"))

            trade_amount = Decimal("0")
            raw_ta = row.get("成交金额")
            if raw_ta is not None and str(raw_ta).strip() not in ("", "-"):
                trade_amount = _to_decimal(raw_ta, "成交金额")

            net_amount = Decimal("0")
            raw_net = row.get("变动金额")
            if raw_net is not None and str(raw_net).strip() not in ("", "-"):
                net_amount = _to_decimal(raw_net, "变动金额")

            trade_date = _parse_date(row["成交时间"], "成交时间")

            expected_gross = (quantity * price).quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)
            if side == "BUY":
                expected_gross = -expected_gross
            discrepancies = []
            if raw_ta is not None and str(raw_ta).strip() not in ("", "-"):
                if abs(trade_amount - expected_gross) > Decimal("0.01"):
                    discrepancies.append(f"成交金额 {trade_amount}，按数量与价格计算 {expected_gross}")
            if raw_net is not None and str(raw_net).strip() not in ("", "-"):
                expected_net = (trade_amount if raw_ta is not None and str(raw_ta).strip() not in ("", "-")
                                else expected_gross) - commission
                if abs(net_amount - expected_net) > Decimal("0.01"):
                    discrepancies.append(f"变动金额 {net_amount}，按成交与费用计算 {expected_net}")
            if discrepancies:
                self.issues.append(CalculationIssue(
                    "TRADE_AMOUNT_MISMATCH", f"行 {excel_row}: " + "；".join(discrepancies) + "，请核对券商原始金额。",
                    account=account_no, symbol=symbol, currency=currency, date=trade_date,
                    source_sheet="交易流水", source_row=excel_row, quantity=quantity,
                ))

            records.append(TradeRecord(
                source_row=excel_row,
                trade_date=trade_date,
                trade_time=_parse_trade_time(row["成交时间"]),
                account_name=account_name,
                account_no=account_no,
                category="证券",
                symbol=symbol,
                market=market,
                side=side,
                settle_date=settle_date,
                currency=currency,
                quantity=quantity,
                price=price,
                trade_amount=trade_amount,
                commission=commission,
                net_amount=net_amount,
            ))

        if skipped_fund > 0:
            self.warnings.append(f"检测到 {skipped_fund} 条基金交易记录，已跳过")

        return records

    def _parse_positions(self, xls: pd.ExcelFile) -> List[PositionRecord]:
        sheet_name = self._find_sheet(xls, "持仓总览")
        if sheet_name is None:
            return []

        df = xls[sheet_name]
        records: List[PositionRecord] = []

        for _, row in df.iterrows():
            raw_date = row.get("日期")
            if raw_date is None or str(raw_date).strip() in ("", "-"):
                continue

            period_type = str(row.get("时期类型", "")).strip()
            category = str(row.get("品类", "")).strip()

            symbol = str(row.get("代码名称", "")).strip().upper()
            symbol = self._symbol_map.get(symbol, symbol)

            records.append(PositionRecord(
                period_type=period_type,
                date=_parse_date(raw_date, "日期"),
                category=category,
                account_name=str(row.get("账户名称", "")).strip(),
                account_no=str(row.get("账户号码", "")).strip(),
                symbol=symbol,
                market=str(row.get("交易所/市场", "")).strip(),
                currency=str(row.get("币种", "")).strip().upper(),
                quantity=_to_decimal(row.get("数量/面值", 0), "数量/面值"),
                price=_to_decimal(row.get("价格", 0), "价格"),
                market_value=_to_decimal(row.get("市值", 0), "市值"),
            ))

        return records

    def _parse_asset_transfers(self, xls) -> Dict[str, str]:
        # Date-only pairing can associate unrelated corporate actions. Until
        # event IDs and cost adjustments are supported, require reconciliation.
        sheet_name = self._find_sheet(xls, "资产进出")
        if sheet_name is not None:
            for idx, row in xls[sheet_name].iterrows():
                self.issues.append(CalculationIssue(
                    "UNSUPPORTED_TRANSFER", "资产进出或公司行动尚未核对，请确认证券身份、数量和成本变化。",
                    account=str(row.get("账户号码", "")), symbol=str(row.get("代码名称", "")).upper(),
                    currency=str(row.get("币种", "")).upper(), source_row=idx + 2,
                    date=_parse_date(row.get("日期", ""), "日期"), source_sheet="资产进出",
                ))
        return {}

    def _parse_withholding_records(self, xls):
        sheet_name = self._find_sheet(xls, "资金进出")
        if sheet_name is None:
            return []
        records = []
        for idx, row in xls[sheet_name].iterrows():
            remarks = str(row.get("备注", "")).strip()
            if "withholding tax" not in remarks.lower():
                continue
            if str(row.get("变动金额", "")).strip() in ("", "-", "—"):
                raise ParseException(f"行 {idx + 2}: 预扣税金额缺失")
            # Cash outflow is negative; reversing it yields positive tax paid.
            records.append(WithholdingRecord(
                date=_parse_date(row.get("日期", ""), "扣税日期"),
                currency=str(row.get("币种", "")).strip().upper(),
                amount=-_to_decimal(row["变动金额"], "变动金额"),
                account_no=str(row.get("账户号码", "")).strip(),
                source_row=idx + 2, description=remarks,
            ))
        return records

    def _parse_deposits(self, xls: pd.ExcelFile) -> List[DepositRecord]:
        """Extract deposit records from capital flows (方向=In, 类型=出入金)."""
        sheet_name = self._find_sheet(xls, "资金进出")
        if sheet_name is None:
            return []

        df = xls[sheet_name]
        deposits: List[DepositRecord] = []

        for _, row in df.iterrows():
            direction = str(row.get("方向", "")).strip()
            flow_type = str(row.get("类型", "")).strip()
            if direction != "In" or flow_type != "出入金":
                continue

            raw_date = row.get("日期")
            if raw_date is None or str(raw_date).strip() in ("", "-"):
                continue

            deposits.append(DepositRecord(
                date=_parse_date(raw_date, "日期"),
                account_name=str(row.get("账户名称", "")).strip(),
                account_no=str(row.get("账户号码", "")).strip(),
                currency=str(row.get("币种", "")).strip().upper(),
                amount=_to_decimal(row.get("变动金额", 0), "变动金额"),
            ))

        return deposits

    def _parse_dividends_received(self, xls: pd.ExcelFile) -> List[DividendReceivedRecord]:
        """Extract received dividends from capital flows.

        Matches type containing '公司行动' (e.g. '公司行动', '公司行动(港A股分红)', '公司行动(美股分红)')
        and direction=In.
        """
        sheet_name = self._find_sheet(xls, "资金进出")
        if sheet_name is None:
            return []

        df = xls[sheet_name]
        records: List[DividendReceivedRecord] = []

        for _, row in df.iterrows():
            direction = str(row.get("方向", "")).strip()
            flow_type = str(row.get("类型", "")).strip()
            if direction != "In" or "公司行动" not in flow_type:
                continue

            raw_date = row.get("日期")
            if raw_date is None or str(raw_date).strip() in ("", "-"):
                continue

            remarks = str(row.get("备注", "")).strip()
            symbol = self._extract_symbol_from_remarks(remarks)

            records.append(DividendReceivedRecord(
                date=_parse_date(raw_date, "日期"),
                account_name=str(row.get("账户名称", "")).strip(),
                account_no=str(row.get("账户号码", "")).strip(),
                currency=str(row.get("币种", "")).strip().upper(),
                amount=_to_decimal(row.get("变动金额", 0), "变动金额"),
                symbol=symbol,
                description=remarks,
            ))

        return records

    @staticmethod
    def _extract_symbol_from_remarks(remarks: str) -> str:
        """Extract stock symbol/dividend company from remarks text.

        Handles two formats:
        - US: "FUTU 20.00000000 SHARES DIVIDENDS ..." -> "FUTU"
        - HK: "<SEHK 700 TENCENT> 50 shares" -> "700 TENCENT"
        - HK: "24 F/D-HKD4.5/SH <SEHK 700 TENCENT> 50 shares" -> "700 TENCENT"
        """
        # Try angle bracket format first: <SEHK 700 TENCENT>
        match = re.search(r"<[A-Z]+\s+\d+\s+(.+?)>", remarks)
        if match:
            # Extract the code and name from within brackets
            bracket_content = re.search(r"<([A-Z]+\s+\d+\s+.+?)>", remarks)
            if bracket_content:
                inner = bracket_content.group(1)
                # Return "CODE NAME" part
                parts = inner.split()
                if len(parts) >= 3:
                    return f"{parts[1]} {' '.join(parts[2:])}"
                return inner

        # Try US format: "FUTU 20.00000000 SHARES DIVIDENDS ..."
        match = re.match(r"^([A-Z]+)\s+[\d.]+\s+SHARES?\s+", remarks)
        if match:
            return match.group(1)

        return remarks[:30] if remarks else ""
