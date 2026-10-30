using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public interface IEtfDocumentReadRepository
{
    bool EtfDocumentCommandComplete(Guid commandId,AlertDocumentRequest request);
}
public interface IEtfDocumentWriteRepository
{
    void RegisterEtfDocument(AlertDocumentRequest request,byte[] document,Guid commandId);
}
