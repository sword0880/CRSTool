using System.Security.Cryptography;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>经受限网关依序取得固定四原件；全部核验后才交给单事务导入，不把半份来源当作NAV事实。</summary>
public sealed class OfficialNavSource(IPluginHttpGateway http, TimeProvider? timeProvider = null)
{
    public async Task<IReadOnlyList<CapturedNavDocument>> FetchAsync(CancellationToken token)
    {
        var clock = timeProvider ?? TimeProvider.System; var documents = new List<CapturedNavDocument>();
        foreach (var url in EfundsNavResearch.OfficialUrls)
        {
            token.ThrowIfCancellationRequested(); var response = await http.GetAsync(new(url), token).ConfigureAwait(false);
            if (response.StatusCode != 200) throw new HttpRequestException("NAV官方来源未返回完整原件。", null, (System.Net.HttpStatusCode)response.StatusCode);
            if (response.Content.Length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException("NAV官方原件大小无效。");
            documents.Add(new(url, response.Content, clock.GetUtcNow(), Convert.ToHexString(SHA256.HashData(response.Content))));
        }
        try
        {
            // 不执行下载脚本；字段或跨文件校验失败时，同一基金来源进入共享冷却。
            _ = EfundsNavResearch.Build(documents, clock.GetUtcNow()); return documents;
        }
        catch (InvalidDataException)
        {
            if (http is IOfficialSourceValidationFeedback feedback) feedback.RejectContract(new(EfundsNavResearch.ProductUrl));
            throw;
        }
    }
}
