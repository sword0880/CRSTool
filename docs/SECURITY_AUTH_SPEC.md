# SECURITY_AUTH_SPEC.md — CRS 完全离线 TOTP 认证规范

> **状态：设计 / P0 实施与验收目标，不代表已完成代码。**  
> **实现记录：** 主密码、受限认证、首次扫码、防重放、手机恢复及锁定已进入源码并提供自动测试；实际行为与尚缺人工证据见 [AUTH_IMPLEMENTATION.md](AUTH_IMPLEMENTATION.md)。本文仍是完整验收规范，不能将自动测试通过等同全部 P0 关闭。
> 版本：1.2（首次主密码、本机登录及可选手机验证）｜日期：2026-10-10｜适用：.NET 10 / WPF / SQLCipher / Dapper。
> 上位规范：[SECURITY_DESIGN.md](SECURITY_DESIGN.md)，架构：[PROJECT_STRUCTURE.md](PROJECT_STRUCTURE.md)。若本文件与上位规范冲突，以已确认的“五项目架构、10 分钟自动锁定、明文 SQLite 直接拒绝、跨电脑恢复”原则为准。  
> **无关范围：** 不实现微软账号登录、Entra ID、MFA 推送、服务器、联网验证、注册/订阅系统；不更改税务计算口径。

## 0. 决策摘要（不得擅自放宽）

1. CRS 为单用户、本地优先、完全离线工具。首次创建设置主密码保护数据库并绑定手机；在已启用的 Windows 用户环境中，日常解锁**仅输入 TOTP**。使用 Microsoft Authenticator“其他账户”扫码，无 Microsoft 账户依赖。
2. 正常登录只在 TOTP 成功后授予业务会话。首次／恢复用主密码解封随机 DEK，日常由 Windows DPAPI CurrentUser 本机槽解封 DEK → 受限 SQLCipher 认证上下文 → 原子验证并消费 TOTP → 业务访问。只在完整认证及恢复凭证保存确认后记住本机槽，不保存主密码。
3. **TOTP 不是数据库密钥学上的第二重因素。** 掌握主密码或当前 Windows 用户资料者可能在受控程序之外解封 DEK；不能承诺防止同用户恶意进程、管理员、本地补丁或凭证泄露者直接读取解密数据。
4. 不把 6 位 TOTP 当作数据库密码、KDF 输入、DEK 或恢复密钥。TOTP Secret 独立用 CSPRNG 生成，并仅存于 SQLCipher 加密库。
5. 原有 Argon2id + AES-256-GCM Envelope + 随机 256 位 DEK + SQLCipher 方案保持不变；原有恢复密钥必须继续实现跨设备恢复，不依赖 Windows SID 或 DPAPI。
6. **手机验证默认开启，设置可显式关闭。** 切换要求授权会话、主密码及新 TOTP，策略保存于加密库并审计。关闭后只对已完整初始化的库使用 DPAPI 本机凭证进入；恢复中不能借开关绕过确认。无万能 OTP、游客访问或失败自动关闭。
7. 明文 SQLite `crs.db` 一律拒绝；**不读取、不转换、不迁移、不删除、不覆盖**；用户只可在新空目录显式创建新加密保险库。
8. WPF Views/ViewModels 仅调用 Application 用例；Security 只含纯密码学/TOTP 机制；Infrastructure 承担 SQLCipher、事务、备份及会话适配。
9. 10 分钟无活动锁定与 Windows 锁屏/休眠规则不变；启用手机验证时解除锁定必须重新验证 TOTP，关闭时依靠本机凭证建立新业务会话。本机槽丢失需主密码／独立恢复路径，不自动覆盖数据。
10. 所有 P0 项须有自动测试和跨设备恢复验收；文档批准不等于代码已完成或正式发布批准。

## 1. 威胁模型与关键约束

### 1.1 可防护范围

- 日常使用中的非授权界面进入、旁人使用已锁定程序、普通的验证码重放/暴力猜测；
- 电脑关机或保险库未解锁时，磁盘/数据库文件被复制后试图读取静态数据：由 **SQLCipher + 高质量主密码、Argon2id** 提供基础保护；
- 手机丢失、密码遗忘时可通过明确分级的离线恢复手段恢复访问。

