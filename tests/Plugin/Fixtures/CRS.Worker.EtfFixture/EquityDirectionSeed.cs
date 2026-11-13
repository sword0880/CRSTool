using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

namespace CRS.Worker.EtfFixture;

/// <summary>仅测试夹具按显式标记登记合成股东原件；正式发行不包含这个程序集。</summary>
internal static class EquityDirectionSeed
{
    public static void Apply(MarketRepository repository,string root)
    {
        var directory=Path.Combine(root,"equity-direction.fixture");if(!Directory.Exists(directory))return;
        var source=JsonSerializer.SerializeToUtf8Bytes(new{sqlId="COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS",actionErrors=Array.Empty<string>(),pageHelp=new{total=2},result=new[]{
            new{REPORT_DATE="2026-08-28",STOCK_ID="600036",NAME="中央汇金资产管理有限责任公司",NUMBER_END=900,RATIO=9},
            new{REPORT_DATE="2026-08-28",STOCK_ID="600036",NAME="全国社保基金一零一组合",NUMBER_END=550,RATIO=5}}});
        repository.SaveEquityDisclosure("SSE","600036",OfficialEquitySource.Request("SSE","600036"),source);
        var disclosure=repository.EquityDisclosures(new("SSE","600036")).Single();var bytes=File.ReadAllBytes(Path.Combine(directory,"synthetic.pdf"));
        foreach(var account in disclosure.Holders.Select(x=>x.Account))
            repository.ReviewEquity(new(disclosure.Id,new(2026,6,30),new(2026,8,28),"https://www.sse.com.cn/fixture-direction-pipe.pdf","合成股东表","synthetic-pipe-reviewer",false,"Portfolio",Account:account,OriginalConfirmed:true),bytes);
    }
}
