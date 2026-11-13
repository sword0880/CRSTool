using System.Net;
using System.Net.Http.Headers;
using CRS.AI.Client.Mcp;
using CRS.AI.Models.Tools;
using CRS.AI.Contracts;
using ModelContextProtocol.Client;

namespace CRS.Tests.AiQuery.Acceptance;

/// <summary>在临时回环端口上验证 MCP 的 HTTP 鉴权、协议握手与 Gateway 查询。</summary>
internal static class McpClientAcceptance
{
    public static async Task RunAsync(AiToolDispatch dispatch, CancellationToken cancellation)
    {
        // 凭证仅用于本次隔离验收，由系统分配可用端口。
        const string credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        await using var app = McpHttpHost.Create(0, credential, dispatch);
        await app.StartAsync(cancellation);
        try
        {
            var endpoint = new Uri(new Uri(app.Urls.Single()), "/mcp");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var anonymous = await http.PostAsync(endpoint, new StringContent("{}"), cancellation);
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "匿名请求拒绝");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "B" + credential[1..]);
            using var wrongToken = await http.PostAsync(endpoint, new StringContent("{}"), cancellation);
            Check(wrongToken.StatusCode == HttpStatusCode.Unauthorized, "错误凭证拒绝");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var origin = new HttpRequestMessage(HttpMethod.Post, endpoint);
            origin.Headers.Add("Origin", "https://untrusted.example");
            using var crossSite = await http.SendAsync(origin, cancellation);
            Check(crossSite.StatusCode == HttpStatusCode.Forbidden, "拒绝非本机 Origin");
            using var host = new HttpRequestMessage(HttpMethod.Post, endpoint);
            host.Headers.Host = $"untrusted.example:{endpoint.Port}";
            using var rebound = await http.SendAsync(host, cancellation);
            Check(rebound.StatusCode == HttpStatusCode.Forbidden, "拒绝非本机 Host");
            using var oversized = await http.PostAsync(endpoint,
                new ByteArrayContent(new byte[LoopbackMcpSecurity.MaxRequestBytes + 1]), cancellation);
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "拒绝超限请求");

            // 使用官方客户端走实际 Streamable HTTP 握手、工具列表及调用。
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp
            }, http), cancellationToken: cancellation);
            var tools = await client.ListToolsAsync(cancellationToken: cancellation);
            Check(tools.Select(tool => tool.Name).Order().SequenceEqual(AiCatalog.Tools.Select(tool => tool.Name).Order()) &&
                  tools.All(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true), "握手后只公布两项只读工具");
            var result = await client.CallToolAsync(AiCatalog.Overview, cancellationToken: cancellation);
            Check(result.IsError != true && result.StructuredContent is { } output &&
                  output.GetProperty("status").GetString() == "Partial" &&
                  output.GetProperty("dataVersion").GetString() is { Length: > 0 }, "工具通过 Windows 管道并保留数据版本");
            var coverage = await client.CallToolAsync(AiCatalog.Coverage, new Dictionary<string, object?>
            {
                ["market"] = "SSE", ["ticker"] = "510300", ["from"] = "2026-11-02", ["to"] = "2026-11-02"
            }, cancellationToken: cancellation);
            Check(coverage.IsError != true && coverage.StructuredContent is { } range &&
                  range.GetProperty("code").GetString() == "RegisteredCalendarOnly" &&
                  range.GetProperty("data").GetProperty("AcceptedDays").GetInt32() == 1,
                "覆盖率工具保留参数和登记日结果");
            var unknown = await client.CallToolAsync("private_tool", cancellationToken: cancellation);
            Check(unknown.IsError == true && unknown.StructuredContent?.GetProperty("code").GetString() == "UnknownTool",
                "目录以外的工具无法进入 Gateway");
        }
        finally { await app.StopAsync(cancellation); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS MCP " + label);
    }
}