### 1.2 不可宣称的保证

- 已掌握主密码、可使用当前 Windows 用户资料、控制管理员权限或能修改 EXE/内存/SQLite 文件的攻击者，可能绕过客户端 TOTP；
- 本地系统时钟可修改、持久化状态可快照回滚，因此离线防重放、锁定和设备撤销不能视为防篡改机制；
- 运行中的原始导入文件、导出 Excel/PDF/CSV、剪贴板、截图及受管终端监控未必被数据库加密覆盖；
- TOTP 不抗钓鱼，不验证用户输入验证码时所处的可信环境。

## 2. 验证协议与默认参数

| 参数 | P0 默认 | 理由/限制 |
| --- | --- | --- |
| 算法 | RFC 6238 TOTP / HMAC-SHA1 | Microsoft Authenticator 第三方账户常见兼容配置；**不是**用 SHA1 给密码做哈希 |
| Secret | CSPRNG 随机 20 字节（160 bit） | 每个 Vault 一份，首次绑定生成；不得由主密码或 VaultId 推导 |
| 输出 | 6 位，仅 ASCII 数字 | 始终保留前导零 |
| 时间步 | 30 秒，自 Unix Epoch/UTC 计算 | 显示本地时间但内部按 UTC；不依赖时区设置 |
| 窗口 | 默认 previous=1, current=1, future=1 | 边缘时钟偏差容忍；不能扩大而不经安全评审 |
| 重放 | 仅允许 `matchedStep > LastAcceptedStep` | 数据库事务内原子消费步骤 |
| 失败门槛 | 连续 5 次失败 → 冷却 15 分钟 | 只抑制普通 UI 猜测；时钟/快照回退可绕过 |
| 自动锁定 | 无活动 10 分钟、Windows 锁屏/休眠 | 沿用 SECURITY_DESIGN.md |
| 限速 | 每次失败逐级短暂延时 + 冷却 | 无需联网、不持久化明文验证码 |
| 绑定标签 | `CRS Desktop:<VaultId短标识>` | 不放券商账号、手机号/邮箱、交易信息 |
| 借助微软云端 | **禁止作为必要条件** | 本功能必须断网完全工作 |

**`otpauth` URI：** `otpauth://totp/CRS%20Desktop%3A%3Clocal-id%3E?secret=<Base32Secret>&issuer=CRS%20Desktop&algorithm=SHA1&digits=6&period=30`。生成时必须对 issuer 和 label 做 RFC3986 URI 编码，Secret 采用标准 Base32；二维码内容、Secret、恢复码一律不得进日志、异常、剪贴板、崩溃报告或遥测。

必须用 RFC 6238 官方测试向量检验算法，并在 Android/iOS 的 Microsoft Authenticator“其他账户”扫码验证互操作，不依赖仅在开发模拟器上通过的测试。

## 3. 核心状态机与生命周期

### 3.1 状态定义

- `VaultAbsent`：无数据库，等待**明确**创建动作；
- `UnsupportedPlaintext`：已有明文 SQLite，强制阻断且原文件不变；
- `Locked`：未持有 DEK，没有业务会话；
- `PasswordVerified`：仅短暂解封装 DEK，未授予业务权限；
- `PendingEnrollment`：只允许绑定 MFA、保存恢复凭证和受限认证查询；
- `PendingMfa`：受限读取 TOTP Secret，不能调用业务仓储；
- `Authenticated`：MFA 验证且步骤成功消费后生成授权工作会话；
- `RecoveryOnly`：通过显式恢复入口恢复主密码/TOTP、旋转凭证，业务不可访问；
- `Error`：异常安全状态，fail-closed，不自动重建或降级。

**许可转换（摘要）：**

