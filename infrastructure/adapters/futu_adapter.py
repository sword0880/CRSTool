"""Adapt existing Futu uploads to a broker-neutral import result."""
from infrastructure.parsers.dividend_parser import FutuDividendParser
from infrastructure.parsers.trade_parser import FutuTradeParser
from domain.models.broker_import import BrokerImportResult


class FutuReportAdapter:
    def parse(self, dividend_file, trade_file=None):
        data = FutuTradeParser().parse_all(trade_file) if trade_file is not None else BrokerImportResult()
        data.dividends = FutuDividendParser().parse(dividend_file)
        return data
