using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Domain;
using Microsoft.Data.Sqlite;

internal static class BusinessContractChecks
{
    private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS 业务契约："+name);}
    private static void Reject(Action action,string code)
    {
        try{action();}catch(EtfContractException error) when(error.Code==code){Check(true,code);return;}
        throw new Exception("未拒绝业务契约："+code);
    }
    public static async Task Run(string root)
    {
        Check(EtfBusinessProtocol.ParseRequest("{\"Operation\":\"configuration\"}").ContractVersion==1,"旧客户端缺版本明确映射V1");
        Check(EtfBusinessProtocol.ParseRequest("{\"Operation\":\"configuration\",\"ContractVersion\":1,\"FutureDisplayHint\":true}").Operation=="configuration","同V1新增可选字段向前兼容");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"ContractVersion\":2}"),"UnsupportedContractVersion");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"ContractVersion\":null}"),"InvalidContractVersion");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{}"),"InvalidRequest");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":null}"),"InvalidRequest");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"Operation\":\"sync\"}"),"DuplicateProperty");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"Name\":\""+new string('牛',1500)+"\"}"),"PayloadTooLarge");
        Reject(()=>EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null,\"ContractVersion\":99}"),"UnsupportedContractVersion");
        Reject(()=>EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"ContractError\":\"UnsupportedContractVersion\",\"SupportedContractVersion\":1}"),"UnsupportedContractVersion");
        Reject(()=>EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"Snapshot\":\"s\",\"NextOffset\":null}"),"InvalidResponse");
        Check(EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null}").ContractVersion==1,"新客户端接受既有V1服务分页");
        Check(EtfBusinessProtocol.ReadResponse<string>("\"公开状态\"")=="公开状态","V1状态字符串兼容，不伪造新版包裹");
        Directory.CreateDirectory(root);
        await using(var session=new NationalTeamBackgroundSession(new Context(root),new NoHttp()))
        {
            var jobs=session.DescribeJobs();
            Check(jobs.Count==27&&jobs.Select(x=>x.JobId).Distinct().Count()==27,"模块注册保留原25项及两市独立原件采集任务描述");
            Check(jobs.All(x=>session.ExecutionGroup(x.JobId).MaximumConcurrency is >=1 and <=2),"每项任务从所属模块声明取得受限执行组");
            Check(session.QueryOperation(JsonSerializer.Serialize(new EtfRequest("acceptance-batch")))=="acceptance-batch"
                &&session.QueryOperation(JsonSerializer.Serialize(new EtfRequest("query-capabilities")))=="query-capabilities"
                &&session.QueryOperation(JsonSerializer.Serialize(new EtfRequest("watchlist")))=="unknown","模块查询注册同时形成诊断白名单，写操作不进入公开读路由");
            await session.StartAsync(default);
            var capabilityPage=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("query-capabilities")),default));
            var capability=JsonSerializer.Deserialize<EtfQueryCapabilities[]>(capabilityPage.Items)!.Single();
            Check(capability.Version==1&&capability.MaximumPageCount==256&&capability.Capabilities.Contains(EtfQueryCapabilities.BatchAcceptance),"生产入口明确声明批量验收能力及版本");
            var through=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-1);
            var batchPage=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("acceptance-batch",To:through,Count:256)),default));
            var batch=JsonSerializer.Deserialize<EtfAcceptance[]>(batchPage.Items)!;var batchMetadata=JsonSerializer.Deserialize<EtfAcceptanceBatchMetadata>(batchPage.Metadata!)!;
            Check(batch.Length==24&&batchPage.NextOffset is null&&batch.All(x=>x.Through==through)&&batchMetadata.ExpectedEtfs==24
                &&batchMetadata.PoolFingerprint==EtfAcceptanceBatchMetadata.Fingerprint(batch),"同一次后台只读验收返回完整24只配置池及绑定元数据");
            var ordinaryPage=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("acceptance",To:through,Count:256)),default));
            Check(batchPage.Items==ordinaryPage.Items,"批量入口与既有全池验收逐字段相同");
            try{await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("acceptance-batch","SSE","510300",To:through)),default);throw new Exception("批量接受了单只范围");}
            catch(InvalidDataException){Check(true,"批量拒绝证券筛选，不能悄悄缩小配置分母");}
            var failure=JsonSerializer.Deserialize<EtfContractFailure>(await session.QueryAsync("{\"Operation\":\"configuration\",\"ContractVersion\":99}",default));
            Check(failure is {ContractError:"UnsupportedContractVersion",SupportedContractVersion:1},"后台查询明确返回版本拒绝，不降级为数据");
            var rejected=await session.ExecuteAsync("watchlist","{\"Operation\":\"watchlist\",\"ContractVersion\":99}",default);
            Check(rejected.Status=="Rejected"&&rejected.Checkpoint=="contract=UnsupportedContractVersion","持久写命令未知版本拒绝且不执行");
            var boundReview=new EquityReview("candidate",new(2026,3,31),new(2026,4,28),"https://invalid.example/report.pdf","P1","reviewer",true,"Portfolio"){ComparisonDisclosureId="prior"};
            var boundRequest=JsonSerializer.Serialize(new EtfRequest("equity-review-bound",EquityReview:boundReview));
            Check(EtfBusinessProtocol.ParseRequest(boundRequest).EquityReview?.ComparisonDisclosureId=="prior","审核基线穿过业务V1序列化保留");
            var invalidDocument=await session.ExecuteAsync("equity-review",boundRequest,default);
            Check(invalidDocument.Status=="Rejected"&&invalidDocument.Checkpoint=="equity=OfficialPdfRequired","新绑定审核操作进入正式拒绝路径，非法原文不下载");
            try{await session.QueryAsync("{\"Operation\":\"unknown-operation\"}",default);throw new Exception("未知操作被放行。");}
            catch(ArgumentException){Check(true,"未知查询操作沿白名单拒绝");}
            var reply=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync("{\"Operation\":\"configuration\"}",default));
            Check(reply.ContractVersion==1&&JsonSerializer.Deserialize<JsonElement[]>(reply.Items)!.Length>0,"拒绝之后旧版查询仍能返回已提交完整分页");
            var released=await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("release-query",Snapshot:reply.Snapshot)),default);
            Check(JsonSerializer.Deserialize<string>(released)=="Released","取消分页可主动释放公开快照额度");
            try{await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("configuration",Offset:10,Snapshot:reply.Snapshot)),default);throw new Exception("已释放快照仍可继续读取");}
            catch(ArgumentException){Check(true,"已释放快照不能被重用");}
        }
        var future=Path.Combine(root,"future-schema");Directory.CreateDirectory(future);SQLitePCL.Batteries_V2.Init();
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(future,"market.db")}.ToString()))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=99";cmd.ExecuteNonQuery();}
        try{using var rejected=TestMarketData.Open(future,new RawEvidenceStore(Path.Combine(future,"raw")));throw new Exception("未知存储版本被放行。");}
        catch(InvalidDataException){Check(true,"未知持久Schema拒绝打开，与业务V1独立");}
        await ClientCompatibility();
        await BatchClientCompatibility();
        await ResearchJobs(Path.Combine(root,"research-jobs"));
        await ResearchClientCancellation();
    }
    private static async Task ResearchClientCancellation()
    {
        var control=new ResearchControl();var name="CRS.Research.Client."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));var server=new BackgroundPipeServer(control,name).RunAsync(stop.Token);
        try
        {
            var reader=new WorkerEtfWorkspace(new(name));using var cancelled=new CancellationTokenSource();
            var research=reader.AlertOverviewAsync(cancelled.Token);await control.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var shortRows=await reader.ConfigurationAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Check(shortRows.Count==0&&!research.IsCompleted,"真实管道客户端等待研究作业期间，短配置读通过独立准入完成");
            cancelled.Cancel();try{await research;throw new Exception("研究取消返回了金融结果。");}catch(OperationCanceledException){}
            await control.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(control.Submissions==1,"取消只释放原研究作业，未重复提交、未执行采集命令");
            using var heldCancellation=new CancellationTokenSource();using var queuedCancellation=new CancellationTokenSource();
            var held=reader.HistoryAsync(null,heldCancellation.Token);await control.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued=reader.HistoryAsync(null,queuedCancellation.Token);
            try
            {
                try{await reader.HistoryAsync(null,stop.Token);throw new Exception("本地研究准入未限制等待数量。");}
                catch(InvalidDataException){Check(true,"客户端队列满返回可呈现的繁忙错误，不泄漏内部准入异常");}
            }
            finally
            {
                heldCancellation.Cancel();queuedCancellation.Cancel();
                foreach(var pending in new[]{held,queued})try{await pending;}catch(OperationCanceledException){}
            }
            Check((await reader.ConfigurationAsync(stop.Token)).Count==0,"本地繁忙及取消后短查询准入恢复");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class ResearchControl:IBackgroundControl
    {
        public TaskCompletionSource Submitted=new(TaskCreationOptions.RunContinuationsAsynchronously),Released=new(TaskCreationOptions.RunContinuationsAsynchronously),HistoryEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Submissions;
        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command!="QueryPlugin")throw new Exception("只读研究发送了写命令。");
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);string items;
            if(query.Operation=="query-capabilities")items=JsonSerializer.Serialize(new[]{new EtfQueryCapabilities(1,[EtfQueryCapabilities.AsyncResearch],256){ShortReadOperations=["configuration","research-submit","research-status","research-release"]}});
            else if(query.Operation=="configuration")items="[]";
            else if(query.Operation=="history"){HistoryEntered.TrySetResult();await Task.Delay(Timeout.Infinite,token);items="[]";}
            else if(query.Operation=="research-release"){Released.TrySetResult();items="[\"Released\"]";}
            else
            {
                if(query.Operation=="research-submit"){Submissions++;Submitted.TrySetResult();}
                var fingerprint=CRS.Common.Integrity.ContentFingerprint.Sha256(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(query.ResearchRequest! with{Offset=0,Snapshot=null,Count=10})));
                items=JsonSerializer.Serialize(new[]{new EtfResearchTicket(query.Revision!,"Queued",null,null,CRS.Timing.BeijingClock.Now.AddMinutes(1),fingerprint)});
            }
            return new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize(new EtfQueryPage(items,Guid.NewGuid().ToString("N"),null)));
        }
    }
    private static async Task ResearchJobs(string root)
    {
        var through=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-1);
        var target=new EtfRequest("acceptance-batch",To:through,Count:256);
        var id=Guid.NewGuid().ToString("N");
        var submit=new EtfRequest("research-submit",Revision:id){ResearchRequest=target};
        await using(var session=new NationalTeamBackgroundSession(new Context(root),new NoHttp()))
        {
            await session.StartAsync(default);
            async Task<EtfResearchTicket> Ticket(EtfRequest request)
            {
                var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(request),default));
                return JsonSerializer.Deserialize<EtfResearchTicket[]>(page.Items)!.Single();
            }
            async Task<EtfResearchTicket> Ready(EtfRequest request)
            {
                for(var attempt=0;attempt<100;attempt++)
                {
                    var ticket=await Ticket(request with{Operation="research-status"});
                    if(ticket.State=="Ready")return ticket;
                    if(ticket.State is not ("Running" or "Queued"))throw new Exception("研究失败："+ticket.Failure);
                    await Task.Delay(30);
                }
                throw new TimeoutException("合成研究未完成。");
            }
            var accepted=await Ticket(submit);var repeated=await Ticket(submit);
            Check(accepted.Id==id&&accepted.State is "Queued" or "Running" or "Ready"&&repeated.RequestFingerprint==accepted.RequestFingerprint,"研究提交返回作业身份，原标识原参数重试不新建计算");
            try{await Ticket(submit with{ResearchRequest=target with{To=through.AddDays(-1)}});throw new Exception("作业参数可替换。");}
            catch(ArgumentException){Check(true,"同一研究作业标识拒绝替换参数");}
            var ready=await Ready(submit);Check(ready.Snapshot is {Length:32}&&ready.Failure is null,"研究完成后才发布结果快照身份");
            var written=await session.ExecuteAsync("watchlist",EtfBusinessProtocol.WriteRequest(new("watchlist","SSE","799999",Name:"合成新增",Index:"测试",Active:true,Reason:"测试快照固定分母")),default);
            Check(written.Status=="Succeeded","研究结果冻结后仍允许独立持久写入");
            try{await session.QueryAsync(EtfBusinessProtocol.WriteRequest(target with{Snapshot=ready.Snapshot,To=through.AddDays(-1)}),default);throw new Exception("快照参数被替换。");}
            catch(ArgumentException){Check(true,"作业生成的快照同样绑定证券、日期等完整参数");}
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(target with{Snapshot=ready.Snapshot}),default));
            var frozen=JsonSerializer.Deserialize<EtfAcceptance[]>(page.Items)!;
            var current=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(target),default));
            Check(frozen.Length==24&&JsonSerializer.Deserialize<EtfAcceptance[]>(current.Items)!.Length==25,"异步分页保留计算时24只分母，未混入随后新增的第25只");
            var second=submit with{Revision=Guid.NewGuid().ToString("N")};await Ticket(second);await Ready(second);
            try{await Ticket(submit with{Revision=Guid.NewGuid().ToString("N")});throw new Exception("研究无界排队。");}
            catch(CRS.Common.Concurrency.WorkAdmissionException){Check(true,"研究作业含完成未释放结果最多两项，第三项明确拒绝");}
            await Task.WhenAll(session.QueryAsync(EtfBusinessProtocol.WriteRequest(submit with{Operation="research-release"}),default),
                session.QueryAsync(EtfBusinessProtocol.WriteRequest(submit with{Operation="research-release"}),default));
            await session.QueryAsync(EtfBusinessProtocol.WriteRequest(second with{Operation="research-release"}),default);
            try{await Ticket(submit with{Operation="research-status"});throw new Exception("已释放作业仍存在。");}
            catch(ArgumentException){Check(true,"并发重复释放安全，作业结果及名额均回收");}
            var invalid=submit with{Revision=Guid.NewGuid().ToString("N"),ResearchRequest=new("joint-exit-study","SSE","512100",To:through.AddDays(2))};
            await Ticket(invalid);EtfResearchTicket failed;
            do{await Task.Delay(30);failed=await Ticket(invalid with{Operation="research-status"});}while(failed.State is "Queued" or "Running");
            Check(failed.State=="Failed"&&failed.Snapshot is null&&failed.Failure is not null,"研究构造失败无金融快照，不以空结果冒充完成");
            await session.QueryAsync(EtfBusinessProtocol.WriteRequest(invalid with{Operation="research-release"}),default);
        }
        await using(var restarted=new NationalTeamBackgroundSession(new Context(root),new NoHttp()))
        {
            await restarted.StartAsync(default);
            try{await restarted.QueryAsync(EtfBusinessProtocol.WriteRequest(submit with{Operation="research-status"}),default);throw new Exception("重启复用旧作业。");}
            catch(ArgumentException){Check(true,"后台重启后旧作业明确失效，不自动重算或返回旧结果");}
        }
    }
    private static async Task BatchClientCompatibility()
    {
        var control=new BatchControl();var name="CRS.BatchAcceptance."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));var server=new BackgroundPipeServer(control,name).RunAsync(stop.Token);
        try
        {
            var client=new WorkerEtfWorkspace(new BackgroundPipeClient(name));
            var result=await client.AcceptanceAsync(control.Through,stop.Token);
            Check(result.Count==24&&control.Operations.SequenceEqual(new[]{"query-capabilities","acceptance-batch"}),"真实管道客户端两次往返完成协商和24只验收，不逐ETF循环");
            foreach(var fault in new[]{"CapabilityVersion","MissingCapability","PageCapability","MetadataVersion","Truncated","Duplicate","Date","Pool"})
            {
                control.Fault=fault;control.Operations.Clear();
                client=new WorkerEtfWorkspace(new BackgroundPipeClient(name));
                try{await client.AcceptanceAsync(control.Through,stop.Token);throw new Exception("客户端接受损坏批量："+fault);}
                catch(InvalidDataException){Check(true,"客户端拒绝不完整批量且不返回部分资料："+fault);}
                Check(control.Operations.All(x=>x is "query-capabilities" or "acceptance-batch"),"版本或批量异常不降级为逐只请求："+fault);
            }
            control.Async=true;control.Fault="";control.Operations.Clear();
            client=new WorkerEtfWorkspace(new BackgroundPipeClient(name));
            result=await client.AcceptanceAsync(control.Through,stop.Token);
            Check(result.Count==24&&control.Operations.SequenceEqual(new[]{"query-capabilities","research-submit","acceptance-batch","research-release"}),"批量与异步能力均已声明时先提交作业，再凭冻结快照读取全池并释放");
            foreach(var fault in new[]{"MetadataVersion","Truncated","Duplicate","Date","Pool"})
            {
                control.Fault=fault;control.Operations.Clear();client=new WorkerEtfWorkspace(new BackgroundPipeClient(name));
                try{await client.AcceptanceAsync(control.Through,stop.Token);throw new Exception("异步批量接受损坏元数据。");}
                catch(InvalidDataException){Check(control.Operations.Count(x=>x=="research-submit")==1&&control.Operations.Count(x=>x=="research-release")==1,"异步批量同样拒绝不完整结果且释放作业："+fault);}
            }
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class BatchControl:IBackgroundControl
    {
        public string Fault="";public bool Async;public List<string> Operations=[];public DateOnly Through=new(2026,9,1);
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);Operations.Add(query.Operation);string payload;
            if(query.Operation=="query-capabilities")
            {
                var features=Async?new[]{EtfQueryCapabilities.BatchAcceptance,EtfQueryCapabilities.AsyncResearch}:new[]{EtfQueryCapabilities.BatchAcceptance};
                var capabilities=new EtfQueryCapabilities(Fault=="CapabilityVersion"?2:1,Fault=="MissingCapability"?[]:features,Fault=="PageCapability"?512:256);
                payload=JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(new[]{capabilities}),"capability",null){TotalRows=1});
            }
            else if(query.Operation=="research-submit"&&Async)
            {
                if(query.ResearchRequest is not{Operation:"acceptance-batch",Snapshot:null,Offset:0} target||target.To!=Through)throw new Exception("异步批量作业请求未绑定完整原参数。");
                var fingerprint=CRS.Common.Integrity.ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(target with{Count=10}));
                payload=JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(new[]{new EtfResearchTicket(query.Revision!,"Ready",new('E',32),null,CRS.Timing.BeijingClock.Now.AddMinutes(1),fingerprint)}),"ticket",null){TotalRows=1});
            }
            else if(query.Operation=="research-release"&&Async)
                payload=JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(new[]{"Released"}),"released",null){TotalRows=1});
            else if(query.Operation=="acceptance-batch")
            {
                if(Async&&query.Snapshot!=new string('E',32))throw new Exception("批量构造不能退回同步查询。");
                var rows=Enumerable.Range(0,24).Select(i=>new EtfAcceptance("SSE",(510000+i).ToString(),Through,null,0,0,0,0,false)).ToArray();
                var metadata=new EtfAcceptanceBatchMetadata(Fault=="MetadataVersion"?2:1,Through,24,EtfAcceptanceBatchMetadata.Fingerprint(rows));
                if(Fault=="Truncated")rows=rows[..^1];if(Fault=="Duplicate")rows[1]=rows[0];
                if(Fault=="Date")rows[0]=rows[0] with{Through=Through.AddDays(-1)};
                if(Fault=="Pool")metadata=metadata with{PoolFingerprint=new string('0',64)};
                payload=JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(rows),"batch",null,JsonSerializer.Serialize(metadata)){TotalRows=rows.Length,MaximumPageCount=256});
            }
            else throw new Exception("批量测试收到未声明的回退操作。");
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:payload));
        }
    }
    private static async Task ClientCompatibility()
    {
        var control=new ResponseControl();var pipe="CRS.Contract.Tests."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listening=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var client=new WorkerEtfWorkspace(new BackgroundPipeClient(pipe));
            Check((await client.ConfigurationAsync(stop.Token)).Count==0,"真实管道客户端接受旧V1分页");
            foreach(var payload in new[]{"{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null,\"ContractVersion\":2}",
                "{\"ContractError\":\"UnsupportedContractVersion\",\"SupportedContractVersion\":1}"})
            {
                control.Payload=payload;
                try{await client.ConfigurationAsync(stop.Token);throw new Exception("客户端接受不兼容响应。");}
                catch(InvalidDataException error) when(error.Message.Contains("UnsupportedContractVersion"))
                {Check(true,"真实管道客户端拒绝未知版本/业务错误，不返回空列表冒充成功");}
            }
            var review=new EquityReview("candidate",new(2026,3,31),new(2026,4,28),"https://www.sse.com.cn/report.pdf","P1","reviewer",true,"Portfolio"){ComparisonDisclosureId="prior"};
            try{await client.ReviewEquityAsync(review,stop.Token);throw new Exception("旧服务意外接受了基线审核。");}
            catch(InvalidDataException){Check(control.Writes==1&&control.LastWrite is {Operation:"equity-review-bound",EquityReview.ComparisonDisclosureId:"prior"},"真实管道写入使用独立绑定操作，旧服务拒绝后不降级重发");}
        }
        finally{stop.Cancel();try{await listening;}catch(OperationCanceledException){}}
    }
    private sealed class ResponseControl:IBackgroundControl
    {
        public string Payload="{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null}";
        public int Writes;public EtfRequest? LastWrite;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var business=EtfBusinessProtocol.ParseRequest(request.Payload!);
            if(business.Operation=="query-capabilities")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownQuery"));
            if(business.ContractVersion!=1)throw new Exception("客户端未发送V1。");
            if(request.Command=="RunNow")
            {Writes++;LastWrite=business;return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Rejected"));}
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:Payload));
        }
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoHttp:IPluginHttpGateway
    {public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("契约测试不可请求真实网络。");}
}
