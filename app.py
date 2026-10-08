"""
海外证券个税助手（中国税务居民版）

主入口文件 - Streamlit 应用
"""

from datetime import date
from io import BytesIO

import streamlit as st

from application.tax_service import TaxCalculationService
from domain.models.exceptions import TaxAssistantError
from application.result_state import input_fingerprint, ibkr_fingerprint, invalidate_result
from infrastructure.adapters.ibkr_flex_client import FlexDownloadError, download_flex_report
from infrastructure.config.ibkr_flex_repo import load_ibkr_flex_config
from domain.services.reporting import capital_rows, deposit_rows, dividend_rows, pnl_reconciliation_rows

st.set_page_config(
    page_title="海外证券个税助手",
    page_icon="💰",
    layout="centered",
)

# --- 免责声明 ---
st.title("💰 海外证券个税助手")
st.caption("中国税务居民版 · 富途 / IBKR")
st.warning("⚠️ 本工具仅用于税务辅助测算，不构成税务申报建议。实际申报请以税务机关要求为准。")

st.divider()

# --- 步骤 1 & 2：上传文件 ---
st.subheader("📁 上传数据文件")

broker = st.selectbox("券商", ["富途", "IBKR"])
dividend_file = trade_file = opening_file = None
report_files = []
opening_zero = False
source_scope_confirmed = False
if broker == "富途":
    col1, col2 = st.columns(2)
    with col1:
        st.markdown("**步骤 1：股息税表**（必填）")
        dividend_file = st.file_uploader("上传股息税表（.xlsx 或 .pdf）", type=["xlsx", "pdf"], key="dividend")
    with col2:
        st.markdown("**步骤 2：年度交易流水**（必填）")
        trade_file = st.file_uploader("上传交易流水（如 2021_717110.xlsx）", type=["xlsx"], key="trade")
else:
    st.caption("导入 Activity Flex XML。可上传年度报告或连续月份报告，重复成交按记录 ID 去重。")
    st.info("手机“税务文件”中的股息报告、1042-S 和外汇收入工作表，目前尚未支持导入。下方入口接收网页 Flex 查询导出的 XML。")
    report_files = st.file_uploader("上传 IBKR 活动报告（可多选）", type=["xml"], accept_multiple_files=True, key="ibkr_reports")
    opening_file = st.file_uploader("上一年末持仓批次 XML（可选，Open Positions 选 Lots）", type=["xml"], key="ibkr_opening")
    opening_zero = st.checkbox("我确认所选年度所有导入账户的期初均无持仓", disabled=opening_file is not None)
    source_scope_confirmed = st.checkbox("我确认导出的报告覆盖目标年度全部相关账户、证券成交和现金明细，且未设置记录过滤")
    if opening_file is not None:
        opening_zero = False
    st.caption("没有期初成本资料且未确认期初无持仓时，仍可生成待复核底稿。可上传或自动获取 Flex XML；CSV 和普通 PDF 对账单暂不支持。")
    with st.expander("IBKR 导出要求"):
        st.markdown("XML 来自网页 Client Portal → Performance & Reports → Flex Queries。手机税务文件或普通活动报表提供的 CSV／PDF 属于其他导出入口，目前不能直接上传；请勿改扩展名。")
        st.markdown("在 Activity Flex Query 中选择 XML，包含 Trades（Executions）、Cash Transactions 和 Open Positions（Summary）。")
        st.markdown("需要账户、Conid、成交／现金记录 ID、日期时间、币种、数量、价格、佣金及佣金币种、交易税、乘数、成交金额和净现金。不要设置证券或现金类型过滤。")
        st.markdown("为核对已实现盈亏，请在 Trades 中增加 Realized PNL（fifoPnlRealized）字段。缺失时仍可测算，但会标明未核对。")
        st.markdown("期初成本需另一份截至上一年 12 月 31 日的 Open Positions（Lots），包含原始买入时间、批次数量、成本金额及原始交易 ID。")

