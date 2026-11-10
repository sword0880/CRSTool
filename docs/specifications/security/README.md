# 安全与认证规范

本目录维护 CRS 本地保险库的安全架构和离线认证目标。**设计规范不等于已实现或已通过安全验收**；当前代码行为必须以实现记录和源码为准。

## 当前权威规范

| 文档 | 唯一职责 | 不负责 |
| --- | --- | --- |
| [安全设计基线](SECURITY_DESIGN.md) | 本地保险库、静态加密、密钥边界、备份/恢复、隐私风险和安全发布门槛 | TOTP 的逐步协议细节、税务算法 |
| [离线 TOTP 认证规范](SECURITY_AUTH_SPEC.md) | TOTP 模式的协议参数、状态机、防重放、恢复码、会话边界和 AUTH 验收 | 通用数据安全架构、Windows Hello 的完整实现流程 |

## 当前实现与证据

- [Authenticator 当前实现](../../guides/AUTH_IMPLEMENTATION.md) — TOTP 使用流程、绑定/恢复及人工验收缺口
- [Windows Hello 当前实现](../../implementation/WINDOWS_HELLO.md) — 首次明确选择 Hello、切换方式、失败关闭和锁定
- [隐私与数据披露](../../guides/PRIVACY.md) — 当前明文/加密数据范围、日志和导出风险
- [项目结构](../../architecture/PROJECT_STRUCTURE.md) — Security、Application、Infrastructure 的依赖边界
- [发布验收](../../validation/RELEASE_ACCEPTANCE.md) · [问题清单](../../KNOWN_ISSUES.md) — 未关闭安全门槛和发布阻断

## 认证路径说明

- 默认路径为离线 TOTP；首次设置时可由用户明确选择 Windows Hello 替代路径。
- Hello 是替代本地认证方式，不是 TOTP 的第二因素，也不是 TOTP 失败后的自动回退。
- 已登记的验证方式失败、取消、冷却或设备不可用时必须拒绝放行；不能通过自动切换方式绕过验证。
- TOTP 专项规范只约束 TOTP 路径；Hello 的实际行为以当前实现记录和源码为准。

## 历史方案

- [2026-10-13 离线认证与安全整改方案](../../archive/security/README.md) — 保留早期综合方案及 Windows Hello / 恢复设计背景，不作为当前实现状态。
