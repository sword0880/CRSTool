# CRS.Feed

CRS.Feed 是 CRS 的公共 Feed 采集基础设施，供 Worker 与业务插件复用。

## 职责

- 通过 HttpClient 抓取 RSS 2.0、RSS 1.0、Atom 和 JSON Feed。
- 统一标准化文章 ID、标题、链接、摘要、正文、作者、发布时间和分类。
- 支持 ETag / Last-Modified 条件请求。
- 手动处理同主机重定向，拒绝跨主机跳转。
- 对超时、响应大小、文章数量、重定向次数和限流设置边界。
- 使用 CRS.Common.Net.BoundedContentReader 进行有界读取。

## 不负责

- Quartz 调度、后台任务生命周期和并发策略。
- 数据库、文章去重策略及业务分类。
- 雪球、Google News 或任何特定媒体的查询规则。
- AI 摘要、观点判断和投资建议。

## 使用示例

~~~csharp
using CRS.Feed;

using var client = new FeedClient();
var validators = new FeedValidators(etag, lastModified);
var result = await client.FetchAsync(new Uri(feedUrl), validators, cancellationToken);

if (result.Status == FeedFetchStatus.Updated && result.Feed is { } feed)
{
    foreach (var item in feed.Items)
    {
        // 文章字段均为外部输入；HTML 正文不能直接交给 WPF / WebView 渲染。
    }
}
~~~

## 安全边界

- 默认只接受 HTTPS，且拒绝 URL 凭据、片段、明显的本机/保留主机和私有 IP 字面量。
- 默认关闭 HttpClient 自动重定向，并拒绝跨主机重定向。
- XML 禁止 DTD 和外部实体；响应正文按字节上限读取。
- 这些限制不能替代 Worker 服务的出站网络 ACL。生产部署仍应限制服务账户访问内网管理网段、云元数据地址和不必要的端口。
- Feed 内容和链接均是不可信外部数据。展示 HTML 前由业务层执行白名单清理；不能把 RSS 内容视为授权证明。

## 依赖

仅依赖 CRS.Common 与 .NET BCL；不引用 Worker、Quartz、WPF、数据库或业务插件。