st.divider()
st.subheader("🧮 计算")
year_label = "税款年度（留空使用最新报告年度）" if broker == "IBKR" else "税款年度（留空时从收入文件识别）"
year_text = st.text_input(year_label, value="", key="tax_year").strip()
if broker == "IBKR":
    with st.expander("从 IBKR 自动获取 Activity Flex XML"):
        st.caption("使用 Client Portal 中的数字查询 ID 和新生成的 Flex Web Service 令牌。令牌仅用于本次请求。")
        try:
            flex_config = load_ibkr_flex_config()
        except (ValueError, OSError) as exc:
            st.warning(f"IBKR Flex 配置无效：{exc}")
            flex_config = {"annual_query_id": "", "opening_lots_query_id": ""}
        # 表单提交后保留本次会话的输入，方便按不同年度重复查询。
        with st.form("ibkr_flex_download", clear_on_submit=False):
            flex_query_id = st.text_input("年度 Activity Flex 查询 ID", value=flex_config["annual_query_id"])
            flex_token = st.text_input("Flex Web Service 服务令牌", type="password",
                                       key="ibkr_flex_token")
            default_year = int(year_text) if year_text.isdigit() and 2000 <= int(year_text) <= 2100 else date.today().year - 1
            flex_from = st.date_input("报告开始日期", value=date(default_year, 1, 1))
            flex_to = st.date_input("报告结束日期", value=date(default_year, 12, 31))
            opening_query_id = st.text_input("期初 LOT 查询 ID（可留空）", value=flex_config["opening_lots_query_id"])
            submitted = st.form_submit_button("生成并获取 XML", use_container_width=True)
        if submitted:
            try:
                with st.spinner("正在从 IBKR 生成并下载报告…"):
                    report_xml = download_flex_report(flex_query_id, flex_token, flex_from, flex_to)
                    opening_xml = None
                    if opening_query_id.strip():
                        # 期初 LOT 固定查询报告开始日期前一日，防止混入本期交易。
                        from datetime import timedelta
                        opening_day = flex_from - timedelta(days=1)
                        import time
                        time.sleep(1.1)
                        opening_xml = download_flex_report(opening_query_id, flex_token, opening_day, opening_day)
                # 两份报告全部成功后才替换会话中的文件，避免混合新旧批次。
                st.session_state["ibkr_flex_report_xml"] = report_xml
                st.session_state["ibkr_flex_report_name"] = f"IBKR_{flex_from:%Y%m%d}_{flex_to:%Y%m%d}.xml"
                st.session_state["ibkr_flex_opening_xml"] = opening_xml
                st.success("IBKR XML 已获取，可直接点击“开始计算”。")
            except FlexDownloadError as exc:
                st.error(str(exc))
        if st.session_state.get("ibkr_flex_report_xml"):
            st.download_button("保存年度 XML 备份", st.session_state["ibkr_flex_report_xml"],
                               file_name=st.session_state["ibkr_flex_report_name"], mime="application/xml")
            if st.session_state.get("ibkr_flex_opening_xml"):
                st.download_button("保存期初 LOT XML 备份", st.session_state["ibkr_flex_opening_xml"],
                                   file_name="IBKR_Opening_Lots.xml", mime="application/xml")
            if st.button("清除已获取的 IBKR XML"):
                for key in ("ibkr_flex_report_xml", "ibkr_flex_report_name", "ibkr_flex_opening_xml"):
                    st.session_state.pop(key, None)
                st.rerun()
    # 手动上传优先；自动获取的 XML 使用与上传控件相同的文件接口。
    if not report_files and st.session_state.get("ibkr_flex_report_xml"):
        report = BytesIO(st.session_state["ibkr_flex_report_xml"])
        report.name = st.session_state["ibkr_flex_report_name"]
        report_files = [report]
        if opening_file is None and st.session_state.get("ibkr_flex_opening_xml"):
            opening_file = BytesIO(st.session_state["ibkr_flex_opening_xml"])
            opening_file.name = "IBKR_Opening_Lots.xml"
            opening_zero = False
    fingerprint = ibkr_fingerprint(report_files, opening_file, year_text, opening_zero, source_scope_confirmed)
else:
    fingerprint = input_fingerprint(dividend_file, trade_file, year_text)
invalidate_result(st.session_state, fingerprint)

if st.button("开始计算", disabled=fingerprint is None, use_container_width=True):
    st.session_state.pop("result", None)
    st.session_state.pop("result_fingerprint", None)
    try:
        if year_text and (not year_text.isdigit() or not 2000 <= int(year_text) <= 2100):
            raise TaxAssistantError("税款年度必须是 2000 至 2100 之间的整数")
        with st.spinner("正在解析和计算..."):
            service = TaxCalculationService()
            year = int(year_text) if year_text else None
            if broker == "IBKR":
                result = service.calculate_ibkr(report_files, opening_file, year, opening_zero,
                                                source_scope_confirmed=source_scope_confirmed)
            else:
                result = service.calculate(dividend_file, trade_file, year)
        st.session_state["result"] = result
        st.session_state["result_fingerprint"] = fingerprint
    except TaxAssistantError as e:
        st.error(f"❌ {e}")
    except Exception as e:
        st.error(f"❌ 发生未知错误: {e}")

st.divider()

# --- 步骤 4：结果展示 ---
st.subheader("📊 测算结果")

