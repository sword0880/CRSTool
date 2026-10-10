# 系统架构

## 当前实现：五个生产项目（V1 安全核心已接入）

Domain 保存交易事实、FIFO 与税额／状态规则，不引用外层。Application 仅引用 Domain，编排用例并声明端口。Infrastructure 实现解析、SQLite、日志、设置、配置、Flex 和输出。Desktop.Wpf 负责展示、交互及启动装配。

Views／ViewModels／Controls 不能访问具体基础设施；Composition 可以创建适配器。完整目录见 [项目结构](PROJECT_STRUCTURE.md)。

## 执行与状态

DesktopWorkflow 执行后台计算、保存和导出；AnnualInputPreparation 检查期初与报告；AnnualReconciliation 核对持仓、收益与现金。CalculationService 封装结果，FifoEngine 和 TaxEngine 完成金额运算。

WorkspaceState 管理跨页面忙碌状态和结果失效。富途收入与交易选择分别存于 ImportViewModel，提交时形成同一个请求。后台运行时不可改输入。历史页只恢复冻结结果。

## 本机设置

IDesktopSettingsService 由 DesktopSettingsStore 实现，持久化、路径校验和原子写入在 Infrastructure。SettingsViewModel 只收集选项。启动组合根读取设置后创建 LocalStore 并配置 NLog；保存后下次启动使用新目录，当前数据库不移动。

SQLite + Dapper，user_version=1；NPOI 处理 Excel，PdfPig 处理已支持文本 PDF，NLog 输出脱敏日志。设置及默认路径见 [设置说明](SETTINGS.md)。

## 重放与限制

ReplayExecutionContext 校验历史口径，禁止缺失冻结汇率时回退当前配置，保留原临时状态和指纹。TimeProvider 支持跨年验证。v2 快照保留原始期初及完整结转，重放重新校验并合入一次；业务输出比对通过后另存父子运行。执行版本封存、旧格式转换等剩余工作见 [当前问题](KNOWN_ISSUES.md)。

只在用户选择 Flex 下载时执行该联网用例；文件计算本地执行。界面测试使用隔离数据库。虚拟化和卖出索引不等于已完成真实性能基准。

## 数据安全与用户体系（规划，尚未实现）

**唯一详细设计规范：** [SECURITY_DESIGN.md](SECURITY_DESIGN.md)。现有程序采用 **SQLCipher + Dapper 加密数据库**，默认路径、`user_version=1`、目录设置生效规则不变；当前已实现 SQLCipher、主密码解锁、加密备份与恢复；独立加密报表导出仍未实现。现状的敏感数据披露见 [PRIVACY.md](PRIVACY.md)。

### 当前安全架构：五个生产项目

现有源码已在 `Domain / Application / Infrastructure / Desktop.Wpf` 外**新增独立 `CRS.Security` 类库作为第五个生产项目**，而不是把密码学核心放在 `Infrastructure/Security` 中。

- `CRS.Domain`：继续处理交易事实、FIFO、汇率与税额规则；**不引用**本地认证、`CRS.Security`、数据库或 WPF。
- `CRS.Application`：依旧**只引用 `CRS.Domain`**。在 `Abstractions` 声明保险库创建、解锁、锁定、恢复、备份、状态检查等业务用例所需端口；不调用 Argon2id/AES-GCM 或持有裸 DEK。
- `CRS.Security`：新增独立的纯安全核心类库（建议 `net10.0`），负责 Argon2id 密码派生、随机 DEK/恢复密钥生成、AES-256-GCM 密钥封装与验签、密钥材料处理及格式校验。**不依赖** `Application`、`Domain`、`Infrastructure`、WPF、Dapper 或 SQLCipher；不操作业务数据库、文件路径及 UI。
- `CRS.Infrastructure`：实现 Application 安全端口，**引用 `CRS.Security`**；`Security` 适配器编排保险库会话与密钥操作，`Persistence` 管理 SQLCipher 连接/文件类型检测，`Backup` 负责加密备份的持久化与恢复，保留现有 Dapper 模型。
- `CRS.Desktop.Wpf`：负责新建、解锁、锁定、恢复、旧明文库不支持提示及设置；**Views/ViewModels/Controls 只使用 Application 用例**，不得接触 DEK、密钥派生库和具体持久化适配器。
- `Composition`：在启动装配处注册 Security 纯核心与 Infrastructure 适配器，区分“不存在数据库”“明文 SQLite（拒绝）”“受支持的加密保险库”“格式未知/不受支持/已损坏”；只在用户指定的全新空位置创建加密库，禁止自动回退明文或覆盖。

**目标依赖方向：** `Application → Domain`；`Infrastructure → Application + Domain + Security`；`Desktop.Wpf` 通过 Composition 引用 `Application/Infrastructure`。`Security` 不向业务项目反向引用；所有业务页面仍经 Application 访问。增设 `CRS.Tests.Security` 独立验证密码学核心，架构检查脚本须检验新的项目引用边界。

### 关键数据与兼容约束

主密码通过 Argon2id 派生 KEK，封装独立随机 DEK；DEK 供 SQLCipher 开库。恢复凭证独立于 Windows SID/DPAPI，离线可迁移。密码/密钥不得进入日志、设置和报表。数据库结构版本与保险库加密格式版本分开管理。

**明文 SQLite 数据库不兼容新安全版本，不开发迁移工具。** 检测到明文 `crs.db` 后仅提示不支持、拒绝读取；用户主动在新空目录创建保险库，旧文件和 WAL/SHM 保持原样，历史数据不会被自动继承，必要时只能根据原始券商文件重新导入。已加密保险库的后续结构升级才要求备份、兼容验证和失败回滚。安全改造不能改变既有冻结复算与税务计算行为。

V1.0 目标为本地保险库及加密备份；V1.5 公开版目标包含可选加密报表导出、多个保险库隔离和发布安全门槛；在线注册及云同步不在当前范围。实现计划与待验收项见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。
