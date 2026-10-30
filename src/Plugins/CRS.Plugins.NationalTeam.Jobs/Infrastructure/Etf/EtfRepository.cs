// ETF仓储的聚合转发及有界读取入口，份额、覆盖和验收计算交给ETF领域Store。
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository:IEtfDashboardProfileRepository,IEtfAcceptanceProfileRepository,IBoundedMarketEvidence
{
    public DateOnly? ReadSyncCursor()=>etfStore.ReadSyncCursor();
    public void RecordSyncCursor(DateOnly day)=>etfStore.RecordSyncCursor(day);
    public void ImportCalendar(string fetchId, IReadOnlyList<(string Market,DateOnly Date)> days, CancellationToken cancellation)=>etfStore.ImportCalendar(fetchId,days,cancellation);
    public int ImportShares(string fetchId, IReadOnlyList<ImportRow> rows, CancellationToken cancellation,bool requireCalendar=true)=>etfStore.ImportShares(fetchId,rows,cancellation,requireCalendar);
    public ShareImportOutcome ImportSharesDetailed(string fetchId, IReadOnlyList<ImportRow> rows, CancellationToken cancellation,bool requireCalendar=true)=>etfStore.ImportSharesDetailed(fetchId,rows,cancellation,requireCalendar);
    public void Review(string revisionId, bool accept, string reviewer, bool comparableConfirmed = false,Guid? commandId=null)=>etfStore.Review(revisionId,accept,reviewer,comparableConfirmed,commandId);
    public List<EvidenceRow> Evidence()=>etfStore.Evidence();
    public List<EvidenceRow> Evidence(EtfKey key)=>etfStore.Evidence(key);
    public List<DateOnly> Calendar(string market)=>etfStore.Calendar(market);
    public List<EtfHistoryPoint> History(EtfKey? key)=>etfStore.History(key);
    public List<SharePoint> Points(EtfKey key)=>etfStore.Points(key);
    public Dictionary<EtfKey,List<SharePoint>> PointsForKeys(IReadOnlyList<EtfKey> keys)=>etfStore.PointsForKeys(keys);
    public List<EtfSummary> Overview()=>etfStore.Overview();
    public CoverageReport Coverage(EtfKey key,DateOnly from,DateOnly to)=>etfStore.Coverage(key,from,to);
    public List<EtfConfiguration> Configuration()=>etfStore.Configuration();
    public void SetMembership(EtfKey key,string name,string index,bool active,string reason,Guid? commandId=null)=>etfStore.SetMembership(key,name,index,active,reason,commandId);
    public void SaveConfiguration(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,Guid? commandId=null)=>etfStore.SaveConfiguration(key,name,index,active,reason,expectedRevision,commandId);
    public string BeginCollection(EtfKey key,DateOnly day)=>etfStore.BeginCollection(key,day);
    public void RecordCollection(string id,string state,string reason,string? fetchId=null)=>etfStore.RecordCollection(id,state,reason,fetchId);
    public List<CollectionAttempt> CollectionAttempts(EtfKey key,DateOnly from,DateOnly to)=>etfStore.CollectionAttempts(key,from,to);
    public List<ShareGap> DueHistoricalShareGaps(DateOnly through,DateTimeOffset now)=>etfStore.DueHistoricalShareGaps(through,now);
    public List<ShareGap> DueShareGaps(DateOnly from,DateOnly to,DateTimeOffset now)=>etfStore.DueShareGaps(from,to,now);
    public EtfAcceptanceQueryProfile ProfileAcceptance(EtfKey? key,DateOnly through)=>etfStore.ProfileAcceptance(key,through);
    public List<EtfAcceptance> Acceptance(DateOnly through)=>etfStore.Acceptance(through);
    public List<EtfAcceptance> Acceptance(EtfKey key,DateOnly through)=>etfStore.Acceptance(key,through);


    public EtfDashboardQueryProfile ProfileDashboard(EtfKey? key)=>etfStore.ProfileDashboard(key);
    public EtfDashboard Dashboard(EtfKey? key)=>etfStore.Dashboard(key);

    public List<EvidenceRow> Evidence(EtfKey? key,DateOnly? from,DateOnly? to,int maximumRows)=>etfStore.Evidence(key,from,to,maximumRows);
}
