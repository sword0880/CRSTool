using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>自动发现有独立水位和原文队列；公开结果不以排队数量冒充已核持仓。</summary>
public interface IAutomaticEquityReadRepository
{
    AutomaticEquityStatus AutomaticEquityStatus();
    List<AutomaticEquityDocument> PendingAutomaticEquityDocuments(int count);
}
public interface IAutomaticEquityWriteRepository
{
    void SaveAutomaticEquitySecurities(byte[] original);
    void BeginAutomaticEquityWindow(DateOnly through);
    void SaveAutomaticEquityPage(byte[] original);
    string ImportAutomaticEquityDocument(string id,byte[] original);
    void RecordAutomaticEquityIssue(string? id,string reason,byte[]? original=null);
}
