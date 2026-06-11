"""FIFO capital gains engine — core matching logic."""

from collections import defaultdict, deque
from decimal import Decimal, ROUND_HALF_UP
from typing import List, Dict

from domain.models.trade_record import TradeRecord
from domain.models.match_record import MatchRecord, Lot
from domain.models.position_record import PositionRecord
from domain.models.exceptions import InventoryException


ZERO = Decimal("0")


class FIFOEngine:
    """FIFO cost matching engine for capital gains calculation.

    Each symbol maintains an independent inventory queue.
    Trades are sorted by (trade_date ASC, source_row ASC) before processing.
    """

    def calculate(
        self,
        trades: List[TradeRecord],
        beginning_positions: List[PositionRecord] = None,
        exchange_rate_func=None,
    ) -> List[MatchRecord]:
        """Run FIFO matching and return all MatchRecords.

        Args:
            trades: Parsed trade records (证券 only, already filtered).
            beginning_positions: Optional beginning-of-year positions for cross-year init.
            exchange_rate_func: callable(tax_year, currency) -> Decimal for CNY conversion.
        """
        if not trades:
            return []

        # Sort: date ASC, BUY before SELL on same date, then source_row ASC
        # This ensures same-day buy-then-sell (T+0) works correctly,
        # even if the export has sell row before buy row.
        sorted_trades = sorted(trades, key=lambda t: (
            t.trade_date,
            0 if t.side == "BUY" else 1,
            t.source_row,
        ))

        # Determine tax year from first trade
        tax_year = sorted_trades[0].trade_date.year

        # Initialize inventory per symbol
        inventories: Dict[str, deque] = defaultdict(deque)

        # Seed with beginning-of-year positions
        if beginning_positions:
            for pos in beginning_positions:
                if pos.period_type == "期初" and pos.category == "证券":
                    inventories[pos.symbol].append(Lot(
                        buy_date=pos.date,
                        quantity_remaining=pos.quantity,
                        price=pos.price,
                        commission_remaining=ZERO,
                        currency=pos.currency,
                        source_row=0,
                    ))

        matches: List[MatchRecord] = []
        self.warnings: List[str] = []
        skipped_symbols = set()

        for trade in sorted_trades:
            if trade.side == "BUY":
                inventories[trade.symbol].append(Lot(
                    buy_date=trade.trade_date,
                    quantity_remaining=trade.quantity,
                    price=trade.price,
                    commission_remaining=trade.commission,
                    currency=trade.currency,
                    source_row=trade.source_row,
                ))
            elif trade.side == "SELL":
                # Check if inventory exists; if not, skip with warning
                if not inventories[trade.symbol] and trade.symbol not in skipped_symbols:
                    skipped_symbols.add(trade.symbol)
                    self.warnings.append(
                        f"证券 {trade.symbol} 于 {trade.trade_date} 卖出 {trade.quantity} 股，"
                        f"但无匹配的买入记录或期初持仓，已跳过。"
                        f"请导入包含该股票历史买入的完整交易流水。"
                    )
                    continue
                if trade.symbol in skipped_symbols:
                    continue
                sell_matches = self._process_sell(
                    trade, inventories[trade.symbol], tax_year, exchange_rate_func
                )
                matches.extend(sell_matches)

        return matches

    def _process_sell(
        self,
        trade: TradeRecord,
        inventory: deque,
        tax_year: int,
        exchange_rate_func,
    ) -> List[MatchRecord]:
        """Match a sell against FIFO inventory, return MatchRecords."""
        remaining = trade.quantity
        sell_matches: List[MatchRecord] = []

        while remaining > ZERO:
            if not inventory:
                raise InventoryException(
                    f"证券 {trade.symbol} 于 {trade.trade_date} 卖出数量超过持仓数量。"
                    f"需要 {trade.quantity}，库存不足。"
                )

            lot = inventory[0]
            matched_qty = min(remaining, lot.quantity_remaining)

            # Commission allocation
            buy_comm_alloc = ZERO
            if lot.commission_remaining > ZERO and lot.quantity_remaining > ZERO:
                buy_comm_alloc = (
                    lot.commission_remaining * matched_qty / lot.quantity_remaining
                ).quantize(Decimal("0.00000001"), rounding=ROUND_HALF_UP)

            sell_comm_alloc = ZERO
            if trade.commission > ZERO and trade.quantity > ZERO:
                sell_comm_alloc = (
                    trade.commission * matched_qty / trade.quantity
                ).quantize(Decimal("0.00000001"), rounding=ROUND_HALF_UP)

            sell_revenue = (matched_qty * trade.price).quantize(
                Decimal("0.01"), rounding=ROUND_HALF_UP
            )
            buy_cost = (matched_qty * lot.price).quantize(
                Decimal("0.01"), rounding=ROUND_HALF_UP
            )

            gain_original = sell_revenue - sell_comm_alloc - buy_cost - buy_comm_alloc

            # CNY conversion
            gain_cny = ZERO
            if exchange_rate_func and gain_original != ZERO:
                rate = exchange_rate_func(tax_year, trade.currency)
                gain_cny = (gain_original * rate).quantize(
                    Decimal("0.01"), rounding=ROUND_HALF_UP
                )

            sell_matches.append(MatchRecord(
                symbol=trade.symbol,
                sell_date=trade.trade_date,
                sell_quantity=matched_qty,
                buy_date=lot.buy_date,
                buy_cost=buy_cost,
                sell_revenue=sell_revenue,
                buy_commission_alloc=buy_comm_alloc.quantize(
                    Decimal("0.01"), rounding=ROUND_HALF_UP
                ),
                sell_commission_alloc=sell_comm_alloc.quantize(
                    Decimal("0.01"), rounding=ROUND_HALF_UP
                ),
                currency=trade.currency,
                gain_original=gain_original.quantize(
                    Decimal("0.01"), rounding=ROUND_HALF_UP
                ),
                gain_cny=gain_cny,
            ))

            # Update inventory
            lot.quantity_remaining -= matched_qty
            lot.commission_remaining -= buy_comm_alloc
            remaining -= matched_qty

            if lot.quantity_remaining <= ZERO:
                inventory.popleft()

        return sell_matches
