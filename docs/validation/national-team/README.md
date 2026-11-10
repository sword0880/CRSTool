# 国家队数据源与专项验证

本目录保存国家队公开数据源的日期化POC、来源行为核验和专项验证快照。**单次POC通过不等于持续采集、历史覆盖、来源许可或正式发行验收通过。**

## 历史验证记录

- [国家队ETF联网数据通道POC（2026-10-14）](DATA_SOURCE_POC_20261014.md) — 上交所/深交所份额数据通道的单时点核验及明确边界

## 当前验收入口

- [通用发布验收](../RELEASE_ACCEPTANCE.md)
- [国家队当前实施记录](../../implementation/NATIONAL_TEAM_EVIDENCE.md)
- [Worker运行与监控状态](../../implementation/WORKER_RUNTIME.md#monitor-readiness)
- [当前架构状态](../../architecture/ARCHITECTURE_STATUS.md)

新增验证记录必须包含测试日期、数据截至日期、来源/接口版本、样本范围、原始证据位置、失败/缺失处理和未覆盖范围；不得把历史回填或任务成功记录等同于连续真实采集。
