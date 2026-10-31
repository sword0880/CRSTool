// 周期数据来源的用途与审批准入合同；公开可访问不等于获准采集、存储或回填，授权证明只在验证期间使用，许可原件须能按摘要复核。
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

public enum MarketCycleSourceApprovalStatus { Pending, Approved, Rejected, Revoked }

public enum MarketCycleSourceOperation { AutomatedPolling, HistoricalBackfill, Redistribution }

/// <summary>Verifies that the referenced license/terms bytes are actually retained and match the SHA-256.</summary>
public interface IMarketCycleSourceApprovalEvidenceVerifier
{
    bool Verify(string sha256);
}

/// <summary>
/// Host-issued management command. AuthorizationProof is transient and must never be persisted or logged.
/// The authority must validate an authenticated administrator session and bind its proof to every command field.
/// </summary>
public sealed record MarketCycleSourceApprovalWriteCommand(
    Guid CommandId,
    string Nonce,
    DateTimeOffset IssuedAt,
    MarketCycleSourceApproval Approval,
    string AuthorizationProof);

public sealed record MarketCycleSourceApprovalWriteReceipt(
    Guid CommandId,
    long Sequence,
    string Status,
    string EventHash,
    bool Replayed = false);

public sealed record MarketCycleSourceApprovalAuditEntry(
    long Sequence,
    Guid CommandId,
    string SourceId,
    string DatasetId,
    MarketCycleSourceApprovalStatus Status,
    string ActorId,
    DateTimeOffset RecordedAt,
    string PayloadHash,
    string PreviousEventHash,
    string EventHash);

/// <summary>
/// Must be supplied by a trusted host composition root. Implementations validate the authenticated
/// administrator session, capability, freshness, and proof binding; callers cannot pass an authority per write.
/// No default implementation is provided, so writes remain disabled until a real management flow exists.
/// </summary>
public interface IMarketCycleApprovalWriteAuthority
{
    bool TryAuthorize(
        MarketCycleSourceApprovalWriteCommand command,
        DateTimeOffset now,
        out string authenticatedActorId);
}

/// <summary>Read-only approval lookup port shared by the market-cycle registry and index-series approval provider.</summary>
public interface IMarketCycleSourceApprovalReader : IMarketCycleSourceApprovalEvidenceVerifier
{
    MarketCycleSourceApproval? Find(string datasetId);
}

/// <summary>Encrypted, append-only registry contract; the implementation also verifies the keyed audit chain.</summary>
public interface IMarketCycleSourceApprovalRegistry : IMarketCycleSourceApprovalReader
{
    MarketCycleSourceApprovalWriteReceipt Apply(MarketCycleSourceApprovalWriteCommand command);
    IReadOnlyList<MarketCycleSourceApprovalAuditEntry> ReadAudit();
    bool VerifyAuditChain();
}

/// <summary>
/// Human-reviewed source approval metadata. TermsEvidenceHash is the SHA-256 of the locally retained
/// license/terms evidence, not a hash inferred from a live website. No source is approved by default.
/// Identity authentication is deliberately delegated to the registry's host-injected write authority.
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
/// Fail-closed admission gate for market-cycle and index-series source use. The gate performs no network
/// calls or writes; callers supply a trusted approval reader and explicit operation time so admission can be replay-tested.
/// This gate supplements, and does not replace, the host allow-list and request-budget checks.
/// </summary>
public static class MarketCycleSourceAdmission
{
    private const MarketCycleSourceUse AllKnownUses =
        MarketCycleSourceUse.AutomatedCollection |
        MarketCycleSourceUse.PersistentStorage |
        MarketCycleSourceUse.HistoricalBackfill |
        MarketCycleSourceUse.Redistribution;

    /// <summary>
    /// Reject path traversal and encoded path separators before applying a base-path prefix check.
    /// Decoding is bounded and fails closed for repeatedly encoded paths.
    /// </summary>
    public static bool IsSafeUriPath(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        var path = uri.AbsolutePath;
        for (var pass = 0; pass < 4; pass++)
        {
            if (path.Contains('\\') ||
                path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
                path.Split('/').Any(segment => segment is "." or ".."))
                return false;

            string decoded;
            try { decoded = Uri.UnescapeDataString(path); }
            catch (UriFormatException) { return false; }
            if (string.Equals(decoded, path, StringComparison.Ordinal)) return true;
            path = decoded;
        }
        return false;
    }

    /// <summary>
    /// Required target-aware gate for market-cycle collectors. The approval reader must be supplied by the
    /// trusted host composition root. It binds the operation's actual target and dataset to the reviewed source base;
    /// checking an approval object alone does not authorize an arbitrary URI.
    /// </summary>
    // 先核对本次数据集与目标范围，来源批准不能被其它证券或用途借用。
    public static void RequireTarget(
        MarketCycleSourceApproval approval,
        string requestedSourceId,
        string requestedDatasetId,
        Uri requestedUri,
        MarketCycleSourceOperation operation,
        DateTimeOffset operationAt,
        IMarketCycleSourceApprovalReader approvalRegistry)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(requestedUri);
        ArgumentNullException.ThrowIfNull(approvalRegistry);

