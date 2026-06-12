"""TradeRecord — parsed from trade history sheet (证券-交易流水)."""

from dataclasses import dataclass
from datetime import date, datetime
from decimal import Decimal
from typing import Optional


@dataclass
class TradeRecord:
    source_row: int
    trade_date: date
    account_name: str
    account_no: str
    category: str
    symbol: str
    market: str
    side: str  # BUY / SELL
    settle_date: date
    currency: str
    quantity: Decimal  # absolute value
    price: Decimal
    trade_amount: Decimal
    commission: Decimal
    net_amount: Decimal
    broker: str = "FUTU"
    instrument_id: str = ""
    trade_time: Optional[datetime] = None
    record_id: str = ""
    source_file: str = ""
