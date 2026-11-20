using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public interface IMarketCycleStudyReadRepository
{
    MarketCycleStudyReport MarketCycleStudy(DateOnly asOf,DateTimeOffset? cutoff);
    MarketBreadthHistory MarketBreadthHistory(DateOnly from,DateOnly through,DateTimeOffset? cutoff);
    MarketBreadthSubmissionReceipt? MarketBreadthSubmissionReceipt(Guid request,MarketBreadthSubmission submission);
    string MarketBreadthEvidence(string revision,bool original);
}
/// <summary>仅后台人工核验导入端口；不授予联网采集权限，不接受客户端路径。</summary>
public interface IMarketBreadthWriteRepository
{
    string ImportReviewedMarketBreadth(byte[] original,byte[] normalized,Uri source,DateTimeOffset retrievedAt,
        string reviewer,string reason,bool originalConfirmed,Guid commandId);
    string SubmitMarketBreadth(byte[] original,byte[] normalized,MarketBreadthSubmission submission,Guid commandId);
    void ReviewMarketBreadth(MarketBreadthReview review,Guid commandId);
}
