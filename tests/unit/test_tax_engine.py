"""Unit tests for tax engine — per TEST_SPEC CASE-101 to CASE-107."""

from datetime import date
from decimal import Decimal

from domain.models.dividend_income import DividendIncomeRecord
from domain.models.match_record import MatchRecord
from domain.services.tax_engine import TaxEngine


D = Decimal


def _rate(year, currency):
    return D("1")  # identity for unit tests


def _div(account, dividend, interest, currency="USD"):
    return DividendIncomeRecord(
        account_name=account,
        account_no="",
        year=2021,
        dividend=D(str(dividend)),
        interest=D(str(interest)),
        other_income=D("0"),
        currency=currency,
    )


def _match(symbol, gain_original, gain_cny):
    return MatchRecord(
        symbol=symbol,
        sell_date=date(2021, 6, 1),
        sell_quantity=D("100"),
        buy_date=date(2021, 1, 1),
        buy_cost=D("0"),
        sell_revenue=D("0"),
        buy_commission_alloc=D("0"),
        sell_commission_alloc=D("0"),
        currency="USD",
        gain_original=D(str(gain_original)),
        gain_cny=D(str(gain_cny)),
    )


class TestTaxEngine:
    def setup_method(self):
        self.engine = TaxEngine()

    def test_case_101_dividend_only_with_withholding(self):
        """CASE-101: Dividend 10000, withholding 1000 -> supplement 1000"""
        dividends = [_div("test", 10000, 0)]
        summary = self.engine.calculate(
            dividends, [], 2021, _rate,
            withholding_tax_by_currency={"USD": D("1000")},
        )
        assert summary.dividend_income_cny == D("10000.00")
        assert summary.dividend_interest_tax == D("2000.00")
        assert summary.foreign_tax_credit == D("1000.00")
        assert summary.dividend_interest_supplement == D("1000.00")

    def test_case_102_interest_only(self):
        """CASE-102: Interest 5000 -> tax 1000"""
        dividends = [_div("test", 0, 5000)]
        summary = self.engine.calculate(dividends, [], 2021, _rate)
        assert summary.interest_income_cny == D("5000.00")
        assert summary.dividend_interest_tax == D("1000.00")

    def test_case_103_div_plus_interest(self):
        """CASE-103: Div 10000 + Int 5000 -> tax 3000"""
        dividends = [_div("test", 10000, 5000)]
        summary = self.engine.calculate(dividends, [], 2021, _rate)
        assert summary.dividend_interest_tax == D("3000.00")

    def test_case_104_foreign_exceeds_china_tax(self):
        """CASE-104: Foreign tax 2000 vs China tax 1500 -> credit 1500, supplement 0"""
        dividends = [_div("test", 5000, 2500)]  # 7500 x 20% = 1500
        summary = self.engine.calculate(
            dividends, [], 2021, _rate,
            withholding_tax_by_currency={"USD": D("2000")},
        )
        assert summary.foreign_tax_credit == D("1500.00")
        assert summary.dividend_interest_supplement == D("0.00")

    def test_case_105_capital_gain(self):
        """CASE-105: Capital gain 60000 -> tax 12000"""
        matches = [_match("AAPL", 60000, 60000)]
        summary = self.engine.calculate([], matches, 2021, _rate)
        assert summary.capital_gain_cny == D("60000.00")
        assert summary.capital_gain_tax == D("12000.00")

    def test_case_106_capital_loss(self):
        """CASE-106: Capital loss -5000 -> tax 0"""
        matches = [_match("AAPL", -5000, -5000)]
        summary = self.engine.calculate([], matches, 2021, _rate)
        assert summary.capital_gain_cny == D("-5000.00")
        assert summary.capital_gain_tax == D("0.00")

    def test_case_107_div_taxed_with_cap_loss(self):
        """CASE-107: Div 10000 + Cap loss -50000 -> div taxed, cap tax 0"""
        dividends = [_div("test", 10000, 0)]
        matches = [_match("AAPL", -50000, -50000)]
        summary = self.engine.calculate(dividends, matches, 2021, _rate)
        assert summary.dividend_interest_tax == D("2000.00")
        assert summary.capital_gain_tax == D("0.00")
        assert summary.total_supplement_tax == D("2000.00")
