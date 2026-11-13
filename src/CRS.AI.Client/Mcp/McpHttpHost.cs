using System.Net;
using System.Text.Json;
using CRS.AI.Adapters;
using CRS.AI.Models.Tools;
using CRS.AI.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace CRS.AI.Client.Mcp;

/// <summary>配置本机 MCP Streamable HTTP 端点，并将工具调用转给只读分发器。</summary>
internal static class McpHttpHost
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>创建尚未启动的回环监听宿主，供命令行与隔离验收共用。</summary>
    public static WebApplication Create(int port, string token, AiToolDispatch dispatch)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(McpHttpHost).Assembly.GetName().Name,
            EnvironmentName = "Production"
        });
        // 监听地址由代码限定为回环地址，清除外部配置源并关闭框架日志提供程序。
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            options.Limits.MaxRequestBodySize = LoopbackMcpSecurity.MaxRequestBytes;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });

        var tools = McpReadToolAdapter.DescribeTools(AiCatalog.Tools).Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true, DestructiveHint = false, OpenWorldHint = false
            }
        }).ToList();
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "CRS", Version = "0.1.0" };
            })
            // 只读工具不需要保存会话，也不需要对客户端发送通知。
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }))
            .WithCallToolHandler(async (context, cancellation) =>
            {
                var request = context.Params;
                var arguments = request?.Arguments is null
                    ? AiToolDispatch.EmptyArguments
                    : JsonSerializer.SerializeToElement(request.Arguments);
                var result = await dispatch.ExecuteAsync(request?.Name, arguments, cancellation);
                // 文本结果和结构化结果复用同一份 JSON，避免客户端看到不同内容。
                var output = JsonSerializer.SerializeToElement(result, WebJson);
                return new CallToolResult
                {
                    IsError = result.Status == "Rejected",
                    Content = [new TextContentBlock { Text = output.GetRawText() }],
                    StructuredContent = output
                };
            });

        var app = builder.Build();
        var security = new LoopbackMcpSecurity(token);
        app.Use(security.AuthorizeAsync);
        // 健康端点只确认 HTTP 监听状态，实际工具调用才检查 Gateway。
        app.MapGet("/health", () => Results.Ok(new { status = "listening" }));
        app.MapMcp("/mcp");
        return app;
    }
}
