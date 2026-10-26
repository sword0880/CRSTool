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
            Console.WriteLine("PASS 预警后台：真实插件入口共用既有调度、加密库和公共渠道端口，指定钉钉Profile提交且重复运行不重发；提供商为假实现。");
        }
        await using(var reopened=new NationalTeamBackgroundSession(context,new NoHttp()))
        {
            await reopened.StartAsync(default);await reopened.ExecuteAsync("alert-evaluate",null,default);
            if(context.Calls!=1||(await Outbox(reopened)).Single().State!="Accepted")throw new Exception("重启丢失已受理状态。");
            Console.WriteLine("PASS 预警后台：重建后台会话保留事件和受理记录，桌面关闭不参与投递。");
        }
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
