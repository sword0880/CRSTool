using System.Text;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Worker.EtfFixture;
/// <summary>仅隔离夹具登记合成原件及人工确认；正式包没有该入口，不将合成审批当作真实主体证据。</summary>
internal static class NamedEtfHoldingHistorySeed
{
    public static void Apply(MarketRepository repository,string root)
    {
        if(!Directory.Exists(Path.Combine(root,"named-holding-history.fixture")))return;
        var key=new EtfKey("SSE","510300");var now=CRS.Timing.BeijingClock.Now;var day=DateOnly.FromDateTime(now.DateTime).AddDays(-7);
        var periods=new[]{day.AddMonths(-6),day.AddMonths(-3),day.AddDays(-1)};AlertDocument? latest=null;
        for(var i=0;i<periods.Length;i++)
        {
            var url="https://www.sse.com.cn/synthetic-history-"+i+".pdf";repository.RegisterAlertDocument(new(key.Market,key.Ticker,url),Encoding.UTF8.GetBytes("%PDF-synthetic-named-history-"+i));
            var doc=repository.AlertDocuments(key).Single(x=>x.Url==url);latest=doc;
            void Save(string legal,string name,string account,decimal shares)=>repository.ReviewNamedEtfHolding(new(doc.Id,legal,name,account,periods[i],now.AddDays(-1),shares,"合成P10","synthetic-reviewer",true,true));
            Save("synthetic-huijin","中央汇金投资有限责任公司","synthetic-huijin-account",100-i*10);
            if(i!=1)Save("synthetic-safe","梧桐树投资平台有限责任公司","synthetic-safe-account",200-i*10);
            if(i==2)foreach(var fund in InstitutionAccounts.Names.Where(x=>x.Group=="国家大基金"))Save("synthetic-"+fund.LegalName,fund.LegalName,fund.LegalName,50);
            if(i==2)Save("synthetic-private","合成私募法人","synthetic-private-account",999999);
        }
        repository.ReviewAlertMetadata(new(latest!.Id,"synthetic-fund","synthetic-manager","synthetic-index",periods[0],day,"synthetic-reviewer",true));
    }
}
