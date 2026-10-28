# 广度档案登记与修订核验（2026-10-28）

状态：源码及隔离机制验证通过，后端已随[双库保全维护](NATIONAL_TEAM_STORAGE_MIGRATION_20261028.md)安装并Committed，真实广度和周期只读查询可用。延续[周期查询接入](NATIONAL_TEAM_MARKET_CYCLE_STUDY_20261027.md)，实现可操作的文件登记与核验；正式全市场广度、来源许可、模型有效性及桌面完整工作流验收仍未完成。

## 使用流程

在顶部“周期与风险 → 市场周期观察 → 广度档案”中选择统计日并读取当前档案。默认收起“登记与核验”编辑区，保留表格空间。管理员可选择原始资料与标准化JSON，填写来源网址、取得时间（北京时间）、登记人及说明，提交后由后台解析并保存为**待核验**；上传完成、后台命令受理和业务回执确认分别表示不同阶段。

普通读取者可查看或导出公开档案。管理员核对原件、当日完整证券名单与复权价格口径后，填写核验人与说明，明确确认原件，再选定版本。后台同时替代原已核版、否决其他待核版，追加相同请求的审核事件；旧记录不删除。否决当前版不会自动复活已替代旧版。新资料或核验动作会清除页面上旧周期结果，需主动重新查询。

登记与核验说明、选择发生变化会清除原件确认。提交结果不明时使用“回读登记结果”，复用原已封存文件与请求；取消等待不会撤销已经受理的后台命令。上传阶段中断，可重新选择相同文件，按完整摘要和用途发现原进度并续传。

取得时刻由登记人根据取得记录申报，不能证明官方发布时间；真正的系统登记、核验时刻由后台记录。标准化格式沿用 `reviewed-breadth-v1`，后台比较逐证券收盘、MA20、MA60与前250日最低收盘计数；不接受前端计算的比例，也不推断历史完整证券宇宙。人工确认仍不能替代全市场覆盖与原始价格历史的正式验收。

## 外层共用机制

| 层次 | 实现及边界 |
| --- | --- |
| `CRS.Common/IO/StagedDocumentStore` | BCL文件暂存，16KiB固定块、每份8MiB、最多16份活动文件及128MiB预留；摘要/长度/用途/所有者绑定，持久进度、跨进程文件锁、原子块写入与登记替换、封存后不可修改；不包含国家队规则 |
| `CRS.Common/Serialization/StrictJson` | 有界JSON，拒绝重复、未知及缺失必填字段；不决定金融事实是否可信 |
| `CRS.Worker.Ipc/PluginDocumentClient` | 本地有界文件读取、摘要计算、续传和封存回执检查；路径留在客户端，不发送给服务 |
| `CRS.Worker.Service/Documents` | 在已加载插件的私有 `document-inbox` 装配存储，实际Windows管理员权限与SID绑定；业务数据判断交给插件 |

`PutPluginDocument` 仅管理端点允许，正文上限32KiB；普通业务参数保留4096字节，主帧保留128KiB。上传与其他管理请求分别计入64次/10秒配额，避免大文件占用其他管理操作额度。`GetPluginDocument`/`ListPluginDocuments` 仍在服务端核验管理员和所有者，仅列出本人的活动文件；不暴露路径或其他人的文件。维护会取消并排空暂存请求，目录异常不冒充金融任务成功。

文件封存只证明字节齐全，不能批准广度。后台读取封存文件后再次校验摘要与用途，把普通SQLite事实、初始待核验事件及业务回执同事务提交；之后才标记暂存消费并清理字节。清理失败保留副本，不推翻已提交事实。消费标记和资料提交属于两个存储，崩溃恢复依据SQL业务回执重放清理，不宣称跨文件/SQL分布式事务。

暂存元数据保留，读取上限10000份。现阶段未接入设置中的暂存清理预览；失败的上传可通过公共暂存接口明确丢弃，尚无面向用户的批量暂存管理页面。该组件当前用于国家队公开研究资料，没有接入收入/报税资料流。

## 插件分层与状态历史

`Core/Domain/MarketCycle` 拥有提交、核验、回执和历史合同；`Jobs/Application/MarketCycle` 经窄端口读写；`Infrastructure/MarketCycle` 独立保存广度档案和事件；`Background/MarketCycle` 只协调封存引用和持久命令；`Watch` 通过第十二份功能客户端和独立ViewModel展示。

