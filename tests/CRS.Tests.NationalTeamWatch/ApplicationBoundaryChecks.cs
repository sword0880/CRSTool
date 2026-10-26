using System.Reflection;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>验收后台用例与客户端合同、初始化失败释放及共享会话生命周期。</summary>
internal static class ApplicationBoundaryChecks
{
    internal static async Task RunAsync(string root)
    {
        var ports=typeof(INationalTeamWorkspace).GetInterfaces();
        Check(ports.SelectMany(x=>x.GetMethods()).All(x=>x.IsAbstract),"界面端口全部要求显式实现，不含默认抛错或忽略筛选的回退");
        var types=new[]{typeof(NationalTeamApplication),typeof(EtfUseCases),typeof(EquityUseCases),typeof(ExitAlertUseCases),
            typeof(IndustryPeUseCases),typeof(IndexSeriesUseCases),typeof(MarketActivityUseCases),typeof(NavUseCases),typeof(EvidenceUseCases)};
        Check(types.All(t=>!t.GetInterfaces().Intersect(ports.Append(typeof(INationalTeamWorkspace))).Any()),"后台组合根与八组用例不实现客户端合同");
        Check(types.Skip(1).SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            .All(m=>m.Name is not ("AlertChannelsAsync" or "QueueIndexSeriesAsync" or "QueueIndustryPeAsync")),"渠道由宿主提供，远程排队属于客户端，不以后台空集合占位");
        var opened=0;var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        await using var application=new NationalTeamApplication(new QueryOnlyContext(),()=>
        {Interlocked.Increment(ref opened);return TestMarketData.Open(Path.Combine(root,"data"),raw);},raw);
        await Reject<InvalidOperationException>(()=>application.Etf.ConfigurationAsync(default),"初始化前读取明确拒绝");
        await Reject<InvalidOperationException>(()=>application.Nav.NavRevisionsAsync(new("SZSE","159915"),new(2026,1,1),new(2026,1,2),default),"不同功能共享同一未初始化生命周期");
        await Reject<OperationCanceledException>(()=>application.InitializeAsync(new(true)),"已取消初始化不调用存储工厂");
        Check(opened==0,"取消不创建数据库或泄漏连接");
        await application.InitializeAsync(default);
        await Reject<InvalidOperationException>(()=>application.InitializeAsync(default),"重复初始化明确拒绝");
        Check(opened==1&&(await application.Etf.ConfigurationAsync(default)).Count>=24,"重复初始化不替换或泄漏既有会话");
        await application.DisposeAsync();
        await Reject<ObjectDisposedException>(()=>application.Etf.ConfigurationAsync(default),"关闭后ETF拒绝新读取");
        await Reject<ObjectDisposedException>(()=>application.Activity.MarketActivityAsync(default),"关闭后其它功能同样拒绝读取");
        await Reject<ObjectDisposedException>(()=>application.Alerts.EvaluateAlertsAsync(default),"关闭后拒绝写入而不是重新打开仓储");
        var failedRoot=Path.Combine(root,"missing-capability");
        await using var incomplete=new NationalTeamApplication(new QueryOnlyContext(),()=>
        {
            var target=TestMarketData.Open(failedRoot,raw);
            var proxy=DispatchProxy.Create<IMarketRepository,ForwardingRepositoryProxy>();
            ((ForwardingRepositoryProxy)proxy).Inner=target;return proxy;
        },raw);
        await Reject<NotSupportedException>(()=>incomplete.InitializeAsync(default),"完整基本仓储但缺独立读取能力时在应用初始化拒绝");
        using(var file=new FileStream(Path.Combine(failedRoot,"market.db"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Check(file.Length>0,"初始化能力核验失败释放写连接，保留资料库");
        Console.WriteLine("PASS 后台应用边界：14项，合成数据库与本地会话，不代表已安装服务验收。");
    }
    private static async Task Reject<T>(Func<Task> action,string message) where T:Exception
    {
        try{await action();throw new Exception("应当拒绝："+message);}
        catch(T){Check(true,message);}
    }
    private static void Check(bool condition,string message)
    {if(!condition)throw new Exception(message);Console.WriteLine("PASS 后台应用边界："+message);}
}
