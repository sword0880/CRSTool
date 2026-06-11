"""TaxCalculationService — orchestrates the full calculation flow."""

from dataclasses import dataclass, field
from decimal import Decimal
from typing import List, Optional

from domain.models.dividend_income import DividendIncomeRecord
from domain.models.trade_record import TradeRecord
from domain.models.position_record import PositionRecord
from domain.models.match_record import MatchRecord
from domain.models.tax_summary import TaxSummary, ExportBundle
from domain.models.exceptions import TaxAssistantError

from infrastructure.parsers.dividend_parser import FutuDividendParser
from infrastructure.parsers.trade_parser import FutuTradeParser
from infrastructure.config.exchange_rate_repo import ExchangeRateRepository
from infrastructure.exporters.excel_exporter import ExcelExporter
from domain.services.engine import FIFOEngine
from domain.services.tax_engine import TaxEngine


@dataclass
class CalculationResult:
    """Holds all results and warnings from a calculation run."""
    export_bundle: Optional[ExportBundle] = None
    warnings: List[str] = field(default_factory=list)
    report_bytes: Optional[bytes] = None


class TaxCalculationService:
    """Main service: parse files -> calculate -> export."""

    def __init__(self):
        self.dividend_parser = FutuDividendParser()
        self.trade_parser = FutuTradeParser()
        self.exchange_rate_repo = ExchangeRateRepository()
        self.fifo_engine = FIFOEngine()
        self.tax_engine = TaxEngine()
        self.excel_exporter = ExcelExporter()

    def calculate(self, dividend_file, trade_file=None, tax_year: int = None) -> CalculationResult:
        """Run the full calculation flow.

        Args:
            dividend_file: Path or UploadedFile for dividend tax sheet.
            trade_file: Path or UploadedFile for trade history (optional).
            tax_year: Tax year. If None, auto-detected from dividend data.

        Returns:
            CalculationResult with export bundle, warnings, and Excel bytes.
        """
        result = CalculationResult()

        # 1. Parse dividend file
        dividends = self.dividend_parser.parse(dividend_file)
        result.warnings.extend(self.dividend_parser.warnings if hasattr(self.dividend_parser, 'warnings') else [])

        if not dividends:
            raise TaxAssistantError("股息表中没有可计算的数据")

        # 2. Parse trade file (optional)
        trades: List[TradeRecord] = []
        positions: List[PositionRecord] = []
        withholding_tax = {}

        if trade_file is not None:
            trades = self.trade_parser.parse(trade_file)
            result.warnings.extend(self.trade_parser.warnings)

            # Parse positions for cross-year init
            positions = self.trade_parser.parse_positions(trade_file)

            # Extract withholding tax
            withholding_tax = self.trade_parser.parse_capital_flows(trade_file)

        # Auto-detect tax year if not set
        if tax_year is None:
            tax_year = dividends[0].year
            if tax_year == 0:
                # PDF without year field — try trade data, then filename
                if trades:
                    tax_year = trades[0].trade_date.year
                else:
                    import re
                    fname = getattr(dividend_file, "name", str(dividend_file))
                    year_match = re.search(r"(20\d{2})", fname)
                    if year_match:
                        tax_year = int(year_match.group(1))
                    else:
                        raise TaxAssistantError(
                            "无法自动识别税款年度，请在配置中指定 tax_year"
                        )
                # Update dividend records with detected year
                for d in dividends:
                    d.year = tax_year

        # 3. Exchange rate function
        def get_rate(year: int, currency: str) -> Decimal:
            return self.exchange_rate_repo.get_rate(year, currency)

        # 4. FIFO matching
        beginning_positions = [p for p in positions if p.period_type == "期初"]
        matches = self.fifo_engine.calculate(
            trades,
            beginning_positions=beginning_positions if beginning_positions else None,
            exchange_rate_func=get_rate,
        )
        if hasattr(self.fifo_engine, 'warnings'):
            result.warnings.extend(self.fifo_engine.warnings)

        # 5. Tax calculation
        summary = self.tax_engine.calculate(
            dividends=dividends,
            matches=matches,
            tax_year=tax_year,
            exchange_rate_func=get_rate,
            withholding_tax_by_currency=withholding_tax if withholding_tax else None,
        )

        # 6. Build export bundle
        bundle = ExportBundle(
            tax_summary=summary,
            match_records=matches,
            tax_year=tax_year,
            dividend_details=dividends,
        )

        result.export_bundle = bundle

        # 7. Generate Excel report (single file, 3 sheets)
        result.report_bytes = self.excel_exporter.build_report(
            summary=summary,
            matches=matches,
            dividends=dividends,
        )

        return result
