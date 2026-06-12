"""MatchRecord (FIFO match detail) and Lot (FIFO inventory unit)."""

from dataclasses import dataclass, field
from datetime import date
from decimal import Decimal
from typing import Optional


@dataclass
class Lot:
    """A single FIFO inventory lot (one buy entry or position)."""
    buy_date: date
    quantity_remaining: Decimal
    price: Decimal
    commission_remaining: Decimal
    currency: str
    source_row: int
    source_file: str = ""
    record_id: str = ""
    cost_remaining: Optional[Decimal] = None


@dataclass
class MatchRecord:
    """One FIFO match result: a sell matched against a buy lot."""
    symbol: str
    sell_date: date
    sell_quantity: Decimal
    buy_date: date
    buy_cost: Decimal
    sell_revenue: Decimal
    buy_commission_alloc: Decimal
    sell_commission_alloc: Decimal
    currency: str
    gain_original: Decimal
    gain_cny: Decimal
    broker: str = "FUTU"
    account_no: str = ""
    account_name: str = ""
    market: str = ""
    instrument_id: str = ""
    buy_source_row: int = 0
    sell_source_row: int = 0
    buy_source_file: str = ""
    sell_source_file: str = ""
    buy_record_id: str = ""
    sell_record_id: str = ""
