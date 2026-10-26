using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private IReadOnlyDictionary<string,bool> ValidateEvidence(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
        =>evidenceIntegrityStore.Validate(shareKey,includeCalendar,activityOnly,dashboardOnly,includeAttempts);
    public void ReconcileEvidenceIntegrity()=>evidenceIntegrityStore.ReconcileEvidenceIntegrity();
    public IReadOnlyList<IntegrityEvidence> CaptureIntegrityEvidence()=>evidenceIntegrityStore.CaptureIntegrityEvidence();
    public void ApplyIntegrityEvidence(IReadOnlyList<IntegrityEvidence> snapshot,IReadOnlyDictionary<string,bool> verified)
        =>evidenceIntegrityStore.ApplyIntegrityEvidence(snapshot,verified);
}
