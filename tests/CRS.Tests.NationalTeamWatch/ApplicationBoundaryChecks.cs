using System.Reflection;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>验收后台用例与客户端合同、初始化失败释放及共享会话生命周期。</summary>
internal static class ApplicationBoundaryChecks
{
    private static int passed;
    internal static async Task RunAsync(string root)
    {
        passed=0;
        var reads=new[]{"AlertDocuments","AlertMetadata","NamedEtfHoldings","EtfHolderDocument"};
        var writes=new[]{"RegisterAlertDocument","ReviewAlertMetadata","ReviewNamedEtfHolding"};
        Check(reads.All(name=>typeof(IExitAlertReadRepository).GetMethod(name) is null)
            &&reads.All(name=>typeof(IEtfDocumentReadRepository).GetMethod(name) is not null||typeof(IEtfHoldingEvidenceReadRepository).GetMethod(name) is not null),"ETF证据读取只由ETF窄端口声明，预警读取不重复拥有");
        Check(writes.All(name=>typeof(IExitAlertWriteRepository).GetMethod(name) is null)
            &&writes.All(name=>typeof(IEtfDocumentWriteRepository).GetMethod(name) is not null||typeof(IEtfHoldingEvidenceWriteRepository).GetMethod(name) is not null),"ETF原件与持仓复核写入独立于预警评价及通知租约");
        var remote=reads.Where(name=>name!="EtfHolderDocument").Concat(writes).Append("EtfHolderCandidates").Select(name=>name+"Async").ToArray();
        Check(remote.All(name=>typeof(IExitAlertWorkspace).GetMethod(name) is null&&typeof(IEtfHoldingsWorkspace).GetMethod(name) is not null),"远程ETF持仓合同独立，旧通信操作名不影响功能归属");
        Check(remote.All(name=>typeof(ExitAlertUseCases).GetMethod(name) is null)
            &&typeof(ExitAlertUseCases).GetFields(BindingFlags.NonPublic|BindingFlags.Instance).All(field=>field.FieldType!=typeof(IEtfHolderCandidateParser)),"预警用例不再持有ETF候选解析器或持仓读写方法");
        Check(remote.All(name=>typeof(EtfHoldingsUseCases).GetMethods().Any(method=>method.Name==name)),"候选解析、口径审核和具名持仓全部进入ETF持仓用例");
        Check(typeof(CRS.Plugins.NationalTeamWatch.Presentation.EtfHoldingsViewModel).GetConstructors().SelectMany(ctor=>ctor.GetParameters()).All(parameter=>parameter.ParameterType!=typeof(IExitAlertWorkspace)),"ETF持仓页面不再借用预警界面端口");
        var ports=typeof(INationalTeamWorkspace).GetInterfaces();
        Check(ports.SelectMany(x=>x.GetMethods()).All(x=>x.IsAbstract),"界面端口全部要求显式实现，不含默认抛错或忽略筛选的回退");
        var types=new[]{typeof(NationalTeamApplication),typeof(EtfUseCases),typeof(EquityUseCases),typeof(ExitAlertUseCases),
            typeof(IndustryPeUseCases),typeof(IndexSeriesUseCases),typeof(MarketActivityUseCases),typeof(NavUseCases),typeof(EvidenceUseCases),typeof(EtfHoldingsUseCases),typeof(MarketCycleStudyUseCases)};
        Check(types.All(t=>!t.GetInterfaces().Intersect(ports.Append(typeof(INationalTeamWorkspace))).Any()),"后台组合根与十组用例不实现客户端合同");
        Check(types.Skip(1).SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            .All(m=>m.Name is not ("AlertChannelsAsync" or "QueueIndexSeriesAsync" or "QueueIndustryPeAsync")),"渠道由宿主提供，远程排队属于客户端，不以后台空集合占位");
        var opened=0;var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        await using var application=CRS.Plugins.NationalTeamWatch.Infrastructure.NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>
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
        await using var incomplete=CRS.Plugins.NationalTeamWatch.Infrastructure.NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>
        {
            var target=TestMarketData.Open(failedRoot,raw);
            var proxy=DispatchProxy.Create<IMarketRepository,ForwardingRepositoryProxy>();
            ((ForwardingRepositoryProxy)proxy).Inner=target;return proxy;
        },raw);
        await Reject<NotSupportedException>(()=>incomplete.InitializeAsync(default),"完整基本仓储但缺独立读取能力时在应用初始化拒绝");
        using(var file=new FileStream(Path.Combine(failedRoot,"market.db"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Check(file.Length>0,"初始化能力核验失败释放写连接，保留资料库");
        Console.WriteLine($"PASS 后台应用边界：{passed}项，合成数据库与本地会话，不代表已安装服务验收。");
    }
    private static async Task Reject<T>(Func<Task> action,string message) where T:Exception
    {
        try{await action();throw new Exception("应当拒绝："+message);}
        catch(T){Check(true,message);}
    }
    private static void Check(bool condition,string message)
    {if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS 后台应用边界："+message);}
}
