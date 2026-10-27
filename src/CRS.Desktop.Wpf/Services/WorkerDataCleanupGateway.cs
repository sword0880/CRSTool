using System.Security.Principal;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Maintenance;
using CRS.Worker.Ipc;
namespace CRS.DesktopClient.Services;

/// <summary>只传固定清理选项和操作编号；查询停止或超时不会取消已受理的后台清理。</summary>
public sealed class WorkerDataCleanupGateway:IDataCleanupGateway
{
    private readonly BackgroundPipeClient client=new();
    public bool CanExecute{get{using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
    public async Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token)
    {
        var id=Guid.NewGuid();var result=await SendAsync(new(WorkerProtocol.Version,id,"BeginDataCleanupPreview",Payload:JsonSerializer.Serialize(options)),id,token);
        return await WaitAsync(result,progress,token);
    }
    public async Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token)
    {
        var result=await SendAsync(new(WorkerProtocol.Version,executionId,"ExecuteDataCleanup",RelatedRequestId:previewId),executionId,token);
        return await WaitAsync(result,progress,token);
    }
    public async Task<DataCleanupStatus> StatusAsync(Guid operationId,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>await WaitAsync(await ReadAsync(operationId,token),progress,token);
    private Task<DataCleanupStatus> ReadAsync(Guid id,CancellationToken token)=>SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupStatus",RelatedRequestId:id),id,token);
    private async Task<DataCleanupStatus> SendAsync(BackgroundRequest request,Guid expected,CancellationToken token)
    {
        var reply=await client.SendAsync(request,token);
        if(reply.Code!="Ok"||reply.Payload is null)throw new InvalidOperationException(reply.Code switch{"UnknownCommand" or "UnsupportedCommand"=>"当前后台版本不支持资料清理，需要更新配套后台。","Forbidden"=>"资料清理需要以管理员身份运行 CRS。","Busy"=>"后台已有扫描或清理正在执行，请稍后查询。","NotFound"=>"操作记录不存在；请重新扫描。",_=>"后台未确认清理状态（"+reply.Code+"），请查询结果。"});
        var status=JsonSerializer.Deserialize<DataCleanupStatus>(reply.Payload)??throw new InvalidOperationException("后台返回了无效的清理状态。");
        if(status.FormatVersion!=1||status.OperationId!=expected||status.Entries.Count>128||status.DeletedBytes<0||status.DeletedFiles<0)throw new InvalidOperationException("清理状态版本或操作编号不匹配。");return status;
    }
    private async Task<DataCleanupStatus> WaitAsync(DataCleanupStatus status,IProgress<DataCleanupStatus>? progress,CancellationToken token)
    {
        var until=DateTimeOffset.UtcNow.AddMinutes(3);
        while(status.State is "Scanning" or "Running")
        {
            progress?.Report(status);if(DateTimeOffset.UtcNow>=until)return status with{Message="后台仍在处理；可稍后点击“查询清理结果”。当前不能视为完成。"};
            await Task.Delay(TimeSpan.FromSeconds(2),token);status=await ReadAsync(status.OperationId,token);
        }
        progress?.Report(status);return status;
    }
}
