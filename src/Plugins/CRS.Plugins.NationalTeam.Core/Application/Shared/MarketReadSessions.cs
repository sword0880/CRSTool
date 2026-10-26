using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>每次查询独立连接和一致性快照；取消只中断本次读取，不触及采集连接。</summary>
public interface IMarketReadSessions
{
    T Read<T>(Func<IMarketRepository,T> query,CancellationToken token);
}

/// <summary>在SQL读取阶段限制证券、日期和结果数量；超限明确失败，不能静默截断证据。</summary>
public interface IBoundedMarketEvidence
{
    List<EvidenceRow> Evidence(EtfKey? key,DateOnly? from,DateOnly? to,int maximumRows);
}
