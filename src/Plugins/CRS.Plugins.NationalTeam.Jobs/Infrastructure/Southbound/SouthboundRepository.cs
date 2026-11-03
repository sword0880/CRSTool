using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>独立南向数据端口；现有功能端口不增加默认空数据实现。</summary>
public sealed partial class MarketRepository:ISouthboundReadRepository,ISouthboundWriteRepository
{
    public SouthboundAnnualReport SouthboundAnnual(int firstYear,int lastYear)=>stores.Southbound.Annual(firstYear,lastYear);
    public string? SouthboundReceipt(Guid requestId)=>stores.Southbound.Receipt(requestId);
    public string SaveSouthbound(SouthboundCapture capture,Guid? commandId)=>stores.Southbound.Save(capture,commandId);
}
