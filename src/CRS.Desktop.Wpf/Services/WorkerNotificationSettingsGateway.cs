using System.Text.Json;
using System.IO;
using CRS.Application.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
namespace CRS.DesktopClient.Services;

/// <summary>仅为应用设置端口适配授权IPC，不连接SMTP、不保存口令、不引用公共组件的实现。</summary>
public sealed class WorkerNotificationSettingsGateway(BackgroundPipeClient? connection=null):INotificationSettingsGateway
{
    private readonly BackgroundPipeClient client=connection??new();
    public async Task<NotificationEmailSettings> LoadAsync(CancellationToken token)=>Map(await Send<WorkerEmailSettings>("GetEmailSettings",null,token));
    public async Task<NotificationEmailSettings> SaveAsync(NotificationEmailEdit edit,CancellationToken token)
    {
        var recipients=edit.Recipients.Split([';',',','\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var update=new WorkerEmailUpdate(new(edit.Enabled,edit.Host.Trim(),edit.Port,edit.Security,edit.From.Trim(),recipients,edit.UserName.Trim(),Revision:edit.Revision),edit.Password,edit.ClearPassword);
        return Map(await Send<WorkerEmailSettings>("SaveEmailSettings",JsonSerializer.Serialize(update),token));
    }
    public async Task<NotificationSendResult> TestAsync(CancellationToken token)
    {
        var result=await Send<BackgroundMailResult>("TestEmailSettings",null,token);return new(result.State,result.Code);
    }
    private async Task<T> Send<T>(string command,string? payload,CancellationToken token)
    {
        var response=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),command,Payload:payload),token);
        if(response.Code!="Ok"||response.Payload is null)throw new InvalidDataException(response.Code switch
        {
            "Forbidden"=>"邮件设置需要管理员权限。",
            "UnknownCommand" or "NotificationChannelUnavailable"=>"后台尚未支持系统邮件设置，请更新配套服务。",
            "InvalidEmailSettings"=>"邮件参数无效或已被其它窗口修改；请刷新并检查SMTP、地址、TLS及授权码。",
            _=>"邮件设置服务不可用；类别："+response.Code
        });
        return JsonSerializer.Deserialize<T>(response.Payload)??throw new InvalidDataException("邮件设置返回为空。");
    }
    private static NotificationEmailSettings Map(WorkerEmailSettings x)=>new(x.Enabled,x.Host,x.Port,x.Security,x.From,string.Join("; ",x.Recipients??[]),x.UserName,x.HasPassword,x.Revision);
}
