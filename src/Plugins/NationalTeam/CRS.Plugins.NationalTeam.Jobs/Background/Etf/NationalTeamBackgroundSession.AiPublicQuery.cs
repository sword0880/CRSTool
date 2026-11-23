using System.Text;
using System.Text.Json;
using CRS.Common.Concurrency;
using CRS.Plugins.NationalTeamWatch.Application;

namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    private readonly BoundedWorkAdmission aiQueries = new(1, 0, TimeSpan.Zero);

    private static string? AiPublicOperation(string operation) => operation switch
    {
        "ai-public-overview" => "overview",
        "ai-public-coverage" => "coverage",
        _ => null
    };

    private async Task<string> ReadAiPublicPageAsync(EtfRequest request, CancellationToken token)
    {
        if (request.Offset != 0 || request.Snapshot is not null || request.Count is < 1 or > 64)
            throw new ArgumentException("AI 公开查询只接受有界单页。");
        if (request.Operation == "coverage" && (request.From is not { } from ||
            request.To is not { } to || to.DayNumber - from.DayNumber is < 0 or > 30))
            throw new ArgumentException("AI 覆盖区间超限。");

        // 桌面异步研究可能在提交 RPC 结束后继续占用名额，这里仍优先让它完成。
        if (researchQueries.HasWork) throw new WorkAdmissionException("DesktopResearchBusy");
        using var lease = await aiQueries.EnterAsync(token);
        using var workload = AiPublicReadScope.Enter();
        // 与桌面使用同一个业务读取，但整个结果在同一次请求完成，永远不占用桌面分页缓存。
        var (rows, metadata) = await ReadQueryRowsAsync(request, token);
        if (rows.Length > request.Count) throw new InvalidDataException("AI 公开查询结果超过行数上限。");
        var page = new EtfQueryPage("[" + string.Join(',', rows) + "]", Guid.NewGuid().ToString("N"), null, metadata)
        {
            TotalRows = rows.Length,
            SinglePage = true
        };
        var json = JsonSerializer.Serialize(page);
        if (Encoding.UTF8.GetByteCount(json) > 46000)
            throw new InvalidDataException("AI 公开查询结果超过字节上限。");
        return json;
    }
}
