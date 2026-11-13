using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CRS.AI.Client.Mcp;
using CRS.AI.Models.Attachments;
using CRS.AI.Models.Tools;
using CRS.AI.Contracts;
using ModelContextProtocol.Client;

namespace CRS.Tests.AiQuery.Acceptance;

/// <summary>在临时回环端口上验证 MCP 鉴权、只读工具及附件会话协议。</summary>
internal static class McpClientAcceptance
{
    public static async Task RunAsync(AiToolDispatch dispatch, CancellationToken cancellation)
    {
        const string credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        using var attachmentSessions = new AiAttachmentConversationStore(startCleanupTimer: false);
        var downloader = new FakeAttachmentDownloader(Encoding.UTF8.GetBytes("CRS 附件样本文本\n第二行内容。"));
        await using var app = McpHttpHost.Create(0, credential, dispatch, attachmentSessions, downloader);
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

            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp
            }, http), cancellationToken: cancellation);
            var tools = await client.ListToolsAsync(cancellationToken: cancellation);
            var readTools = tools.Where(tool => tool.Name is not ("crs_chat_start" or "crs_chat_message" or
                "crs_chat_remove_attachment" or "crs_chat_end")).ToArray();
            Check(readTools.Select(tool => tool.Name).Order().SequenceEqual(AiCatalog.Tools.Select(tool => tool.Name).Order()) &&
                  readTools.All(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true), "保留原只读工具目录和标记");
            Check(tools.Select(tool => tool.Name).Contains("crs_chat_message") &&
                  tools.Select(tool => tool.Name).Contains("crs_chat_end"), "公布附件消息与会话结束工具");
            var messageTool = tools.Single(tool => tool.Name == "crs_chat_message");
            Check(messageTool.ProtocolTool.Meta?["openai/fileParams"] is JsonArray fileParams &&
                  fileParams.Count == 1 && fileParams[0]?.GetValue<string>() == "files",
                "ChatGPT 工具元数据声明官方文件参数");
            var fileProperties = messageTool.ProtocolTool.InputSchema.GetProperty("$defs")
                .GetProperty("OpenAIFile").GetProperty("properties");
            Check(new[] { "download_url", "file_id", "mime_type", "file_name" }
                    .All(name => fileProperties.TryGetProperty(name, out _)) &&
                  messageTool.ProtocolTool.OutputSchema is not null,
                "文件参数声明四个官方字段并定义结构化输出");

            var result = await client.CallToolAsync(AiCatalog.Overview, cancellationToken: cancellation);
            Check(result.IsError != true && result.StructuredContent is { } output &&
                  output.GetProperty("status").GetString() == "Partial" &&
                  output.GetProperty("dataVersion").GetString() is { Length: > 0 }, "只读工具通过 Windows 管道并保留数据版本");
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
                "目录以外的只读工具无法进入 Gateway");

            var started = await client.CallToolAsync("crs_chat_start", cancellationToken: cancellation);
            Check(started.IsError != true && started.StructuredContent is { } startData &&
                  startData.GetProperty("status").GetString() == "Success", "MCP 附件会话创建");
            var sessionId = started.StructuredContent!.Value.GetProperty("sessionId").GetString()!;
            var upload = await client.CallToolAsync("crs_chat_message", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["prompt"] = "请阅读这个附件",
                ["files"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["download_url"] = "https://files.oaiusercontent.com/test-download",
                        ["file_id"] = "file_test_123",
                        ["mime_type"] = "text/plain",
                        ["file_name"] = "note.txt"
                    }
                }
            }, cancellationToken: cancellation);
            var uploaded = upload.StructuredContent ?? throw new InvalidOperationException("MCP 附件上传未返回结构化结果。");
            Check(upload.IsError != true && uploaded.GetProperty("attachments").GetArrayLength() == 1 &&
                  uploaded.GetProperty("attachmentContext").GetString()!.Contains("CRS 附件样本文本", StringComparison.Ordinal),
                "问题与 ChatGPT 文件同一工具消息提交并解析");

            var followUp = await client.CallToolAsync("crs_chat_message", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId, ["prompt"] = "继续解释第二行"
            }, cancellationToken: cancellation);
            Check(followUp.IsError != true && followUp.StructuredContent is { } continued &&
                  continued.GetProperty("attachmentContext").GetString()!.Contains("第二行内容", StringComparison.Ordinal),
                "同一 MCP 会话连续追问保留解析文本");

            var attachmentId = uploaded.GetProperty("attachments")[0].GetProperty("id").GetString()!;
            var removed = await client.CallToolAsync("crs_chat_remove_attachment", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId, ["attachmentId"] = attachmentId
            }, cancellationToken: cancellation);
            Check(removed.IsError != true, "MCP 会话逐个移除附件");
            var afterRemove = await client.CallToolAsync("crs_chat_message", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId, ["prompt"] = "现在还有附件吗"
            }, cancellationToken: cancellation);
            Check(afterRemove.IsError != true && afterRemove.StructuredContent is { } empty &&
                  empty.GetProperty("attachments").GetArrayLength() == 0,
                "移除附件后后续上下文不再包含原文");

            var ended = await client.CallToolAsync("crs_chat_end", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId
            }, cancellationToken: cancellation);
            var afterEnd = await client.CallToolAsync("crs_chat_message", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId, ["prompt"] = "继续"
            }, cancellationToken: cancellation);
            Check(ended.IsError != true && afterEnd.IsError == true, "结束会话后句柄立即失效");
        }
        finally { await app.StopAsync(cancellation); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS MCP " + label);
    }

    private sealed class FakeAttachmentDownloader(byte[] content) : IAiAttachmentFileDownloader
    {
        public Task<byte[]> DownloadAsync(AiAttachmentFileInput file, int maximumBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(content.ToArray());
        }
    }
}
