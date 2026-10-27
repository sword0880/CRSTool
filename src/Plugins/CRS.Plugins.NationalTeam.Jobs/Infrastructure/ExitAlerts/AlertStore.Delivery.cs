using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class AlertStore
{
    /// <summary>先持久领取再联网；进程中断时InFlight仅转待核实，不能自动再发。</summary>
    public AlertOutboxItem? ClaimAlertNotification()
    {
        var config=AlertConfiguration();if(!config.Rules.Enabled)return null;
        var now=CRS.Timing.BeijingClock.Read(clock);
        var due=alertDeliveryStore.DueNotifications(now);if(due.Count==0)return null;
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
                if(!DeliveryDocumentsHealthy(row))
                {WriteOutbox(row with{State="BlockedAcceptance",LastCode="DisclosureEvidenceUnavailable"});continue;}
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
    /// <summary>领取前核验本条通知冻结的原件，不用今天的新披露替代当次证据；历史更正检查其全部原快照。</summary>
    private bool DeliveryDocumentsHealthy(AlertOutboxItem notification)
    {
        var saved=NotificationInputs(notification);
        if(saved.Length==0||saved.Any(x=>x.Inputs.Length==0||x.Inputs.Any(i=>i.Metadata is not {OriginalConfirmed:true})
            ||x.Holdings.Any(h=>!ExitAlertEvaluator.NamedVerified(h))))return false;
        var references=saved.SelectMany(x=>x.Inputs.Select(i=>Reference(i.Metadata!)).Concat(x.Holdings.Select(Reference))).ToArray();
        return InspectDocuments(references).Values.All(x=>x is not null);
    }
    public ExitAlertEvent? AlertEvent(string id)=>alertEventStore.AlertEvent(id,null);
    public ExitAlertEvent? AlertEvent(string id,int? sequence)=>alertEventStore.AlertEvent(id,sequence);
    /// <summary>按事件版本的实际输入筛选证据后分页，历史修订仍可定位原件与接收时间。</summary>
    public List<CRS.Plugins.NationalTeamWatch.Application.EvidenceRow> AlertInputs(string id,int? sequence=null)
    {
        var row=AlertEvent(id,sequence)??throw new InvalidDataException("事件版本不存在。");ValidateEvidence();
        var ids=row.RevisionIds.Concat(row.HistoricalRevisionIds??[]).ToHashSet(StringComparer.Ordinal);return ReadEvidence().Where(x=>ids.Contains(x.RevisionId)).ToList();
    }
    /// <summary>Reuse the existing lease, outcome and audit protocol through an isolated delivery store.</summary>
    public void CompleteAlertNotification(string id,string lease,AlertDispatchOutcome outcome)=>alertDeliveryStore.CompleteAlertNotification(id,lease,outcome);
    public List<AlertDeliveryRecord> AlertDeliveries(string id)=>alertDeliveryStore.AlertDeliveries(id);
    public void RetryAlertNotification(string id,Guid? commandId=null)=>alertDeliveryStore.RetryAlertNotification(id,commandId);
}
