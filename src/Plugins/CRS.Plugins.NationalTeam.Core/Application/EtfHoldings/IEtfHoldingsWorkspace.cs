using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF持仓研究的远程目录合同；所有来源采集、持久化及解析在后台执行。</summary>
public interface IEtfHoldingsWorkspace:IWorkspaceAccess
{
    Task<EtfReportIndexSnapshot> EtfReportIndexAsync(EtfKey key,CancellationToken token);
    Task<EtfReportSearchReceipt> QueueEtfReportsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
}
