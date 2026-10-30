// 纯SHA-256内容身份工具，供不同插件共用；只计算和检查指纹，原件读取、来源授权、大小限制及金融审核由调用模块负责。
using System.Security.Cryptography;

namespace CRS.Common.Integrity;

/// <summary>
/// Pure SHA-256 identity helpers, reusable across isolated plugin evidence stores.
/// No file access, database access, caching, provider approvals or storage permissions.
/// Financial plugins still own evidence size, provenance, source authorization and retention rules.
/// </summary>
public static class ContentFingerprint
{
    /// <summary>Canonical uppercase hex identity of the exact supplied bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)=>Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Checks only shape; does not establish trust, provenance, review status or license.
    /// Accepts either hex case for legacy verification compatibility.
    /// </summary>
    public static bool IsSha256Hex(string? hash)
        =>hash is {Length:64}&&hash.All(Uri.IsHexDigit);

    /// <summary>Exact case-sensitive identity comparison, matching the original evidence store semantics.</summary>
    public static bool Matches(string expected,ReadOnlySpan<byte> bytes)
        =>IsSha256Hex(expected)&&string.Equals(Sha256(bytes),expected,StringComparison.Ordinal);
}
