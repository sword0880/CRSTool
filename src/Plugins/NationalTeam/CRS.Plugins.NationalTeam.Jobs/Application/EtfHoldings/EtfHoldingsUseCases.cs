using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>基金持仓研究的目录用例，与预警协调分离；目录不升级持仓批准状态。</summary>
public sealed partial class EtfHoldingsUseCases : MarketUseCaseScope
{
    private readonly IEtfHolderCandidateParser holderParser;
    internal EtfHoldingsUseCases(MarketApplicationSession session,IEtfHolderCandidateParser holderParser):base(session)
    {this.holderParser=holderParser;}
    public Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEtfDocumentReadRepository current)=>current.AlertDocuments(key),token);
    public Task RegisterAlertDocumentAsync(AlertDocumentRequest request,byte[] bytes,CancellationToken token,Guid? commandId)=>RunAsync((IEtfDocumentWriteRepository repository)=>{repository.RegisterAlertDocument(request,bytes,commandId);return true;},token);
    public Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>ReadPortAsync((IEtfHoldingEvidenceReadRepository current)=>current.AlertMetadata(),token);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)=>ReviewAlertMetadataAsync(review,token,null);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token,Guid? commandId)=>RunAsync((IEtfHoldingEvidenceWriteRepository repository)=>{repository.ReviewAlertMetadata(review,commandId);return true;},token);
    public Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEtfHoldingEvidenceReadRepository current)=>current.NamedEtfHoldings(key),token);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token)=>ReviewNamedEtfHoldingAsync(review,token,null);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token,Guid? commandId)=>RunAsync((IEtfHoldingEvidenceWriteRepository repository)=>{repository.ReviewNamedEtfHolding(review,commandId);return true;},token);
    public Task<bool> DocumentCommandCompleteAsync(Guid id,AlertDocumentRequest request,CancellationToken token)
        =>ReadPortAsync((IEtfDocumentReadRepository repository)=>repository.EtfDocumentCommandComplete(id,request),token);
    public Task RegisterDocumentAsync(AlertDocumentRequest request,byte[] bytes,Guid id,CancellationToken token)
        =>RunAsync((IEtfDocumentWriteRepository repository)=>{repository.RegisterEtfDocument(request,bytes,id);return true;},token);
    public Task<NamedEtfHoldingHistoryReport> HoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token)=>ReadPortAsync((IEtfHoldingHistoryReadRepository repository)=>repository.EtfHoldingHistory(key,asOf),token);
    public Task<bool> EtfReportCommandCompleteAsync(Guid id,EtfKey key,DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync((IEtfReportIndexReadRepository repository)=>repository.EtfReportCommandComplete(id,key,from,through),token);
    public Task<EtfReportIndexSnapshot> ReportIndexAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEtfReportIndexReadRepository repository)=>repository.EtfReportIndex(key),token);
    public Task SaveReportIndexAsync(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,CancellationToken token,Guid? commandId=null)
        =>RunAsync((IEtfReportIndexWriteRepository repository)=>{repository.SaveEtfReportIndex(key,from,through,pages,commandId);return true;},token);
}
