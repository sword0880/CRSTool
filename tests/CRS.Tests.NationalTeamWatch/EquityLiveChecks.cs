using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class EquityLiveChecks
{
    public static async Task Run()
    {
        var root=Path.Combine(Environment.CurrentDirectory,"artifacts","direct-equity-20261018","live",Guid.NewGuid().ToString("N"));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(55));var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,"policy"),RandomNumberGenerator.GetBytes(32)));
        foreach(var ticker in new[]{"601398","600153","600036"})
        {
            var uri=OfficialEquitySource.Request("SSE",ticker);var response=await gateway.GetAsync(uri,deadline.Token);if(response.StatusCode!=200)throw new Exception("官方股东接口未返回成功。");
            repository.SaveEquityDisclosure("SSE",ticker,uri,response.Content);
        }
        foreach(var market in new[]{"SSE","SZSE"})
        {
            var uri=CRS.Plugins.NationalTeamWatch.Domain.ReviewedExchangeCalendar.Source(market,2026);var response=await gateway.GetAsync(uri,deadline.Token);
            if(response.StatusCode!=200)throw new Exception("官方日历未返回成功。");repository.RegisterReviewedCalendar(market,2026,response.Content,uri);
        }
        var rows=repository.EquityHoldings(null,null,null,false);if(rows.Any(x=>x.ReportAsOf is not null||x.Delta is not null))throw new Exception("来源日期被冒充已确认持仓。");
        File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS 真实官方股东与两市日历：隔离保存{rows.Count}条机构候选，未自动确认持仓；目录={root}");
    }
}