```
VaultAbsent --明确创建--> Locked/PasswordVerified
PasswordVerified --MFA未配置--> PendingEnrollment
PasswordVerified --MFA已配置--> PendingMfa
Locked --Windows当前用户解封本机槽--> LocalKeyVerified
LocalKeyVerified --读取已有认证状态--> PendingMfa/PendingEnrollment
LocalKeyVerified --手机验证明确关闭且初始化完整--> Authenticated
PendingEnrollment --首码+恢复凭证确认成功--> Authenticated
PendingMfa --TOTP成功且原子消费step--> Authenticated
Locked --恢复密钥--> RecoveryOnly
PendingMfa --主密码+一次性MFA恢复码--> RecoveryOnly
RecoveryOnly --凭证旋转+绑定新TOTP确认--> Authenticated
Authenticated --超时/锁屏/休眠/退出--> Locked
任何状态 --库格式损坏/校验失败--> Error
```

不同 Vault 恢复状态必须按 VaultId 区分，不能跨库复用会话、Secret 或恢复码。

### 3.2 首次初始化

1. 判断路径与文件状态。若发现明文头 `SQLite format 3\0`、未知格式或已有数据库，不触发迁移/覆盖；用户只能显式选择新空位置创建。
2. 创建临时 staging 目录并开始事务式初始化。随机生成 256 位 DEK、每 Vault 独立 Salt，用 Argon2id 从主密码派生 KEK，再以 AES-256-GCM 封装 DEK。
3. 生成**独立 256 位数据库恢复密钥**及恢复封装槽；明示保存后可解封装 DEK，丢失主密码且缺少恢复密钥时不可恢复。
4. 新建 SQLCipher 数据库并完成格式/加密验证，再生成待确认 TOTP Secret；受限地写入安全设置，`MfaStatus=PendingEnrollment`。
5. 使用 `otpauth` URI 生成二维码供 Microsoft Authenticator“其他账户”扫描。手动输入 Secret 作为可选辅助，不自动复制到剪贴板。
6. 要求输入首个 6 位 TOTP；校验后**原子**持久化 `MfaStatus=Enabled`、Secret、`LastAcceptedStep`。首码本身即计为一次已消费步骤，不能立即用于登录。
7. 生成 8 个一次性 MFA 恢复码（每个至少 128 bit CSPRNG 熵，例如无歧义 Base32 字符串），**只显示一次**。用户确认离线保存恢复码及数据库恢复密钥，再标记 `InitializationComplete=true`。
8. 做一次重新解锁与校验的验证测试（必要时等待下一 30 秒时间步）；验证成功才发布最终保险库路径。失败或关闭时保留受限安全恢复/继续绑定流程，**不允许访问业务主界面**。

初始化临时文件须始终采用 SQLCipher，不留临时明文数据库；最后采用同卷原子替换/持久化方案及目录完整性检查，杜绝半初始化文件被误识为可用库。`vault.meta` 与 `data.db` 一致性需验证，恢复/提交时需要不可混搭的 VaultId、EnvelopeVersion。

### 3.3 日常登录

1. 从 `Locked` 开始，验证路径／格式／封装版本。默认由 DPAPI CurrentUser 解封与 VaultId 绑定的本机槽，无需主密码；本机槽损坏、丢失或新 Windows 用户环境时，显式提供原主密码或独立恢复入口重新启用。任何路径都不能直接开启业务 DB Factory。
2. 创建生命周期最短的**受限 SQLCipher 认证连接**，只可访问 SecuritySettings / RecoveryCodes（不让业务仓储/工作流拿到该连接或 DEK）。
3. 手机开关开启时输入六位代码并检查失败计数／冷却；明确关闭且初始化完整时由本机凭证直接建立业务会话，不能读取普通配置决定跳过验证。
4. 算法验证通过后，执行 `TryConsumeTotpStepAsync` 在 SQLite 事务中 `LastAcceptedStep < matchedStep` 条件更新；只有受影响行数为 1 才成功。
5. 提交验证/计数器事务，释放受限连接，创建正常 `Authenticated` 会话与业务 DB Factory。失败时清理短期 DEK 及句柄，保持 `Locked`/`PendingMfa`。
6. 每次授权的业务操作在入口和持久化提交前检查会话代际 `SessionGeneration`；锁定后旧的异步任务不能提交。
7. 窗口最小化不等于锁定；超时、Windows 锁屏、休眠、显式锁定、退出均必须撤销业务会话，清理缓存、停止后台导入/导出及关闭池化连接。

**SQL 原子消费示意（安全设置仅一行）：**

