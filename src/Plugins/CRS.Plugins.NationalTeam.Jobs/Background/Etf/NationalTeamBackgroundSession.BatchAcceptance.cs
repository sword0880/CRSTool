// 后台查询能力及ETF批量验收入口，冻结配置池并返回版本化元数据，客户端据此检查完整性。
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamQuery("query-capabilities",NationalTeamQueryKind.ShortRead)]
    private Task<NationalTeamQueryResult> QueryCapabilities(EtfRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new NationalTeamQueryResult([JsonSerializer.Serialize(new EtfQueryCapabilities(EtfQueryCapabilities.CurrentVersion,[EtfQueryCapabilities.BatchAcceptance,EtfQueryCapabilities.AsyncResearch,EtfQueryCapabilities.PublicationDateHoldings,EtfDocumentCollectionProtocol.Capability],256)
            {ShortReadOperations=DescribeQueryRouting().Routes.Where(x=>x.Workload==CRS.Worker.Abstractions.BackgroundQueryWorkload.ShortRead).Select(x=>x.Operation).ToArray()})]));
    }
    [NationalTeamQuery("acceptance-batch")]
    private async Task<NationalTeamQueryResult> QueryAcceptanceBatch(EtfRequest request,CancellationToken token)
    {
        if(request.To is not {} through||request.Market is not null||request.Ticker is not null||request.From is not null||request.KnowledgeCutoff is not null)
            throw new InvalidDataException("批量验收只接受全配置池和已结束统计日。");
        var rows=await workspace!.Etf.AcceptanceAsync(through,token);
        var metadata=new EtfAcceptanceBatchMetadata(1,through,rows.Count,EtfAcceptanceBatchMetadata.Fingerprint(rows));
        var serialization=new NationalTeamQuerySerialization(token);
        return new(rows.Select(serialization.Row).ToArray(),JsonSerializer.Serialize(metadata));
    }
}