保留 `market-breadth/1` 原档案，新增 `market-breadth-review/1` 三表：`nt_breadth_event`、`nt_breadth_head`、`nt_breadth_action`。SHA-256链与独立链头用于发现意外改写、删尾及不完整状态，不是抵御本机管理员的签名机制。V1中原本已明确人工核验的档案按其原登记时刻迁移初始状态，不编造未知的更早审核历史；后台受信适配器的 `ImportReviewedMarketBreadth` 不通过UI/RPC开放。

`market-breadth-submit`、`market-breadth-review` 都是默认关闭、管理员持久手动任务。只读 `market-breadth-history`、`market-breadth-receipt`、`export-breadth` 不联网。审核携带展示时的完整当天版本摘要，其他窗口有新资料、原件异常或状态改变时拒绝旧基线；审核事件、竞争版状态、链头和命令完成一起提交或回滚。较早知识截止读取当时状态，物理原件健康仍按本次查询验证。

周期查询仅扫描规则实际需要的最近六个已登记交易日广度窗口，逐证券旧档案不再随400日价格窗口一并读取；窗口外异常档案仍可单独查询并明确报错。数据缺失、窗口不足或日历未核验继续关闭全部周期信号。

## 验证入口与剩余门槛

- `CRS.Tests.Background --staged-documents <新目录>`：合成文件验证续传、不可变块、用途、所有者、配额、消费、原子登记中断及摘要异常。
- `CRS.Tests.Background --document-ingress-boundaries <新目录>`：普通读取身份服务端拒绝、实际管理员的公共/管理通道隔离、4KB普通额度、32KB专用额度及上传限流隔离；传输层使用明确的Spy处理器，不把它称为金融导入成功。
- `CRS.Tests.NationalTeamWatch --breadth-workflow <新目录>`：合成SQLite档案验证登记待核验、版本冲突、原件确认、审核因果、过去截面、否决不复活、事务回滚、删尾及页面竞态；纳入默认回归。
- `CRS.Tests.WorkerEtf --breadth-pipe <完整候选包> <不存在目录>`：实际客户端 → 管理管道 → Service暂存 → 监督Runner解析与持久任务 → 业务回执/执行终态 → 原件导出/周期读取，全部金融事实为合成夹具。
- `CRS.Tests.PluginRecovery --breadth-visual-preview <专项输出> <图片目录>`：真实控件的待核验/竞争/选定三状态、窄宽屏及两种密度，另检查展开编辑区。

仍需：正式逐证券历史与当日名单来源、停牌/新股/退市定义、复权方法与实际证券宇宙验证；自动采集准入及历史回填；独立模型评价及影子记录；联合退出预警；保全式正式数据迁移安装。此次没有访问外网、批准真实市场资料或替换正式Worker。

## 本地验收结果

以下均已终态通过；远端CI未执行，全部金融事实为合成资料。

| 范围 | 证据（仓库内artifacts） |
| --- | --- |
| 38项目Release构建 | `breadth-workflow-release-build-20261028.log`，0警告、0错误；后续只更新截图工具的展开动画等待 |
| 文件暂存 | `staged-document-final-checks-20261028.log`，26项 |
| 档案/审核及页面状态 | `breadth-workflow-final-checks-20261028.log`，29项 |
| 实际管理员管道、Service和监督Runner | `breadth-workflow-pipe-20261028/acceptance.json`，17项，`Installed=false`；回读三组成功命令与对应执行终态，进程在finally停止 |
| 国家队插件全回归 | `breadth-workflow-full-regression-20261028.log` |
| Worker取消及生命周期回归 | `breadth-workflow-worker-cancellation-20261028.log` |
| 实际WPF控件 | `breadth-workflow-visual-final-20261028`，12张三状态/两宽度/两密度截图及2张展开编辑区截图；已查看640宽度展开效果 |
| 未安装候选 | `breadth-workflow-worker-20261028`，`breadth-workflow-package-hashes-20261028.json`核验182份文件摘要 |
| 静态门禁 | 38项目依赖、功能分层、12份客户端、12组XAML/后置文件、50份XAML规范及文档链接检查 |

专项合计72项：26项暂存、29项档案/界面、17项真实隔离进程检查；不把它们计为真实全市场数据覆盖或生产发布验收。早期截图在动画完成前未显示展开内容，已保留并用最终截图及展开高度断言修正验证。

后续反向复核另通过13项文件入口边界检查，见 `document-ingress-boundaries-20261028/acceptance.json` 与同名日志，`Installed=false`。这13项与前述72项合计85项。普通读取身份部分使用服务端调用者合同，公共/管理通道和额度部分使用实际Windows管理员管道；不宣称已用另一真实Windows普通账户完成整套安装验收。专项构建记录为 `document-ingress-boundaries-build-20261028.log`，0警告、0错误；未修改候选包内的生产代码。
