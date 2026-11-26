# CRS.Feed 公共 Feed Engine

> 状态：首版实施规范。项目边界与验收以本文件为准。

## 1. 定位

CRS.Feed 是可复用的公共基础设施类库，不是插件，也不是独立 Windows Service。它负责把外部 RSS / Atom / JSON Feed 转换为标准化、受资源限制的内容对象。

## 2. 依赖方向

~~~text
CRS.Common
    ↑
CRS.Feed
    ↑
需要通用 Feed 能力的业务插件 / 其他调用方
    ↑
CRS.Worker.Service（唯一调度宿主）
~~~

- CRS.Common 继续保持 BCL 优先，不引用 CRS.Feed。
- CRS.Feed 可复用 BoundedContentReader，但不依赖 Worker、Quartz、WPF、数据库或 AI。
- 业务插件负责来源地址、业务过滤、持久化和证据规则。
- Worker 负责调度、任务并发、重试策略与生命周期；RSS Engine 不启动自己的 Quartz。

## 3. 首版能力

| 能力 | 设计 |
|---|---|
| 格式 | RSS 2.0、RSS 1.0、Atom、JSON Feed |
| HTTP | HttpClient、ETag、Last-Modified、手动重定向 |
| 安全 | HTTPS 默认、拒绝 URL 凭据/片段、本机和私有 IP 字面量；禁止 XML DTD/外部实体 |
| 资源 | 正文默认 512 KiB、最多 200 篇、12 秒超时、最多 3 次重定向 |
| 限流 | 识别 HTTP 429 与 Retry-After，交由调用方决定重试 |
| 结果 | Updated、NotModified、RateLimited、AccessDenied、NotFound、RedirectRejected、TooLarge、InvalidFeed、TimedOut、Unavailable |

所有限制可通过 FeedClientOptions 调整，但调用方不得取消响应大小、超时和取消令牌边界。

## 4. 不在 Engine 中实现的能力

- 定时调度、任务状态、重试队列和来源预算。
- 业务数据存储、去重主键和历史文章清理。
- 雪球账号查询、雪球作者时间线或其他来源专属逻辑。
- HTML 渲染、AI 摘要、情绪/观点分析和证券归属。

## 5. 雪球插件的数据源边界

雪球观点追踪通过雪球作者时间线 API 和原帖详情 API 采集指定作者动态，不使用 `CRS.Feed`，也不依赖 Google Alerts、Google RSS、RSSHub 或浏览器自动化。采集任务沿用 Worker 任务中心的直连或 Mihomo 网络线路。

`CRS.Feed` 保留为独立的通用 Feed 基础库，供其他明确需要 RSS / Atom / JSON Feed 的模块复用。雪球模块移除 Feed 依赖不影响该公共库及其他调用方。

## 6. 反方审查与验收

- 跨主机重定向必须拒绝；同主机重定向必须重新校验协议和 URL。
- 304 不得被解释成“作者没有发言”，只能解释为服务端确认 Feed 未变化。
- 429、超时、无权限、无效 XML 和正文过大不得覆盖历史线索。
- 一个来源失败不得阻断其他来源。
- 文章 URL 必须重新规范化并校验作者账号，不能信任来源标题、作者字段或摘要。
- 采集成功不等于覆盖完整；Feed 源返回空列表也不能证明作者没有发言。
- 服务账户仍需配置出站网络 ACL；URL 字面量校验无法完全防止 DNS 重绑定。
