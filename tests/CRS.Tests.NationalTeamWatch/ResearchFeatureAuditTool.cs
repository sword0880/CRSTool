using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>只读复核已配置ETF和现有研究契约；逐项保留不可用，绝不触发采集、审核或通知。</summary>
internal static class ResearchFeatureAuditTool
{
    public static async Task RunAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pipe = new BackgroundPipeClient(); var client = new WorkerEtfWorkspace(pipe);
        var records = new List<object>(); var failures = 0;
        async Task<T?> Read<T>(string feature, Func<CancellationToken, Task<T>> query) where T : class
        {
            if (deadline.IsCancellationRequested)
            { records.Add(new { Feature = feature, State = "Unavailable", Code = "AuditDeadline" }); failures++; return null; }
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            operation.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var value = await query(operation.Token);
                records.Add(new { Feature = feature, State = "Available", ObservedAt = CRS.Timing.BeijingClock.Now, Data = value });
                return value;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or NotSupportedException)
            {
                failures++;
                records.Add(new { Feature = feature, State = "Unavailable", ObservedAt = CRS.Timing.BeijingClock.Now,
                    Code = error is BackgroundServiceException service ? service.Code : error.GetType().Name });
                return null;
            }
        }
        var before = await Read("WorkerHealthBefore", token => pipe.QueryAsync("GetHealth", token));
        var configuration = await Read("ConfiguredEtfs", client.ConfigurationAsync);
        var pool = configuration?.Where(x => x.Active).ToArray();
        var holdings = new List<NamedEtfHolding>();
        if (pool is not null)
            foreach (var fund in pool)
                if (await Read("NamedEtfHolding:" + fund.Market + ":" + fund.Ticker,
                    token => client.NamedEtfHoldingsAsync(new(fund.Market, fund.Ticker), token)) is { } rows)
                    holdings.AddRange(rows);
        var metadata = await Read("EtfComparableMetadata", client.AlertMetadataAsync);
        var overview = await Read("ExitAlertOverview", client.AlertOverviewAsync);
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
        // 只读抽样验证协议可用；不将一只基金或当前截面的结果推广为全池金融覆盖。
        if(pool is{Length:>0})
        {
            var fund=pool[0];var key=new EtfKey(fund.Market,fund.Ticker);
            await Read("NamedEtfHoldingHistory:"+key.Market+":"+key.Ticker,token=>client.EtfHoldingHistoryAsync(key,studyAsOf,token));
            await Read("JointExitStudy:"+key.Market+":"+key.Ticker,token=>client.JointExitStudyAsync(key,studyAsOf,token));
        }
        await Read("MarketCycleStudyCurrent",token=>client.MarketCycleStudyAsync(studyAsOf,null,token));
        await Read("MarketBreadthHistoryCurrent",token=>client.MarketBreadthHistoryAsync(through,token));
        var after = await Read("WorkerHealthAfter", token => pipe.QueryAsync("GetHealth", token));
        var healthStable = before?.Health is { State: "Running" } a && after?.Health is { State: "Running" } b && a.ProcessId == b.ProcessId;
        var summary = new { CheckedAt = CRS.Timing.BeijingClock.Now, StudyAsOf=studyAsOf, ReadOnly = true, HealthStable = healthStable,
            AllQueriesAvailable = failures == 0, UnavailableQueries = failures, ConfiguredActiveEtfs = pool?.Length,
            NamedEtfHoldingRows = holdings.Count, NamedEtfHoldingScope = "Only successfully read configured ETF queries; inspect UnavailableQueries",
            ConfirmedNamedEtfHoldingRows = holdings.Count(x => x.OriginalConfirmed), ComparableEtfs = metadata?.Count(x => x.OriginalConfirmed),
            AlertDataAcceptance = overview?.DataAcceptancePassed, AlertEvents = events?.Count, MarketActivityRows = activity?.Count,
            DirectEquityRows = equities?.Count, DisclosedEquityRows = equities?.Count(x => x.EvidenceLevel == EvidenceLevel.DisclosedHolding),
            IndustrySeries = industry?.Rows.Count, IndexSeries = indices?.Rows.Count, Records = records };
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "feature-audit.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"AUDIT readonly;healthy={healthStable};activeEtfs={pool?.Length};namedEtfRows={holdings.Count};unavailable={failures};dataAcceptance={overview?.DataAcceptancePassed};equityRows={equities?.Count}");
        // 查询可用与业务充分性是两回事；失败保留报告并以非零退出，不能把不可用变为零覆盖。
        if (!healthStable || failures != 0) Environment.ExitCode = 2;
    }
}
