using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace CRS.AI.Client.Mcp;

/// <summary>在 MCP 处理请求正文之前校验本机来源、访问凭证和已声明的正文大小。</summary>
internal sealed class LoopbackMcpSecurity(string token)
{
    internal const int MaxRequestBytes = 64 * 1024;
    internal const string OwnerItemKey = "CRS.MCP.CallerIdentity";
    private readonly byte[] tokenHash = HashToken(token);

    /// <summary>检查配置值是否符合 32 字节随机凭证对应的无填充 Base64URL 文本格式。</summary>
    public static bool IsTokenFormatValid(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public Task AuthorizeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var port = context.Connection.LocalPort;
        var host = request.Host.Host;

        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            request.Host.Port != port ||
            (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1") ||
            (request.Headers.TryGetValue("Origin", out var origins) &&
             (origins.Count != 1 ||
              (!string.Equals(origins[0], $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(origins[0], $"http://127.0.0.1:{port}", StringComparison.Ordinal)))))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            !IsTokenFormatValid(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(tokenHash, HashToken(header.Parameter!)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }

        // 仅向当前请求上下文注入凭证摘要，不把原始访问令牌交给 MCP 工具或附件仓储。
        context.Items[OwnerItemKey] = Convert.ToHexString(tokenHash);
        context.Response.Headers.CacheControl = "no-store";
        if (request.ContentLength > MaxRequestBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return Task.CompletedTask;
        }
        return next(context);
    }

    private static byte[] HashToken(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));
}
