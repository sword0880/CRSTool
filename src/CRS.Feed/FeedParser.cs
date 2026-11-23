using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CRS.Feed;

/// <summary>将 RSS 2.0、RSS 1.0、Atom 和 JSON Feed 转换为统一模型。</summary>
public static class FeedParser
{
    public static FeedDocument Parse(ReadOnlyMemory<byte> content, Uri feedUrl, int maximumItems = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        ArgumentNullException.ThrowIfNull(feedUrl);
        if (content.IsEmpty)
            throw new InvalidDataException("Feed 正文为空。");

        // 兼容 UTF-8 BOM，同时保留 XML 声明中的编码检测。
        var payload = content;
        if (payload.Length >= 3 && payload.Span[0] == 0xEF && payload.Span[1] == 0xBB && payload.Span[2] == 0xBF)
            payload = payload[3..];

        var first = 0;
        while (first < payload.Length && char.IsWhiteSpace((char)payload.Span[first]))
            first++;

        try
        {
            return first < payload.Length && payload.Span[first] == (byte)'{'
                ? ParseJson(payload, feedUrl, maximumItems)
                : ParseXml(payload, feedUrl, maximumItems);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is XmlException or JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Feed 文档格式无效。", error);
        }
    }

    private static FeedDocument ParseXml(ReadOnlyMemory<byte> payload, Uri feedUrl, int maximumItems)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = Math.Max(1024, payload.Length * 2L),
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Feed 缺少根节点。");

        if (root.Name.LocalName.Equals("rss", StringComparison.OrdinalIgnoreCase))
        {
            var channel = Child(root, "channel") ?? throw new InvalidDataException("RSS 缺少 channel 节点。");
            var items = channel.Elements().Where(element => IsName(element, "item"))
                .Take(maximumItems).Select(item => ReadRssItem(item, feedUrl)).ToArray();
            return new(Text(Child(channel, "title")), Text(Child(channel, "description")),
                Resolve(feedUrl, Text(Child(channel, "link"))), feedUrl, items);
        }

        if (root.Name.LocalName.Equals("RDF", StringComparison.OrdinalIgnoreCase))
        {
            var channel = Child(root, "channel");
            var items = root.Elements().Where(element => IsName(element, "item"))
                .Take(maximumItems).Select(item => ReadRssItem(item, feedUrl)).ToArray();
            return new(Text(Child(channel, "title")), Text(Child(channel, "description")),
                Resolve(feedUrl, Text(Child(channel, "link"))), feedUrl, items);
        }

        if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
        {
            var items = root.Elements().Where(element => IsName(element, "entry"))
                .Take(maximumItems).Select(item => ReadAtomItem(item, feedUrl)).ToArray();
            var home = root.Elements().Where(element => IsName(element, "link"))
                .FirstOrDefault(element => (string?)element.Attribute("rel") is null or "alternate");
            return new(Text(Child(root, "title")), Text(Child(root, "subtitle")),
                Resolve(feedUrl, (string?)home?.Attribute("href")), feedUrl, items);
        }

        throw new InvalidDataException("不支持的 XML Feed 根节点。");
    }

    private static FeedItem ReadRssItem(XElement item, Uri feedUrl)
    {
        var linkText = Text(Child(item, "link"));
        var link = Resolve(feedUrl, linkText);
        var title = Text(Child(item, "title")) ?? string.Empty;
        var id = Text(Child(item, "guid")) ?? Text(Child(item, "id")) ?? link?.AbsoluteUri ?? title;
        var content = Text(Child(item, "encoded")) ?? Text(Child(item, "content"));
        var summary = Text(Child(item, "description")) ?? Text(Child(item, "summary"));
        var author = Text(Child(item, "creator")) ?? Text(Child(item, "author"));
        var published = ParseDate(Text(Child(item, "pubDate")) ?? Text(Child(item, "published")) ??
            Text(Child(item, "date")));
        var categories = item.Elements().Where(element => IsName(element, "category"))
            .Select(element => element.Value.Trim()).Where(value => value.Length > 0).Distinct().ToArray();
        return new(id, title, link, summary, content ?? summary, author, published, null, categories);
    }

    private static FeedItem ReadAtomItem(XElement item, Uri feedUrl)
    {
        var links = item.Elements().Where(element => IsName(element, "link")).ToArray();
        var link = links.FirstOrDefault(element =>
            (string?)element.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault();
        var url = Resolve(feedUrl, (string?)link?.Attribute("href"));
        var title = Text(Child(item, "title")) ?? string.Empty;
        var id = Text(Child(item, "id")) ?? url?.AbsoluteUri ?? title;
        var author = Child(item, "author");
        var authorName = Text(Child(author, "name")) ?? Text(author);
        var summary = Text(Child(item, "summary"));
        var content = Text(Child(item, "content")) ?? summary;
        var published = ParseDate(Text(Child(item, "published")));
        var updated = ParseDate(Text(Child(item, "updated")));
        var categories = item.Elements().Where(element => IsName(element, "category"))
            .Select(element => ((string?)element.Attribute("term") ?? element.Value).Trim())
            .Where(value => value.Length > 0).Distinct().ToArray();
        return new(id, title, url, summary, content, authorName, published, updated, categories);
    }

    private static FeedDocument ParseJson(ReadOnlyMemory<byte> payload, Uri feedUrl, int maximumItems)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var version = String(root, "version");
        if (version is null || !version.StartsWith("https://jsonfeed.org/version/", StringComparison.Ordinal))
            throw new InvalidDataException("JSON Feed 版本标识无效。");

        var items = root.TryGetProperty("items", out var source) && source.ValueKind == JsonValueKind.Array
            ? source.EnumerateArray().Take(maximumItems).Select(item => ReadJsonItem(item, feedUrl)).ToArray()
            : throw new InvalidDataException("JSON Feed 缺少 items 数组。");
        return new(String(root, "title"), String(root, "description"),
            Resolve(feedUrl, String(root, "home_page_url")), feedUrl, items);
    }

    private static FeedItem ReadJsonItem(JsonElement item, Uri feedUrl)
    {
        var title = String(item, "title") ?? string.Empty;
        var url = Resolve(feedUrl, String(item, "url") ?? String(item, "external_url"));
        var id = String(item, "id") ?? url?.AbsoluteUri ?? title;
        var author = ReadJsonAuthor(item);
        var categories = item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String)
                .Select(tag => tag.GetString()!).Where(value => value.Length > 0).Distinct().ToArray()
            : [];
        return new(id, title, url, String(item, "summary"),
            String(item, "content_html") ?? String(item, "content_text") ?? String(item, "summary"),
            author, ParseDate(String(item, "date_published")), ParseDate(String(item, "date_modified")), categories);
    }

    private static string? ReadJsonAuthor(JsonElement item)
    {
        if (item.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
        {
            var first = authors.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
                return String(first, "name");
        }
        if (item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
            return String(author, "name");
        return null;
    }

    private static XElement? Child(XElement? element, string name) =>
        element?.Elements().FirstOrDefault(child => IsName(child, name));

    private static bool IsName(XElement? element, string name) =>
        element is not null && element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? Text(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? Resolve(Uri baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Uri.TryCreate(baseUri, value.Trim(), out var uri) ? uri : null;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
}
