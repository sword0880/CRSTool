"""PositionRecord — parsed from position overview sheet (证券-持仓总览)."""

from dataclasses import dataclass
from datetime import date
from decimal import Decimal


@dataclass
class PositionRecord:
    period_type: str  # 期初 / 期末
    date: date
    category: str
    account_name: str
    account_no: str
    symbol: str
    market: str
    currency: str
    quantity: Decimal
    price: Decimal
    market_value: Decimal
