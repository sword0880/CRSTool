# ServiceKey 恢复第一阶段验收

基线：main `cbb3d8ba`，PR #16 已合并。2026-10-23 核对其最新提交 `aa51e280` 的
WPF UI design checks / Worker upgrade and recovery checks 均 success；该结果不是本 PR 的验证。

本阶段只加入 internal 内存封装，不开放生产恢复。设计与剩余授权/暂存/激活门槛见
[分阶段安全审查](../architecture/WORKER_SERVICE_KEY_RECOVERY.md)。

## 本机隔离验证

- `dotnet build tests/CRS.Tests.Background/CRS.Tests.Background.csproj -c Release -m:1`：
  成功，0 warnings / 0 errors；保留原有 NuGet 审计。初次受限网络的 NU1900 通过正常网络重试解决。
- `dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --service-key-recovery`：
  正常 Windows 令牌下 **345 项合成检查通过**。
- 首次沙箱运行在测试 GUID 临时目录 ACL/清理处被拒绝；正常 Windows 令牌重跑通过，未修改生产 ACL/SID 策略。
- 全解决方案 `dotnet build CRS.sln -c Release -m:1`：成功，0 warnings / 0 errors。
- 完整构建后的 `tests/Verify-Architecture.ps1`：全部通过；首次构建前执行因缺少 Domain 测试二进制而中止，未修改门禁。
- `tests/Test-WorkerMaintenance.ps1 -CheckStartupInterop`：25 项通过。
- `tests/Test-WorkerUpgradeTransactions.ps1`：145 项通过；两者均未操作真实 SCM。
- `git diff --check` 通过。最新提交的远端 Windows CI 仍须单独核对。

夹具仅使用 CSPRNG 合成 ServiceKey/恢复凭据、自建临时目录和合成数据库哨兵。不读取 ProgramData
或已安装服务，不进行生产导出/恢复、真实通知或采集。代码不打印秘密，不上传密钥/凭据 artifact。

## 自动化门禁

现有 Windows `Worker upgrade and recovery checks` 新增 `--service-key-recovery`，同时保留
维护快照互操作、升级事务故障矩阵及真实隔离 Worker 启动。常规 WPF workflow 继续完整构建和架构回归。
只有本 PR 最新 head 的 CI 结果能证明本次改动的自动化状态；排队/运行中不算通过。

## 不能据此关闭的风险

没有授权导出、完整认证加密备份、目标机 DPAPI/SID 重封装、全 SQLCipher/Schema/原件只读验证、
恢复事务显式激活、两个干净 Windows 环境的合成端到端演练、硬断电/磁盘满或生产签名验收。
ARCH-005 / P0-02 仍 Open；PR 保持 Draft，不自动合并。
