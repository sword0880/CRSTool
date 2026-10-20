using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
namespace CRS.Notifications;

/// <summary>公共SMTP传输，仅处理TLS、认证和受理结果，不认识Worker、减持预警、调度或税务。</summary>
public sealed class SmtpEmailTransport:IEmailTransport
{
    public async Task<EmailSendResult> SendAsync(EmailSettings settings,string password,EmailMessage mail,CancellationToken token)
    {
        if(settings.Security is not ("StartTls" or "SslOnConnect"))throw new InvalidDataException("SMTP必须明确指定强制TLS方式。");
        using var client=new SmtpClient();var submitting=false;
        try
        {
            // 强制TLS且保持系统证书验证；不提供忽略证书或静默降级到明文的选项。
            await client.ConnectAsync(settings.Host,settings.Port,settings.Security=="StartTls"?SecureSocketOptions.StartTls:SecureSocketOptions.SslOnConnect,token);
            if(settings.UserName.Length>0)await client.AuthenticateAsync(settings.UserName,password,token);
            var message=new MimeMessage{MessageId=mail.MessageId,Subject=mail.Subject,Body=new TextPart("plain"){Text=mail.Body}};
            message.From.Add(new MailboxAddress("小财牛通知",settings.From));foreach(var recipient in settings.Recipients??[])message.To.Add(new MailboxAddress("",recipient));
            submitting=true;await client.SendAsync(message,token);
            return new("SmtpAccepted","ServerAccepted",mail.MessageId);
        }
        catch(MailKit.Security.AuthenticationException){return new("Failed","AuthenticationFailed");}
        catch(SslHandshakeException){return new("Failed","TlsVerificationFailed");}
        catch(NotSupportedException){return new("Failed","SmtpCapabilityNotSupported");}
        catch(SmtpCommandException error){return new((int)error.StatusCode>=500?"Failed":"Retry","Smtp"+(int)error.StatusCode);}
        catch(Exception error) when(error is IOException or OperationCanceledException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or SmtpProtocolException or ServiceNotConnectedException)
        {
            // DATA提交后的网络中断无法确认是否已经受理，由上层记录待核实，不能自动承诺恰好一次。
            return new(submitting?"UnknownOutcome":"Retry",error is OperationCanceledException?"TimeoutOrCancelled":"ConnectionFailed",mail.MessageId);
        }
    }
}
