# 当前发布验收

> **结论：No-Go / ReleaseApproved=false**  
> 快照日期：2026-11-13。文档整理不会改变发布批准状态。

## 1. 当前结论依据

1. **当前代码基线的自动化 CI 已通过。** PR #38 已合并，PR head 与 main 合并提交的 Git tree 一致。WPF UI 完整工作流 [run 43422999898](https://github.com/sword0880/CRSTool/actions/runs/43422999898) 79/79 步骤通过；Worker PluginRunner [run 43422999954](https://github.com/sword0880/CRSTool/actions/runs/43422999954) 11/11、Worker 升级恢复 [run 43423000041](https://github.com/sword0880/CRSTool/actions/runs/43423000041) 18/18、NationalTeam storage maintenance [run 43422999950](https://github.com/sword0880/CRSTool/actions/runs/43422999950) 11/11 均通过。自动化 CI 红灯已解除，但不代表正式 Worker 已部署或其他业务/安全发布门槛已关闭。
2. **正式部署证据落后于当前 main。** 最后可确认的正式 Worker 代次记录为 2026-10-31；没有本次核验后取得的新 head 正式安装回执。不得把 main 上的代码当作生产环境已运行。
3. **税务与绩效的独立业务正确性仍未签认。** 真实样本解析或内部回归通过不能替代独立金额 Golden Dataset、原币逐项对账和人工签认。
4. **NationalTeam 金融资料与模型仍有缺口。** 最近正式证据快照为 2026-10-31；ETF 历史覆盖、连续采集、报告原件、具名持仓、市场周期/广度和样本外有效性未满足完整发布条件。
5. **公开版安全/恢复门槛仍开放。** 生产签名、依赖许可与 NOTICE、隐私告知、跨机恢复、断电/磁盘满和真实 Windows 生命周期仍需验收。
6. **AI 外部接入不是已交付的生产能力。** 本机代码和专项测试已合并，但远程 ChatGPT 连接、真实附件上传/下载、用户级身份、生产 Gateway/Worker 配套部署及外发合规仍未验收。

## 2. 当前已取得的技术证据

| 范围 | 证据 | 结论 |
| --- | --- | --- |
| AI 代码合并 | PR #31 至 #35；附件功能 PR #35 已合并 | 源码已进入 main，不代表正式部署 |
| AI/附件专项 | run 43422999898 的 Release Build、工具一致性、MCP fileParams/会话生命周期和项目图/架构依赖步骤通过 | WPF 完整工作流 79/79 步骤通过 |
| Worker PluginRunner 与监督验收 | PR #38；WPF run 43422999898、Runner run 43422999954 | WPF 监督/持久命令及结果未知规则通过；Runner 项目图 11/11 通过 |
| Worker 升级恢复 | run 43423000041 | 18/18 自动化升级/恢复检查通过；不替代目标生产服务安装回执 |
| NationalTeam | 2026-10-31 正式数据与保全快照 | 覆盖、审核和模型有效性仍未完成 |

## 3. 发布前必须完成

- [x] PR #38 合并后的 main 代码树已完成 Windows Release Build 与完整 WPF 工作流（79/79）；Runner、Worker upgrade/recovery、NationalTeam maintenance 工作流全部通过。
- [x] Worker PluginRunner 启动、崩溃、取消、结果未知持久终态、普通失败重试与恢复矩阵已完成自动化回归；真实生产 SCM 安装仍由下一项单独验收。
- [ ] 在目标 Windows 环境完成正式安装/升级/卸载/重装及数据保全；版本与服务身份回执可复核。
- [ ] 完成 ServiceKey 跨机器恢复、生产签名、依赖许可证/NOTICE、隐私告知和发行包敏感信息扫描。
- [ ] 完成税务独立 Golden Dataset、真实案例金额对账和人工签认。
- [ ] 完成绩效真实期初、价格、现金流/转仓及原币金额对账。
- [ ] 重新核验 NationalTeam 官方来源、ETF 覆盖、披露原件/修订、具名持仓和模型样本外表现。
- [ ] 若将 AI MCP 纳入本次产品发布，另行完成真实 ChatGPT 账号、远程连接、附件 fileParams、撤权和生产运维验收；否则保持默认关闭/仅本机测试，不宣称远程可用。

## 4. 执行入口

在 Windows + .NET 10 环境按测试总览执行：

```powershell
dotnet restore CRS.sln --locked-mode
dotnet build CRS.sln -c Release --no-restore
dotnet run --project tests/CRS.Tests.AiQuery -c Release --no-build
pwsh -File tests/Verify-Architecture.ps1
```

完整测试、真实样本和验收脚本见 [tests/README.md](../../tests/README.md) 与 [TEST_SPEC.md](TEST_SPEC.md)。以上命令不是发布批准本身；必须使用最新提交、完整工作流和目标环境回执。

## 5. 权威关联

- 未关闭问题：[KNOWN_ISSUES.md](../KNOWN_ISSUES.md)
- 当前架构与部署状态：[ARCHITECTURE_STATUS.md](../architecture/ARCHITECTURE_STATUS.md)
- 安全与认证：[安全规范](../specifications/security/README.md)
- 税务正确性：[税务规范](../specifications/tax/README.md)
- Worker 升级恢复：[部署文档](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)
