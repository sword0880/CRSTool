# AI 双入口只读查询 POC（MCP / OpenAI Function Calling）

> 状态：**PR #31、#32 已合并 main，公开 ETF 双入口只读 POC 与进程内失败关闭审计门禁已具备代码基础；尚未正式部署**。本 PR 新增本地审计落盘基础设施，仍需 Windows CI 和系统安全验收。此前 PR #31 Windows CI 的 AI 编译和离线测试通过，完整 UI 工作流的其他测试曾失败。本记录对应 [统一 AI Gateway 架构](../architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) 与 [MCP 只读方案](../architecture/AI_MCP_INTEGRATION.md)，不是生产能力发布声明。基线：2026-11-11 main@884394f。

## 实际工作范围

本期新增三个 .NET 项目：

- `CRS.AI.Contracts`：BCL-only 工具契约、授权调用者标记、结果、数据模型。
- `CRS.AI.Query`：只通过 `CRS.Worker.Ipc` 连接 Worker 的窄只读桥接；固定操作、数据脱敏、限制分页/时间和字节。
- `CRS.AI.Adapters`：提供 `McpReadToolAdapter` 和 `OpenAiFunctionReadAdapter`，共享同一 `IAiReadGateway`，同一份工具 Schema 与业务响应。

**没有增加 HTTP 监听、MCP SDK、OpenAI API 访问、密钥、个人保险库访问、SQLite 连接、Quartz 或业务数据副本。** 两个适配器目前是可测试的协议描述/函数分发层，不是已经连接云端的网络入口。

## 已确认 Worker 工具映射

| 对外工具名（新增） | Worker 现有 operation | 来源与实际能力 |
| --- | --- | --- |
| `crs_get_etf_overview_v1` | `overview` | `NationalTeamBackgroundSession.Handlers.cs`，调用 `workspace.Etf.OverviewAsync`，只映射已发布市场观察值，不对国家队买卖归因 |
| `crs_get_etf_coverage_v1` | `coverage` | 同文件调用 `workspace.Etf.CoverageAsync`，返回已登记日历覆盖、质量与有限日期状态，不当作全部历史覆盖验收 |

实际 IPC 传输：`BackgroundRequest(WorkerProtocol.Version=2, Command="QueryPlugin", PluginId="crs.national-team")`；业务为 `EtfBusinessProtocol` V1。工具不接收原始 SQL、文件路径、operation、采集命令或用户自行上报的授权字段。

- overview 参数必须为空对象；最多 64 条公开摘要，只返回 Market、Ticker、Name、AsOf、Shares、Change1/5/20、Status、CoveredDays。
- coverage 需要 SSE/SZSE、六位 ticker、from/to，时间窗不超过 30 天；只显示聚合质量和 Date/State。缺口和日历未核验返回 NotReady。
- 单次最多 64 条、8 页、48 KiB JSON、15 秒。跨页必须使用相同 Snapshot，响应无效则失败关闭。
- 保留 null，不补造 0；未从现有 DTO 取得完整证据/发布时点的字段不伪造；整体 ETF 份额变化不能证明机构真实买卖。
- 错误统一编码，不回显 SQL、文件路径、异常内容或原件；服务停机不自动回源采集。

## 权限与发布阻断

目前 Worker 对普通已认证 Windows 用户存在 `QueryPlugin` 读取入口，内部还包含 `release-query` 等状态操作。新 AI Facade 将 operation 绑定为上述两个固定值，但**此约束尚不足以放开对公网客户端的访问**。

下一阶段必须先完成专用 Gateway service SID、Worker 对该 SID 的二次操作白名单、Pipe ACL、调用者实际鉴权与 token 撤销；对原查询开展 DB 前后差异检查，证明无业务写副作用。不能将这个由可信宿主构造的 `AiCaller` 直接反序列化自模型请求。

地区、数据跨境、OpenAI API Key/预算、MCP OAuth/TLS/Host/Origin/Tunnel 等均需通过 P0 安全验收后才能对外连接。Worker 无个人 SQLCipher 密钥，个人 Performance/税务内容仍不可读取。

## P1 增量：共享调用预算与失败关闭审计（2026-11-11）

新增 `CRS.AI.Query/AiReadPolicyGateway.cs`：通过 `AiReadGatewayFactory.Create(worker, audit, limits)` 创建唯一受控的 `IAiReadGateway`，供 MCP 与 OpenAI 适配器共享。底层 `AiReadQueryService` 改为程序集内部可见，降低宿主直接越过准入层的机会。

- **两阶段审计**：Worker 查询前写入 `Attempt`，释放结果前写入 `Outcome`。审计失败时返回 `AuditUnavailable`，不得读取 Worker（前置失败）或向模型释放已取得的响应（后置失败）。
- **最小日志**：事件只包含 requestId、UTC 时间、主体 SHA-256 指纹、工具白名单名、阶段、决策、入参 SHA-256 指纹。绝不写原始主体、提示词、工具参数、业务响应或异常堆栈。生产审计 sink 需提供持久化、访问控制、保留周期与恢复验收；本次仅定义接口和离线伪实现，**尚无生产 sink**。
- **进程内预算**：单主体滑动一分钟调用上限、全部主体共享并发槽、有限主体表；预算使用单调计时 `Stopwatch`，跨两入口由同一个网关实例维护；拒绝请求也写审计。当前默认 20 次/分钟/主体、总并发 2、主体槽 128；仅用于本机 POC。
- **错误约束**：不回传未列入工具目录的原始工具名；审计、预算、并发和未知异常均只返回固定错误码。真实调用者凭证仍必须在接入层校验，`AiCaller` 绝不能由模型内容生成。
- **离线回归**：请求审计失败不触达 Worker、结果审计失败不泄漏数据、预算共享、并发阻断、指纹不等于原始字段；保留先前只读、质量和分页测试。

