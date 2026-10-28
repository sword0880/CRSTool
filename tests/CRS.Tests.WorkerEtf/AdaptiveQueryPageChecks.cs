using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

/// <summary>真实业务会话验证字节缩页和快照；真实隔离管道验证新旧协商、完整性及故障不返回半份结果。</summary>
internal static class AdaptiveQueryPageChecks
{
    public static async Task RunAsync()
    {
        var passed=0;
        void Check(bool value,string text){if(!value)throw new InvalidDataException(text);passed++;Console.WriteLine("PASS 自适应分页："+text);}
        var root=Path.Combine(Path.GetTempPath(),"CRS.AdaptivePages",Guid.NewGuid().ToString("N"));var context=new Context(root);
        using(var seed=new MarketRepository(Path.Combine(root,"data"),new RawEvidenceStore(Path.Combine(root,"raw")),approvalAuditKey:context.Key))
            for(var i=0;i<80;i++)seed.SetMembership(new("SSE",(700000+i).ToString()),new string('测',64),new string('行',16),true,new string('据',80));
        await using(var session=new NationalTeamBackgroundSession(context,new NoHttp()))
        {
            await session.StartAsync(default);
            async Task<EtfQueryPage> Page(EtfRequest request)
            {
                var json=await session.QueryAsync(JsonSerializer.Serialize(request),default);
                Check(Encoding.UTF8.GetByteCount(json)<=46000,"实际业务响应保留46KB字节限额");
                return EtfBusinessProtocol.ReadResponse<EtfQueryPage>(json);
            }
            for(var i=0;i<9;i++)
            {try{await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("configuration",Count:257)),default);throw new Exception("超限页大小被接受。");}catch(ArgumentException){}}
            var first=await Page(new("configuration"));var rows=JsonSerializer.Deserialize<EtfConfiguration[]>(first.Items)!;
            Check(rows.Length==10&&first.MaximumPageCount==256,"旧式首页十行，明确声明可选扩页能力；无效请求不耗尽快照");
            using(var writer=new MarketRepository(Path.Combine(root,"data"),new RawEvidenceStore(Path.Combine(root,"raw")),approvalAuditKey:context.Key))writer.SetMembership(new("SSE","799999"),"合成后插","合成",true,"只验证快照隔离");
            try{await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("configuration",Ticker:"799999",Offset:10,Count:256,Snapshot:first.Snapshot)),default);throw new Exception("快照范围改变未拒绝。");}catch(ArgumentException){Check(true,"传输页大小可变，但证券筛选仍绑定原快照");}
            var all=rows.ToList();var offset=first.NextOffset;var wide=false;
            while(offset is {} next)
            {
                var page=await Page(new("configuration",Offset:next,Count:256,Snapshot:first.Snapshot));var added=JsonSerializer.Deserialize<EtfConfiguration[]>(page.Items)!;
                Check(page.NextOffset is null||page.NextOffset==next+added.Length,"字节缩页后的游标严格连续");wide|=added.Length>10;all.AddRange(added);offset=page.NextOffset;
            }
            Check(wide&&all.Count==104&&all.Select(x=>(x.Market,x.Ticker)).Distinct().Count()==104&&!all.Any(x=>x.Ticker=="799999"),"同一快照完整104行，扩页不重复、不漏行、不混入后插资料");
            var updated=await Page(new("configuration",Count:256));Check(JsonSerializer.Deserialize<EtfConfiguration[]>(updated.Items)!.Length>10&&updated.NextOffset is not null,"大页受字节限额主动缩小，仍可继续读取");
            await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("release-query",Snapshot:updated.Snapshot)),default);
        }
        CryptographicOperations.ZeroMemory(context.Key);

        using var stop=new CancellationTokenSource();var control=new Control();var name="CRS.AdaptivePages."+Guid.NewGuid().ToString("N");var server=new BackgroundPipeServer(control,name).RunAsync(stop.Token);
        try
        {
            var reader=new WorkerEtfWorkspace(new(name));var old=await reader.DashboardAsync(null,default);
            Check(old?.History.Count==600&&control.Counts.Count==60&&control.Counts.All(x=>x==10),"旧V1没有能力字段时保持十行，完整读取600行");
            control.Reset("Valid",true);var modern=await reader.DashboardAsync(null,default);
            Check(JsonSerializer.Serialize(old)==JsonSerializer.Serialize(modern)&&control.Counts[0]==10&&control.Counts.Skip(1).All(x=>x==256)&&control.Counts.Count<30,$"新服务协商后{control.Counts.Count}次往返，与旧服务60次结果完全一致，仍按字节缩页");
            foreach(var fault in new[]{"Snapshot","Cursor","Metadata","Rows","Capability","Truncated"})
            {
                control.Reset(fault,true);
                try{await reader.DashboardAsync(null,default);throw new Exception("分页故障返回了部分仪表盘："+fault);}catch(InvalidDataException){}
                await control.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(true,"拒绝异常且释放原快照："+fault);
            }
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
        await CleanupQueue();Check(true,"取消快照的清理可等待在途读取，不因本地排队超过两秒而丢弃");
        Console.WriteLine($"PASS 自适应分页合计 {passed}；隔离会话/管道，不替代正式性能验收。");
    }
    private static async Task CleanupQueue()
    {
        using var stop=new CancellationTokenSource();var control=new CleanupControl();var pipe="CRS.Etf.Cleanup."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var reader=new WorkerEtfWorkspace(new(pipe));using var cancelled=new CancellationTokenSource();
            var old=reader.DashboardAsync(null,cancelled.Token);await control.SecondPage.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // 新读取先进入本地队列，取消旧分页后清理排在它后面；不把清理的网络期限当成排队期限。
            var next=reader.ConfigurationAsync(default);cancelled.Cancel();
            try{await old;throw new Exception("取消仍返回不完整整页。");}catch(OperationCanceledException){}
            await control.ConfigurationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));await Task.Delay(2300);
            control.ConfigurationRelease.TrySetResult();await next;
            await control.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally{control.ConfigurationRelease.TrySetResult();stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class CleanupControl:IBackgroundControl
    {
        public TaskCompletionSource SecondPage=new(TaskCreationOptions.RunContinuationsAsynchronously),ConfigurationEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),
            ConfigurationRelease=new(TaskCreationOptions.RunContinuationsAsynchronously),Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);string payload;
            if(query.Operation=="query-capabilities")return new(WorkerProtocol.Version,request.RequestId,"UnknownQuery");
            if(query.Operation=="release-query"){Released.TrySetResult();payload=JsonSerializer.Serialize("Released");}
            else if(query.Operation=="configuration")
            {
                ConfigurationEntered.TrySetResult();await ConfigurationRelease.Task.WaitAsync(token);
                payload=JsonSerializer.Serialize(new EtfQueryPage("[]","configuration",null){MaximumPageCount=256,TotalRows=0});
            }
            else
            {
                if(query.Offset>0){SecondPage.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
                var rows=Enumerable.Range(0,10).Select(i=>new EtfDashboardRow(History:new(new DateOnly(2025,1,1).AddDays(i),i,null,1,1)));
                payload=JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(rows),"cancelled-snapshot",10,JsonSerializer.Serialize("synthetic")){MaximumPageCount=256,TotalRows=20});
            }
            return new(WorkerProtocol.Version,request.RequestId,"Ok",Payload:payload);
        }
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public readonly byte[] Key=RandomNumberGenerator.GetBytes(32);public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>Key.ToArray();}
    private sealed class NoHttp:IPluginHttpGateway
    {public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("分页检查不允许联网。");}
    private sealed class Control:IBackgroundControl
    {
        public bool Modern;public string Fault="Valid";public List<int> Counts=[];public TaskCompletionSource Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset(string fault,bool modern){Fault=fault;Modern=modern;Counts.Clear();Released=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);
            if(query.Operation=="query-capabilities")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownQuery"));
            if(query.Operation=="release-query"){Released.TrySetResult();return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize("Released")));}
            Counts.Add(query.Count);var count=Math.Min(query.Count,600-query.Offset);if(Fault=="Rows")count=query.Count+1;
            while(true)
            {
                var rows=Enumerable.Range(query.Offset,count).Select(i=>new EtfDashboardRow(History:new(new DateOnly(2025,1,1).AddDays(i),i,null,1,1))).ToArray();
                var next=query.Offset+count;var page=new EtfQueryPage(JsonSerializer.Serialize(rows),Fault=="Snapshot"&&query.Offset>0?"changed":"original",next<600&&Fault!="Truncated"?next+(Fault=="Cursor"?1:0):null,JsonSerializer.Serialize(Fault=="Metadata"&&query.Offset>0?"changed":"synthetic")){MaximumPageCount=Modern?Fault=="Capability"?257:256:null,TotalRows=Modern?600:null};
                var payload=JsonSerializer.Serialize(page);
                if(Encoding.UTF8.GetByteCount(payload)<=46000)return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:payload));
                count=Math.Max(1,count/2);
            }
        }
    }
}
