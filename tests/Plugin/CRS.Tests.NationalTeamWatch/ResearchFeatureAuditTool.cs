using System.Text.Json;
using System.Diagnostics;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>只读复核已配置ETF和现有研究契约；逐项保留不可用，绝不触发采集、审核或通知。</summary>
internal static class ResearchFeatureAuditTool
{
    public static async Task BatchAsync(DateOnly through,string directory)
    {
        if(through>=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime))throw new ArgumentException("只接受已结束统计日。");
        var path=Path.Combine(Path.GetFullPath(directory),"acceptance-batch.json");
        if(File.Exists(path))throw new IOException("批量回查结果已存在，拒绝覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(EtfResearchTicket.MaximumWaitSeconds+10));
        var pipe=new BackgroundPipeClient();var pages=new List<EtfQueryPageMeasurement>();var client=new WorkerEtfWorkspace(pipe,pages.Add);
        var before=await pipe.QueryAsync("GetHealth",budget.Token);var started=Stopwatch.GetTimestamp();
        List<EtfAcceptance>? rows=null;string? failure=null;
        try{rows=await client.AcceptanceAsync(through,budget.Token);}
        catch(Exception error) when(error is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {failure=error is BackgroundServiceException service?service.Code:error.GetType().Name;}
        var elapsed=(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var after=await pipe.QueryAsync("GetHealth",budget.Token);
        var stable=before.Health is{State:"Running"}a&&after.Health is{State:"Running"}b&&a.ProcessId==b.ProcessId;
        var report=new{CheckedAt=CRS.Timing.BeijingClock.Now,ReadOnly=true,Through=through,State=rows is null?"Unavailable":"Available",Failure=failure,
            ElapsedMs=elapsed,HealthStable=stable,Before=before.Health,After=after.Health,Rows=rows,Pages=pages,FinancialAcceptance=rows is{Count:>0}&&rows.All(x=>x.DataAcceptancePassed)};
        await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            await JsonSerializer.SerializeAsync(file,report,new JsonSerializerOptions{WriteIndented=true});
        Console.WriteLine($"BATCH readonly;state={report.State};rows={rows?.Count};elapsedMs={elapsed};healthy={stable};financialAcceptance={report.FinancialAcceptance}");
        if(rows is null||!stable)Environment.ExitCode=2;
    }
    public static async Task EtfReportCoverageAsync(string directory)
    {
        var output=Path.GetFullPath(directory);Directory.CreateDirectory(output);
        var path=Path.Combine(output,"etf-report-coverage.json");
        if(File.Exists(path))throw new IOException("ETF报告覆盖取证已存在，拒绝覆盖。");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var pipe=new BackgroundPipeClient();var client=new WorkerEtfWorkspace(pipe);var records=new List<object>();var failures=0;
        async Task<T?> Read<T>(string feature,Func<CancellationToken,Task<T>> read) where T:class
        {
            using var operation=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);operation.CancelAfter(TimeSpan.FromSeconds(20));
            var started=Stopwatch.GetTimestamp();
            try
            {
                var value=await read(operation.Token);
                records.Add(new{Feature=feature,State="Available",ObservedAt=CRS.Timing.BeijingClock.Now,ElapsedMs=(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,Data=value});return value;
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or NotSupportedException)
            {
                failures++;records.Add(new{Feature=feature,State="Unavailable",ObservedAt=CRS.Timing.BeijingClock.Now,
                    ElapsedMs=(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,Code=error is BackgroundServiceException service?service.Code:error.GetType().Name});return null;
            }
        }
        var before=await Read("WorkerHealthBefore",token=>pipe.QueryAsync("GetHealth",token));
        var configuration=await Read("ConfiguredEtfs",client.ConfigurationAsync);var pool=configuration?.Where(x=>x.Active).ToArray();
        var coverage=new List<object>();
        if(pool is not null)
            foreach(var fund in pool)
            {
                var key=new EtfKey(fund.Market,fund.Ticker);var label=key.Market+":"+key.Ticker;
                var index=await Read("ReportIndex:"+label,token=>client.EtfReportIndexAsync(key,token));
                var documents=await Read("RegisteredDocuments:"+label,token=>client.AlertDocumentsAsync(key,token));
                var holdings=await Read("NamedHoldings:"+label,token=>client.NamedEtfHoldingsAsync(key,token));
                var candidateStates=new List<object>();
                if(documents is not null)
                    foreach(var document in documents)
                    {
                        var candidates=await Read("HolderCandidates:"+label+":"+document.Id,token=>client.EtfHolderCandidatesAsync(key,document.Id,token));
                        candidateStates.Add(new{DocumentId=document.Id,State=candidates?.State??"Unavailable",CandidateRows=candidates?.Holders.Count});
                    }
                // 不可用保留null，目录和登记原件均不能当作已确认持仓，也不推断模板支持范围。
                coverage.Add(new{key.Market,key.Ticker,fund.Name,IndexState=index?.State,IndexDocuments=index?.Documents.Count,
                    RegisteredDocuments=documents?.Count,NamedHoldingRows=holdings?.Count,ConfirmedNamedHoldingRows=holdings?.Count(x=>x.OriginalConfirmed),
                    IndexRead=index is not null,DocumentsRead=documents is not null,HoldingsRead=holdings is not null,CandidateQueries=candidateStates});
                Console.WriteLine($"ETF {label} index={index?.State??"Unavailable"};reports={index?.Documents.Count};documents={documents?.Count};confirmed={holdings?.Count(x=>x.OriginalConfirmed)}");
            }
        var after=await Read("WorkerHealthAfter",token=>pipe.QueryAsync("GetHealth",token));
        var stable=before?.Health is{State:"Running"}a&&after?.Health is{State:"Running"}b&&a.ProcessId==b.ProcessId;
        var report=new{CheckedAt=CRS.Timing.BeijingClock.Now,ReadOnly=true,ConfiguredActiveEtfs=pool?.Length,HealthStable=stable,
            AllQueriesAvailable=failures==0,UnavailableQueries=failures,Coverage=coverage,Records=records};
        await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            await JsonSerializer.SerializeAsync(file,report,new JsonSerializerOptions{WriteIndented=true});
        if(!stable||failures!=0)Environment.ExitCode=2;
    }

    public static async Task RunAsync(string directory)
    {
        var output=Path.GetFullPath(directory);
        if(File.Exists(Path.Combine(output,"feature-audit.json")))throw new IOException("研究取证报告已存在，拒绝覆盖原结果。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var pipe = new BackgroundPipeClient(); var client = new WorkerEtfWorkspace(pipe);
        var records = new List<object>(); var failures = 0;
        async Task<T?> Read<T>(string feature, Func<CancellationToken, Task<T>> query,bool research=false) where T : class
        {
            if (deadline.IsCancellationRequested)
            { records.Add(new { Feature = feature, State = "Unavailable", Code = "AuditDeadline" }); failures++; return null; }
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var seconds=research?EtfResearchTicket.MaximumWaitSeconds+10:20;
            operation.CancelAfter(TimeSpan.FromSeconds(seconds));
            var started=Stopwatch.GetTimestamp();
            try
            {
                var value = await query(operation.Token);
                records.Add(new { Feature = feature, State = "Available", ObservedAt = CRS.Timing.BeijingClock.Now, ElapsedMs=(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,BudgetSeconds=seconds,Data = value });
                return value;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or NotSupportedException)
            {
                failures++;
                records.Add(new { Feature = feature, State = "Unavailable", ObservedAt = CRS.Timing.BeijingClock.Now,
                    ElapsedMs=(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,BudgetSeconds=seconds,
                    Code = error is BackgroundServiceException service ? service.Code : error.GetType().Name });
                return null;
            }
        }
        var before = await Read("WorkerHealthBefore", token => pipe.QueryAsync("GetHealth", token));
        var configuration = await Read("ConfiguredEtfs", client.ConfigurationAsync);
        var pool = configuration?.Where(x => x.Active).ToArray();
        var holdings = new List<NamedEtfHolding>();var completedHoldingQueries=0;
        if (pool is not null)
            foreach (var fund in pool)
                if (await Read("NamedEtfHolding:" + fund.Market + ":" + fund.Ticker,
                    token => client.NamedEtfHoldingsAsync(new(fund.Market, fund.Ticker), token)) is { } rows)
                    {holdings.AddRange(rows);completedHoldingQueries++;}
        var metadata = await Read("EtfComparableMetadata", client.AlertMetadataAsync);
        var overview = await Read("ExitAlertOverview", client.AlertOverviewAsync,research:true);
        var events = await Read("ExitAlertEvents", token => client.AlertEventsAsync(new(), token));
        var activity = await Read("WholeMarketActivity", client.MarketActivityAsync);
        var equities = await Read("DirectEquityHoldings", token => client.EquityHoldingsAsync(null, null, null, false, token));
        var through = DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime); var from = through.AddDays(-400);
        var industry = await Read("IndustryPublishedPe", token => client.IndustryPeReportAsync(from, through, token));
        var indices = await Read("IndexSeriesResearch", token => client.IndexSeriesReportAsync(from, through, token));
        // 已有NAV适配器的明确基金；不能以一只基金可用声称监控池NAV全部接通。
        if (pool?.Any(x => x.Market == "SZSE" && x.Ticker == "159915") == true)
            await Read("Nav:SZSE:159915", token => client.NavPointsAsync(new("SZSE", "159915"), from, through, token));
        // 新研究接口只接受已结束日；当天未收盘不能作为收盘事实，沿用页面的昨日默认口径。
        var studyAsOf=through.AddDays(-1);
        var acceptance=await Read("EtfAcceptanceBatch",token=>client.AcceptanceAsync(studyAsOf,token),research:true);
        // 只读抽样验证协议可用；不将一只基金或当前截面的结果推广为全池金融覆盖。
        if(pool is{Length:>0})
        {
            var fund=pool[0];var key=new EtfKey(fund.Market,fund.Ticker);
            await Read("NamedEtfHoldingHistory:"+key.Market+":"+key.Ticker,token=>client.EtfHoldingHistoryAsync(key,studyAsOf,token));
            await Read("JointExitStudy:"+key.Market+":"+key.Ticker,token=>client.JointExitStudyAsync(key,studyAsOf,token),research:true);
        }
        await Read("MarketCycleStudyCurrent",token=>client.MarketCycleStudyAsync(studyAsOf,null,token),research:true);
        await Read("MarketBreadthHistoryCurrent",token=>client.MarketBreadthHistoryAsync(through,token));
        var after = await Read("WorkerHealthAfter", token => pipe.QueryAsync("GetHealth", token));
        var healthStable = before?.Health is { State: "Running" } a && after?.Health is { State: "Running" } b && a.ProcessId == b.ProcessId;
        var summary = new { CheckedAt = CRS.Timing.BeijingClock.Now, StudyAsOf=studyAsOf, ReadOnly = true, HealthStable = healthStable,
            AllQueriesAvailable = failures == 0, UnavailableQueries = failures, ConfiguredActiveEtfs = pool?.Length,
            CompletedNamedHoldingQueries=completedHoldingQueries,NamedHoldingQueryCoverageComplete=pool is not null&&completedHoldingQueries==pool.Length,
            BatchAcceptanceEtfs=acceptance?.Count,
            NamedEtfHoldingRows = holdings.Count, NamedEtfHoldingScope = "Only successfully read configured ETF queries; inspect UnavailableQueries",
            ConfirmedNamedEtfHoldingRows = holdings.Count(x => x.OriginalConfirmed), ComparableEtfs = metadata?.Count(x => x.OriginalConfirmed),
            AlertDataAcceptance = overview?.DataAcceptancePassed, AlertEvents = events?.Count, MarketActivityRows = activity?.Count,
            DirectEquityRows = equities?.Count, DisclosedEquityRows = equities?.Count(x => x.EvidenceLevel == EvidenceLevel.DisclosedHolding),
            IndustrySeries = industry?.Rows.Count, IndexSeries = indices?.Rows.Count, Records = records };
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "feature-audit.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"AUDIT readonly;healthy={healthStable};activeEtfs={pool?.Length};namedEtfRows={holdings.Count};unavailable={failures};dataAcceptance={overview?.DataAcceptancePassed};equityRows={equities?.Count}");
        // 查询可用与业务充分性是两回事；失败保留报告并以非零退出，不能把不可用变为零覆盖。
        if (!healthStable || failures != 0) Environment.ExitCode = 2;
    }
}
