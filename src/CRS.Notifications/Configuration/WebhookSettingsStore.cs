using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace CRS.Notifications;

/// <summary>宿主准备受保护目录及ACL；与邮件使用同一LocalMachine DPAPI范围，配置不进入业务任务日志。</summary>
public sealed class WebhookSettingsStore(string directory)
{
    private readonly object gate=new();
    internal sealed record Stored(WebhookSettings Settings,string Token,string SigningSecret)
    {public override string ToString()=>"Stored { Credentials=[已隐藏] }";}
    private static string Channel(string id)=>id is "dingtalk" or "wechat"?id:throw new InvalidDataException("未知通知渠道。");
    private string FileName(string id)=>Path.Combine(directory,Channel(id)+"-settings.dpapi");
    private static byte[] Entropy(string id)=>Encoding.UTF8.GetBytes("CRS.Notifications.WebhookSettings/v1/"+Channel(id));
    public WebhookSettings Read(string id){lock(gate)return Open(id).Settings;}
    internal Stored Open(string id)
    {
        lock(gate)
        {
            var path=FileName(id);if(!File.Exists(path))return new(new(id),"","");
            var encrypted=File.ReadAllBytes(path);if(encrypted.Length>16384)throw new InvalidDataException("通知配置超限。");
            var bytes=ProtectedData.Unprotect(encrypted,Entropy(id),DataProtectionScope.LocalMachine);
            try
            {
                var saved=JsonSerializer.Deserialize<Stored>(bytes)??throw new InvalidDataException("通知配置为空。");
                Validate(saved,id);return saved;
            }
            finally{CryptographicOperations.ZeroMemory(bytes);}
        }
    }
    public WebhookSettings Save(WebhookSettingsUpdate update)
    {
        lock(gate)
        {
            var old=Open(update.ChannelId);if(update.Revision!=old.Settings.Revision)throw new InvalidDataException("通知配置已变化，请刷新。");
            var access=update.ClearToken?"":string.IsNullOrEmpty(update.Token)?old.Token:update.Token;
            var signing=update.ClearSigningSecret?"":string.IsNullOrEmpty(update.SigningSecret)?old.SigningSecret:update.SigningSecret;
            var settings=new WebhookSettings(update.ChannelId,update.Enabled,access.Length>0,signing.Length>0,checked(old.Settings.Revision+1));
            var stored=new Stored(settings,access,signing);Validate(stored,update.ChannelId);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(stored);var temporary=FileName(update.ChannelId)+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                var encrypted=ProtectedData.Protect(bytes,Entropy(update.ChannelId),DataProtectionScope.LocalMachine);
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(encrypted);stream.Flush(true);}
                File.Move(temporary,FileName(update.ChannelId),true);return settings;
            }
            finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    private static void Validate(Stored saved,string id)
    {
        Channel(id);
        bool Safe(string? value)=>value is {Length:<=512}&&value.All(char.IsAsciiLetterOrDigit);
        if(saved.Settings is null||saved.Settings.ChannelId!=id||saved.Settings.Revision<0||!Safe(saved.Token)||!Safe(saved.SigningSecret)
            ||saved.Settings.HasToken!=(saved.Token.Length>0)||saved.Settings.HasSigningSecret!=(saved.SigningSecret.Length>0)
            ||id=="wechat"&&saved.SigningSecret.Length>0||saved.Settings.Enabled&&(saved.Token.Length==0||id=="dingtalk"&&saved.SigningSecret.Length==0))
            throw new InvalidDataException("请检查通知Token及钉钉加签密钥；启用前需填写完整。");
    }
}
