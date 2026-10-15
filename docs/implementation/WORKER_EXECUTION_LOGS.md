# 任务中心执行日志（2026-10-15）

## 日志窗口可读性与滚动

2026-10-15界面增量：浅色背景、白色圆角卡片，标题/任务/执行ID、状态标签与最近进展分层呈现；已结束记录显示最后阶段，不再持续累加距今秒数。正文增加内边距及22像素行距，保持原生只读文本选择和复制。右侧14像素滚动条常驻，独立于主题自动隐藏样式；滚动事件显式作用于日志视口，支持拖动、翻页、滚轮，另有回到开头/跳到末尾。底部刷新及关闭操作保持可达。

窗口专项以91条合成阶段验证可见滑块、实际滚动范围、拖动后视口改变、滚动到末尾和720×500最小布局，并回归自动刷新、稳定执行ID和取消清理。Release构建0警告/0错误、窗口检查通过；截图 `artifacts/log-window-polish/log.png` 已核对实际绘制。用户当前Debug程序占用原输出文件，Debug使用独立目录 `artifacts/log-window-polish/desktop-debug` 发布验证；关闭原程序后正常F5重建使用新界面。本轮只修改桌面日志展示，不更新Worker或修改研究数据。

独立Debug发布成功，配套复制原已核验Debug插件包用于完整窗口冒烟；窗口仍使用隔离测试保险库，不关闭当前用户程序。架构门禁和差异空白检查通过。

## 阶段日志增量

新的ETF执行通过可选 `IProgressBackgroundSession` 回调保存结构化阶段：区间/日期开始、来源开始/解析结果/失败、HTTPS请求开始、HTTP响应头、来源间隔等待、冷却截止、连接失败或20秒超时、当日提交及区间汇总。请求返回响应头不表示正文已经校验成功；来源解析行数可能包含未监控证券，提交阶段才按当前池过滤。日志不保存完整URL、参数、响应正文和原始异常消息。

数量口径明确分离：收到行数；新增已接受事实；同值重复跳过；新增待审核修订。待审核不算已接受；失败或回滚不会报告为当日写入完成。同一天的来源事实各自事务提交，若后续导入失败，之前已提交事实仍保留，不能由缺失的当日完成事件断言整天回滚。最终执行结果仍以宿主持久状态为准，区间查询结束不代表全部来源成功。

加密调度库Schema 2内增量创建 `execution_progress`，按执行ID/序号索引，宿主生成时间和顺序，阶段只接受固定白名单与标量。每次执行保留最近512条，`GetExecution`响应返回最近128条，避免超过IPC消息上限；首条序号大于1时窗口明确说明更早阶段未在本次响应返回。这是有界阶段日志，不是无限量完整调试流水。业务执行保留最近500条、诊断心跳单独保留最近100条，运行中执行不清理，防止心跳耗尽业务历史额度；记录清理时阶段随外键级联清理。阶段持久化失败进入StorageFault并取消采集，不悄悄改成仅内存日志。

日志窗口在Running时每3秒只读刷新，展示最后阶段时间及距今秒数，完成后停止轮询；关闭、锁定或取消父会话后停止。无法查询时保留上次快照并明确提示。没有新阶段只说明没有新的已保存进展，不能直接判定死锁。任务中心支持插件编号、任务编号独立或组合筛选，默认插件为crs.national-team；留空表示全部，点击“应用筛选”重置分页。下拉建议来自计划及已读历史，也可输入尚未加载的手工任务编号。服务端SQL先按插件和任务过滤再分页，“更多历史”沿用已应用条件；编辑尚未应用的输入不会改变当前分页范围。

旧服务/旧执行没有阶段时仍展示原摘要，不能补造历史过程。扩展为IPC v2的可选字段，未改变原有命令权限、调度所有权或来源限速。实际使用需同步更新Worker和桌面。

验证入口：

```powershell
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- artifacts/worker-progress-heartbeat-v3
dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- artifacts/worker-etf-progress-v3
dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- --source-policy
dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --execution-log-ui artifacts/etf-progress-ui/log.png
dotnet run --project tests/CRS.Tests.Background -c Release --no-build -- --etf-progress-probe
```

前三项使用隔离数据库/固定HTTP，不向用户库写合成数据；最后一项只读实际安装服务最近的ETF执行及阶段，不触发采集。来源长期覆盖、官方交易日历与NAV金额的开放门槛保持不变，ReleaseApproved=false。

阶段增量验证：后台29基础＋36整改＋9权限＋8仓储阶段，共82项通过；最终包ETF服务化27项、来源策略20项、既有ETF质量/覆盖率及新数量统计通过。实际WPF日志窗口验证3秒自动刷新、等待截止、最新阶段、终态数量及关闭清理；完整桌面冒烟无绑定错误，插件宿主/双入口只读检查与架构门禁通过。

最终服务包 `artifacts/worker-etf-progress-v3`，故障隔离包 `artifacts/worker-progress-heartbeat-v3`，桌面包 `artifacts/desktop-etf-progress`、Debug包 `artifacts/desktop-etf-progress-debug`；双宿主ETF包52文件逐项一致，Debug/Release的F5插件已同步。2026-10-15 20:32北京时间本机服务更新，PID20520，数据及DPAPI密钥保留且摘要一致、数据树权限检查通过。真实执行 `f5ad89fc9ffb4bc0b3304f6377eb3548` 于20:32:42开始，20:33:16实测已有74阶段，包含2026-10-12两市HTTP200、来源解析及3秒间隔等待，证明新阶段来自实际官网执行。该时刻仍为Running，不把运行中采样当作最终完成结论。旧历史被清理的阶段无法恢复；主程序须重新启动载入新日志界面。

