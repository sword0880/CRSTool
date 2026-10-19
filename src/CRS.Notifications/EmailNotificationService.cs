using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;
namespace CRS.Notifications;

/// <summary>公共邮件组件；宿主只传入受保护目录，不读取个人保险库，不把凭据写入明文配置或日志。</summary>
public sealed class EmailNotificationService(string directory,IEmailTransport? transport=null)
{
    private readonly object settingsGate=new();
    private readonly SemaphoreSlim sending=new(1);
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("CRS.Notifications.EmailSettings/v1");
    private string PathName=>Path.Combine(directory,"email-settings.dpapi");
    private sealed record Stored(EmailSettings Settings,string Password);
    public EmailSettings Read()
    {lock(settingsGate)return ReadStored().Settings;}
    private Stored ReadStored()
    {
        if(!File.Exists(PathName))return new(new(),"");
        var encrypted=File.ReadAllBytes(PathName);
        if(encrypted.Length>32768)throw new InvalidDataException("邮件配置长度异常。");
        var bytes=ProtectedData.Unprotect(encrypted,Entropy,DataProtectionScope.LocalMachine);
        try{return JsonSerializer.Deserialize<Stored>(bytes)??throw new InvalidDataException("邮件配置无效。");}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public EmailSettings Save(EmailSettingsUpdate update)
    {
        if(update.Settings is null)throw new InvalidDataException("邮件配置为空。");
        lock(settingsGate)
        {
            var old=ReadStored();if(update.Settings.Revision!=old.Settings.Revision)throw new InvalidDataException("邮件设置已变更，请刷新后重试。");
            var secret=update.ClearPassword?"":string.IsNullOrEmpty(update.Password)?old.Password:update.Password;
            if(secret.Length>2048)throw new InvalidDataException("邮件授权码过长。");
            var settings=Validate(update.Settings with{HasPassword=secret.Length>0,Revision=old.Settings.Revision+1});
            if(settings.Enabled&&settings.UserName.Length>0&&secret.Length==0)throw new InvalidDataException("SMTP登录需要已保存的授权码或密码。");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new Stored(settings,secret));
            try
            {
                var encrypted=ProtectedData.Protect(bytes,Entropy,DataProtectionScope.LocalMachine);var temporary=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(encrypted);stream.Flush(true);}File.Move(temporary,PathName,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            finally{CryptographicOperations.ZeroMemory(bytes);}
            return settings;
        }
    }
    private static EmailSettings Validate(EmailSettings settings)
    {
        if(settings.Host is null||settings.Security is null||settings.From is null||settings.UserName is null)throw new InvalidDataException("邮件字段不能为null。");
        if(settings.Security is not ("StartTls" or "SslOnConnect")||settings.Port is <1 or >65535||settings.Host.Length>253||settings.UserName.Length>256
            ||settings.Host.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c))||settings.UserName.Any(char.IsControl))throw new InvalidDataException("SMTP服务器、端口或TLS方式无效。");
        var recipients=settings.Recipients??[];
        if(recipients.Length>10)throw new InvalidDataException("最多配置10个收件地址。");
        bool Address(string? value)=>value is {Length:>0 and <=254}&&!value.Any(char.IsControl)&&MailboxAddress.TryParse(value,out var parsed)&&parsed.Address==value;
        if(settings.Enabled&&(string.IsNullOrWhiteSpace(settings.Host)||!Address(settings.From)||recipients.Length==0)
            ||settings.From.Length>0&&!Address(settings.From)||recipients.Any(x=>!Address(x)))throw new InvalidDataException("请填写SMTP服务器及有效的发件、收件地址，不使用显示名或换行。");
        return settings with{Recipients=recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()};
    }
    /// <summary>测试仅发送已经保存的配置，必须由用户显式点击；保存设置本身不发邮件。</summary>
    public Task<EmailSendResult> TestAsync(Guid requestId,CancellationToken token)=>SendAsync(new("test."+requestId.ToString("N")+"@crs.local","CRS 邮件通知测试","这是系统设置中的邮件测试。SMTP受理不等于收件人已收到或已阅读。"),token);
    public async Task<EmailSendResult> SendAsync(EmailMessage mail,CancellationToken token)
    {
        if(mail.MessageId.Length is <1 or >160||mail.MessageId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '.' and not '@' and not '-')
            ||mail.Subject.Length is <1 or >200||mail.Subject.Any(char.IsControl)||mail.Body.Length>32768)throw new InvalidDataException("通知内容或消息标识超限。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await sending.WaitAsync(timeout.Token);
        try
        {
            Stored saved;lock(settingsGate)saved=ReadStored();if(!saved.Settings.Enabled)return new("Disabled","EmailNotEnabled");
            return await (transport??new SmtpEmailTransport()).SendAsync(saved.Settings,saved.Password,mail,timeout.Token);
        }
        finally{sending.Release();}
    }
}
