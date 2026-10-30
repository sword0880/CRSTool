using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public interface IEtfDocumentReadRepository
{
    bool EtfDocumentCommandComplete(Guid commandId,AlertDocumentRequest request);
    List<AlertDocument> AlertDocuments(EtfKey key);
    EtfHolderDocument EtfHolderDocument(EtfKey key,string documentId);
}
public interface IEtfDocumentWriteRepository
{
    void RegisterEtfDocument(AlertDocumentRequest request,byte[] document,Guid commandId);
    void RegisterAlertDocument(AlertDocumentRequest request,byte[] document,Guid? commandId=null);
}
