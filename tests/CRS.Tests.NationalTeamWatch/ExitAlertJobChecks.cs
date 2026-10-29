using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>实际后台插件入口的端到端隔离验证，仅将通知提供商替换为假渠道。</summary>
internal static class ExitAlertJobChecks
{
    public static async Task Run(string root)
    {
        var context=new Context(root);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using(var repository=TestMarketData.Open(Path.Combine(root,"data"),raw))
        {
            foreach(var fund in repository.Configuration().Where(x=>x.Ticker!="510300"))repository.SetMembership(new(fund.Market,fund.Ticker),fund.Name,fund.IndexGroup,false,"synthetic job");
            var config=repository.AlertConfiguration();repository.SaveAlertConfiguration(config with{Routes=[new("dingtalk","research")]});
        }
        await using(var session=new NationalTeamBackgroundSession(context,new NoHttp()))
        {
            await session.StartAsync(default);
            var through=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-1);
            var profileRequest=JsonSerializer.Serialize(new EtfRequest("acceptance-profile",To:through));
            if(session.QueryOperation(profileRequest)!="acceptance-profile")throw new Exception("验收诊断未登记为只读操作。");
            var profilePage=JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(profileRequest,default))!;
            var profile=JsonSerializer.Deserialize<List<EtfAcceptanceQueryProfile>>(profilePage.Items)!.Single();
            var normalPage=JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("acceptance",To:through)),default))!;
            if(JsonSerializer.Serialize(profile.Acceptance)!=JsonSerializer.Serialize(JsonSerializer.Deserialize<List<EtfAcceptance>>(normalPage.Items))||context.Calls!=0)
                throw new Exception("后台验收诊断改变金融结果或发送了通知。");
            Console.WriteLine("PASS 预警后台：验收分段诊断通过真实插件查询入口，结果与正常验收一致且不投递通知。");
            if(!session.DescribeJobs().Any(x=>x.JobId=="alert-evaluate"&&x.EnabledByDefault&&x.RunOnStartup))throw new Exception("预警未沿现有调度注册。");
            var result=await session.ExecuteAsync("alert-evaluate",null,default);
            var first=await Outbox(session);
            if(result.Status!="Succeeded"||context.Calls!=1||first.Single().State!="Accepted"||context.Profile!="research")throw new Exception("后台入口没有按选定渠道提交质量提醒。");
            await session.ExecuteAsync("alert-evaluate",null,default);
            if(context.Calls!=1)throw new Exception("重复评价重复外发。");
            Console.WriteLine("PASS 预警后台：真实插件入口共用既有调度、普通SQLite和公共渠道端口，指定钉钉Profile提交且重复运行不重发；提供商为假实现。");
        }
        await using(var reopened=new NationalTeamBackgroundSession(context,new NoHttp()))
        {
            await reopened.StartAsync(default);await reopened.ExecuteAsync("alert-evaluate",null,default);
            if(context.Calls!=1||(await Outbox(reopened)).Single().State!="Accepted")throw new Exception("重启丢失已受理状态。");
            Console.WriteLine("PASS 预警后台：重建后台会话保留事件和受理记录，桌面关闭不参与投递。");
        }
        await DocumentRecovery(Path.Combine(root,"document-recovery"));
    }
    public static async Task DocumentRecovery(string root)
    {
        var context=new Context(root);var gateway=new DocumentGateway();var requestId=Guid.NewGuid();var passed=0;
        const string url="https://www.sse.com.cn/synthetic-document-recovery.pdf";
        var payload=JsonSerializer.Serialize(new EtfRequest("alert-document",AlertDocument:new("SSE","510300",url)));
        void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS 原件恢复："+name);}
        await using var session=new NationalTeamBackgroundSession(context,gateway);await session.StartAsync(default);
        async Task<List<AlertDocument>> Documents()
        {
            var page=JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("alert-documents","SSE","510300")),default))!;
            return JsonSerializer.Deserialize<List<AlertDocument>>(page.Items)!;
        }
        async Task Deferred(Exception error,string reason,DateTimeOffset? until=null)
        {
            gateway.Read=(_,_)=>Task.FromException<PublicHttpResponse>(error);
            var result=await session.ExecuteAsync("alert-manage",payload,default,requestId);
            var checkpoint=result.Checkpoint??"";
            Check(result.Status=="RetryDeferred"&&checkpoint.StartsWith("alert="+reason+";",StringComparison.Ordinal),reason+"保留明确原因");
            Check(result.RetryAfter>TimeSpan.Zero&&checkpoint.Contains("retryAt=")&&checkpoint.Contains("+08:00"),reason+"回执包含北京时间重试时刻");
            if(until is{} expected)Check(CRS.Timing.BeijingClock.Now+result.RetryAfter>=expected,reason+"不缩短来源冷却");
            Check((await Documents()).Count==0&&context.Calls==0,reason+"不登记原件或投递通知");
        }
        var blockedUntil=CRS.Timing.BeijingClock.Now.AddHours(7);
        await Deferred(new SourceBudgetUnavailableException("SourceCooldown",blockedUntil),"DocumentSourceCooldown",blockedUntil);
        await Deferred(new SourceBudgetUnavailableException("SourceQueueFull"),"DocumentSourceQueueFull");
        var network=new HttpRequestException("合成连接失败");network.Data["SourceRetryAt"]=blockedUntil;
        await Deferred(network,"DocumentConnectionFailed",blockedUntil);
        var timeout=new TimeoutException("合成来源超时");timeout.Data["SourceRetryAt"]=blockedUntil;
        await Deferred(timeout,"DocumentTimedOut",blockedUntil);
        gateway.Read=(_,_)=>Task.FromResult(new PublicHttpResponse(503,[],null));
        var unavailable=await session.ExecuteAsync("alert-manage",payload,default,requestId);
        Check(unavailable is{Status:"RetryDeferred",RetryAfter:not null}&&unavailable.Checkpoint!.Contains("DocumentHttpUnavailable;http=503;"),"非200响应保留状态码和退避");
        gateway.Read=(_,_)=>Task.FromException<PublicHttpResponse>(new IOException("合成非网络故障"));
        var escaped=false;try{await session.ExecuteAsync("alert-manage",payload,default,requestId);}catch(IOException){escaped=true;}
        Check(escaped,"非网络异常不伪装为来源冷却");
        using(var cancel=new CancellationTokenSource())
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Read=async(_,token)=>{entered.SetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return new(200,[],null);};
            var waiting=session.ExecuteAsync("alert-manage",payload,cancel.Token,requestId);await entered.Task;cancel.Cancel();
            var cancelled=false;try{await waiting;}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&(await Documents()).Count==0,"调用方取消传播且不登记原件");
        }
        gateway.Read=(_,_)=>Task.FromResult(new PublicHttpResponse(200,"not-a-pdf"u8.ToArray(),"application/pdf"));
        var invalid=await session.ExecuteAsync("alert-manage",payload,default,requestId);
        Check(invalid.Status=="Rejected"&&(await Documents()).Count==0,"200但原件无效仍拒绝，不进入网络重试");
        gateway.Read=(_,_)=>Task.FromResult(new PublicHttpResponse(200,"%PDF-synthetic-document-recovery"u8.ToArray(),"application/pdf"));
        var recovered=await session.ExecuteAsync("alert-manage",payload,default,requestId);
        Check(recovered.Status=="Succeeded"&&(await Documents()).Count==1,"来源恢复后同一命令ID可完成登记");
        var original=(await Documents()).Single();
        var replay=await session.ExecuteAsync("alert-manage",payload,default,requestId);
        Check(replay.Status=="Succeeded"&&(await Documents()).Single()==original,"相同命令重放不覆盖原件身份及首见时间");
        var holdingsPage=JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("alert-holdings","SSE","510300")),default))!;
        Check(JsonSerializer.Deserialize<List<NamedEtfHolding>>(holdingsPage.Items)!.Count==0&&context.Calls==0,"登记与重放均不批准持仓或发送通知");
        // 用实际HTTP适配器回放403，验证持久六小时限制传到业务回执；不连接任何外网。
        using(var policy=new SourceRequestPolicy(Path.Combine(root,"source-policy")))
        using(var transport=new ForbiddenTransport())
        using(var official=new OfficialHttpGateway(policy,transport))
        {
            await using var deniedSession=new NationalTeamBackgroundSession(new Context(Path.Combine(root,"forbidden")),official);
            await deniedSession.StartAsync(default);
            var forbidden=await deniedSession.ExecuteAsync("alert-manage",payload,default,Guid.NewGuid());
            Check(forbidden is{Status:"RetryDeferred"}&&forbidden.Checkpoint!.Contains("http=403")&&forbidden.RetryAfter>TimeSpan.FromHours(5.9),"实际HTTP适配器403的持久冷却不缩成30分钟");
            var cooldown=await deniedSession.ExecuteAsync("alert-manage",payload,default,Guid.NewGuid());
            Check(cooldown.Checkpoint!.Contains("DocumentSourceCooldown")&&transport.Calls==1,"冷却中再次执行不发起HTTP且保持来源原因");
        }
        Console.WriteLine($"DOCUMENT_RECOVERY passed={passed};installed=false;network=synthetic");
    }
    private sealed class DocumentGateway:IPluginHttpGateway
    {
        public Func<Uri,CancellationToken,Task<PublicHttpResponse>> Read{get;set;}=(_,_)=>throw new InvalidOperationException("未指定合成来源。");
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>Read(uri,token);
    }
    private sealed class ForbiddenTransport:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls++;return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden){Content=new ByteArrayContent([])});}
    }
    private static async Task<List<AlertOutboxItem>> Outbox(NationalTeamBackgroundSession session)
    {
        var page=JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("alert-outbox")),default))!;
        return JsonSerializer.Deserialize<List<AlertOutboxItem>>(page.Items)!;
    }
    private sealed class Context(string root):IBackgroundHostContext,IBackgroundNotificationContext
    {
        public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();
        public int Calls;public string? Profile;
        public Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<BackgroundNotificationChannel>>([new("dingtalk","合成钉钉",true,true)]);
        public Task<BackgroundNotificationResult> SendNotificationAsync(string channelId,string profileId,BackgroundNotification message,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(channelId!="dingtalk")throw new Exception("出现未订阅渠道。");Calls++;Profile=profileId;
            return Task.FromResult(new BackgroundNotificationResult(channelId,"Accepted","SyntheticAccepted",message.MessageId,"synthetic-receipt"));
        }
    }
    private sealed class NoHttp:IPluginHttpGateway
    {
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("预警评价不可发起额外采集或请求真实网络。");
    }
}
