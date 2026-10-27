using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Maintenance;
using CRS.Worker.Ipc;
namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private BackgroundResponse HandleCleanup(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
    {
        BackgroundResponse Reply(string code,DataCleanupStatus? status=null)=>new(WorkerProtocol.Version,request.RequestId,code,Payload:status is null?null:JsonSerializer.Serialize(status));
        if(!caller.CanManage)return Reply("Forbidden");
        if(cleanup is null)return Reply("UnsupportedCommand");
        if(maintenance)return Reply("Maintenance");
        token.ThrowIfCancellationRequested();
        if(request.RequestId==Guid.Empty||request.PluginId is not null||request.JobId is not null||request.Cursor is not null||request.ExecutionId is not null||request.Cron is not null||request.TimeZoneId is not null)return Reply("InvalidRequest");
        try
        {
            DataCleanupStatus result;
            if(request.Command=="BeginDataCleanupPreview")
            {
                if(request.Payload is null||request.Payload.Length>1024||request.RelatedRequestId is not null)return Reply("InvalidRequest");
                using var json=JsonDocument.Parse(request.Payload);if(json.RootElement.ValueKind!=JsonValueKind.Object||json.RootElement.EnumerateObject().GroupBy(x=>x.Name).Any(x=>x.Count()>1))return Reply("InvalidRequest");
                var options=JsonSerializer.Deserialize<DataCleanupOptions>(request.Payload,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException();
                result=cleanup.BeginPreview(request.RequestId,caller.Sid,options);
            }
            else
            {
                if(request.Payload is not null||request.RelatedRequestId is not {} related||related==Guid.Empty)return Reply("InvalidRequest");
                result=request.Command=="GetDataCleanupStatus"?cleanup.Status(related,caller.Sid):cleanup.BeginExecute(request.RequestId,related,caller.Sid);
            }
            return Reply("Ok",result);
        }
        catch(UnauthorizedAccessException){return Reply("Forbidden");}
        catch(KeyNotFoundException){return Reply("NotFound");}
        catch(InvalidOperationException){return Reply("Busy");}
        catch(Exception error) when(error is IOException or InvalidDataException or JsonException or FormatException or ArgumentException){return Reply("CleanupUnavailable");}
    }
}
