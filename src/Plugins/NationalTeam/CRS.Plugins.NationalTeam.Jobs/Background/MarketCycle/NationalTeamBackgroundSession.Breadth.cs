using CRS.IO.Staging;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    private async Task<BackgroundJobResult> SubmitMarketBreadthAsync(MarketBreadthSubmission submission,Guid request,CancellationToken token)
    {
        try
        {
            var store=new StagedDocumentStore(Path.Combine(context.DataDirectory,"document-inbox"));
            if(await workspace!.Cycle.MarketBreadthSubmissionReceiptAsync(request,submission,token) is null)
            {
                var original=store.ReadReady(submission.Original,"market-breadth-original-v1",token);var normalized=store.ReadReady(submission.Normalized,"market-breadth-normalized-v1",token);
                await workspace.Cycle.SubmitMarketBreadthAsync(original,normalized,submission,request,token);
            }
            // SQL事实和回执已提交后才标记消费；清理失败保留暂存字节，不推翻已提交事实。
            try{store.Consume(submission.Original,request,token);store.Consume(submission.Normalized,request,token);}
            catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException){return BackgroundJobResult.Success("breadth=Registered;approval=Separate;staging=Retained");}
            return BackgroundJobResult.Success("breadth=Registered;approval=Separate");
        }
        catch(InvalidDataException){return new("Rejected","breadth=SubmissionInvalid");}
        catch(InvalidOperationException){return new("RetryDeferred","breadth=DocumentBusy",TimeSpan.FromSeconds(10));}
    }
}
