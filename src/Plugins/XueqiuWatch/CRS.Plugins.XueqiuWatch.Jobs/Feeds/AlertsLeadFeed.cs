using System.Net;
using System.Xml;
using System.Xml.Linq;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed record LeadLink(string Account, string Url);
internal sealed record LeadFeedResult(string State, LeadLink[] Links, TimeSpan? RetryAfter = null);

internal sealed class AlertsLeadFeed : IDisposable
{
    private const int MaximumBytes = 512 * 1024;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private readonly HttpClient http;

    internal AlertsLeadFeed() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal AlertsLeadFeed(HttpMessageHandler handler)
    {
        http = new(handler) { MaxResponseContentBufferSize = MaximumBytes };
    }

    internal async Task<LeadFeedResult> ReadAsync(string url, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var reply = await http.GetAsync(WatchInput.GoogleAlertFeed(url), deadline.Token).ConfigureAwait(false);
            return reply.StatusCode switch
            {
                HttpStatusCode.OK => await ParseAsync(reply.Content, deadline.Token).ConfigureAwait(false),
                HttpStatusCode.TooManyRequests => new("RateLimited", [], RetryDelay(reply)),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect => new("AccessRequired", []),
                _ => new("FeedUnavailable", [])
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("TimedOut", []); }
        catch (HttpRequestException) { return new("FeedUnavailable", []); }
        catch (XmlException) { return new("InvalidFeed", []); }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage reply)
    {
        var delay = reply.Headers.RetryAfter?.Delta ??
            (reply.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromHours(1));
        return delay > TimeSpan.FromSeconds(30) ? delay : TimeSpan.FromSeconds(30);
    }

    private static async Task<LeadFeedResult> ParseAsync(HttpContent content, CancellationToken token)
    {
        await using var body = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = XmlReader.Create(body, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false);
        var root = document.Root;
        var urls = root?.Name == Atom + "feed"
            ? root.Elements(Atom + "entry").Elements(Atom + "link")
                .Where(link => (string?)link.Attribute("rel") is null or "alternate")
                .Select(link => (string?)link.Attribute("href"))
            : root?.Name == "rss" && root.Element("channel") is { } channel
                ? channel.Elements("item").Select(item => (string?)item.Element("link"))
                : null;
        if (urls is null) return new("InvalidFeed", []);

        var matches = new HashSet<LeadLink>();
        foreach (var url in urls)
        {
            token.ThrowIfCancellationRequested();
            if (CanonicalPost(url) is { } post)
                matches.Add(new(post.Split('/')[3], post));
        }
        return new("Read", matches.ToArray());
    }

    private static string? CanonicalPost(string? value)
    {
        if (value is not { Length: > 0 and <= 4096 } || !Uri.TryCreate(value, UriKind.Absolute, out var link))
            return null;
        if (link.Scheme == "https" && link.Host is "www.google.com" or "google.com" && link.AbsolutePath == "/url")
        {
            value = link.Query.TrimStart('?').Split('&').Select(parameter => parameter.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0] is "url" or "q")
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault();
        }
        try
        {
            var url = WatchInput.SourceLink(value);
            return url.Length == 0 ? null : url;
        }
        catch (ArgumentException) { return null; }
    }

    public void Dispose() => http.Dispose();
}
