"""Unit tests for exchange rate — per TEST_SPEC CASE-201 to CASE-203."""

from decimal import Decimal
import json
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


@pytest.mark.parametrize("bad", ["NaN", "Infinity", "-1", "0", "abc"])
def test_invalid_config_rate_is_rejected(tmp_path, bad):
    config = tmp_path / "rates.json"
    config.write_text(json.dumps({"2025": {"USD": bad}}), encoding="utf-8")
    with pytest.raises(ValueError, match="汇率"):
        ExchangeRateRepository(config)


def test_provisional_rate_matches_observations():
    """临时汇率必须与存档日报价的算术平均一致。"""
    from pathlib import Path
    from decimal import ROUND_HALF_UP
    observations = json.loads((Path(__file__).parents[2] / "config" /
                               "exchange_rate_observations_2026.json").read_text(encoding="utf-8"))
    repo = ExchangeRateRepository()
    assert len(observations) == len({r["date"] for r in observations}) == 182
    assert min(r["date"] for r in observations) == "2026-01-05"
    assert max(r["date"] for r in observations) == "2026-10-08"
    for currency in ("USD", "HKD"):
        mean = sum(D(r[currency]) for r in observations) / len(observations)
        assert repo.get_rate(2026, currency) == mean.quantize(D("0.000001"), rounding=ROUND_HALF_UP)
        assert repo.rate_details(2026, currency)["provisional"] is True
    assert repo.rate_details(2026, "CNY") == {}


def test_changed_rate_does_not_reuse_stale_source(tmp_path):
    """人工改值后不能继续宣称来自旧统计结果。"""
    config = tmp_path / "rates.json"
    config.write_text(json.dumps({"2026": {"USD": "7"}}), encoding="utf-8")
    config.with_name("rates_sources.json").write_text(
        json.dumps({"2026": {"rates": {"USD": "6.852873"}}}), encoding="utf-8")
    assert ExchangeRateRepository(config).rate_details(2026, "USD") == {}
