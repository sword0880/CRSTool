using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Common.Net;

namespace CRS.AI.Models.Providers;

/// <summary>发送有报文上限的模型请求，统一屏蔽上游错误正文并尽力清理请求缓冲。</summary>
internal static class AiModelHttp
{
    private const int MaxPayloadBytes = 256 * 1024;
    private const int MaxAttachmentPayloadBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonDocument> PostAsync(HttpClient http, Uri endpoint, string apiKey,
        object payload, CancellationToken token, bool anthropic = false, bool includesScreenshot = false,
        bool includesAttachments = false)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        try
        {
            var requestLimit = includesScreenshot ? 2 * 1024 * 1024 :
                includesAttachments ? MaxAttachmentPayloadBytes : MaxPayloadBytes;
            if (body.Length > requestLimit) throw new AiModelException("TurnBudgetExceeded");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
            if (anthropic)
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            // 每次调用只尝试一次，避免请求结果未知时重复计费。
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiModelException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "ApiUnauthorized",
                    HttpStatusCode.Forbidden => "ApiForbidden",
                    HttpStatusCode.PaymentRequired => "ApiPaymentRequired",
                    HttpStatusCode.NotFound => "ApiModelUnavailable",
                    HttpStatusCode.TooManyRequests => "ApiRateLimited",
                    HttpStatusCode.BadRequest => "ApiRequestRejected",
                    _ => "ApiUnavailable"
                });
            var data = await BoundedContentReader.ReadAsync(response.Content, MaxPayloadBytes, token).ConfigureAwait(false);
            return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        }
        finally
        {
            // 请求发送完成或失败后尽力清除含附件正文的 UTF-8 报文缓冲。
            CryptographicOperations.ZeroMemory(body);
        }
    }
}
