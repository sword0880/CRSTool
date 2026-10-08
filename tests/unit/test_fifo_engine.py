"""Unit tests for FIFO engine — per TEST_SPEC CASE-001 to CASE-006."""

from datetime import date
from decimal import Decimal

import pytest

from domain.models.trade_record import TradeRecord
from domain.models.exceptions import InventoryException
from domain.services.engine import FIFOEngine


D = Decimal


def _trade(row, dt, symbol, side, qty, price, comm=0, currency="USD"):
    return TradeRecord(
        source_row=row,
        trade_date=dt,
        account_name="test",
        account_no="test",
        category="证券",
        symbol=symbol,
        market="US",
        side=side,
        settle_date=dt,
        currency=currency,
        quantity=D(str(qty)),
        price=D(str(price)),
        trade_amount=D(str(qty)) * D(str(price)),
        commission=D(str(comm)),
        net_amount=D("0"),
    )


def _rate(year, currency):
    return D("1")  # identity for unit tests


class TestFIFOEngine:
    def setup_method(self):
        self.engine = FIFOEngine()

    def test_one_sale_many_tiny_lots_preserves_rounded_totals(self):
        buys = [_trade(i, date(2021, 1, i), "TINY", "BUY", 1, "0.005", "0.005")
                for i in range(1, 11)]
        sale = _trade(20, date(2021, 2, 1), "TINY", "SELL", 10, "0.005", "0.05")
        matches = self.engine.calculate(buys + [sale], exchange_rate_func=lambda y, c: D("6.4515"))
        assert len(matches) == 10
        assert sum(m.sell_revenue for m in matches) == D("0.05")
        assert sum(m.buy_cost for m in matches) == D("0.05")
        assert sum(m.buy_commission_alloc for m in matches) == D("0.05")
        assert sum(m.sell_commission_alloc for m in matches) == D("0.05")
        assert sum(m.gain_original for m in matches) == D("-0.10")
        assert sum(m.gain_cny for m in matches) == D("-0.65")
        assert all(m.gain_original == m.sell_revenue - m.buy_cost - m.buy_commission_alloc - m.sell_commission_alloc for m in matches)

    def test_one_purchase_sold_three_times_preserves_basis_and_fee(self):
        trades = [_trade(1, date(2021, 1, 1), "TINY", "BUY", 3, "0.005", "0.01")]
        trades += [_trade(i + 2, date(2021, 1, i + 2), "TINY", "SELL", 1, 1)
                   for i in range(3)]
        matches = self.engine.calculate(trades, exchange_rate_func=_rate)
        assert [m.buy_cost for m in matches] == [D("0.01"), D("0.00"), D("0.01")]
        assert sum(m.buy_cost for m in matches) == D("0.02")
        assert sum(m.buy_commission_alloc for m in matches) == D("0.01")
        assert sum(m.gain_original for m in matches) == D("2.97")

    def test_case_001_full_match(self):
        """CASE-001: Buy 100@100 comm 2, Sell 100@150 comm 2 -> gain 4996"""
        trades = [
            _trade(1, date(2021, 1, 1), "AAPL", "BUY", 100, 100, 2),
            _trade(2, date(2021, 6, 1), "AAPL", "SELL", 100, 150, 2),
        ]
        matches = self.engine.calculate(trades, exchange_rate_func=_rate)
        assert len(matches) == 1
        assert matches[0].gain_original == D("4996.00")

    def test_case_002_partial_match(self):
        """CASE-002: Buy 100@100 comm 2, Sell 40@150 comm 2 -> gain 1997.2, remaining 60"""
        trades = [
            _trade(1, date(2021, 1, 1), "AAPL", "BUY", 100, 100, 2),
            _trade(2, date(2021, 6, 1), "AAPL", "SELL", 40, 150, 2),
        ]
        matches = self.engine.calculate(trades, exchange_rate_func=_rate)
        assert len(matches) == 1
        assert matches[0].gain_original == D("1997.20")

    def test_case_003_cross_batch(self):
        """CASE-003: Buy A 100@100 comm 2, Buy B 100@120 comm 2, Sell 150@150 comm 3"""
        trades = [
            _trade(1, date(2021, 1, 1), "AAPL", "BUY", 100, 100, 2),
            _trade(2, date(2021, 2, 1), "AAPL", "BUY", 100, 120, 2),
            _trade(3, date(2021, 6, 1), "AAPL", "SELL", 150, 150, 3),
        ]
        matches = self.engine.calculate(trades, exchange_rate_func=_rate)
        assert len(matches) == 2
        total_gain = sum(m.gain_original for m in matches)
        # Per PRD formula: each batch uses qty*price for revenue
        # Batch 1: 150*100 - 2 - 100*100 - 2 = 15000 - 2 - 10000 - 2 = 4996
        # Batch 2: 150*50 - 1 - 120*50 - 1 = 7500 - 1 - 6000 - 1 = 1498
        # Total = 6494
        assert total_gain == D("6494.00")

    def test_case_005_inventory_insufficiency(self):
        """CASE-005: Sell more than inventory -> exception"""
        trades = [
            _trade(1, date(2021, 1, 1), "AAPL", "BUY", 100, 100, 0),
            _trade(2, date(2021, 6, 1), "AAPL", "SELL", 150, 150, 0),
        ]
        with pytest.raises(InventoryException):
            self.engine.calculate(trades, exchange_rate_func=_rate)

    def test_case_006_same_day_stable_sort(self):
        """CASE-006: Same-day trades sorted by source_row."""
        trades = [
            _trade(3, date(2021, 1, 1), "AAPL", "BUY", 100, 100, 0),
            _trade(1, date(2021, 1, 1), "AAPL", "BUY", 50, 120, 0),
            _trade(2, date(2021, 6, 1), "AAPL", "SELL", 80, 150, 0),
        ]
        matches = self.engine.calculate(trades, exchange_rate_func=_rate)
        # Sort: row 1 (50@120) first, then row 3 (100@100)
        # Sell 80: match 50@120 + 30@100
        # Batch 1: 150*50 - 0 - 120*50 - 0 = 7500 - 6000 = 1500
        # Batch 2: 150*30 - 0 - 100*30 - 0 = 4500 - 3000 = 1500
        assert len(matches) == 2
        assert matches[0].buy_date == date(2021, 1, 1)
        assert matches[0].gain_original == D("1500.00")
