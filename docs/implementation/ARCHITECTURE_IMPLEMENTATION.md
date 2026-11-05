# 架构机制与整改边界

<a id="arch-followup"></a>
<a id="arch-optimization"></a>
<a id="arch-remediation"></a>
<a id="architecture-remediation-continued"></a>
<a id="nationalteam-boundary-remediation"></a>

本文归纳已经进入实现的机制；项目清单和依赖以[当前架构](../architecture/CURRENT_ARCHITECTURE.md)为准，未关闭风险以[架构状态](../architecture/ARCHITECTURE_STATUS.md)及[发布验收](../validation/RELEASE_ACCEPTANCE.md)为准，不另维护一份完成状态台账。

## 联合导入与业务原子性

`DesktopWorkflow`显式建立`IDesktopCalculationSession`。解析同源回调只准备绩效投影，计算成功后适配器在同一连接和事务内保存原件、版本、税务结果及归档；提交前检查取消和原保险库会话。任一必需投影失败则整次联合导入失败，不以警告掩盖半份提交。

原件及编码投影有容量预算，按摘要去重并在结束清理副本。初始化与业务提交分离；重复计算可追加税务历史，绩效原件仍幂等。手工表单只映射输入，账户、事件、净值和覆盖校验属于应用用例；历史重放保留冻结版本、币种与Provisional，不用当前日期重新推断历史状态。

券商共享原件、独立税务/绩效投影及版本审计见[券商来源事实](../architecture/BROKER_SOURCE_FACTS.md)；北京时间实现见[时间规范](BEIJING_TIME.md)。

## 显式端口与只读隔离

必需仓储能力必须显式实现，不能通过空集合、默认null或延迟抛错伪装为可用。初始化验证必需能力并实际打开独立只读事务；失败释放连接，不回退临时库、内存库或写会话。

每次查询建立独立ReadOnly连接和延迟事务，读取不初始化或迁移数据库。证券、日期、LIMIT及行/字节预算在适当层执行，取消只中断本次读取；关闭先排空在途读再释放主连接。查询不登记或“修复”金融事实，Q15损坏/恢复由明确后台巡检事务提交。

页面依赖功能端口，完整仓储族只留在装配和执行会话。公共文件、时间、指纹和并发能力进入Common；金融模型、来源许可和私有存储继续归业务模块。删除无调用旧页面与适配壳，避免同时维护两套执行路径。

## 插件监督与来源预算

每插件独立Runner，父进程先设置Job Object，再经私有继承句柄交付绑定包摘要、随机挑战及实际PID的启动材料。Runner重新验证受信包并持目录独占租约；旧进程确认退出前不能释放写入所有权。缺失运行闭包时明确失败，不回退进程内执行。

监督协议、桌面IPC、加载契约和业务版本分别管理。结果未知保留真实失败及原命令身份，重启预算只限制故障插件，不因普通业务拒绝或证据损坏重启。固定诊断与业务回执分开，见[查询及IPC](QUERY_PERFORMANCE.md)。

宿主共享来源额度、插件来源策略、冷却及恢复边界见[共享来源预算](../architecture/SHARED_SOURCE_BUDGETS.md)。更换域名、请求ID或插件实例不能绕过同源冷却。

## 维护、容量与灾备

合法资料增长与安全故障分开：容量达到预警阈值不应直接当ACL攻击；权限、所有者、链接或路径异常仍失败关闭。运行中完整巡检可取消且不叠加，不占据调度协调循环。清理策略独立见[运行资料清理](RUNTIME_DATA_CLEANUP.md)。

升级事务先持久记录维护状态、停止并快照，再切换已核验程序；候选打开数据前记录可能产生新事实，应用健康不自动等于Commit。存在新事实风险时不能把旧数据库覆盖回来，失败候选和原程序保留。具体操作见[维护与恢复](../deployment/WORKER_UPGRADE_AND_RECOVERY.md)。

恢复导出的内部一次性授权、持久防重放和加密审计，不代表生产导出入口及跨机恢复已完成。Prepared/Consuming/Authorized重启作废，Sealed仅表示封装完成；审计失败不能交付密文，丢失索引不能初始化空索引替代。独立凭据交付、目标服务身份重封装、全部库只读验证、显式激活和双Windows演练仍需专项验收，见[服务密钥恢复](../architecture/WORKER_SERVICE_KEY_RECOVERY.md)。

## 验证入口

- 基本门禁：`tests/Verify-Architecture.ps1`、`tests/Verify-ProjectGraph.ps1`。
- 原子提交：`CRS.Tests.Infrastructure --calculation-commit`。
- 国家队端口/读取：`--repository-contracts`、`--application-boundaries`、`--read-isolation`。
- Worker隔离/容量：`--supervised-plugins`、`--storage-growth`。
- 灾备内部状态：`--service-key-recovery`、`--recovery-export-authorization`、`--recovery-export-persistence`。

上述入口的完整环境和参数见[测试说明](../../tests/README.md)。隔离成功不能代替签名、SCM维护、实际源站、跨机恢复或金融结果独立签认。
