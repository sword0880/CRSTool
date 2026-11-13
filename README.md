# 小财牛

**财富有数，投资有度**

小财牛（工程标识 CRSTool）是面向个人的 Windows 本地投资分析与税务辅助工具，覆盖券商数据导入、交易与 FIFO 成本核对、税额辅助计算、投资绩效，以及独立后台的公开市场研究。计算与预警不代表税务申报或投资建议；程序不自动报税、不执行交易。

## 当前项目状态（2026-11-13）

- 技术基线：Windows 10 及以上、.NET 10、WPF；后台使用独立 Windows Service 与 Worker IPC。
- AI：MCP / OpenAI 双入口、只读工具、模型适配及 WPF/MCP 统一附件处理代码已合并 main。MCP 当前仍是本机 loopback；远程 ChatGPT 连接、真实账号附件往返和生产部署尚未验收。
- 附件：首版仅支持 UTF-8 TXT、MD、CSV、JSON；不将原始文件或解析正文持久化到 CRS 聊天历史。
- 发布：ReleaseApproved 仍为 false。最新相关 Windows 工作流的 Release Build 与 AI 专项步骤通过，但整体在 Worker 插件监督测试处失败，不能视为全绿。

## 启动

环境：Windows 10（build 19041）或更新版本、.NET 10 SDK。打开 CRS.sln，选择 CRS.Desktop.Wpf，或在仓库根目录执行：

```powershell
dotnet run --project src/CRS.Desktop.Wpf
```

首次使用需创建加密保险库并配置登录验证；已有数据库仅支持受支持的加密格式，旧明文 SQLite 不会自动迁移。认证、恢复与数据保护要求见[保险库与登录指南](docs/guides/VAULT_GUIDE.md)。

## 文档入口

- [文档中心](docs/README.md)：所有当前权威文档与专题导航。
- [当前架构](docs/architecture/CURRENT_ARCHITECTURE.md) · [架构状态](docs/architecture/ARCHITECTURE_STATUS.md) · [未关闭问题](docs/KNOWN_ISSUES.md)。
- [发布验收](docs/validation/RELEASE_ACCEPTANCE.md) · [测试命令](tests/README.md)。
- [券商导入](docs/guides/IBKR_IMPORT_GUIDE.md) · [富途导入](docs/guides/FUTU_CSHARP_IMPORT_GUIDE.md) · [设置](docs/guides/SETTINGS.md)。
- [AI Gateway / MCP 目标架构](docs/architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) · [AI 双入口与附件实现状态](docs/implementation/AI_DUAL_ENTRY_POC.md)。

目录职责：src 为生产代码，tests 为测试与验收，docs 为设计、操作和证据，tools 为维护脚本。设计目标、源码实现、隔离测试、正式部署和业务验收必须分别判断，不能互相替代。
