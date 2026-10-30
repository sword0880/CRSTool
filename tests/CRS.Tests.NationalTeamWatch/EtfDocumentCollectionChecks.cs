using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using Microsoft.Data.Sqlite;

/// <summary>独立采集、持久幂等和版本协商反例；仅自有普通SQLite及合成公开原件，不联网。</summary>
internal static class EtfDocumentCollectionChecks
{
    private static int passed;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS 原件独立采集："+text);}
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root);var gateway=new Gateway();var context=new Context(root);
        var sse=new AlertDocumentRequest("SSE","510300","https://www.sse.com.cn/isolated-etf.pdf");
        var szse=new AlertDocumentRequest("SZSE","159919","https://disc.static.szse.cn/isolated-etf.pdf");
        string Payload(AlertDocumentRequest document,string operation=EtfDocumentCollectionProtocol.Operation)=>EtfBusinessProtocol.WriteRequest(new(operation,AlertDocument:document));
        var committedId=Guid.NewGuid();
        await using(var session=new NationalTeamBackgroundSession(context,gateway))
        {
            await session.StartAsync(default);
            foreach(var job in new[]{EtfDocumentCollectionProtocol.SseJob,EtfDocumentCollectionProtocol.SzseJob})
            {
                Check(session.DescribeJobs().Single(x=>x.JobId==job) is{ManualOnly:true,EnabledByDefault:false,RunOnStartup:false},"新入口仅持久人工采集，不新增自动计划："+job);
                var group=session.ExecutionGroup(job);Check(group.Id=="collection"&&group.MaximumConcurrency==2,"新采集沿既有两项采集预算："+job);
            }
            Check(session.ExecutionGroup("alert-manage") is{Id:"management",MaximumConcurrency:1},"管理预算保持独立单项");
            foreach(var (job,document,operation) in new[]{
                (EtfDocumentCollectionProtocol.SseJob,szse,EtfDocumentCollectionProtocol.Operation),
                (EtfDocumentCollectionProtocol.SseJob,sse with{Url=szse.Url},EtfDocumentCollectionProtocol.Operation),
                (EtfDocumentCollectionProtocol.SseJob,sse,"alert-document"),
                (EtfDocumentCollectionProtocol.SzseJob,szse with{Ticker="ABCDEF"},EtfDocumentCollectionProtocol.Operation),
                (EtfDocumentCollectionProtocol.SseJob,sse with{Ticker="999999"},EtfDocumentCollectionProtocol.Operation),
                (EtfDocumentCollectionProtocol.SseJob,sse with{Url=sse.Url+"#page=1"},EtfDocumentCollectionProtocol.Operation),
                (EtfDocumentCollectionProtocol.SseJob,sse with{Url=sse.Url+"?other=1"},EtfDocumentCollectionProtocol.Operation)})
                Check((await session.ExecuteAsync(job,Payload(document,operation),default,Guid.NewGuid())).Status=="Rejected"&&gateway.Calls==0,"错任务、市场、旧操作或未配置证券在联网前拒绝");
            Check((await session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),default)).Status=="Rejected"&&gateway.Calls==0,"独立采集必须有持久命令身份");
            Check((await session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),default,Guid.Empty)).Status=="Rejected"&&gateway.Calls==0,"空命令身份不能成为持久幂等键");
            var until=CRS.Timing.BeijingClock.Now.AddMinutes(7);
            gateway.Read=(uri,_)=>uri.Host.Contains("sse.com.cn",StringComparison.Ordinal)
                ?Task.FromException<PublicHttpResponse>(new SourceBudgetUnavailableException("SourceCooldown",until)):Task.FromResult(Pdf());
            var deferred=await session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),default,Guid.NewGuid());
            Check(deferred.Status=="RetryDeferred"&&deferred.Checkpoint!.Contains("DocumentSourceCooldown")&&deferred.RetryAfter<TimeSpan.FromMinutes(8),"沪市来源保持明确七分钟退避");
            var other=await session.ExecuteAsync(EtfDocumentCollectionProtocol.SzseJob,Payload(szse),default,Guid.NewGuid());
            Check(other is{Status:"Succeeded",Checkpoint:"document=Registered;evaluation=IndependentJob"},"沪市退避时深市原件可独立登记，不等待评价");
            async Task<List<T>> Rows<T>(string operation,string? market=null,string? ticker=null)
            {
                var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(new(operation,market,ticker)),default));
                return JsonSerializer.Deserialize<List<T>>(page.Items)!;
            }
            Check((await Rows<AlertDocument>("alert-documents","SZSE","159919")).Count==1&&(await Rows<NamedEtfHolding>("alert-holdings","SZSE","159919")).Count==0,"登记没有自动批准真实持仓");
            Check((await Rows<ExitAlertEvent>("alert-events")).Count==0&&(await Rows<AlertOutboxItem>("alert-outbox")).Count==0,"新采集没有夹带评价或通知入队");
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"data","market.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
            {db.Open();using var command=db.CreateCommand();command.CommandText="SELECT count(*) FROM nt_alert_pending";Check(Convert.ToInt32(command.ExecuteScalar())==1,"登记事务保留待评价标记");}
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Read=async(_,token)=>{entered.SetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return Pdf();};
            using(var stop=new CancellationTokenSource())
            {
                var downloading=session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),stop.Token,Guid.NewGuid());await entered.Task;
                try
                {
                    var settings=(await Rows<ExitAlertConfiguration>("alert-configuration")).Single();
                    var save=session.ExecuteAsync("alert-manage",EtfBusinessProtocol.WriteRequest(new("alert-settings",AlertConfiguration:settings with{Rules=settings.Rules with{Version="collection-isolation-test",Enabled=false}})),default,Guid.NewGuid());
                    Check(await Task.WhenAny(save,Task.Delay(5000))==save&&(await save).Status=="Succeeded","HTTP仍等待时管理设置已独立提交");
                }
                finally{stop.Cancel();try{await downloading;}catch(OperationCanceledException){}}
                Check(downloading.IsCanceled,"取消下载不停止其它管理结果");
            }
            gateway.Read=(_,_)=>Task.FromResult(Pdf());
            var collected=await session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),default,committedId);
            Check(collected.Status=="Succeeded"&&(await Rows<AlertDocument>("alert-documents","SSE","510300")).Count==1,"取消后可完整重新登记");
            var calls=gateway.Calls;gateway.Read=(_,_)=>throw new Exception("已有提交不允许再次采集。");
            var replay=await session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),default,committedId);
            Check(replay.Status=="Succeeded"&&replay.Checkpoint!.Contains("reusedCommitted=True")&&gateway.Calls==calls,"提交后相同ID恢复直接复用登记结果，无二次采集");
            var changed=await session.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse with{Url="https://www.sse.com.cn/other.pdf"}),default,committedId);
            Check(changed.Status=="Rejected"&&gateway.Calls==calls,"同ID变更原件参数在联网前失败关闭");
        }
        await using(var restarted=new NationalTeamBackgroundSession(context,new NoHttp()))
        {
            await restarted.StartAsync(default);
            var replay=await restarted.ExecuteAsync(EtfDocumentCollectionProtocol.SseJob,Payload(sse),default,committedId);
            Check(replay.Status=="Succeeded"&&replay.Checkpoint!.Contains("reusedCommitted=True"),"重建应用会话仍读取持久登记绑定，不依赖内存缓存");
        }
        await ClientContracts();Console.WriteLine($"DOCUMENT_COLLECTION passed={passed};installed=false;network=synthetic");
    }
    private static PublicHttpResponse Pdf()=>new(200,"%PDF-synthetic-independent-document"u8.ToArray(),"application/pdf");
    private static async Task ClientContracts()
    {
        var control=new ClientControl();var pipe="CRS.DocumentCollection."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            foreach(var market in new[]{"SSE","SZSE"})
            {var client=new WorkerEtfWorkspace(new(pipe));await client.RegisterAlertDocumentAsync(new(market,market=="SSE"?"510300":"159919",market=="SSE"?"https://www.sse.com.cn/fixture.pdf":"https://disc.static.szse.cn/fixture.pdf"),stop.Token);Check(control.Last is{Command:"RunNow"} request&&request.JobId==EtfDocumentCollectionProtocol.Job(market)&&EtfBusinessProtocol.ParseRequest(request.Payload!).Operation==EtfDocumentCollectionProtocol.Operation,"客户端协商后只发对应市场新采集命令："+market);}
            control.Supported=false;var before=control.Writes;
            try{await new WorkerEtfWorkspace(new(pipe)).RegisterAlertDocumentAsync(new("SSE","510300","https://www.sse.com.cn/fixture.pdf"),stop.Token);throw new Exception("旧后台被隐式写入。");}
            catch(InvalidDataException){Check(control.Writes==before,"缺少能力明确拒绝，不回退alert-manage或偷偷排队");}
            control.Supported=true;control.HoldSse=true;
            var concurrent=new WorkerEtfWorkspace(new(pipe));using var waitingStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var downloading=concurrent.RegisterAlertDocumentAsync(new("SSE","510300","https://www.sse.com.cn/held.pdf"),waitingStop.Token);
            await control.SseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                await concurrent.SaveAlertConfigurationAsync(new(new ExitAlertRules(),[],0),stop.Token);
                Check(!downloading.IsCompleted&&control.Last?.JobId=="alert-manage","同一客户端在沪市采集等待中仍可完成管理操作");
                await concurrent.RegisterAlertDocumentAsync(new("SZSE","159919","https://disc.static.szse.cn/concurrent.pdf"),stop.Token);
                Check(!downloading.IsCompleted&&control.Last?.JobId==EtfDocumentCollectionProtocol.SzseJob,"同一客户端的另一市场不被全局待确认请求挡住");
                control.HoldSse=false;await downloading.WaitAsync(TimeSpan.FromSeconds(4));
                Check(true,"其它命令的完成没有丢失沪市原请求的后续回执");
            }
            finally{waitingStop.Cancel();try{await downloading;}catch(OperationCanceledException){}}
            var uncertainClient=new WorkerEtfWorkspace(new(pipe));var uncertainDocument=new AlertDocumentRequest("SSE","510300","https://www.sse.com.cn/uncertain.pdf");
            control.UnknownNext=true;
            await ExpectPending(()=>uncertainClient.RegisterAlertDocumentAsync(uncertainDocument,stop.Token),"无法确认后台是否受理");
            var uncertainId=control.Last!.RequestId;var writes=control.Writes;control.UnknownGet=true;
            await ExpectPending(()=>uncertainClient.RegisterAlertDocumentAsync(uncertainDocument with{Url="https://www.sse.com.cn/changed.pdf"},stop.Token),"上次结果尚未确认");
            Check(control.Writes==writes,"未知受理且回查UnknownRequest时，改参数不能创建替代采集");
            await uncertainClient.SaveAlertConfigurationAsync(new(new ExitAlertRules(),[],0),stop.Token);
            Check(control.Last!.JobId=="alert-manage","未知采集结果仍不阻断其它管理任务");
            control.UnknownGet=false;await uncertainClient.RegisterAlertDocumentAsync(uncertainDocument,stop.Token);
            Check(control.Last!.RequestId==uncertainId,"丢失受理结果后按原参数重试沿用原ID");
            var deferredClient=new WorkerEtfWorkspace(new(pipe));control.DeferredNext=true;control.KeepPending=true;
            await ExpectPending(()=>deferredClient.RegisterAlertDocumentAsync(uncertainDocument,stop.Token),"原请求已保留");
            var deferredId=control.Last!.RequestId;writes=control.Writes;
            await ExpectPending(()=>deferredClient.RegisterAlertDocumentAsync(uncertainDocument with{Url="https://www.sse.com.cn/newer.pdf"},stop.Token),"上次结果尚未确认");
            Check(control.Writes==writes,"已知来源延期时改参数不会绕过原任务等待");
            control.KeepPending=false;await deferredClient.RegisterAlertDocumentAsync(uncertainDocument,stop.Token);
            Check(control.Last!.RequestId==deferredId,"已知延期后原参数回读仍保持原命令身份");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private static async Task ExpectPending(Func<Task> action,string message)
    {try{await action();}catch(InvalidDataException error) when(error.Message.Contains(message,StringComparison.Ordinal)){return;}throw new Exception("客户端未明确保留待确认结果："+message);}
    private sealed class ClientControl:IBackgroundControl
    {
        public bool Supported=true,HoldSse,UnknownNext,UnknownGet,DeferredNext,KeepPending;public int Writes;public BackgroundRequest? Last;
        public TaskCompletionSource SseStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command=="QueryPlugin")
            {
                var capabilities=new EtfQueryCapabilities(1,Supported?[EtfDocumentCollectionProtocol.Capability]:[],256);
                var page=new EtfQueryPage(JsonSerializer.Serialize(new[]{capabilities}),"cap",null){TotalRows=1};
                return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize(page)));
            }
            if(request.Command=="GetCommand"&&request.RelatedRequestId is{} id)
                return Task.FromResult(UnknownGet?new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownRequest"):
                    new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Receipt:new(id,KeepPending?"Pending":HoldSse?"Running":"Completed",KeepPending?"RetryDeferred":HoldSse?"Accepted":"Succeeded",null)));
            if(request.Command!="RunNow")throw new Exception("意外操作。");Writes++;Last=request;
            if(UnknownNext){UnknownNext=false;return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"StorageFault"));}
            if(DeferredNext){DeferredNext=false;return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"RetryDeferred",Receipt:new(request.RequestId,"Pending","RetryDeferred",null)));}
            if(HoldSse&&request.JobId==EtfDocumentCollectionProtocol.SseJob)
            {SseStarted.TrySetResult();return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Accepted",Receipt:new(request.RequestId,"Running","Accepted",null)));}
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Succeeded",Receipt:new(request.RequestId,"Completed","Succeeded",new string('A',32))));
        }
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class Gateway:IPluginHttpGateway
    {
        public int Calls;public Func<Uri,CancellationToken,Task<PublicHttpResponse>> Read=(_,_)=>Task.FromResult(Pdf());
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token){Calls++;return Read(uri,token);}
    }
    private sealed class NoHttp:IPluginHttpGateway
    {public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("持久恢复不能发起采集。");}
}
