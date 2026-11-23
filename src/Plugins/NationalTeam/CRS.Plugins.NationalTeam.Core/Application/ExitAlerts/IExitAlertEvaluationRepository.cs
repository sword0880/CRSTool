namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>仅后台内存能力，不进入IPC；版本与准备结果由仓储签发，调用方不能填充SQL或金融结论。</summary>
public interface IExitAlertEvaluationVersion {}
public interface IPreparedExitAlertEvaluation:IDisposable {}
/// <summary>准备包含准入和独立读取；提交体仅计算取得单写名额后执行版本核对及事务的时间。</summary>
public sealed record ExitAlertEvaluationRun(long PreparationMilliseconds,long CommitBodyMilliseconds,long CommitHeldMilliseconds,int Attempts,bool Skipped);
public interface IExitAlertEvaluationRepository
{
    IExitAlertEvaluationVersion CaptureExitAlertEvaluationVersion();
    IPreparedExitAlertEvaluation? PrepareExitAlertEvaluation(IExitAlertEvaluationVersion version,bool force);
    void CommitExitAlertEvaluation(IPreparedExitAlertEvaluation prepared);
}
