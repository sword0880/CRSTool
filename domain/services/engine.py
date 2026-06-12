"""FIFO capital gains engine — core matching logic."""

from collections import defaultdict, deque
from decimal import Decimal, ROUND_HALF_UP
from typing import List

from domain.models.trade_record import TradeRecord
from domain.models.match_record import MatchRecord, Lot
from domain.models.position_record import PositionRecord
from domain.models.exceptions import InventoryException


ZERO = Decimal("0")


class FIFOEngine:
    """Account-isolated FIFO; uncertain inventory never becomes a final result."""

    def calculate(self, trades, beginning_positions=None, exchange_rate_func=None,
                  tax_year=None, allow_incomplete=False, unresolved_events=None, opening_lots=None):
        self.warnings = []
        self.issues = []
        self.ending_quantities = {}
        self.uncertain_keys = set()
        from datetime import datetime, time, timezone
        from domain.models.calculation_issue import CalculationIssue, inventory_key

        inventories = defaultdict(deque)
        blocked = set()
        trades = [t for t in trades if tax_year is None or t.trade_date.year <= tax_year]
        for event in unresolved_events or []:
            for t in trades:
                if ((not event.account or event.account == t.account_no)
                        and ((event.instrument_id == t.instrument_id) if event.instrument_id
                             else (not event.symbol or event.symbol == t.symbol))
                        and (not event.currency or event.currency == t.currency)):
                    blocked.add(inventory_key(t))
        # Date-only records cannot be reliably interleaved with timed records.
        date_only = {(inventory_key(t), t.trade_date) for t in trades if t.trade_time is None}
        if date_only:
            self.warnings.append("部分成交缺少时间，同账户证券在该日按原始行序处理，请核对成交顺序。")
        awareness = defaultdict(set)
        for t in trades:
            awareness[inventory_key(t)].add(t.trade_time is not None and t.trade_time.utcoffset() is not None)
            if t.trade_time is not None and t.trade_time.date() != t.trade_date:
                raise InventoryException(f"行 {t.source_row}: 成交日期与成交时间不一致")
        for opening in opening_lots or []:
            awareness[inventory_key(opening)].add(opening.buy_time.utcoffset() is not None)
        if any(len(styles) > 1 for styles in awareness.values()):
            raise InventoryException("同账户证券的成交混用带时区和无时区时间，请统一时间来源")

        def sequence(t):
            if (inventory_key(t), t.trade_date) in date_only:
                instant = datetime.combine(t.trade_date, time.min)
            elif t.trade_time.utcoffset() is not None:
                instant = t.trade_time.astimezone(timezone.utc).replace(tzinfo=None)
            else:
                instant = t.trade_time
            return instant, t.source_row

        sorted_trades = sorted(trades, key=sequence)

        def issue(record, code, message):
            if not allow_incomplete:
                raise InventoryException(message)
            self.issues.append(CalculationIssue(
                code=code, message=message,
                account=record.account_no or record.account_name,
                symbol=record.symbol, currency=record.currency,
                source_row=getattr(record, "source_row", 0),
                date=getattr(record, "trade_date", getattr(record, "date", None)),
                quantity=record.quantity,
                source_sheet="交易流水" if hasattr(record, "trade_date") else "持仓总览",
                source_file=getattr(record, "source_file", ""), record_id=getattr(record, "record_id", ""),
                instrument_id=record.instrument_id,
            ))

        seen_positions = set()
        for pos in beginning_positions or []:
            if pos.period_type != "期初" or pos.category != "证券" or pos.quantity == ZERO:
                continue
            key = inventory_key(pos)
            if key in seen_positions:
                blocked.add(key)
                issue(pos, "DUPLICATE_OPENING", f"{pos.symbol} 存在多份期初持仓，无法确定成本范围。")
                continue
            seen_positions.add(key)
            if any(inventory_key(t) == key and t.trade_date <= pos.date for t in trades):
                blocked.add(key)
                issue(pos, "OVERLAPPING_COST", f"{pos.symbol} 期初持仓与历史交易重叠，请选择并核实成本来源。")
                continue
            if pos.cost_basis_price is None or pos.quantity < ZERO:
                blocked.add(key)
                issue(pos, "UNKNOWN_OPENING_COST", f"{pos.symbol} 期初持仓缺少已确认的历史成本，未使用持仓市价代替。")
                continue
            if not pos.cost_basis_price.is_finite() or pos.cost_basis_price < ZERO:
                raise InventoryException(f"{pos.symbol} 期初成本无效")
            inventories[key].append(Lot(pos.date, pos.quantity, pos.cost_basis_price,
                                        ZERO, pos.currency, 0))

        def opening_sequence(o):
            stamp = o.buy_time.astimezone(timezone.utc).replace(tzinfo=None) if o.buy_time.utcoffset() is not None else o.buy_time
            return stamp, o.source_row

        for opening in sorted(opening_lots or [], key=opening_sequence):
            if not opening.quantity.is_finite() or opening.quantity <= ZERO or not opening.cost.is_finite() or opening.cost < ZERO:
                raise InventoryException("期初批次数量或成本无效")
            key = inventory_key(opening)
            if key in seen_positions or any(inventory_key(t) == key and t.trade_date <= opening.snapshot_date for t in trades):
                raise InventoryException("期初批次与持仓或历史成交重叠，不能重复计入成本")
            inventories[key].append(Lot(opening.buy_time.date(), opening.quantity, opening.cost / opening.quantity,
                ZERO, opening.currency, opening.source_row, opening.source_file, opening.record_id, opening.cost))

        matches = []
        # 按账户与证券延续不足一分的成本和买入费用；同一批次分多次卖出时，
        # 不能在每笔卖出后丢弃舍入余数，否则总成本和总费用会失衡。
        basis_ledgers = defaultdict(lambda: [ZERO, ZERO, ZERO, ZERO])
        for trade in sorted_trades:
            key = inventory_key(trade)
            if not trade.account_no or not (trade.instrument_id or (trade.market and trade.symbol)):
                blocked.add(key)
                issue(trade, "MISSING_IDENTITY", f"行 {trade.source_row}: 账户或证券身份不完整，无法匹配成本。")
                continue
            if (not trade.quantity.is_finite() or trade.quantity <= ZERO
                    or not trade.price.is_finite() or trade.price <= ZERO
                    or not trade.commission.is_finite() or trade.commission < ZERO):
                raise InventoryException(f"行 {trade.source_row}: 数量、价格或费用无效")
            if trade.side not in ("BUY", "SELL"):
                raise InventoryException(f"行 {trade.source_row}: 不支持的交易方向 {trade.side}")
            if trade.side == "BUY":
                inventories[key].append(Lot(trade.trade_date, trade.quantity, trade.price,
                                            trade.commission, trade.currency, trade.source_row, trade.source_file, trade.record_id))
                continue
            if key in blocked:
                issue(trade, "UNCERTAIN_INVENTORY", f"{trade.symbol} 此笔卖出的历史库存未确认，需补齐成本后重新计算。")
                continue
            available = sum((lot.quantity_remaining for lot in inventories[key]), ZERO)
            if available < trade.quantity:
                blocked.add(key)
                issue(trade, "MISSING_COST", f"{trade.symbol} 卖出 {trade.quantity}，可确认库存仅 {available}；此笔及后续受影响卖出待复核。")
                continue
            # Earlier disposals consume inventory but do not need historical FX rates
            # when producing one target year's report.
            target = tax_year is None or trade.trade_date.year == tax_year
            rows = self._process_sell(trade, inventories[key], trade.trade_date.year,
                                      exchange_rate_func if target else None, basis_ledgers[key])
            if target:
                matches.extend(rows)
        self.ending_quantities = {key: sum((lot.quantity_remaining for lot in lots), ZERO) for key, lots in inventories.items()}
        self.uncertain_keys = blocked
        return matches

    def _process_sell(
        self,
        trade: TradeRecord,
        inventory: deque,
        tax_year: int,
        exchange_rate_func,
        basis_ledger,
    ) -> List[MatchRecord]:
        """Match a sell against FIFO inventory, return MatchRecords."""
        remaining = trade.quantity
        sell_matches: List[MatchRecord] = []
        # 按整笔卖出累计分配收入和卖出费用；每一分只落入一个匹配批次。
        exact_revenue = ZERO
        exact_sell_fee = ZERO
        allocated_revenue = ZERO
        allocated_sell_fee = ZERO
        cent = Decimal("0.01")

        def allocation(exact_total, allocated_total):
            return exact_total.quantize(cent, rounding=ROUND_HALF_UP) - allocated_total

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
                buy_comm_alloc = (lot.commission_remaining if matched_qty == lot.quantity_remaining else
                                  (lot.commission_remaining * matched_qty / lot.quantity_remaining)
                                  .quantize(Decimal("0.00000001"), rounding=ROUND_HALF_UP))

            raw_cost = matched_qty * lot.price
            if lot.cost_remaining is not None:
                raw_cost = (lot.cost_remaining if matched_qty == lot.quantity_remaining else
                            lot.cost_remaining * matched_qty / lot.quantity_remaining)
                lot.cost_remaining -= raw_cost

            exact_revenue += matched_qty * trade.price
            basis_ledger[0] += raw_cost
            basis_ledger[2] += buy_comm_alloc
            exact_sell_fee += trade.commission * matched_qty / trade.quantity
            sell_revenue = allocation(exact_revenue, allocated_revenue)
            buy_cost = allocation(basis_ledger[0], basis_ledger[1])
            buy_fee = allocation(basis_ledger[2], basis_ledger[3])
            sell_fee = allocation(exact_sell_fee, allocated_sell_fee)
            allocated_revenue += sell_revenue
            basis_ledger[1] += buy_cost
            basis_ledger[3] += buy_fee
            allocated_sell_fee += sell_fee
            gain_original = sell_revenue - sell_fee - buy_cost - buy_fee

            sell_matches.append(MatchRecord(
                symbol=trade.symbol,
                broker=trade.broker,
                account_no=trade.account_no,
                account_name=trade.account_name,
                market=trade.market,
                instrument_id=trade.instrument_id,
                buy_source_row=lot.source_row,
                sell_source_row=trade.source_row,
                buy_source_file=lot.source_file, sell_source_file=trade.source_file,
                buy_record_id=lot.record_id, sell_record_id=trade.record_id,
                sell_date=trade.trade_date,
                sell_quantity=matched_qty,
                buy_date=lot.buy_date,
                buy_cost=buy_cost,
                sell_revenue=sell_revenue,
                buy_commission_alloc=buy_fee,
                sell_commission_alloc=sell_fee,
                currency=trade.currency,
                gain_original=gain_original,
                gain_cny=ZERO,
            ))

            # Update inventory
            lot.quantity_remaining -= matched_qty
            lot.commission_remaining -= buy_comm_alloc
            remaining -= matched_qty

            if lot.quantity_remaining <= ZERO:
                inventory.popleft()

        if exchange_rate_func and sell_matches:
            rate = exchange_rate_func(tax_year, trade.currency)
            converted_total = ZERO
            allocated_cny = ZERO
            for match in sell_matches:
                converted_total += match.gain_original * rate
                match.gain_cny = converted_total.quantize(cent, rounding=ROUND_HALF_UP) - allocated_cny
                allocated_cny += match.gain_cny

        return sell_matches
