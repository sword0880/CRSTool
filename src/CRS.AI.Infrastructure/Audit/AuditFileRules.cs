using CRS.AI.Contracts;
using CRS.AI.Query;

namespace CRS.AI.Infrastructure;

internal static class AuditFileRules
{
    public const int MaximumEventBytes = 1024;
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private static readonly HashSet<string> AllowedDecisions = new(StringComparer.Ordinal)
    {
        "Received", "Rejected", "Partial", "NotReady", "Unauthorized", "UnknownTool",
        "InvalidArguments", "BudgetExceeded", "ConcurrencyExceeded", "Cancelled",
        "UpstreamTimeout", "UpstreamUnavailable", "UpstreamFailure", "UpstreamInvalid",
        "Unsupported", "AuditUnavailable"
    };

    public static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("审计文件必须使用本机绝对路径。", nameof(path));
        return Path.GetFullPath(path);
    }

    public static void RejectReparsePoints(string path, bool allowMissing = false)
    {
        try { RejectLink(File.GetAttributes(path)); }
        catch (FileNotFoundException) when (allowMissing) { }
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            RejectLink(parent.Attributes);
    }

    private static void RejectLink(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("审计路径不能包含符号链接或目录联接。");
    }

    public static void ValidateEvent(AiReadAuditEvent entry)
    {
        // 写入和归档沿用同一字段白名单，旧版已经写入的决定码仍有效。
        if (entry.RequestId == Guid.Empty || entry.TimestampUtc.Offset != TimeSpan.Zero ||
            entry.Tool is not (AiCatalog.Overview or AiCatalog.Coverage or "unlisted") ||
            entry.Phase is not ("Attempt" or "Outcome") ||
            !AllowedDecisions.Contains(entry.Decision) ||
            !IsSha256OrMarker(entry.SubjectSha256, "unverified", null) ||
            !IsSha256OrMarker(entry.ArgumentSha256, "invalid", "oversize"))
            throw new InvalidDataException("审计事件字段不符合约定。");
    }

    private static bool IsSha256OrMarker(string? value, string marker1, string? marker2) =>
        value == marker1 || (marker2 is not null && value == marker2) ||
        (value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'));
}
