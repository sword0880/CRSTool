using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

/// <summary>只读正式服务公开预警及非秘密设置摘要，不采集、不审核、不保存、不发送任何通知。</summary>
internal static class ExitAlertInstalledProbe
{
    public static async Task Run()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var token=deadline.Token;
        var pipe=new BackgroundPipeClient();var workspace=new WorkerEtfWorkspace(pipe);
        var overview=await workspace.AlertOverviewAsync(token);var configuration=await workspace.AlertConfigurationAsync(token);
        var channels=await workspace.AlertChannelsAsync(token);var events=await workspace.AlertEventsAsync(new(),token);
        var outbox=await workspace.AlertOutboxAsync(token);
        var settings=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetEmailSettings"),token);
        if(settings.Code!="Ok"||settings.Payload is null)throw new InvalidOperationException("系统邮件设置只读验收失败："+settings.Code);
        using var json=JsonDocument.Parse(settings.Payload);
        if(json.RootElement.TryGetProperty("Password",out _))throw new InvalidOperationException("邮件设置响应泄露密码字段。");
        var email=JsonSerializer.Deserialize<WorkerEmailSettings>(settings.Payload)!;
        var jobs=(await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),token)).Jobs!;
        if(!jobs.Any(x=>x.PluginId=="crs.national-team"&&x.JobId=="alert-evaluate"&&x.Enabled))throw new InvalidOperationException("预警恢复评价计划未启用。");
        if(!overview.DataAcceptancePassed&&events.Any(x=>x.Lane!=AlertLane.DataQuality&&!x.Preview))throw new InvalidOperationException("真实数据门槛不足却生成正式金融事件。");
        var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","exit-alert-live");Directory.CreateDirectory(directory);
        var result=new{CheckedAt=DateTimeOffset.UtcNow,Overview=overview,Configuration=configuration,Channels=channels,
            Events=events,Notifications=outbox,Email=new{email.Enabled,email.HasPassword,email.Revision},
            Plans=jobs.Where(x=>x.PluginId=="crs.national-team").Select(x=>new{x.JobId,x.Enabled,x.Cron,x.TimeZoneId})};
        await File.WriteAllTextAsync(Path.Combine(directory,"installed-preview.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),token);
        Console.WriteLine($"PASS 已安装预警只读：监控{overview.MonitoredFunds}只，可比口径{overview.ComparableFunds}只，事件{events.Count}条，通知记录{outbox.Count}条；DataAcceptancePassed={overview.DataAcceptancePassed}。");
        Console.WriteLine($"PASS 系统邮件管理IPC可读，无Password字段；Enabled={email.Enabled}；本次仅查询，未发起测试或业务投递。");
    }
}
