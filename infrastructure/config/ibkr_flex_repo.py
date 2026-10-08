"""读取可复用的 IBKR Flex 查询模板配置。"""

from __future__ import annotations

import json
from pathlib import Path


_DEFAULT_PATH = Path(__file__).resolve().parents[2] / "config" / "ibkr_flex.json"


def load_ibkr_flex_config(path: Path | None = None) -> dict[str, str]:
    """读取查询 ID；服务令牌不得写入此配置。"""
    config_path = Path(path) if path is not None else _DEFAULT_PATH
    if not config_path.exists():
        return {"annual_query_id": "", "opening_lots_query_id": ""}
    with config_path.open("r", encoding="utf-8") as handle:
        raw = json.load(handle)
    if not isinstance(raw, dict):
        raise ValueError("IBKR Flex 配置必须是 JSON 对象。")
    if any(key in raw for key in ("token", "service_token", "access_token")):
        raise ValueError("请从 IBKR Flex 配置中移除服务令牌，并只在应用密码框输入。")
    result = {}
    for key in ("annual_query_id", "opening_lots_query_id"):
        value = str(raw.get(key, "")).strip()
        if value and not value.isascii():
            raise ValueError(f"{key} 必须是 IBKR 提供的数字查询 ID。")
        if value and not value.isdigit():
            raise ValueError(f"{key} 必须是 IBKR 提供的数字查询 ID。")
        result[key] = value
    return result
