# 数据库存储抽象与替换边界

状态：2026-10-20实施约束，当前运行实现仍为SQLite/SQLCipher。此文不代表已支持或已验收其他数据库。

## 必须遵循的边界

- Domain、Application、WPF及插件用例以业务对象和仓储接口交互，不出现SQL、连接、事务对象、数据库异常或提供方类型。接口不返回IQueryable或未关闭的数据库读取器。
- 仓储接口按业务能力定义，例如IPerformanceStore、ITaskRepository和ISourceBudgetStore。接口表达读取、导入、保存冻结结果或来源预算状态，不建立接收任意SQL的“通用数据库接口”。
- 连接创建、连接字符串、参数绑定、方言、DDL、PRAGMA、数据库版本迁移及错误转换属于具体存储适配器；启动装配入口选择实现。适配器内部可复用连接/事务帮助类，但不将其传入业务层。
- 一次业务操作的原子性由仓储保证。例如绩效原件与版本元数据共同提交或共同回滚；来源预算预留必须持久提交后才能访问网络。禁止为了统一接口将事务拆成多次非原子调用。
- 数据库适配器将运行期提供方故障转换为稳定的存储错误语义，保留内部原因供诊断；读取失败不能返回空集合、默认值或零。业务校验、唯一性冲突和不可用应分别表达，重试必须考虑幂等与并发。
- SQLCipher密钥派生、设备绑定、加密备份和容量检查属于当前安全/存储实现。更换数据库时必须独立验证等价安全能力，不能用普通连接抽象声称已解决加密或备份迁移。

## 当前落地及缺口

| 范围 | 当前边界 | 仍需处理 |
| --- | --- | --- |
| 绩效 | Application的IPerformanceStore屏蔽具体数据库；导入及版本元数据在实现内同事务 | PerformanceStore内部仍含SQLite方言、DDL、容量PRAGMA和VaultSession；替换时应整体提供另一适配器并复用仓储契约验收 |
| Worker任务 | ITaskRepository隔离调度与任务仓储 | 18个公开仓储操作（含本地快照）在适配器内转换SQLite错误为IOException，业务冲突保持原分类；调度库迁移、并发及真实第二数据库仍需验收 |
| 共享来源预算 | SourceBudgetRegistry只依赖ISourceBudgetStore；Program选择SqliteSourceBudgetStore，运行期SQLite错误转为IOException | 当前为单Worker写入；多实例/远程库必须增加原子预留或并发控制，不能沿用独立Read/Save冒充跨进程安全 |
| 保险库 | VaultManager转发IVaultStorageBackend；SQLCipher认证、计算/绩效仓储、锁定和备份属于同一适配器族 | 新提供方必须完整实现会话与安全能力并验证数据迁移；当前只内置SQLCipher |
| 国家队插件 | NationalTeamStorageProviders同时选择市场仓储和ISourceRequestPolicy；入口/网关不创建具体数据库 | 新提供方仍需实现IMarketRepository、来源策略与迁移，不等同于已有第二数据库 |

ISourceBudgetStore由注册表串行调用并接管释放。Save必须持久、原子，损坏/未知状态必须拒绝使用；当前本地实现保留原数据库格式、密钥派生、冷却与故障锁定。内存替身仅用于验证规则独立性，不是生产替代数据库。

## 后续整改和验收

1. 按业务模块盘点持久化入口，优先去掉业务规则中直接创建具体数据库实现的代码；复用已有仓储接口，不另建重复抽象。
2. 逐模块补齐事务、唯一性、排序/分页、decimal精度、时间时区、空值、取消与重试的契约；数据库实现决定参数和方言。
3. 实施另一个数据库时，提供独立适配器、迁移和备份恢复方案；使用同一套仓储契约检查原子回滚、幂等、并发、故障和重启。内存测试不能替代真实数据库验收。
4. 数据迁移与历史快照不重算、不重签；财务结果必须以独立金额验收确认一致。应用层规则及UI不应因提供方替换而修改。

验证入口：tests/Verify-Architecture.ps1检查来源预算策略及端口不得引用具体数据库；CRS.Tests.WorkerEtf --source-budget同时验证替身端口和真实SQLCipher故障/恢复。更换数据库的目标是把改动集中在存储适配器、装配和迁移，不能承诺任意数据库零改动。

## Worker任务仓储异常边界续项

