using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    /// <summary>提交只读作业、短轮询后读取已冻结分页；失败或取消不返回部分结果，也不重新提交未知作业。</summary>
    private async Task<(List<T> Rows,string? Metadata)> ReadResearchPagesAsync<T>(EtfRequest query,CancellationToken token)
    {
        var capability=await EnsureCapabilitiesAsync(token);
        if(capability is null||!capability.Capabilities.Contains(EtfQueryCapabilities.AsyncResearch,StringComparer.Ordinal))throw new InvalidDataException("后台未声明研究快照作业能力，请更新后台插件。");
        var id=Guid.NewGuid().ToString("N");
        var fingerprint=ContentFingerprint.Sha256(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(query with{Offset=0,Snapshot=null,Count=10})));
        var request=new EtfRequest("research-submit",Revision:id){ResearchRequest=query};
        var completed=false;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromSeconds(EtfResearchTicket.MaximumWaitSeconds));
        try
        {
            while(true)
            {
                var rows=(await ReadPagesAsync<EtfResearchTicket>(request,budget.Token)).Rows;
                if(rows.Count!=1||rows[0].Version!=1||rows[0].Id!=id||rows[0].RequestFingerprint!=fingerprint||rows[0].ExpiresAt<=CRS.Timing.BeijingClock.Now)
                    throw new InvalidDataException("研究作业身份、版本或期限不一致。");
                var ticket=rows[0];
                if(ticket.State=="Ready")
                {
                    if(ticket.Snapshot is not {Length:32} snapshot||!snapshot.All(Uri.IsHexDigit)||ticket.Failure is not null)throw new InvalidDataException("研究结果快照无效。");
                    var result=await ReadPagesAsync<T>(query with{Snapshot=snapshot},budget.Token);completed=true;return result;
                }
                if(ticket.State is not ("Queued" or "Running")||ticket.Snapshot is not null||ticket.Failure is not null)
                    throw new InvalidDataException("研究快照未完成："+ticket.State+" / "+ticket.Failure);
                request=request with{Operation="research-status"};
                await Task.Delay(2000,budget.Token);
            }
        }
        finally
        {
            // 界面退出只取消本次研究，后台采集和持久写命令不受影响；清理失败由作业TTL兜底。
            var release=ReleaseResearchAsync(request with{Operation="research-release"});
            if(completed)await release;
        }
    }
    private async Task ReleaseResearchAsync(EtfRequest request)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try{await ReadPagesAsync<string>(request,timeout.Token);}
        catch(Exception error) when(error is OperationCanceledException or InvalidDataException or CRS.Worker.Ipc.BackgroundServiceException or UnauthorizedAccessException or CRS.Common.Concurrency.WorkAdmissionException){}
    }
}
