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
if(args is ["--source-policy"]){await SourcePolicyChecks.RunAsync();return;}
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
        await Until(async()=>(await Query("GetHistory")).History!.Any(x=>x.Result=="Succeeded"));
        Check((await ui.OverviewAsync(default)).Count==5 && (await ui.OverviewAsync(default)).All(x=>x.Shares>0),"启动后自动采集两市五只ETF并经管道查询");
        var first=(await Query("GetHistory")).History!.First(x=>x.PluginId=="crs.national-team" && x.Result=="Succeeded");
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
        var evidence=await ui.EvidenceAsync(default);Check(evidence.Count==40 && evidence.Select(x=>x.RevisionId).Distinct().Count()==40,"多页证据快照完整且无重复");
        var output=Path.Combine(root,"original.json");await ui.ExportEvidenceAsync(evidence[0].RevisionId,true,output,default);
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)))==evidence[0].OriginalHash,"原始证据分块导出并验证完整哈希");
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
        await Send(Request("RunNow"));
        await Until(async()=>(await Query("GetHistory")).History!.Any(x=>x.JobId=="official-share-sync" && x.Result=="Running"));
        var activeRun=(await Query("GetHistory")).History!.First(x=>x.JobId=="official-share-sync" && x.Result=="Running");
        // Running由宿主先提交，来源阶段随后写入；等待实际阶段，不能把这两个时刻当作原子动作。
        await Until(async()=>(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:activeRun.ExecutionId))).Execution?.Progress?.Any(x=>x.Detail.Stage=="SourceStarted")==true);
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