**剩余生产阻断**：本增量不能替代 Windows Worker Pipe SID/ACL + 二次业务工具授权、真实 OAuth/MCP 鉴权、账户级持久限额、审计落地持久性和机密存储、撤权、地区及数据外发审查。未经这些核验禁止放开 HTTP、Tunnel 或私有库。更不能把上述内存预算说成可跨服务实例计费保障。

## P1.5 增量：本地只追加审计文件写入器（本 PR）

新增独立 `CRS.AI.Infrastructure` 项目及 `AiAppendOnlyFileAuditSink`，实现现有 `IAiReadAuditSink`，供两个入口共享的 `AiReadGatewayFactory.Create(worker, audit, limits)` 注入。它不会调用模型、监听端口、读取个人数据或改变 Worker 的权限。

- 只接受可信宿主提供的**绝对本机路径**。日志文件和所在目录必须由安装/运维方提前创建并配置访问控制；组件不会自动创建文件或设置 NTFS ACL。未配置、被删除或指向目录联接/符号链接时，审计失败关闭。
- 每条审计记录仅序列化 `AiReadAuditEvent` 七个最小字段，以 UTF-8 JSONL 追加，并尝试 `WriteThrough` 和 `Flush(true)` 刷盘。写入器采用进程内互斥且禁止同一时刻由其他写入器占用；多实例共享文件不是已支持的场景。
- 文件尾部无换行视为可能存在未完成记录，拒绝继续写入；默认上限 64 MiB（可信宿主可设 4 KiB 至 1 GiB），到达上限不覆盖、不删减、不自动轮转，调用立即转为 `AuditUnavailable`。上线前需要制定停写后的离线归档及恢复流程。
- 若 Worker 查询期间文件丢失，`Outcome` 无法写入时仍丢弃业务结果。此操作通过真实本地文件和伪 Worker 离线测试验证。
- `AiReadPolicyGateway` 原有两秒审计写入截止时间保留；不得把这个文件实现视为安全文件系统、审计防篡改或跨进程额度治理。物理介质失败、恶意管理员篡改、事后留存、备份、OS 身份、文件 ACL、进程崩溃/突然断电恢复仍需独立验收。

### 部署前置条件（当前尚未实施）

1. 使用可信 Windows 服务安装流程预创建专用审计目录和空 JSONL 文件，不使用用户临时目录、共享盘或代码仓库路径。
2. 只授予实际 Gateway 服务 SID 所需的读/追加权限，并限定管理员与独立审计/备份角色；验证一般交互式用户不能写入或删除。当前代码为 `ReadWrite` 打开方式，服务 SID ACL 必须匹配实际读写行为；**它不是操作系统级 WORM 防篡改存储**。
3. 设计容量预警、日志完整性验证、受控归档与不可变远端备份，再在安装阶段验证 ACL 和模拟磁盘满/权限丢失/文件缺失/重启恢复。
4. 先完成独立 Gateway SID、Worker Pipe ACL/二次业务权限和外部调用者鉴权，才考虑真正的 MCP HTTP、OpenAI API 和 Tunnel；本 PR 不提供网络能力。

### 本次补充离线验收

`CRS.Tests.AiQuery` 增加真实本地文件测试：两阶段 JSONL 结构、RequestId 关联、主体和参数不落明文、未预创建文件时不查询 Worker、尾部残缺、容量上限、完成审计失效时不释放结果；其余旧工具与权限回归继续保留。**提交代码不等于 Windows 构建已通过**，以 PR 对应的 GitHub Actions 记录为准。

## 验证命令与验证证据

在 Windows .NET 10 SDK、已恢复 NuGet 的环境运行：

```powershell
dotnet restore CRS.sln --locked-mode
dotnet build CRS.sln -c Release --no-restore
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build
pwsh -File tests/Verify-Architecture.ps1
```

测试工程目前以伪造 Worker 响应验证：两入口一致性、拒绝未授权/未知工具/非法入参、数据 null、字段脱敏、质量不足、错误响应、分页越界、服务不可用、取消与只读命令路径。

**验证声明**：PR #31 的 Windows 工作流 [run 43028261293](https://github.com/sword0880/CRSTool/actions/runs/43028261293) 已完成 Restore、Release Build、AI 查询 POC 和架构检查；完整工作流在后续非 AI 的 PluginRunner 故障隔离测试发生 TaskCanceledException 而失败。因此仅可称 AI 离线 POC 在该次 CI 获得通过证据，不能称整体 CI 通过或已完成 Windows SCM / 真实财务数据/发布验收。新审计与准入代码的验证以新 PR CI 为准。

## 下一阶段实施次序

1. 修正任何 PR CI 编译、lockfile、架构门禁失败并记录回执；
2. 用真实 Worker 公开研究样本验证两种查询的数值、分页、数据版本、只读副作用；
3. 单独 PR 实现最小安全 Gateway，先本地只读鉴权，随后 OpenAI Responses API 与 MCP Streamable HTTP 接线；
4. 最后完成远程授权、Tunnel、费用审计、WPF UI 与正式安装/安全验收。任何阶段不得绕过地区与隐私使用限制。
