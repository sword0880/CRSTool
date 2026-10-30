// 广度原件的分块上传、登记与回执确认；中断后保留本任务的原提交身份，未知结果不得被新文件替换。
using System.IO;
using System.Text.Json;
using CRS.IO.Staging;
using CRS.Serialization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    private MarketBreadthSubmission? pendingBreadth;
    private Guid? completedBreadthRequest;
    public async Task<MarketBreadthHistory> MarketBreadthHistoryAsync(DateOnly date,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("market-breadth-history",From:date,To:date,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<MarketBreadthHistory>(blocks,token);
        if(report.FormatVersion!=MarketBreadthHistory.Version||report.From!=date||report.Through!=date||MarketBreadthHistory.Freeze(report.From,report.Through,report.Cutoff,report.Rows).StateFingerprint!=report.StateFingerprint)throw new InvalidDataException("广度档案日期或版本摘要不符。");return report;
    }
    public async Task<MarketBreadthSubmissionReceipt> SubmitMarketBreadthAsync(string originalPath,string normalizedPath,string sourceUrl,DateTimeOffset retrievedAt,string submitter,string reason,CancellationToken token,IProgress<(int Received,int Total)>? progress=null)
    {
        if(!CanManage)throw new UnauthorizedAccessException("资料登记需要管理员权限。");
        if(commands.Find(Plugin,"market-breadth-submit") is not null&&pendingBreadth is null)throw new InvalidDataException("上次广度登记尚待确认，请先用原参数回读结果。");
        if(pendingBreadth is not null)throw new InvalidDataException("上次资料登记尚待确认，请先回读登记结果；新文件尚未提交。");
        if(pendingBreadth is null)
        {
            var uploader=new PluginDocumentClient(client);
            var original=await uploader.UploadAsync(Plugin,"market-breadth-original-v1",originalPath,token,progress);
            var normalized=await uploader.UploadAsync(Plugin,"market-breadth-normalized-v1",normalizedPath,token,progress);
            pendingBreadth=new(original,normalized,sourceUrl,retrievedAt,submitter,reason);
        }
        return await ConfirmMarketBreadthSubmissionAsync(token);
    }
    public async Task<MarketBreadthSubmissionReceipt> ConfirmMarketBreadthSubmissionAsync(CancellationToken token)
    {
        if(pendingBreadth is null)throw new InvalidDataException("当前没有待确认的已封存登记；中断于上传时可重新选择原文件续传。");
        try
        {
            completedBreadthRequest??=await RunAsync("market-breadth-submit",new("market-breadth-submit",BreadthSubmission:pendingBreadth),token);
            var result=(await ReadPagesAsync<MarketBreadthSubmissionReceipt?>(new("market-breadth-receipt",Revision:completedBreadthRequest.Value.ToString("D"),BreadthSubmission:pendingBreadth),token)).Rows.SingleOrDefault()
                ??throw new InvalidDataException("广度登记虽已结束，但业务回执尚未确认。");
            if(result.RequestId!=completedBreadthRequest||result.OriginalHash!=pendingBreadth.Original.Sha256||result.NormalizedHash!=pendingBreadth.Normalized.Sha256)throw new InvalidDataException("广度业务回执与提交文件不符。");
            pendingBreadth=null;completedBreadthRequest=null;return result;
        }
        catch{if(commands.Find(Plugin,"market-breadth-submit") is null&&completedBreadthRequest is null)pendingBreadth=null;throw;}
    }
    public Task ReviewMarketBreadthAsync(MarketBreadthReview review,CancellationToken token)=>RunAsync("market-breadth-review",new("market-breadth-review",BreadthReview:review),token);
    public Task ExportMarketBreadthEvidenceAsync(string revision,bool original,string destination,CancellationToken token)=>ExportEvidenceCoreAsync(new("export-breadth",Revision:revision,Original:original),destination,token);
}
