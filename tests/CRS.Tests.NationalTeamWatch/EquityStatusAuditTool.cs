using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>正式服务股票状态只读留证；不下载、不审核、不发消息，不把报告目录当成持仓。</summary>
internal static class EquityStatusAuditTool
{
    /// <summary>逐只回读已登记披露和目录；空投影不能冒充没有采集或实际未持有。</summary>
    public static async Task Coverage(string directory)
    {
        var root=Path.GetFullPath(directory);Directory.CreateDirectory(root);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var token=deadline.Token;
        var workspace=new WorkerEtfWorkspace();var pipe=new BackgroundPipeClient();
        var started=CRS.Timing.BeijingClock.Now;
        var healthBefore=await ReadStatus(pipe,"GetHealth",null,token);
        if(healthBefore.Code!="Ok"||healthBefore.Health is null)throw new InvalidDataException("后台健康不可用，拒绝形成完整股票覆盖核查。");
        var watches=await MonitorReadinessTool.Read(()=>workspace.EquityWatchlistAsync(token),token);
        var rows=await MonitorReadinessTool.Read(()=>workspace.EquityHoldingsAsync(null,null,null,false,token),token);
        var details=new List<CoverageItem>();var json=new JsonSerializerOptions{WriteIndented=true};
        foreach(var watch in watches)
        {
            var key=new EtfKey(watch.Market,watch.Ticker);
            var disclosures=await MonitorReadinessTool.Read(()=>workspace.EquityDisclosuresAsync(key,token),token);
            var reports=await MonitorReadinessTool.Read(()=>workspace.EquityReportsAsync(key,token),token);
            var holdings=rows.Where(x=>x.Market==watch.Market&&x.Ticker==watch.Ticker).ToArray();
            details.Add(new(watch,holdings,disclosures,reports));
            // 分证券查询不共享同一数据库事务，进度及最终报告均明确这一边界。
            await File.WriteAllTextAsync(Path.Combine(root,"progress.json"),JsonSerializer.Serialize(new
            {StartedAt=started,CheckedAt=CRS.Timing.BeijingClock.Now,Complete=false,ReadScope="SequentialRead",Expected=watches.Count,Details=details},json),token);
            Console.WriteLine($"READ {watch.Ticker} {watch.Name}：已核账户{holdings.Count(EquityStockList.IsVerifiedHolding)}，其他投影{holdings.Count(x=>!EquityStockList.IsVerifiedHolding(x))}，已存披露{disclosures.Count}，目录{reports.Count}。");
        }
        var finalRows=await MonitorReadinessTool.Read(()=>workspace.EquityHoldingsAsync(null,null,null,false,token),token);
        var jobs=await ReadStatus(pipe,"GetJobs",null,token);
        var history=await ReadStatus(pipe,"GetFilteredHistory","equity-disclosure-sync",token);
        var healthAfter=await ReadStatus(pipe,"GetHealth",null,token);
        if(jobs.Code!="Ok"||jobs.Jobs is null||history.Code!="Ok"||history.History is null||healthAfter.Code!="Ok"||healthAfter.Health is null)
            throw new InvalidDataException("计划、执行历史或末次健康不可用，保留逐只进度，不保存完整覆盖报告。");
        var stable=JsonSerializer.Serialize(rows)==JsonSerializer.Serialize(finalRows);
        var report=new{StartedAt=started,CheckedAt=CRS.Timing.BeijingClock.Now,Complete=stable,Use="ReadOnlyInstalledWorker",
            ReadScope="SequentialRead",HoldingsStableAcrossRead=stable,HealthBefore=healthBefore.Health,HealthAfter=healthAfter.Health,
            Watches=watches,VerifiedStocks=EquityStockList.Build(rows,true),OtherStocks=EquityStockList.Build(rows,false),Rows=rows,Details=details,
            Plans=jobs.Jobs.Where(x=>x.PluginId=="crs.national-team"&&x.JobId.StartsWith("equity-",StringComparison.Ordinal)),history.History,
            Boundary="仅核查已配置股票及已登记原件；逐只读取不构成同一事务，不下载、不审核、不批准持仓或清仓，不证明全市场覆盖。"};
        var path=Path.Combine(root,"coverage.json");var staging=path+".tmp";
        await File.WriteAllTextAsync(staging,JsonSerializer.Serialize(report,json),token);token.ThrowIfCancellationRequested();File.Move(staging,path,true);
        Console.WriteLine($"READ 覆盖核查：观察{watches.Count}只，已核{EquityStockList.Build(rows,true).Count}只，其他披露{EquityStockList.Build(rows,false).Count}只；前后持仓一致={stable}。");
    }
    private sealed record CoverageItem(EquityWatch Watch,IReadOnlyList<EquityHoldingRow> Rows,IReadOnlyList<EquityDisclosure> Disclosures,IReadOnlyList<EquityReportDocument> Reports);

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
