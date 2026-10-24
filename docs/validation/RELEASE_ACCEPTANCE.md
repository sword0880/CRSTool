# 当前发布验收

2026-10-24晚间ETF/国家队配套已正式Committed安装，160发行文件及5614快照逐项校验、密钥/计划/配置保留；5个已重新核对原件的账户均Completed/Succeeded并回读A类首次披露快照。来源补查与目录请求仍Pending/RetryDeferred，ETF250日仅11/24、连续20日0只，ReleaseApproved=false。见[正式安装及边界](../implementation/ETF_EQUITY_DEPLOYMENT_20261024.md)，下方较早“尚未安装/A类0条”保留为历史时点。

2026-10-24 ETF/国家队股票：新增比较前期绑定、旧Worker拒绝绑定审核、连续采集原件接收时间检查及反例回归。正式完整24只审计：11只250日通过、连续20日0只通过，目标范围8缺失证券日/36待审证券日；当前股票29条待审、A类快照0条，不能沿用早期5账户回读判定当前验收完成。新修复尚未安装，金融数据及发行门槛仍开放。详见[本轮反方续验](../implementation/ETF_EQUITY_FOLLOWUP_20261024.md)。

更新：2026-10-15。**ReleaseApproved=false**。自动回归和真实样本技术验证已通过；独立金额签认、实际设备恢复及发布包审查仍未完成。本文维护当前门槛与执行入口，历史测量见 [验收轮次归档](../archive/RELEASE_ACCEPTANCE_HISTORY.md)。

## 当前证据

| 范围 | 证据与结果 | 限制 |
| --- | --- | --- |
| ETF视口与监控数量分离（2026-10-15） | 加密库26只重启保留、旧触发器解除后继续启用至27只；真实WPF经Worker启用第21只并滚动到后续行，服务化27项及插件宿主/双入口只读回归通过；本机服务与调试插件已更新 | 20仅是表格约20行视口，不再是监控数量上限；原启停设置和历史事实保留，采集仍受来源限速及有界执行保护；见[配置说明](../implementation/ETF_CONFIGURATION.md) |
| ETF阶段日志（2026-10-15） | 后台29基础＋36整改＋9权限＋8阶段仓储，共82项；最终Worker包ETF服务化27项、来源策略20项、数量统计及真实WPF自动刷新通过；插件加载/只读边界、完整桌面冒烟与架构门禁通过 | 每执行保留最近512阶段，单次返回最近128并标明截断；旧记录不补造；心跳独立100条、业务500条；见[日志实现](../implementation/WORKER_EXECUTION_LOGS.md)，不代替长期来源验收 |
| ETF可编辑配置与滚动（2026-10-15） | Release全解决方案构建0警告/0错误；ETF配置/质量/覆盖率、25项服务化与真实WPF新增/修改、插件宿主及双入口只读检查通过；后台72项和架构门禁通过 | 24个候选仅原5只默认监控；20只业务限制已按用户澄清取消，20仅指视口行数；参考名称/指数待核验，采集期间保存排队；见[配置实现](../implementation/ETF_CONFIGURATION.md)，不代替长期来源与发布验收 |
| 单次执行日志（2026-10-15） | Release构建0警告/0错误、后台72项及ETF服务化19项通过；真实双击窗口验证固定执行ID、刷新、摘要、北京时间和取消，截图核对通过；本机服务已更新 | 展示持久执行摘要，不宣称未记录的逐步调试日志；见[实现与边界](../implementation/WORKER_EXECUTION_LOGS.md) |
| ETF来源可靠性（2026-10-15增量） | Debug/Release构建0警告/0错误；来源策略18项、最终包服务化19项、ETF格式/质量/覆盖率及双入口UI只读回归通过；双宿主包52文件哈希一致 | 来源策略重建验证不等于物理断电；结构变化分类、任意历史缺口、跨插件公平预算及长期官网验收仍开放；见[实施记录](../implementation/ETF_WORKER_MIGRATION.md) |
| ETF迁入Worker（2026-10-15） | Debug/Release全量构建0警告/0错误；后台65项、ETF服务化19项、既有ETF与WPF交互、双入口UI只读及通用插件回归通过；真实LocalService联网取得两市五只ETF共25条事实；52文件双宿主包逐项哈希一致，窗口冒烟通过 | 全新Schema 2测试库，无旧库迁移；完整交易日历、250日/连续20日、整机恢复、服务升级/密钥恢复与生产签名仍开放；见[当前实施](../implementation/ETF_WORKER_MIGRATION.md) |
| Worker权限巡检与分组导航（2026-10-15） | Release后台65项检查通过，真实隔离宿主发现ACL漂移后故障退出且密钥不变；当前安装目录只读检查通过；桌面构建、十页窗口冒烟、截图核对和架构门禁通过 | 安装程序未替换；F09恢复封装与F01任务所有权迁移仍未完成；见[实施记录](../implementation/WORKER_REMEDIATION.md) |
| 加密安全依赖修复（2026-10-15） | 集中固定System.Security.Cryptography.Xml及Pkcs为10.0.13，同步三个锁文件；开启NuGetAudit的还原、Release全量构建（0警告/0错误）、68项综合回归通过；包含间接依赖的全解决方案漏洞扫描未报告已知漏洞 | 修复[Xml高危公告](https://github.com/advisories/GHSA-7hqp-7c5m-4vq6)及[Pkcs公告](https://github.com/advisories/GHSA-2fmv-rv2c-59x7)；扫描结论限于本次NuGet数据，不代表发布包已完成安全审查 |
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

2026-10-23架构四项候选整改：联合导入原子提交、市场只读快照、有界证据查询、Runner进程监督和容量预警已接线。42项专项、最终候选包46项隔离服务化及相关回归通过，正式服务未替换、生产签名及原有发布门槛未关闭，详见[实施与验证](../implementation/ARCH_OPTIMIZATION_20261023.md)。ReleaseApproved保持false。

2026-10-23 架构续项：市场仓储契约/启动能力核验与北京时间年度边界已验证；恢复授权新增内部持久索引与强杀重放检查。未注册生产恢复 IPC/CLI，未完成目标机激活或双干净机器演练，P0-02 / ARCH-005 继续 Open，ReleaseApproved=false。见[续项记录](../implementation/ARCH_FOLLOWUP_20261023.md)。
