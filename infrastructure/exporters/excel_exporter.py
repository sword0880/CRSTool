"""Export calculated amounts together with their coverage and review issues."""
import io
import json
import pandas as pd
from domain.services.reporting import capital_rows, deposit_rows, dividend_rows, pnl_reconciliation_rows


class ExcelExporter:
    def build_report(self, summary, matches, dividends, deposits=None, dividends_received=None,
                     warnings=None, issues=None, tax_year=None, source_reports=None, cash_events=None, pnl_reconciliations=None,
                     exchange_rates=None, source_scope_confirmed=None, tax_policy_version="V1",
                     status_rows=None, calculation_snapshot=None, cash_reconciliations=None, final_complete=None):
        issues = issues or []
        incomplete = bool(issues) if final_complete is None else not final_complete
        status = ("不完整：仅供复核，不能作为完整年度结果" if issues else
                  "待复核／临时测算：不能作为已核对的完整年度结果" if incomplete else
                  "当前导入范围内测算与对账完成（税务规则与抵免归属仍需复核）")
        buf = io.BytesIO()
        with pd.ExcelWriter(buf, engine="openpyxl") as writer:
            self._write(writer, "计算说明", [
                {"项目": "年度", "说明": tax_year or "未指定"},
                {"项目": "结果状态", "说明": status},
                *(status_rows or []),
                {"项目": "计算口径", "说明": "FIFO；配置汇率（统计期间与来源见汇率底稿）；沿用现有 V1 测算规则"},
                {"项目": "税务规则版本", "说明": tax_policy_version},
                {"项目": "规则适用边界", "说明": "境外扣税按年度和币种合计抵免，尚未按所得项目及国家／地区核对；申报前需复核。"},
                *([{"项目": "IBKR 来源范围确认", "说明": "用户已确认导出范围" if source_scope_confirmed else "未确认导出范围"}]
                  if source_scope_confirmed is not None else []),
                {"项目": "待复核数量", "说明": len(issues)},
                *[{"项目": "提示", "说明": w} for w in warnings or []],
            ])
            self._write(writer, "税务汇总", [
                {"项目": "结果状态", "金额（人民币）": status},
                {"项目": "股息收入（人民币）", "金额（人民币）": float(summary.dividend_income_cny)},
                {"项目": "利息收入（人民币）", "金额（人民币）": float(summary.interest_income_cny)},
                {"项目": "已计算资本利得（人民币）", "金额（人民币）": float(summary.capital_gain_cny)},
                {"项目": "股息利息应纳税额", "金额（人民币）": float(summary.dividend_interest_tax)},
                {"项目": "已计算资本利得税额", "金额（人民币）": float(summary.capital_gain_tax)},
                {"项目": "境外税额抵免", "金额（人民币）": float(summary.foreign_tax_credit)},
                {"项目": "已计算部分补税（非完整年度）" if issues else "测算补税（待对账／临时）" if incomplete else "预计补税", "金额（人民币）": float(summary.total_supplement_tax)},
            ])
            self._write(writer, "汇率底稿", exchange_rates or
                        [{"年度": tax_year, "币种": "", "汇率（兑人民币）": "", "来源": "本次无外币换算"}])
            if calculation_snapshot:
                # 嵌套配置保存为文本，便于按当次版本复核，不执行其中任何内容。
                self._write(writer, "计算快照", [{"项目": k, "值": json.dumps(v, ensure_ascii=False, sort_keys=True)}
                                             for k, v in calculation_snapshot.items()])
            if cash_reconciliations:
                self._write(writer, "现金余额对账", cash_reconciliations)
            if source_reports:
                self._write(writer, "导入来源", [
                    {"文件": r.filename, "SHA256": r.sha256, "账户": r.account, "开始": str(r.start),
                     "结束": str(r.end), "用途": r.role} for r in source_reports])
            if pnl_reconciliations:
                self._write(writer, "已实现盈亏对账", pnl_reconciliation_rows(pnl_reconciliations))
            if cash_events:
                self._write(writer, "现金事件明细", [
                    {"账户": e.account, "日期": str(e.date), "类型": e.type, "币种": e.currency,
                     "原始金额": float(e.amount), "证券": e.symbol, "记录ID": e.record_id,
                     "来源文件": e.source_file, "记录序号": e.source_row} for e in cash_events])
            if dividends:
                self._write(writer, "收入明细", [
                    {"年度": d.year, "账户": d.account_no or d.account_name, "币种": d.currency,
                     "股息": float(d.dividend), "利息": float(d.interest), "其他收入（待分类）": float(d.other_income)}
                    for d in dividends])
            if issues:
                self._write(writer, "待复核记录", [
                    {"原因代码": i.code, "说明": i.message, "账户": i.account, "证券": i.symbol,
                     "币种": i.currency, "日期": str(i.date or ""), "来源表": i.source_sheet, "原始行": i.source_row,
                     "数量": float(i.quantity), "来源文件": i.source_file,
                     "来源开始": str(i.source_start or ""), "来源结束": str(i.source_end or ""),
                     "记录ID": i.record_id} for i in issues])
            if matches:
                self._write(writer, "资本利得汇总", capital_rows(matches))
                self._write(writer, "资本利得明细", [
                    {"券商": m.broker, "账户": m.account_no or m.account_name, "证券代码": m.symbol,
                     "市场": m.market, "卖出日期": str(m.sell_date), "卖出数量": float(m.sell_quantity),
                     "买入日期": str(m.buy_date), "匹配成本": float(m.buy_cost), "收入": float(m.sell_revenue),
                     "买入手续费": float(m.buy_commission_alloc), "卖出手续费": float(m.sell_commission_alloc),
                     "币种": m.currency, "收益（原币）": float(m.gain_original), "收益（人民币）": float(m.gain_cny),
                     "买入来源行": m.buy_source_row, "卖出来源行": m.sell_source_row,
                     "买入文件": m.buy_source_file, "卖出文件": m.sell_source_file,
                     "买入记录ID": m.buy_record_id, "卖出记录ID": m.sell_record_id} for m in matches])
            if deposits:
                self._write(writer, "入金汇总", deposit_rows(deposits))
                self._write(writer, "入金明细", [
                    {"日期": str(d.date), "账户": d.account_no or d.account_name, "币种": d.currency,
                     "入金金额": float(d.amount)} for d in deposits])
            if dividends_received:
                self._write(writer, "分红到账汇总", dividend_rows(dividends_received))
                self._write(writer, "分红到账明细", [
                    {"到账日期": str(d.date), "股票/公司": d.symbol, "币种": d.currency,
                     "分红金额": float(d.amount), "账户": d.account_no or d.account_name,
                     "备注": d.description} for d in dividends_received])
            # Imported identifiers and descriptions are text, never formulas.
            for ws in writer.sheets.values():
                ws.freeze_panes = "A2"
                for row in ws:
                    for cell in row:
                        if cell.data_type == "f":
                            cell.data_type = "s"
        return buf.getvalue()

    @staticmethod
    def _write(writer, name, rows):
        pd.DataFrame(rows).to_excel(writer, sheet_name=name, index=False)
