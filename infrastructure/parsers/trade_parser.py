"""Futu trade history parser — reads trade records, asset transfers, and capital flows."""

from datetime import datetime, date
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import List, Dict, Tuple, Optional

import pandas as pd

from domain.models.trade_record import TradeRecord
from domain.models.position_record import PositionRecord
from domain.models.asset_transfer import AssetTransferRecord
from domain.models.exceptions import ParseException


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
        return Decimal(str(value).strip().replace(",", ""))
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

    def parse(self, file_path) -> List[TradeRecord]:
        xls = self._open_excel(file_path)
        # Parse asset transfers first to build symbol rename map
        self._symbol_map = self._parse_asset_transfers(xls)
        # Parse trades
        trades = self._parse_trades(xls)
        return trades

    def parse_positions(self, file_path) -> List[PositionRecord]:
        xls = self._open_excel(file_path)
        return self._parse_positions(xls)

    def parse_capital_flows(self, file_path) -> Dict[str, Decimal]:
        """Parse capital flows and extract withholding tax by currency."""
        xls = self._open_excel(file_path)
        return self._parse_withholding_tax(xls)

    def _find_sheet(self, xls: pd.ExcelFile, keyword: str) -> Optional[str]:
        for name in xls.sheet_names:
            if keyword in name:
                return name
        return None

    def _parse_trades(self, xls: pd.ExcelFile) -> List[TradeRecord]:
        sheet_name = self._find_sheet(xls, "交易流水")
        if sheet_name is None:
            raise ParseException("未找到'交易流水' Sheet")

        df = pd.read_excel(xls, sheet_name=sheet_name, engine="openpyxl")
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
                continue
            if category != "证券":
                self.warnings.append(f"行 {excel_row}: 未知品类 '{category}'，已跳过")
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
                self.warnings.append(f"行 {excel_row}: 数量为 0，已跳过")
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

            records.append(TradeRecord(
                source_row=excel_row,
                trade_date=trade_date,
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

        df = pd.read_excel(xls, sheet_name=sheet_name, engine="openpyxl")
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

    def _parse_asset_transfers(self, xls: pd.ExcelFile) -> Dict[str, str]:
        """Parse asset transfers and return symbol rename map {old: new}."""
        sheet_name = self._find_sheet(xls, "资产进出")
        if sheet_name is None:
            return {}

        df = pd.read_excel(xls, sheet_name=sheet_name, engine="openpyxl")
        rename_map: Dict[str, str] = {}

        # Find paired SYMBOL CHANGE records
        change_records = []
        for _, row in df.iterrows():
            remarks = str(row.get("备注", "")).strip()
            if "SYMBOL CHANGE" not in remarks.upper():
                continue
            direction = str(row.get("方向", "")).strip()
            symbol = str(row.get("代码名称", "")).strip().upper()
            change_records.append((direction, symbol, str(row.get("日期", ""))))

        # Pair In/Out records on same date
        for i, (d1, s1, dt1) in enumerate(change_records):
            for j, (d2, s2, dt2) in enumerate(change_records):
                if i >= j:
                    continue
                if dt1 == dt2 and d1 != d2:
                    # One is In (new), one is Out (old)
                    if d1 == "Out" and d2 == "In":
                        rename_map[s1] = s2
                        self.warnings.append(f"检测到股票更名: {s1} → {s2}，已自动关联")
                    elif d1 == "In" and d2 == "Out":
                        rename_map[s2] = s1
                        self.warnings.append(f"检测到股票更名: {s2} → {s1}，已自动关联")

        return rename_map

    def _parse_withholding_tax(self, xls: pd.ExcelFile) -> Dict[str, Decimal]:
        """Extract withholding tax from capital flows sheet."""
        sheet_name = self._find_sheet(xls, "资金进出")
        if sheet_name is None:
            return {}

        df = pd.read_excel(xls, sheet_name=sheet_name, engine="openpyxl")
        tax_by_currency: Dict[str, Decimal] = {}

        for _, row in df.iterrows():
            remarks = str(row.get("备注", "")).strip()
            if "Withholding Tax" not in remarks:
                continue

            currency = str(row.get("币种", "")).strip().upper()
            amount = abs(_to_decimal(row.get("变动金额", 0), "变动金额"))
            tax_by_currency[currency] = tax_by_currency.get(currency, Decimal("0")) + amount

        return tax_by_currency
