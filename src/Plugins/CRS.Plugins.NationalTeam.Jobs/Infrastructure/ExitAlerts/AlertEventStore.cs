using System.Text.Json;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Exit-alert event journal read/write projection. The caller owns the transaction,
/// so risk event, frozen inputs and notification outbox remain an atomic unit.
/// This store neither opens a writer nor dispatches notifications.
/// </summary>
internal sealed class AlertEventStore(IMarketRepositorySession session)
{
    private readonly IMarketRepositorySession _session=session;

    public List<ExitAlertEvent> AlertEvents(ExitAlertFilter filter)
    {
        if(filter.From>filter.To||filter.Search?.Length>120||filter.Lane is not (null or "DataQuality" or "EtfOutflow" or "InstitutionEvidence")
            ||filter.State is not (null or "Active" or "PendingVerification" or "Resolved" or "RetractedByRevision" or "HistoricalEvidence" or "RuleSuperseded"))throw new InvalidDataException("预警筛选无效，请检查日期、车道或状态。");
        // 业务筛选先进入SQL，再建立IPC快照；超过上限要求缩小范围，不静默截断。
        using var command=_session.Command("""
            SELECT payload FROM nt_risk_event WHERE ($lane IS NULL OR lane=$lane) AND ($state IS NULL OR state=$state)
            AND ($from IS NULL OR last_date>=$from) AND ($to IS NULL OR first_date<=$to)
            AND ($search IS NULL OR instr(scope_id,$search)>0 OR instr(payload,$search)>0) ORDER BY julianday(updated_at) DESC LIMIT 1001
            """,("$lane",filter.Lane),("$state",filter.State),("$from",filter.From?.ToString("yyyy-MM-dd")),("$to",filter.To?.ToString("yyyy-MM-dd")),("$search",string.IsNullOrWhiteSpace(filter.Search)?null:filter.Search));
        var result=ReadJson<ExitAlertEvent>(command);if(result.Count>1000)throw new InvalidDataException("事件过多，请缩小日期或状态范围。");return result;
    }
    public List<ExitAlertChange> AlertChanges(string id)
    {
        using var command=_session.Command("SELECT event_id,sequence,kind,at,payload FROM nt_risk_event_change WHERE event_id=$id ORDER BY sequence",("$id",id));
        using var reader=command.ExecuteReader();var rows=new List<ExitAlertChange>();while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));return rows;
    }
    public ExitAlertEvent? AlertEvent(string id)
        =>AlertEvent(id,null);
    public ExitAlertEvent? AlertEvent(string id,int? sequence)
    {
        var json=sequence is {} number?_session.Scalar("SELECT payload FROM nt_risk_event_change WHERE event_id=$id AND sequence=$n",("$id",id),("$n",number)) as string
            :_session.Scalar("SELECT payload FROM nt_risk_event WHERE id=$id",("$id",id)) as string;
        return json is null?null:JsonSerializer.Deserialize<ExitAlertEvent>(json);
    }
    public ExitAlertEvent? CurrentAlert(ExitAlertFinding finding,string version)
    {
        using var command=_session.Command("SELECT payload FROM nt_risk_event WHERE rule_id=$r AND scope_id=$s AND rule_version=$v AND state NOT IN ('Resolved','RetractedByRevision','HistoricalEvidence','RuleSuperseded') ORDER BY julianday(updated_at) DESC LIMIT 1",("$r",finding.RuleId),("$s",finding.ScopeId),("$v",version));
        var json=command.ExecuteScalar() as string;return json is null?null:JsonSerializer.Deserialize<ExitAlertEvent>(json);
    }

    /// <summary>Persist event and change journal inside the caller's existing transaction.</summary>
    public void RecordChange(ExitAlertEvent row,string kind)
    {
        _session.Execute("""
            INSERT INTO nt_risk_event VALUES($id,$r,$s,$v,$first,$last,$lane,$state,$at,$p)
            ON CONFLICT(id) DO UPDATE SET last_date=excluded.last_date,state=excluded.state,updated_at=excluded.updated_at,payload=excluded.payload
            """,("$id",row.Id),("$r",row.RuleId),("$s",row.ScopeId),("$v",row.RuleVersion),("$first",row.FirstTradeDate.ToString("yyyy-MM-dd")),("$last",row.LastTradeDate.ToString("yyyy-MM-dd")),("$lane",row.Lane.ToString()),("$state",row.State),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)));
        _session.Execute("INSERT INTO nt_risk_event_change VALUES($id,$n,$kind,$at,$p)",("$id",row.Id),("$n",row.ChangeNo),("$kind",kind),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)));
    }

    private static List<T> ReadJson<T>(SqliteCommand command)
    {
        using var reader=command.ExecuteReader();
        var result=new List<T>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))??throw new InvalidDataException("预警记录损坏。"));
        return result;
    }
}
