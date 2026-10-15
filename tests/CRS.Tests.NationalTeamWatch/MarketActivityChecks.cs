using System.Text;
using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeam.Jobs;

internal static class MarketActivityChecks
{
    private static readonly DateOnly Day=new(2026,10,13);
    private const string Sse="""{"sqlId":"COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C","actionErrors":[],"result":[{"PRODUCT_CODE":"01","TRADE_DATE":"20261013","TRADE_AMT":"5471.02","TRADE_VOL":"441.21"},{"PRODUCT_CODE":"17","TRADE_DATE":"20261013","TRADE_AMT":"7804.23","TRADE_VOL":"484.13"}]}""";
    private const string Szse="""[{"metadata":{"catalogid":"scsj_gprdgk_after","tabkey":"tab1","cols":{"gp":"深市合计"},"conditions":[{"name":"txtQueryDate","defaultValue":"2026-10-13"}]},"data":[{"zbmc":"成交金额（亿元）","gp":"8,976.31"},{"zbmc":"成交量（亿）","gp":"562.44"}],"error":null}]""";
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS 市场温度："+name);}
    private static void Reject(Action action,string name){try{action();}catch{Console.WriteLine("PASS 市场温度："+name);return;}throw new Exception(name);}
    public static void Run(string root)
    {
        var sse=OfficialMarketActivitySource.Parse("SSE",Day,Encoding.UTF8.GetBytes(Sse))!;
        var szse=OfficialMarketActivitySource.Parse("SZSE",Day,Encoding.UTF8.GetBytes(Szse))!;
        Check(sse.AmountYuan==780423000000m && szse.VolumeShares==56244000000m,"官方分类只取总计一次，单位换算准确");
        Reject(()=>OfficialMarketActivitySource.Parse("SSE",Day.AddDays(1),Encoding.UTF8.GetBytes(Sse)),"沪市旧日期拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SZSE",Day.AddDays(1),Encoding.UTF8.GetBytes(Szse)),"深市旧日期拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SZSE",Day,Encoding.UTF8.GetBytes(Szse.Replace("亿元","万元"))),"成交金额单位改变拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SZSE",Day,Encoding.UTF8.GetBytes(Szse.Replace("8,976.31","89,76.31"))),"错误千分位拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SSE",Day,Encoding.UTF8.GetBytes(Sse.Replace("7804.23","-1"))),"负数成交拒绝");
        var raw=new RawEvidenceStore(Path.Combine(root,"activity-raw"));
        using var repository=TestMarketData.Open(Path.Combine(root,"activity-data"),raw);
        MarketActivitySource Source(string market,string content,MarketActivityObservation observation)=>new(OfficialMarketActivitySource.Request(market,Day),Encoding.UTF8.GetBytes(content),market,Day,observation);
        var a=Source("SSE",Sse,sse);var b=Source("SZSE",Szse,szse);
        repository.SaveMarketActivity("SSE",Day,a);
        Check(repository.MarketActivity().Single() is {AmountYuan:null,VolumeShares:null},"单市场不冒充沪深总额");
        repository.SaveMarketActivity("SZSE",Day,b);repository.SaveMarketActivity("SSE",Day,a);
        Check(repository.MarketActivity().Single() is {AmountYuan:1678054000000m,VolumeShares:104657000000m},"两市同日总额与成交量，重复采集不重复计算");
        repository.SaveMarketActivity("SSE",Day,null);
        Check(repository.MarketActivity().Single().AmountYuan==1678054000000m,"后续请求失败保留已核验事实");
        repository.RecordMarketActivityCursor(Day);Check(repository.ReadMarketActivityCursor()==Day && repository.ReadSyncCursor() is null,"成交游标与ETF游标独立");
        var dates=Enumerable.Range(0,22).Select(i=>new MarketActivityPoint(Day.AddDays(i),100m+i,1000,"Complete")).ToArray();
        var metrics=MarketActivityMetrics.Calculate(dates);
        Check(metrics[19].Mean20 is null && metrics[20] is {Mean20:109.5m,Mean5:117m} && metrics[20].ActivityRatio==120m/109.5m,"前20条记录均值不含当日，样本不足不填充");
        var gap=MarketActivityMetrics.Calculate(dates.Select((x,i)=>i==19?x with{State="Incomplete",AmountYuan=null}:x).ToArray());
        Check(gap[20].Mean5 is null && gap[21].Mean20 is null,"已知缺口中断均值窗口");
        Check(MarketActivityMetrics.Calculate(Enumerable.Range(0,21).Select(i=>new MarketActivityPoint(Day.AddDays(i),0,0,"Complete")).ToArray())[^1].ActivityRatio is null,"零基准不除零");
        File.WriteAllText(Path.Combine(root,"activity-raw",Convert.ToHexString(SHA256.HashData(a.Content))+".raw"),"tampered");
        Check(repository.MarketActivity().Single().AmountYuan is null,"官方原件损坏阻断总额");
        var conflictRaw=new RawEvidenceStore(Path.Combine(root,"activity-conflict-raw"));
        using var conflict=TestMarketData.Open(Path.Combine(root,"activity-conflict-data"),conflictRaw);
        conflict.SaveMarketActivity("SSE",Day,a);conflict.SaveMarketActivity("SZSE",Day,b);
        conflict.SaveMarketActivity("SSE",Day,Source("SSE",Sse.Replace("7804.23","7805.23"),sse with{AmountYuan=sse.AmountYuan+100000000m}));
        Check(conflict.MarketActivity().Single().AmountYuan is null,"官方不同值修订保留并阻断冲突日");
    }
    public static async Task RunLive(string root)
    {
        using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,"policy"),RandomNumberGenerator.GetBytes(32)));
        var source=new OfficialMarketActivitySource(gateway);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        foreach(var market in new[]{"SSE","SZSE"})
        {
            var result=await source.FetchAsync(market,Day,default);
            Check(result.Observation is not null,"实际官网日期和字段解析 "+market);
            repository.SaveMarketActivity(market,Day,result);
        }
        var point=repository.MarketActivity().Single();
        Check(point is {AmountYuan:1678054000000m,VolumeShares:104657000000m,ActivityRatio:null},"2026-10-13实际两市股票统计汇总与原件存证，未伪造20记录均值");
        Console.WriteLine("只读官网样本核验完成，隔离证据目录："+root);
    }
}
