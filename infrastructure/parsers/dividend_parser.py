"""Futu dividend tax sheet parser — reads account-level annual summary.

Supports both Excel (.xlsx) and PDF (.pdf) formats.
PDF may contain Chinese or English headers depending on the version.
"""

from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import List, Dict

import pandas as pd

from domain.models.dividend_income import DividendIncomeRecord
from domain.models.exceptions import ParseException


SUPPORTED_CURRENCIES = {"USD", "HKD", "CNY"}

# Header mapping: various possible header names -> internal field name
HEADER_ALIASES: Dict[str, str] = {
    # Chinese headers
    "牛牛号": "niuniu_id",
    "年份": "year",
    "账户名称": "account_name",
    "全年股息": "dividend",
    "全年利息": "interest",
    "全年其他收入": "other_income",
    "币种": "currency",
    # English headers
    "Account": "account_name",
    "Dividends": "dividend",
    "Interest": "interest",
    "Other income": "other_income",
}

# Minimum required fields for a valid record
REQUIRED_FIELDS = {"account_name", "dividend", "interest", "other_income"}


def _to_decimal(value, field_name: str) -> Decimal:
    """Convert a value (possibly text like 'HKD 7791.14') to Decimal."""
    if value is None or (isinstance(value, str) and value.strip() in ("", "-", "—")):
        return Decimal("0")
    try:
        cleaned = str(value).strip()
        # Remove currency prefix like "HKD ", "USD ", "CNY "
        for prefix in ("HKD", "USD", "CNY", "hkd", "usd", "cny"):
            if cleaned.startswith(prefix):
                cleaned = cleaned[len(prefix):].strip()
        # Remove thousand separators
        cleaned = cleaned.replace(",", "").replace(" ", "")
        return Decimal(cleaned)
    except InvalidOperation:
        raise ParseException(f"字段【{field_name}】的值 '{value}' 无法转换为数字")


def _extract_currency(value) -> str:
    """Extract currency code from a value like 'HKD 7791.14' or plain 'HKD'."""
    s = str(value).strip().upper()
    for cur in SUPPORTED_CURRENCIES:
        if s.startswith(cur):
            return cur
    return s


def _normalize_headers(columns) -> List[str]:
    """Map column names to internal field names using aliases."""
    result = []
    for col in columns:
        name = str(col).strip()
        mapped = HEADER_ALIASES.get(name, name)
        result.append(mapped)
    return result


