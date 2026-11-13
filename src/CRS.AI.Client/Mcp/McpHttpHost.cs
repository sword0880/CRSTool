using System.Net;
using System.Text.Json;
using CRS.AI.Adapters;
using CRS.AI.Models.Attachments;
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

/// <summary>配置本机 MCP Streamable HTTP 端点；只读研究工具与附件会话工具分开授权处理。</summary>
internal static class McpHttpHost
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>创建尚未启动的回环监听宿主；附件解析和会话管理由共享模型模块提供。</summary>
    public static WebApplication Create(
        int port,
        string token,
        AiToolDispatch dispatch,
        AiAttachmentConversationStore? attachmentSessions = null,
        IAiAttachmentFileDownloader? attachmentDownloader = null)
    {
        var ownsSessions = attachmentSessions is null;
        var ownsDownloader = attachmentDownloader is null;
        var sessions = attachmentSessions ?? new AiAttachmentConversationStore();
        var downloader = attachmentDownloader ?? new OpenAiAttachmentFileDownloader();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(McpHttpHost).Assembly.GetName().Name,
            EnvironmentName = "Production"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            // MCP 文件只传临时下载地址，原始文件由受控下载器按流读取；JSON 请求正文仍限制为 64 KiB。
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
        tools.AddRange(McpAttachmentToolAdapter.DescribeTools());

        IHttpContextAccessor? httpContextAccessor = null;
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "CRS", Version = "0.2.0" };
            })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }))
            .WithCallToolHandler(async (context, cancellation) =>
            {
                var request = context.Params;
                var arguments = request?.Arguments is null
                    ? AiToolDispatch.EmptyArguments
                    : JsonSerializer.SerializeToElement(request.Arguments);

                object result;
                bool isError;
                var isAttachmentTool = McpAttachmentToolAdapter.IsAttachmentTool(request?.Name);
                if (isAttachmentTool)
                {
                    var owner = httpContextAccessor?.HttpContext?.Items[LoopbackMcpSecurity.OwnerItemKey] as string;
                    var attachmentResult = owner is null
                        ? new AiAttachmentToolResult("Rejected", "MissingCallerIdentity", null, null, [], null,
                            "无法确认当前 MCP 调用者身份。")
                        : await McpAttachmentToolAdapter.ExecuteAsync(request?.Name, arguments, owner,
                            sessions, downloader, cancellation).ConfigureAwait(false);
                    result = attachmentResult;
                    isError = attachmentResult.Status == "Rejected";
                }
                else
                {
                    var readResult = await dispatch.ExecuteAsync(request?.Name, arguments, cancellation).ConfigureAwait(false);
                    result = readResult;
                    isError = readResult.Status == "Rejected";
                }

                var output = JsonSerializer.SerializeToElement(result, WebJson);
                // 附件正文只放入 structuredContent 一次，避免 MCP 文本块重复消耗模型上下文。
                var contentText = isAttachmentTool && result is AiAttachmentToolResult attachment
                    ? $"CRS 附件会话状态：{attachment.Status}；当前附件数量：{attachment.Attachments.Count}。"
                    : output.GetRawText();
                return new CallToolResult
                {
                    IsError = isError,
                    Content = [new TextContentBlock { Text = contentText }],
                    StructuredContent = output
                };
            });

        var app = builder.Build();
        httpContextAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        var security = new LoopbackMcpSecurity(token);
        app.Use(security.AuthorizeAsync);
        app.MapGet("/health", () => Results.Ok(new { status = "listening" }));
        app.MapMcp("/mcp");
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            if (ownsSessions) sessions.Dispose();
            if (ownsDownloader && downloader is IDisposable disposable) disposable.Dispose();
        });
        return app;
    }
}
