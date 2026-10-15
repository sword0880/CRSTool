# ETF目录配置与页面滚动

更新：2026-10-15。此增量不改变 ReleaseApproved=false。

## 使用方式

进入“国家队持仓与ETF观察 → ETF配置与来源”，按代码、名称、市场或指数分组搜索；选中或双击ETF后，可修改名称、指数分组、配置理由和监控状态。点击“新增ETF”可填写SSE/SZSE及6位代码，新增默认不启用监控。保存配置需要管理员权限，与既有后台控制策略一致。

市场和代码是固定标识，更换代码应新增记录。停用仅退出自动采集池，保留目录及已有历史事实。2026-10-15用户澄清：20仅指表格约20行的可见容量，超出部分滚动查看，不限制实际监控数量。启用后由现有Worker计划自动采集，可在任务中心立即执行或调整计划。

后台按插件串行执行写任务，采集期间保存会排队，界面显示等待说明；只读查询继续可用。取消桌面等待不撤销已经接受的服务命令，结果可通过任务中心确认。并发旧修订被明确拒绝，刷新后重新选择再保存；输入或修订拒绝不会触发采集故障退避。全部停用时返回 `monitoring=NoActiveEtfs`，不发HTTP请求。

## 参考目录

参考开源项目 [national-team-etf-flow](https://github.com/wwww1998/national-team-etf-flow) 的 [refresh.py](https://github.com/wwww1998/national-team-etf-flow/blob/main/refresh.py) 中23个目标ETF代码，与原有5只取并集，形成24个候选：沪深300、上证50、中证500、中证1000、创业板、科创50、上证180及金融、深证100、中证800和MSCI中国A50等。原有588000保留，新增候选默认全部停用，已有监控和用户修改不被目录初始化覆盖。

这里只独立整理代码、参考名称和分组，不复制该项目Python计算、模拟数据或机构归因逻辑。参考名称及指数待官方核验，目录不代表全市场基金清单或确认的国家队持仓。自动份额来源仍为受限HTTPS官网，不把第三方页面当作份额事实。

## 数据与边界

市场库沿用SQLCipher、独立插件密钥和Schema 2，追加 `etf_configuration_version` 与 `etf_metadata_change` 表；初始化只补齐缺失候选，不清空旧事实或重置启停状态。配置名称/分组、池成员、元数据审计、修订号及命令去重在同一事务提交。新增使用create-only修订-1，修改携带当前修订，避免静默覆盖；不按20行视口限制监控数量。

修复旧 `SetMembership` 只插入不更新名称/分组的问题。已取消界面与仓储层的20只检查，并在数据库打开时删除旧版本两个容量触发器；无需清空研究数据或重设密钥。新增/修改属于后台用例，桌面不创建市场库或下载器。

监控、配置、覆盖率和证据表格采用640像素高度、32像素行高，正常布局约18行。页面整体可向下滚动，表格到边界后滚轮继续滚动页面，下方图表和配置编辑区可达。

本次数量语义纠正已验证：隔离加密库启用26只后重启仍完整保留，重启时自动删除模拟旧触发器并继续启用至27只；真实Worker管道及WPF界面可启用第21只，数据源保留21行且表格内部可滚动访问末行。服务化27项、既有解析/质量/覆盖率、插件宿主视口与双入口只读回归通过。更正后的正式设计和架构文档不再规定20只业务上限；源站限速、超时、取消及单次响应大小保护仍有效。

本次服务包为 `artifacts/worker-etf-viewport`，桌面包为 `artifacts/desktop-etf-viewport`、`artifacts/desktop-etf-viewport-debug`。2026-10-15 21:07北京时间本机LocalService更新，PID35340，健康与数据树权限正常、DPAPI密钥摘要一致。更新后只读检查仍为24个候选、20只启用，保留用户当前启停设置；不会因解除限制自动启用其它候选。Debug/Release调试插件已同步，重新启动主程序后加载新的数量显示。

## 验证入口

使用全新输出目录发布开发包：

```powershell
./tests/Publish-BackgroundService.ps1 -OutputDirectory artifacts/worker-etf-config-v2 -Configuration Release
./tests/Publish-Plugins.ps1 -OutputDirectory artifacts/desktop-etf-config-v2 -IncludeDemo -Configuration Release
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release --no-build
dotnet run --project tests/CRS.Tests.WorkerEtf -c Release --no-build -- artifacts/worker-etf-config-v2
dotnet run --project tests/CRS.Tests.PluginHost -c Release --no-build -- artifacts/desktop-etf-config-v2/Plugins
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- artifacts/desktop-etf-config-v2/Plugins
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- --configuration-live
```

服务化套件在隔离加密库中验证真实管道配置、命令排队、旧修订拒绝后立即合法新增、全停用不访问HTTP，并通过真实WPF页面执行搜索、选中、修改和新增；截图 `artifacts/etf-configuration-ui/configuration.png`。测试ETF均为合成数据，不写实际用户监控池。

完整官方交易日历、NAV金额、250交易日、连续20日以及生产签名和灾难恢复仍需独立验收；补充候选目录不等于这些门槛通过。

本轮Release全解决方案构建0警告/0错误，配置仓储及既有格式/质量/覆盖率回归通过；最终Worker包25项服务化检查及真实WPF新增/修改通过，插件宿主布局/滚动与双入口只读检查通过，后台72项及架构门禁通过。最终Release包：`artifacts/worker-etf-config-v2`、`artifacts/desktop-etf-config-v2`。

本机LocalService于2026-10-15 16:21北京时间更新为该Worker包，PID18564；数据与DPAPI密钥保留，更新前后密钥摘要一致，正式数据树ACL检查通过。只读管道实测目录24个候选、5只启用。双宿主ETF包52个文件逐项哈希一致；Debug桌面包 `artifacts/desktop-etf-config-debug-v2` 及对应Debug只读加载验证通过，Debug/Release的F5插件目录均已同步。运行中的桌面需退出后重新启动以载入新页面。
