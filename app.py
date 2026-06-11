"""
富途海外证券个税助手（中国税务居民版）

主入口文件 - Streamlit 应用
"""

import streamlit as st

from application.tax_service import TaxCalculationService
from domain.models.exceptions import TaxAssistantError

st.set_page_config(
    page_title="富途海外证券个税助手",
    page_icon="💰",
    layout="centered",
)

# --- 免责声明 ---
st.title("💰 富途海外证券个税助手")
st.caption("中国税务居民版 · V1.0")
st.warning("⚠️ 本工具仅用于税务辅助测算，不构成税务申报建议。实际申报请以税务机关要求为准。")

st.divider()

# --- 步骤 1 & 2：上传文件 ---
st.subheader("📁 上传数据文件")

col1, col2 = st.columns(2)

with col1:
    st.markdown("**步骤 1：股息税表**（必填）")
    dividend_file = st.file_uploader(
        "上传股息税表（.xlsx 或 .pdf）",
        type=["xlsx", "pdf"],
        key="dividend",
    )

with col2:
    st.markdown("**步骤 2：年度交易流水**（可选）")
    trade_file = st.file_uploader(
        "上传交易流水（如 2021_717110.xlsx）",
        type=["xlsx"],
        key="trade",
    )

st.divider()

# --- 步骤 3：开始计算 ---
st.subheader("🧮 计算")

if st.button("开始计算", disabled=(dividend_file is None), use_container_width=True):
    if dividend_file is None:
        st.error("请先上传股息税表。")
    else:
        try:
            with st.spinner("正在解析和计算..."):
                service = TaxCalculationService()
                result = service.calculate(
                    dividend_file=dividend_file,
                    trade_file=trade_file,
                )

            # Show warnings
            for w in result.warnings:
                st.warning(w)

            # Store result in session state
            st.session_state["result"] = result
            st.success("✅ 计算完成！")

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
    st.subheader("💰 预计补税")
    st.metric(
        "预计补税金额",
        f"¥ {summary.total_supplement_tax:,.2f}",
        delta=None,
    )

    # Capital gain summary by symbol
    if result.export_bundle.match_records:
        from collections import defaultdict
        symbol_gain = defaultdict(lambda: {"original": 0, "cny": 0, "currency": "", "count": 0})
        for m in result.export_bundle.match_records:
            symbol_gain[m.symbol]["original"] += float(m.gain_original)
            symbol_gain[m.symbol]["cny"] += float(m.gain_cny)
            symbol_gain[m.symbol]["currency"] = m.currency
            symbol_gain[m.symbol]["count"] += 1

        st.subheader("📈 资本利得汇总（按股票）")
        summary_rows = []
        for sym in sorted(symbol_gain.keys(), key=lambda s: symbol_gain[s]["cny"], reverse=True):
            g = symbol_gain[sym]
            summary_rows.append({
                "股票代码": sym,
                "交易笔数": g["count"],
                "币种": g["currency"],
                "收益（原币）": round(g["original"], 2),
                "收益（人民币）": round(g["cny"], 2),
            })
        st.dataframe(summary_rows, use_container_width=True, hide_index=True)

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
    st.download_button(
        label="📥 下载税务报告（Tax_Report.xlsx）",
        data=report_bytes,
        file_name="Tax_Report.xlsx",
        mime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        use_container_width=True,
    )
    st.caption("包含 3 个 Sheet：税务汇总、资本利得汇总（按股票）、资本利得明细")
else:
    st.info("计算完成后可在此导出 Excel 报告。")

# --- 底部信息 ---
st.divider()
st.caption("所有数据仅在本地处理，不上传任何服务器，不联网。")
