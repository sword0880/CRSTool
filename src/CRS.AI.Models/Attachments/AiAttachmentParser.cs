using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CRS.AI.Models.Attachments;

/// <summary>WPF 与 MCP 共用的附件白名单、文件校验及文本解析入口。</summary>
public static class AiAttachmentParser
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFiles = 5;
    public const int MaxTotalBytes = 20 * 1024 * 1024;
    public const int MaxExtractedCharacters = 50_000;
    public const int PreviewCharacters = 240;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>从有界内存字节解析附件；不创建文件、不执行公式、脚本或外部内容。</summary>
    public static AiAttachmentDocument Parse(string fileName, string? declaredMimeType, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxFileBytes)
            throw new InvalidDataException("附件必须非空且不能超过 10 MiB。");

        var safeName = ValidateFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        var (mediaType, allowedMimeTypes) = extension switch
        {
            ".txt" => ("text/plain", new[] { "text/plain" }),
            ".md" => ("text/markdown", new[] { "text/markdown", "text/plain" }),
            ".csv" => ("text/csv", new[] { "text/csv", "text/plain" }),
            ".json" => ("application/json", new[] { "application/json", "text/json" }),
            _ => throw new InvalidDataException("首版仅支持 TXT、MD、CSV 和 JSON 文件。")
        };

        ValidateMimeType(declaredMimeType, allowedMimeTypes);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("附件不是有效的 UTF-8 文本。", error);
        }

        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("附件没有可读取的文本内容。");
        if (text.Length > MaxExtractedCharacters) throw new InvalidDataException("单个附件提取文本超过 50000 个字符。");
        if (text.Any(character => character == '\0' ||
            (char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f'))))
            throw new InvalidDataException("附件包含不允许的控制字符。");

        if (extension == ".json") ValidateJson(text);
        if (extension == ".csv") ValidateCsv(text);

        text = text.Normalize(NormalizationForm.FormC);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Guid.NewGuid(), safeName, mediaType, bytes.Length, hash, text);
    }

    /// <summary>从用户选择的本地文件读取有界字节；读取结束后尽力清零原始字节缓冲。</summary>
    public static async Task<AiAttachmentDocument> ReadFileAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 1 or > MaxFileBytes)
            throw new InvalidDataException("附件必须非空且不能超过 10 MiB。");

        var bytes = new byte[checked((int)input.Length)];
        try
        {
            await input.ReadExactlyAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            if (await input.ReadAsync(new byte[1].AsMemory(), token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("附件在读取期间发生变化，请重新选择。");
            token.ThrowIfCancellationRequested();
            return Parse(Path.GetFileName(path), null, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>验证当前会话的附件数量、原始大小与解析文本总量。</summary>
    public static void ValidateCollection(IReadOnlyCollection<AiAttachmentDocument> attachments)
    {
        if (attachments.Count > MaxFiles) throw new InvalidDataException("一个会话最多保留 5 个附件。");
        if (attachments.Select(item => item.Id).Distinct().Count() != attachments.Count)
            throw new InvalidDataException("附件列表包含重复标识。");
        if (attachments.Sum(item => (long)item.Length) > MaxTotalBytes)
            throw new InvalidDataException("一个会话的附件总大小不能超过 20 MiB。");
        if (attachments.Sum(item => (long)item.CharacterCount) > MaxExtractedCharacters)
            throw new InvalidDataException("一个会话的附件提取文本总量不能超过 50000 个字符。");
    }

    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 128 ||
            fileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            fileName is "." or ".." ||
            fileName.Any(char.IsControl) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new InvalidDataException("附件文件名无效。");
        return fileName;
    }

    private static void ValidateMimeType(string? declaredMimeType, IReadOnlyCollection<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(declaredMimeType)) return;
        var mimeType = declaredMimeType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (mimeType == "application/octet-stream") return;
        if (!allowed.Contains(mimeType, StringComparer.Ordinal))
            throw new InvalidDataException("附件声明的 MIME 类型与文件扩展名不匹配。");
    }

    private static void ValidateJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("JSON 附件格式无效。", error);
        }
    }

    private static void ValidateCsv(string text)
    {
        if (text.Count(character => character == '\n') + 1 > 10_000)
            throw new InvalidDataException("CSV 附件不能超过 10000 行。");
        if (text.Split('\n').Any(line => line.Length > 16_384))
            throw new InvalidDataException("CSV 附件存在超过 16384 个字符的单行。");
    }
}