EncryptedTaskRepository在18个公开操作（17个ITaskRepository操作与本地Snapshot）外层转换运行期SqliteException为IOException，保留InnerException。原SQL、参数、锁与事务主体未改，事务释放/回滚完成后才转换异常。构造阶段的库归属、Schema迁移与加密校验仍在装配边界；不承诺旧二进制或其他数据库自动兼容。

BackgroundRuntime不再识别具体SQLite类型；已适配任务/来源存储使用IO异常，尚未统一转换的插件持久化故障暂以BCL DbException兼容分类，避免丢失原有失败关闭保护。这是过渡边界，并不等于所有插件已遵守稳定异常契约；后续需逐个插件存储适配器收拢，再移除该兼容分类。

任务仓储端口明确Save/Begin/End与关联命令状态的原子提交、业务冲突分类及不透明分页游标。专项入口 CRS.Tests.Background --task-storage-boundary：真实SQLCipher事务/读取注入12项，IO与通用DbException分别覆盖Begin/End/Audit/Progress故障锁定18项，共30项。通用异常夹具验证故障分类，不构成第二数据库集成验收。证据：artifacts/task-storage-boundary-checks.log。

## 可插拔结构与配置（2026-10-20）

用户本轮选择“暂不指定，先完成可插拔结构”。当前只内置sqlcipher，没有虚构PostgreSQL/SQL Server实现。受信任代码注册新适配器后，只改数据库类型配置即可选择已注册实现；首次更换仍需另行执行迁移、加密、并发和恢复验收，不自动将现有库转换格式。

两个进程均读取各自运行目录的 `config/storage-providers.json`，同一文件结构为：

```json
{
  "Version": 1,
  "Vault": "sqlcipher",
  "NationalTeam": "sqlcipher"
}
```

样例源文件为 `config/storage-providers.example.json`。部署时将所需配置放到对应桌面/Worker运行目录；运行中不热切换，修改后重启相应进程。缺省文件沿用sqlcipher；重复字段、未知版本、非法ID、未知提供方明确拒绝，不能回落内存或明文库。配置不保存连接密码或密钥。两处组合根独立解析同一配置契约，避免基础设施与业务插件产生项目依赖；Worker调度库/宿主预算库不受此两字段切换，应单独保持现有注册边界。

### 保险库

- `VaultStorageProviders`在装配时注册不可变的ID→工厂映射，拒绝覆盖内置ID。`VaultManager`持有选中的`IVaultStorageBackend`，全部公开认证、文档密码、仓储、绩效、备份恢复及锁定操作转发给同一实例。
- 原SQLCipher实现及其Session、认证表和快照代码归 `CRS.Infrastructure/Storage/SqlCipher`。原业务SQL与加密格式未改；新提供方替换整个存储族，不能让认证使用一种库而财务或备份偷偷使用另一种。
- `IVaultStorageBackend`不暴露连接/SQL；来源冻结输入仅公开只读流与摘要，不暴露底层可写字节。旧验收用的原生连接/时钟注入集中在适配器目录的测试钩子，不进入生产端口。
- 桌面组合根通过配置选择类型，DesktopOperations只依赖存储族端口。未来注册另一个实现时无需修改WPF、计算或绩效用例。

### 插件

- `NationalTeamStorageProviders`按类型选择`INationalTeamStorageProvider`，同族提供市场仓储与持久来源策略。会话先解析提供方，再创建目录、取密钥、构建网关与工作区。
- `OfficialHttpGateway`仅依赖`ISourceRequestPolicy`。SQLCipher来源策略在真实SQL入口转换故障；MarketRepository在ExecuteOperation用例边界转换数据库故障，SQL中断的取消语义保持不变。
- 新市场适配器继续实现既有`IMarketRepository`，来源策略必须满足持久冷却、不以未知为零及网络前预留的约束；原件存证仍通过独立证据存储能力处理。
- 注册工厂只接受随可信发行构建的代码，不从配置加载任意程序集、类型名或可执行内容。两个自定义注册测试都转发现有SQLCipher，只证明选择/接线，不代表第二种数据库集成验收。

验证入口：`CRS.Tests.Infrastructure --vault-providers`（10项）、`CRS.Tests.WorkerEtf --storage-providers`（10项）；完整回归与证据见实施续记。架构门禁禁止保险库入口暴露SQL、插件入口直接创建具体仓储，以及网关引用具体数据库策略。
