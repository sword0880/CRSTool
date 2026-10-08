"""通过 IBKR Flex Web Service 获取 Activity Flex XML。"""

from __future__ import annotations

from datetime import date
import socket
import ssl
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen
from xml.etree import ElementTree
import re
import time


_BASE_URL = "https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService"
_MAX_RESPONSE_BYTES = 25 * 1024 * 1024
_MAX_ATTEMPTS = 5


class FlexDownloadError(ValueError):
    """下载或校验 Flex 报告失败。"""


def _parse_xml(payload: bytes) -> ElementTree.Element:
    # 拒绝实体与文档类型声明，避免解析外部实体或实体扩展。
    if b"<!DOCTYPE" in payload.upper() or b"<!ENTITY" in payload.upper():
        raise FlexDownloadError("IBKR 返回的 XML 包含不支持的文档声明。")
    try:
        return ElementTree.fromstring(payload)
    except ElementTree.ParseError as exc:
        raise FlexDownloadError("IBKR 返回的内容不是有效 XML。") from exc


def _request_xml(endpoint: str, parameters: dict[str, str]) -> bytes:
    url = f"{_BASE_URL}/{endpoint}?{urlencode(parameters)}"
    request = Request(url, headers={"User-Agent": "CRS-IBKR-Flex/1.0"})
    stage = "生成报告" if endpoint == "SendRequest" else "下载报告"
    try:
        with urlopen(request, timeout=30) as response:
            payload = response.read(_MAX_RESPONSE_BYTES + 1)
    except HTTPError as exc:
        # HTTP 异常中的 URL 可能包含令牌，只显示状态码和固定诊断信息。
        if exc.code == 429:
            detail = "请求过于频繁，请稍后重试"
        elif exc.code in (401, 403):
            detail = "访问被拒绝，请检查令牌、IP 限制或网络代理"
        elif 500 <= exc.code < 600:
            detail = "IBKR 服务暂时不可用，请稍后重试"
        else:
            detail = "请检查查询参数和 IBKR 服务状态"
        raise FlexDownloadError(f"IBKR {stage}失败：HTTP {exc.code}，{detail}。") from None
    except URLError as exc:
        # 只判断异常类型，不显示可能含有令牌或代理地址的原始异常文本。
        reason = exc.reason
        if isinstance(reason, socket.gaierror):
            detail = "无法解析 IBKR 域名，请检查 DNS 和网络连接"
        elif isinstance(reason, ssl.SSLError):
            detail = "HTTPS 证书校验失败，请检查系统时间和证书或代理设置"
        elif isinstance(reason, TimeoutError):
            detail = "连接超时，请检查网络或稍后重试"
        else:
            detail = "网络连接异常，请检查代理、防火墙和网络设置"
        raise FlexDownloadError(f"IBKR {stage}失败：{detail}。") from None
    except TimeoutError:
        raise FlexDownloadError(f"IBKR {stage}失败：连接超时，请稍后重试。") from None
    except OSError:
        raise FlexDownloadError(f"IBKR {stage}失败：网络连接异常，请检查网络设置。") from None
    if len(payload) > _MAX_RESPONSE_BYTES:
        raise FlexDownloadError("IBKR 报告超过 25 MB，请缩短查询日期范围。")
    if not payload:
        raise FlexDownloadError("IBKR 返回了空报告。")
    return payload


def _response_value(root: ElementTree.Element, tag: str) -> str:
    return (root.findtext(tag) or "").strip()


def download_flex_report(
    query_id: str,
    token: str,
    from_date: date,
    to_date: date,
    *,
    sleep=time.sleep,
) -> bytes:
    """生成并下载单份报告；令牌只在函数调用期间传给 IBKR。"""
    query_id = str(query_id).strip()
    token = str(token).strip()
    if not re.fullmatch(r"[0-9]+", query_id):
        raise FlexDownloadError("查询 ID 必须是 IBKR 提供的数字。")
    if not token:
        raise FlexDownloadError("请输入 Flex Web Service 服务令牌。")
    if not isinstance(from_date, date) or not isinstance(to_date, date):
        raise FlexDownloadError("请选择有效的起止日期。")
    if from_date > to_date:
        raise FlexDownloadError("开始日期不能晚于结束日期。")
    common = {"t": token, "v": "3"}
    response = _parse_xml(
        _request_xml(
            "SendRequest",
            {
                **common,
                "q": query_id,
                "fd": from_date.strftime("%Y%m%d"),
                "td": to_date.strftime("%Y%m%d"),
            },
        )
    )
    if response.tag != "FlexStatementResponse" or _response_value(response, "Status") != "Success":
        error_code = _response_value(response, "ErrorCode")
        detail = f"（错误码 {error_code}）" if len(error_code) <= 6 and error_code.isdigit() else ""
        raise FlexDownloadError(f"IBKR 未接受报告生成请求{detail}，请检查查询 ID、令牌及模板设置。")
    reference_code = _response_value(response, "ReferenceCode")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", reference_code):
        raise FlexDownloadError("IBKR 未返回有效的报告引用码。")

    # IBKR 每秒最多允许一次请求；生成报告后先等待，再用同一引用码有限轮询。
    sleep(1.1)
    for attempt in range(_MAX_ATTEMPTS):
        if attempt:
            sleep(min(2 * attempt, 8))
        payload = _request_xml("GetStatement", {**common, "q": reference_code})
        root = _parse_xml(payload)
        if root.tag == "FlexQueryResponse":
            return payload
        if root.tag != "FlexStatementResponse":
            raise FlexDownloadError("IBKR 返回的不是 Activity Flex XML 报告。")
        if _response_value(root, "Status") == "Success":
            raise FlexDownloadError("IBKR 返回成功状态，但未包含 Activity Flex XML。")
        error_code = _response_value(root, "ErrorCode")
        if error_code not in {"1019", "1021"}:
            detail = f"（错误码 {error_code}）" if len(error_code) <= 6 and error_code.isdigit() else ""
            raise FlexDownloadError(f"IBKR 无法获取报告{detail}，请检查令牌和查询模板。")
        if attempt == _MAX_ATTEMPTS - 1:
            break
    raise FlexDownloadError("IBKR 尚未生成报告，请稍后重试下载。")
