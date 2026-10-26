using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>仅回读指定证券的完整披露、候选和目录；部分文件不是验收，最后同一健康进程才写完整凭据。</summary>
internal static class EquityDisclosureReadLive
{
    public static async Task Run(string market,string ticker,string output)
    {
        if(market is not("SSE" or "SZSE")||ticker.Length!=6||!ticker.All(char.IsAsciiDigit))throw new ArgumentException("证券身份无效。");
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("只读取证目录已存在，不覆盖既有时点。");Directory.CreateDirectory(output);
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=limit.Token;var pipe=new BackgroundPipeClient();var reader=new WorkerEtfWorkspace(pipe);var json=new JsonSerializerOptions{WriteIndented=true};
        async Task Save(string name,object value)=>await File.WriteAllTextAsync(Path.Combine(output,name),JsonSerializer.Serialize(value,json),token);
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await pipe.SendAsync(request,token);if(reply.Code!="RateLimited")return reply;await Task.Delay(500,token);}}
        var before=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(before.Health?.State!="Running")throw new InvalidDataException("正式后台未就绪。");
        var key=new EtfKey(market,ticker);var holdings=await reader.EquityHoldingsAsync(null,null,null,false,token);await Save("holdings.json",holdings);
        var disclosures=await reader.EquityDisclosuresAsync(key,token);await Save("disclosures.json",disclosures);
        var events=await reader.EquityEventsAsync(key,token);await Save("events.json",events);
        var reports=await reader.EquityReportsAsync(key,token);await Save("reports.json",reports);
        var history=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory","crs.national-team","equity-report-search",PageSize:1));await Save("report-requests.json",history);
        if(history.Code!="Ok"||history.PendingRequests is null)throw new InvalidDataException("报告待处理请求未确认，不能猜测没有既有任务。");
        var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(after.Health?.State!="Running"||after.Health.ProcessId!=before.Health.ProcessId)throw new InvalidDataException("取证期间后台健康或进程改变。");
        var selected=holdings.Where(x=>x.Market==market&&x.Ticker==ticker).ToArray();
        await Save("acceptance.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Use="ReadOnlyInstalledWorker",Key=key,Health=after.Health,CurrentRows=selected.Length,
            CurrentDisclosed=selected.Count(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding),Disclosures=disclosures.Count,HistoricalEvents=events.Count,ReportDocuments=reports.Count,
            AllCurrentRows=holdings.Count,AllCurrentDisclosed=holdings.Count(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding),AllDisclosedStocks=holdings.Where(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding).Select(x=>new{x.Market,x.Ticker}).Distinct().Count(),
            Boundary="只读目录和候选不等于已核持仓；历史事件不重复计为当前组合，不证明机构交易或完整方向"});
        Console.WriteLine($"READ {ticker}：当前{selected.Length}、已核{selected.Count(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding)}、历次{events.Count}、目录{reports.Count}；同一健康PID{after.Health.ProcessId}。");
    }
}
