# 绩效 P0 验收记录

> 2026-10-16 范围调整：绩效统一为联网操作，取消离线入口及离线失败回退。每次更新均获取 SSE 上证指数 / SPX 标普500 / HSI 恒生 / IXIC 纳斯达克综合及对比用汇率；默认 SSE。网络失败不发布旧行情重算结果。证券流水共用导入、完整性门槛、冻结历史及加密证据保留。下文早期离线 P0 记录为历史验收背景，当前行为以此调整及 [实现说明](../implementation/PERFORMANCE_P0.md) 为准。

2026-10-16 本次验证：WPF 与 Infrastructure Release 构建零警告零错误；WPF 交互、Domain 32 项冻结回归及组件、架构/复杂度门禁、绩效 Infrastructure 集成通过。新增合成测试覆盖上证代码/原币/跨币种汇率、每次更新强制联网、网络失败清空旧结果及锁定撤销访问。Infrastructure 集成使用仓库内隔离临时目录。未完成真实行情服务在线可用性和真实账户金额验收。



2026-10-14。实现与操作见[绩效 P0](../implementation/PERFORMANCE_P0.md)，需求见[绩效规范](../specifications/PERFORMANCE_ANALYTICS_SPEC.md)。本记录不改变全局 ReleaseApproved=false。

## 自动场景追踪

| 需求 | 验收位置 | 核验内容 |
| --- | --- | --- |
| PA-01/02/03 | Domain/PerformanceChecks | 缺 NAV/覆盖不填零；无流量 10%；入金无市场涨跌；精确流量前后 NAV |
| PA-04/06/16 | Domain/PerformanceChecks | 双账户现金消除、范围退出变外部、证券转仓区间成本连续 |
| PA-05/07/08 | Domain/PerformanceChecks | 换汇不算投入、股息/预扣税净收入、资产减负债、借款不算投入；独立桥接差异超限阻断收益率 |
| PA-09 | Infrastructure/PerformanceChecks | 同字节不重复导入，格式/负汇率/空集合项与未知账户拒绝 |
| PA-10/11/12/13 | Domain/PerformanceChecks | 缺价格、历史汇率折算、XIRR 多解/缺符号、半年度不造完整历史 |
| PA-14 | PerformanceBrokerImporter + 真实样本专项 | CashReport 只做核对，收入主表拒绝转换成重复现金事件 |
| PA-15 | Domain + Infrastructure/PerformanceChecks | 同资料确定性、冻结输入/结果、父运行重放另存、当前元数据不改原历史 |
| PA-17 | Infrastructure/PerformanceChecks | 真实 SQLCipher 备份、新目录恢复账户/组合/价格/汇率/运行与原件；原件摘要复核 |
| PA-18 | Domain/PerformanceChecks | 拆股调整数量不增加市值或区间市场成本 |
| PA-19 | Infrastructure + Wpf/PerformanceInteractionChecks | 税务仓储写入及界面年度变更不改绩效运行 |
| PA-20 | Infrastructure + Wpf/PerformanceInteractionChecks | Excel 字符串不执行公式；明文确认、TEMP 拒绝、加密底稿恢复及密码清理 |
| 其他保护 | Infrastructure + Wpf/PerformanceInteractionChecks | 预取消不保存、原件/结果篡改拒绝、锁后旧端口失效、清理敏感页面状态 |

已运行：解决方案 Debug 编译（0 警告、0 错误）、领域绩效套件、界面模型套件及绩效加密集成专项通过。发布配置编译（0 警告、0 错误）、实际窗口十页导航与最小窗口的绩效计算/导出栏可见性通过，绑定错误为 0。不能把替身界面测试或实际绘制视为金融金额验收。

在仓库根目录执行：

```powershell
dotnet build CRS.sln
dotnet run --project tests/CRS.Tests.Domain --no-build
dotnet run --project tests/CRS.Tests.Wpf --no-build
dotnet run --project tests/CRS.Tests.Infrastructure --no-build -- --performance-only
dotnet run --project tests/CRS.Tests.Infrastructure --no-build -- --performance-real-only
./tests/Verify-Architecture.ps1
```

真实专项仅显示券商、集合计数、问题代码，不输出账号、金额、私有文件名；原件只读，校验导入前后摘要一致。普通导出测试的 TEMP 特许仅用于隔离合成验收，生产界面不开放此特许。

## 真实资料与开放项

用户授权的 Sample/IBKR 三份 XML、Sample/FUTU 年度交易工作簿作为真实解析输入。IBKR 现有文件含交易、现金与期末持仓，缺完整期初净值；部分年度 2025 为年中新开户，不能扩展到 1/1。富途持仓缺证券价格/市值和可靠基础币种，部分转仓/公司行动缺公允价值。历史估值汇率、外部流量边界、独立完整损益桥接也需核对。因此“两券商真实独立金额对账”仍未通过，TWR/XIRR 必须在缺项时不可计算，不以程序输出回填证据。

跨 Windows 用户/真实换机恢复尚需实机证据；自动恢复用不同新目录和原恢复密钥模拟便携恢复，不等于另一台设备的 DPAPI 验收。真实流量时区、NAV 全资产范围、证券转仓身份桥和复杂公司行动需要业务证据。原币逐项独立对账阈值表尚未提供；目前可配置报告币绝对/相对/单位容差。绩效暂存全量程序集及历史运行时环境尚未归档，重放版本/输出不一致会拒绝。

P1 的四指数比较未实现；已确认标普500、纳斯达克综合、上证综指、中证500。价格/总回报数据类型、币种与区间仍须随离线来源确认。

[返回验证目录](README.md)

真实解析专项最终通过：3 份 IBKR XML + 5 份富途年度交易表，累计 9 个伪名账户、1,570 条规范事件、65 个净值快照；4 份富途收入汇总明确拒绝转换成重复事件。账户/金额不写验收输出。缺项代码包含 NAV_MISSING、PRICE_MISSING、FX_MISSING、CASH_FLOW_COVERAGE、TRANSFER_UNMATCHED、UNKNOWN_EVENT；数值业务验收仍未通过。

2026-10-14 最终窗口证据：[绩效工作台合成截图](../../artifacts/performance-p0/workspace-performance.png)、[最小窗口合成截图](../../artifacts/performance-p0/workspace-performance-minimum.png)。分类方向/数量校验、未确认草稿阻断、离线模板价格/汇率重建和修订/转账完整恢复均已加入集成回归。原有 Application 套件和 68 项综合回归通过。