class FutuDividendParser:
    """Parse the dividend tax sheet (e.g. 2021股息.xlsx or 2025_7171103.pdf).

    Reads account-level annual summary of dividend/interest/other income.
    """

    def parse(self, file_path) -> List[DividendIncomeRecord]:
        """Parse dividend data from Excel or PDF file.

        Args:
            file_path: Path, string, or file-like object (Streamlit UploadedFile).
        """
        if isinstance(file_path, (str, Path)):
            path = Path(file_path)
            if not path.exists():
                raise ParseException(f"文件不存在: {path}")
            suffix = path.suffix.lower()
            if suffix == ".pdf":
                return self._parse_pdf(path)
            else:
                return self._parse_excel(path)
        else:
            name = getattr(file_path, "name", "")
            if name.lower().endswith(".pdf"):
                return self._parse_pdf(file_path)
            else:
                return self._parse_excel(file_path)

    def _parse_excel(self, source) -> List[DividendIncomeRecord]:
        """Parse from Excel file."""
        try:
            xls = pd.ExcelFile(source, engine="openpyxl")
        except Exception as e:
            raise ParseException(f"无法打开 Excel 文件: {e}")

        target_sheet = None
        for name in xls.sheet_names:
            if "股息" in name:
                target_sheet = name
                break
        if target_sheet is None:
            raise ParseException("未找到包含'股息'的 Sheet，请确认文件格式")

        df = pd.read_excel(xls, sheet_name=target_sheet, engine="openpyxl")
        if df.empty:
            raise ParseException("股息表数据为空")

        df.columns = _normalize_headers(df.columns)
        return self._build_records(df)

    def _parse_pdf(self, source) -> List[DividendIncomeRecord]:
        """Parse from PDF file using pdfplumber."""
        try:
            import pdfplumber
        except ImportError:
            raise ParseException("解析 PDF 需要安装 pdfplumber，请运行: pip install pdfplumber")

        try:
            pdf = pdfplumber.open(source)
        except Exception as e:
            raise ParseException(f"无法打开 PDF 文件: {e}")

        all_records: List[DividendIncomeRecord] = []

        for page in pdf.pages:
            tables = page.extract_tables()
            if not tables:
                continue

            for table in tables:
                if not table or len(table) < 2:
                    continue

                # Find header row
                header_idx = self._find_header_row(table)
                if header_idx is None:
                    continue

                headers_raw = [str(c).strip() if c else "" for c in table[header_idx]]
                headers = _normalize_headers(headers_raw)

                # Check if this table has our required fields
                if not REQUIRED_FIELDS.issubset(set(headers)):
                    continue

                data_rows = table[header_idx + 1:]
                clean_rows = self._clean_rows(data_rows, len(headers))

                if not clean_rows:
                    continue

                df = pd.DataFrame(clean_rows, columns=headers)
                records = self._build_records(df)
                all_records.extend(records)

        pdf.close()

        if not all_records:
            raise ParseException("PDF 中未找到股息数据表格，请确认文件格式")

        return all_records

    def _find_header_row(self, table) -> int:
        """Find the row index that looks like a header containing account/dividend fields."""
        for i, row in enumerate(table):
            if not row:
                continue
            cells = [str(c).strip() if c else "" for c in row]
            # Check for known header keywords
            has_account = any(k in cells for k in ("牛牛号", "Account"))
            has_dividend = any(k in cells for k in ("全年股息", "Dividends"))
            has_interest = any(k in cells for k in ("全年利息", "Interest"))
            if has_account and has_dividend and has_interest:
                return i
        return None

    def _clean_rows(self, data_rows, num_cols: int) -> list:
        """Clean data rows: remove empty/separator rows, align column count."""
        clean = []
        for row in data_rows:
            if not row:
                continue
            # Skip rows that are all empty or dashes
            vals = [str(c).strip() if c else "" for c in row]
            if all(v in ("", "-", "—", "None") for v in vals):
                continue
            # Align column count
            if len(vals) >= num_cols:
                clean.append(vals[:num_cols])
            else:
                clean.append(vals + [""] * (num_cols - len(vals)))
        return clean

    def _build_records(self, df: pd.DataFrame) -> List[DividendIncomeRecord]:
        """Validate and build DividendIncomeRecord list from normalized DataFrame."""
        records: List[DividendIncomeRecord] = []
        for _, row in df.iterrows():
            account_name = str(row.get("account_name", "")).strip()
            if not account_name or account_name in ("", "-", "—", "None"):
                continue

            # Detect currency: from dedicated column or from dividend value
            currency_raw = row.get("currency", "")
            if currency_raw and str(currency_raw).strip() not in ("", "-", "None"):
                currency = _extract_currency(currency_raw)
            else:
                # Try to extract from dividend value (e.g. "HKD 7791.14")
                currency = _extract_currency(row.get("dividend", ""))

            if currency not in SUPPORTED_CURRENCIES:
                raise ParseException(f"暂不支持币种: {currency}")

            year_raw = row.get("year", 0)
            if year_raw and str(year_raw).strip() not in ("", "-", "None", "0"):
                year = int(_to_decimal(year_raw, "年份"))
            else:
                year = 0  # will be auto-detected by service

            record = DividendIncomeRecord(
                account_name=account_name,
                account_no="",
                year=year,
                dividend=_to_decimal(row.get("dividend", 0), "全年股息"),
                interest=_to_decimal(row.get("interest", 0), "全年利息"),
                other_income=_to_decimal(row.get("other_income", 0), "全年其他收入"),
                currency=currency,
            )
            records.append(record)

        return records
