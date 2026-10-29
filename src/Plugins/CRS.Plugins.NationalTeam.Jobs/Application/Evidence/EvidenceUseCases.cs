namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>协调证据核验与质量状态提交；不在数据库写门槛内读取原件。</summary>
public sealed class EvidenceUseCases : MarketUseCaseScope
{
    private readonly IRawEvidenceStore raw;
    private readonly SemaphoreSlim integrityGate=new(1,1);
    internal EvidenceUseCases(MarketApplicationSession session,IRawEvidenceStore raw):base(session)=>this.raw=raw;
    public async Task ReconcileEvidenceIntegrityAsync(CancellationToken token)
    {
        await integrityGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(raw is not IRawEvidenceVerifier verifier)
            {await RunAsync((IEvidenceMaintenanceRepository r)=>{r.ReconcileEvidenceIntegrity();return true;},token);return;}
            var snapshot=await ReadPortAsync((IEvidenceIntegrityRepository r)=>r.CaptureIntegrityEvidence(),token);
            var verified=await Task.Run(()=>verifier.VerifyMany(snapshot.SelectMany(x=>new[]{x.Hash,x.OriginalHash}),token),token);
            await RunAsync((IEvidenceIntegrityRepository r)=>{r.ApplyIntegrityEvidence(snapshot,verified);return true;},token);
        }
        finally{integrityGate.Release();}
    }
}
