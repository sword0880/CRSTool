using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>按配置证券读取已核持仓及可比性；不发现报告、不写审核或触发网络。</summary>
internal sealed class EtfHoldingHistoryStore(IMarketRepositorySession session,EtfStore etfs,AlertStore alerts)
{
    public NamedEtfHoldingHistoryReport Read(EtfKey key,DateOnly asOf)
    {
        if(!session.IsReadSnapshot||session.ReadSnapshotCutoff is not {} captured)throw new InvalidOperationException("具名持仓历史必须使用独立读快照。");
        if(!etfs.Configuration().Any(x=>x.Market==key.Market&&x.Ticker==key.Ticker&&x.Active))throw new InvalidDataException("请选择当前配置池中启用的ETF。");
        session.OperationCancellation.ThrowIfCancellationRequested();
        return NamedEtfHoldingHistory.Evaluate(new(key,asOf,CRS.Timing.BeijingClock.Normalize(captured),alerts.MetadataForNavStudy(key),alerts.NamedEtfHoldings(key)));
    }
}
