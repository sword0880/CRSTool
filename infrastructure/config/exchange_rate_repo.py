"""ExchangeRateRepository — loads and serves annual average exchange rates."""

import json
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Dict

from domain.models.exceptions import UnsupportedCurrencyException


class ExchangeRateRepository:
    def __init__(self, config_path: str = None):
        if config_path is None:
            config_path = Path(__file__).parent.parent.parent / "config" / "exchange_rate.json"
        self._config_path = Path(config_path)
        self._rates: Dict[str, Dict[str, Decimal]] = {}
        self._load()

    def _load(self):
        if not self._config_path.exists():
            raise FileNotFoundError(f"汇率配置文件不存在: {self._config_path}")
        with open(self._config_path, "r", encoding="utf-8") as f:
            raw = json.load(f)
        if not isinstance(raw, dict):
            raise ValueError("汇率配置必须按年度列出币种汇率")
        for year_str, currencies in raw.items():
            if not isinstance(currencies, dict):
                raise ValueError(f"{year_str} 年汇率配置必须是币种映射")
            self._rates[year_str] = {}
            for currency, rate in currencies.items():
                try:
                    value = Decimal(str(rate))
                except InvalidOperation as exc:
                    raise ValueError(f"{year_str} 年 {currency} 汇率不是有效数字") from exc
                if not value.is_finite() or value <= 0:
                    raise ValueError(f"{year_str} 年 {currency} 汇率必须是有限正数")
                self._rates[year_str][currency.upper()] = value

    @property
    def source(self) -> str:
        return str(self._config_path.resolve())

    def get_rate(self, tax_year: int, currency: str) -> Decimal:
        """Get annual average rate for a given year and currency -> CNY."""
        currency = currency.upper()
        if currency == "CNY":
            return Decimal("1")
        year_str = str(tax_year)
        if year_str not in self._rates:
            raise UnsupportedCurrencyException(
                f"配置文件中缺少 {tax_year} 年的汇率，请补充 exchange_rate.json"
            )
        if currency not in self._rates[year_str]:
            raise UnsupportedCurrencyException(
                f"配置文件中缺少 {tax_year} 年 {currency} 的汇率，请补充 exchange_rate.json"
            )
        return self._rates[year_str][currency]
