# Worker服务目录

根目录只保留Program.cs启动入口、项目/依赖文件和本说明。按职责放置实现，命名空间继续使用CRS.Worker.Service，不因整理目录改变公共类型或运行标识。

```text
CRS.Worker.Service/
├─ Program.cs                 # Generic Host启动、SCM/控制台参数及服务装配
├─ Hosting/                   # 插件宿主上下文、身份/私有目录能力接线
├─ Runtime/                   # 调度执行、并发额度、通知命令与运行状态
├─ Sources/                   # 跨插件来源预算、公平队列与冷却规则
├─ Persistence/               # 数据库无关仓储接口
│  └─ SqlCipher/              # 当前SQLCipher仓储实现、SQL和事务
├─ Security/                  # 服务密钥、DPAPI与数据树权限
├─ Notifications/             # 宿主通知能力适配，不包含SMTP实现
├─ Network/                   # VPN 订阅加密存储与按节点管理 Mihomo HTTP 代理
├─ Diagnostics/               # 脱敏服务事件日志
└─ Deployment/                # 升级日志、归档校验与启动恢复门禁
```

数据库替换先保持Persistence接口，再增加提供方实现；Sources和Runtime不引用具体数据库类型。新增服务生命周期装配放Hosting，业务预算放Sources，不能再次堆回根目录。bin/obj由构建管理，不属于源代码目录。

架构说明见 ../../../docs/architecture/CURRENT_ARCHITECTURE.md，门禁入口为 ../../../tests/Verify-Architecture.ps1。

## VPN 订阅与节点

开发发行包可通过 `tests/Publish-BackgroundService.ps1 -MihomoArchive <官方 Windows x64 归档>` 加入 Mihomo v1.19.34。打包脚本校验归档哈希，核心随 Worker 发行索引校验，其余部署流程与现有 Worker 一致。[Mihomo 官方发布](https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.34)

管理员在「系统设置 → 网络 → VPN」保存 HTTPS 订阅并刷新节点。活动订阅在 Worker 私有目录加密保存，界面只显示保存状态与节点名称。每个联网任务在「任务中心 → 任务详情」选择节点。AI 模型在「系统设置 → AI 模型」按供应商选节点，通过「测试并设为默认」保存。AI 默认直连。VPN 节点不可用时，配置了节点的请求报告失败。

VPN 使用绑定在本机回环的独立 HTTP 代理。Worker 定时与手动任务使用计划保存的节点，AI 请求使用供应商配置保存的节点，两个不同节点可并行使用。
