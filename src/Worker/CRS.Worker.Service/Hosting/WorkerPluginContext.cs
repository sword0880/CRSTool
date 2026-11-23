using System.Security.Cryptography;
using System.Text;
using CRS.Worker.Abstractions;
namespace CRS.Worker.Service;

/// <summary>每个插件使用独立私有目录和域分离的密钥副本，主密钥不交给业务实现。</summary>
public sealed class WorkerPluginContext(string id,string root,byte[] masterKey,IBackgroundNotificationContext? notifications=null,SourceBudgetRegistry? sources=null):IBackgroundHostContext,IBackgroundNotificationContext,IBackgroundSourceContext
{
    public void ImportSourceState(string host,SourceBudgetState legacy)
    {
        if(sources is null)throw new SourceBudgetUnavailableException("SourceBudgetUnavailable");
        sources.Import(host,legacy);
    }
    public Task<IBackgroundSourceLease> AcquireSourceAsync(string host,SourceBudgetState legacy,CancellationToken token)=>sources?.AcquireAsync(id,host,legacy,token)
        ??Task.FromException<IBackgroundSourceLease>(new SourceBudgetUnavailableException("SourceBudgetUnavailable"));
    public void ReportSourceFailure(string host,int status,DateTimeOffset? retryAfter=null)
    {
        if(sources is null)throw new SourceBudgetUnavailableException("SourceBudgetUnavailable");
        sources.ReportFailure(host,status,retryAfter);
    }
    public string PluginId=>id;
    public string DataDirectory=>Path.Combine(root,"plugins",id);
    public byte[] OpenDataKey()=>HKDF.DeriveKey(HashAlgorithmName.SHA256,masterKey,32,
        info:Encoding.UTF8.GetBytes("CRS.Worker.Plugin.Data/v1/"+id));
    // 插件只能提交通知内容，不能取得SMTP凭据、修改收件人或直接连接邮件服务器。
    public Task<BackgroundNotificationResult> SendNotificationAsync(string channelId,string profileId,BackgroundNotification message,CancellationToken token)=>notifications?.SendNotificationAsync(channelId,profileId,message,token)
        ??Task.FromResult(new BackgroundNotificationResult(channelId,"Unavailable","NotificationChannelUnavailable",message.MessageId));
    public Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token)=>notifications?.NotificationChannelsAsync(token)
        ??Task.FromResult<IReadOnlyList<BackgroundNotificationChannel>>([]);
}
