"""Unit tests for parsers — validate against sample Excel files."""

from pathlib import Path
import pandas as pd

import pytest

from infrastructure.parsers.dividend_parser import FutuDividendParser
from infrastructure.parsers.trade_parser import FutuTradeParser
from domain.models.exceptions import ParseException


SAMPLES_DIR = Path(__file__).parent.parent.parent / "samples" / "FUTU"


class TestDividendParser:
    def setup_method(self):
        self.parser = FutuDividendParser()

    @pytest.mark.skipif(not (SAMPLES_DIR / "2021股息.xlsx").exists(), reason="需本地富途样本")
    def test_parse_sample_excel(self):
        """Parse the actual sample dividend Excel file successfully."""
        records = self.parser.parse(SAMPLES_DIR / "2021股息.xlsx")
        assert len(records) > 0
        for r in records:
            assert r.year == 2021
            assert r.currency in ("USD", "HKD", "CNY")

    @pytest.mark.skipif(not (SAMPLES_DIR / "2025_7171103.pdf").exists(), reason="需本地富途样本")
    def test_parse_sample_pdf(self):
        """Parse the actual sample dividend PDF file successfully."""
        records = self.parser.parse(SAMPLES_DIR / "2025_7171103.pdf")
        assert len(records) > 0
        for r in records:
            assert r.currency in ("USD", "HKD", "CNY")
            assert r.dividend >= 0 or r.interest >= 0

    @pytest.mark.skipif(not (SAMPLES_DIR / "2021股息.xlsx").exists(), reason="需本地富途样本")
    def test_dividend_has_account_name(self):
        records = self.parser.parse(SAMPLES_DIR / "2021股息.xlsx")
        for r in records:
            assert r.account_name != ""

    def test_parse_nonexistent_file(self):
        with pytest.raises(ParseException):
            self.parser.parse("nonexistent.xlsx")


@pytest.mark.skipif(not (SAMPLES_DIR / "2021_717110.xlsx").exists(), reason="需本地富途样本")
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


def test_interest_only_row_infers_currency_and_rejects_fractional_year():
    parser = FutuDividendParser()
    row = {"account_name": "A", "dividend": "0", "interest": "USD 1.25",
           "other_income": "0", "year": "2025"}
    assert parser._build_records(pd.DataFrame([row]))[0].currency == "USD"
    row["year"] = "2025.5"
    with pytest.raises(ParseException, match="年份"):
        parser._build_records(pd.DataFrame([row]))


def test_income_currency_conflict_is_rejected():
    row = {"account_name": "A", "dividend": "HKD 1", "interest": "USD 2",
           "other_income": "0", "year": "2025"}
    with pytest.raises(ParseException, match="币种"):
        FutuDividendParser()._build_records(pd.DataFrame([row]))


def test_futu_trade_amount_discrepancy_creates_review_issue():
    parser = FutuTradeParser()
    row = {"成交时间": "2025-01-02", "品类": "证券", "代码名称": "ABC",
           "方向": "卖出", "数量/面值": "1", "价格": "10", "币种": "USD",
           "账户号码": "A", "交易所/市场": "US", "成交金额": "12",
           "总费用": "1", "变动金额": "8"}
    trades = parser._parse_trades({"证券-交易流水": pd.DataFrame([row])})
    assert len(trades) == 1
    assert parser.issues[0].code == "TRADE_AMOUNT_MISMATCH"
