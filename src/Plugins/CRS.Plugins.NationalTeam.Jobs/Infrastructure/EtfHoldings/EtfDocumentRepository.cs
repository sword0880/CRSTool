using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public bool EtfDocumentCommandComplete(Guid commandId,AlertDocumentRequest request)=>etfEvidenceStore.EtfDocumentCommandComplete(commandId,request);
    public void RegisterEtfDocument(AlertDocumentRequest request,byte[] document,Guid commandId)=>etfEvidenceStore.RegisterEtfDocument(request,document,commandId);
}
