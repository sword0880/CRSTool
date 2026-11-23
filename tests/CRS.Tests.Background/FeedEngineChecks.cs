using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CRS.Feed;

internal static class FeedEngineChecks
{
    internal static async Task RunAsync()
    {
        var baseUri = new Uri("https://feeds.example.com/updates");
        var rss = FeedParser.Parse(Encoding.UTF8.GetBytes("""
            <rss version="2.0"><channel><title>测试源</title><link>https://example.com</link>
              <item><guid>post-1</guid><title>第一篇</title><link>/posts/1</link><description>摘要</description></item>
            </channel></rss>
            """), baseUri);
        Check(rss.Title == "测试源" && rss.Items is [{ Id: "post-1", Title: "第一篇" }] &&
            rss.Items[0].Url?.AbsoluteUri == "https://feeds.example.com/posts/1", "RSS 2.0 解析与相对链接解析");

        var atom = FeedParser.Parse(Encoding.UTF8.GetBytes("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Atom 源</title>
              <entry><id>atom-1</id><title>Atom 文章</title><link rel="alternate" href="https://example.com/a/1"/>
                <author><name>作者甲</name></author><updated>2026-11-23T01:00:00Z</updated></entry>
            </feed>
            """), baseUri);
        Check(atom.Items is [{ Id: "atom-1", Author: "作者甲" }], "Atom 解析");

        var json = FeedParser.Parse(Encoding.UTF8.GetBytes("""
            {"version":"https://jsonfeed.org/version/1.1","title":"JSON 源","items":[
              {"id":"json-1","url":"https://example.com/j/1","title":"JSON 文章","content_text":"正文",
               "authors":[{"name":"作者乙"}],"tags":["研究"]}]}
            """), baseUri);
        Check(json.Items is [{ Id: "json-1", Author: "作者乙" }] && json.Items[0].Categories.Contains("研究"),
            "JSON Feed 解析");

        var dtdRejected = false;
        try
        {
            FeedParser.Parse(Encoding.UTF8.GetBytes("""
                <!DOCTYPE rss [<!ENTITY x SYSTEM "file:///unused">]>
                <rss version="2.0"><channel><title>&x;</title></channel></rss>
                """), baseUri);
        }
        catch (InvalidDataException) { dtdRejected = true; }
        Check(dtdRejected, "XML DTD 与外部实体被拒绝");

        var calls = 0;
        using (var client = new FeedClient(new DelegateHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<rss version='2.0'><channel><item><title>x</title></item></channel></rss>")
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(response);
            }
            Check(request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"v1\""), "条件请求发送 ETag");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        })))
        {
            var first = await client.FetchAsync(baseUri);
            var second = await client.FetchAsync(baseUri, new FeedValidators(first.ETag, first.LastModified));
            Check(first.Status == FeedFetchStatus.Updated && second.Status == FeedFetchStatus.NotModified,
                "ETag 与 HTTP 304 增量抓取");
        }

        using (var client = new FeedClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example.com/feed") }
            }))))
        {
            var result = await client.FetchAsync(baseUri);
            Check(result.Status == FeedFetchStatus.RedirectRejected, "跨主机重定向被拒绝");
        }

        using (var client = new FeedClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 2048))
            })), new FeedClientOptions { MaximumResponseBytes = 1024 }))
        {
            var result = await client.FetchAsync(baseUri);
            Check(result.Status == FeedFetchStatus.TooLarge, "响应正文大小上限");
        }

        var privateAddressRejected = false;
        try { FeedAddress.Normalize("https://127.0.0.1/rss"); }
        catch (ArgumentException) { privateAddressRejected = true; }
        Check(privateAddressRejected, "拒绝私有 IP 字面量");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS CRS.Feed：" + message);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
