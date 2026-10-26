using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;

namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    /// <summary>仅手动采集固定证券；所有来源通过共用网关，不接受客户端URL、文件路径或回填时间。</summary>
    private async Task<BackgroundJobResult> SyncNavAsync(EtfRequest request, CancellationToken token, Guid? commandId)
    {
        if (request.Operation != "nav-sync" || request.Market != "SZSE" || request.Ticker != "159915" || request.From is not null || request.To is not null
            || request.Revision is not null || request.Reviewer is not null || request.Accept) return new("Rejected", "nav=UnsupportedSecurityOrArguments");
        try
        {
            var outcome = commandId is { } id ? await workspace!.NavImportReceiptAsync(id, token) : null;
            if (outcome is null)
            {
                var documents = await new OfficialNavSource(http!).FetchAsync(token);
                outcome = await workspace!.ImportNavDocumentsAsync(documents, token, commandId);
            }
            // Pending计数必须重读全部尚待审核的版本；重复采集不等于已有冲突消失。
            var today = DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
            var revisions = await workspace!.NavRevisionsAsync(new("SZSE", "159915"), new(2011, 9, 20), today, token);
            if (revisions.Any(x => (x.State is "Accepted" or "NeedsReview") && !x.EvidenceHealthy)) return new("Rejected", "nav=EvidenceUnhealthy");
            var pending = revisions.Count(x => x.State == "NeedsReview");
            return new(pending == 0 ? "Succeeded" : "NeedsReview", $"navAdded={outcome.Added};navDuplicates={outcome.Duplicates};navPending={pending};capture={outcome.CaptureId}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidOperationException or SourceBudgetUnavailableException or InvalidDataException)
        {
            // 来源失败保留宿主持久命令和冷却，不把无新值或旧缓存解释为本次采集成功。
            return new("RetryDeferred", "nav=SourceOrContractUnavailable", TimeSpan.FromMinutes(30));
        }
    }
}
