# 当前架构

> 基线：2026-11-13，Git main。本文只描述当前源码中的分层、进程、依赖和数据所有权；部署证据见 [ARCHITECTURE_STATUS.md](ARCHITECTURE_STATUS.md)，未关闭事项见 [KNOWN_ISSUES.md](../KNOWN_ISSUES.md)。

## 1. 解决方案分层

| 领域 | 主要项目 | 职责与边界 |
| --- | --- | --- |
| 领域与应用 | CRS.Domain、CRS.Application | 业务规则、用例和应用端口；不依赖 WPF、MCP 或具体数据库 |
| 基础设施与安全 | CRS.Infrastructure、CRS.Security | 数据访问、加密、安全机制和外部基础设施适配 |
| 通用能力 | CRS.Common、CRS.Documents | Common 提供跨模块纯通用能力；Documents 提供通用 PDF/Excel 文档处理，不承载业务模板或金融计算 |
| WPF 桌面 | CRS.Desktop.Wpf、CRS.Plugin.Abstractions、CRS.Plugin.Wpf、CRS.PluginHost、src/Plugins | 主窗口、导航、设置、插件 UI 和用户交互 |
| Worker 后台 | CRS.Worker.Abstractions、CRS.Worker.Ipc、CRS.Worker.PluginHost、CRS.Worker.PluginRunner、CRS.Worker.Service | 后台契约、授权 IPC、插件执行、Quartz 调度、持久任务、健康和维护 |
| AI | CRS.AI.Contracts、CRS.AI.Query、CRS.AI.Adapters、CRS.AI.Infrastructure、CRS.AI.Gateway、CRS.AI.Client、CRS.AI.Models | 统一只读工具契约、查询、协议适配、审计、Gateway、MCP/Responses 入口和模型供应商适配 |
| 测试 | tests/CRS.Tests.*、tests/Plugin | 领域、应用、基础设施、Worker、WPF、AI、安全和架构验证 |

具体项目清单以 CRS.sln 和项目文件为准。目录名称或历史设计稿不能替代实际项目引用。

## 2. 进程与调用边界

| 进程/入口 | 主要职责 | 不允许越过的边界 |
| --- | --- | --- |
| WPF | 私人保险库交互、税务/绩效工作台、插件 UI | 不直接执行后台采集，不绕过 Application 直接操作业务库 |
| Worker Service | 无人值守公开数据采集、Quartz 作业、受控查询和维护 | 不持有用户个人保险库解密密钥，不承载 WPF 类型 |
| AI Gateway | 身份授权、审计、限流及受控 Worker 只读查询 | 不持有个人 SQLCipher 密钥，不开放任意 SQL、文件路径或写入业务工具 |
| AI Client / MCP | 本机协议入口和客户端适配 | 当前仅 loopback；不将本机 HTTP 端口直接暴露公网 |
| 模型适配 | Responses API 及已接入的其他供应商协议 | 模型输出不是金融事实，不回写账本、税表或发布事实 |

Worker Service 是唯一 Quartz 调度宿主。WPF 与 Worker 通过版本化 IPC/Named Pipes 契约通信；不得在 WPF 或 AI Gateway 中再创建第二套后台调度器。

## 3. NationalTeam 模块边界

- NationalTeam.Core：领域规则、DTO 和窄端口，不依赖 WPF 或数据库实现。
- NationalTeam.Jobs：采集、来源策略、类型化用例、Store、任务和一致性读写。
- NationalTeamWatch：工作台页面、展示模型和功能客户端，通过 Worker 授权管道交互。
- 公开市场研究数据与个人税务保险库分属不同数据域；ETF 份额、全市场成交额不能直接归因为国家队真实买卖。
- 具名披露、NAV、行业估值、周期和退出研究都必须保留来源、知识时点、修订、审核状态和缺口；资料不足时失败关闭。

## 4. 数据与存储所有权

| 数据域 | 当前存储/所有者 | 约束 |
| --- | --- | --- |
| 用户税务、交易和绩效保险库 | SQLCipher，由交互式桌面会话解锁 | 明文 SQLite 不自动迁移或覆盖；后台 Worker/AI 不得绕过用户解锁读取 |
| Worker 控制与任务数据 | 受保护的 SQLCipher 控制库 | 由 Worker 服务身份和维护协议管理 |
| NationalTeam 公开研究数据 | 普通 SQLite，由 Worker/领域 Store 管理 | 普通 SQLite 不等于可直接向外部开放；仍须通过授权只读查询 |
| AI 最小审计 | 受控本地 JSONL 审计文件 | 安装/运维方预创建目录和 ACL；不记录提示词、原始附件或业务响应 |

## 5. AI 双入口与附件

- WPF 内置助手通过受控 Gateway 使用 Responses API 或已配置的模型适配器。
- ChatGPT 等外部 MCP 客户端通过 MCP 协议调用同一受控只读工具边界；本机 MCP 当前只监听 127.0.0.1，远程连接需受支持的远程 HTTPS 或 Secure MCP Tunnel。
- 当前实际开放的公开研究查询工具只有 ETF overview 和 coverage 两类；目标架构中的其他工具不能写成已开放。
- WPF 与 MCP 共用附件解析策略。首版只接受 UTF-8 TXT/MD/CSV/JSON；原始文件不写入本机临时文件或聊天历史。完整规则见 [AI 双入口与附件实现](../implementation/AI_DUAL_ENTRY_POC.md#ai-附件处理)。

## 6. 依赖方向

- Domain 不引用 Application、Infrastructure、WPF、Worker、AI 或数据库实现。
- Application 依赖领域和抽象端口，不引用 UI、MCP SDK、OpenAI SDK 或具体存储。
- Common 保持纯通用能力，不放 AI、NationalTeam、税务或绩效业务规则。
- Documents 只提供通用文档处理；券商/税务解析规则归各自领域。
- Worker 插件通过版本化契约与窄端口工作；桌面 UI 不共享 Worker DI 容器或数据库连接。
- AI 适配器只调用固定白名单工具，不接收任意 SQL、路径、插件 operation 或用户自报身份。

## 7. 当前状态的判断规则

源码存在不代表已部署；离线测试通过不代表 Windows 服务安装成功；服务运行不代表金融数据覆盖或模型有效；模型输出不代表税务金额正确。发布判断只看 [发布验收](../validation/RELEASE_ACCEPTANCE.md) 及可复核证据。
