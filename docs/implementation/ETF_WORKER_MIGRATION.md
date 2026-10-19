# ETF 公开任务迁入 Worker（2026-10-15）

> 2026-10-19 P0增量：后台新增跨年日历、持久采集阶段与缺口调度、真实250日/连续20日验收查询；桌面按所选ETF读取证据，对并行只读查询的PluginBusy等待重试。新增关键逻辑与验收工具附中文注释。配套52文件插件包逐项哈希一致，Debug/Release开发插件和可信索引同步；正式服务升级、真实原件及人工样本回读状态见[整改记录](ETF_EQUITY_P0_REMEDIATION_20261019.md)与[来源和黄金样本](ETF_EQUITY_P0_SOURCE_AND_GOLDEN_20261019.md)。不将隔离服务检查或源码完成视作真实P0数据门槛通过。

本轮完成执行归属切换：ETF公开采集、市场库写入、修订审核与验证池变更由独立Worker执行；桌面插件只申请导航能力，通过本机授权管道查询结果、提交管理员命令。关闭或锁定桌面不停止公开任务。此结论不代表ETF一期数据覆盖与正式发布全部通过。

## 组件与唯一执行者

| 组件 | 当前责任 |
| --- | --- |
| CRS.Plugins.NationalTeam.Core | 纯模型、格式规则、DTO与用例契约，不带WPF或数据库实现 |
| CRS.Plugins.NationalTeam.Jobs | 无界面采集、SQLCipher市场库、公开查询与业务命令，后台入口由Worker加载 |
| CRS.Plugins.NationalTeamWatch | UI程序集保留既有名称，仅含页面、展示模型和管道客户端；不引用Jobs或SQLite |
| CRS.Worker.Service | 唯一运行中的Quartz调度器、持久计划和命令、服务主密钥、进程与权限保护 |
| CRS.PluginHost | 桌面页面会话；不再创建QuartzTaskScheduler，拒绝桌面jobs/schedules/http-public/storage-private能力 |

既有业务源码通过项目链接归属Core与Jobs，不复制份额算法。独立测试仍可验证旧调度辅助类；主程序运行会话不会实例化它。页面查询失败会提示服务不可用，不创建本地空库或替代采集器。

`crs.national-team/2.0.0` 是一个双入口完整包。`plugin.json` 同时声明UI入口及backgroundEntry，Worker使用hostContract 2.0；桌面契约仍为1.0。两个发行脚本调用同一个 `Publish-NationalTeamPackage.ps1`，各宿主的外部可信索引覆盖整个包；最终双宿主包52个文件已逐项验证哈希一致。服务只加载后台入口及其已核验依赖，不实例化包中的WPF入口。原生提供器按包中登记的win-x64路径核验哈希。

## 全新加密数据

用户明确确认现有数据均为测试数据，旧数据可删除。本轮不实现旧桌面研究库迁移。正式路径仍为 `%ProgramData%/CRS/Background`，插件私有目录为 `plugins/crs.national-team`；市场库位于其中的 `data/market.db`，使用Worker专用Schema 2，拒绝旧格式。旧桌面ETF测试目录及旧禁用标记已清理。

独立服务主密钥由机器DPAPI保护；业务上下文只提供按PluginId域分离的HKDF密钥副本，Jobs停止时清零。市场库和任务库不共享连接，也不使用个人税务密钥。数据树延续启动与每30秒权限/所有者/链接检查。原件是公开来源字节，按哈希存证并受服务目录ACL保护。

## 采集、恢复与去重

- 默认北京时间每日23:15执行，服务启动时执行一次补采。首次查询最近7天至今天，共8个自然日。
- 自动查询游标持久保存在市场库。恢复时重叠7天，每批最多30个自然日；仍有较早积压时持久等待30秒后继续下一批。来源错误阻止该批游标继续前进，已成功事实保留。游标只表示已查询日期，不证明交易日数据完整。
- 手动区间补采由管理员提交，区间最多400个自然日，受宿主15分钟执行期限约束；长区间可能需要分批。取消桌面等待不撤销已接受的命令，须在任务中心取消后台执行。
- 业务采用至少一次执行。原件哈希和份额事实保持幂等；审核及池变更把稳定命令ID与业务事务同时提交，重启重放时不再写第二次变更。
- 没有来源错误但未取得任何池内份额时返回NotPublished；部分来源错误或待继续补采时返回RetryDeferred，不把缺失填零。403/429不绕过，当前会话对相应域名停止请求6小时；该来源阻断尚未跨进程持久化。

审核和池变更属于手动操作，不出现在可编辑的定时计划列表中；服务拒绝将其启用为无参数定时任务。

## 管道与桌面入口

沿用 `CRS.Worker.Service.v2` 及独立管理员控制管道。协议增加有界Payload与QueryPlugin只读命令：业务参数最多4096字符，业务响应最多48000个UTF-8字节，最终帧仍受131072字节上限保护。

公开概览、历史份额、证据和覆盖率通过白名单查询。分页每次最多10行，使用服务端快照避免采集并发导致重漏；最多8个快照，每份最多10000行且序列化行总长不超过2 Mi字符，过期10分钟。超限明确失败，页面不展示半份读取结果。

证据导出不把桌面路径交给服务。服务验证登记与哈希，最多暂存两份8 MiB以内的字节快照，每块16 KiB、5分钟过期；桌面验证全部分块的哈希后原子写入用户选择的位置，拒绝覆盖程序及CRS数据目录。旧桌面CSV导入入口已移除，公开采集无需用户下载文件。完整官方交易日历自动接入仍待实现。

左侧“任务与服务”分类下：

