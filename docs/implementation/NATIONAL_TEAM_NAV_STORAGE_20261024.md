# R05A：独立NAV事实、修订与知识时间

## 修正的反方问题

[开发清单](NATIONAL_TEAM_RESEARCH_DEVELOPMENT_20261024.md)的R03/R04只能核验离线原件及计算，不能作为正式后台的持久净值来源。把一个NAV数直接塞进份额点，会丢失来源、修订、审核和系统实际获知时间。本阶段在同一个公开市场SQLCipher库内新增独立NAV能力，不取私人税务或绩效库数据。

候选代码已具备存储实现；当前验证使用合成资料及真实公开原件的隔离库，**尚未安装到正式Worker，也未开放网络采集任务或改变正式金额结果**。R05整体仍开放。

## 数据模型与接口

新增必需端口`INavReadRepository`/`INavWriteRepository`并入`IMarketRepository`，均无默认空实现。SQLCipher提供方显式实现，独立只读会话同样支持查询并拒绝写入。

| 表 | 责任 |
| --- | --- |
| `nt_nav_capture` | 固定证券、解析版本、验证/登记时间、原件组指纹和条数 |
| `nt_nav_capture_document` | 把四份原件绑定到既有`source_fetch`，参与原件库与Q15完整性巡检 |
| `nt_nav_revision` | 单位/累计净值分别保存，按证券与净值日期追加版本，保留首见与来源组 |
| `nt_nav_revision_event` | 追加Accepted/NeedsReview/Rejected/Superseded事件、审核人和实际记录时间 |
| `nt_nav_command` | 绑定稳定命令、操作、负载指纹和结果，与事实及既有业务命令同事务提交 |

特性模式`etf-nav=1`保持原有市场库Schema 2，不覆盖既有份额/股票事实。未来不支持的特性版本拒绝打开，初始化失败关闭连接。首次建表为空，不虚构净值。

## 导入、冲突与时间规则

1. 复制输入字节，调用固定四原件的`EfundsNavResearch.Build`；字段/代码/单位/历史衔接/页面核对失败时，不进入写事务。
2. 原件组身份由解析版本、固定URL、哈希和实际取得时间组成。验证时间与组身份分别保存，同一原件重新登记不会移动首见或审核时点。
3. 正式`FirstSeenAt`、初次`AcceptedAt`取实际登记时刻；原件`RetrievedAt`保留真实下载回执时间，逐日官方发布时间仍未知。多年回填不能显示为系统当年已知。
4. 首次原件核验通过的NAV作为独立净值事实接受，这不确认份额可比性或机构持仓。后来不同单位/累计净值追加NeedsReview，金融输入查询暂停该日，不能回退旧值绕过冲突。
5. 明确接受新版本时，旧接受版本追加Superseded，其他待审版本追加Rejected；拒绝候选则保留原接受值。拒绝已接受版本不会自动复活已取代的旧版。重复来源也不复活被拒版本。
6. 知识截止读取以追加事件重建该时点状态，后来的审核不会改写过去。UTC时间使用固定七位精度格式作边界比较，检查早一个tick不可见；时间水位包括事实、审核、命令和模式登记，只有重复命令也不能绕过倒退检查。
7. 相同稳定命令重放返回原结果，改操作/负载拒绝；NAV回执和既有业务命令记录缺少任一方都拒绝重放。取消中途事务回滚，不留下部分金融事实或成功回执；已经落盘的无引用原件不代表已提交事实。

## 完整性和范围

NAV金融输入依赖整组四份原件，包括字段显示脚本和产品页。每次读取重新校验原件/原始哈希、来源身份及Q15状态；损坏时抛出不可用，不能用空结果或旧值冒充完成。文件恢复后仍须巡检解除原完整性异常。审计读取保留版本和健康标记。

NAV原件通过专用内部路径登记`source_fetch`，通用`RecordFetch`与HTTP白名单没有扩大到基金网站。全量巡检覆盖NAV，原有份额专属查询范围不会因无关NAV损坏而失败。

## 验证入口

```powershell
dotnet build tests/CRS.Tests.NationalTeamWatch -c Release -m:1 -nr:false
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-storage-checks
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build -- --nav-storage-sample artifacts/national-team-marketdata-20261024/research-snapshot artifacts/national-team-nav-storage-20261024/official-sample
```

专项涵盖知识截止、幂等和负载拒绝、冲突/拒绝/取代、历史重建、只读会话、时间倒退、四原件健康、巡检恢复、无关份额查询、重启、取消原子性、未来模式拒绝及失败句柄释放。也纳入完整国家队回归。合成测试不证明源站可持续访问或模型有效性。

真实原件隔离验收在北京时间2026-10-24 21:29完成：新增3635条，重启后回读3635条；查询登记之前时点为0条；原件巡检契约含4份文件，同一命令重放结果不变。`Installed=false`、`TestKeyOnly=true`，明确使用隔离测试密钥，不能把该库当作正式数据迁移或部署证据。文件位于`artifacts/national-team-nav-storage-20261024/official-sample/storage-acceptance.json`。

最终修订后35项存储专项和443项完整回归通过，构建0警告/0错误；最新真实原件隔离重跑同样为3635条/重启3635条/登记前0条，保存于`official-sample-verified/storage-acceptance.json`。完整性/架构门禁及41个相对链接检查通过，日志和清单位于同一证据根。上述计数不证明正式源站采集、安装或市场研究效果。

全解决方案Release构建于21:49通过，0警告/0错误，日志`solution-release.log`。构建同步的桌面开发包与正式服务安装分别验收；本轮未替换运行中的Worker。

## 下一步

R05B仍需固定URL网络采集入口、共享来源预算及冷却、显式查询/审核业务操作、完整候选包/真实管道验收及保留数据的正式安装回读。自动采集许可和运行稳定性按来源要求另验，不以一次公开静态文件下载代替。

金额仍须独立的份额可比性和完整相邻交易日；区域判断仍需量价、广度、估值与样本外验证；持仓方向仍需扩大真实披露及日期匹配市值/分母。R05A不关闭这些目标。