result = st.session_state.get("result")
if result and result.export_bundle:
    summary = result.export_bundle.tax_summary
    for warning in result.warnings:
        st.warning(warning)
    if result.is_complete:
        st.success("当前导入范围内已按 V1 规则算出金额；税务规则与抵免归属仍需复核。")
    else:
        st.error("结果不完整：以下金额仅为已计算部分，不能作为完整年度补税结果。")
        with st.expander(f"待复核记录（{len(result.issues)} 条）", expanded=True):
            st.dataframe([{"原因": i.message, "账户": i.account, "证券": i.symbol,
                           "日期": str(i.date or ""), "来源表": i.source_sheet, "原始行": i.source_row,
                           "数量": str(i.quantity), "来源文件": i.source_file, "记录ID": i.record_id} for i in result.issues], hide_index=True)

    if result.pnl_reconciliations:
        rows = result.pnl_reconciliations
        passed = sum(r.status == "一致" for r in rows)
        eligible = sum(r.status != "未支持资产" for r in rows)
        excluded = len(rows) - eligible
        suffix = f"，另有 {excluded} 笔未支持资产" if excluded else ""
        with st.expander(f"IBKR 已实现盈亏对账（{passed}/{eligible} 笔一致{suffix}）", expanded=passed != eligible or excluded > 0):
            st.caption("只将股票／ETF 卖出计入一致率；其他资产单独列示。原币收益容差为 0.02，尚未核对全部现金及账户收益。")
            st.dataframe(pnl_reconciliation_rows(rows), hide_index=True)

    col_a, col_b, col_c = st.columns(3)
    with col_a:
        st.metric("股息收入（¥）", f"{summary.dividend_income_cny:,.2f}")
        st.metric("利息收入（¥）", f"{summary.interest_income_cny:,.2f}")
    with col_b:
        st.metric("资本利得（¥）", f"{summary.capital_gain_cny:,.2f}")
        st.metric("境外税额抵免（¥）", f"{summary.foreign_tax_credit:,.2f}")
    with col_c:
        st.metric("股息利息税额（¥）", f"{summary.dividend_interest_tax:,.2f}")
        st.metric("资本利得税额（¥）", f"{summary.capital_gain_tax:,.2f}")

    st.divider()
    st.subheader("💰 预计补税" if result.is_complete else "💰 已计算部分补税")
    st.metric(
        "预计补税金额" if result.is_complete else "部分金额（不能代表完整年度）",
        f"¥ {summary.total_supplement_tax:,.2f}",
        delta=None,
    )

    if result.export_bundle.deposits:
        st.subheader("💵 入金汇总（按账户、币种）")
        st.dataframe(deposit_rows(result.export_bundle.deposits), use_container_width=True, hide_index=True)
    if result.export_bundle.dividends_received:
        st.subheader("🏷️ 分红到账汇总（按账户、证券、币种）")
        st.dataframe(dividend_rows(result.export_bundle.dividends_received), use_container_width=True, hide_index=True)
    if result.export_bundle.match_records:
        st.subheader("📈 资本利得汇总")
        st.dataframe(capital_rows(result.export_bundle.match_records), use_container_width=True, hide_index=True)

    # Dividend details
    if result.export_bundle.dividend_details:
        with st.expander("📋 股息利息明细"):
            for d in result.export_bundle.dividend_details:
                st.write(
                    f"- **{d.account_name}** ({d.currency}): "
                    f"股息 {d.dividend}, 利息 {d.interest}, 其他 {d.other_income}"
                )

    # Capital gain details
    if result.export_bundle.match_records:
        with st.expander(f"📋 资本利得明细（{len(result.export_bundle.match_records)} 笔）"):
            for m in result.export_bundle.match_records:
                st.write(
                    f"- **{m.symbol}** {m.sell_date} 卖出 {m.sell_quantity} 股: "
                    f"收益 {m.currency} {m.gain_original} (¥{m.gain_cny})"
                )
else:
    st.info("上传文件并点击「开始计算」后，结果将在此显示。")

st.divider()

# --- 步骤 5：导出 ---
st.subheader("📥 导出")

report_bytes = getattr(result, 'report_bytes', None) if result else None
if report_bytes:
    tax_year = getattr(result.export_bundle, 'tax_year', '') if result and result.export_bundle else ''
    prefix = "Tax_Report" if result.is_complete else "Partial_Review"
    file_name = f"{prefix}_{tax_year}.xlsx"
    st.download_button(
        label=f"📥 下载{'税务报告' if result.is_complete else '待复核底稿'}（{file_name}）",
        data=report_bytes,
        file_name=file_name,
        mime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        use_container_width=True,
    )
    st.caption("包含计算说明、税务汇总，以及有数据的明细和待复核记录；原币金额按币种分别汇总。")
else:
    st.info("计算完成后可在此导出 Excel 报告。")

# --- 底部信息 ---
st.divider()
st.caption("手动上传的文件在本地处理；使用 IBKR 自动获取时，应用会向 IBKR Flex Web Service 发送查询 ID、日期和服务令牌。")