后续只读复核：同一执行于20:33:38成功结束，完整126个阶段；8个查询日、收到25条、新增已接受0条、重复跳过25条、新增待审核0条，来源问题0。这解释了本次采集已经真实执行但表格没有新增记录。最终Release全解决方案构建0警告/0错误，Debug桌面发布与加载验证通过。

“最近执行”中的实际数据行支持双击查看运行日志，也可选中后按回车或点击“查看日志”。双击表头、滚动条和空白区域不打开上次选中记录。窗口绑定稳定执行ID，刷新不会跳到同名任务后来的另一次执行。

## 日志边界

日志展示服务已经持久化的计划触发时间、开始时间、结束记录、尝试次数、结果和检查点。ETF检查点转换为自然日查询数、收到记录数和来源问题数；异常仅保存异常类型分类，不保存可能含凭据、请求参数或本机路径的异常消息与堆栈。窗口时间固定为北京时间（UTC+08:00）。

初版只有结束摘要。2026-10-15阶段日志增量开始保存新的采集过程；旧记录未保存的网络过程、解析步骤或异常堆栈不能补造。历史执行始终按固定执行ID查询，不把当前插件总体状态冒充旧执行的进度。

## 查询与生命周期

IPC v2新增向后兼容的 `GetExecution` 只读命令及可选执行ID/执行记录字段。服务只接受32位N格式GUID，以参数化查询从加密任务库读取单条记录，不扫描首屏历史或接受文件路径；普通已认证查询身份与历史列表使用相同授权和限流。摘要初版未增加表；本轮在Schema 2中增量创建阶段表，不重置既有数据或密钥。

服务返回UnknownExecution时，窗口说明记录已被清理并保留上次快照；旧服务UnknownCommand明确提示更新服务。服务离线、超时和限流也保留带提示的快照。关闭窗口取消当前查询；父会话取消/锁定时日志窗口关闭，避免敏感工作区锁定后留下窗口。

## 验证入口

```powershell
dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --execution-log-ui artifacts/execution-log-ui/log.png
```

窗口专项通过真实命名管道和实际行双击验证模态窗口、固定执行ID、运行中到终态刷新、ETF摘要、北京时间、清理快照、旧服务提示、取消及空白双击。服务专项并入后台回归，覆盖非首屏记录、普通查询身份、非法标识、不存在记录、运行中/完成记录及异常分类脱敏。全部使用隔离夹具和合成数据。

本轮后台回归为29项基础＋34项整改＋9项权限，共72项；最终完整包ETF服务化19项通过。Release构建0警告/0错误，架构门禁通过。窗口截图为 `artifacts/execution-log-ui/log.png`，已核对实际绘制。

最终服务包为 `artifacts/worker-execution-log-reviewed`，桌面包为 `artifacts/desktop-execution-log-reviewed`；双宿主插件包52文件哈希一致。2026-10-15 15:35本机LocalService已更新，两个插件健康、数据树权限正常，原有数据和DPAPI密钥保持不变。后台隔离回归必须使用单独的 `artifacts/worker-execution-log-tests` 心跳包，不用官网联网混入故障套件。


## 日志设置与实际位置

桌面设置仅控制 `%LOCALAPPDATA%/CRS/logs/crs.log`（目录可配置），保存后立即更新NLog；降低级别只放行已有事件，不自动触发采集或补写历史。服务不读取登录用户settings.json。

任务详细阶段及执行结果集中保存在 `C:/ProgramData/CRS/Background/scheduler-config.db` 加密调度库，通过任务中心/IPC按执行ID读取，未按插件复制为文本。服务结构化诊断为 `C:/ProgramData/CRS/Background/logs/service-yyyyMMdd.jsonl`，固定分类、每天5MiB和14文件保留，与桌面日志级别独立。插件数据私有目录为 `C:/ProgramData/CRS/Background/plugins/<插件编号>`，ETF数据库位于其data子目录；程序安装包 `C:/Program Files/CRS/Background/Plugins` 不用于写运行日志。


2026-10-15日志设置与筛选验证：Release全解决方案0警告/0错误；即时日志设置专项验证同一记录器的级别、目录、关闭和轮转。后台29基础＋38整改＋9权限＋11阶段仓储，共87项通过，覆盖真实管道组合筛选、空结果和分页。WPF实际窗口通过筛选、更多历史、清空范围及两个输入可见性检查，截图 `artifacts/log-filter-ui/background.png`；服务刚启动时首次窗口查询未取得快照，健康确认后复验通过。插件宿主、架构门禁、diff检查通过。Debug独立桌面发布及插件索引验证通过，Debug/Release的F5插件已同步。

本机服务更新为 `artifacts/worker-log-filter`，21:24:57（北京时间）启动、PID45092；保留数据及DPAPI密钥，摘要一致且正式数据树ACL检查通过。桌面发行 `artifacts/desktop-log-filter`，独立Debug发行 `artifacts/desktop-log-filter-debug`。新组合筛选使用只读 `GetFilteredHistory` 命令，旧服务明确拒绝，避免忽略任务条件；旧 `GetHistory` 入口保留。当前桌面会话须重新启动加载新代码，此后日志设置保存立即生效。
