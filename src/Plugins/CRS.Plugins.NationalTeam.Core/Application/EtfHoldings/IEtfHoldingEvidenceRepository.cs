using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF可比口径及具名持仓读取，独立于预警事件与通知。</summary>
public interface IEtfHoldingEvidenceReadRepository
{
    List<AlertFundMetadata> AlertMetadata();
    List<NamedEtfHolding> NamedEtfHoldings(EtfKey? key=null);
}
public interface IEtfHoldingEvidenceWriteRepository
{
    void ReviewAlertMetadata(AlertMetadataReview review,Guid? commandId=null);
    void ReviewNamedEtfHolding(NamedEtfHoldingReview review,Guid? commandId=null);
}
