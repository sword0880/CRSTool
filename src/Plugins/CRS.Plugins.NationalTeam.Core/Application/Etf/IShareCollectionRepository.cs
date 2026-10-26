using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>份额采集游标、重试缺口和尝试记录。</summary>
public interface IShareCollectionRepository
{
    string BeginCollection(EtfKey key,DateOnly day);
    void RecordCollection(string id,string state,string reason,string? fetchId=null);
    List<CollectionAttempt> CollectionAttempts(EtfKey key,DateOnly from,DateOnly to);
    List<ShareGap> DueShareGaps(DateOnly from,DateOnly to,DateTimeOffset now);
    List<ShareGap> DueHistoricalShareGaps(DateOnly through,DateTimeOffset now);
    bool? IsReviewedTradingDay(string market,DateOnly day);
    DateOnly? ReadSyncCursor();
    void RecordSyncCursor(DateOnly day);
}
