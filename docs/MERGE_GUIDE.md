# CRS TOTP P0 合并与实施说明

- 生成日期：2026-10-10；状态：**交付给开发端使用的设计文件，不表示代码已修改**。
- 依据：用户已确认离线 TOTP 正常登录、五项目架构、SQLCipher 信封加密、跨电脑恢复与明文 SQLite 直接拒绝政策。
- 本包根据当前可读取的 Library 快照形成；另一个 Work 会话正在推进数据库加密。**不要把包中的旧快照整文件覆盖正在工作的仓库版本**。

## 包内文件

1. `SECURITY_AUTH_SPEC.md`：新增的 P0 离线 MFA 完整实施规范（主文件）。
2. `SECURITY_DESIGN.md`：在原 v1.2 上附加 MFA 约束和引用的**合并候选**。
3. `KNOWN_ISSUES.md`：增加 SEC-11～SEC-14 的 P0 任务与验收索引的**合并候选**。
4. `PROJECT_STRUCTURE.md`：只补充目标结构的 Authentication 目录、接口与会话边界的**合并候选**。
5. `RELEASE_PLAN.md`：把 TOTP 纳入 G1 和故障验收的**合并候选**。

## 正确合并顺序

1. 开发端先检查实际源码与最新 `SECURITY_DESIGN.md`、`PROJECT_STRUCTURE.md`、`KNOWN_ISSUES.md`、`RELEASE_PLAN.md`、`TEST_SPEC.md`、`RELEASE_ACCEPTANCE.md`。确认安全改造实际进度（当前参考文档可能滞后）。
2. 将 `SECURITY_AUTH_SPEC.md` 作为新文档加入项目 `docs/` 目录（或仓库现有规范目录，保持链接相对路径正确）。
3. 以现有仓库**最新版本**为 base，人工合并其他四文件的 MFA 差异；禁止回退加密改造成果，禁止替换已有类/协议但无兼容计划。
4. 在 `TEST_SPEC.md` 新增 `AUTH-01`～`AUTH-18` 测试矩阵，在 `RELEASE_ACCEPTANCE.md` 挂接自动测试证据、确认结果与阻断条件；若实际未实现，状态必须保持 Pending。
5. 检查 `Verify-Architecture.ps1` 的五项目边界与 `CRS.Tests.Security` 独立测试。重测富途/IBKR 数据导入、FIFO、汇率、税额和历史冻结回归。
6. 清理计划和验收报告里的旧文案“仅主密码解锁后即可访问业务”；新方案以正常登录完成 TOTP 为界，不宣称主密码泄露后数据库仍不可解密。
7. 在未完成全部 P0 及真实跨设备断网测试前，保持 ReleaseApproved=false。

## 对照差异和关键不变约束

- **保持 10 分钟自动锁定**（以现有 `SECURITY_DESIGN.md` 为准），不按先前一般性建议改成 15 分钟。
- **保留 Argon2id + KEK / DEK + AES-256-GCM + SQLCipher**，不要让六位 OTP 参与加解密数据库密钥。
- **不迁移明文 SQLite**；旧数据库和 WAL/SHM 不改动；只允许显式的新空路径创建。
- **保持五项目引用边界**，Application 只引用 Domain，Security 只做纯机制，Infrastructure 管 SQLCipher/事务，WPF 页面只调用应用用例。
- **保留备份可跨机器恢复，不依赖 DPAPI 为唯一关键**；承认旧离线备份上的 MFA/恢复凭证无法全局吊销。
- P0 包含一次性 MFA 恢复码（手机丢失）及独立 256 位数据库恢复密钥（忘记主密码），两条路径不能混为一谈。

## 建议给开发端的执行提示

> 按 `SECURITY_AUTH_SPEC.md` 实施离线 Microsoft Authenticator TOTP P0。先检查实际加密分支已实现的 Vault/Envelope、密钥/SQLCipher、测试和文档；不要推翻或覆盖并行加密工作。按现有五项目分层增量加入纯 TOTP、Application 状态机、Infrastructure 原子步骤消费、WPF 扫码/登录/恢复页面。保持明文 SQLite 禁止迁移、10 分钟锁定、跨设备恢复。逐项完成 AUTH-01～AUTH-18，更新相关文档和发布门禁；以真实源码和测试为准，不得提前宣称完成。
