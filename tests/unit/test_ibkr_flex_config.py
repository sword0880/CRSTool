"""IBKR Flex 查询 ID 配置测试。"""

import pytest

from infrastructure.config.ibkr_flex_repo import load_ibkr_flex_config


def test_load_query_ids_without_token(tmp_path):
    path = tmp_path / "ibkr_flex.json"
    path.write_text('{"annual_query_id":"12345","opening_lots_query_id":"67890"}', encoding="utf-8")
    assert load_ibkr_flex_config(path) == {
        "annual_query_id": "12345", "opening_lots_query_id": "67890"
    }


def test_reject_token_in_plaintext_config(tmp_path):
    path = tmp_path / "ibkr_flex.json"
    path.write_text('{"annual_query_id":"12345","token":"example-only"}', encoding="utf-8")
    with pytest.raises(ValueError, match="服务令牌"):
        load_ibkr_flex_config(path)


def test_reject_invalid_query_id(tmp_path):
    path = tmp_path / "ibkr_flex.json"
    path.write_text('{"annual_query_id":"1a"}', encoding="utf-8")
    with pytest.raises(ValueError, match="annual_query_id"):
        load_ibkr_flex_config(path)
