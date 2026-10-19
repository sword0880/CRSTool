using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>先持久领取再联网；进程中断时InFlight仅转待核实，不能自动再发。</summary>
    public AlertOutboxItem? ClaimAlertNotification()
    {
        var config=AlertConfiguration();if(!config.Rules.Enabled)return null;
        var now=clock.GetUtcNow();
        using var query=Command("SELECT payload FROM nt_alert_outbox WHERE state IN ('Pending','Retry','Disabled','Unavailable') AND next_at<=$now ORDER BY next_at LIMIT 20",("$now",now.ToString("O")));
        var due=ReadJson<AlertOutboxItem>(query);if(due.Count==0)return null;
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        foreach(var row in due)
        {
            var current=AlertEvent(row.EventId);
            if(current is null||row.Kind!="HistoricalCorrection"&&(current.State=="RuleSuperseded"||current.RuleVersion!=config.Rules.Version)
                ||current.State is ("Resolved" or "RetractedByRevision" or "HistoricalEvidence")&&row.ChangeNo!=current.ChangeNo||!config.Routes.Contains(new(row.ChannelId,row.ProfileId)))
            {WriteOutbox(row with{State="Superseded",LastCode="EventOrSubscriptionChanged"});continue;}
            // 发送前重验正式门槛；不能借以前的门槛状态发出当前已失效的金融提醒。
            if(current.Lane!=AlertLane.DataQuality)
            {
                var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now,"China Standard Time").DateTime);
                var acceptance=Acceptance(today.AddDays(-1));
                if(current.Preview||current.DataHealth!="Complete"||acceptance.Count==0||acceptance.Any(x=>!x.DataAcceptancePassed))
                {WriteOutbox(row with{State="BlockedAcceptance",LastCode="DataAcceptanceNotPassed"});continue;}
            }
            var claimed=row with{State="InFlight",Attempts=row.Attempts+1,Lease=Guid.NewGuid().ToString("N")};
            WriteOutbox(claimed);transaction.Commit();return claimed;
        }
        transaction.Commit();return null;
    }
    public ExitAlertEvent? AlertEvent(string id)
        =>AlertEvent(id,null);
    public ExitAlertEvent? AlertEvent(string id,int? sequence)
    {
        var json=sequence is {} number?Scalar("SELECT payload FROM nt_risk_event_change WHERE event_id=$id AND sequence=$n",("$id",id),("$n",number)) as string
            :Scalar("SELECT payload FROM nt_risk_event WHERE id=$id",("$id",id)) as string;
        return json is null?null:JsonSerializer.Deserialize<ExitAlertEvent>(json);
    }
    /// <summary>按事件版本的实际输入筛选证据后分页，历史修订仍可定位原件与接收时间。</summary>
    public List<CRS.Plugins.NationalTeamWatch.Application.EvidenceRow> AlertInputs(string id,int? sequence=null)
    {
        var row=AlertEvent(id,sequence)??throw new InvalidDataException("事件版本不存在。");ValidateEvidence();
        var ids=row.RevisionIds.Concat(row.HistoricalRevisionIds??[]).ToHashSet(StringComparer.Ordinal);return ReadEvidence().Where(x=>ids.Contains(x.RevisionId)).ToList();
    }
    /// <summary>匹配持久租约后记录结果及每次尝试；受理结果未知不进入自动重试。</summary>
    public void CompleteAlertNotification(string id,string lease,AlertDispatchOutcome outcome)
    {
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var json=Scalar("SELECT payload FROM nt_alert_outbox WHERE id=$id",("$id",id)) as string;
        var row=json is null?null:JsonSerializer.Deserialize<AlertOutboxItem>(json);
        if(row is null||row.State!="InFlight"||row.Lease!=lease)throw new InvalidDataException("通知提交租约不匹配。");
        var failures=outcome.State=="RetryableFailure"?row.Failures+1:row.Failures;
        var state=outcome.State switch{"Accepted"=>"Accepted","RetryableFailure"=>failures>=8?"Failed":"Retry",
            "PermanentFailure"=>"Failed","Disabled"=>"Disabled","Unavailable"=>"Unavailable",_=>"OutcomeUnknown"};
        var delay=state is "Disabled" or "Unavailable"?300:Math.Min(3600,30*Math.Pow(2,Math.Min(failures,7)));
        var at=Now;var safeCode=outcome.Code is {Length:>0 and <=80}&&outcome.Code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?outcome.Code:"ProviderResult";
        var next=row with{State=state,LastCode=safeCode,Lease=null,Failures=failures,NextAttemptAt=clock.GetUtcNow().AddSeconds(delay).ToString("O"),AcceptedAt=state=="Accepted"?at:null};
        WriteOutbox(next);
        var delivery=new AlertDeliveryRecord(Guid.NewGuid().ToString("N"),id,row.Attempts,at,state,safeCode,outcome.ProviderReceipt is {Length:<=256}?outcome.ProviderReceipt:null);
        Execute("INSERT INTO nt_alert_delivery VALUES($id,$out,$attempt,$at,$p)",("$id",delivery.Id),("$out",id),("$attempt",row.Attempts),("$at",at),("$p",JsonSerializer.Serialize(delivery)));
        transaction.Commit();
    }
    public List<AlertDeliveryRecord> AlertDeliveries(string id)
    {
        using var command=Command("SELECT payload FROM nt_alert_delivery WHERE outbox_id=$id ORDER BY at DESC LIMIT 100",("$id",id));return ReadJson<AlertDeliveryRecord>(command);
    }
    /// <summary>只允许重试明确未受理的记录；未知、已受理和正式门槛阻断的记录不能借重试绕过。</summary>
    public void RetryAlertNotification(string id,Guid? commandId=null)
    {
        if(Committed(commandId))return;beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var json=Scalar("SELECT payload FROM nt_alert_outbox WHERE id=$id",("$id",id)) as string;
        var row=json is null?null:JsonSerializer.Deserialize<AlertOutboxItem>(json);
        if(row is null||row.State is not ("Failed" or "Retry" or "Disabled" or "Unavailable"))throw new InvalidDataException("该通知不允许安全重试，请先核对受理情况或正式数据门槛。");
        WriteOutbox(row with{State="Pending",Failures=0,NextAttemptAt=Now,LastCode="ManualRetry"});RecordCommand(commandId,"alert-retry");transaction.Commit();
    }
}
