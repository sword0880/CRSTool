# 历史安全设计归档

本目录保存早期安全整改方案，供追溯当时的威胁模型、设计取舍和实现计划。历史方案中的“尚未实现”“计划”“应当”等表述均指其原始日期，不代表当前状态。

## 历史文件

- [离线身份认证与数据安全整改规范（2026-10-13）](CRS_OFFLINE_AUTH_SECURITY_REMEDIATION_20261013.md) — 保留当时的 TOTP、Windows Hello、恢复码、跨设备恢复、备份和验收设计。

## 当前权威入口

- [安全与认证规范](../../specifications/security/README.md)
- [安全设计基线](../../specifications/security/SECURITY_DESIGN.md)
- [离线 TOTP 认证规范](../../specifications/security/SECURITY_AUTH_SPEC.md)
- [Authenticator 当前实现](../../guides/AUTH_IMPLEMENTATION.md)
- [Windows Hello 当前实现](../../implementation/WINDOWS_HELLO.md)
- [隐私与数据披露](../../guides/PRIVACY.md)
- [发布验收](../../validation/RELEASE_ACCEPTANCE.md)

该归档不授权按旧方案重写现有密钥封装、恢复协议、数据库格式或业务计算。
