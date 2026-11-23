using System.Net;
using System.Net.Sockets;

namespace CRS.Feed;

/// <summary>统一校验 Feed 地址，避免凭据、非预期协议和明显的本机地址进入采集队列。</summary>
public static class FeedAddress
{
    public static string Normalize(string? value, bool allowHttp = false) =>
        Validate(value, allowHttp).AbsoluteUri;

    public static Uri Validate(string? value, bool allowHttp = false)
    {
        if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("请输入有效的 RSS / Atom / JSON Feed 地址。");
        return Validate(uri, allowHttp);
    }

    public static Uri Validate(Uri uri, bool allowHttp = false)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var schemeAllowed = uri.Scheme == Uri.UriSchemeHttps ||
            (allowHttp && uri.Scheme == Uri.UriSchemeHttp);
        if (!uri.IsAbsoluteUri || !schemeAllowed || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Feed 地址必须使用 HTTPS 默认端口，且不能包含账号凭据或片段。");

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" or "metadata.google.internal" ||
            host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal))
            throw new ArgumentException("不允许使用本机或内部保留主机名作为 Feed 地址。");

        var ipText = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(ipText, out var address) && IsNonPublicAddress(address))
            throw new ArgumentException("不允许直接访问回环、私有或保留 IP 地址。");

        return uri;
    }

    private static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            return a == 0 || a == 10 || a == 127 || a >= 224 ||
                (a == 100 && b is >= 64 and <= 127) ||
                (a == 169 && b == 254) ||
                (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && (b == 168 || (b == 0 && c == 0) || (b == 0 && c == 2))) ||
                (a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) ||
                (a == 203 && b == 0 && c == 113);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.Equals(IPAddress.IPv6Any) || address.IsIPv6Loopback ||
                address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
                bytes[0] is 0xfc or 0xfd ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);

        return true;
    }
}
