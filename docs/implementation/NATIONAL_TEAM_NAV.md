# NAV事实与份额金额研究

<a id="national-team-nav-deployment"></a>
<a id="national-team-nav-research"></a>
<a id="national-team-nav-source"></a>
<a id="national-team-nav-storage"></a>
<a id="national-team-nav-workspace"></a>

独立“NAV与份额金额研究”桌面页面和导航已于2026-11-05移除。后台NAV事实、修订、研究算法和只读查询保留，供联合研究及离线复算使用；不再维护已删除页面的操作步骤。

## 来源与采集

已核对的固定来源限定SZSE/159915：

- [基金产品页](https://www.efunds.com.cn/fund/159915.shtml)：身份、最近净值、历史总数和引用链接。
- [历史数据](https://cdn.efunds.com.cn/market/2.0/his/159915_all.js)与[最近一年](https://cdn.efunds.com.cn/market/2.0/159915_1y.js)：按日期核对重叠并去重。
- [官方显示脚本](https://www.efunds.com.cn/js/fund_details/product-detail-overview.js?7905e4a6)：核对单位净值、累计净值和日期字段映射。

只解析固定数据外壳，不执行脚本、不猜隐藏接口。四原件全部取得并通过身份、长度、摘要、字段和衔接校验才提交；冲突、挑战页或缺一份不能用旧缓存冒充本次成功。官网/CDN共用EFUNDS预算，冷却不通过换域名绕过。

`nav-sync`、`nav-review`为明确手动任务；不因打开页面、启动服务或有多年历史就自动开启持续采集。跨基金、长期来源许可和持续稳定性须分别验收。

## 持久净值、冲突与知识时间

独立NAV表保存capture、四份source_fetch绑定、每日单位/累计净值修订、追加状态事件及双重命令回执。不把一个净值直接塞入份额点而丢掉来源和知识时点。

正式FirstSeenAt、AcceptedAt取真实登记时钟，RetrievedAt保留下载回执，逐日官方PublishedAt未知。回填多年历史不能倒填为系统当时可知。知识截止按追加事件重建，时钟倒退、缺少双回执或同ID改负载拒绝。

后续不同净值进入NeedsReview并阻断该日，不回退旧接受值绕过冲突。接受更正时明确取代/拒绝相应版本，拒绝或重复来源不自动复活旧版。每次金融读取重新核验整组原件、provider、哈希及Q15；损坏返回不可用，不能以空结果或旧值替代。

2026-10-25的一次正式手动采集登记3635条，覆盖2011-09-20至2026-10-23；正式首见为实际登记时刻。该历史验收不表示当前已同步到最新日、全池有NAV或自动采集已经验收。

## 金额研究

1/5/20交易日窗口逐日计算并求和：`Σ[(Q_t−Q_(t−1)) × 单位NAV_t]`。不能用累计净值、IOPV、成交价，或期末净值乘整个期间份额差额。

`EtfNavStudyStore`在同一只读快照冻结当前证券份额/修订、已登记日历、NAV知识截止、可比性和原件身份，并提供预热区间。缺日历、前置日、份额、来源时间或可比性时不计算差额/金额；只缺同日NAV时可保留可比份额差额，金额仍未知。未知不是零。

报告版本`etf-nav-study-v2`固定`RetrospectiveOnly / NotProven / NotObserved`。这是基金整体份额变化金额估算，不是国家队具名成交、精确现金净流入、顶底或退出信号；数学复算通过不证明原件再次验收或预测有效。

## 传输与复算

`nav-study`先冻结完整研究，再通过`JsonDocumentBlocks`分块；证券、日期、连续偏移、总长、统一SHA256和版本共同绑定。缺块、乱序、混入另一文档、超限或取消拒绝整份结果，不逐页重算，也不提高原宿主页/快照/超时限额。

主要入口：`--nav-source-checks`、`--nav-storage-checks`、`--nav-research-checks`、`--nav-workspace`和`--nav-workspace-verify <冻结JSON>`。冻结文件复算不联网、不读正式库；真实采集及审核另沿稳定命令处理。准确命令和参数以[测试说明](../../tests/README.md)与对应测试入口为准。

研究、隔离存储、完整后台包、正式采集和金融覆盖分别验收。历史原件与本机维护证据位于`artifacts/national-team-marketdata-20261024/`等目录；当前发布缺口见[架构状态](../architecture/ARCHITECTURE_STATUS.md)。
