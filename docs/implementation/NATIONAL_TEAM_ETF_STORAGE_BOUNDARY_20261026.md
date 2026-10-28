# ETF核心、连接和初始化所有权拆分

日期：2026-10-26。范围由用户选中的重构续项确定：先分离ETF核心，再移出连接和初始化所有权。用户随后明确：国家队公开研究数据不加密，收入和报税核心继续加密。

2026-10-28续项：双库保全迁移已接独立维护Runner、LocalService派生密钥交付及认证回执，通用路径/认证文件机制外移Common。生产入口与真实SCM合成验证通过，正式保全维护已启动；本文早期“尚未接入”的结论仅对应第一阶段，当前现场状态和剩余边界以[迁移续记](NATIONAL_TEAM_STORAGE_MIGRATION_20261028.md)为准。

## 当前实现

| 组件 | 所有权和职责 |
| --- | --- |
| `EtfStore` | ETF目录、份额导入/修订、覆盖、采集记录、缺口、历史、仪表盘和验收；按功能文件拆分，不再作为总仓储partial |
| `MarketDatabaseSession` | 普通SQLite连接、命令执行、取消作用域、独立只读快照、备份和释放；不反向持有MarketRepository |
| `NationalTeamStores` | 装配各功能Store及窄事实端口，不打开连接或执行业务SQL |
| `NationalTeamSchemaInitializer` | 根版本检查与各模块初始化顺序；只读会话不调用它，所有模块成功后才记录根版本 |
| `SourceFetchStore` / `EvidenceSchema` | 来源登记、来源/质量/完整性表；来源白名单、原件校验与状态保持原口径 |
| `AlertPendingStore` | 共享的预警重新评价标记；ETF、完整性和预警通过明确组件协作 |
| `MarketRepository` | 对外组合和转发；不再打开/关闭连接，不再包含ETF核心SQL或ETF初始化 |

预警直接消费`EtfStore`提供的`IAlertMarketFacts`。原反向指向总仓储的`AlertMarketFacts`适配器删除。旧ETF partial文件改为`EtfStore.*`，不保留重复实现或旧接口兼容壳。

同时移除总仓储中10个已经没有调用方的私有初始化/读取转发方法，各Store的有效实现和公共入口保留；现有测试项目继续承担回归，没有为本次拆分新增工程。

## 加密边界

- `market.db`、`source-policy.db`以及国家队市场快照均为普通SQLite文件。默认国家队提供方ID改为`sqlite`，不注册`sqlcipher`兼容别名。
- 收入/报税保险库默认仍为`sqlcipher`。配置样例仅更改`NationalTeam`字段，`Vault`不变；本轮未修改核心加密实现。
- 国家队审批审计仍使用独立认证密钥保护HMAC和防篡改链；它不进入国家队正常数据库连接的Password。不能把保留审计认证误写成研究数据库仍加密。
- 底层SQLite运行组件沿用解决方案锁定版本；是否加密由连接是否设置密钥及实际文件格式证明，不能凭原生库名字判断。

新配置示例：

```json
{"Version":1,"Vault":"sqlcipher","NationalTeam":"sqlite"}
```

## 既有加密资料

正常启动识别旧加密文件后明确拒绝，不将其重建为空库。`LegacyNationalTeamExport.Export`是显式离线导出入口，接受国家队两个固定库名，不替换原文件，不由启动自动调用：

1. 独占读取原库及可能存在的WAL；文件仍被使用时拒绝迁移。
2. 复制到本次临时目录，只在副本上进行WAL恢复及旧密钥解密。
3. 导出到全新普通SQLite，核对Schema版本、完整性、外键、结构及逐行逻辑指纹。
4. 校验成功后发布新目标；已有目标拒绝覆盖，失败清理本次临时文件。原库及原WAL保留。

它是已实现的离线存储能力，尚未接入正式安装维护流程；本轮没有读取正式密钥、迁移已安装Worker数据或替换服务。正式更新前仍须完成保留式迁移、配置切换和独立回读，不将源码或合成测试称为现场上线。

## 必须保留的行为

所有功能仍共用一个写会话及原事务提交点。事件、冻结输入和Outbox一起提交；OutcomeUnknown不重发。查询独立只读连接，建立数据库快照后才校验原件；取消中断本次SQLite/原件读取，不跨连接停止采集。无资料、待审、Q15损坏、知识截止及逐账户身份不因存储格式改变而被补零或升级为确认。

验证入口：`CRS.Tests.NationalTeamWatch --public-storage`，默认回归也包含该专项；另运行`--read-isolation`、`--exit-alerts`、`--acceptance-batch-checks`、`CRS.Tests.WorkerEtf --storage-providers`及来源策略/预算专项。功能布局门禁禁止ETF SQL回流、连接重新进入总仓储或公开研究连接重新设置Password。

## 源码与隔离验证结果

2026-10-26在工作区独立目录验证，不连接已安装服务：

- 37项目Release构建零警告、零错误；架构及功能目录门禁通过。最终构建日志为`artifacts/national-team-etf-storage-final-build-20261026.log`。
- NationalTeam完整默认回归通过，包含公开存储33项，以及读取隔离、NAV、直接持股、原件、预警事务回滚/重启、历史修订、覆盖和批量验收。公开存储专项覆盖普通文件头及无密码读取、只读拒绝写入、连接释放、未知版本/截断文件拒绝重建、旧库错误密钥/占用/目标冲突、源库与未检查点WAL保全及导出回读。日志为`artifacts/national-team-etf-storage-full-regression-20261026.log`。
- 来源策略22项及持久故障恢复专项、共享来源预算78项、插件存储提供方11项、核心保险库存储提供方10项、并发读取/取消5项、行业PE64项通过；实际桌面开发包的四个内部页面创建通过。分别记录于`artifacts/national-team-etf-storage-`前缀下的专项日志。
- 测试装配中残留的国家队密码连接和“无密钥不可读”旧断言已按新边界修正；核心调度/预算/保险库的既有加密断言保留。初次失败日志单独保全，不能归入通过证据。
- 最终独立后台候选包为`artifacts/national-team-etf-storage-worker-candidate-v2-20261026`，完整服务、Runner和两个插件均由当前源码打包。它是开发候选包，未签署生产发行签名，未安装。实际启动该候选包副本的47项跨进程集成检查通过，覆盖普通库读取、分页/原件导出、强杀重启、计划恢复、事务去重、采集中配置修改及空监控池；日志为`artifacts/national-team-etf-storage-worker-integration-v2-20261026.log`。需要正常Windows命名管道权限，使用工作区隔离目录，不更改正式ACL。
- 候选包160个索引文件SHA256一致，未携带测试夹具；文档本地链接658个，失效0个；`git diff --check`通过。分别保存`artifacts/national-team-etf-storage-package-hashes-20261026.json`和`artifacts/national-team-etf-storage-doc-links-20261026.json`。

以上测试使用合成数据库或固定资料，不证明官网长期采集、模型有效性或正式资料已迁移。指数原始资料专项因本地原始目录缺失，本轮没有重跑，未计入通过项。

## 尚未关闭的边界

整个插件目标继续进行。来源审批注册表仍有聚合partial实现，预警用例/存储协调和全池完整性依赖范围仍需继续收拢；市场周期接线、主要持仓方向和联合退出模型并不因本次存储拆分自动完成。业务与现场证据见[反方复核](../validation/NATIONAL_TEAM_POST_REFACTOR_REVIEW_20261026.md)。
