# 国证公布指数点位与估值快照事实链

当前状态：后台代码已保留维护安装；新来源生产采集因用途证据待核保持关闭，正式价格/PB事实为0。本地原件隔离复算与正式缺口显示分别留证，整体研究未完成。

## 来源和目标

第三轮窗口北京时间2026-10-25 14:43:32至19:43:32，沿原目标补R06价格及R11行业走势/PB输入。现有公布全行业PE、国证1000行业样本指数、国证A指和具名持仓分别存证，不互相改名替代。

官网[module/index-detail.html](https://www.cnindex.com.cn/module/index-detail.html?act_menu=1&indexCode=399317)显式引用entry-index-detail.js、index-detail.js、common.js；脚本指定hq.cnindex.com.cn及getIndexDailyDataWithDataFormat的indexCode/startDate/endDate/frequency=day参数。首次真实399317样本为2025-09-19至2026-10-23共261条，字段头11项、total=261，与数组数一致。HTML明确成交量万手、成交额亿元，并说明均为指数样本汇总；不是全市场或机构成交。

[当前国证1000行业方法](https://www.cnindex.com.cn/docs/gz_399381.pdf)本次取得4页、SHA256=40D5AFC05865E31E8242A3719542B4BC50AE5AFBD5CA42407C20363912444F31，列出10个指数。其他旧文件仍有11条和旧电信代码，不据名称混入同版本目录。[国证A指当前方法](https://www.cnindex.com.cn/docs/gz_399317.pdf)SHA256=6BFE1E609422F4107F99613E54CFA7F20806E57809D370A564C2C1E73AE96D83，覆盖符合纳入规则的沪深北A股并排除ST/*ST；不能将其指数样本成交冒充全市场成交。

官方指数系列脚本用/index/queryDay标注统计日期，用/index/indexList?channelCode=201&rows=20&pageNum=1返回当前行业指数列表。首20行包含固定目录10条；该页总计200条不等于本研究覆盖200条。PE/PB字段存在，但每行没有独立日期，须在相同日期头前后取得、与该日独立日线收盘逐项匹配才能登记为“网页标注日快照”。samplesize=0是未提供有效样本数，不得用作PE覆盖率或零成分股；也不能将当期快照填入历史252天。

## 实施及开放边界

新增独立指数点位/估值快照领域契约，固定当前目录、方法与单位；只允许当前知识公布序列研究。价格曲线采用同一核验起点100，缺日/待审/损坏原件断线。PB历史、估值市值覆盖、基本面及历史方法一致性仍未验证，机会固定未评估。候选已接加密修订存储、后台手动入口、分页查询和行业页第二标签；完整包及正式维护仍须独立验收。

源站、脚本、方法、首批JSON及官方历史导出保存在artifacts/national-team-index-research-20261025/source。历史导出虽名为xls，实际为OOXML且仅价格/成交列，没有历史PB；不猜测额外日期参数或私有接口。元数据脚本一次404源于RequireJS相对基址解析错误，已按实际/common/market-download.js修正；数据请求均HTTP200，未绕过拒绝或冷却。正式SSE冷却至17:05:42保持不变，HQ与WWW国证将共享CNINDEX预算。

## 来源准入与协作对齐

开发途中main由用户提交/合并至981a8c19872a0f5ce78e3951c8b0bc4b3eee68fb，已读取PR20新增的[来源可行性](MARKET_CYCLE_DATA_SOURCE_FEASIBILITY_20261025.md)、[P2接入设计](MARKET_CYCLE_P2_INGESTION_DESIGN.md)和MarketCycleSourceAdmission；没有切分支、合并或重复实现其周期模型。

[国证产品服务](https://www.cnindex.com.cn/zh_services/data_licensing/index.html?act_menu=4&act_service=3)区分对外展示授权及需签数据服务协议的产品。本轮未取得明确适用于个人本地自动下载、持久化和历史回填的条款/确认，不将网页可访问或任务开发授权作为来源许可。因此新增采集候选在任何HTTP前调用现有准入门禁，分别核验cnindex-published-prices与cnindex-published-valuation的数据集、来源、契约/解析版本、原件哈希、有效时间和用途；历史价格要求HistoricalBackfill及PersistentStorage，当前估值要求AutomatedCollection及PersistentStorage。分段采集和金融提交前再次核验，避免长步骤跨越到期/撤销边界。

生产入口未配置审批台账提供方，默认SourceApprovalRequired且HTTP=0；只读报告仍可查询，并向页面返回两个采集准入状态，未批准按钮关闭。没有伪造审核人或协议，没有因本轮候选自动启用计划。审批台账持久化、真实许可及正式采集仍开放；隔离夹具使用显式SyntheticFixture许可，不能登记为正式来源授权。

## 事务与反方检查

六张独立指数表以feature_schema_migration=index-series/1登记；不改写旧PE或国家队事实。捕获证明绑定角色、精确URL、取得时间、原件哈希、解析事实及估值所依赖的具体价格修订；解析事实变化不能仅靠原件健康掩盖。金融修订、追加状态事件与双命令回执同事务提交，取消回滚。不同值进入NeedsReview；拒绝后同原件再送审保留新的实际知识时点。缺捕获/状态事件查询失败关闭，不以连接漏行恢复旧值。

PB必须同时满足前后同一官网日期头、正确目录和各指数同日健康价格。价格差异待审或价格基线更正会使旧PB证明不可用；旧知识截止仍可回看旧状态。后置日期头畸形归来源合同待审，不归宿主退出。HQ/WWW复用CNINDEX持久冷却，跨域不能绕过403/429。

当前76项原件/领域/加密存储/后台会话检查及12项隔离页面检查已通过；随后增加金融值损坏、缺状态事件和未获许可按钮检查，最终结果以后续验收记录为准。首次截图发现夹具误传最后一天导致空曲线，已修正为完整四日并要求每条起点100、末值非空且至少一条有变化。原始截图和日志保存在上述证据根，不将测试进程、合成日历/取得时点或合成许可称作正式Worker验收。远端CI尚未运行；工作流仅构建新增专项，不联网下载或上传未确认再分发许可的原件。

## 17:01正式维护与17:07只读回查

最终本地检查为78项指数链、13项隔离页面、11项完整候选管道；既有国家队516项、行业54项及新增农行后的来源预算74项通过。完整Release构建和桌面构建均0警告/0错误，架构及42个XAML门禁通过。v1/v2不可变保留，最终后台使用worker-candidate-v3，release.json SHA256=336C867D1A60453A95D9F59840161096ED571E13628D3CEB359F29D8C92AA342；远端CI未执行。

正式保留维护6dfed905a99a4b57bbc407be5c40544f于北京时间17:01:03 Committed，新PID36324。独立Python核验5886个一致性快照文件、原密钥密文摘要、候选/安装程序清单哈希，并比较维护前后7计划、24ETF配置和29股票记录均保留。启动时先核验全部快照，Switched阶段多次健康探测未就绪，随后Starting/Healthy/Committed；未重复启动事务、绕过核验或覆盖旧库。证据为upgrade.log、upgrade-journal.json、preservation-acceptance.json、state-preservation.json及before/after。

正式新指数事实仍为0，11行均明确0/261及未知价格/PB，两个采集准入标记false；未提交正式新指数采集。installed-index.json及installed-index-empty-state.json来自实际业务管道，不能将其与本地261日A指、四日10行业和10个PB原件回放混为一组正式覆盖。原全行业PE正式回读保持11行业、2871修订、核验261交易日、待审0，机会仍未评估。

正式页面复核发现空图控件会生成默认零刻度，已在桌面视图层改为“尚无可展示的已核价格”占位；13项非空回放及正式空值界面均通过。此后普通Release构建已自动同步两个桌面开发插件及可信索引，不手动覆盖后台不可变v3包；后台不加载WPF，空图修正不要求第二次后台维护。当前打开的用户桌面需重新启动以加载新插件；没有强制关闭用户窗口。最新页面证据为installed-index-empty-state.png和index-ui-final-visible.png。

来源用途证据/审批台账、真实正式价格/PB采集、可比PB历史、市值覆盖、基本面、历史方法一致性及时间样本外验证继续开放。两个候选数据集的许可询问尚未取得明确条款位置，继续默认关闭，不能以等待经过的时间视为批准。
