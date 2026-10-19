using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

/// <summary>客户端只访问授权管道，不打开市场库或向服务提交桌面文件路径。</summary>
public sealed class WorkerEtfWorkspace(BackgroundPipeClient? connection=null):IEtfWorkspace
{
    private const string Plugin="crs.national-team";
    private readonly BackgroundPipeClient client=connection??new();
    private BackgroundRequest? uncertain;
    public string NetworkStatus{get;private set;}="公开采集由独立后台服务执行，关闭桌面后继续运行。";
    public bool CanManage=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>限流后有界等待令牌；连接失败不以桌面空库代替。</summary>
    private async Task<BackgroundResponse> SendAsync(BackgroundRequest request,CancellationToken token)
    {
        while(true)
        {
            var response=await client.SendAsync(request,token);
            if(response.Code=="RateLimited"){await Task.Delay(500,token);continue;}
            if(response.Code=="Forbidden")throw new InvalidDataException("操作需要管理员权限；查询公开结果无需管理员权限。");
            return response;
        }
    }
    private async Task<T> QueryAsync<T>(EtfRequest query,CancellationToken token)
    {
        var reply=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin",Plugin,Payload:JsonSerializer.Serialize(query)),token);
        if(reply.Code!="Ok" || reply.Payload is null)throw new InvalidDataException("后台查询失败："+reply.Code+"；请确认服务和ETF后台插件已安装。");
        return JsonSerializer.Deserialize<T>(reply.Payload)??throw new InvalidDataException("后台返回空数据。");
    }
    /// <summary>同一次读取沿用快照，任何分页失败都不把半份结果交给页面。</summary>
    private async Task<(List<T> Rows,string? Metadata)> ReadPagesAsync<T>(EtfRequest query,CancellationToken token)
    {
        var rows=new List<T>();string? metadata=null;
        while(true)
        {
            var page=await QueryAsync<EtfQueryPage>(query,token);
            rows.AddRange(JsonSerializer.Deserialize<List<T>>(page.Items)??throw new InvalidDataException("分页为空。"));metadata??=page.Metadata;
            if(rows.Count>10000)throw new InvalidDataException("查询行数超限。");
            if(page.NextOffset is not { } next)return(rows,metadata);
            if(next<=query.Offset)throw new InvalidDataException("分页游标未前进。");
            query=query with{Snapshot=page.Snapshot,Offset=next};
        }
    }
    public async Task<List<EtfSummary>> OverviewAsync(CancellationToken token)
    {
        var result=await ReadPagesAsync<EtfSummary>(new("overview"),token);
        NetworkStatus=await QueryAsync<string>(new("status"),token);return result.Rows;
    }
    public async Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>(await ReadPagesAsync<EvidenceRow>(new("evidence"),token)).Rows;
    public async Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>(await ReadPagesAsync<MarketActivityPoint>(new("market-activity"),token)).Rows;
    public async Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token)=>(await ReadPagesAsync<MarketActivityRevision>(new("activity-revisions"),token)).Rows;
    public Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token)=>RunAsync("activity-review",new("activity-review",Revision:id,Accept:accept,Reviewer:reviewer),token);
    public Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token)=>RunAsync("market-activity-sync",new("market-sync",From:from,To:to),token);
    public async Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>(await ReadPagesAsync<EtfConfiguration>(new("configuration"),token)).Rows;
    public async Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token)=>(await ReadPagesAsync<EquityWatch>(new("equity-watchlist"),token)).Rows;
    public async Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token)=>(await ReadPagesAsync<EquityHoldingRow>(new("equity-holdings",Search:search,Institution:institution,Nature:nature,Favorites:favorites),token)).Rows;
    public async Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token)=>(await ReadPagesAsync<EquityDisclosure>(new("equity-disclosures",key.Market,key.Ticker),token)).Rows;
    public async Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token)=>(await ReadPagesAsync<EquityHoldingRow>(new("equity-events",key.Market,key.Ticker),token)).Rows;
    public async Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token)=>(await ReadPagesAsync<EquityReportDocument>(new("equity-reports",key.Market,key.Ticker),token)).Rows;
    private BackgroundRequest? pendingReportSearch;
    private EtfKey? pendingReportKey;
    private readonly Dictionary<EtfKey,BackgroundRequest> reportSearches=[];
    public async Task<EquityReportSearch> QueueEquityReportsAsync(EtfKey key,CancellationToken token)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var payload=JsonSerializer.Serialize(new EtfRequest("equity-report-search",key.Market,key.Ticker,From:today.AddDays(-400),To:today));
        if(pendingReportSearch is not null&&pendingReportKey!=key)throw new InvalidDataException("上次报告检索受理情况未确认，请先用原股票重试。");
        pendingReportKey=key;
        var request=pendingReportSearch??=(reportSearches.TryGetValue(key,out var existing)?existing:new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,"equity-report-search",Payload:payload));
        var reply=await SendAsync(request,token);
        if(reply.Code is not ("Accepted" or "Succeeded" or "RetryDeferred")&&reply.Receipt is null){pendingReportSearch=null;pendingReportKey=null;throw new InvalidDataException("后台未接受报告检索："+reply.Code);}
        pendingReportSearch=null;pendingReportKey=null;
        if(reply.Receipt?.State=="Completed")reportSearches.Remove(key);else reportSearches[key]=request;
        return new(request.RequestId,reply.Receipt?.State??reply.Code,reply.Receipt?.Code??reply.Code);
    }
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token)=>RunAsync("equity-watch",new("equity-watch",EquityWatch:watch),token);
    public Task SyncEquityAsync(CancellationToken token)=>RunAsync("equity-disclosure-sync",new("equity-sync"),token);
    public Task ReviewEquityAsync(EquityReview review,CancellationToken token)=>RunAsync("equity-review",new("equity-review",EquityReview:review),token);
    public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token)=>RunAsync("watchlist",new("watchlist",key.Market,key.Ticker,Name:name,Index:index,Active:active,Reason:reason,ConfigurationRevision:expectedRevision),token);
    public async Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)=>(await ReadPagesAsync<EtfHistoryPoint>(new("history",key?.Market,key?.Ticker),token)).Rows;
    public async Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>(await ReadPagesAsync<SharePoint>(new("points",key.Market,key.Ticker),token)).Rows;
    public async Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)
    {
        var result=await ReadPagesAsync<CoverageDay>(new("coverage",key.Market,key.Ticker,from,to),token);
        var metadata=JsonSerializer.Deserialize<CoverageReport>(result.Metadata??"")??throw new InvalidDataException("覆盖率口径缺失。");
        return metadata with{Days=result.Rows};
    }
    public async Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)
        =>(await ReadPagesAsync<CollectionAttempt>(new("collection-attempts",key.Market,key.Ticker,from,to),token)).Rows;
    public async Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token)
        =>(await ReadPagesAsync<EtfAcceptance>(new("acceptance",To:through),token)).Rows;
    /// <summary>写操作先持久接受；响应丢失后沿用原ID、原参数，禁止新操作冒充重试。</summary>
    private async Task RunAsync(string job,EtfRequest value,CancellationToken token)
    {
        var payload=JsonSerializer.Serialize(value);
        if(uncertain is not null && (uncertain.JobId!=job || uncertain.Payload!=payload))
            throw new InvalidDataException("上次任务结果尚未确认，请用原参数重试或到任务管理查看记录。");
        var request=uncertain??=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,job,Payload:payload);
        var reply=await SendAsync(request,token);
        if(reply.Code is not ("Accepted" or "Succeeded" or "RetryDeferred") && reply.Receipt?.State!="Completed")
        {uncertain=null;throw new InvalidDataException("后台未接受任务："+reply.Code);}
        while(reply.Receipt?.State!="Completed")
        {
            await Task.Delay(750,token);
            reply=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
            if(reply.Code!="Ok" || reply.Receipt is null)throw new InvalidDataException("无法确认后台任务，请保留原参数重试。");
        }
        uncertain=null;
        if(reply.Receipt.Code=="Rejected")throw new InvalidDataException("配置未保存：输入不符合要求或已被其它窗口修改，请刷新目录后重新选择并重试。");
        if(reply.Receipt.Code!="Succeeded")throw new InvalidDataException("后台任务结果："+reply.Receipt.Code+"；已提交事实保留，详情见执行记录。");
    }
    public async Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)
    {await RunAsync("official-share-sync",new("sync",From:from,To:to),token);return new(to.DayNumber-from.DayNumber+1,0);}
    public Task ReviewAsync(string id,bool accept,string reviewer,bool comparable,CancellationToken token)=>RunAsync("review",new("review",Revision:id,Accept:accept,Reviewer:reviewer,Comparable:comparable),token);
    public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token)=>RunAsync("watchlist",new("watchlist",key.Market,key.Ticker,Name:name,Index:index,Active:active,Reason:reason),token);

    /// <summary>只写用户选择的本地导出文件，完整哈希匹配后原子替换目标。</summary>
    public async Task ExportEvidenceAsync(string revision,bool original,string destination,CancellationToken token)
    {
        var target=Path.GetFullPath(destination);
        // 即使管理员选择错误位置，也不能让导出覆盖程序、服务库或桌面私有数据。
        foreach(var directory in new[]{AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CRS","Background"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CRS")})
            if(target.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("不能把导出文件写入程序或受保护的数据目录。");
        for(var parent=new DirectoryInfo(Path.GetDirectoryName(target)!);parent is not null;parent=parent.Parent)
            if(parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("导出目录不能包含链接。");
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,16384,true);
            var offset=0;string? expected=null;long? length=null;
            while(true)
            {
                var chunk=await QueryAsync<EvidenceChunk>(new("export",Revision:revision,Offset:offset,Original:original),token);
                expected??=chunk.Hash;length??=chunk.Length;
                if(chunk.Hash!=expected || chunk.Length!=length || chunk.Length>8*1024*1024 || chunk.Offset!=offset || chunk.Bytes.Length>16384 || offset+chunk.Bytes.Length>length)
                    throw new InvalidDataException("证据分块不一致。");
                hash.AppendData(chunk.Bytes);await output.WriteAsync(chunk.Bytes,token);offset+=chunk.Bytes.Length;
                if(offset==length)break;
                if(chunk.Bytes.Length==0)throw new InvalidDataException("证据分块未前进。");
            }
            if(Convert.ToHexString(hash.GetHashAndReset())!=expected)throw new InvalidDataException("证据哈希不匹配。");
            output.Flush(true);await output.DisposeAsync();token.ThrowIfCancellationRequested();File.Move(temporary,target,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
