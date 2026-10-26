using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Alert delivery persistence and lease completion.
/// Claim selection still enforces acceptance and subscription rules in the compatibility coordinator.
/// All state transitions and delivery attempts commit on the same existing SQLCipher connection.
/// </summary>
internal sealed class AlertDeliveryStore(IMarketRepositorySession session,AlertOutboxStore outbox)
{
    private readonly IMarketRepositorySession _session=session;
    private readonly AlertOutboxStore _outbox=outbox;

    public List<AlertOutboxItem> DueNotifications(DateTimeOffset now)
    {
        using var query=_session.Command("SELECT payload FROM nt_alert_outbox WHERE state IN ('Pending','Retry','Disabled','Unavailable') AND julianday(next_at)<=julianday($now) ORDER BY julianday(next_at) LIMIT 20",("$now",now.ToString("O")));
        return ReadJson<AlertOutboxItem>(query);
    }

    /// <summary>匹配持久租约后记录结果及每次尝试；受理结果未知不进入自动重试。</summary>
    public void CompleteAlertNotification(string id,string lease,AlertDispatchOutcome outcome)
    {
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();
        var json=_session.Scalar("SELECT payload FROM nt_alert_outbox WHERE id=$id",("$id",id)) as string;
        var row=json is null?null:JsonSerializer.Deserialize<AlertOutboxItem>(json);
        if(row is null||row.State!="InFlight"||row.Lease!=lease)throw new InvalidDataException("通知提交租约不匹配。");
        var failures=outcome.State=="RetryableFailure"?row.Failures+1:row.Failures;
        var state=outcome.State switch{"Accepted"=>"Accepted","RetryableFailure"=>failures>=8?"Failed":"Retry",
            "PermanentFailure"=>"Failed","Disabled"=>"Disabled","Unavailable"=>"Unavailable",_=>"OutcomeUnknown"};
        var delay=state is "Disabled" or "Unavailable"?300:Math.Min(3600,30*Math.Pow(2,Math.Min(failures,7)));
        var at=_session.Now;var safeCode=outcome.Code is {Length:>0 and <=80}&&outcome.Code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?outcome.Code:"ProviderResult";
        var next=row with{State=state,LastCode=safeCode,Lease=null,Failures=failures,NextAttemptAt=CRS.Timing.BeijingClock.Read(_session.Clock).AddSeconds(delay).ToString("O"),AcceptedAt=state=="Accepted"?at:null};
        _outbox.WriteOutbox(next);
        var delivery=new AlertDeliveryRecord(Guid.NewGuid().ToString("N"),id,row.Attempts,at,state,safeCode,outcome.ProviderReceipt is {Length:<=256}?outcome.ProviderReceipt:null);
        _session.Execute("INSERT INTO nt_alert_delivery VALUES($id,$out,$attempt,$at,$p)",("$id",delivery.Id),("$out",id),("$attempt",row.Attempts),("$at",at),("$p",JsonSerializer.Serialize(delivery)));
        transaction.Commit();
    }

    public List<AlertDeliveryRecord> AlertDeliveries(string id)
    {
        using var command=_session.Command("SELECT payload FROM nt_alert_delivery WHERE outbox_id=$id ORDER BY julianday(at) DESC LIMIT 100",("$id",id));return ReadJson<AlertDeliveryRecord>(command);
    }

    /// <summary>只允许重试明确未受理的记录；未知、已受理和正式门槛阻断的记录不能借重试绕过。</summary>
    public void RetryAlertNotification(string id,Guid? commandId=null)
    {
        if(_session.Committed(commandId))return;_session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();
        var json=_session.Scalar("SELECT payload FROM nt_alert_outbox WHERE id=$id",("$id",id)) as string;
        var row=json is null?null:JsonSerializer.Deserialize<AlertOutboxItem>(json);
        if(row is null||row.State is not ("Failed" or "Retry" or "Disabled" or "Unavailable"))throw new InvalidDataException("该通知不允许安全重试，请先核对受理情况或正式数据门槛。");
        _outbox.WriteOutbox(row with{State="Pending",Failures=0,NextAttemptAt=_session.Now,LastCode="ManualRetry"});_session.RecordCommand(commandId,"alert-retry");transaction.Commit();
    }

    private static List<T> ReadJson<T>(SqliteCommand command)
    {
        using var reader=command.ExecuteReader();
        var result=new List<T>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))??throw new InvalidDataException("预警记录损坏。"));
        return result;
    }
}
