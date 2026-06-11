"""Unit tests for parsers — validate against sample Excel files."""

from pathlib import Path

import pytest

from infrastructure.parsers.dividend_parser import FutuDividendParser
from infrastructure.parsers.trade_parser import FutuTradeParser
from domain.models.exceptions import ParseException


SAMPLES_DIR = Path(__file__).parent.parent.parent / "samples"


class TestDividendParser:
    def setup_method(self):
        self.parser = FutuDividendParser()

    def test_parse_sample_excel(self):
        """Parse the actual sample dividend Excel file successfully."""
        records = self.parser.parse(SAMPLES_DIR / "2021股息.xlsx")
        assert len(records) > 0
        for r in records:
            assert r.year == 2021
            assert r.currency in ("USD", "HKD", "CNY")

    def test_parse_sample_pdf(self):
        """Parse the actual sample dividend PDF file successfully."""
        records = self.parser.parse(SAMPLES_DIR / "2025_7171103.pdf")
        assert len(records) > 0
        for r in records:
            assert r.currency in ("USD", "HKD", "CNY")
            assert r.dividend >= 0 or r.interest >= 0

    def test_dividend_has_account_name(self):
        records = self.parser.parse(SAMPLES_DIR / "2021股息.xlsx")
        for r in records:
            assert r.account_name != ""

    def test_parse_nonexistent_file(self):
        with pytest.raises(ParseException):
            self.parser.parse("nonexistent.xlsx")


class TestTradeParser:
    def setup_method(self):
        self.parser = FutuTradeParser()

    def test_parse_sample_file(self):
        """Parse the actual sample trade file successfully."""
        records = self.parser.parse(SAMPLES_DIR / "2021_717110.xlsx")
        assert len(records) > 0

    def test_only_securities_records(self):
        """All returned records should be category=证券."""
        records = self.parser.parse(SAMPLES_DIR / "2021_717110.xlsx")
        for r in records:
            assert r.category == "证券"

    def test_direction_is_buy_or_sell(self):
        records = self.parser.parse(SAMPLES_DIR / "2021_717110.xlsx")
        for r in records:
            assert r.side in ("BUY", "SELL")

    def test_quantity_is_positive(self):
        """All quantities should be absolute (positive)."""
        records = self.parser.parse(SAMPLES_DIR / "2021_717110.xlsx")
        for r in records:
            assert r.quantity > 0

    def test_warnings_collected(self):
        self.parser.parse(SAMPLES_DIR / "2021_717110.xlsx")
        # Should have at least a fund-skip warning
        assert any("基金" in w for w in self.parser.warnings)
