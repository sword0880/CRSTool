// ETF原件的登记与读取端口，沿用命令身份实现恢复；旧登记方法仅保留既有命令语义，登记不自动批准持仓。
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
