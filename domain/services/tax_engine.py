"""Tax engine — calculates China personal income tax on overseas income."""

from decimal import Decimal, ROUND_HALF_UP
from typing import List, Dict

from domain.models.dividend_income import DividendIncomeRecord
from domain.models.match_record import MatchRecord
from domain.models.tax_summary import TaxSummary


RATE_20 = Decimal("0.20")
ZERO = Decimal("0")
TAX_POLICY_VERSION = "V1-2026-09"


class TaxEngine:
    """Calculate tax per TAX_ENGINE_SPEC.

    Dividend/interest: (div + int) x 20%, with foreign tax credit.
    Capital gains: gain x 20%, independent from dividend tax.
    """

    def calculate(
        self,
        dividends: List[DividendIncomeRecord],
        matches: List[MatchRecord],
        tax_year: int,
        exchange_rate_func,
        withholding_tax_by_currency: Dict[str, Decimal] = None,
    ) -> TaxSummary:
        summary = TaxSummary()

        # --- Dividend & Interest ---
        dividend_cny = ZERO
        interest_cny = ZERO
        for d in dividends:
            if d.year != tax_year:
                continue
            rate = exchange_rate_func(tax_year, d.currency)
            dividend_cny += (d.dividend * rate).quantize(
                Decimal("0.01"), rounding=ROUND_HALF_UP
            )
            interest_cny += (d.interest * rate).quantize(
                Decimal("0.01"), rounding=ROUND_HALF_UP
            )

        summary.dividend_income_cny = dividend_cny
        summary.interest_income_cny = interest_cny

        # Dividend/interest tax
        combined = dividend_cny + interest_cny
        div_int_tax = (combined * RATE_20).quantize(
            Decimal("0.01"), rounding=ROUND_HALF_UP
        )
        summary.dividend_interest_tax = div_int_tax

        # Foreign tax credit
        foreign_paid = ZERO
        if withholding_tax_by_currency:
            for currency, amount in withholding_tax_by_currency.items():
                rate = exchange_rate_func(tax_year, currency)
                foreign_paid += (amount * rate).quantize(
                    Decimal("0.01"), rounding=ROUND_HALF_UP
                )

        creditable = max(min(foreign_paid, div_int_tax), ZERO)
        summary.foreign_tax_credit = creditable

        div_int_supplement = max(div_int_tax - creditable, ZERO)
        summary.dividend_interest_supplement = div_int_supplement

        # --- Capital Gains ---
        cap_gain_cny = ZERO
        for m in matches:
            if m.sell_date.year != tax_year:
                continue
            cap_gain_cny += m.gain_cny
        summary.capital_gain_cny = cap_gain_cny

        cap_gain_tax = ZERO
        if cap_gain_cny > ZERO:
            cap_gain_tax = (cap_gain_cny * RATE_20).quantize(
                Decimal("0.01"), rounding=ROUND_HALF_UP
            )
        summary.capital_gain_tax = cap_gain_tax
        summary.capital_gain_supplement = cap_gain_tax

        # --- Total ---
        summary.total_supplement_tax = max(div_int_supplement + cap_gain_tax, ZERO)

        return summary
