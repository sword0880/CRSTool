using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>本功能声明自己的公开查询、持久任务及执行组，入口仅负责授权协议和生命周期。</summary>
public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamQuery("industry-pe-report",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryIndustryPeReport(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is not {} industryFrom||request.To is not {} industryThrough)throw new InvalidDataException("行业研究需要完整日期区间。");
        var industry=await workspace!.Industry.IndustryPeReportAsync(industryFrom,industryThrough,token);
        metadata=JsonSerializer.Serialize(industry with{Rows=[]});rows=industry.Rows.Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("industry-pe-revisions",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryIndustryPeRevisions(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is not {} revisionFrom||request.To is not {} revisionThrough)throw new InvalidDataException("行业修订需要完整日期区间。");
        rows=(await workspace!.Industry.IndustryPeRevisionsAsync(revisionFrom,revisionThrough,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamJob("industry-pe-sync","国证行业公布PE采集（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=18)]
    private async Task<BackgroundJobResult> ExecuteIndustryPeSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        return await SyncIndustryPeAsync(request,token,commandId);
    }

    [NationalTeamJob("industry-pe-review","行业公布PE修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=19)]
    private async Task<BackgroundJobResult> ExecuteIndustryPeReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="industry-pe-review"))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Industry.ReviewIndustryPeAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success("industry=RevisionReviewed");
    }
}
