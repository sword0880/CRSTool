# 查询性能、分页与IPC取消

<a id="etf-loading-performance"></a>
<a id="etf-query-stage-profile"></a>
<a id="etf-query-timeout-performance"></a>
<a id="etf-runner-diagnostic-cancellation"></a>
<a id="ipc-eof-brand-header"></a>

## 完整结果与有界读取

ETF的`dashboard`在同一独立、延迟只读快照内取得概览、份额历史和成交，相关原件本次完整核验后复用。独立证券查询只读取自身依赖；巡检仍检查全量。不能以优化为由跳过新损坏、取消、待审、日历和来源门槛，也不跨查询缓存原件核验结论。

独立读端口在初始化完成后发布，在途读取与释放由读取门槛管理；取得读端口不能再次排在长写用例后。SQL、连接、事务与批量读取归仓储，桌面仅组装并展示完整结果。日历排序及按日期索引优化保持逐点口径一致。

V1协商分页保留46KB页响应限制、10,000行/2MiB快照上限及有限快照数量。首页仍按兼容路径读取；只有服务声明能力后才请求至多256行，超字节折半缩页。证券、日期和筛选绑定快照，页大小不改变金融输入身份。

客户端检查快照、元数据、连续游标、实际行数和声明总数，完整收齐才返回。失败、取消、缺页或非法参数不能发布部分结果；已知快照尽力释放，断线由到期机制兜底。切换证券或日期会取消旧读取，迟到结果不能覆盖当前选择。

## 原件额度与取消

原件逐块读至EOF，同时校验大小、规范化/原始摘要并传递当前取消令牌。默认8并发读取，受公共有界准入和查询/后台公平策略约束；读取额度不是任意增加任务并发的开关。并行故障或取消拒绝部分校验结果，句柄、租约、读事务及取消回调必须释放。

SQLite执行保留原生中断；只有取消令牌已取消且返回对应中断码时转换为取消，不能吞掉其他存储错误。取消当前查询不能误伤后续查询或正常采集。

## 通信与诊断

收帧、控制处理、响应写入各自有界，截断帧和未知结果转换为受控错误。查询超时不等于Worker退出；写操作断线可能已受理，必须保留原请求ID查询，不能自动创建第二个命令。普通页面不展示调试字段。

Runner的可选诊断使用32条有界队列及单消费者，拥塞丢弃旧诊断；业务返回和取消确认不等待诊断RPC。一次诊断结果未知后停止继续发送，停止等待有界。金融审计、业务进度和命令回执继续走可靠通道。原15秒业务查询期限、取消宽限及不合作插件隔离保护保留。

## 性能测量口径

`dashboard-profile`和验收分段仅报告固定阶段、耗时、行数及完整结果摘要，不输出SQL、路径、账户、来源URL或密钥。

- `RawVerificationIncludingAdmission`包含准入等待与摘要读取，不能全部称为磁盘耗时。
- SQL与领域投影、序列化、完整分页往返分别记录，残差也不能全部归因IPC。
- 成功和失败都保留前后健康、PID及负载观察；超时记录为Unavailable，不能记零行成功。
- 未控制系统缓存时不称严格冷盘；少量成功样本不能给出长期p95承诺。较早的合成算法提速和某轮现场成功，不代替当前正式负载验收。

历史测量和保全回执主要位于`artifacts/etf-query-performance-20261025/`及`artifacts/etf-fifth-20261026/`。这些是当时证据，文件缺失时不能补造结论；当前风险统一看[架构状态](../architecture/ARCHITECTURE_STATUS.md)与[发布验收](../validation/RELEASE_ACCEPTANCE.md)。

## 验证入口

`CRS.Tests.NationalTeamWatch --etf-loading`、`--business-contracts`、`--dashboard-profile-checks`；`CRS.Tests.WorkerEtf --adaptive-query-pages`、`--raw-batch`、`--query-cancellation`、`--market-query-reliability`；`CRS.Tests.Background --runner-diagnostic-cancel`。

`--query-performance-live`、`--dashboard-profile-live`及`--raw-parallel-profile`属于有条件的现场只读测量，不作为普通回归自动运行。完整参数与前置环境见[测试说明](../../tests/README.md)。
