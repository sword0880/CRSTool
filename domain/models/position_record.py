"""PositionRecord — parsed from position overview sheet (证券-持仓总览)."""

from dataclasses import dataclass
from datetime import date
from decimal import Decimal
from typing import Optional


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
    broker: str = "FUTU"
    instrument_id: str = ""
    # Only populate from a verified historical cost source, never market price.
    cost_basis_price: Optional[Decimal] = None
