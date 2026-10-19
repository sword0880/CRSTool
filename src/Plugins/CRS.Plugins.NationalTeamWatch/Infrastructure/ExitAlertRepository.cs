using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>在既有加密市场库内增量建表；事件与Outbox共用事务，不增加数据库或调度器。</summary>
    private void InitializeExitAlertSchema()
    {
        using var transaction=db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS nt_alert_rule(version TEXT PRIMARY KEY,payload TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_alert_configuration(id INTEGER PRIMARY KEY CHECK(id=1),version TEXT NOT NULL REFERENCES nt_alert_rule(version),routes TEXT NOT NULL,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_alert_document(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,url TEXT NOT NULL,hash TEXT NOT NULL,first_seen TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id));
            CREATE TABLE IF NOT EXISTS nt_alert_metadata(market TEXT NOT NULL,ticker TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(market,ticker));
            CREATE TABLE IF NOT EXISTS nt_named_etf_holding(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_risk_event(id TEXT PRIMARY KEY,rule_id TEXT NOT NULL,scope_id TEXT NOT NULL,rule_version TEXT NOT NULL,first_date TEXT NOT NULL,last_date TEXT NOT NULL,lane TEXT NOT NULL,state TEXT NOT NULL,updated_at TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS nt_risk_event_query ON nt_risk_event(lane,state,last_date,updated_at);
            CREATE TABLE IF NOT EXISTS nt_risk_event_change(event_id TEXT NOT NULL REFERENCES nt_risk_event(id),sequence INTEGER NOT NULL,kind TEXT NOT NULL,at TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(event_id,sequence));
            CREATE TABLE IF NOT EXISTS nt_alert_outbox(id TEXT PRIMARY KEY,event_id TEXT NOT NULL REFERENCES nt_risk_event(id),change_no INTEGER NOT NULL,channel_id TEXT NOT NULL,profile_id TEXT NOT NULL,state TEXT NOT NULL,next_at TEXT NOT NULL,payload TEXT NOT NULL,UNIQUE(event_id,change_no,channel_id,profile_id));
            CREATE INDEX IF NOT EXISTS nt_alert_outbox_due ON nt_alert_outbox(state,next_at);
            CREATE TABLE IF NOT EXISTS nt_alert_delivery(id TEXT PRIMARY KEY,outbox_id TEXT NOT NULL REFERENCES nt_alert_outbox(id),attempt INTEGER NOT NULL,at TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_alert_pending(id INTEGER PRIMARY KEY CHECK(id=1),changed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_alert_evaluation(id INTEGER PRIMARY KEY CHECK(id=1),at TEXT NOT NULL);
            """);
        var rules=new ExitAlertRules();Execute("INSERT OR IGNORE INTO nt_alert_rule VALUES($v,$p,$now)",("$v",rules.Version),("$p",JsonSerializer.Serialize(rules)),("$now",Now));
        Execute("INSERT OR IGNORE INTO nt_alert_configuration VALUES(1,$v,$r,0)",("$v",rules.Version),("$r",JsonSerializer.Serialize(new[]{new AlertNotificationRoute()})));
        // 进程退出时正在提交的邮件可能已经被服务商接受，重启后不能自动重复外发。
        foreach(var row in ReadOutbox("state='InFlight'"))WriteOutbox(row with{State="OutcomeUnknown",LastCode="ProcessInterruptedDuringDelivery",Lease=null});
        transaction.Commit();
    }
    private void MarkAlertPending()=>Execute("INSERT INTO nt_alert_pending VALUES(1,$now) ON CONFLICT(id) DO UPDATE SET changed_at=excluded.changed_at",("$now",Now));
    public ExitAlertConfiguration AlertConfiguration()
    {
        using var command=Command("SELECT r.payload,c.routes,c.revision FROM nt_alert_configuration c JOIN nt_alert_rule r ON r.version=c.version WHERE c.id=1");
        using var reader=command.ExecuteReader();if(!reader.Read())throw new InvalidDataException("预警规则缺失。");
        return new(JsonSerializer.Deserialize<ExitAlertRules>(reader.GetString(0))!,JsonSerializer.Deserialize<List<AlertNotificationRoute>>(reader.GetString(1))!,reader.GetInt64(2));
    }
    public void SaveAlertConfiguration(ExitAlertConfiguration value,Guid? commandId=null)
    {
        if(Committed(commandId))return;value.Rules.Validate();
        if(value.Routes.Count>10||value.Routes.Any(x=>!RouteId(x.ChannelId)||!RouteId(x.ProfileId))||value.Routes.Distinct().Count()!=value.Routes.Count)throw new InvalidDataException("通知订阅渠道或Profile无效。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();var old=AlertConfiguration();
        if(value.Revision!=old.Revision)throw new InvalidDataException("预警设置已改变，请刷新。");
        var json=JsonSerializer.Serialize(value.Rules);var existing=Scalar("SELECT payload FROM nt_alert_rule WHERE version=$v",("$v",value.Rules.Version)) as string;
        if(existing is not null&&JsonSerializer.Deserialize<ExitAlertRules>(existing)!=value.Rules)throw new InvalidDataException("规则版本不可覆盖，请登记新版本。");
        Execute("INSERT OR IGNORE INTO nt_alert_rule VALUES($v,$p,$now)",("$v",value.Rules.Version),("$p",json),("$now",Now));
        Execute("UPDATE nt_alert_configuration SET version=$v,routes=$r,revision=revision+1 WHERE id=1",("$v",value.Rules.Version),("$r",JsonSerializer.Serialize(value.Routes)));
        MarkAlertPending();RecordCommand(commandId,"alert-settings");transaction.Commit();
    }
    private static bool RouteId(string value)=>value.Length is >0 and <=40&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    public List<ExitAlertEvent> AlertEvents(ExitAlertFilter filter)
    {
        // 业务筛选先进入SQL，再建立IPC快照；超过上限要求缩小范围，不静默截断。
        using var command=Command("""
            SELECT payload FROM nt_risk_event WHERE ($lane IS NULL OR lane=$lane) AND ($state IS NULL OR state=$state)
            AND ($from IS NULL OR last_date>=$from) AND ($to IS NULL OR first_date<=$to)
            AND ($search IS NULL OR instr(scope_id,$search)>0 OR instr(payload,$search)>0) ORDER BY updated_at DESC LIMIT 1001
            """,("$lane",filter.Lane),("$state",filter.State),("$from",filter.From?.ToString("yyyy-MM-dd")),("$to",filter.To?.ToString("yyyy-MM-dd")),("$search",string.IsNullOrWhiteSpace(filter.Search)?null:filter.Search));
        var result=ReadJson<ExitAlertEvent>(command);if(result.Count>1000)throw new InvalidDataException("事件过多，请缩小日期或状态范围。");return result;
    }
    private static List<T> ReadJson<T>(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        using var reader=command.ExecuteReader();var result=new List<T>();while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))??throw new InvalidDataException("预警记录损坏。"));return result;
    }
    public List<ExitAlertChange> AlertChanges(string id)
    {
        using var command=Command("SELECT event_id,sequence,kind,at,payload FROM nt_risk_event_change WHERE event_id=$id ORDER BY sequence",("$id",id));
        using var reader=command.ExecuteReader();var rows=new List<ExitAlertChange>();while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));return rows;
    }
    public List<AlertOutboxItem> AlertOutbox()=>ReadOutbox("1=1");
    private List<AlertOutboxItem> ReadOutbox(string trustedCondition)
    {
        using var command=Command("SELECT payload FROM nt_alert_outbox WHERE "+trustedCondition+" ORDER BY next_at LIMIT 1001");
        var rows=ReadJson<AlertOutboxItem>(command);if(rows.Count>1000)throw new InvalidDataException("通知记录超过查询上限。");return rows;
    }
    private void WriteOutbox(AlertOutboxItem row)=>Execute("""
        INSERT INTO nt_alert_outbox VALUES($id,$event,$change,$channel,$profile,$state,$next,$p)
        ON CONFLICT(id) DO UPDATE SET state=excluded.state,next_at=excluded.next_at,payload=excluded.payload
        """,("$id",row.Id),("$event",row.EventId),("$change",row.ChangeNo),("$channel",row.ChannelId),("$profile",row.ProfileId),("$state",row.State),("$next",row.NextAttemptAt),("$p",JsonSerializer.Serialize(row)));
}