- **任务中心**：唯一计划/手动执行/取消/执行历史入口，所有操作指向Worker；修改需要管理员权限。
- **后台服务状态**：宿主健康与连接说明，不重复放置任务操作。
- **导入历史**：原有个人资料导入历史，独立于公开后台任务记录。

ETF页面仍提供业务结果、区间补采、验证池与修订审核；所有业务写入通过持久RunNow请求完成。普通用户只能查询公开结果。

## 验证与执行入口

Debug、Release全解决方案构建均为0警告/0错误。既有后台65项、ETF服务化19项、ETF格式/质量/覆盖率/证据回归、通用插件宿主及UI只读双入口验证通过。服务化19项包含真实子进程、命名管道、加密文件头/无钥拒读、退出独立桌面客户端后Quartz继续执行、强杀重启及业务去重；重建业务提交后调度结果未提交的故障窗口用于去重验证，未宣称物理断电验收。

真实LocalService已独立安装并自动访问官网；一次8自然日查询取得两市五只ETF共25条份额事实，日期为2026-10-08、09、12、13、14，来源错误0。该证据不把10月15日尚未发布的数据填零，不证明交易所完整历史、无人登录或连续20日运行已验收。

```powershell
./tests/Publish-BackgroundService.ps1 -OutputDirectory artifacts/worker-new -Configuration Release
dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- artifacts/worker-new
./tests/Publish-Plugins.ps1 -OutputDirectory artifacts/desktop-new -IncludeDemo -Configuration Release
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- artifacts/desktop-new/Plugins
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- --official-live
```

宿主可靠性65项必须使用显式 `-HeartbeatOnly` 发布的独立包，避免把真实官网采集混入隔离故障套件。正常安装默认包含ETF及诊断心跳。测试夹具HTTP仅位于tests，不进入正常发行包。

最终窗口截图为 `artifacts/worker-etf-ui/window-scheduled-tasks.png`，已人工核对分组、图标和两个服务计划；窗口导航、最小尺寸及绑定检查通过。最终Worker包为 `artifacts/worker-etf-reviewed`，桌面包为 `artifacts/worker-etf-desktop-reviewed`。旧桌面库升级测试已替换为UI无写库/双入口校验，Worker恢复由新服务化套件覆盖，不把旧七中断点结果继承为Worker升级证明。

开发调试：已将对应配置插件包同步到主程序Debug和Release输出目录，F5可加载新页面。后续修改业务插件后，先重新发布到全新目录，再运行 `tests/Sync-DevelopmentPlugins.ps1 -PackageDirectory <该桌面发行目录> -Configuration Debug`；宿主不通过静态业务项目引用偷偷加载未核验程序集。

## 保留的门槛

完整官方交易日历、任意历史缺口、跨插件公平限流、结构变化分类、250个有效交易日/连续20日、来源许可、无人登录/整机重启与睡眠、断电/磁盘满、完整服务升级事务及密钥恢复、生产签名仍需验收。NAV金额与二期股票持仓不在本轮交付范围。DataPilot不等于已确认机构持有，份额变化不等于中央汇金交易，ReleaseApproved仍为false。

## 2026-10-15 来源可靠性增量

新增 `data/source-policy.db`，沿用插件独立密钥进行SQLCipher加密，格式1；与市场库分开连接，避免采集策略与只读查询争用同一个连接。首次创建不清理既有市场事实。库损坏、密钥错误或不支持的版本拒绝初始化，不静默清空冷却状态。

每次请求先持久登记至少3秒间隔，再访问官网；上交所三个允许域名共享一个来源额度，深交所独立。403/429至少冷却6小时，服务器Retry-After可以延长等待；连接异常、超时和5xx以5分钟起步指数退避，基础等待上限6小时，并增加0至20%正向抖动。策略在服务重建后继续有效，成功响应清除连续失败计数。策略库由当前插件会话串行使用；尚不宣称跨插件共享预算或多进程竞争配额。

HTTP头部与正文共用20秒期限，响应仍限8MiB；两个传输明确禁用Cookie及自动重定向，保持TLS验证与既有限域。调用方取消直接传播且不增加失败计数；单一来源超时作为来源问题保留，继续处理另一交易所。用户仍通过Worker计划触发任务，来源冷却未到期时不发请求。

新增 `CRS.Tests.WorkerEtf --source-policy` 共18项，使用固定HTTP响应验证加密持久状态、同来源跨域阻断、两市隔离、指数退避、Retry-After、正文停滞、取消与损坏拒绝；不访问真实官网。既有19项服务化回归另行执行，不能将18项当作连续20日、睡眠或物理断电验收。

本增量Debug/Release全解决方案构建均0警告/0错误，最终包的18项来源策略、19项服务化、既有ETF格式/质量/覆盖率和双入口UI只读检查通过，架构门禁通过。最新服务包为 `artifacts/worker-source-policy-reviewed`，桌面包为 `artifacts/desktop-source-policy-reviewed`，52个插件文件逐项哈希一致；Debug/Release的F5插件包已同步。

本机LocalService已换用最新开发包（2026-10-15 14:43启动），服务及两个插件健康，来源策略库实际创建，既有服务密钥哈希核对保持一致；市场库、原件及历史保留。安装时首次启动探测超时后自动重试成功，不将该瞬时结果算作持久故障。此操作沿用现有停止/卸载程序/重装维护流程，不把它称为完整升级Journal或物理断电回滚验收。

新包启动后的真实官网任务于14:43:59成功结束，执行检查点为 `days=8;sourceIssues=0;rows=25`；既有25条事实保持幂等，当前服务仍为LocalService运行，新策略库所在数据树ACL检查通过。
