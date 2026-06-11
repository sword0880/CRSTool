"""DividendIncomeRecord — parsed from dividend tax sheet (account-level annual summary)."""

from dataclasses import dataclass
from decimal import Decimal


@dataclass
class DividendIncomeRecord:
    account_name: str
    account_no: str
    year: int
    dividend: Decimal
    interest: Decimal
    other_income: Decimal
    currency: str
