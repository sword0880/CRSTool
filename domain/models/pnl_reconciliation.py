"""Broker-reported realized P/L is an audit reference, never a tax input."""
from dataclasses import dataclass
from datetime import date
from decimal import Decimal
from typing import Optional


@dataclass
class BrokerRealizedPnl:
    account: str
    instrument_id: str
    symbol: str
    currency: str
    date: date
    quantity: Decimal
    record_id: str
    source_file: str
    source_row: int
    amount: Optional[Decimal]
    asset_category: str = ""


@dataclass
class PnlReconciliation:
    reported: BrokerRealizedPnl
    calculated: Optional[Decimal]
    difference: Optional[Decimal]
    status: str
    explanation: str
