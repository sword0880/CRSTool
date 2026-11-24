using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CRS.Plugins.XueqiuWatch.Core;

/// <summary>仅从明确的 Google 账号入口识别邮箱，保持旧版 SHA-256 指纹兼容。</summary>
public static partial class GoogleAlertsAccountIdentity
{
    public static string? ResolveFingerprint(IEnumerable<string> captions)
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var caption in captions)
        {
            if (string.IsNullOrWhiteSpace(caption) || !AccountCaption().IsMatch(caption)) continue;
            var matches = AccountAddress().Matches(caption);
            foreach (Match match in matches) accounts.Add(match.Value.ToLowerInvariant());
        }
        // 同一账号的重复入口允许；不同账号同时出现时拒绝推断。
        if (accounts.Count != 1) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accounts.Single())));
    }

    [GeneratedRegex(@"^\s*Google\s*(?:账号|帐号|账户|帳號|帳戶|Account)(?:\s|[:：（(]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccountCaption();

    [GeneratedRegex(@"[a-zA-Z0-9.!#$%&'*+/=?^_\x60{|}~-]+@[a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)+")]
    private static partial Regex AccountAddress();
}
