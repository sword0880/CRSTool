// 后台暂存文件控制入口，按调用者和插件隔离上传、封存与读取，避免业务命令直接访问任意本机文件。
using System.Text.Json;
using CRS.IO.Staging;
using CRS.Serialization;
using CRS.Worker.Ipc;
namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private async Task<BackgroundResponse> HandleDocumentsAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
    {
        BackgroundResponse Reply(string code,object? value=null)=>new(WorkerProtocol.Version,request.RequestId,code,Payload:value is null?null:JsonSerializer.Serialize(value));
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(token);
        try
        {
            if(!caller.CanManage)return Reply("Forbidden");if(documentInbox is null)return Reply("UnsupportedCommand");if(fault is not null)return Reply(fault);if(maintenance)return Reply("Maintenance");
            if(request.RequestId==Guid.Empty||request.PluginId is not {} plugin||!catalog.CanDispatch(plugin)||request.JobId is not null||request.Cursor is not null||request.ExecutionId is not null||request.Cron is not null||request.TimeZoneId is not null)return Reply("InvalidRequest");
            activeQueries.Add(completed);
        }
        finally{gate.Release();}
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,queryStopping.Token);
        try
        {
            var store=documentInbox!.For(request.PluginId!);object result;
            if(request.Command=="PutPluginDocument")
            {
                if(request.Payload is null||request.RelatedRequestId is not null)return Reply("InvalidRequest");
                var upload=StrictJson.Read<DocumentUploadRequest>(request.Payload,32768);
                result=await Task.Run(()=>store.Apply(upload,caller.Sid,cancellation.Token),cancellation.Token);
                if(upload.Action!="Chunk")
                {
                    await gate.WaitAsync(CancellationToken.None);
                    try{repository.Audit(caller.Sid,request.RequestId,"PutPluginDocument."+upload.Action,"Ok");}finally{gate.Release();}
                }
            }
            else
            {
                if(request.Payload is not null)return Reply("InvalidRequest");
                result=request.Command=="ListPluginDocuments"&&request.RelatedRequestId is null
                    ?await Task.Run(()=>store.List(caller.Sid,cancellation.Token).Where(x=>x.State is "Uploading" or "Ready").ToArray(),cancellation.Token)
                    :request.Command=="GetPluginDocument"&&request.RelatedRequestId is {} id?await Task.Run(()=>store.Status(id,caller.Sid,cancellation.Token),cancellation.Token)
                    :throw new InvalidDataException("暂存查询参数不符。");
            }
            return Reply("Ok",result);
        }
        catch(UnauthorizedAccessException){return Reply("Forbidden");}
        catch(FileNotFoundException){return Reply("NotFound");}
        catch(DirectoryNotFoundException){return Reply("NotFound");}
        catch(InvalidOperationException){return Reply("Busy");}
        catch(InvalidDataException){return Reply("InvalidRequest");}
        catch(IOException){return Reply("DocumentInboxUnavailable");}
        catch(System.Data.Common.DbException)
        {
            await gate.WaitAsync(CancellationToken.None);try{await TripFaultAsync("StorageFault");}finally{gate.Release();}return Reply("StorageFault");
        }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);try{activeQueries.Remove(completed);completed.SetResult();}finally{gate.Release();}
        }
    }
}
