using System.Text.Json;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Isolates outbox SQL and restart recovery. Notification lifecycle and transport remain in existing Worker use cases.
/// Never accepts arbitrary SQL filters or introduces a second writer/transaction boundary.
/// </summary>
internal sealed class AlertOutboxStore(IMarketRepositorySession session)
{
    private readonly IMarketRepositorySession _session=session;
    private static List<T> ReadJson<T>(SqliteCommand command)
    {
        using var reader=command.ExecuteReader();
        var result=new List<T>();
        while(reader.Read())
            result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))??throw new InvalidDataException("预警记录损坏。"));
        return result;
    }

    public List<AlertOutboxItem> AlertOutbox(ExitAlertFilter? filter=null)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(_session.Clock).DateTime);
        filter??=new(From:today.AddDays(-31),To:today);
        if(filter.From>filter.To||filter.Search?.Length>120||filter.State is not (null or "Pending" or "InFlight" or "Accepted" or "Retry" or "Disabled" or "Unavailable" or "Failed" or "OutcomeUnknown" or "BlockedAcceptance" or "Superseded"))throw new InvalidDataException("通知筛选无效。");
        string? Start(DateOnly? day)=>day is {} value?new DateTimeOffset(value.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(8)).ToString("O"):null;
        using var command=_session.Command("""
            SELECT payload FROM nt_alert_outbox WHERE ($state IS NULL OR state=$state)
            AND ($from IS NULL OR julianday(json_extract(payload,'$.CreatedAt'))>=julianday($from)) AND ($to IS NULL OR julianday(json_extract(payload,'$.CreatedAt'))<julianday($to))
            AND ($search IS NULL OR instr(payload,$search)>0) ORDER BY julianday(json_extract(payload,'$.CreatedAt')) DESC LIMIT 1001
            """,("$state",filter.State),("$from",Start(filter.From)),("$to",Start(filter.To?.AddDays(1))),("$search",string.IsNullOrWhiteSpace(filter.Search)?null:filter.Search));
        var rows=ReadJson<AlertOutboxItem>(command);if(rows.Count>1000)throw new InvalidDataException("通知记录过多，请缩小日期、状态或搜索范围。");return rows;
    }

    // Do not accept SQL fragments from callers; restart recovery has one fixed state query.
    public List<AlertOutboxItem> ReadInFlight()
    {
        using var command=_session.Command("SELECT payload FROM nt_alert_outbox WHERE state='InFlight' ORDER BY julianday(next_at) LIMIT 1001");
        var rows=ReadJson<AlertOutboxItem>(command);
        if(rows.Count>1000)throw new InvalidDataException("通知记录超过查询上限。");
        return rows;
    }

    public void WriteOutbox(AlertOutboxItem row)=>_session.Execute("""
        INSERT INTO nt_alert_outbox VALUES($id,$event,$change,$channel,$profile,$state,$next,$p)
        ON CONFLICT(id) DO UPDATE SET state=excluded.state,next_at=excluded.next_at,payload=excluded.payload
        """,("$id",row.Id),("$event",row.EventId),("$change",row.ChangeNo),("$channel",row.ChannelId),("$profile",row.ProfileId),("$state",row.State),("$next",row.NextAttemptAt),("$p",JsonSerializer.Serialize(row)));
}
