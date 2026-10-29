namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record IntegrityEvidence(string Id,string Hash,string OriginalHash);
/// <summary>只在短仓储操作内取得不可变标识/哈希快照、提交同一快照的核验结果。</summary>
public interface IEvidenceIntegrityRepository
{
    IReadOnlyList<IntegrityEvidence> CaptureIntegrityEvidence();
    void ApplyIntegrityEvidence(IReadOnlyList<IntegrityEvidence> snapshot,IReadOnlyDictionary<string,bool> verified);
}
public interface IRawEvidenceVerifier
{
    IReadOnlyDictionary<string,bool> VerifyMany(IEnumerable<string> hashes,CancellationToken token);
}
/// <summary>跨计算和提交保持已核原件稳定；数量有界，取消/完成后释放，不是按时长缓存。</summary>
public interface IRawEvidenceStability
{
    IDisposable HoldVerifiedEvidence();
}
