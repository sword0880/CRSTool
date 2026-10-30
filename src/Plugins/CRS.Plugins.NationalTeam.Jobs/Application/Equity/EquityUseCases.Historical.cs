// 股票历史披露用例，复用应用会话的读取快照及写入门槛，来源登记与持仓批准分别处理。
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EquityUseCases
{
    public Task<List<HistoricalEquityPdf>> HistoricalEquityDocumentsAsync(CancellationToken token)
        =>ReadPortAsync<IHistoricalEquityReadRepository,List<HistoricalEquityPdf>>(r=>r.HistoricalEquityDocuments().ToList(),token);
    public Task<HistoricalEquityReceipt?> HistoricalEquityReceiptAsync(Guid requestId,CancellationToken token)
        =>ReadPortAsync<IHistoricalEquityReadRepository,HistoricalEquityReceipt?>(r=>r.HistoricalEquityReceipt(requestId),token);
    public Task<bool> HistoricalEquityRequestMatchesAsync(Guid requestId,string definitionId,string reviewer,string reason,CancellationToken token)
        =>ReadPortAsync<IHistoricalEquityReadRepository,bool>(r=>r.HistoricalEquityRequestMatches(requestId,definitionId,reviewer,reason),token);
    public Task<HistoricalEquityReceipt> ImportHistoricalEquityDocumentAsync(string id,byte[] document,string reviewer,string reason,Guid requestId,CancellationToken token)
        =>RunAsync((IHistoricalEquityWriteRepository repository)=>repository
            .ImportHistoricalEquityPdf(id,document,reviewer,reason,requestId),token);
}
