using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class DashboardProfileChecks
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.DashboardProfile",Guid.NewGuid().ToString("N"));var rawPath=Path.Combine(root,"raw");var reads=0;var cancelMid=false;
        using var stop=new CancellationTokenSource();
        var raw=new RawEvidenceStore(rawPath,openRead:path=>{Interlocked.Increment(ref reads);if(cancelMid)stop.Cancel();return File.OpenRead(path);});
        using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw);var bytes=Encoding.UTF8.GetBytes("synthetic shared calendar and share evidence; no source acceptance");
        var hash=raw.Save(bytes);var fetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-profile");var date=new DateOnly(2026,10,23);
        repo.ImportCalendar(fetch,[("SSE",date),("SZSE",date)],default);repo.ImportShares(fetch,[new(new("SSE","510300"),date,100,"万份")],default);
        void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 仪表盘分段："+label);}
        var expected=repo.ExecuteOperation(()=>repo.Dashboard(null),default);var before=reads;var profile=repo.ExecuteOperation(()=>repo.ProfileDashboard(null),default);
        Check(JsonSerializer.Serialize(profile.Dashboard)==JsonSerializer.Serialize(expected),"诊断与正常查询金融结果完整一致");
        Check(reads>before,"诊断重新核验原件，不复用前一查询健康结论");
        var names=new[]{"EvidenceSql","RawVerificationIncludingAdmission","EvidenceClassification","PointsSql","OverviewSqlAndProjection","HistorySqlAndProjection","ActivitySqlAndProjection"};
        Check(names.All(name=>profile.Stages.Any(x=>x.Name==name))&&profile.Stages.Select(x=>x.Name).Distinct().Count()==profile.Stages.Count
            &&profile.Stages.All(x=>x.ElapsedMs>=0&&double.IsFinite(x.ElapsedMs)&&x.Items>=0),"有限阶段包含SQL、含准入的原件核验和投影，计数不是金融值");
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"damaged");
        try{repo.ExecuteOperation(()=>repo.ProfileDashboard(null),default);throw new Exception("诊断绕过损坏。");}catch(InvalidDataException){}
        File.WriteAllBytes(Path.Combine(rawPath,hash+".raw"),bytes);
        before=reads;
        Check(JsonSerializer.Serialize(repo.ExecuteOperation(()=>repo.ProfileDashboard(null),default).Dashboard)==JsonSerializer.Serialize(expected)&&reads>before,
            "损坏失败关闭且释放诊断作用域，后续重新核验并返回相同完整结果");
        cancelMid=true;
        try{repo.ExecuteOperation(()=>repo.ProfileDashboard(null),stop.Token);throw new Exception("诊断未取消。");}catch(OperationCanceledException){}
        cancelMid=false;
        Check(JsonSerializer.Serialize(repo.ExecuteOperation(()=>repo.ProfileDashboard(null),default).Dashboard)==JsonSerializer.Serialize(expected),"在原件读取中取消后释放作用域与事务，不留下部分金融结果");
    }
}
