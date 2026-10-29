namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>只在后台内存流转的单次采集池版本，调用者不能填写版本或提交SQL。</summary>
public interface IEtfCollectionEvidenceVersion;
/// <summary>份额批次的完整性预读与提交前版本核对；不改变现有事实和质量规则。</summary>
public interface IEtfCollectionEvidenceRepository
{
    IEtfCollectionEvidenceVersion CaptureCollectionEvidenceVersion();
    void ValidateCollectionEvidence();
    void RequireCollectionEvidenceVersion(IEtfCollectionEvidenceVersion version);
}
