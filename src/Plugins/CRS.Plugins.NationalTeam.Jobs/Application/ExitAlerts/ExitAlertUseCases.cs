using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>预警仍共用既有市场库门槛；联网投递由宿主适配，不能在数据库锁内等待提供商。</summary>
public sealed partial class ExitAlertUseCases : MarketUseCaseScope
{
    private readonly MarketApplicationSession evaluationSession;
    private readonly IRawEvidenceStore raw;
    internal ExitAlertUseCases(MarketApplicationSession session,IRawEvidenceStore raw):base(session)
    {this.raw=raw;evaluationSession=session;}

    public Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOverview(),token);
    public Task<JointExitStudyReport> JointExitStudyAsync(EtfKey key,DateOnly asOf,CancellationToken token)=>ReadPortAsync<IJointExitStudyReadRepository,JointExitStudyReport>(r=>r.JointExitStudy(key,asOf),token);
    public Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertConfiguration(),token);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token)=>SaveAlertConfigurationAsync(value,token,null);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.SaveAlertConfiguration(value,commandId);return true;},token);
    public Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvents(filter),token);
    public Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvent(id)??throw new InvalidDataException("事件不存在。"),token);
    public Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvent(id,sequence)??throw new InvalidDataException("历史事件版本不存在。"),token);
    public Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertInputs(id,sequence),token);
    public Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertChanges(id),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOutbox(),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOutbox(filter),token);
    public Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertDeliveries(id),token);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token)=>RetryAlertNotificationAsync(id,token,null);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.RetryAlertNotification(id,commandId);return true;},token);
    public Task EvaluateAlertsAsync(CancellationToken token)=>EvaluateAlertsAsync(false,token);
    public Task<ExitAlertEvaluationRun> EvaluateAlertsAsync(bool force,CancellationToken token)=>evaluationSession.EvaluateAsync(async()=>
    {
        if(raw is not IRawEvidenceStability stability)throw new NotSupportedException("预警评价需要有界原件稳定租约。");
        long preparationMs=0,commitMs=0,heldMs=0;
        for(var attempt=0;;attempt++)
        {
            token.ThrowIfCancellationRequested();using var evidence=stability.HoldVerifiedEvidence();
            var preparing=System.Diagnostics.Stopwatch.GetTimestamp();
            var version=await RunAsync((IExitAlertEvaluationRepository repository)=>repository.CaptureExitAlertEvaluationVersion(),token).ConfigureAwait(false);
            using var prepared=await evaluationSession.PrepareExitAlertEvaluationAsync(version,force,token).ConfigureAwait(false);
            preparationMs+=(long)System.Diagnostics.Stopwatch.GetElapsedTime(preparing).TotalMilliseconds;
            if(prepared is null)return new ExitAlertEvaluationRun(preparationMs,commitMs,heldMs,attempt+1,true);
            try
            {
                await RunMeasuredAsync((IExitAlertEvaluationRepository repository)=>
                {
                    var committing=System.Diagnostics.Stopwatch.GetTimestamp();
                    try{repository.CommitExitAlertEvaluation(prepared);return true;}
                    finally{commitMs+=(long)System.Diagnostics.Stopwatch.GetElapsedTime(committing).TotalMilliseconds;}
                },token,value=>heldMs+=value).ConfigureAwait(false);
                return new ExitAlertEvaluationRun(preparationMs,commitMs,heldMs,attempt+1,false);
            }
            catch(CRS.Common.Concurrency.WorkAdmissionException error) when(error.Reason=="InputsChanged"&&attempt<2){}
        }
    },token);
    public Task<AlertOutboxItem?> ClaimAlertNotificationAsync(CancellationToken token)=>RunAsync((IExitAlertWriteRepository repository)=>repository.ClaimAlertNotification(),token);
    public Task CompleteAlertNotificationAsync(AlertOutboxItem item,AlertDispatchOutcome result)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.CompleteAlertNotification(item.Id,item.Lease!,result);return true;},CancellationToken.None);
}
