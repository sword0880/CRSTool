using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>正式服务股票状态只读留证；不下载、不审核、不发消息，不把报告目录当成持仓。</summary>
internal static class EquityStatusAuditTool
{
    public static async Task Run()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;
        var workspace=new WorkerEtfWorkspace();var pipe=new BackgroundPipeClient();
        var rows=await MonitorReadinessTool.Read(()=>workspace.EquityHoldingsAsync(null,null,null,false,token),token);
        var watches=await MonitorReadinessTool.Read(()=>workspace.EquityWatchlistAsync(token),token);
        var key=new EtfKey("SSE","600153");
        var disclosures=await MonitorReadinessTool.Read(()=>workspace.EquityDisclosuresAsync(key,token),token);
        var reports=await MonitorReadinessTool.Read(()=>workspace.EquityReportsAsync(key,token),token);
        var jobs=await ReadStatus(pipe,"GetJobs",null,token);
        var history=await ReadStatus(pipe,"GetFilteredHistory","equity-disclosure-sync",token);
        if(jobs.Code!="Ok"||history.Code!="Ok"||jobs.Jobs is null||history.History is null)
            throw new InvalidDataException($"正式计划或执行历史不可用：jobs={jobs.Code}，history={history.Code}；拒绝保存完整股票核验。");
        var checkedAt=CRS.Timing.BeijingClock.Now;
        var root=Path.Combine(Environment.CurrentDirectory,"artifacts","etf-equity-followup-20261024");Directory.CreateDirectory(root);
        var document=new{CheckedAt=checkedAt,Use="ReadOnlyInstalledWorker",Watches=watches,Rows=rows,
            SampleKey=key,SampleDisclosures=disclosures,SampleReportIndex=reports,
            Plans=jobs.Jobs.Where(x=>x.PluginId=="crs.national-team"&&x.JobId.StartsWith("equity-",StringComparison.Ordinal)),
            history.History,Boundary="目录、候选和A类快照分别统计；未证明全部股票、历史期间或每日交易完整；本轮候选修复尚未安装。"};
        var path=Path.Combine(root,"equity-status.json");var staging=path+".tmp";
        await File.WriteAllTextAsync(staging,JsonSerializer.Serialize(document,new JsonSerializerOptions{WriteIndented=true}),token);
        token.ThrowIfCancellationRequested();File.Move(staging,path,true);
        Console.WriteLine($"READ {checkedAt:yyyy-MM-dd HH:mm:ss zzz} 股票观察{watches.Count}只，A类快照{rows.Count(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding)}条，候选/其他{rows.Count(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding)}条；建发报告目录{reports.Count}份，目录不等于已审核持仓。");
    }
    private static async Task<BackgroundResponse> ReadStatus(BackgroundPipeClient pipe,string command,string? job,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while(true)
        {
            var response=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),command,
                job is null?null:"crs.national-team",job),deadline.Token);
            if(response.Code is not ("RateLimited" or "PluginBusy"))return response;
            await Task.Delay(500,deadline.Token);
        }
    }
}
