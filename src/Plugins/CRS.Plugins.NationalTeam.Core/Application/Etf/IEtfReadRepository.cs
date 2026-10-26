using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF事实、证据和配置读取；缺失能力不能伪装为空结果。</summary>
public interface IEtfReadRepository
{
    List<EtfAcceptance> Acceptance(DateOnly through);
    List<EtfAcceptance> Acceptance(EtfKey key,DateOnly through);
    List<EtfSummary> Overview();
    List<EvidenceRow> Evidence();
    List<EvidenceRow> Evidence(EtfKey key);
    List<EtfHistoryPoint> History(EtfKey? key);
    List<SharePoint> Points(EtfKey key);
    Dictionary<EtfKey,List<SharePoint>> PointsForKeys(IReadOnlyList<EtfKey> keys);
    CoverageReport Coverage(EtfKey key,DateOnly from,DateOnly to);
    List<EtfConfiguration> Configuration();
}
