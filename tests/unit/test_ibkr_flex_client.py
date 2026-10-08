"""IBKR Flex Web Service 下载流程测试。"""

from datetime import date
import socket
import ssl
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlparse

import pytest

from infrastructure.adapters.ibkr_flex_client import FlexDownloadError, download_flex_report


class _Response:
    def __init__(self, payload):
        self.payload = payload

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def read(self, _limit):
        return self.payload


def test_download_annual_report_and_wait_until_ready(monkeypatch):
    calls = []
    payloads = iter([
        b"<FlexStatementResponse><Status>Success</Status><ReferenceCode>ref123</ReferenceCode></FlexStatementResponse>",
        b"<FlexStatementResponse><Status>Fail</Status><ErrorCode>1019</ErrorCode></FlexStatementResponse>",
        b"<FlexQueryResponse><FlexStatements /></FlexQueryResponse>",
    ])

    def fake_urlopen(request, timeout):
        calls.append((request.full_url, timeout))
        return _Response(next(payloads))

    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen", fake_urlopen)
    waits = []
    report = download_flex_report("12345", "dummy-token", date(2025, 1, 1), date(2025, 12, 31), sleep=waits.append)

    assert report.startswith(b"<FlexQueryResponse>")
    assert len(calls) == 3
    assert calls[0][0].split("?", 1)[0].endswith("/SendRequest")
    assert calls[1][0].split("?", 1)[0].endswith("/GetStatement")
    assert parse_qs(urlparse(calls[0][0]).query)["fd"] == ["20250101"]
    assert parse_qs(urlparse(calls[0][0]).query)["td"] == ["20251231"]
    assert parse_qs(urlparse(calls[1][0]).query)["q"] == ["ref123"]
    assert waits == [1.1, 2]


def test_download_error_never_exposes_token(monkeypatch):
    def fake_urlopen(request, timeout):
        raise URLError(request.full_url)

    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen", fake_urlopen)
    with pytest.raises(FlexDownloadError) as error:
        download_flex_report("12345", "sensitive-test-token", date(2025, 1, 1), date(2025, 1, 2))
    assert "sensitive-test-token" not in str(error.value)


@pytest.mark.parametrize(
    ("reason", "expected"),
    [
        (socket.gaierror(11001, "getaddrinfo failed"), "无法解析 IBKR 域名"),
        (ssl.SSLError("certificate verify failed"), "HTTPS 证书校验失败"),
        (TimeoutError("timed out"), "连接超时"),
    ],
)
def test_download_identifies_connection_failure_without_token(monkeypatch, reason, expected):
    def fake_urlopen(request, timeout):
        raise URLError(reason)

    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen", fake_urlopen)
    with pytest.raises(FlexDownloadError) as error:
        download_flex_report("12345", "sensitive-test-token", date(2025, 1, 1), date(2025, 1, 2))
    assert expected in str(error.value)
    assert "sensitive-test-token" not in str(error.value)


def test_download_identifies_http_status_without_token(monkeypatch):
    def fake_urlopen(request, timeout):
        raise HTTPError(request.full_url, 403, "Forbidden", None, None)

    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen", fake_urlopen)
    with pytest.raises(FlexDownloadError) as error:
        download_flex_report("12345", "sensitive-test-token", date(2025, 1, 1), date(2025, 1, 2))
    assert "HTTP 403" in str(error.value)
    assert "sensitive-test-token" not in str(error.value)


def test_download_rejects_reversed_period_before_request(monkeypatch):
    def fake_urlopen(*_args, **_kwargs):
        raise AssertionError("不应发送请求")

    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen", fake_urlopen)
    with pytest.raises(FlexDownloadError, match="开始日期"):
        download_flex_report("12345", "dummy-token", date(2024, 12, 31), date(2024, 1, 1))


def test_download_accepts_leap_year_full_year(monkeypatch):
    calls = []
    payloads = iter([
        b"<FlexStatementResponse><Status>Success</Status><ReferenceCode>ref123</ReferenceCode></FlexStatementResponse>",
        b"<FlexQueryResponse><FlexStatements /></FlexQueryResponse>",
    ])

    def fake_urlopen(request, timeout):
        calls.append(parse_qs(urlparse(request.full_url).query))
        return _Response(next(payloads))

    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen", fake_urlopen)
    report = download_flex_report("12345", "dummy-token", date(2024, 1, 1), date(2024, 12, 31),
                                  sleep=lambda _: None)
    assert report.startswith(b"<FlexQueryResponse>")
    assert calls[0]["fd"] == ["20240101"]
    assert calls[0]["td"] == ["20241231"]


def test_download_rejects_csv_template_and_permanent_service_error(monkeypatch):
    response = b"<FlexStatementResponse><Status>Success</Status><ReferenceCode>ref123</ReferenceCode></FlexStatementResponse>"
    payloads = iter([response, b"Date,Symbol,Quantity\n2025-01-01,AAPL,1"])
    monkeypatch.setattr("infrastructure.adapters.ibkr_flex_client.urlopen",
                        lambda *_args, **_kwargs: _Response(next(payloads)))
    with pytest.raises(FlexDownloadError, match="有效 XML"):
        download_flex_report("12345", "dummy-token", date(2025, 1, 1), date(2025, 1, 2), sleep=lambda _: None)

    payloads = iter([
        response,
        b"<FlexStatementResponse><Status>Fail</Status><ErrorCode>1015</ErrorCode></FlexStatementResponse>",
    ])
    with pytest.raises(FlexDownloadError, match="1015"):
        download_flex_report("12345", "dummy-token", date(2025, 1, 1), date(2025, 1, 2), sleep=lambda _: None)
