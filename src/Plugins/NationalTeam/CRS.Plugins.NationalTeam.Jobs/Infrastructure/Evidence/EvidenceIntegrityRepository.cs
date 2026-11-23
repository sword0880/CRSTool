// 原件完整性巡检的窄接口转发，将损坏、隔离和恢复交给独立完整性模块处理。
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
