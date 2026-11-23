using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record SouthboundCommandReceipt(Guid RequestId,string State,string Code);
/// <summary>年度比较的独立客户端端口；界面不联网或计算金融金额。</summary>
public interface ISouthboundWorkspace:IWorkspaceAccess
{
    Task<SouthboundAnnualReport> SouthboundAnnualAsync(int firstYear,int lastYear,CancellationToken token);
    Task<SouthboundCommandReceipt> QueueSouthboundAsync(CancellationToken token);
    Task<SouthboundCommandReceipt> SouthboundStatusAsync(Guid requestId,CancellationToken token);
}
