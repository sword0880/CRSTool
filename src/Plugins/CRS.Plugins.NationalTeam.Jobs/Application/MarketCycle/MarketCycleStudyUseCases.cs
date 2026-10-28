using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
public sealed class MarketCycleStudyUseCases:MarketUseCaseScope
{
    internal MarketCycleStudyUseCases(MarketApplicationSession session):base(session){}
    public Task<MarketBreadthHistory> MarketBreadthHistoryAsync(DateOnly from,DateOnly through,DateTimeOffset? cutoff,CancellationToken token)
        =>ReadPortAsync<IMarketCycleStudyReadRepository,MarketBreadthHistory>(r=>r.MarketBreadthHistory(from,through,cutoff),token);
    public Task<MarketBreadthSubmissionReceipt?> MarketBreadthSubmissionReceiptAsync(Guid request,MarketBreadthSubmission submission,CancellationToken token)
        =>ReadPortAsync<IMarketCycleStudyReadRepository,MarketBreadthSubmissionReceipt?>(r=>r.MarketBreadthSubmissionReceipt(request,submission),token);
    public Task<string> MarketBreadthEvidenceAsync(string revision,bool original,CancellationToken token)=>ReadPortAsync<IMarketCycleStudyReadRepository,string>(r=>r.MarketBreadthEvidence(revision,original),token);
    public Task<string> SubmitMarketBreadthAsync(byte[] original,byte[] normalized,MarketBreadthSubmission submission,Guid request,CancellationToken token)
        =>RunAsync<IMarketBreadthWriteRepository,string>(r=>r.SubmitMarketBreadth(original,normalized,submission,request),token);
    public Task ReviewMarketBreadthAsync(MarketBreadthReview review,Guid request,CancellationToken token)
        =>RunAsync<IMarketBreadthWriteRepository,bool>(r=>{r.ReviewMarketBreadth(review,request);return true;},token);
    public Task<MarketCycleStudyReport> MarketCycleStudyAsync(DateOnly asOf,DateTimeOffset? cutoff,CancellationToken token)
        =>ReadPortAsync<IMarketCycleStudyReadRepository,MarketCycleStudyReport>(r=>r.MarketCycleStudy(asOf,cutoff),token);
    public Task<string> ImportReviewedMarketBreadthAsync(byte[] original,byte[] normalized,Uri source,DateTimeOffset retrievedAt,string reviewer,string reason,bool originalConfirmed,Guid commandId,CancellationToken token)
        =>RunAsync<IMarketBreadthWriteRepository,string>(r=>r.ImportReviewedMarketBreadth(original,normalized,source,retrievedAt,reviewer,reason,originalConfirmed,commandId),token);
}
