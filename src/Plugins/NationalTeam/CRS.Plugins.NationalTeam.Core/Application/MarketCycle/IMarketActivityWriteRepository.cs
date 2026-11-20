using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>股票成交统计提交与修订审核。</summary>
public interface IMarketActivityWriteRepository
{
    void SaveMarketActivity(string market,DateOnly day,MarketActivitySource? source);
    void RecordMarketActivityCursor(DateOnly day);
    void ReviewMarketActivity(string id,bool accept,string reviewer,Guid? commandId=null);
}
