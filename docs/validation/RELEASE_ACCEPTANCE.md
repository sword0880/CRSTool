# 当前发布验收

更新：2026-10-15。**ReleaseApproved=false**。自动回归和真实样本技术验证已通过；独立金额签认、实际设备恢复及发布包审查仍未完成。本文维护当前门槛与执行入口，历史测量见 [验收轮次归档](../archive/RELEASE_ACCEPTANCE_HISTORY.md)。

## 当前证据

| 范围 | 证据与结果 | 限制 |
| --- | --- | --- |
| Worker v2首批整改（2026-10-15） | Debug、Release各56项后台检查、Release全量构建、WPF交互和架构门禁通过；LocalService复用原密钥及历史，新管道和真实窗口通过；见[整改记录](../implementation/WORKER_REMEDIATION.md) | F02—F06首批修复；ETF独占写库迁移、完整升级恢复、生产签名等仍待完成；不改变ReleaseApproved=false |
| 独立后台心跳 POC（2026-10-15） | LocalService 真实安装、SQLCipher/Quartz/IPC、只读令牌授权、SCM 强杀恢复、卸载保留数据和重装通过；实施与命令见[记录](../implementation/BACKGROUND_SERVICE.md) | ETF 未迁移；无人登录、升级恢复、官网网络、物理断电和生产签名未验收；不改变 ReleaseApproved=false |
| 最近 P0 续修 | 六组回归、68 个综合场景、架构检查、WPF 冒烟；期间清单、加密往返和 Excel 导出专项通过 | 见 [P0 记录](P0_FIX_PROGRESS.md)；该轮未重新收集覆盖率或性能 |
| 真实原件 | FUTU 五年度、IBKR 五组合只读导入、SQLCipher 保存与同构建复算通过，原件摘要不变 | 见 [真实样本](REAL_SAMPLE_PROGRESS.md)；不替代独立税额签认 |
| 完整资源保护轮次 | artifacts/resource-protection-acceptance；实际覆盖率 3839／4329（88.68%） | 这是该轮构建的数字，不是后续新增代码覆盖率；性能复用原测量 |
| 密钥与保护 | SQLCipher、明文库拒绝、手机验证、恢复、加密导出、升级回滚、分块备份与有界读取自动验证通过 | 手机实机、跨电脑、断电／磁盘满与实际硬件锁屏仍未通过 |

最近 P0 产物在 artifacts/p0-followup-acceptance；自动执行结果应结合对应构建、日志与报告读取。详细实现见 [认证](../guides/AUTH_IMPLEMENTATION.md)、[备份容量](../implementation/BACKUP_CAPACITY.md)、[锁定中断](../implementation/LOCK_INTERRUPTION.md) 和 [冻结证据](../implementation/FROZEN_EVIDENCE.md)。

## 执行入口

在仓库根目录运行：

~~~powershell
./tests/Run-ReleaseAcceptance.ps1 -Smoke -LocalRealDirectory Sample/FUTU -IbkrRealDirectory Sample/IBKR
~~~

脚本执行构建、架构检查、六组回归、合成性能测量、依赖许可证清单以及可选窗口与真实样本检查，产物默认位于被 Git 忽略的 artifacts/acceptance。失败返回非零并保留已有报告；仅清理本次隔离目录，不删除用户数据。-SkipBuild 明确标记未重新构建；-ReuseBenchmark 保留复用测量的原始时间。

若可用，使用官方 dotnet-coverage 或 Visual Studio CodeCoverage.Console 收集五个生产程序集的覆盖率；工具不可用时标记未收集，不以通过率替代。覆盖范围以 tests/coverage.settings.xml 为准。运行命令见 [tests/README](../../tests/README.md)，测试范围见 [TEST_SPEC](TEST_SPEC.md)。

## 独立金额验收

复制 [真实样本模板](../../tests/templates/real-sample.example.json) 到本地私有目录，按事实填入来源性质、授权、账户范围、期初依据及独立核对的收入、成本、收益、税额和抵免期望值，然后运行：

~~~powershell
./tests/Run-ReleaseAcceptance.ps1 -Smoke -RealManifest 'D:/本地验收/real-sample.json'
~~~

模板的零值是占位值；禁止用本程序输出倒填。相对原件路径以清单所在目录解析，金额比较报告也属于私有资料。RealSample 表示独立金额验收；RealSampleTechnical／IbkrRealSampleTechnical 仅表示技术往返。用户确认暂时没有独立计算记录，该门槛继续未通过。

## 未通过的发布门槛

- 独立收入、真实成本、税额及抵免签认，真实开户／关闭依据与全年范围确认。
- 手机离线实机、跨 Windows 用户／电脑恢复、锁屏／休眠、断电／磁盘满及特殊文件系统演练。
- 覆盖率目标、性能与峰值内存阈值，含 2 GiB 备份实际测量。
- 项目 LICENSE 决策、第三方及原生库分发许可／NOTICE 审核。
- 实际发布包签名、离线安装运行、私有样本／日志／临时文件／WAL 及异常信息隐私审核。

未完成项统一维护在 [KNOWN_ISSUES](../KNOWN_ISSUES.md)，隐私范围见 [PRIVACY](../guides/PRIVACY.md)。acceptance.json 保持 ReleaseApproved=false，技术检查不能自动批准正式发布。
