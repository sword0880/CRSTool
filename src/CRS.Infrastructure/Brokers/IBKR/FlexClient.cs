using System.Net;
using System.Text.RegularExpressions;
using CRS.Domain;
using NLog;

namespace CRS.Infrastructure;

/// <summary>通过调用方提供的 HttpClient 建立可取消的 IBKR Flex 下载服务。</summary>
public sealed class FlexClient(HttpClient client)
{
    private const string BaseUrl = "https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService/";
    /// <summary>发送生成请求并有限轮询获取 XML；令牌仅驻留调用期间，不写入日志。</summary>
    public async Task<byte[]> DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, CancellationToken cancellation)
    {
        queryId = queryId.Trim(); token = token.Trim();
        if (!Regex.IsMatch(queryId, "^[0-9]+$") || token.Length == 0) throw new CrsException("请填写数字查询 ID 和服务令牌。");
        if (from > to) throw new CrsException("开始日期不能晚于结束日期。");
        var reply = XmlFields.ReadXml(await RequestAsync("SendRequest", new() { ["q"] = queryId, ["t"] = token, ["v"] = "3", ["fd"] = from.ToString("yyyyMMdd"), ["td"] = to.ToString("yyyyMMdd") }, cancellation));
        var root = reply.Root;
        if (root?.Name != "FlexStatementResponse" || root.Element("Status")?.Value != "Success") throw Error(root?.Element("ErrorCode")?.Value, "报告生成请求未被接受");
        var reference = root.Element("ReferenceCode")?.Value.Trim() ?? "";
        if (!Regex.IsMatch(reference, "^[A-Za-z0-9_-]{1,128}$")) throw new CrsException("IBKR 报告引用码无效。");
        // 忽略服务返回的 URL，始终向固定 HTTPS 域名发送令牌。
        await Task.Delay(1100, cancellation);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 8)), cancellation);
            var bytes = await RequestAsync("GetStatement", new() { ["q"] = reference, ["t"] = token, ["v"] = "3" }, cancellation);
            var document = XmlFields.ReadXml(bytes);
            if (document.Root?.Name == "FlexQueryResponse" && XmlFields.Optional(document.Root, "type", "AF") == "AF") return bytes;
            var code = document.Root?.Element("ErrorCode")?.Value;
            if (code is not ("1019" or "1021")) throw Error(code, "无法获取 Activity Flex XML");
        }
        throw new CrsException("报告尚未生成，请稍后重试。");
    }
    /// <summary>对固定官方端点限量读取响应，网络错误转换为不含令牌的说明。</summary>
    private async Task<byte[]> RequestAsync(string endpoint, Dictionary<string, string> parameters, CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetAsync(BaseUrl + endpoint + "?" + string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))), HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) throw new CrsException($"IBKR 返回 HTTP {(int)response.StatusCode}，请检查网络或访问限制。");
            using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var buffer = new MemoryStream(); var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellation)) > 0)
            {
                if (buffer.Length + count > XmlFields.MaxBytes) throw new CrsException("IBKR 报告超过 25 MB，请缩短查询期间。");
                buffer.Write(chunk, 0, count);
            }
            return buffer.ToArray();
        }
        catch (HttpRequestException) { LogManager.GetCurrentClassLogger().Warn("Flex 网络请求失败"); throw new CrsException("无法连接 IBKR，请检查代理、证书及网络设置。"); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new CrsException("IBKR 连接超时，请稍后重试。"); }
    }
    /// <summary>只展示经过验证的数字错误码，丢弃服务返回的任意描述与 URL。</summary>
    private static CrsException Error(string? code, string stage) => new($"IBKR {stage}{(code is not null && Regex.IsMatch(code, "^[0-9]{1,6}$") ? $"（错误码 {code}）" : "")}，请检查令牌及查询模板。");
}
