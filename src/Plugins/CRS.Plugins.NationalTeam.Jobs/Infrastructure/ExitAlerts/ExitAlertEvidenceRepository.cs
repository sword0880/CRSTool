using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    // 聚合入口只转发；预警实现、事务及状态由独立模块负责。
    public void RegisterAlertDocument(AlertDocumentRequest request,byte[] document,Guid? commandId=null) => alertStore.RegisterAlertDocument(request, document, commandId);
    public List<AlertDocument> AlertDocuments(EtfKey key) => alertStore.AlertDocuments(key);
    public void ReviewAlertMetadata(AlertMetadataReview review,Guid? commandId=null) => alertStore.ReviewAlertMetadata(review, commandId);
    public List<AlertFundMetadata> AlertMetadata() => alertStore.AlertMetadata();
    public void ReviewNamedEtfHolding(NamedEtfHoldingReview review,Guid? commandId=null) => alertStore.ReviewNamedEtfHolding(review, commandId);
    public List<NamedEtfHolding> NamedEtfHoldings(EtfKey? key=null) => alertStore.NamedEtfHoldings(key);
    public EtfHolderDocument EtfHolderDocument(EtfKey key,string documentId) => alertStore.EtfHolderDocument(key,documentId);
}
