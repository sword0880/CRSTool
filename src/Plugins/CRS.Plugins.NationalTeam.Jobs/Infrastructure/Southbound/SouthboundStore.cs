using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>完整原件快照追加登记；读取重新核验原件并重建年度曲线，不混合不同采集批次。</summary>
internal sealed class SouthboundStore(MarketDatabaseSession session)
{
    private sealed record PageReference(string Url,string Hash,DateTimeOffset RetrievedAt);
    private sealed record Manifest(DateOnly From,DateOnly Through,IReadOnlyList<PageReference> Pages);
    public void InitializeSchema()=>session.Execute("""
        CREATE TABLE IF NOT EXISTS nt_southbound_capture(id TEXT PRIMARY KEY,from_date TEXT NOT NULL,through_date TEXT NOT NULL,registered_at TEXT NOT NULL,manifest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS nt_southbound_command(request TEXT PRIMARY KEY,capture_id TEXT NOT NULL REFERENCES nt_southbound_capture(id));
        """);
    public string? Receipt(Guid id)=>session.Scalar("SELECT capture_id FROM nt_southbound_command WHERE request=$id",("$id",id.ToString("N"))) as string;
    public string Save(SouthboundCapture capture,Guid? commandId)
    {
        if(commandId is {} request&&Receipt(request) is {} prior)return prior;
        var now=session.Clock.GetUtcNow();
        if(capture.Through>DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime)||capture.Pages.Any(x=>x.RetrievedAt>now||x.RetrievedAt<now.AddHours(-1)))throw new InvalidDataException("南向采集时间或数据截止日期无效。");
        _=SouthboundHistorySource.ParseCapture(capture);
        var refs=capture.Pages.Select(x=>new PageReference(x.Url,session.RawEvidence.Save(x.Content,session.OperationCancellation),x.RetrievedAt)).ToArray();
        var manifest=JsonSerializer.Serialize(new Manifest(capture.From,capture.Through,refs));var id=Hash(manifest);
        session.BeforeWrite?.Invoke();using var tx=session.Connection.BeginTransaction();
        session.Execute("INSERT OR IGNORE INTO nt_southbound_capture VALUES($id,$from,$through,$now,$manifest)",("$id",id),("$from",capture.From.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$through",capture.Through.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$now",session.Now),("$manifest",manifest));
        if(commandId is {} command){session.Execute("INSERT INTO nt_southbound_command VALUES($request,$id)",("$request",command.ToString("N")),("$id",id));session.RecordCommand(commandId,"southbound-sync");}
        session.OperationCancellation.ThrowIfCancellationRequested();tx.Commit();return id;
    }
    public SouthboundAnnualReport Annual(int first,int last)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(session.Clock).DateTime);SouthboundAnnualComparison.ValidateYears(first,last,today.Year);
        using var cmd=session.Command("SELECT id,manifest FROM nt_southbound_capture ORDER BY registered_at DESC,rowid DESC LIMIT 1");using var reader=cmd.ExecuteReader();
        if(!reader.Read())return SouthboundAnnualComparison.Build(first,last,today,null,null,[]);
        var id=reader.GetString(0);var json=reader.GetString(1);
        if(Hash(json)!=id)throw new InvalidDataException("南向历史快照登记身份损坏。");
        var manifest=JsonSerializer.Deserialize<Manifest>(json)??throw new InvalidDataException("南向历史登记缺失。");
        if(manifest.From!=new DateOnly(SouthboundAnnualComparison.EarliestYear,1,1)||manifest.Through>today||manifest.Pages.Count is <1 or >5
            ||manifest.Pages.Any(x=>x.RetrievedAt>session.Clock.GetUtcNow()))throw new InvalidDataException("南向历史快照范围或时间无效。");
        var capture=new SouthboundCapture(manifest.From,manifest.Through,manifest.Pages.Select(x=>new SouthboundCapturedPage(x.Url,session.RawEvidence.ReadVerified(x.Hash,session.OperationCancellation),x.RetrievedAt)).ToArray());
        var rows=SouthboundHistorySource.ParseCapture(capture);
        return SouthboundAnnualComparison.Build(first,last,today,manifest.Pages.Max(x=>x.RetrievedAt),id,rows) with{Through=rows.Max(x=>x.Date)};
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
