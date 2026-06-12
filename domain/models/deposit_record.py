"""DepositRecord — parsed from capital flows sheet (证券-资金进出), deposit entries only."""

from dataclasses import dataclass
from datetime import date
from decimal import Decimal


@dataclass
class DepositRecord:
    date: date
    account_name: str
    account_no: str
    currency: str
    amount: Decimal
