using System.Text.Json;
using CRS.Common.Concurrency;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    private readonly CancellationTokenSource researchStopping=new();
    private readonly Dictionary<string,ResearchJob> researchJobs=new(StringComparer.Ordinal);
    private sealed class ResearchJob(string id,EtfRequest request,string fingerprint,CancellationTokenSource cancellation)
    {
        public string Id{get;}=id;
        public EtfRequest Request{get;}=request;
        public string Fingerprint{get;}=fingerprint;
        public CancellationTokenSource Cancellation{get;}=cancellation;
        public string State="Queued";
        public string? Snapshot,Failure;
        public DateTimeOffset ExpiresAt=CRS.Timing.BeijingClock.Now.AddMinutes(10);
        public Task Work=Task.CompletedTask;
        public Task? Release;
        public EtfResearchTicket Ticket()=>new(Id,State,Snapshot,Failure,ExpiresAt,Fingerprint);
    }
    [NationalTeamQuery("research-submit",NationalTeamQueryKind.ShortRead)]
    private Task<NationalTeamQueryResult> SubmitResearch(EtfRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target=ResearchTarget(request);
        if(QueryHandler(target.Operation).Kind!=NationalTeamQueryKind.ResearchRead)throw new ArgumentException("只支持已注册的研究读作业。");
        var fingerprint=ContentFingerprint.Sha256(System.Text.Encoding.UTF8.GetBytes(Canonical(target)));
        var id=ResearchId(request);ResearchJob job;
        lock(cacheGate)
        {
            researchStopping.Token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            foreach(var expired in researchJobs.Values.Where(x=>x.ExpiresAt<CRS.Timing.BeijingClock.Now&&x.Work.IsCompleted&&x.Release is null).ToArray())
            {if(expired.Snapshot is {} snapshot)snapshots.Remove(snapshot);researchJobs.Remove(expired.Id);expired.Cancellation.Dispose();}
            if(researchJobs.TryGetValue(id,out job!))
            {if(job.Fingerprint!=fingerprint)throw new ArgumentException("研究作业标识已绑定其它参数。");}
            else
            {
                if(researchJobs.Count>=2)throw new WorkAdmissionException("ResearchCapacity");
                var cancellation=CancellationTokenSource.CreateLinkedTokenSource(researchStopping.Token);
                cancellation.CancelAfter(TimeSpan.FromSeconds(90));
                job=new(id,target,fingerprint,cancellation);researchJobs.Add(id,job);
                job.Work=Task.Run(()=>ConstructResearchAsync(job),CancellationToken.None);
            }
            return TicketResult(job.Ticket());
        }
    }
    [NationalTeamQuery("research-status",NationalTeamQueryKind.ShortRead)]
    private Task<NationalTeamQueryResult> ResearchStatus(EtfRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var id=ResearchId(request);var target=ResearchTarget(request);
        lock(cacheGate)
        {
            if(!researchJobs.TryGetValue(id,out var job)||job.ExpiresAt<CRS.Timing.BeijingClock.Now)
                throw new ArgumentException("研究作业已过期或后台已重启，请重新提交。");
            if(Canonical(target)!=Canonical(job.Request))throw new ArgumentException("研究作业参数改变。");
            return TicketResult(job.Ticket());
        }
    }
    [NationalTeamQuery("research-release",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> ReleaseResearch(EtfRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var id=ResearchId(request);var target=ResearchTarget(request);Task release;
        lock(cacheGate)
        {
            if(!researchJobs.TryGetValue(id,out var job))return new([JsonSerializer.Serialize("Released")]);
            if(Canonical(target)!=Canonical(job.Request))throw new ArgumentException("研究作业参数改变。");
            release=job.Release??=ReleaseResearchCore(job);
        }
        await release.WaitAsync(token);
        return new([JsonSerializer.Serialize("Released")]);
    }
    private async Task ReleaseResearchCore(ResearchJob job)
    {
        await Task.Yield();
        // 取消回调可能结束一个数据库读取，不能持有快照锁调用Cancel。
        await job.Cancellation.CancelAsync();
        await job.Work;
        lock(cacheGate)
        {
            if(job.Snapshot is {} snapshot)snapshots.Remove(snapshot);
            if(researchJobs.Remove(job.Id))job.Cancellation.Dispose();
        }
    }
    private static string ResearchId(EtfRequest request)=>request.Revision is {Length:32} id&&id.All(Uri.IsHexDigit)?id:throw new ArgumentException("研究作业标识无效。");
    private static EtfRequest ResearchTarget(EtfRequest request)
    {
        var target=request.ResearchRequest??throw new ArgumentException("缺少研究请求。");
        if(target.ResearchRequest is not null||target.Snapshot is not null||target.Offset!=0||target.Count is <1 or >256)throw new ArgumentException("研究请求不可嵌套或携带现有分页。");
        return Parse(EtfBusinessProtocol.WriteRequest(target));
    }
    private static Task<NationalTeamQueryResult> TicketResult(EtfResearchTicket ticket)=>Task.FromResult(new NationalTeamQueryResult([JsonSerializer.Serialize(ticket)]));
    private async Task ConstructResearchAsync(ResearchJob job)
    {
        try
        {
            using var lease=await researchQueries.EnterAsync(job.Cancellation.Token,TimeSpan.FromSeconds(30));
            lock(cacheGate){job.Cancellation.Token.ThrowIfCancellationRequested();job.State="Running";}
            var result=await BuildSnapshotAsync(job.Request,job.Cancellation.Token);
            lock(cacheGate)
            {
                if(job.Cancellation.IsCancellationRequested){snapshots.Remove(result.Id);job.Cancellation.Token.ThrowIfCancellationRequested();}
                job.Snapshot=result.Id;job.State="Ready";
            }
        }
        catch(Exception error)
        {
            lock(cacheGate)
            {
                job.State=error is OperationCanceledException?"Cancelled":"Failed";
                job.Failure=error switch
                {
                    OperationCanceledException=>researchStopping.IsCancellationRequested?"Stopped":"CancelledOrTimedOut",
                    WorkAdmissionException=>"Busy",InvalidDataException=>"EvidenceOrRequestInvalid",ArgumentException=>"InvalidRequest",_=>"ConstructionFailed"
                };
            }
        }
    }
    private async Task StopQueriesAsync()
    {
        var shortDrain=shortQueries.CloseAsync();var researchDrain=researchQueries.CloseAsync();
        await researchStopping.CancelAsync();
        await Task.WhenAll(shortDrain,researchDrain);
        Task[] jobs;lock(cacheGate)jobs=researchJobs.Values.SelectMany(x=>x.Release is {} release?new[]{x.Work,release}:new[]{x.Work}).ToArray();
        await Task.WhenAll(jobs);
    }
}
