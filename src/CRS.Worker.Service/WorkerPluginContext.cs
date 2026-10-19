using System.Security.Cryptography;
using System.Text;
using CRS.Worker.Abstractions;
namespace CRS.Worker.Service;

/// <summary>每个插件使用独立私有目录和域分离的密钥副本，主密钥不交给业务实现。</summary>
public sealed class WorkerPluginContext(string id,string root,byte[] masterKey,IBackgroundNotificationContext? notifications=null):IBackgroundHostContext,IBackgroundNotificationContext
{
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
