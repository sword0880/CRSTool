# 小财牛

**财富有数，投资有度**

小财牛（工程标识 CRSTool）是面向个人的 Windows 本地投资分析与税务辅助工具，覆盖券商数据导入、交易与 FIFO 成本核对、税额辅助计算、投资绩效，以及独立后台的公开市场研究。计算与预警不代表税务申报或投资建议；程序不自动报税、不执行交易。

## 当前项目状态（2026-11-27）

- 技术基线：Windows 10 及以上、.NET 10、WPF；后台使用独立 Windows Service 与 Worker IPC。
- Worker 更新：PR #46 已合并。新增 `WorkerUpdateService` 统一 GitHub 更新检查、异常分类、安装环境门禁、下载/安装协调及临时 ZIP 生命周期；ViewModel 只负责界面状态、命令和结果展示。
- 历史汇率复算：用户显式发起历史复算时，按复算时加载的年度汇率提供方生成新结果并关联原任务；原历史结果不被覆盖。专项测试已注入独立官方年末快照。
- 雪球观点追踪：仅跟踪指定作者，直接使用雪球时间线和原帖详情 API；按股票聚合；Google Alerts、Google RSS 和浏览器自动化不属于当前实现。
- AI：MCP / OpenAI 双入口、只读工具、模型适配及 WPF/MCP 统一附件处理代码已合并 main。MCP 当前仍是本机 loopback；远程 ChatGPT 连接、真实账号附件往返和生产部署尚未验收。
- 附件：首版仅支持 UTF-8 TXT、MD、CSV、JSON；不将原始文件或解析正文持久化到 CRS 聊天历史。
- 最新自动化验证：[PR #46 Windows CI](https://github.com/sword0880/CRSTool/actions/runs/45927491641) 通过，79/79 步骤成功；PR head 与 main 合并提交的 Git Tree 一致。
- 发布：ReleaseApproved 仍为 false（No-Go）。最新 CI 通过不等于正式 Worker 已部署，也不替代税务金额、绩效、数据覆盖和生产安全验收。

## 启动

环境：Windows 10（build 19041）或更新版本、.NET 10 SDK。打开 CRS.sln，选择 CRS.Desktop.Wpf，或在仓库根目录执行：

```powershell
dotnet run --project src/Desktop/CRS.Desktop.Wpf
```

首次使用需创建加密保险库并配置登录验证；已有数据库仅支持受支持的加密格式，旧明文 SQLite 不会自动迁移。认证、恢复与数据保护要求见[保险库与登录指南](docs/guides/VAULT_GUIDE.md)。

## 文档入口

- [文档中心](docs/README.md)：所有当前权威文档与专题导航。
- [当前架构](docs/architecture/CURRENT_ARCHITECTURE.md) · [架构状态](docs/architecture/ARCHITECTURE_STATUS.md) · [未关闭问题](docs/KNOWN_ISSUES.md)。
- [发布验收](docs/validation/RELEASE_ACCEPTANCE.md) · [测试命令](tests/README.md)。
- [券商导入](docs/guides/IBKR_IMPORT_GUIDE.md) · [富途导入](docs/guides/FUTU_CSHARP_IMPORT_GUIDE.md) · [设置](docs/guides/SETTINGS.md)。
- [AI Gateway / MCP 目标架构](docs/architecture/AI_GATEWAY_MCP_ARCHITECTURE.md) · [AI 双入口与附件实现状态](docs/implementation/AI_DUAL_ENTRY_POC.md)。

目录职责：src 为生产代码，tests 为测试与验收，docs 为设计、操作和证据，tools 为维护脚本。设计目标、源码实现、隔离测试、正式部署和业务验收必须分别判断，不能互相替代。
