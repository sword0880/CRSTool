using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>直接持股观察配置、原件登记与人工复核。</summary>
public interface IEquityWriteRepository
{
    void SaveEquityWatch(EquityWatch watch,Guid? commandId=null);
    void SaveEquityDisclosure(string market,string ticker,Uri uri,byte[] bytes);
    void SaveEquityReports(EtfKey key,DateOnly from,DateOnly to,IReadOnlyList<byte[]> pages,Guid? commandId=null);
    void ReviewEquity(EquityReview review,byte[] document,Guid? commandId=null);
    void ReviewEquityDirection(EquityDirectionReview review,Guid commandId);
}
