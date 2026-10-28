using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>本功能声明自己的公开查询、持久任务及执行组，入口仅负责授权协议和生命周期。</summary>
public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamJob("evidence-integrity","证据完整性巡检","0 0/5 * * * ?","China Standard Time",true,true,Group="collection",MaximumConcurrency=2,Order=0)]
    private async Task<BackgroundJobResult> ExecuteEvidenceIntegrity(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation is "sync" or "integrity"))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Evidence.ReconcileEvidenceIntegrityAsync(token);
        return BackgroundJobResult.Success("integrity=Reconciled");
    }
}
