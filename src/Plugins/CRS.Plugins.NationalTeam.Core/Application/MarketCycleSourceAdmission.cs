using System.IO;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>
/// Explicit, reviewable uses for a source. Publicly reachable is not a permission.
/// The flags are independent so a source may allow local polling but forbid backfill or redistribution.
/// </summary>
[Flags]
public enum MarketCycleSourceUse
{
    None = 0,
    AutomatedCollection = 1,
    PersistentStorage = 2,
    HistoricalBackfill = 4,
    Redistribution = 8
}

public enum MarketCycleSourceApprovalStatus { Pending, Approved, Rejected }

public enum MarketCycleSourceOperation { AutomatedPolling, HistoricalBackfill, Redistribution }

/// <summary>Verifies that the referenced license/terms bytes are actually retained and match the SHA-256.</summary>
public interface IMarketCycleSourceApprovalEvidenceVerifier
{
    bool Verify(string sha256);
}

/// <summary>
/// Human-reviewed source approval evidence. TermsEvidenceHash is the SHA-256 of the locally retained
/// license/terms evidence, not a hash inferred from a live website. No source is approved by default.
/// </summary>
public sealed record MarketCycleSourceApproval(
    string SourceId,
    Uri SourceUri,
    string DatasetId,
    string ContractVersion,
    string ParserVersion,
    MarketCycleSourceApprovalStatus Status,
    MarketCycleSourceUse ApprovedUses,
    string TermsEvidenceHash,
    string Reviewer,
    DateTimeOffset ReviewedAt,
    string PolicyVersion,
    DateTimeOffset? ValidUntil = null);

/// <summary>
/// Fail-closed admission gate for future market-cycle collectors. It has no network, storage or clock
/// dependency; callers supply the intended operation time so the same approval can be replay-tested.
/// This gate supplements, and does not replace, the host allow-list and request-budget checks.
/// </summary>
public static class MarketCycleSourceAdmission
{
    private const MarketCycleSourceUse AllKnownUses =
        MarketCycleSourceUse.AutomatedCollection |
        MarketCycleSourceUse.PersistentStorage |
        MarketCycleSourceUse.HistoricalBackfill |
        MarketCycleSourceUse.Redistribution;

    public static void Require(
        MarketCycleSourceApproval approval,
        MarketCycleSourceOperation operation,
        DateTimeOffset operationAt,
        IMarketCycleSourceApprovalEvidenceVerifier evidenceVerifier)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(evidenceVerifier);

        if (string.IsNullOrWhiteSpace(approval.SourceId) || approval.SourceId.Length > 100 ||
            string.IsNullOrWhiteSpace(approval.DatasetId) || approval.DatasetId.Length > 100 ||
            string.IsNullOrWhiteSpace(approval.ContractVersion) || approval.ContractVersion.Length > 40 ||
            string.IsNullOrWhiteSpace(approval.ParserVersion) || approval.ParserVersion.Length > 40 ||
            string.IsNullOrWhiteSpace(approval.PolicyVersion) || approval.PolicyVersion.Length > 40)
            throw new InvalidDataException("市场周期来源审批缺少稳定标识或契约版本。");

        if (approval.SourceUri is null ||
            approval.SourceUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(approval.SourceUri.Host) ||
            !string.IsNullOrEmpty(approval.SourceUri.UserInfo) ||
            !string.IsNullOrEmpty(approval.SourceUri.Fragment))
            throw new InvalidDataException("市场周期来源必须是无凭据、无片段的 HTTPS 地址。");

        if (approval.TermsEvidenceHash is null ||
            approval.TermsEvidenceHash.Length != 64 ||
            !approval.TermsEvidenceHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("来源许可/条款证据必须具有有效 SHA-256 哈希。");

        if (!evidenceVerifier.Verify(approval.TermsEvidenceHash))
            throw new InvalidDataException("来源许可/条款原件不存在或 SHA-256 核验失败。");

        if (string.IsNullOrWhiteSpace(approval.Reviewer) || approval.Reviewer.Length > 100 ||
            approval.ReviewedAt == default)
            throw new InvalidDataException("来源审批必须记录审核人和审核时间。");

        if ((approval.ApprovedUses & ~AllKnownUses) != 0)
            throw new InvalidDataException("来源审批包含未知用途权限。");

        if (!Enum.IsDefined(approval.Status) || !Enum.IsDefined(operation))
            throw new InvalidDataException("来源审批状态或操作类型无效。");

        if (approval.ReviewedAt > operationAt)
            throw new InvalidDataException("来源审批在拟执行时点尚未生效。");

        if (approval.ValidUntil is { } validUntil && validUntil <= operationAt)
            throw new InvalidOperationException("来源审批已过期，拒绝执行。");

        if (approval.Status != MarketCycleSourceApprovalStatus.Approved)
            throw new InvalidOperationException("来源尚未通过人工审批，拒绝执行市场周期数据操作。");

        var required = operation switch
        {
            MarketCycleSourceOperation.AutomatedPolling =>
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
            MarketCycleSourceOperation.HistoricalBackfill =>
                MarketCycleSourceUse.HistoricalBackfill | MarketCycleSourceUse.PersistentStorage,
            MarketCycleSourceOperation.Redistribution =>
                MarketCycleSourceUse.Redistribution,
            _ => throw new InvalidDataException("未知的市场周期来源操作。")
        };

        if ((approval.ApprovedUses & required) != required)
            throw new InvalidOperationException(
                $"来源审批不允许执行 {operation}；需要显式批准用途：{required}。");
    }
}
