using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Compatibility facade: industry PE persistence is implemented by the independent IndustryPeStore.</summary>
public sealed partial class MarketRepository
{
    public IndustryPeImportOutcome? IndustryPeReceipt(Guid commandId)=>industryPeStore.IndustryPeReceipt(commandId);
    public IndustryPeImportOutcome ImportIndustryPe(CapturedIndustryPeDocument document,Guid? commandId=null)=>industryPeStore.ImportIndustryPe(document,commandId);
    public IReadOnlyList<IndustryPeRevision> IndustryPeRevisions(DateOnly from,DateOnly through,DateTimeOffset cutoff)=>industryPeStore.IndustryPeRevisions(from,through,cutoff);
    public void ReviewIndustryPe(string revision,bool accept,string reviewer,Guid? commandId=null)=>industryPeStore.ReviewIndustryPe(revision,accept,reviewer,commandId);
}
