"""Unit tests for exchange rate — per TEST_SPEC CASE-201 to CASE-203."""

from decimal import Decimal
import pytest

from infrastructure.config.exchange_rate_repo import ExchangeRateRepository
from domain.models.exceptions import UnsupportedCurrencyException


D = Decimal


class TestExchangeRate:
    def setup_method(self):
        self.repo = ExchangeRateRepository()

    def test_case_201_usd_rate(self):
        """CASE-201: 100 USD at rate 6.4515 -> 645.15"""
        rate = self.repo.get_rate(2021, "USD")
        assert rate == D("6.4515")
        assert (D("100") * rate).quantize(D("0.01")) == D("645.15")

    def test_case_202_hkd_rate(self):
        """CASE-202: 100 HKD at rate 0.8291 -> 82.91"""
        rate = self.repo.get_rate(2021, "HKD")
        assert rate == D("0.8291")
        assert (D("100") * rate).quantize(D("0.01")) == D("82.91")

    def test_case_203_cny_is_identity(self):
        """CNY rate should always be 1.0"""
        rate = self.repo.get_rate(2021, "CNY")
        assert rate == D("1")

    def test_missing_year_raises(self):
        with pytest.raises(UnsupportedCurrencyException):
            self.repo.get_rate(1999, "USD")

    def test_missing_currency_raises(self):
        with pytest.raises(UnsupportedCurrencyException):
            self.repo.get_rate(2021, "SGD")
