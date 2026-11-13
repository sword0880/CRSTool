# AI 双入口只读查询 POC（MCP / OpenAI Function Calling）

> PR #31 至 #33 已合并 main，涵盖公开 ETF 双入口、共享审计门禁和本地审计落盘。当前源码已加入 Worker 服务身份隔离、Gateway 本机服务、Windows 用户授权和撤销，以及独立的本机 MCP 和 OpenAI Responses 命令行入口。真实模型和正式部署状态见本文末尾。架构见 [统一 AI Gateway 架构](../architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) 和 [MCP 只读方案](../architecture/AI_MCP_INTEGRATION.md)。

## 实际工作范围

初期新增三个 .NET 项目：

- `CRS.AI.Contracts`：BCL-only 工具契约、授权调用者标记、结果、数据模型。
- `CRS.AI.Query`：只通过 `CRS.Worker.Ipc` 连接 Worker 的窄只读桥接；固定操作、数据脱敏、限制分页/时间和字节。
- `CRS.AI.Adapters`：提供 `McpReadToolAdapter` 和 `OpenAiFunctionReadAdapter`，共享同一 `IAiReadGateway`，同一份工具 Schema 与业务响应。

两个协议适配器共用工具契约与分发接口。独立的 `CRS.AI.Client` 负责传输，业务查询、授权及审计仍由 Gateway 和 Worker 执行。

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

Worker 侧的专用管道与操作白名单见下方本地身份隔离记录。Gateway 接收用户请求时仍需验证实际调用者和授权撤销。可信宿主根据已验证身份构造 `AiCaller`。

地区、数据跨境、OpenAI API Key/预算、MCP OAuth/TLS/Host/Origin/Tunnel 等均需通过 P0 安全验收后才能对外连接。Worker 无个人 SQLCipher 密钥，个人 Performance/税务内容仍不可读取。

## P1 增量：共享调用预算与失败关闭审计（2026-11-11）

通过 `CRS.AI.Query/Policy` 的 `AiReadGatewayFactory.Create(worker, audit, limits)` 创建受控的 `IAiReadGateway`，供 MCP 与 OpenAI 适配器共享。审计契约在 `Audit`，只读业务在 `Queries`，Worker 通信在 `Worker`。

- **两阶段审计**：Worker 查询前写入 `Attempt`，释放结果前写入 `Outcome`。审计失败时返回 `AuditUnavailable`，不得读取 Worker（前置失败）或向模型释放已取得的响应（后置失败）。
- **最小日志**：事件只包含 requestId、UTC 时间、主体 SHA-256 指纹、工具白名单名、阶段、决策、入参 SHA-256 指纹。绝不写原始主体、提示词、工具参数、业务响应或异常堆栈。生产审计 sink 需提供持久化、访问控制、保留周期与恢复验收；本次仅定义接口和离线伪实现，**尚无生产 sink**。
- **进程内预算**：单主体滑动一分钟调用上限、全部主体共享全局上限和并发槽。默认 20 次/分钟/主体、全局 60 次/分钟、并发 2、主体槽 128。仅通过准入的请求写两阶段审计，其中包括通过准入后的无效参数和未知工具。
- **错误约束**：不回传未列入工具目录的原始工具名；审计、预算、并发和未知异常均只返回固定错误码。真实调用者凭证仍必须在接入层校验，`AiCaller` 绝不能由模型内容生成。
- **离线回归**：请求审计失败不触达 Worker、结果审计失败不泄漏数据、预算共享、并发阻断、指纹不等于原始字段；保留先前只读、质量和分页测试。

**剩余生产阻断**：本增量不能替代 Windows Worker Pipe SID/ACL + 二次业务工具授权、真实 OAuth/MCP 鉴权、账户级持久限额、审计落地持久性和机密存储、撤权、地区及数据外发审查。未经这些核验禁止放开 HTTP、Tunnel 或私有库。更不能把上述内存预算说成可跨服务实例计费保障。

## P1.5 增量：本地只追加审计文件写入器（PR #33）

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
4. 对外发布前验收独立 Gateway SID、Worker Pipe ACL、二次业务权限和远程调用者鉴权。

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

默认离线测试覆盖双入口一致性、权限与参数、脱敏、空值、覆盖状态、分页、限额、审计和通信故障。

