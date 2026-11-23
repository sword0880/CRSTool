using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    /// <summary>明确协商独立采集能力；旧后台拒绝新入口，不隐式退回会阻塞管理的旧任务。</summary>
    public async Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token)
    {
        if(request is null||request.Ticker is not{Length:6} ticker||!ticker.All(char.IsAsciiDigit))throw new InvalidDataException("ETF代码无效。");
        var job=EtfDocumentCollectionProtocol.Job(request.Market);
        var capabilities=await EnsureCapabilitiesAsync(token);
        if(capabilities is null||!capabilities.Capabilities.Contains(EtfDocumentCollectionProtocol.Capability,StringComparer.Ordinal))
            throw new InvalidDataException("后台尚未支持独立ETF原件采集，请更新后台插件。");
        await RunAsync(job,new(EtfDocumentCollectionProtocol.Operation,AlertDocument:request),token);
    }
}
