"""TaxSummary and ExportBundle — final calculation results."""

from dataclasses import dataclass, field
from decimal import Decimal
from typing import List

from domain.models.dividend_income import DividendIncomeRecord
from domain.models.match_record import MatchRecord
from domain.models.deposit_record import DepositRecord
from domain.models.dividend_received import DividendReceivedRecord


@dataclass
class TaxSummary:
    dividend_income_cny: Decimal = Decimal("0")
    interest_income_cny: Decimal = Decimal("0")
    capital_gain_cny: Decimal = Decimal("0")
    dividend_interest_tax: Decimal = Decimal("0")
    capital_gain_tax: Decimal = Decimal("0")
    foreign_tax_credit: Decimal = Decimal("0")
    dividend_interest_supplement: Decimal = Decimal("0")
    capital_gain_supplement: Decimal = Decimal("0")
    total_supplement_tax: Decimal = Decimal("0")


@dataclass
class ExportBundle:
    tax_summary: TaxSummary
    match_records: List[MatchRecord]
    tax_year: int
    dividend_details: List[DividendIncomeRecord]
    deposits: List[DepositRecord] = field(default_factory=list)
    dividends_received: List[DividendReceivedRecord] = field(default_factory=list)