        if (!string.Equals(approval.SourceId, requestedSourceId, StringComparison.Ordinal) ||
            !string.Equals(approval.DatasetId, requestedDatasetId, StringComparison.Ordinal))
            throw new InvalidDataException("实际请求的来源 ID 或数据集 ID 与审批身份不匹配。");

        var approvedBase = approval.SourceUri;
        if (approvedBase is null || !approvedBase.IsAbsoluteUri ||
            approvedBase.Scheme != Uri.UriSchemeHttps || !approvedBase.IsDefaultPort ||
            !string.IsNullOrEmpty(approvedBase.UserInfo) || !string.IsNullOrEmpty(approvedBase.Query) ||
            !string.IsNullOrEmpty(approvedBase.Fragment) ||
            !IsSafeUriPath(approvedBase) || !IsSafeUriPath(requestedUri) ||
            requestedUri.Scheme != Uri.UriSchemeHttps || !requestedUri.IsDefaultPort ||
            !string.IsNullOrEmpty(requestedUri.UserInfo) || !string.IsNullOrEmpty(requestedUri.Fragment) ||
            !string.Equals(approvedBase.IdnHost, requestedUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            approvedBase.Port != requestedUri.Port)
            throw new InvalidDataException("实际请求 URI 不属于获批的 HTTPS 来源基址。");

        var basePath = approvedBase.AbsolutePath.TrimEnd('/');
        var targetPath = requestedUri.AbsolutePath;
        if (basePath.Length > 0 &&
            !string.Equals(targetPath, basePath, StringComparison.Ordinal) &&
            !targetPath.StartsWith(basePath + "/", StringComparison.Ordinal))
            throw new InvalidDataException("实际请求路径不属于获批的来源路径边界。");

        Require(approval, operation, operationAt, approvalRegistry);
    }

    // 来源准入同时核对用途、状态、有效期与许可原件；公开URL可访问不足以代替批准。
    public static void Require(
        MarketCycleSourceApproval approval,
        MarketCycleSourceOperation operation,
        DateTimeOffset operationAt,
         IMarketCycleSourceApprovalReader approvalRegistry)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(approvalRegistry);

        if (string.IsNullOrWhiteSpace(approval.SourceId) || approval.SourceId.Length > 100 ||
            string.IsNullOrWhiteSpace(approval.DatasetId) || approval.DatasetId.Length > 100 ||
            string.IsNullOrWhiteSpace(approval.ContractVersion) || approval.ContractVersion.Length > 40 ||
            string.IsNullOrWhiteSpace(approval.ParserVersion) || approval.ParserVersion.Length > 40 ||
            string.IsNullOrWhiteSpace(approval.PolicyVersion) || approval.PolicyVersion.Length > 40)
            throw new InvalidDataException("市场周期来源审批缺少稳定标识或契约版本。");

        if (approval.SourceUri is null ||
            !approval.SourceUri.IsAbsoluteUri ||
            approval.SourceUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(approval.SourceUri.Host) ||
            !string.IsNullOrEmpty(approval.SourceUri.UserInfo) ||
            !string.IsNullOrEmpty(approval.SourceUri.Query) ||
            !string.IsNullOrEmpty(approval.SourceUri.Fragment) ||
            !IsSafeUriPath(approval.SourceUri))
            throw new InvalidDataException("市场周期来源基址必须是无凭据、无查询参数、无片段的绝对 HTTPS 地址。");

        if (approval.TermsEvidenceHash is null ||
            approval.TermsEvidenceHash.Length != 64 ||
            !approval.TermsEvidenceHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("来源许可/条款证据必须具有有效 SHA-256 哈希。");

        if (!approvalRegistry.Verify(approval.TermsEvidenceHash))
            throw new InvalidDataException("来源许可/条款原件不存在或 SHA-256 核验失败。");

        if (string.IsNullOrWhiteSpace(approval.Reviewer) || approval.Reviewer.Length > 100 ||
            approval.ReviewedAt == default)
            throw new InvalidDataException("来源审批必须记录审核人和审核时间。");

        if ((approval.ApprovedUses & ~AllKnownUses) != 0)
            throw new InvalidDataException("来源审批包含未知用途权限。");

        if (approval.Status == MarketCycleSourceApprovalStatus.Pending)
            throw new InvalidOperationException("待审批来源不能执行市场周期数据操作。");

        if (!Enum.IsDefined(approval.Status) || !Enum.IsDefined(operation))
            throw new InvalidDataException("来源审批状态或操作类型无效。");

        if (approval.ReviewedAt > operationAt)
            throw new InvalidDataException("来源审批在拟执行时点尚未生效。");

        if (approval.ValidUntil is { } effectiveExpiry && effectiveExpiry <= approval.ReviewedAt)
            throw new InvalidDataException("来源审批有效期必须晚于审核生效时间。");

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

        // A well-formed object is not authority by itself. The exact immutable record must still be
        // the latest record returned by the trusted registry, preventing a caller from editing ApprovedUses,
        // status, URI, reviewer, or expiry in a copied JSON/config object.
        var registered = approvalRegistry.Find(approval.DatasetId);
        if (registered is null || registered != approval)
            throw new InvalidDataException("来源审批与可信注册表中的当前记录不一致，拒绝执行。");
    }
}
