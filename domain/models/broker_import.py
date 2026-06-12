"""Internal normalized data, not a file users need to obtain from a broker."""

from dataclasses import dataclass, field
from datetime import date, datetime
from decimal import Decimal
from typing import List

from domain.models.calculation_issue import CalculationIssue
from domain.models.dividend_income import DividendIncomeRecord
from domain.models.trade_record import TradeRecord
from domain.models.position_record import PositionRecord
from domain.models.deposit_record import DepositRecord
from domain.models.dividend_received import DividendReceivedRecord


@dataclass
class WithholdingRecord:
    date: date
    currency: str
    amount: Decimal  # positive = tax paid; negative = refund/reversal
    account_no: str = ""
    source_row: int = 0
    description: str = ""


@dataclass
class BrokerImportResult:
    broker: str = "FUTU"
    dividends: List[DividendIncomeRecord] = field(default_factory=list)
    trades: List[TradeRecord] = field(default_factory=list)
    positions: List[PositionRecord] = field(default_factory=list)
    withholding: List[WithholdingRecord] = field(default_factory=list)
    deposits: List[DepositRecord] = field(default_factory=list)
    dividends_received: List[DividendReceivedRecord] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    issues: List[CalculationIssue] = field(default_factory=list)
    trade_file_present: bool = False
    report_years: List[int] = field(default_factory=list)
    income_data_provided: bool = False
    source_reports: list = field(default_factory=list)
    opening_lots: list = field(default_factory=list)
    cash_events: list = field(default_factory=list)
    opening_zero_confirmed: bool = False
    source_scope_confirmed: bool = False
    realized_pnl: list = field(default_factory=list)


@dataclass
class SourceReport:
    filename: str
    sha256: str
    account: str
    start: date
    end: date
    role: str = "activity"
    has_trades: bool = False
    has_cash: bool = False
    has_positions: bool = False


@dataclass
class OpeningLotRecord:
    account_no: str
    symbol: str
    instrument_id: str
    currency: str
    quantity: Decimal
    cost: Decimal  # total remaining basis, including fees already capitalized by IBKR
    buy_time: datetime
    snapshot_date: date
    source_row: int
    record_id: str
    source_file: str
    broker: str = "IBKR"
    account_name: str = ""
    market: str = ""


@dataclass
class CashEvent:
    account: str
    date: date
    type: str
    currency: str
    amount: Decimal
    record_id: str
    source_file: str
    source_row: int
    symbol: str = ""
