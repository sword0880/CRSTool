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
    [NationalTeamQuery("dashboard",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryDashboard(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
        var dashboard=await workspace!.Etf.DashboardAsync(request.Market is null?null:Key(request),token)
            ??throw new ArgumentException("存储提供器不支持整页读取。");
        metadata=JsonSerializer.Serialize(workspace.Etf.NetworkStatus);
        rows=DashboardRows(dashboard,x=>SerializeRow(x));
        return new(rows,metadata);
    }

    [NationalTeamQuery("dashboard-profile",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryDashboardProfile(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
        var profile=await workspace!.Etf.ProfileDashboardAsync(request.Market is null?null:Key(request),token);
        var serializationStarted=System.Diagnostics.Stopwatch.GetTimestamp();var serialized=DashboardRows(profile.Dashboard,x=>SerializeRow(x));
        if(serialized.Length>10000||serialized.Sum(x=>x.Length)>2*1024*1024)throw new ArgumentException("完整查询结果超限，诊断不能绕过正常边界。");
        var serializationMs=System.Diagnostics.Stopwatch.GetElapsedTime(serializationStarted).TotalMilliseconds;
        // 使用同一金融读取和逐行序列化路径，只返回有限诊断；不扩大超时、不截断或跳过任何原件。
        rows=[JsonSerializer.Serialize(new{profile.Stages,RowSerializationMs=serializationMs,FinancialRows=serialized.Length,LogicalCharacters=serialized.Sum(x=>x.Length),
            DashboardSha256=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile.Dashboard))),
            Cache="PerQueryOnly",PhysicalColdState="NotControlled"})];
        return new(rows,metadata);
    }

    [NationalTeamQuery("overview",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryOverview(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Etf.OverviewAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("configuration",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryConfiguration(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Etf.ConfigurationAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("evidence",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEvidence(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        // 按证券过滤必须发生在快照分页和容量校验之前。
        if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
        rows=(await workspace!.Etf.EvidenceAsync(request.Market is null?null:Key(request),request.From,request.To,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("history",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryHistory(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
        rows=(await workspace!.Etf.HistoryAsync(request.Market is null?null:Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("points",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryPoints(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Etf.PointsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("collection-attempts",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryCollectionAttempts(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is null||request.To is null||request.To<request.From||request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new InvalidDataException("采集查询区间必须不超过400个自然日。");
        rows=(await workspace!.Etf.CollectionAttemptsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("acceptance-profile",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAcceptanceProfile(EtfRequest request,CancellationToken token)
    {
        string[] rows;string? metadata=null;
        if(request.To is null||(request.Market is null)!=(request.Ticker is null))throw new InvalidDataException("验收诊断需要截至日期及完整ETF标识。");
        var acceptanceProfile=await workspace!.Etf.ProfileAcceptanceAsync(request.Market is null?null:Key(request),request.To.Value,token);
        rows=[JsonSerializer.Serialize(acceptanceProfile)];
        return new(rows,metadata);
    }

    [NationalTeamQuery("acceptance",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAcceptance(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.To is null)throw new InvalidDataException("验收需要截至日期。");
        rows=(request.Market is null&&request.Ticker is null?await workspace!.Etf.AcceptanceAsync(request.To.Value,token)
            :await workspace!.Etf.AcceptanceAsync(Key(request),request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("coverage",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryCoverage(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is null || request.To is null || request.To<request.From || request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new ArgumentException("覆盖区间超限。");
        var coverage=await workspace!.Etf.CoverageAsync(Key(request),request.From.Value,request.To.Value,token);
        metadata=JsonSerializer.Serialize(coverage with{Days=[]});rows=coverage.Days.Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamJob("official-calendar-sync","官方交易日历核验","0 5 23 * * ?","China Standard Time",true,true,Group="collection",MaximumConcurrency=2,Order=3)]
    private async Task<BackgroundJobResult> ExecuteOfficialCalendarSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="sync"))throw new InvalidDataException("任务与业务参数不匹配。");
        var calendarYear=CRS.Timing.BeijingClock.Now.Year;
        var calendarIssues=0;
        foreach(var market in new[]{"SSE","SZSE"})
        foreach(var year in new[]{calendarYear-1,calendarYear})
        {
            try
            {
                var uri=ReviewedExchangeCalendar.Source(market,year);var response=await http!.GetAsync(uri,token);
                if(response.StatusCode!=200){calendarIssues++;continue;}
                await workspace!.Etf.RegisterCalendarAsync(market,year,response.Content,uri,token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException){calendarIssues++;}
        }
        return calendarIssues==0?BackgroundJobResult.Success("calendar=ReviewedAnnualSchedule"):new("RetryDeferred",$"calendarSourceIssues={calendarIssues}",TimeSpan.FromMinutes(30));
    }

    [NationalTeamJob("official-share-sync","官方ETF份额同步","0 15 23 * * ?","China Standard Time",true,true,Group="collection",MaximumConcurrency=2,Order=1)]
    private async Task<BackgroundJobResult> ExecuteOfficialShareSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="sync"))throw new InvalidDataException("任务与业务参数不匹配。");
        if(!(await workspace!.Etf.ConfigurationAsync(token)).Any(x=>x.Active))return BackgroundJobResult.Success("monitoring=NoActiveEtfs");
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        var report=request.From is null && request.To is null?await workspace!.Etf.SyncRecentAsync(token):
            await workspace!.Etf.SyncAsync(request.From??today.AddDays(-7),request.To??today,token);
        await workspace!.Alerts.EvaluateAlertsAsync(token);
        return new(report.SourceIssues>0 || report.HasMore?"RetryDeferred":report.ReceivedRows==0?"NotPublished":"Succeeded",
            $"days={report.QueriedDays};sourceIssues={report.SourceIssues};rows={report.ReceivedRows}",report.HasMore?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(30));
    }

    [NationalTeamJob("review","ETF修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=23)]
    private async Task<BackgroundJobResult> ExecuteReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="review"))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Etf.ReviewAsync(request.Revision??"",request.Accept,request.Reviewer??"",request.Comparable,token,commandId);
        await workspace!.Alerts.EvaluateAlertsAsync(token);
        return BackgroundJobResult.Success();
    }

    [NationalTeamJob("watchlist","ETF验证池变更（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=24)]
    private async Task<BackgroundJobResult> ExecuteWatchlist(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="watchlist"))throw new InvalidDataException("任务与业务参数不匹配。");
        if(request.ConfigurationRevision is { } revision)
        {
            // 用户输入/并发冲突不等于采集故障，不能让下一次正常配置请求进入故障退避。
            try{await workspace!.Etf.SaveConfigurationAsync(Key(request),request.Name??"",request.Index??"",request.Active,request.Reason??"",revision,token,commandId);}
            catch(InvalidDataException){return new("Rejected","configuration=InvalidOrStale");}
        }
        else await workspace!.Etf.MembershipAsync(Key(request),request.Name??"",request.Index??"",request.Active,request.Reason??"",token,commandId);
        
        return BackgroundJobResult.Success();
    }
}
