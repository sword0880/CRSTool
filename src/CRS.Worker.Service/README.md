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
├─ Diagnostics/               # 脱敏服务事件日志
└─ Deployment/                # 升级日志、归档校验与启动恢复门禁
```

数据库替换先保持Persistence接口，再增加提供方实现；Sources和Runtime不引用具体数据库类型。新增服务生命周期装配放Hosting，业务预算放Sources，不能再次堆回根目录。bin/obj由构建管理，不属于源代码目录。

架构说明见 ../../docs/architecture/CURRENT_ARCHITECTURE.md，门禁入口为 ../../tests/Verify-Architecture.ps1。
