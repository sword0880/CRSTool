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
    [NationalTeamQuery("nav-study",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryNavStudy(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is null||request.To is null)throw new ArgumentException("NAV份额研究必须指定日期区间。");
        var navStudy=await workspace!.Nav.NavStudyAsync(Key(request),request.From.Value,request.To.Value,token);
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(navStudy,token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("nav-points",NationalTeamQueryKind.ResearchRead)]
    [NationalTeamQuery("nav-revisions",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryNavPoints(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        // NAV仍按显式日期有界读取，避免多年历史分页耗尽快照期限后返回半份结果。
        if(request.From is null||request.To is null||request.To<request.From||request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new InvalidDataException("NAV查询区间必须不超过400个自然日。");
        rows=request.Operation=="nav-points"?(await workspace!.Nav.NavPointsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray()
            :(await workspace!.Nav.NavRevisionsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamJob("nav-sync","官方NAV原件采集（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=16)]
    private async Task<BackgroundJobResult> ExecuteNavSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        return await SyncNavAsync(request,token,commandId);
    }

    [NationalTeamJob("nav-review","NAV冲突修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=17)]
    private async Task<BackgroundJobResult> ExecuteNavReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="nav-review"))throw new InvalidDataException("任务与业务参数不匹配。");
        if(request.Market!="SZSE"||request.Ticker!="159915")return new("Rejected","nav=UnsupportedSecurity");
        try { await workspace!.Nav.ReviewNavAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success("nav=RevisionReviewed"); }
        catch(InvalidDataException) { return new("Rejected","nav=ReviewInvalid"); }
    }
}
