using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using Microsoft.Data.Sqlite;

// 子客户端用于验证桌面进程退出后，服务进程仍能按真实 Quartz 时钟采集。
if(args is ["--source-budget"]){await SourceBudgetChecks.RunAsync();return;}
if(args is ["--source-budget-soak",var soakSeconds]){await SourceBudgetSoakChecks.RunAsync(int.Parse(soakSeconds,System.Globalization.CultureInfo.InvariantCulture));return;}
if(args is ["--source-policy"]){await SourcePolicyChecks.RunAsync();return;}
if(args is ["--enable-reference-live"])
{
    var live=new WorkerEtfWorkspace();
    var keys=CRS.Plugins.NationalTeamWatch.Domain.ReferenceEtfDirectory.Entries.Select(x=>(x.Market,x.Ticker)).Append(("SSE","588000")).ToHashSet();
    foreach(var row in (await live.ConfigurationAsync(default)).Where(x=>keys.Contains((x.Market,x.Ticker)) && !x.Active))
    {
        Console.WriteLine("正在启用观察候选 "+row.Ticker);
        await live.SaveConfigurationAsync(new(row.Market,row.Ticker),row.Name,row.IndexGroup,true,"用户要求全部内置观察项目加入监控；不代表机构持仓已确认",row.Revision,default);
    }
    var enabled=(await live.ConfigurationAsync(default)).Where(x=>keys.Contains((x.Market,x.Ticker))).ToArray();
    if(enabled.Length!=24 || enabled.Any(x=>!x.Active))throw new InvalidOperationException("观察清单未全部启用。");
    Console.WriteLine("PASS 实际观察目录24只全部启用；用户自定义条目不变。");return;
}
if(args is ["--backfill-live",var fromText,var toText])
{
    var from=DateOnly.ParseExact(fromText,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
    var to=DateOnly.ParseExact(toText,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
    var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
    if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new ArgumentException("补采区间无效。");
    var live=new BackgroundPipeClient();
    foreach(var job in new[]{"official-share-sync","market-activity-sync"})
    {
        var deadline=DateTimeOffset.UtcNow.AddMinutes(2);
        while(true)
        {
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team",job,Payload:JsonSerializer.Serialize(new EtfRequest("sync",From:from,To:to)));
            var response=await live.SendAsync(request);
            if(response.Code is "AlreadyRunning" or "RateLimited" && DateTimeOffset.UtcNow<deadline)
            {Console.WriteLine(job+"：等待当前采集/限流后提交指定历史区间。");await Task.Delay(5000);continue;}
            Console.WriteLine(JsonSerializer.Serialize(new{Job=job,From=from,To=to,RequestId=request.RequestId,response.Code,response.Receipt}));
            if(response.Code is not ("Accepted" or "Succeeded" or "RetryDeferred"))Environment.ExitCode=1;
            break;
        }
    }
    return;
}
if(args is ["--installed-chart-ui"]){EtfConfigurationUiChecks.RunInstalled();return;}
if(args is ["--installed-chart-probe"])
{
    var installedClient=new BackgroundPipeClient();
    foreach(var query in new[]{new EtfRequest("overview"),new EtfRequest("history","SSE","510300"),new EtfRequest("history"),new EtfRequest("market-activity")})
    {
        var response=await installedClient.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:JsonSerializer.Serialize(query)));
        Console.WriteLine($"{query.Operation}:{query.Ticker??"all"}={response.Code}");
        if(response.Code!="Ok")Environment.ExitCode=1;
    }
    if(Environment.ExitCode==0)
    {
        var reader=new WorkerEtfWorkspace(installedClient);var overview=await reader.OverviewAsync(default);
        foreach(var row in overview)
        {
            var points=await reader.HistoryAsync(new(row.Market,row.Ticker),default);
            Console.WriteLine($"PASS 已安装服务ETF历史 {row.Market}:{row.Ticker} rows={points.Count};from={points.FirstOrDefault()?.Date};to={points.LastOrDefault()?.Date}");
        }
        Console.WriteLine($"PASS 已安装服务全池 rows={(await reader.HistoryAsync(null,default)).Count};成交额 rows={(await reader.MarketActivityAsync(default)).Count}");
    }
    return;
}
var chartOnly=args is ["--chart-ui",_];if(chartOnly)args=[args[1]];
if(args is ["--client",var childPipe])
{var reader=new WorkerEtfWorkspace(new(childPipe));if((await reader.OverviewAsync(default)).Count<5)Environment.ExitCode=2;return;}
if(args.Length!=1)throw new ArgumentException("需要本轮完整 Worker 发行目录。");
var package=Path.GetFullPath(args[0]);var root=Path.Combine(Path.GetTempPath(),"CRS.Worker.Etf",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team","2.0.0");Directory.CreateDirectory(target);
foreach(var file in Directory.EnumerateFiles(Path.Combine(package,"Plugins","crs.national-team","2.0.0"),"*",SearchOption.AllDirectories))
{var destination=Path.Combine(target,Path.GetRelativePath(Path.Combine(package,"Plugins","crs.national-team","2.0.0"),file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new BackgroundEntry("CRS.Worker.EtfFixture.dll","CRS.Worker.EtfFixture.Entry"));
File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(x=>Path.GetRelativePath(target,x).Replace('\\','/'),x=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new TrustedPackage("crs.national-team","2.0.0","crs.national-team/2.0.0",hashes)}));
var data=Path.Combine(root,"service");var pipe="CRS.Worker.Etf."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);var ui=new WorkerEtfWorkspace(client);int passed=0;
// 与正式客户端相同地处理查询限流，测试不能把限流误当作空历史。
async Task<BackgroundResponse> Send(BackgroundRequest request)
{
    while(true){var response=await client.SendAsync(request);if(response.Code!="RateLimited")return response;await Task.Delay(500);}
}
Task<BackgroundResponse> Query(string command)=>Send(new(WorkerProtocol.Version,Guid.NewGuid(),command));
void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS ETF服务化："+name);}
async Task Until(Func<Task<bool>> condition)
{var until=DateTimeOffset.UtcNow.AddSeconds(35);while(!await condition()){if(DateTimeOffset.UtcNow>until)throw new TimeoutException("ETF服务化等待超时。");await Task.Delay(100);}}
BackgroundRequest Request(string command,string job="official-share-sync",string? payload=null,long? revision=null)=>new(WorkerProtocol.Version,Guid.NewGuid(),command,"crs.national-team",job,ExpectedRevision:revision,Payload:payload);
Process Start()
{
    var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
    foreach(var value in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(value);
    var process=Process.Start(info)!;process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();return process;
}
using(var service=Start())
{
    try
    {
        var health=await Query("GetHealth");Check(health.Health?.State=="Running","真实独立服务加载ETF后台入口");
        if(chartOnly)
        {
            await Until(async()=>(await ui.OverviewAsync(default)).All(x=>x.Shares>0) && (await ui.MarketActivityAsync(default)).Count(x=>x.State=="Complete")>=21);
            EtfConfigurationUiChecks.Run(client,Path.Combine(Environment.CurrentDirectory,"artifacts","etf-configuration-ui","configuration.png"));return;
        }
        // 新增独立预警启动任务后，任意成功不再代表份额批次完成；比较两个查询前先等待目标采集提交。
        await Until(async()=>(await Query("GetHistory")).History!.Any(x=>x.JobId=="official-share-sync"&&x.Result=="Succeeded"));
        Check((await ui.OverviewAsync(default)).Count==5 && (await ui.OverviewAsync(default)).All(x=>x.Shares>0),"启动后自动采集两市五只ETF并经管道查询");
        await Until(async()=>(await ui.MarketActivityAsync(default)).Any(x=>x.State=="Complete"));
        Check((await ui.MarketActivityAsync(default)).Where(x=>x.State=="Complete").All(x=>x.AmountYuan==1678054000000m && x.VolumeShares==104657000000m),"独立成交任务自动采集两市并通过真实管道返回统一单位总额");
        var basketHistory=await ui.HistoryAsync(null,default);
        Check(basketHistory.Count>0 && basketHistory.All(x=>x.Total==5 && x.Covered==5 && x.Shares>0 && x.DeltaShares is null),"全部ETF历史经真实分页返回同日总和，无日历不计算净变化");
        var singleHistory=await ui.HistoryAsync(new("SSE","510300"),default);
        Check(singleHistory.Count==basketHistory.Count && singleHistory.All(x=>x.Total==1 && x.Covered==1 && x.Shares>0),"单只ETF历史使用独立口径");
        var acceptanceThrough=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime).AddDays(-1);
        var acceptance=await ui.AcceptanceAsync(acceptanceThrough,default);
        Check(acceptance.Count==5&&acceptance.All(x=>!x.DataAcceptancePassed&&!x.CalendarVerified),"生产验收查询经真实管道返回，无日历时不得批准250日或连续20日");
        var attempts=await ui.CollectionAttemptsAsync(new("SSE","510300"),acceptanceThrough.AddDays(-8),acceptanceThrough.AddDays(1),default);
        Check(attempts.Any(x=>x.State=="Accepted"&&x.OriginalHash is not null&&x.ParserVersion=="sse-etf-json-v1"),"采集尝试经真实分页管道返回来源版本与原件关联");
        Check((await ui.EquityEventsAsync(new("SSE","600036"),default)).Count==0,"历次持股变动经真实只读管道返回，未采集不伪造事件");
        Check((await ui.EquityReportsAsync(new("SSE","600036"),default)).Count==0,"未检索前官方报告目录留空");
        // 预警与通知经真实服务管道，未配置邮件不联系外部提供商，也不能越过真实数据门槛。
        var alertOverview=await ui.AlertOverviewAsync(default);var alertRules=await ui.AlertConfigurationAsync(default);
        Check(!alertOverview.DataAcceptancePassed&&alertRules.Routes.Single().ChannelId=="email","预警真实管道明确研究门槛及独立渠道订阅");
        Check((await ui.AlertChannelsAsync(default)).Single(x=>x.Id=="email") is {Enabled:false,Configured:false},"邮件默认关闭，业务页面不读取SMTP凭据");
        await ui.EvaluateAlertsAsync(default);
        var alerts=await ui.AlertEventsAsync(new("DataQuality"),default);
        Check(alerts.Count>0&&alerts.All(x=>x.Lane==CRS.Plugins.NationalTeamWatch.Domain.AlertLane.DataQuality),"无完整日历时独立形成数据核查，不伪造金融信号");
        var alert=await ui.AlertEventAsync(alerts[0].Id,default);
        Check(alert.Id==alerts[0].Id&&(await ui.AlertChangesAsync(alert.Id,default)).Count>0,"预警详情和变更记录由服务分页提供");
        Check((await ui.AlertEventsAsync(new("EtfOutflow"),default)).Count==0&&(await ui.AlertOutboxAsync(default)).All(x=>x.ChannelId=="email"&&x.State!="Accepted"),"无可比性原件不生成流出结论，默认关闭邮件不出现受理假象");
        var reportHold=Path.Combine(data,"plugins","crs.national-team","http-hold.fixture");File.WriteAllText(reportHold,"report query hold");
        try
        {
            var reportReceipt=await ui.QueueEquityReportsAsync(new("SSE","600036"),default);
            Check(reportReceipt.State is "Pending" or "Running" or "Completed","官方报告检索持久受理后立即返回，不等待来源冷却");
            var repeatedReport=await ui.QueueEquityReportsAsync(new("SSE","600036"),default);
            Check(repeatedReport.RequestId==reportReceipt.RequestId,"重复待执行检索沿用请求ID，避免重复占用来源额度");
        }
        finally{File.Delete(reportHold);}
        try{await Until(async()=>(await ui.EquityReportsAsync(new("SSE","600036"),default)).Count==1);}
        catch
        {
            var diagnostic=await Query("GetHistory");Console.WriteLine(JsonSerializer.Serialize(diagnostic.History));
            foreach(var run in diagnostic.History?.Where(x=>x.JobId=="equity-report-search")??[])Console.WriteLine(JsonSerializer.Serialize((await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:run.ExecutionId))).Execution));
            throw;
        }
        Check((await ui.EquityReportsAsync(new("SSE","600036"),default)).Single() is {State:"Indexed",Title:"合成2026年半年度报告"},"官方目录通过后台分页与只读管道提供，尚未确认持仓");
        var first=(await Query("GetHistory")).History!.First(x=>x.PluginId=="crs.national-team" && x.JobId=="official-share-sync" && x.Result=="Succeeded");
        var firstLog=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:first.ExecutionId));
        Check(firstLog.Execution?.Progress is{Count:>0} stages && stages.Any(x=>x.Detail.Stage=="SourceParsed") && stages.Any(x=>x.Detail.Stage=="DayCommitted") && stages[^1].Detail.Stage=="RangeCompleted","真实管道提供来源解析、事实提交及区间汇总阶段");
        var assemblies=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:"fixture-assemblies"));
        var loaded=JsonSerializer.Deserialize<string[]>(assemblies.Payload!)!;
        Check(!loaded.Any(x=>x.StartsWith("Presentation",StringComparison.Ordinal)||x is "CRS.Plugins.NationalTeamWatch" or "CRS.Infrastructure" or "CRS.Security"),"服务不加载WPF、UI插件或私人税务程序集");
        var market=Path.Combine(data,"plugins","crs.national-team","data","market.db");
        using(var file=new FileStream(market,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {var header=new byte[16];file.ReadExactly(header);Check(!System.Text.Encoding.ASCII.GetString(header).StartsWith("SQLite format"),"市场库文件头为SQLCipher密文");}
        var plainRejected=false;
        try{using var raw=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=market,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());raw.Open();using var command=raw.CreateCommand();command.CommandText="SELECT count(*) FROM security_master";command.ExecuteScalar();}
        catch(SqliteException){plainRejected=true;}Check(plainRejected,"未提供独立密钥不能读取市场库");
        var forbiddenQuery=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:JsonSerializer.Serialize(new EtfRequest("review"))));
        Check(forbiddenQuery.Code=="UnknownQuery","只读业务端点拒绝审核写操作");
        Check((await Send(Request("Resume","review",revision:1))).Code=="ManualOnly","审核入口不能被启用为无参数定时任务");
        await ui.MembershipAsync(new("SSE","510900"),"合成池成员","TEST",true,"服务化测试",default);
        Check((await ui.OverviewAsync(default)).Count==6,"桌面验证池变更由服务持久执行");
        Check((await ui.HistoryAsync(null,default)).All(x=>x.Total==6 && x.Covered==5 && x.Shares is null && x.DeltaShares is null),"新增无数据成员后汇总留空，不沿用旧篮子或填零");
        var evidence=await ui.EvidenceAsync(default);Check(evidence.Count==40 && evidence.Select(x=>x.RevisionId).Distinct().Count()==40,"多页证据快照完整且无重复");
        // 真实管道校验证券过滤发生在服务端分页之前，避免全池历史增长后证据页不可用。
        var selectedEvidence=await ui.EvidenceAsync(new("SSE","510300"),default);
        Check(selectedEvidence.Count==8&&selectedEvidence.All(x=>x.Market=="SSE"&&x.Ticker=="510300"),"单只ETF证据按证券过滤后分页，不混入其它ETF记录");
        var output=Path.Combine(root,"original.json");await ui.ExportEvidenceAsync(evidence[0].RevisionId,true,output,default);
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)))==evidence[0].OriginalHash,"原始证据分块导出并验证完整哈希");
        // 连续三次完整导出应释放缓存额度；保留未完成导出的有界保护。
        foreach(var row in evidence.Skip(1).Take(2))await ui.ExportEvidenceAsync(row.RevisionId,true,Path.Combine(root,row.RevisionId+".json"),default);
        Check(true,"连续三次完整原件导出不会占满服务端缓存额度");
        Check((await ui.CoverageAsync(new("SSE","510300"),new(2026,1,1),new(2026,10,15),default)).State=="NoCalendar","没有完整交易日历时保持覆盖率不可判定");
        var plan=(await Query("GetJobs")).Jobs!.Single(x=>x.JobId=="official-share-sync");
        var changed=await Send(Request("ChangeSchedule",revision:plan.Revision) with{Cron="0/2 * * * * ?",TimeZoneId="China Standard Time"});
        Check(changed.Code=="Ok","真实服务保存采集计划并重建Quartz触发器");
        var clientInfo=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};
        foreach(var value in new[]{System.Reflection.Assembly.GetExecutingAssembly().Location,"--client",pipe})clientInfo.ArgumentList.Add(value);
        using(var child=Process.Start(clientInfo)!){await child.WaitForExitAsync();Check(child.ExitCode==0 && !service.HasExited,"独立桌面客户端退出不终止后台服务");}
        await Until(async()=>(await Query("GetHistory")).History!.Count(x=>x.JobId=="official-share-sync" && x.Result=="Succeeded")>=2);
        Check(true,"桌面退出后仍由真实Quartz继续执行采集");
        plan=(await Query("GetJobs")).Jobs!.Single(x=>x.JobId=="official-share-sync");await Send(Request("Pause",revision:plan.Revision));
    }
    finally{if(!service.HasExited)service.Kill();await service.WaitForExitAsync();}
}
// 重建“业务已提交、调度结果尚未提交”的故障窗口，检验稳定命令标识在业务库内去重。
var master=ServiceKey.Open(data);
try
{
    using var scheduler=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"scheduler-config.db"),Password=Convert.ToHexString(master),Pooling=false}.ToString());scheduler.Open();
    using var change=scheduler.CreateCommand();change.CommandText="UPDATE execution SET ended=NULL,result='Running' WHERE id IN(SELECT execution FROM command_request WHERE job='watchlist'); UPDATE command_request SET state='Running',code='Running' WHERE job='watchlist';";change.ExecuteNonQuery();
}
finally{CryptographicOperations.ZeroMemory(master);}
using(var service=Start())
{
    try
    {
        await Query("GetHealth");var plans=(await Query("GetJobs")).Jobs!;
        Check(plans.Single(x=>x.JobId=="official-share-sync") is{Enabled:false,Cron:"0/2 * * * * ?"},"强杀重启后保留采集计划与暂停状态");
        Check((await ui.OverviewAsync(default)).Count==6,"重启后保留独立加密库和服务端池变更");
        Check((await ui.EvidenceAsync(default)).Count==40,"同值重复采集不增加事实修订");
        await Until(async()=>(await Query("GetHistory")).History!.Any(x=>x.JobId=="watchlist" && x.Result=="Succeeded"));
    }
    finally{if(!service.HasExited)service.Kill();await service.WaitForExitAsync();}
}
master=ServiceKey.Open(data);byte[]? marketKey=null;
try
{
    marketKey=new WorkerPluginContext("crs.national-team",data,master).OpenDataKey();
    using var market=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"plugins","crs.national-team","data","market.db"),Password=Convert.ToHexString(marketKey),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());market.Open();
    using var query=market.CreateCommand();query.CommandText="SELECT count(*) FROM watchlist_change";
    Check(Convert.ToInt32(query.ExecuteScalar())==1,"业务提交后重放命令只记录一次池变更，业务去重与事实同事务");
    query.CommandText="SELECT checked_through FROM worker_sync_cursor WHERE id=1";
    Check(query.ExecuteScalar() is string,"自动补采查询游标持久保存在独立加密市场库");
}
finally{CryptographicOperations.ZeroMemory(master);if(marketKey is not null)CryptographicOperations.ZeroMemory(marketKey);}
// 原有恢复/去重验收完成后，再在同一隔离服务验证可编辑配置，不改动用户监控池。
using(var service=Start())
{
    var hold=Path.Combine(data,"plugins","crs.national-team","http-hold.fixture");
    try
    {
        await Query("GetHealth");var configurations=await ui.ConfigurationAsync(default);
        Check(configurations.Count>=24 && configurations.Any(x=>x.Ticker=="510500" && !x.Active),"服务分页返回参考候选及停用ETF，不自动加入监控池");
        var existing=configurations.Single(x=>x.Ticker=="510900");
        File.WriteAllText(hold,"synthetic network hold");
        var heldRequest=Request("RunNow");var heldAccepted=await Send(heldRequest);
        if(heldAccepted.Code!="Accepted")throw new InvalidOperationException("采集阻塞夹具未受理："+heldAccepted.Code);
        string? heldExecution=null;
        await Until(async()=>
        {
            var state=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:heldRequest.RequestId));
            if(state.Receipt?.State=="Completed")throw new InvalidOperationException("阻塞采集提前结束："+state.Receipt.Code);
            heldExecution=state.Receipt?.ExecutionId;
            return heldExecution is not null;
        });
        // Running由宿主先提交，来源阶段随后写入；等待实际阶段，不能把这两个时刻当作原子动作。
        await Until(async()=>(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:heldExecution))).Execution?.Progress?.Any(x=>x.Detail.Stage=="SourceStarted")==true);
        Check(true,"网络尚未返回时可读取来源开始与最近进展");
        var editing=ui.SaveConfigurationAsync(new(existing.Market,existing.Ticker),"合成后台编辑名称","CONFIG",false,"采集期间修改",existing.Revision,default);
        // 宿主按插件串行写入；网络等待期间应保留命令，不能绕开统一写入门槛。
        await Task.Delay(1500);
        Check(!editing.IsCompleted,"采集期间配置命令排队，不绕过插件写入门槛");
        File.Delete(hold);
        if(await Task.WhenAny(editing,Task.Delay(20000))!=editing)throw new TimeoutException("采集结束后配置保存仍未完成。");
        await editing;
        Check((await ui.ConfigurationAsync(default)).Single(x=>x.Ticker==existing.Ticker) is{Name:"合成后台编辑名称",Active:false},"采集结束后排队配置正确保存名称、指数和监控状态");
        var rejected=false;try{await ui.SaveConfigurationAsync(new(existing.Market,existing.Ticker),"旧窗口覆盖","OLD",true,"旧修订测试",existing.Revision,default);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"管道配置写入拒绝旧修订");
        await ui.SaveConfigurationAsync(new("SSE","510902"),"合成新增配置","CONFIG",false,"自定义候选测试",-1,default);
        Check((await ui.ConfigurationAsync(default)).Any(x=>x.Ticker=="510902" && !x.Active),"配置拒绝不触发故障退避，下一次合法新增可立即完成");
        EtfConfigurationUiChecks.Run(client,Path.Combine(Environment.CurrentDirectory,"artifacts","etf-configuration-ui","configuration.png"));
        foreach(var row in (await ui.ConfigurationAsync(default)).Where(x=>x.Active))
            await ui.SaveConfigurationAsync(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"空监控池验证",row.Revision,default);
        File.WriteAllText(hold,"must not access HTTP");
        var emptyRun=await Send(Request("RunNow"));
        await Until(async()=>
        {
            var receipt=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:emptyRun.RequestId));
            return receipt.Receipt?.State=="Completed";
        });
        Check((await Query("GetHistory")).History!.Any(x=>x.Checkpoint=="monitoring=NoActiveEtfs" && x.Result=="Succeeded"),"全部停用时不请求网络，也不记录为采集故障");
    }
    finally{if(File.Exists(hold))File.Delete(hold);if(!service.HasExited)service.Kill();await service.WaitForExitAsync();}
}
Console.WriteLine($"ETF服务化通过：{passed}项；全部为隔离合成来源，不代表官网长期验收。现场：{root}");

