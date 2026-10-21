using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace : IEtfDashboardWorkspace
{
    public async Task<EtfDashboard?> DashboardAsync(EtfKey? key,CancellationToken token)
    {
        try
        {
            var result=await ReadPagesAsync<EtfDashboardRow>(new("dashboard",key?.Market,key?.Ticker),token);
            if(result.Rows.Any(x=>(x.Overview is null?0:1)+(x.History is null?0:1)+(x.Activity is null?0:1)!=1))
                throw new InvalidDataException("整页查询行类型不正确。");
            NetworkStatus=JsonSerializer.Deserialize<string>(result.Metadata??"null")??throw new InvalidDataException("整页查询缺少来源状态。");
            return new(result.Rows.Where(x=>x.Overview is not null).Select(x=>x.Overview!).ToList(),
                result.Rows.Where(x=>x.History is not null).Select(x=>x.History!).ToList(),
                result.Rows.Where(x=>x.Activity is not null).Select(x=>x.Activity!).ToList());
        }
        catch(UnsupportedDashboardException){return null;}
    }
    private async Task ReleaseSnapshotAsync(string snapshot)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try{await QueryAsync<string>(new("release-query",Snapshot:snapshot),timeout.Token);}
        catch(Exception error) when(error is OperationCanceledException or InvalidDataException or CRS.Worker.Ipc.BackgroundServiceException or UnauthorizedAccessException)
        { /* 旧后台或断连时由服务端快照期限兜底，不覆盖用户的新查询状态。 */ }
    }
    private sealed class UnsupportedDashboardException:Exception;
}