```sql
UPDATE SecuritySettings
   SET LastAcceptedStep = @matchedStep,
       FailedAttempts = 0,
       LockedUntilUtc = NULL
 WHERE Id = 1
   AND MfaStatus = 'Enabled'
   AND (LastAcceptedStep IS NULL OR LastAcceptedStep < @matchedStep);
-- 验证受影响行数 == 1；必须与当前 VaultId、会话令牌等校验共处事务
```

**注意：** 对照 `LastAcceptedStep` 仅防止同一数据库状态上的重放；旧备份恢复可回退计数/已用步骤。纯离线无法完全消除此风险，必须在威胁模型与恢复测试中标注。

### 3.4 修改主密码、更换手机及恢复

- **修改主密码（正常通道）：** 已授权会话 + 主密码重新认证 + 一次新鲜的 TOTP 验证（也原子消费步骤）→ 用新 Argon2id KEK **重新封装**原 DEK，原子写入新 Envelope。旧密码不应再解锁最新 Envelope。主密码泄露时须提示历史备份仍可用旧密钥解锁，必要时采取 DEK 轮换和旧备份销毁策略。
- **更换手机（正常通道）：** 主密码 + 旧 TOTP 校验 → 生成 PendingReplacement Secret → 新手机扫码并首码校验 → 在一个事务中替换 Secret、重置 `LastAcceptedStep`、作废旧恢复码并保存新恢复码；新扫码失败时旧 Secret 仍生效。显示新恢复码前不能先销毁唯一有效恢复通道。
- **手机丢失（主密码仍在）：** 提供明确入口“使用 MFA 恢复码”。要求正确主密码 + **未使用过**的高熵恢复码，原子消费恢复码后进入 `RecoveryOnly`，只能重新绑定 TOTP/再生成恢复码；不能直接放开业务数据页面。
- **忘记主密码：** 使用独立 256 位恢复密钥解封 DEK → 进入 `RecoveryOnly` → 必须重设主密码并重新绑定 TOTP、旋转 MFA 恢复码。此密钥是最高强度的紧急恢复通道，持有者可解密数据库；流程中显著告知风险并生成本地事件审计。
- **全部凭证遗失：** 主密码与数据库恢复密钥丢失时，不能依靠便携备份迁移；本机槽仍有效且手机可用则可以继续日常访问。本机槽也无法解封时，数据不可恢复；不得暗藏万能口令。
- **恢复后的旧备份：** 可能仍携带旧密码封装、旧 Secret 和旧恢复码；无法远程吊销离线副本。提供提示，不声称“所有旧凭证已在全部副本作废”。

`RecoveryOnly` 不允许进入业务功能；在恢复操作完成且新 TOTP 首码通过前保持受限态。

## 4. 持久化与密钥边界

### 4.1 文件及格式

```
%LOCALAPPDATA%/CRS/Vaults/{VaultId}/
    vault.meta    # KDF/Key Envelope/Recovery Slot 及版本，绝无明文 DEK
    data.db       # SQLCipher 数据库，保存业务及 MFA 配置
```

存储路径为建议值，不擅自更改现有可配置数据库位置规则；统一由 Infrastructure 管理。文件不得存储未加密 TOTP Secret、原始恢复码、登录口令或可直接使用的 SQLCipher key。`vault.meta` 的关键身份字段和 EnvelopeVersion 必须通过 AES-GCM AAD 认证绑定；还须有防混搭/完整性验证策略，不允许把两个 Vault 的 meta 与 DB 交换使用。

### 4.2 SQLCipher 安全表（建议 schema；实际迁移需版本化）

```sql
CREATE TABLE SecuritySettings (
    Id                  INTEGER PRIMARY KEY CHECK(Id=1),
    VaultId             TEXT NOT NULL,
    MfaStatus           TEXT NOT NULL, -- PendingEnrollment / Enabled / PendingReplacement
    TotpSecret          BLOB,
    PendingTotpSecret   BLOB,
    LastAcceptedStep    INTEGER,
    FailedAttempts      INTEGER NOT NULL DEFAULT 0,
    LockedUntilUtc      TEXT,
    InitializationComplete INTEGER NOT NULL DEFAULT 0,
    SecurityVersion     INTEGER NOT NULL
);

CREATE TABLE RecoveryCodes (
    Id          TEXT PRIMARY KEY,
    CodeSalt    BLOB NOT NULL,
    CodeHash    BLOB NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UsedAtUtc   TEXT
);

CREATE TABLE SecurityEvents (
    Id          TEXT PRIMARY KEY,
    VaultId     TEXT NOT NULL,
    EventType   TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    Result      TEXT NOT NULL,
    DetailCode  TEXT
);
```

