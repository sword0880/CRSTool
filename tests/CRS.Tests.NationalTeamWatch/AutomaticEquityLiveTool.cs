using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Worker.Ipc;

internal static class AutomaticEquityLiveTool
{
    public static async Task Sources(string directory)
    {
        Directory.CreateDirectory(directory);using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(directory,"policy")));
        var securities=await gateway.GetAsync(AutomaticEquitySource.SecuritiesUri,stop.Token);
        if(securities.StatusCode!=200)throw new InvalidDataException("官方证券目录未成功，不继续请求。");
        File.WriteAllBytes(Path.Combine(directory,"securities.json"),securities.Content);
        var stocks=AutomaticEquitySource.ParseSecurities(securities.Content).ToDictionary(x=>x.Ticker);
        var result=await gateway.PostJsonAsync(AutomaticEquitySource.ReportsUri,AutomaticEquitySource.Request(new(2026,10,31),1),stop.Token);
        if(result.StatusCode!=200)throw new InvalidDataException("官方报告目录未成功，不继续请求。");
        File.WriteAllBytes(Path.Combine(directory,"reports.json"),result.Content);
        var page=AutomaticEquitySource.ParsePage(new(2026,10,31),1,result.Content,stocks);
        File.WriteAllText(Path.Combine(directory,"source-acceptance.json"),JsonSerializer.Serialize(new{CheckedAt=CRS.Timing.BeijingClock.Now,Securities=stocks.Count,page.Total,Documents=page.Documents.Count,Installed=false,FinancialWrites=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"SOURCE 正式网关取得{stocks.Count}个沪深A股身份、单日{page.Total}条公告；仅验证来源，无金融写入。");
    }
    public static async Task Status(string directory)
    {
        Directory.CreateDirectory(directory);using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var pipe=new BackgroundPipeClient();var client=new WorkerEtfWorkspace(pipe);
        var health=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),stop.Token);
        var status=await client.AutomaticEquityStatusAsync(stop.Token);
        var jobs=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),stop.Token);
        var history=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory","crs.national-team","equity-auto-sync"),stop.Token);
        var watches=await client.EquityWatchlistAsync(stop.Token);var rows=await client.EquityHoldingsAsync(null,null,null,false,stop.Token);
        File.WriteAllText(Path.Combine(directory,"status.json"),JsonSerializer.Serialize(new{CheckedAt=CRS.Timing.BeijingClock.Now,Health=health.Health,Status=status,Jobs=jobs.Jobs,History=history.History,Watches=watches,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(status.DisplayText);
    }
}
