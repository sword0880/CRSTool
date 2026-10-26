# 公牛集团披露覆盖：发行人原文已取得，正式审核未完成

状态：603195当前两个候选、0已核、0历史事件；官方报告目录检索完成16份。09:01从发行人官网独立取得半年报并逐页核对，尚未扩展正式固定来源或审核两个账户，不增加正式持仓数量。下文07时的原件失败继续作为历史证据保留。

07时续项先通过实际Worker只读核对当前候选、0报告目录及报告检索PendingRequests为空，才提交原请求76f7e19c-8702-4ca4-8715-080cfcbfdb69。已Completed/Succeeded，正式目录含2026年半年度报告、公告日2026-08-28及其他15份报告。16份目录不能当作16份股东原文或16条已核持仓，已完成的目录检索不再重复排队。

[上交所公司概况](https://www.sse.com.cn/assortment/stock/list/info/company/index.shtml?COMPANY_CODE=603195)明确XBRL展示须以PDF为准。本轮公牛尚属候选；其来源日期不是已经确认的持仓截止日。公司行业当前网页分类也不能直接回填为历史有效行业映射。

正式目录指定原文为[2026年半年度报告](https://static.sse.com.cn/disclosure/listedinfo/announcement/c/new/2026-08-28/603195_20260828_4QCU.pdf)。网页工具无法读取；随后北京时间07:16:20对同一固定地址进行一次本地公开原文请求，返回200、Content-Type=text/html，正文未通过%PDF-校验。没有原PDF文件、PDF摘要、页码核对、逐账户审核或持仓登记。不能把200当原件有效，也不能臆测为403、已获许可或后端故障。

保留capture.json及失败日志，未自动换源、修改请求头重试、扩大白名单或用其他报告替代本期原文。后续须取得合法可验证的同一报告原件，再核对完整股东表、股类/单位/分母、报告期及公告日、具名账户与投资性质。性质未知时继续Unknown且差额不可比；名义账户不能升级为实际机构。

证据根artifacts/etf-fifth-20261026：gongniu-before的只读数据、gongniu-report-search的稳定请求/目录/回执、gongniu-original/capture.json及etf-fifth-gongniu-capture.log（日志位于artifacts根）。当前整体仍13条当前已核快照/3只股票，新增建发前期四条另计，不能推导国家队完整方向或交易。

## 09时独立发行人来源核对

[公司官网投资者关系](https://www.gongniu.cn/investor/index.html)公开脚本的disclosure只读接口，按页面已有year/page/pagesize/catid/type参数查询2026信息披露，明确链接[2026半年报PDF](https://gnstaticcdn.gongniukaiguan.net/uploadfile/2026/0831/20260831101946479.pdf)。该选择来自发行人页面，不是对失败地址的自动回退、伪造SSE响应或猜测CDN路径。公开POST只用于读取公司披露列表，未写入外部内容。

09:01:28实际HTTP200/application/pdf，1233414字节，162页，SHA256=2BCCE8A397D72AD7BDB9A414A357246BDDBC842E5C1AFBE3BC384238BE4AAD8A。封面及第34—35页已渲染目视核对：完整前十名股东表、单位股、不含通过转融通出借股份；社保114组合7879000股/0.44%，养老808组合4520552股/0.25%，与正式候选一致。前十名无限售表明确为人民币普通股；香港中央结算52902652股仍为名义账户，不归国家队。两个账户报告内增减栏均为0，不能替代前期独立原文、可比审核或成交证据。

第33页说明7月9日回购注销500969股，总股本从1803946978变为1803446009；不能将公告日股本倒填6月30日持股分母。报告期由释义明确为2026年1月1日至6月30日；SSE目录公告日8月28日与发行人8月31日路径上传时间分开，实际取得知识时点为10月26日，不回溯成此前系统已知。当前投资性质和期间比较仍Unknown/null。PDF和网页/接口原文及捕获凭据保存在gongniu-issuer-discovery，不等于正式Worker已登记该来源。

## 精确来源候选

候选仅新增固定CDN路径/字节摘要/603195/SSE/2026-06-30匹配；不准许同目录、查询参数、ZIP、普通官网页面或其他报告期。官网与披露CDN在插件策略及Worker宿主共享ISSUER-603195持久预算，沿三秒间隔、失败与实际Until，不清除SSE或其他发行人冷却，不自动替换任何已排队SSE请求。

8项真实原件隔离核验通过，含完整十户/两个具名账户一致、错证券/报告期/路径拒绝、原文变化失败关闭、重启回读Unknown/null及完整导出。首次受限测试进程在自有临时目录File.Move被拒绝；原日志gongniu-golden.log保留，正常权限复核通过于gongniu-golden-verified.log，未据此改写生产逻辑。宿主共享来源78项、插件冷却及故障恢复专项通过；完整Release0警告0错误、47公开类型兼容及分层门禁通过。完整Worker和默认国家队回归仍按实际句柄等退出，不把在跑当完成。

v5不可变候选worker-candidate-v5，发行1F1DF93328C52D0EE0E6236D945A5B794068D73FFC4B557B0980208DD43292EA。需全部验证终态、正式维护Committed及独立保留之后，才用gongniu-review-live按两个固定账户分别保存稳定请求、回查原句柄和正式执行/持仓。当前未安装、未审核，13/3的正式覆盖数量不提前升级。

09:29核对：默认国家队537、完整隔离Worker47均已退出0。v5保留维护f6aab6806f824ee3ac9ccef6ebfaeb66已Prepared，执行句柄83059，日志artifacts/etf-fifth-gongniu-completion.log。只沿该事务观察；complete_gongniu_v5.ps1依序等Committed、回读、逐项verify_preservation_v5.py，再执行两账户稳定请求及verify_gongniu_review.py。任一步失败保存Unconfirmed、停止后续审核，不重开ID；10:35以后不启动新维护/审核以留10:48:40绝对截止收尾余量。终态必须读取gongniu-completion.json、保留验收及审核回执，不能凭脚本已启动填成功。
