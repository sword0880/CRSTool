using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Common.Concurrency;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository:IExitAlertEvaluationRepository
{
    public IExitAlertEvaluationVersion CaptureExitAlertEvaluationVersion()=>new ExitAlertEvaluationVersion(database.EvaluationOwner,database.EvaluationVersion(),clock.GetUtcNow());
    public IPreparedExitAlertEvaluation? PrepareExitAlertEvaluation(IExitAlertEvaluationVersion version,bool force)
    {
        if(version is not ExitAlertEvaluationVersion issued||!ReferenceEquals(issued.Owner,database.EvaluationOwner))throw new InvalidDataException("评价版本不属于当前资料库。");
        if(!alertStore.NeedsEvaluation(force))return null;
        var lease=raw.StableEvidence??throw new InvalidOperationException("评价准备缺少原件稳定租约。");
        var plan=new PreparedExitAlertEvaluation(issued,lease);
        try
        {
            using var copy=database.CopyForEvaluation();copy.RecordEvaluationMutation=plan.Record;
            using var prepared=new MarketRepository(copy,marketCycleApprovalAuditKey.ToArray());
            prepared.ExecuteOperation(()=>prepared.EvaluateExitAlerts(force),database.OperationCancellation);
            return plan;
        }
        catch{plan.Dispose();throw;}
    }
    public void CommitExitAlertEvaluation(IPreparedExitAlertEvaluation prepared)
    {
        if(prepared is not PreparedExitAlertEvaluation plan)throw new InvalidDataException("评价准备结果无效。");
        using var stable=plan.PinForCommit();
        var mutations=plan.Consume(database.EvaluationOwner,clock.GetUtcNow());
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        if(database.EvaluationVersion()!=plan.Version.DataVersion)throw new WorkAdmissionException("InputsChanged");
        foreach(var mutation in mutations){operationCancellation.ThrowIfCancellationRequested();database.Execute(mutation.Sql,mutation.Args);}
        operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    // 聚合入口只转发；预警实现、事务及状态由独立模块负责。
    public ExitAlertOverview EvaluateExitAlerts(bool force=false) => alertStore.EvaluateExitAlerts(force);
    public ExitAlertOverview AlertOverview() => alertStore.AlertOverview();
}
