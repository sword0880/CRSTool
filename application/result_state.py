"""Bind results to the exact uploaded content and selected annual scope."""
from hashlib import sha256


def input_fingerprint(dividend_file, trade_file, year_text):
    if dividend_file is None or trade_file is None:
        return None
    digest = sha256()
    for value in (year_text.encode(), dividend_file.name.encode(), dividend_file.getvalue(),
                  trade_file.name.encode(), trade_file.getvalue()):
        digest.update(len(value).to_bytes(8, "big"))
        digest.update(value)
    return digest.hexdigest()


def invalidate_result(state, fingerprint):
    if fingerprint is None or state.get("result_fingerprint") != fingerprint:
        state.pop("result", None)
        state.pop("result_fingerprint", None)


def ibkr_fingerprint(report_files, opening_file, year_text, opening_zero, source_scope_confirmed=False):
    if not report_files:
        return None
    digest = sha256()
    values = [b"IBKR", year_text.encode(), str(opening_zero).encode(), str(source_scope_confirmed).encode()]
    for source in report_files:
        values.extend([b"activity", source.name.encode(), source.getvalue()])
    if opening_file is not None:
        values.extend([b"opening", opening_file.name.encode(), opening_file.getvalue()])
    for value in values:
        digest.update(len(value).to_bytes(8, "big"))
        digest.update(value)
    return digest.hexdigest()
