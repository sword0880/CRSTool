using System.Diagnostics;
using System.Text.Json;
using CRS.Domain.Performance;

internal static class PerformanceRegression
{
    internal sealed record Case(PerformanceData Input, PerformanceRequest Request, PerformanceResult Result);
    private static readonly List<Case> captured=[];
    internal static PerformanceResult Calculate(PerformanceData input, PerformanceRequest request)
    {
        var result=PerformanceCalculator.Calculate(input,request);
        captured.Add(new(input,request,result));
        return result;
    }
    internal static void Verify(string? capturePath)
    {
        if(capturePath is not null)
        {
            File.WriteAllText(capturePath,JsonSerializer.Serialize(captured));
            Console.WriteLine($"已冻结 {captured.Count} 个完整绩效输入/输出案例。");
            return;
        }
        var expected=JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","performance-v1.json")))!;
        if(expected.Length!=captured.Count)throw new Exception("绩效冻结案例数量改变，请审查新增/遗漏的案例。");
        for(var i=0;i<expected.Length;i++)
        {
            var actual=PerformanceCalculator.Calculate(expected[i].Input,expected[i].Request);
            if(JsonSerializer.Serialize(actual)!=JsonSerializer.Serialize(expected[i].Result))
                throw new Exception($"绩效冻结案例 {i+1} 发生逐字段差异（含质量、解释及来源）。");
        }
        Console.WriteLine($"绩效冻结回归通过：{expected.Length} 个案例逐字段一致。");
    }
    internal static void Benchmark()
    {
        var from=new DateOnly(2025,1,1);var to=from.AddDays(99);
        var source=new PerformanceSource("SYNTHETIC-BENCHMARK","generated");
        var accounts=Enumerable.Range(0,20).Select(i=>new InvestmentAccount($"A{i}","SYNTHETIC",$"合成{i}","USD",from)).ToArray();
        var navs=accounts.SelectMany(a=>Enumerable.Range(0,100).Select(i=>
            new ValuationSnapshot(a.Id+":"+i,a.Id,new(from.AddDays(i).ToDateTime(new(23,0)),TimeSpan.Zero),from.AddDays(i),ValuationMoment.EndOfDay,
                [new("Cash","USD",10000+i),new("Security","USD",null,"DEMO","US",10)],source,PerformanceQuality.Verified))).ToArray();
        var data=new PerformanceData {Accounts=accounts,Valuations=navs,
            Events=Enumerable.Range(0,10000).Select(i=>new InvestmentEvent("E"+i,accounts[i%20].Id,new(from.AddDays(i%100).ToDateTime(new(12,0)),TimeSpan.Zero),from.AddDays(i%100),InvestmentEventKind.Fee,0,"USD",source,Confirmed:true,Quality:PerformanceQuality.Verified)).ToArray(),
            Prices=Enumerable.Range(0,30000).Select(i=>new SecurityPrice("P"+i,i<100?"DEMO":"OTHER"+i,"US","USD",from.AddDays(i%100),10,source,PerformanceQuality.Verified)).ToArray(),
            Fx=Enumerable.Range(0,30000).Select(i=>new PerformanceFx("F"+i,"USD","CNY",from.AddDays(i%100),7,source,PerformanceQuality.Verified)).ToArray(),
            Coverage=accounts.Select(a=>new PerformanceCoverage(a.Id,from,to,true,source)).ToArray()};
        var request=new PerformanceRequest(accounts.Select(a=>a.Id).ToArray(),from,to,"CNY",ReturnMethod.EndOfDayReconstructed);
        _=PerformanceCalculator.Calculate(data,request);
        var times=new List<double>();long allocated=0;
        for(var i=0;i<3;i++)
        {
            var before=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();
            _=PerformanceCalculator.Calculate(data,request);watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);allocated+=GC.GetTotalAllocatedBytes(true)-before;
        }
        Console.WriteLine(JsonSerializer.Serialize(new {Accounts=20,Valuations=2000,Events=10000,Prices=30000,Fx=30000,MedianMs=times.Order().ElementAt(1),AllocatedBytes=allocated/3,PeakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64,Runtime=Environment.Version.ToString(),CPU=Environment.ProcessorCount}));
    }
}
