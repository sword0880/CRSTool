"""Traceable data gaps; an issue means the result is only a partial calculation."""

from dataclasses import dataclass
from datetime import date
from decimal import Decimal
from typing import Optional


@dataclass
class CalculationIssue:
    code: str
    message: str
    account: str = ""
    symbol: str = ""
    currency: str = ""
    source_row: int = 0
    date: Optional[date] = None
    quantity: Decimal = Decimal("0")
    source_sheet: str = ""
    source_file: str = ""
    record_id: str = ""
    instrument_id: str = ""
    source_start: Optional[date] = None
    source_end: Optional[date] = None


def inventory_key(record):
    """A broker-local instrument ID takes priority over the display symbol."""
    account = record.account_no or record.account_name
    instrument = record.instrument_id or (record.market, record.symbol)
    return record.broker, account, instrument, record.currency
