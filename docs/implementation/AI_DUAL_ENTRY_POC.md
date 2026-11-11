# AI 双入口只读查询 POC（MCP / OpenAI Function Calling）

> 状态：**候选源码已提交，尚未构建/自动测试/正式部署**。本记录对应 [统一 AI Gateway 架构](../architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) 与 [MCP 只读方案](../architecture/AI_MCP_INTEGRATION.md)，不是生产能力发布声明。基线：2026-11-11 main@944fcac。

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

## 验证命令（尚未执行）

在 Windows .NET 10 SDK、已恢复 NuGet 的环境运行：

```powershell
dotnet restore CRS.sln --locked-mode
dotnet build CRS.sln -c Release --no-restore
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build
pwsh -File tests/Verify-Architecture.ps1
```

测试工程目前以伪造 Worker 响应验证：两入口一致性、拒绝未授权/未知工具/非法入参、数据 null、字段脱敏、质量不足、错误响应、分页越界、服务不可用、取消与只读命令路径。

**验证声明**：当前运行环境无 .NET SDK 且不能直接从 GitHub 克隆；因此没有完成编译或测试。待 PR CI 与真实 Windows SCM 验收后，才允许更新为 Verified。

## 下一阶段实施次序

1. 修正任何 PR CI 编译、lockfile、架构门禁失败并记录回执；
2. 用真实 Worker 公开研究样本验证两种查询的数值、分页、数据版本、只读副作用；
3. 单独 PR 实现最小安全 Gateway，先本地只读鉴权，随后 OpenAI Responses API 与 MCP Streamable HTTP 接线；
4. 最后完成远程授权、Tunnel、费用审计、WPF UI 与正式安装/安全验收。任何阶段不得绕过地区与隐私使用限制。