`TotpSecret` 和 `PendingTotpSecret` 字段中的 Secret 受整个 SQLCipher 文件加密保护。若使用字段级加密，应采用由 DEK 通过 HKDF 分离的子密钥；这是隔离用途而非独立第二因素。审计事件不得包含 6 位代码、Secret、二维码 URI、恢复码、明文密码、DEK、完整财务记录或敏感文件路径。

建议恢复码：用户保存的随机码以至少 128 位熵生成，数据库保存 `SHA-256("CRS-RECOVERY-CODE-V1" || salt || normalize(code))` 等带用途域隔离的不可逆值，按常数时间比较；**每次成功只消费一个**并事务更新 `UsedAtUtc`。对于高熵随机码，这种单向哈希是适当的；不要对仅 6 位 TOTP 使用相同长期哈希存储方式。

### 4.3 失败状态与强制关闭

- 只有主密码恢复或本机 DPAPI 槽成功解封后才能访问加密库内的持久化失败次数；主密码错误仍在当前进程临时限速，不能假定加密库能持久记录密码错误。
- 计数更新和锁定检查事务化；15 分钟的 `LockedUntilUtc` 会受本机时钟回退影响。对本进程可使用单调计时辅助；安全承诺仍仅限普通客户端。
- 错误密码、错误恢复密钥、SQLCipher 损坏、不支持版本、缓存同步异常，全部 **fail-closed**：不得自动关闭 MFA、允许游客访问业务或降级为普通 SQLite。
- 应审查 SQLite WAL/journal、备份临时文件、崩溃转储、WPF 绑定异常与 NLog 格式化对象，以免产生明文 TOTP Secret / DEK。
- SQLCipher key 仅通过经过检查的受控连接注入，不应写入连接字符串、DI singleton 实例、日志或普通配置文件；密钥字节缓冲在可控生命周期内尽可能清理。

## 5. 架构、依赖与接口

**沿用既有五项目决策，禁止新增第六个生产项目。**

| 项目 | 新增职责 | 禁止 |
| --- | --- | --- |
| `CRS.Desktop.Wpf` | `LoginView`、`TotpEnrollmentView`、`RecoveryView`、`SecuritySettingsView`；绑定/恢复 UX | Views/ViewModels/Controls 直接用 Security、SQLCipher、Secret/DEK |
| `CRS.Application` | `InitializeVault`、`BeginUnlock`、`ConfirmTotp`、`RotateTotp`、`RecoverMfa`、`LockSession` 用例；认证状态机与业务许可门槛 | 直接依赖 `CRS.Security`、`Infrastructure` 或持有/传播 DEK |
| `CRS.Domain` | 保留税务/FIFO 业务规则；必要时只放与业务独立的安全状态值对象 | 引入 UI、SQLite、加密库或微软身份服务 |
| `CRS.Security` | TOTP/RFC6238 计算与 URI 构造、CSPRNG Secret/恢复码、常数时间验证；继续 Argon2id/AES-GCM | 文件 I/O、数据库、Dapper、WPF、Domain/Application 依赖 |
| `CRS.Infrastructure` | Application 端口适配、受限认证上下文、SQLCipher 安全表事务、持久审计、生命周期、备份 | 向 WPF 业务页面暴露原生连接、裸 DEK、跳过状态检查 |

端口接口应放 `CRS.Application/Abstractions`（沿用现有约定）；纯 TOTP 核心契约留 `CRS.Security` 内，Infrastructure 适配 Application 端口。Application **不直接引用 Security**；依赖由 Composition Root 注入。

建议的 Application 端口：

