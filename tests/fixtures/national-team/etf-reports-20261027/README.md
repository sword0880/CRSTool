# 官方ETF报告目录响应回放

用途：复现2026-10-27协议核对，默认回归不访问外网。公开目录元数据不包含个人账户资料；不携带HTTP凭据、Cookie或私钥。

| 样本 | 方法与范围 | 响应SHA-256 |
| --- | --- | --- |
| `sse-510300-periodic-2026-direct.json` | 沪市基金公告GET，510300，2026-01-01至2026-10-27，第1页25条，本次返回6条 | `872953CC2942C37636D71B171949270A674F113EF4F51AEBAB52A73CF41A98DA` |
| `szse-159915-periodic-2026-page1.json` | 深市基金公告JSON POST，159915，同区间，第1页50条，本次返回6条 | `C4C534144FC3CDD40BFCE72049C4242724597651B62180EA23BD345A12F6952B` |

同名`*.receipt.json`保存实际请求、采集时刻、正文摘要和默认TLS核验标志；深市`*-request.json`另保留原请求正文。测试按收据核对实际字节摘要。沪市直连仍是同一官方URL、默认TLS核验，仅在此前DNS/代理TLS连接失败后进行，不涉及HTTP拒绝绕过。

收据是采集工具的单独记录，并非交易所签名或第三方持仓证明。

来源页面：[上交所基金公告](https://www.sse.com.cn/disclosure/fund/announcement/)、[深交所基金公告](https://www.szse.cn/disclosure/fund/notice/index.html)。字段、来源限制和运行证据边界见[实现说明](../../../../docs/implementation/NATIONAL_TEAM_ETF_REPORT_INDEX_20261027.md)。

这里只证明保存响应的结构和解析可复现；没有冻结的官方历史目录快照，不证明全部披露完整，更不证明机构持仓、交易、公布准确时点或正式Worker已运行该检索。
