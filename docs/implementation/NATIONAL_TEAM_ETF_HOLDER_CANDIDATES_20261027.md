# ETF具名持有人候选接线

日期：2026-10-27。范围是已登记官方PDF到待核验候选、只读分页和核验草稿，不是完整国家队资金流跟踪或正式持仓批准。

## 职责与共享边界

通用PDF文字/坐标读取从个人业务基础设施迁到外层`CRS.Documents`，只依赖既有固定版本PdfPig。`CRS.Common`继续纯BCL；国家队后台和富途导入共同使用文档机制，国家队不引用个人税务基础设施。保留25MiB、200页、200万字符限额；增加最多32个不重复物理页的选择读取。富途适配器将文档错误转换为原业务异常，不改收入、税务或重复主表规则。

`Domain/EtfHoldings`保存候选合同，`Application/EtfHoldings`定义解析端口；后台对应目录分别承担用例协调和PDF模板适配。外层`NationalTeamApplicationComposition`选择适配器，应用和功能用例不引用具体解析类。已登记原件由现有预警Store提供，查询同时绑定证券、来源摘要、provider和Q15；取得同一份经哈希核验的完整字节后结束数据库读事务，再解析PDF。没有新增数据表、连接、计划或自动通知。

## 模板与证据限制

首批模板只登记已保存并查看的510300两份2024/2025年报的精确URL、SHA256和物理页坐标，详见[原件核对](../validation/NATIONAL_TEAM_ETF_HOLDER_SOURCE_REVIEW_20261027.md)。金融数值从原件坐标读取，不硬编码持仓金额或份额；新报告/同URL更换字节返回`UnsupportedTemplate`，不能继承旧模板成功。

解析同时校验封面、表名、份额单位、比例分母、连续11行、唯一登记名称、数值列及联接基金脚注。保留跨行原名和规范化名称、原件身份及页内行号。两家汇金分别列示，联接基金不静默删除；前十未列示的主体没有被填零。

结果统一`NeedsReview`，原件确认和独立持有关系均为false。公告只记录已知日期，准确`PublishedAt`为null。公开期末持仓不是每日成交或现金流；本次不推导主体资金金额、不生成期间净买卖结论。

## 查询与界面

只读`etf-holder-candidates`参数仅为证券及已登记原件ID，不接受本机路径、任意网页或PDF正文。后台完整冻结结果后通过外层`JsonDocumentBlocks`分块；客户端核对版本、原件和证券，保留现有分页容量、快照和取消限制。

“ETF原件与人工核验”页增加候选表。用户主动读取所选原件；选中候选并明确点击后才填核验草稿，法人标识、准确公告时刻及所有确认框不自动填写。切换原件立即清空候选、草稿和原确认、取消旧请求；迟到响应和另一份原件返回不能写回。失败明确不可用，未知模板明确不支持。既有人工保存门槛保留；日期缺少准确时刻的候选不能直接登记为最低净减少预警证据。

## 验证与剩余门槛

默认`--etf-holder-candidates`使用合成资料检查未知模板、原件/取消/公共读取参数，以及界面迟到响应、草稿、失败和错原件。`--etf-holder-golden <原件目录>`另读取两份真实公开PDF及独立页面摘录，在普通SQLite隔离库逐行比对，再调用真实后台只读分页；不访问正式服务或发送HTTP。

本轮已取得以下证据：

- 38项目Release零警告、零错误；包括最终窄屏列宽修正的记录为`artifacts/national-team-holder-final-build-v4-20261027.log`。架构门禁与功能目录门禁通过。首次门禁指出应用装配引用具体解析实现，已移入外层组合根，没有放宽门禁；新增管道测试缺少System.IO引用导致的首次编译错误已修正，原日志保留。
- 真实公开原件专项50项及界面草稿/迟到响应5项通过；对照两期全部22行，未知模板、证券/来源不匹配、Q15、ParseFailed、原件损坏、取消及完整后台结果均有反例。日志为`national-team-holder-golden-final-20261027.log`。初版脚注文字和跨页拼接曾导致拒绝；按原页修正。短报告可在一个传输块中完整返回，测试按实际编码块数核对，完整内容比较保留。
- NationalTeam默认完整回归通过，含新增11项不依赖外部年报的候选边界和5项界面状态反例；`national-team-holder-full-regression-20261027.log`。
- 首份完整候选包经真实Worker、监督Runner和管道14项通过；两份原件分别以持久命令登记并按原ID回查，查询及命令重放只发生两次夹具原件读取，没有已审核具名持仓。`national-team-holder-pipe-20261027.log`和隔离目录`national-team-holder-pipe-20261027/acceptance.json`。同候选48项Worker回归及11项并发检查通过；`national-team-holder-worker-regression-20261027.log`。这些是保存原件回放和隔离数据，不是官网实时或已安装服务验收。
- 富途PDF/Excel收入及版本证据16项通过，保险库提供方10项通过；`national-team-holder-futu-regression-20261027.log`及`national-team-holder-vault-boundary-20261027.log`。不改变收入/报税加密或金融计算规则。

最后仅调整候选表的名称换行、最小数值列宽及标题，并加入真实窗口列宽断言。最终完整候选为`artifacts/national-team-holder-worker-candidate-v2-20261027`：169个发行索引文件全部匹配，未含测试夹具或个人税务程序集；插件相对首包仅UI DLL/PDB变化，全部后台文件逐字节一致，见`national-team-holder-package-hashes-v2-20261027.json`。最终包再经14项监督进程/管道回放通过，见`national-team-holder-pipe-v2-20261027.log`及同名隔离目录的`acceptance.json`；没有因UI调整重跑无关金融逻辑。

最终桌面自动开发包的五个内部页面创建及布局通过，`national-team-holder-package-load-v2-20261027.log`。640/1040/1280 DIP真实窗口3张合成截图通过列宽断言，并查看640和1040视口；份额小数及比例完整显示，长名称可换行。首次窄屏数值被压缩的截图保留但不作为最终验收。截图及口径见仓库`design-qa.md`，最终日志为`national-team-holder-ui-v2-20261027.log`。

需继续扩展实际配置ETF池、官方报告发现、新模板、其他国家队主体及日精度披露的独立事实审核；不能据这两期候选宣称整体目标完成。正式Worker安装和金融审核未执行。

最终文档检查覆盖156份Markdown、683个本地文件/图片链接，全部存在；`artifacts/national-team-holder-doc-links-final-20261027.json`。`git diff --check`通过。文件存在性不代表历史文档中的现场或模型结论已重新验收。
