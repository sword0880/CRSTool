"""Normalize broker files, enforce annual boundaries, calculate and export."""

import re
from dataclasses import dataclass, field, replace
from datetime import date
from decimal import Decimal
from pathlib import Path
from typing import List, Optional

from domain.models.tax_summary import ExportBundle
from domain.models.calculation_issue import CalculationIssue
from domain.models.exceptions import TaxAssistantError
from infrastructure.adapters.futu_adapter import FutuReportAdapter
from infrastructure.config.exchange_rate_repo import ExchangeRateRepository
from infrastructure.exporters.excel_exporter import ExcelExporter
from domain.services.engine import FIFOEngine
from domain.services.tax_engine import TaxEngine, TAX_POLICY_VERSION


@dataclass
class CalculationResult:
    export_bundle: Optional[ExportBundle] = None
    warnings: List[str] = field(default_factory=list)
    report_bytes: Optional[bytes] = None
    issues: List[CalculationIssue] = field(default_factory=list)

    pnl_reconciliations: list = field(default_factory=list)

    @property
    def is_complete(self):
        return not self.issues


class TaxCalculationService:
    def __init__(self, adapter=None, exchange_rate_repo=None):
        self.adapter = adapter or FutuReportAdapter()
        self.exchange_rate_repo = exchange_rate_repo or ExchangeRateRepository()
        self.fifo_engine = FIFOEngine()
        self.tax_engine = TaxEngine()
        self.excel_exporter = ExcelExporter()

    def calculate(self, dividend_file, trade_file=None, tax_year=None):
        data = self.adapter.parse(dividend_file, trade_file)
        filename = Path(getattr(dividend_file, "name", str(dividend_file))).name
        match = re.search(r"(?<!\d)(20\d{2})(?!\d)", filename)
        # Existing files such as 2025_7171103.pdf provide the annual summary year.
        file_year = int(match.group(1)) if match else None
        return self.calculate_imported(data, tax_year, file_year)

    def calculate_ibkr(self, report_files, opening_file=None, tax_year=None, opening_zero=False,
                       source_scope_confirmed=False):
        from infrastructure.adapters.ibkr_adapter import IbkrReportAdapter
        data = IbkrReportAdapter().parse_files(report_files, opening_file, opening_zero)
        data.source_scope_confirmed = source_scope_confirmed
        if tax_year is None and data.report_years:
            tax_year = max(data.report_years)
            data.warnings.append(f"按最新报告年度 {tax_year} 测算，可在页面修改。")
        return self.calculate_imported(data, tax_year)

    def calculate_imported(self, data, tax_year=None, income_file_year=None):
        """Public normalized-data entry point for future broker adapters."""
        if not data.dividends and not data.income_data_provided:
            raise TaxAssistantError("缺少收入数据，不能将缺少股息表视为全年零收入")
        known_years = {d.year for d in data.dividends if d.year}
        if tax_year is None:
            if len(known_years) == 1:
                tax_year = next(iter(known_years))
            elif known_years:
                raise TaxAssistantError("收入文件包含多个年度，请指定税款年度")
            elif income_file_year:
                tax_year = income_file_year
            else:
                raise TaxAssistantError("收入文件未注明年度，请在页面指定税款年度")
        if not isinstance(tax_year, int) or not 2000 <= tax_year <= 2100:
            raise TaxAssistantError("税款年度必须是 2000 至 2100 之间的整数")
        if any(d.year == 0 for d in data.dividends) and income_file_year and income_file_year != tax_year:
            raise TaxAssistantError("指定年度与收入文件名中的年度不一致，请核对文件")
        dividends = [replace(d, year=tax_year) if d.year == 0 else d for d in data.dividends]
        dividends = [d for d in dividends if d.year == tax_year]
        if not dividends and not data.income_data_provided:
            raise TaxAssistantError(f"收入文件中没有 {tax_year} 年数据")

        if data.report_years and tax_year not in data.report_years:
            raise TaxAssistantError("交易报告年度与目标年度不一致，请上传对应年度流水")
        result = CalculationResult(warnings=list(data.warnings))
        if data.broker == "IBKR":
            from application.ibkr_review import check_coverage
            result.issues.extend(check_coverage(data, tax_year))
            # XML 无法证明导出模板未筛选账户或记录，必须由用户确认来源范围。
            if not data.source_scope_confirmed:
                result.issues.append(CalculationIssue("SOURCE_SCOPE_UNCONFIRMED",
                    "尚未确认 Activity Flex 导出包含目标年度全部相关账户、证券成交和现金明细。"))
            if data.opening_zero_confirmed:
                result.warnings.append("用户确认所选年度全部导入账户的期初无持仓。")
            if any(d.dividend < 0 or d.interest < 0 for d in dividends):
                result.issues.append(CalculationIssue("NEGATIVE_INCOME", "存在年度净负收入，请核对收入冲正归属年度。"))
        if data.trade_file_present and not data.report_years:
            result.issues.append(CalculationIssue("UNKNOWN_REPORT_PERIOD", "交易文件缺少明确报告年度，无法确认年度覆盖范围。"))
        # 历史成本事件可能影响目标年度；无日期的文件级问题按报告期间归属。
        for issue in data.issues:
            if issue.date is not None:
                relevant = issue.date.year == tax_year or (
                    issue.date.year < tax_year and issue.code == "UNSUPPORTED_TRANSFER")
            elif issue.source_start is not None and issue.source_end is not None:
                # 同名文件可能覆盖不同期间，使用问题自身的来源期间判断归属。
                relevant = issue.source_start.year <= tax_year <= issue.source_end.year
                if issue.code == "UNSUPPORTED_TRANSFER" and issue.source_end.year < tax_year:
                    relevant = True
            else:
                relevant = True
            if relevant:
                result.issues.append(issue)
        if any(d.year == 0 for d in data.dividends):
            result.warnings.append(f"收入表未注明年度，已按 {tax_year} 年处理，请确认报告期间。")
        if not data.trade_file_present:
            result.issues.append(CalculationIssue("MISSING_TRADE_FILE", "未提供交易流水，仅测算收入；资本收益和预扣税尚未确认。"))
        if any(d.other_income != 0 for d in dividends):
            result.issues.append(CalculationIssue("OTHER_INCOME", "存在其他收入，尚未分类计税，请复核。"))

        beginning = []
        for pos in data.positions:
            if pos.period_type != "期初" or pos.category != "证券":
                continue
            if pos.date.year == tax_year - 1 or pos.date == date(tax_year, 1, 1):
                beginning.append(pos)
            else:
                result.issues.append(CalculationIssue("OPENING_PERIOD", "期初持仓日期与目标年度不一致，未使用该持仓。",
                    account=pos.account_no, symbol=pos.symbol, currency=pos.currency, date=pos.date, quantity=pos.quantity))
        used_rates = {}

        def get_rate(year, currency):
            rate = self.exchange_rate_repo.get_rate(year, currency)
            used_rates[(year, currency.upper())] = rate
            return rate
        matches = self.fifo_engine.calculate(data.trades, beginning, get_rate,
                                             tax_year=tax_year, allow_incomplete=True,
                                             unresolved_events=[i for i in result.issues if i.code == "UNSUPPORTED_TRANSFER"],
                                             opening_lots=data.opening_lots)
        result.warnings.extend(self.fifo_engine.warnings)
        result.issues.extend(self.fifo_engine.issues)
        if data.broker == "IBKR":
            from application.ibkr_review import reconcile_positions
            result.issues.extend(reconcile_positions(data, tax_year, self.fifo_engine))
            from application.ibkr_review import reconcile_realized_pnl
            result.pnl_reconciliations, pnl_issues, pnl_warnings = reconcile_realized_pnl(data, tax_year, self.fifo_engine, matches)
            result.issues.extend(pnl_issues)
            result.warnings.extend(pnl_warnings)
        withholding = {}
        for record in data.withholding:
            if record.date.year == tax_year:
                withholding[record.currency] = withholding.get(record.currency, Decimal("0")) + record.amount
        if any(value < 0 for value in withholding.values()):
            result.issues.append(CalculationIssue("NET_TAX_REFUND", "本年度存在净退税，请核对原扣税年度；抵免不会计算为负数。"))
        summary = self.tax_engine.calculate(dividends, matches, tax_year, get_rate, withholding)
        deposits = [d for d in data.deposits if d.date.year == tax_year]
        received = [d for d in data.dividends_received if d.date.year == tax_year]
        result.export_bundle = ExportBundle(summary, matches, tax_year, dividends, deposits, received)
        result.report_bytes = self.excel_exporter.build_report(
            summary, matches, dividends, deposits, received,
            warnings=result.warnings, issues=result.issues, tax_year=tax_year,
            source_reports=data.source_reports,
            pnl_reconciliations=result.pnl_reconciliations,
            cash_events=[e for e in data.cash_events if e.date.year == tax_year],
            exchange_rates=[{"年度": year, "币种": currency, "汇率（兑人民币）": str(rate),
                             "来源": "系统固定汇率 CNY=1" if currency == "CNY" else
                             getattr(self.exchange_rate_repo, "source", "注入的汇率提供者")}
                            for (year, currency), rate in sorted(used_rates.items())],
            source_scope_confirmed=data.source_scope_confirmed if data.broker == "IBKR" else None,
            tax_policy_version=TAX_POLICY_VERSION,
        )
        return result