```csharp
public interface ILocalAuthenticationUseCases
{
    Task<PasswordChallengeResult> BeginUnlockAsync(
        string vaultId, string password, CancellationToken ct);
    Task<AuthSessionResult> VerifyTotpAsync(
        string challengeId, string code, CancellationToken ct);
    Task<TotpEnrollmentResult> BeginEnrollmentAsync(
        string challengeId, CancellationToken ct);
    Task<AuthSessionResult> ConfirmEnrollmentAsync(
        string challengeId, string code, CancellationToken ct);
    Task<RecoverySessionResult> BeginRecoveryAsync(
        string vaultId, RecoveryInput input, CancellationToken ct);
    Task LockAsync(CancellationToken ct);
}
```

> DTO 必须用不含秘密的视图模型；`BeginEnrollmentAsync` 只在用户明确进入绑定 UI 后返回仅供短暂展示的二维码素材，不能持久化在通用状态/日志中。`PasswordChallengeResult` 只含不透明 challengeId，DEK 由 Infrastructure 的受限会话内部管理；完成 MFA 前普通 `IDesktopUseCases` 不能执行任何业务命令。

`ISecurityStateRepository.TryConsumeTotpStepAsync` 应带上有效会话 challenge、VaultId 和状态版本（乐观锁），在事务中完成条件更新，不能让 WPF 持有 SQLCipher 事务。

## 6. 备份、升级、时间回退

1. `.crsbak` 必须包括**加密一致性快照**、`vault.meta`、格式版本、完整性清单和必要恢复说明；不能对运行中的 `data.db` 直接 `File.Copy` 冒充完整备份。
2. 备份默认加密，排除 vault.device 本机槽，跨 Windows 账号／电脑用备份时主密码或数据库恢复密钥解封，不依赖 DPAPI/SID。恢复检查身份、结构及完整性；成功手机认证后重新启用该 Windows 用户的本机槽。
3. 更换 Windows 电脑且仍有有效 Authenticator 绑定时，原 TOTP 可继续使用；用户可手动旋转绑定。跨机恢复不能静默绕过 MFA。
4. 数据库恢复密钥不可写入备份包的明文区，不得自动在本机生成一个可直接跳过密码/MFA 的持久明文凭证。
5. 备份时间早于 MFA 旋转时，可能重现旧 Secret、旧恢复码、旧 `LastAcceptedStep`。恢复 UI 必须明确警告，推荐完成新绑定/旧备份安全处理；纯离线不可保证全局撤销。
6. 密码/安全 schema 变化的版本兼容测试独立于税务规则版本。仅允许**已有加密保险库**的受支持升级；明文 SQLite 永不进入迁移路径。
7. 备份恢复、密钥封装原子变更必须有断电、磁盘满、取消、损坏、部分文件更新与多 Vault 混搭测试。

## 7. P0 实现顺序（与正在实施的加密开发衔接）

| 顺序 | 任务 | 与已有模块关系 | 输出证据 |
| --- | --- | --- | --- |
| 1 | 确认已实现的 Vault/Envelope/SQLCipher 契约及实际类名 | **先检查当前代码，不重写加密层** | 差异报告、依赖边界 |
| 2 | 在 `CRS.Security` 增加 TOTP 核心与 Secret/恢复码生成 | 新模块，仅纯算法 | RFC6238 向量、二维码兼容测试 |
| 3 | Application 端口与 `PendingMfa` 状态机 | 在现有保险库解锁用例后扩展 | 未通过 MFA 不能打开业务页面 |
| 4 | Infrastructure 安全表、受限连接、原子防重放 | 沿用已接入 SQLCipher 和 Dapper | 并发、锁定、事务测试 |
| 5 | WPF 登录、绑定、丢失手机与恢复 UI | 不改变九个业务页面 | 首次登录、扫码、失败与恢复 E2E |
| 6 | 跨机加密备份/恢复与 TOTP 协同 | 沿用既有加密备份框架 | 新电脑、旧备份、回滚测试 |
| 7 | 架构校验、隐私扫描、发布验收更新 | `KNOWN_ISSUES` / `RELEASE_PLAN` / `TEST_SPEC` | P0 关闭凭据，不自动宣称发布 |

