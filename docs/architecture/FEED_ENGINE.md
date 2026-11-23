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
业务插件 Jobs / 其他调用方
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
- 雪球账号查询、Google Alerts 查询规则、Google 跳转链接还原或其他来源专属逻辑。
- HTML 渲染、AI 摘要、情绪/观点分析和证券归属。

## 5. Google Alerts 与雪球插件接入约定

第一阶段以免费 Google Alerts 作为外部文章发现服务，不自建搜索引擎，不采购舆情 API，也不依赖 RSSHub。用户在 Google Alerts 中为每位作者单独创建提醒，默认搜索条件由雪球插件按数字账号 ID 生成：`site:xueqiu.com/{账号ID}`。用户在 Google Alerts 中选择 RSS 投递后，将生成的 Feed URL 保存到对应作者配置。

- CRS 不自动登录 Google，不使用 Playwright，也不保存 Google 登录凭据。
- Google Alerts RSS URL 含唯一订阅标识，按凭据处理；保存在 XueqiuWatch 的 SQLCipher 加密库中，不记录到日志。
- Google Alerts 只是搜索索引发现，不是雪球官方全量动态接口；收录范围和更新时间不保证完整。
- CRS.Feed 只负责通用 Feed 抓取与解析。雪球 Jobs 负责从 Google `/url` 跳转链接中安全提取原文 URL，并再次验证目标主机为雪球、路径作者 ID 与当前订阅作者一致。
- 只接受规范的 `https://xueqiu.com/{authorId}/{postId}` 原帖链接。非雪球链接、主页、其他作者文章和不完整路径均忽略。
- 采集保存标题、摘要、正文片段、Feed 作者、发布时间、原帖 URL 和首次发现时间；保留 ETag / Last-Modified 条件请求。
- 文章正文只使用 Feed 返回的内容，不自动抓取雪球页面。后续是否使用 AI 分析需要单独核对内容使用范围与模型调用成本。

RSSHub 及第三方爬虫不属于第一阶段方案。若 Google Alerts 试运行无法达到所需收录率，再基于实测数据评估其他数据源，不提前增加复杂依赖。

旧版单一 Google Alerts Feed 状态不会直接迁移为某个作者的订阅，因为它缺少可靠的作者隔离。升级保留已经发现的历史线索；用户为每位作者粘贴独立 RSS URL 后，恢复自动采集。


## 6. 反方审查与验收

- 跨主机重定向必须拒绝；同主机重定向必须重新校验协议和 URL。
- 304 不得被解释成“作者没有发言”，只能解释为服务端确认 Feed 未变化。
- 429、超时、无权限、无效 XML 和正文过大不得覆盖历史线索。
- 一个作者来源失败不得阻断其他作者来源。
- 文章 URL 必须重新规范化并校验作者账号，不能信任 Feed 标题、作者字段或摘要。
- 采集成功不等于覆盖完整；RSS 源返回空列表也不能证明作者没有发言。
- 服务账户仍需配置出站网络 ACL；URL 字面量校验无法完全防止 DNS 重绑定。
