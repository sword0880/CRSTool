# TOTP 默认与首次 Windows Hello 选择

2026-10-13，按用户确认实现：默认 TOTP；首次没有验证器时，可明确选择 Windows Hello。已绑定 TOTP 后，不会因为错误、取消、冷却或手机丢失自动切换。

## 使用与恢复

设置中切换到 Hello 时，先填写创建数据库时的主密码和当前手机上的新六位代码，再点击保存，随后完成 Windows 系统验证。缺少凭证时在设置页明确提示并保留待保存选择；主密码错误时提示认证失败，不再误报为文件或权限问题。

- 首次创建主密码、离线保存数据库恢复密钥后，默认扫描 Microsoft Authenticator 二维码。没有验证器时，可以点击“首次没有验证器：选择 Windows Hello”。Windows 必须已配置 PIN、指纹或人脸，系统验证成功才登记方式并进入。
- Hello 方式不生成手机恢复码，未完成绑定的 TOTP 秘密会在同一加密事务中清除。取消或设备不可用时保持原来的首次设置状态，可继续默认 TOTP。
- 再次进入不显示数据库路径和主密码输入，先由当前 Windows 用户的 DPAPI 本机槽准备受限会话，再明确点击 Hello 验证；本机槽不能单独放行业务。
- 十分钟空闲、Windows 锁屏或休眠时自动锁定。尚未完成的系统验证属于旧会话，即使随后返回成功也不能重新解锁。
- 设置页提供互斥的手机验证和 Windows Hello 选项，未配置 Hello 时禁用其选项。选择后显示“尚未保存”，点击底部“保存设置”会同时处理验证方式；完成系统验证后才显示已启用，取消或失败保留原方式。切换到 Hello 需要主密码、当前手机的新代码和系统验证；切回手机需要主密码和系统验证，并锁定工作区、重新绑定手机。不能关闭全部验证；旧版关闭手机的设置也不再允许免验证进入。
- 日常解锁页只显示已登记的手机或 Hello 验证，不显示更换手机、恢复与加密备份；这些维护操作保留在已解锁工作区的安全管理入口。
- 本机槽损坏、丢失或更换 Windows 用户时，先用原主密码建立恢复上下文，再明确验证 Hello。身份改变不能仅凭旧本机槽重新登记。遗忘主密码可用数据库恢复密钥重设，再验证 Hello；不会自动改为 TOTP。
- 便携加密备份不携带 DPAPI 本机槽。恢复到新空目录后，使用新主密码和系统验证重新启用本机登录。主密码与数据库恢复密钥全部丢失时不提供跨设备恢复后门。

## 分层与格式

Application 提供 `IWindowsHelloVerifier` 端口。Desktop.Wpf 的组合根装配真实 Windows 适配器，Infrastructure 的保险库用例直接调用可信端口，不能接受界面传来的“已验证”布尔值。

WPF 使用真实安全窗口句柄和 `UserConsentVerifierInterop.RequestVerificationForWindowAsync`，只有 `Verified` 才成功。不可用、策略禁用、未配置、取消、设备忙或重试耗尽均拒绝。实现依据 [Microsoft 桌面验证说明](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier)。

首次 Hello 成功才将加密认证表升级至 `SecurityVersion=3`，保存 `LoginMethod=WindowsHello`、当前 Windows 用户 SID、`MfaStatus=HelloEnabled` 及完整初始化标记。旧版 1/2 的加密 TOTP 库继续原行为，不主动升级；不兼容明文 SQLite。旧程序不能忽略新认证格式访问 Hello 库。

系统弹窗期间不持有保险库门闩。成功返回后重新检查操作代次、原受限会话、五分钟时限、Windows 身份和最新认证状态，再提交和发布仓储。其他认证操作、锁定或恢复导致状态变化时拒绝迟到结果。

## 保护边界与验收

Hello 是系统身份验证门槛，DEK 仍由 Windows 当前用户 DPAPI 保管。本实现没有使用 TPM 密钥签名或 Hello 硬件密钥来封装 DEK，不能宣称系统验证是数据库第二重加密，也不能阻挡同用户恶意程序或客户端补丁。

`WindowsHelloChecks` 使用操作系统端口替身，其他部分运行真实 SQLCipher、DPAPI 和加密备份。覆盖首次保存确认、默认 TOTP、取消/失败、方式隔离、日常验证、改密、锁屏迟到结果、Windows 身份变化、恢复密钥及便携备份恢复。`VaultInteractionChecks` 验证首次按钮门槛、日常不显示路径/密码、不显示 TOTP 降级入口及设置隔离。

真实 Windows PIN、指纹、人脸提示、设备不可用、取消和锁屏/休眠中断仍需人工实机验收，自动替身测试不代表硬件已通过。

本轮构建零警告、零错误；Hello 专项、Infrastructure 全套、WPF 交互及桌面窗口检查通过。项目分层检查通过，文档本地链接和目录索引无遗漏。这里的系统验证成功/失败由端口替身提供，不能替代上述人工验收。

[返回当前实现](README.md)