**严禁：** 为了 TOTP 把整个安全实现重新塞进 `CRS.Infrastructure`；修改既有 DEK 格式/恢复协议却未提供回滚；把旧明文 `crs.db` 转到 SQLCipher；在密码正确后便启动业务导入/报表后台线程；把 UI 中“登录成功”当作单独的授权状态。

## 8. 强制验收案例

| ID | 场景 | 通过条件 |
| --- | --- | --- |
| AUTH-01 | Microsoft Authenticator 首次扫码 | 断网可绑定并校验，URI/标签正确；未验证首码不能启用 |
| AUTH-02 | 正常登录 | 首次主密码与手机绑定；日常本机槽解封 + TOTP 原子消费 → Authenticated；本机解封本身不开放业务，损坏／丢失槽可用主密码恢复 |
| AUTH-03 | TOTP 边界时间 | 当前、前后 1 步可按策略校验；超过窗口拒绝；前导零正确 |
| AUTH-04 | 并发/重复验证码 | 同一 Vault 同一步骤并行验证只有一个成功 |
| AUTH-05 | 失败次数锁定 | 5 次后 15 分钟冷却；错误值无日志、无业务进入 |
| AUTH-06 | 初次绑定崩溃/取消 | Pending 状态不能进入业务，不丢唯一恢复方式 |
| AUTH-07 | 新手机绑定失败 | 旧 Secret、旧恢复凭证仍可用；成功后旧 Secret 对最新库失效 |
| AUTH-08 | 手机丢失 | 主密码 + **一次性**高熵 MFA 恢复码进入 RecoveryOnly，强制新绑定 |
| AUTH-09 | 忘记密码 | 独立恢复密钥跨 Windows 用户/设备解封 DEK、重设密码/绑定；没有秘密无法恢复 |
| AUTH-10 | 10 分钟空闲/锁屏/休眠 | 会话失效，后台任务不可继续提交/导出，敏感 UI 清空 |
| AUTH-11 | 明文 SQLite | 检测到明文库/WAL/SHM → 拒绝读取/迁移/删除/覆盖，只能新空目录创建 |
| AUTH-12 | 不受支持/损坏/错误密码库 | 阻断且不自动新建空库、不回退普通 SQLite |
| AUTH-13 | 跨机备份恢复 | 离线恢复成功；`vault.meta` 和 SQLCipher 一致性验证通过 |
| AUTH-14 | 历史备份/快照回滚 | 旧 MFA 状态风险有明确告知，不声称全局吊销 |
| AUTH-15 | 机密扫描 | NLog、配置、二维码/异常、WAL/临时目录、导出无意外密钥/Secret 泄露 |
| AUTH-16 | 架构约束 | `Application -> Domain`；Security 无外层依赖；WPF 非 Composition 层不能直接用 Infrastructure |
| AUTH-17 | 业务回归 | 富途/IBKR 导入、FIFO、汇率、税务、历史复算与导出不受安全层修改影响 |
| AUTH-18 | 时钟回退及同时间步恢复 | 不因系统时间异常自动放行；已消费步不会在同一当前数据库上再次使用 |

**发布阻断：** AUTH-01～AUTH-18 必须有具名自动/手工验证证据，失败不得关闭 P0；测试在干净 Windows 测试机及离线环境中进行。不能以真实券商账户、Flex Token、密钥或交易数据作为公开测试材料。

## 9. 依赖、接口与标准参考

- RFC 6238：<https://www.rfc-editor.org/rfc/rfc6238>
- Microsoft Authenticator 其他账户扫码：<https://support.microsoft.com/zh-cn/authenticator/how-to-add-your-accounts-to-microsoft-authenticator>
- OWASP MFA：<https://cheatsheetseries.owasp.org/cheatsheets/Multifactor_Authentication_Cheat_Sheet.html>
- OWASP 密码存储：<https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html>
- SQLCipher 文档：<https://www.zetetic.net/sqlcipher/documentation/>

实现前评估并锁定 `Otp.NET`、`QRCoder` 等实际使用版本，核验兼容性、许可证、持续维护和离线发行依赖；它们只是候选库，不构成包选择已批准或功能已实现的声明。
