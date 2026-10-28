using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

namespace CRS.Worker.EtfFixture;

/// <summary>测试专用的真实登记时序，不回写系统时钟、不联网、不进入正式发行。</summary>
internal static class MarketActivityKnowledgeSeed
{
    public static void Apply(MarketRepository repository,string root)
    {
        if(!Directory.Exists(Path.Combine(root,"market-knowledge.fixture")))return;
        var day=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-7);
        MarketActivitySource Source(string market,decimal amount)=>new(OfficialMarketActivitySource.Request(market,day),Encoding.UTF8.GetBytes("synthetic-knowledge|"+market+"|"+amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),market,day,new(market,day,amount,amount*10));
        repository.SaveMarketActivity("SSE",day,Source("SSE",100));repository.SaveMarketActivity("SZSE",day,Source("SZSE",200));
        // 明确跨过系统时钟分辨率，使真实截止时点可以位于两次登记之间。
        Thread.Sleep(40);repository.SaveMarketActivity("SSE",day,Source("SSE",101));
    }
}