PR #31 的 Windows CI 中，Restore、Release Build、AI 查询 POC 和架构检查通过。该次完整工作流在 PluginRunner 故障隔离测试中因 TaskCanceledException 失败。[Windows CI 回执](https://github.com/sword0880/CRSTool/actions/runs/43028261293)

## 本机 Worker 公开样本验收（2026-11-11）

查询按同一 Worker 快照连续读取，核对已声明的总行数，提前结束或混页时丢弃结果。响应的 `DataVersion` 是公开投影内容的 SHA-256 指纹，供两入口比对内容。Worker 临时分页令牌和查询时刻不参与指纹计算。

在有权核验已安装 Windows 服务身份的本机终端执行以下命令。验收在两个本地适配器上分别使用缩小首页和正常分页，比较数据、状态与版本指纹，并检查两阶段审计记录数量。

```powershell
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build -- --live-worker SSE 510300 2026-11-02 2026-11-06
```

| 查询 | 样本结果 | 缩小首页 / 正常分页 |
| --- | --- | --- |
| ETF 概览 | 24 条，状态为 `Partial/FinancialAcceptanceNotEstablished` | 2 / 2 页 |
| SSE 510300 覆盖率 | 5 个登记交易日均有已接受记录，状态为 `Partial/RegisteredCalendarOnly` | 2 / 1 页 |

两入口数据及版本指纹一致，四次调用生成 8 条本地审计事件。查询前后对公开研究目录的 `market.db`、`market.db-wal`、`source-policy.db` 分别计算 SHA-256，三个文件的指纹均一致。该比较覆盖本次样本时段。独立服务身份、正式审计目录和远程调用鉴权仍需单独验收。

## Worker 本地服务身份隔离（2026-11-12）

