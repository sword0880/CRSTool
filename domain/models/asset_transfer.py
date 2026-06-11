"""AssetTransferRecord — parsed from asset transfer sheet (证券-资产进出)."""

from dataclasses import dataclass
from datetime import date
from decimal import Decimal


@dataclass
class AssetTransferRecord:
    date: date
    account_name: str
    account_no: str
    category: str
    symbol: str
    market: str
    direction: str  # In / Out
    currency: str
    quantity: Decimal  # In=positive, Out=negative
    remarks: str
