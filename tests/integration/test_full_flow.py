"""Integration tests — full flow with sample Excel files."""

from pathlib import Path

import pytest

from application.tax_service import TaxCalculationService
from domain.models.exceptions import TaxAssistantError


SAMPLES_DIR = Path(__file__).parent.parent.parent / "samples"


class TestFullFlow:
    def setup_method(self):
        self.service = TaxCalculationService()

    def test_case_401_full_flow(self):
        """CASE-401: Import both files, calculate, export — full success."""
        result = self.service.calculate(
            dividend_file=SAMPLES_DIR / "2021股息.xlsx",
            trade_file=SAMPLES_DIR / "2021_717110.xlsx",
        )
        assert result.export_bundle is not None
        assert result.export_bundle.tax_summary is not None
        assert result.report_bytes is not None
        assert result.export_bundle.tax_year == 2021

    def test_case_402_dividend_only(self):
        """CASE-402: Dividend only — no trade file."""
        result = self.service.calculate(
            dividend_file=SAMPLES_DIR / "2021股息.xlsx",
            trade_file=None,
        )
        assert result.export_bundle is not None
        assert result.export_bundle.tax_summary.dividend_income_cny > 0
        # No capital gains
        assert result.export_bundle.tax_summary.capital_gain_cny == 0
        assert result.export_bundle.match_records == []

    def test_export_bytes_not_empty(self):
        """Exported Excel file should not be empty."""
        result = self.service.calculate(
            dividend_file=SAMPLES_DIR / "2021股息.xlsx",
            trade_file=SAMPLES_DIR / "2021_717110.xlsx",
        )
        assert len(result.report_bytes) > 0

    def test_warnings_present(self):
        """Should have warnings (e.g. fund records skipped)."""
        result = self.service.calculate(
            dividend_file=SAMPLES_DIR / "2021股息.xlsx",
            trade_file=SAMPLES_DIR / "2021_717110.xlsx",
        )
        assert len(result.warnings) > 0
