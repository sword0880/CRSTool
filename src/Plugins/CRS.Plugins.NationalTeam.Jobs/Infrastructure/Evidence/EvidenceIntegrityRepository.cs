using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public void ReconcileEvidenceIntegrity()=>evidenceIntegrityStore.ReconcileEvidenceIntegrity();
    public IReadOnlyList<IntegrityEvidence> CaptureIntegrityEvidence()=>evidenceIntegrityStore.CaptureIntegrityEvidence();
    public void ApplyIntegrityEvidence(IReadOnlyList<IntegrityEvidence> snapshot,IReadOnlyDictionary<string,bool> verified)
        =>evidenceIntegrityStore.ApplyIntegrityEvidence(snapshot,verified);
}
