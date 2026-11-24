using System.Text;

namespace CRS.Plugins.XueqiuWatch.Core;

public sealed record PostBodyRequest(string Account = "", string Url = "", long Revision = 0,
    Guid UploadId = default, int Offset = 0, int Length = 0, string? Sha256 = null,
    byte[]? Bytes = null, DateTimeOffset? PublishedAt = null);

public enum PostBodySource { Manual, AuthorizedFeed, OriginalPage }

public sealed record PostBodyInfo(long Revision, int Length, string Sha256, DateTimeOffset PublishedAt,
    PostBodySource Source = PostBodySource.Manual);

public static class PostBody
{
    public const int MaximumBytes = 256 * 1024;
    public const int UploadChunkBytes = 2500;
    public const int ReadChunkBytes = 24 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string SourceLabel(PostBodySource source) =>
        source switch
        {
            PostBodySource.AuthorizedFeed => "获授权来源",
            PostBodySource.OriginalPage => "雪球原帖网页",
            _ => "手工补录"
        };

    public static byte[] Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入原帖正文。");
        try
        {
            if (Utf8.GetByteCount(text) > MaximumBytes) throw new ArgumentException("正文不能超过 256 KiB。");
            return Utf8.GetBytes(text);
        }
        catch (EncoderFallbackException) { throw new ArgumentException("正文含无效的 Unicode 字符。"); }
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new ArgumentException("正文须在 1 字节至 256 KiB 之间。");
        try
        {
            var text = Utf8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入原帖正文。");
            return text;
        }
        catch (DecoderFallbackException) { throw new ArgumentException("正文不是有效的 UTF-8 文本。"); }
    }
}
