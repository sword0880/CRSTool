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
