using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>基金持仓研究的目录用例，与预警协调分离；目录不升级持仓批准状态。</summary>
public sealed class EtfHoldingsUseCases : MarketUseCaseScope
{
    internal EtfHoldingsUseCases(MarketApplicationSession session):base(session){}
    public Task<NamedEtfHoldingHistoryReport> HoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token)=>ReadPortAsync((IEtfHoldingHistoryReadRepository repository)=>repository.EtfHoldingHistory(key,asOf),token);
    public Task<bool> EtfReportCommandCompleteAsync(Guid id,EtfKey key,DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync((IEtfReportIndexReadRepository repository)=>repository.EtfReportCommandComplete(id,key,from,through),token);
    public Task<EtfReportIndexSnapshot> ReportIndexAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEtfReportIndexReadRepository repository)=>repository.EtfReportIndex(key),token);
    public Task SaveReportIndexAsync(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,CancellationToken token,Guid? commandId=null)
        =>RunAsync((IEtfReportIndexWriteRepository repository)=>{repository.SaveEtfReportIndex(key,from,through,pages,commandId);return true;},token);
}
