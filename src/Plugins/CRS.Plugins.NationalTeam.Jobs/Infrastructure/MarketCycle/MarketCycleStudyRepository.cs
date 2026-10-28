using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository
{
    public MarketCycleStudyReport MarketCycleStudy(DateOnly asOf,DateTimeOffset? cutoff)=>stores.Cycle.Read(asOf,cutoff);
    public MarketBreadthHistory MarketBreadthHistory(DateOnly from,DateOnly through,DateTimeOffset? cutoff)=>stores.Breadth.History(from,through,cutoff);
    public MarketBreadthSubmissionReceipt? MarketBreadthSubmissionReceipt(Guid request,MarketBreadthSubmission submission)=>stores.Breadth.Receipt(request,submission);
    public string SubmitMarketBreadth(byte[] original,byte[] normalized,MarketBreadthSubmission submission,Guid commandId)=>stores.Breadth.Submit(original,normalized,submission,commandId);
    public void ReviewMarketBreadth(MarketBreadthReview review,Guid commandId)=>stores.Breadth.Review(review,commandId);
    public string MarketBreadthEvidence(string revision,bool original)=>stores.Breadth.Evidence(revision,original);
    public string ImportReviewedMarketBreadth(byte[] original,byte[] normalized,Uri source,DateTimeOffset retrievedAt,string reviewer,string reason,bool originalConfirmed,Guid commandId)
        =>stores.Breadth.Import(original,normalized,source,retrievedAt,reviewer,reason,originalConfirmed,commandId);
}
