"""DividendReceivedRecord — parsed from capital flows (证券-资金进出), type=公司行动 In."""

from dataclasses import dataclass
from datetime import date
from decimal import Decimal


@dataclass
class DividendReceivedRecord:
    date: date
    account_name: str
    account_no: str
    currency: str
    amount: Decimal
    symbol: str
    description: str
