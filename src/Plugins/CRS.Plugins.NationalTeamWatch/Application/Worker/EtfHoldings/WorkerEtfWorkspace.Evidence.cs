using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    // 保留通信操作名，使旧后台及既有命令仍能识别；功能合同属于ETF持仓。
    public async Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)
        =>(await ReadPagesAsync<AlertDocument>(new("alert-documents",key.Market,key.Ticker),token)).Rows;
    public async Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)
        =>(await ReadPagesAsync<AlertFundMetadata>(new("alert-metadata"),token)).Rows;
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)
        =>RunAsync("alert-manage",new("alert-metadata-review",AlertMetadata:review),token);
    public async Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)
        =>(await ReadPagesAsync<NamedEtfHolding>(new("alert-holdings",key.Market,key.Ticker),token)).Rows;
}
