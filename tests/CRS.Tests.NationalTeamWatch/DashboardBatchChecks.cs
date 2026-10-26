using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class DashboardBatchChecks
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.DashboardBatch",Guid.NewGuid().ToString("N"));var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);
        using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw);var first=new DateOnly(2026,10,22);var last=first.AddDays(1);
        var bytes=Encoding.UTF8.GetBytes("synthetic multi-market shares and calendar");var hash=raw.Save(bytes);var fetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-batch");
        repo.ImportCalendar(fetch,[("SSE",first),("SSE",last),("SZSE",first),("SZSE",last)],default);
        // 超过128个目标以覆盖多批，并保留24只内置但缺资料的证券，禁止把缺口分母缩成已有样本。
        var keys=Enumerable.Range(0,130).Select(i=>new EtfKey(i%2==0?"SSE":"SZSE",(600000+i).ToString())).ToArray();
        foreach(var key in keys)repo.SetMembership(key,"synthetic","batch",true,"fixture");
        var cross=new EtfKey("SZSE",keys[0].Ticker);repo.SetMembership(cross,"cross-market","batch",true,"fixture");
        var inactive=new EtfKey("SSE","699999");repo.SetMembership(inactive,"inactive selection","batch",true,"fixture seed before stopping");
        var all=keys.Append(cross).Append(inactive).ToArray();
        var values=all.SelectMany((key,i)=>new[]{new ImportRow(key,last,1000+i,"份"),new ImportRow(key,first,900+i,"份")}).ToArray();
        repo.ImportShares(fetch,values,default);
        repo.SetMembership(inactive,"inactive selection","batch",false,"fixture stop after facts exist");
        var passed=0;void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 仪表盘批量："+label);}
        void Parity(EtfKey? key,string label)
        {
            // 参考由原有逐证券独立查询组成，与批量SQL没有共享实现。
            var expected=new EtfDashboard(repo.Overview(),repo.History(key),repo.MarketActivity());var actual=repo.ExecuteOperation(()=>repo.Dashboard(key),default);
            Check(JsonSerializer.Serialize(expected)==JsonSerializer.Serialize(actual),label);
        }
        Parity(null,"跨128目标边界仍完整，缺资料内置证券保留原覆盖分母");
        Parity(keys[0],"多市场同代码的选中历史不串线，日期与修订顺序相同");
        Parity(cross,"另一市场同代码有独立股数和证据身份");
        Parity(inactive,"停用但显式选择的证券仍纳入读取，不加入汇总监控池");
        var otherBytes=Encoding.UTF8.GetBytes("synthetic unreviewed revision");var otherHash=raw.Save(otherBytes);var otherFetch=repo.RecordFetch(otherHash,"https://www.sse.com.cn/synthetic-batch-pending");
        repo.ImportShares(otherFetch,[new(keys[0],last,9000,"份")],default);
        Parity(keys[0],"未审新修订仍阻断当日有效份额，不静默回退旧值");
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"damaged");
        try{repo.ExecuteOperation(()=>repo.Dashboard(null),default);throw new Exception("批量读取绕过原件损坏。");}catch(InvalidDataException){}
        repo.ReconcileEvidenceIntegrity();Parity(null,"已确认Q15仍按原口径过滤，未将损坏份额作为零");
        var beforeCancel=JsonSerializer.Serialize(repo.ExecuteOperation(()=>repo.Dashboard(inactive),default));
        using var stop=new CancellationTokenSource();stop.Cancel();try{repo.ExecuteOperation(()=>repo.Dashboard(null),stop.Token);throw new Exception("取消未生效。");}catch(OperationCanceledException){}
        Check(JsonSerializer.Serialize(repo.ExecuteOperation(()=>repo.Dashboard(inactive),default))==beforeCancel,"取消后可再查询，批量未留下未释放事务或半份结果");
        Console.WriteLine($"PASS 仪表盘批量合计{passed}；全为隔离合成资料，不代表正式性能。");
    }
}