- 候选 Worker 增加 `CRS.Worker.Service.v2.ai-public`。管道访问控制列表仅给 Worker 所有者管理权限，给 `NT SERVICE\CRSAiGateway` 精确读写权限。Worker 从 Windows 令牌核对登录账户 SID，要求使用专用虚拟账户。桌面和管理管道对该 SID 显式拒绝。Gateway 客户端连接后核对管道进程与 SCM 中的 Worker PID。 [Windows 虚拟账户](https://learn.microsoft.com/windows/security/identity-protection/access-control/service-accounts) · [命名管道访问权限](https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights)
- 分派前校验请求只能是 `QueryPlugin` → `crs.national-team` → `overview` 或 `coverage`。只接受约定字段，限制日期、证券标识、分页和业务协议版本。释放快照、导出、研究提交、读取设置和文档以及任务命令均在控制层之前拒绝。
- Gateway 宿主使用 `WorkerPublicReadClient.ForGateway()`，服务启动账户使用 `NT SERVICE\CRSAiGateway`。两条原有管道继续服务桌面调用。上一节的桌面诊断方式可用于旧版 Worker 的本地样本验收。

隔离测试使用随机命名管道和合成查询。可在管理员终端加 `--scm` 启动一次性测试服务，核验真实虚拟账户及拒绝路径。测试服务仅使用该组隔离管道，结束后移除本次创建的 SCM 注册。

```powershell
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --ai-gateway-worker-boundary
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --ai-gateway-worker-boundary --scm
```

本机 Release 构建、架构检查、59 项 AI 离线断言、12 项原有管道断言和 4 项隔离管道断言通过。SCM 以专用虚拟账户启动夹具。两项公开查询进入合成控制层，11 条越权或畸形请求均返回拒绝。测试结束后核对临时服务已删除。已安装 Worker 保持运行，使用桌面诊断入口复跑公开样本通过。

## Gateway 本机服务与用户授权

候选 `CRS.AI.Gateway` 以 `NT SERVICE\CRSAiGateway` 运行。用户管道从 Windows 连接令牌取得实际账户 SID，管理管道要求管理员令牌。命令行在发送请求前核对管道进程与 SCM 中的 Gateway PID，只授予服务读取身份的权限。退出身份识别回调后，Gateway 使用服务身份访问审计及 Worker。 [Windows 身份识别级别](https://learn.microsoft.com/windows/win32/secauthz/impersonation-levels)

首次安装时授权表为空。管理员按账户 SID 授权或撤销，变更刷盘并原子替换后确认。撤销取消该账户进行中的查询，结果写入管道前复核原授权代次，重新授权只对新请求生效。客户端断开后可以按原 SID 重试同一授权命令。

`%ProgramData%\CRSAiGateway\Authorization\subjects.json` 保存允许名单，`Audit\events.jsonl` 记录工具调用的最小两阶段事件。安装器为数据目录和文件预设专用 ACL，服务启动时核对访问权限和路径。审计保留周期和离线归档仍需随正式运维验收。

构建候选包后，可在管理员终端执行首次安装。当前候选 Gateway 查询需要支持 `.ai-public.v2` 的 Worker。服务状态只确认本机 Gateway 入口就绪。

```powershell
dotnet publish src/CRS.AI.Gateway -c Release -o artifacts/ai-gateway
pwsh -File tools/ai/Install-AiGateway.ps1 -PackageDirectory artifacts/ai-gateway
$gateway = Join-Path $env:ProgramFiles 'CRSAiGateway/CRS.AI.Gateway.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& $gateway --grant $sid
& $gateway --call crs_get_etf_overview_v1
& $gateway --call crs_get_etf_coverage_v1 '{"market":"SSE","ticker":"510300","from":"2026-11-02","to":"2026-11-06"}'
& $gateway --revoke $sid
```

管理员终端的隔离验收使用随机命名管道和合成 Worker。

```powershell
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build -- --local-gateway
```

本机 13 项隔离断言通过，覆盖身份伪造、授权、撤销在途请求、重新授权后隔离旧结果、重载状态及审计脱敏。临时安装验收通过了真实 SCM 虚拟账户启动、状态核验、授权及撤销后的重启持久化，并写入 8 条审计事件。已核对测试服务及目录已清理。当前安装的 Worker 继续运行，尚无 `.ai-public` 管道，经候选 Gateway 发起公开查询返回 `UpstreamUnavailable`。与新版 Worker 的真实数据联查待验证。

临时安装发现显式断开管道会丢失尚未读取的管理响应。Gateway 在回复后创建下一监听实例并关闭当前服务端句柄，延迟读取回归已通过。[Windows 命名管道断开语义](https://learn.microsoft.com/windows/win32/api/namedpipeapi/nf-namedpipeapi-disconnectnamedpipe)

## 反方审查与加固

- **持久审计可被拒绝流量耗尽。** 将鉴权、全局与主体预算、并发限制移到落盘前。通过准入的查询依然先审计再访问 Worker、先审计再释放结果。同步刷盘阻塞或写入器忽略取消时，审计等待最多两秒；未结束的写入保有唯一后台写入槽。已取消的请求不能释放晚到的查询结果。
- **中断的 AI 分页可能占满桌面共享快照池。** Gateway 使用版本化专用管道 `CRS.Worker.Service.v2.ai-public.v2`。Worker 从白名单映射到专用单页读取，校验完整返回，最多 64 行和 46,000 个业务载荷字节。专用路径不存放桌面快照，也不接受外部游标。旧版桌面诊断仍沿原 V1 分页协议。发布时需成套升级 Gateway、Worker 与 NationalTeam 插件。
- **覆盖率汇总可能掩盖不完整或矛盾的明细。** AI 根据请求区间、去重后的逐日状态和已登记交易日重算计数与比例，并复核日历状态。矛盾结果统一返回 `UpstreamInvalid`。一致但未通过质量条件的记录继续标记 `NotReady`。

验证通过 74 项 AI 离线断言、13 项本机 Gateway 断言、4 项真实 Windows 服务身份断言和 25 项分页断言。连续九次超限 AI 查询后，桌面可建立全部八个快照。桌面快照池占满时，AI 覆盖率仍能返回完整单页。已安装 Worker 的 V1 诊断复跑得到 24 条 ETF 概览及 510300 的 5 个已接受登记交易日。

## AI 读取准入与审计归档整改

- Worker 宿主、NationalTeam 查询会话和数据读取分别为 AI 公开查询预留一个独立名额，等待数为零。桌面有在途或排队的研究读取时，新 AI 请求返回繁忙。已开始的 AI 请求可以与之后进入的桌面读取重叠，宿主给 AI 八秒执行预算。AI 读取沿用一致性快照，原件核验继续优先处理桌面读取。维护会取消并排空两类查询。
- Gateway 新增管理员命令 `--archive-audit`。先停止 Gateway 服务。命令独占维护锁，校验每条现有记录，将原字节刷盘到 `Audit\Archive\<SHA-256>.jsonl`，核对归档指纹后再用同目录空文件替换活动日志。新文件继承原活动日志权限，归档仅管理员和系统账户可访问。相同内容的重试复用已校验副本，畸形活动文件和指纹不符的同名归档保留供核对。 [Windows 文件替换语义](https://learn.microsoft.com/windows/win32/fileio/moving-and-replacing-files)
- 首次启动或归档时按需建立维护锁，适用于现有审计目录。归档历史由运维方按留存要求转存。

```powershell
$gateway = Join-Path $env:ProgramFiles 'CRSAiGateway/CRS.AI.Gateway.exe'
Stop-Service -Name CRSAiGateway
& $gateway --archive-audit
if ($LASTEXITCODE -ne 0) { throw '审计归档未完成。' }
Start-Service -Name CRSAiGateway
```

核验命令（管理员终端）：

```powershell
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build -- --audit-archive
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --ai-read-admission
```

验证通过 10 项 AI 混合读取断言、9 项归档断言、原有查询分组及分页回归。临时 Windows 服务运行时拒绝归档，停止后归档指纹与原日志一致，重启后成功续写活动日志。该验收使用临时 Gateway 与合成读取夹具。

正式服务保持现有版本。生产真实峰值下的磁盘与 CPU 延迟及长期审计留存仍待验收。

## 本机 MCP 与 OpenAI Responses API

Visual Studio 在 `src / AI` 下显示 `CRS.AI.Client` 和 `CRS.AI.Models`。前者提供 MCP 与 OpenAI 命令行入口，后者负责供应商适配、模型配置和工具分发。两条入口与桌面共用已安装 Gateway 的 Windows 命名管道，Gateway 依据客户端进程的用户令牌准入并审计。实际查询需配套部署支持 `.ai-public.v2` 的 Worker 和 NationalTeam 插件，并为运行客户端的 Windows 账户授予查询权限。

### 本机 MCP

以下命令在 PowerShell 7 中运行。使用本次启动生成的密钥，在同机 MCP 客户端中配置 Streamable HTTP 地址及 `Authorization: Bearer <CRS_MCP_TOKEN>`。访问 `/health` 需要同一凭证，返回监听状态。

```powershell
$env:CRS_MCP_TOKEN = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
dotnet run --project src/CRS.AI.Client -c Release --no-build -- --mcp 8765
```

MCP 端点为 `http://127.0.0.1:8765/mcp`，仅监听回环地址，检查 Host、Origin 与 Bearer 凭证。运行时的 Windows 身份属于启动该进程的账户，持有本机令牌的应用可以通过它调用该账户获准使用的两个公开 ETF 工具。请仅将令牌提供给信任的同机客户端。

### OpenAI Responses API

在受支持地区的联调主机上，为当前进程配置 `OPENAI_API_KEY` 和支持 Responses Function Calling 的 `OPENAI_MODEL`，然后在 PowerShell 7 中运行公开 ETF 样例。该命令向 OpenAI 发送标准输入中的问题及模型请求的公开 ETF 工具结果。

```powershell
'调用 CRS 公开 ETF 概览工具，列出返回条数、数据版本和限制。' |
    dotnet run --project src/CRS.AI.Client -c Release --no-build -- --ask
```

请求使用 `store=false`，每轮最多一次工具调用，每条问题最多四轮模型请求、三次工具调用及 90 秒。程序按实际回执显示模型请求次数、工具调用次数及 token 用量。开发命令依赖项目本身的账户计费及限额。 [OpenAI 函数调用](https://developers.openai.com/api/docs/guides/function-calling) · [OpenAI API 支持地区](https://developers.openai.com/api/docs/supported-countries)

### 验收与部署状态

```powershell
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build
# 管理员终端，使用临时命名管道、回环端口与合成 Worker 数据
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build -- --client-integration
```

官方 MCP 客户端在本机完成握手、工具列表及查询。Responses 请求、工具回传、预算保护与错误处理通过模拟响应验收。两条入口均经真实 Windows 命名管道验证授权和撤销。真实 OpenAI 调用、新版 Worker 正式联查及 ChatGPT 远程入口尚待验收。远程 OAuth 和 Tunnel 仍在后续交付范围。

## 桌面 AI 模型设置

在「设置 → AI 模型」选择 DeepSeek、OpenAI、小米 MiMo 或 Claude（Anthropic），再选择模型并填入 API Key，点击「测试并设为默认」。模型连接测试发送一条固定短提示。本机工具测试独立查询 Gateway，诊断结果不会发送到供应商。模型连接通过即可保存默认模型。密钥按供应商分别保存，Windows DPAPI 使用当前账户加密。切换回已配置的供应商时，密码框留空可继续使用原密钥。清除密钥会同时清除该供应商的本机模型设置。

主窗口右下角的「小财牛助手」覆盖所有业务页与插件页，收起或切页时保留当前对话。「弹出」可将同一面板放入置顶窗口，「停靠」可移回主窗口。侧栏「AI 聊天历史」可搜索最近 100 个匹配会话、查看、继续和删除。历史保存在当前已解锁的 SQLCipher 保险库。

「截取页面」只获取小财牛的当前业务区域，再由用户选区。「粘贴截图」读取剪贴板图像。发送前可以预览、裁剪或移除，只有勾选「在历史中保留截图」才加密保存原附件。后续问题可携带近期文字对话，历史图片不会自动重发。当前模型显示在面板顶部，切换模型后，原会话需切回相同模型才继续发送。窗口锁定时关闭置顶窗口和截图选区、取消在途请求并清理当前内容。

DeepSeek 使用官方 Responses API 的非思考模式。其同轮返回的函数调用按顺序执行，整条问题最多执行三次工具。OpenAI 继续保留原有的单轮单工具约束与加密推理项回传。 [DeepSeek Responses 兼容性说明](https://api-docs.deepseek.com/zh-cn/guides/responses_api/)

小米 MiMo 使用官方 Responses API，提供 V2.6 Flash 和 Pro。工具预算耗尽后省略工具定义。Claude 使用 Anthropic Messages API，提供 Haiku 5.5 和 Sonnet 5.5，按调用标识成组回传工具结果。MiMo Code 与 Claude Code 的 MCP 客户端可单独配置，桌面助手使用上述模型 API 与本机 Gateway。 [MiMo Responses](https://mimo.mi.com/docs/en-US/api/chat/responses-post) · [MiMo Code MCP](https://github.com/XiaomiMiMo/MiMo-Code) · [Claude 工具调用](https://platform.claude.com/docs/en/agents-and-tools/tool-use/how-tool-use-works)

如需让同机的 MiMo Code 使用小财牛工具，先按上文启动本机 MCP，将以下项目合并到个人的 MiMo Code 配置。启动 MiMo Code 的终端须能读取同一个 `CRS_MCP_TOKEN`，用 `mimo mcp` 查看连接状态。该令牌只用于本机 MCP 访问。

```json
{
  "mcp": {
    "xiaocainiu": {
      "type": "remote",
      "url": "http://127.0.0.1:8765/mcp",
      "oauth": false,
      "headers": { "Authorization": "Bearer {env:CRS_MCP_TOKEN}" }
    }
  }
}
```

```powershell
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build -- --model-settings
dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --settings-ui artifacts/settings-ai
```

上述验收使用合成模型响应、临时配置目录和测试凭证。真实供应商联调需在桌面填写已开通的 API Key。


## 附件能力增量（2026-11-13）

桌面助手与 MCP 新增统一附件处理，具体规则、协议、生命周期和开放问题见 [AI 助手附件处理](AI_ATTACHMENTS.md)。

- WPF 和 MCP 都调用 `CRS.AI.Models.Attachments.AiAttachmentParser`；Responses API 与 Anthropic/MiMo 将解析文本作为同一条 user message 的附件引用内容发送。
- MCP 增加 `crs_chat_start`、`crs_chat_message`、`crs_chat_remove_attachment`、`crs_chat_end`，通过 ChatGPT 官方 `openai/fileParams` 接收文件临时下载引用。
- MCP 传输继续保持 Stateless；附件使用独立的随机会话句柄和内存 TTL 仓储，不能依赖 HTTP 连接或 transport session。
- 附件原文不写入 SQLCipher 聊天历史、临时文件目录或日志；历史恢复不恢复附件内容。
- 当前首版仅开放 UTF-8 TXT/MD/CSV/JSON。PDF/DOCX/XLSX 尚未开放。
- 自动化 MCP 测试使用假下载器；真实 ChatGPT 账号文件上传、实际下载域名和 UI 授权提示仍需单独验收。不能把协议元数据测试写成真实 ChatGPT 端到端通过。
