using System.Text;
using System.Text.RegularExpressions;
using CRS.Common.Concurrency;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Application;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>受信后台的单次内存能力；没有可由IPC填充的金融结论或SQL入口。</summary>
internal sealed record ExitAlertEvaluationVersion(object Owner,long DataVersion,DateTimeOffset CapturedAt):IExitAlertEvaluationVersion;
internal sealed class PreparedExitAlertEvaluation(ExitAlertEvaluationVersion version,VerifiedContentLease evidence):IPreparedExitAlertEvaluation
{
    internal sealed record Mutation(string Sql,(string Name,object? Value)[] Args);
    private static readonly HashSet<string> AllowedTables=["nt_risk_event","nt_risk_event_change","nt_alert_input_snapshot","nt_alert_snapshot_revision","nt_alert_snapshot_source","nt_alert_notification_input","nt_alert_outbox","nt_alert_evaluation","nt_alert_pending"];
    private readonly List<Mutation> mutations=[];
    private int consumed;private long bytes;
    internal ExitAlertEvaluationVersion Version=>version;
    internal IDisposable PinForCommit()=>evidence.PinForCommit();
    internal IReadOnlyList<Mutation> Consume(object owner,DateTimeOffset now)
    {
        if(!ReferenceEquals(owner,version.Owner)||!evidence.IsActive)throw new InvalidDataException("评价结果不属于当前资料库，或原件稳定租约已结束。");
        if(Interlocked.Exchange(ref consumed,1)!=0)throw new InvalidOperationException("评价结果只能提交一次。");
        if(now<version.CapturedAt||DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime)!=DateOnly.FromDateTime(version.CapturedAt.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new WorkAdmissionException("InputsChanged");
        return mutations;
    }
    internal void Record(string sql,(string Name,object? Value)[] args)
    {
        var match=Regex.Match(sql,@"^\s*(?:INSERT(?:\s+OR\s+(?:IGNORE|REPLACE))?\s+INTO|UPDATE|DELETE\s+FROM)\s+([a-z_]+)\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        if(!match.Success||!AllowedTables.Contains(match.Groups[1].Value.ToLowerInvariant())||sql.Trim().TrimEnd(';').Contains(';'))throw new InvalidDataException("评价准备阶段尝试修改非预警资料。");
        if(args.Any(x=>x.Value is not (null or string or int or long or bool or double or decimal)))throw new InvalidDataException("评价准备参数类型无效。");
        bytes+=Encoding.UTF8.GetByteCount(sql)+args.Sum(x=>(long)Encoding.UTF8.GetByteCount(x.Name)+(x.Value is string text?Encoding.UTF8.GetByteCount(text):16));
        if(mutations.Count>=100000||bytes>32L*1024*1024)throw new InvalidDataException("评价准备结果超过容量上限。");
        mutations.Add(new(sql,args.ToArray()));
    }
    public void Dispose(){Interlocked.Exchange(ref consumed,1);mutations.Clear();}
}
