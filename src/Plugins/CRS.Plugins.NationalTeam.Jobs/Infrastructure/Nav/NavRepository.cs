using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>NAV存储组合入口，仅转发至独立NavStore，不拥有连接或业务SQL。</summary>
public sealed partial class MarketRepository
{
    public NavImportOutcome? NavImportReceipt(Guid commandId)=>navStore.NavImportReceipt(commandId);
    public NavImportOutcome ImportNavDocuments(IReadOnlyList<CapturedNavDocument> documents,Guid? commandId=null)=>navStore.ImportNavDocuments(documents,commandId);
    public IReadOnlyList<NavRevision> NavRevisions(EtfKey key,DateOnly from,DateOnly through,DateTimeOffset knowledgeCutoff)=>navStore.NavRevisions(key,from,through,knowledgeCutoff);
    public IReadOnlyList<NavAcceptedPoint> NavPoints(EtfKey key,DateOnly from,DateOnly through,DateTimeOffset knowledgeCutoff)=>navStore.NavPoints(key,from,through,knowledgeCutoff);
    public void ReviewNavRevision(string revisionId,bool accept,string reviewer,Guid? commandId=null)=>navStore.ReviewNavRevision(revisionId,accept,reviewer,commandId);
}
