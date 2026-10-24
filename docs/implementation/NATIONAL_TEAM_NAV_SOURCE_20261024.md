# R05B：固定NAV来源、共享预算和后台入口

## 实现与反方修正

在[R05A存储](NATIONAL_TEAM_NAV_STORAGE_20261024.md)基础上，新增固定四URL采集器`OfficialNavSource`、后台手动作业`nav-sync`/`nav-review`和独立查询`nav-points`/`nav-revisions`。只支持SZSE/159915，不接受任意URL、桌面文件或回填取得时间；新增代码以中文注释解释边界。任务默认停用、仅手动，不在服务启动时联网。

基金官网与CDN在插件持久策略和宿主共享预算中同属EFUNDS，与SSE/SZSE/发行人独立；禁止403/429后切换基金域名绕过冷却。网关只为核对过的四个完整地址开放，不为整个基金域名开放任意路径。

四原件全部取得并验证后才进入仓储事务。第二文件失败或字段映射变更时保留RetryDeferred，不用旧缓存冒充本次采集成功。字段契约变化通知来源冷却；调用方取消传递，不继续发新请求。

反方复核修正两项重试问题：

- 数据已提交但执行回执未返回时，先读取双重持久回执，避免重取文件导致取得时间变化、同命令负载不符。
- 重复采集待审值时，新增待审数量可能为0；必须重读全部当前冲突，继续返回NeedsReview，不能报告全成功。当前原件异常也明确拒绝。

查询按显式日期范围读取，单次不超过400个自然日，沿用冻结分页；失败不返回半份结果。新操作名使旧服务明确拒绝；正式金额计算仍不自动填入SharePoint.Nav。

## 当前验收状态

国家队工具与WorkerEtf工具均构建成功，0警告/0错误。23项固定来源/插件入口反例通过，全部HTTP为替身；它们不证明正式源站请求、真实管道或安装完成。

证据根：`artifacts/national-team-nav-source-20261024`。完整回归、宿主来源预算、架构检查的结果及退出码分别写入`full-regression.log`/`full-exit.txt`、`source-budget.log`/`budget-exit.txt`、`architecture.log`/`architecture-exit.txt`。10月25日恢复后核对三项退出码均为0，完整回归466项、共享预算66项通过；包含专项检查，不能重复加总。

本轮尚未打完整候选包、执行真实管道验收或替换正式服务。后续必须继续这三项，再进行保留数据的维护安装与正式回读；整体研究目标仍开放。

2026-10-25续记：以上为来源阶段历史状态。随后完整包的7项真实管道检查通过，正式维护已Committed，四个固定源站实际HTTP200并登记3635条、近年IPC回读241条。最新安装及保留门槛以[实机记录](NATIONAL_TEAM_NAV_DEPLOYMENT_20261025.md)为准；不把手动一次成功写成持续自动采集或研究目标完成。

## 复核入口

```powershell
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-source-checks
dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- --source-budget
```

调用前先完成相应Release构建。以上使用隔离数据与模拟HTTP；真实来源与部署分别留证。
