# 南向累计查询UnknownQuery修复（2026-11-03）

用户打开南向页面时，Loaded异步事件向外抛出InvalidDataException。升级前正式服务为10月31日代次c84d1bf74522495fa91a4cf4ce3a272b，PID42984，Running/Auto/LocalService；实际受限IPC返回UnknownQuery，已安装Jobs文件摘要与当前构建不同。不是服务未安装，也不是空历史。

## 源码修复

- 客户端仅对southbound-net-annual的UnknownQuery返回明确的“不支持当前指标、需要更新后台版本”提示，不回退到旧买入金额或生成空曲线。
- SouthboundViewModel查询与管理两条异常边界显式接住InvalidDataException。该异常不能依靠IOException分支捕获；原过滤器使真实Loaded首次查询逃出页面。
- 更新失败仍清理Busy和旧报告；更新回执未知仍保留原命令身份，不误报受理或成功。

界面26项检查通过，新增真实Loaded首次查询、无效合同、旧后台及回执异常反例。正式服务回读后再次将该报告作为展示输入，26项仍通过；这是界面检查，实时通信另有独立IPC验收。真实隔离管道模拟旧后台验证UnknownQuery映射为版本不支持，全程仅有query-capabilities和southbound-net-annual两次读取。见[最终界面记录](../../artifacts/southbound-installed-20261103/ui-formal-report-checks.log)、[正式报告展示](../../artifacts/southbound-installed-20261103/ui-formal-report.png)及[旧后台合同](../../artifacts/southbound-installed-20261103/contract/legacy-contract.json)。

## 候选及保全

原密钥文件仅记录SHA256，不导出明文。升级前保存7项计划、24只ETF配置、29条股票投影、10条近期执行及健康。当前源码生成完整候选，182份发行文件逐项校验；与旧Worker的60个公开通信类型兼容。兼容检查脚本补充在各自隔离上下文加载同代Common依赖，避免缺依赖阻断反射检查，没有放宽成员兼容规则。

候选发行摘要3B3F7D3C19690BAB720CE40C36C678F3BF315BA63EA7C92C6C6A890D98E121E3。使用既有Upgrade-Service保全事务，维护编号7185e9d20b084e01a335248560bafc3c；完整停机快照及旧程序保留。首轮在完整权限/快照核验期间超过默认300秒启动等待，转入NeedsManualRepair，NewFactsPossible=true，未回退程序或覆盖旧库。

随后经Recover-Service的RetryCandidate沿同一维护编号继续同一候选，使用该入口已有的900秒启动等待；不修改查询、采集、取消期限或跳过权限/备份核验。该事务最终达到Committed，应用健康和IPC另行验收如下。

Debug与Release前台开发包已同步修复版；先核对Debug输出无占用后再同步，没有停止用户前台进程。已运行的调试会话需要重新启动才能加载新程序集。

## 最终验收

维护7185e9d20b084e01a335248560bafc3c已Committed，正式服务PID32432在实际查询前后均Running。182份当前发行文件、182份保留旧程序文件及6095份快照文件逐项验真通过，密钥摘要一致。[保全记录](../../artifacts/southbound-installed-20261103/preservation-acceptance.json)、[维护日志](../../artifacts/southbound-installed-20261103/upgrade.log)及[同代恢复日志](../../artifacts/southbound-installed-20261103/recover.log)同时保留首轮超时和恢复结果。

真实IPC回读确认原7项计划、24只ETF配置及29条股票投影保留，南向计划新增为第8项，默认总并发仍为8；见[原数据回读](../../artifacts/southbound-installed-20261103/readback-preservation.json)。快照保留了原调度数据，本轮没有把“读取10条近期历史”表述为逐条旧历史IPC验收。

南向采集只提交一次，原请求d66c0944-de05-40c0-a182-ab1359d37245终态Completed/Succeeded，执行7dc44a3898e14fb4a0cb3084f9ed3d78，Attempt=1；[原回执](../../artifacts/southbound-installed-20261103/collection/GetCommand.json)和[执行检查点](../../artifacts/southbound-installed-20261103/collection/GetExecution.json)均指向快照13CCC88B972286E1531F5AF0C574BA6AC6DB404F7174A93E8A48572AB74DAD35。

当前客户端经正式服务成功读取FormatVersion=2、YearToDateNetBuyTurnover报告，2022—2026年可计算日期数为228、231、233、238、194，最新实际日期2026-11-02。末值分别为3862.809、3188.4151、8078.6934、14048.4363、4026.467亿港元；见[正式曲线报告](../../artifacts/southbound-installed-20261103/live/annual-report.json)和[查询后健康](../../artifacts/southbound-installed-20261103/live/health-after.json)。这是该第三方来源已取得记录的连续性核对，不是独立交易日历覆盖率或交易所全历史核验。

汇总见[验收清单](../../artifacts/southbound-installed-20261103/acceptance.json)。前台Debug/Release各88份开发包文件与修复版构建一致，重启现有调试会话后生效。

本机保全维护不代表正式生产签署、整个国家队金融资料覆盖或既有性能门槛全部通过。原F4导航检查与本次问题分别记录。
