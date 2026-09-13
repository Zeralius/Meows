using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Meows.Plugins.Birdwatch.Services;

/// <summary>
/// Any RSS or Atom feed with pictures in it: a Tumblr blog's <c>/rss</c>, a DeviantArt gallery
/// through <c>backend.deviantart.com/rss.xml</c>, a WordPress site, a Pixiv proxy. The pictures
/// are taken from enclosures, from <c>media:content</c> and <c>media:thumbnail</c>, and from
/// <c>img</c> tags in the description, in that order of trust. No login, and no paging: a feed
/// is one page, and the newest is at the top.
///
/// The handle is the feed address itself. There is nothing shorter that would still be it.
/// </summary>
public sealed partial class RssFeed : IFeedSource
{
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private readonly HttpClient _http;

    public RssFeed(HttpClient http) => _http = http;

    public string ServiceName => "Feed";

    /// <summary>A URL that nothing more specific claimed. Last in line, on purpose.</summary>
    public static bool Owns(string pasted) =>
        pasted.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase);

    public string TidyHandle(string pasted) => pasted.Trim();

    public async Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token)
    {
        // One page. Asking for more of a feed is asking for the same feed.
        if (cursor is { Length: > 0 })
            return FeedPage.Empty;

        using var response = await _http.GetAsync(handle, token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token);

        if (!body.TrimStart().StartsWith('<'))
            throw new InvalidOperationException("that address did not answer with a feed");

        return Parse(body, handle);
    }

    public static FeedPage Parse(string xml, string feedUrl)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("that address did not answer with a feed");
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName is not ("rss" or "feed" or "RDF"))
            throw new InvalidOperationException("that address did not answer with a feed");

        return root.Name.LocalName == "feed"
            ? new FeedPage(ParseAtom(root, feedUrl), null)
            : new FeedPage(ParseRss(root, feedUrl), null);
    }

    private static List<FeedPost> ParseRss(XElement root, string feedUrl)
    {
        var channel = root.Element("channel") ?? root;
        var site = (string?)channel.Element("title") ?? Host(feedUrl);
        var posts = new List<FeedPost>();

        foreach (var item in channel.Elements("item"))
        {
            var link = (string?)item.Element("link");
            var media = new List<FeedMedia>();

            foreach (var enclosure in item.Elements("enclosure"))
            {
                var type = (string?)enclosure.Attribute("type") ?? "";
                var url = (string?)enclosure.Attribute("url");
                if (url is { Length: > 0 } && (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || LooksLikeImage(url)))
                    Add(media, url, url, "");
            }

            foreach (var content in item.Elements(Media + "content"))
            {
                var url = (string?)content.Attribute("url");
                var medium = (string?)content.Attribute("medium") ?? "";
                var type = (string?)content.Attribute("type") ?? "";
                if (url is { Length: > 0 } && (medium == "image" || type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || LooksLikeImage(url)))
                {
                    var thumb = (string?)content.Element(Media + "thumbnail")?.Attribute("url") ?? url;
                    Add(media, url, thumb, (string?)content.Element(Media + "description") ?? "");
                }
            }

            if (media.Count == 0)
            {
                foreach (var thumbnail in item.Elements(Media + "thumbnail"))
                {
                    var url = (string?)thumbnail.Attribute("url");
                    if (url is { Length: > 0 })
                        Add(media, url, url, "");
                }
            }

            var description = (string?)item.Element("description") ?? (string?)item.Element("{http://purl.org/rss/1.0/modules/content/}encoded") ?? "";
            if (media.Count == 0)
                foreach (var url in ImagesIn(description))
                    Add(media, url, url, "");

            posts.Add(new FeedPost
            {
                Id = (string?)item.Element("guid") ?? link ?? $"{feedUrl}#{posts.Count}",
                AuthorHandle = (string?)item.Element(Dc + "creator") ?? (string?)item.Element("author") ?? site,
                AuthorName = site,
                Text = ((string?)item.Element("title") ?? MastodonFeed.StripHtml(description)).Trim(),
                PostedAt = When((string?)item.Element("pubDate") ?? (string?)item.Element(Dc + "date")),
                WebUrl = link,
                Labels = [],
                Media = media,
            });
        }

        return posts;
    }

    private static List<FeedPost> ParseAtom(XElement root, string feedUrl)
    {
        var site = (string?)root.Element(Atom + "title") ?? Host(feedUrl);
        var posts = new List<FeedPost>();

        foreach (var entry in root.Elements(Atom + "entry"))
        {
            var link = entry.Elements(Atom + "link")
                .FirstOrDefault(l => ((string?)l.Attribute("rel") ?? "alternate") == "alternate")
                ?.Attribute("href")?.Value;
            var media = new List<FeedMedia>();

            foreach (var l in entry.Elements(Atom + "link"))
            {
                var rel = (string?)l.Attribute("rel");
                var type = (string?)l.Attribute("type") ?? "";
                var href = (string?)l.Attribute("href");
                if (rel == "enclosure" && href is { Length: > 0 } && (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || LooksLikeImage(href)))
                    Add(media, href, href, "");
            }

            foreach (var content in entry.Elements(Media + "content"))
            {
                var url = (string?)content.Attribute("url");
                if (url is { Length: > 0 })
                    Add(media, url, (string?)content.Element(Media + "thumbnail")?.Attribute("url") ?? url, "");
            }

            var body = (string?)entry.Element(Atom + "content") ?? (string?)entry.Element(Atom + "summary") ?? "";
            if (media.Count == 0)
                foreach (var url in ImagesIn(body))
                    Add(media, url, url, "");

            var author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value;
            posts.Add(new FeedPost
            {
                Id = (string?)entry.Element(Atom + "id") ?? link ?? $"{feedUrl}#{posts.Count}",
                AuthorHandle = author ?? site,
                AuthorName = site,
                Text = ((string?)entry.Element(Atom + "title") ?? MastodonFeed.StripHtml(body)).Trim(),
                PostedAt = When((string?)entry.Element(Atom + "published") ?? (string?)entry.Element(Atom + "updated")),
                WebUrl = link,
                Labels = [],
                Media = media,
            });
        }

        return posts;
    }

    private static void Add(List<FeedMedia> media, string full, string thumb, string alt)
    {
        full = WebUtility.HtmlDecode(full);
        if (media.Any(m => m.FullUrl == full))
            return;
        media.Add(new FeedMedia { Kind = MediaKind.Image, ThumbnailUrl = WebUtility.HtmlDecode(thumb), FullUrl = full, Alt = alt });
    }

    /// <summary>img tags in a description. Tracking pixels and emoji are dropped by size hints where present.</summary>
    public static IEnumerable<string> ImagesIn(string html)
    {
        foreach (Match match in ImgTag().Matches(html))
        {
            var tag = match.Value;
            if (OneByOne().IsMatch(tag))
                continue;
            var src = match.Groups["src"].Value;
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                yield return WebUtility.HtmlDecode(src);
        }
    }

    private static bool LooksLikeImage(string url)
    {
        var path = url;
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>RFC 822 in RSS, ISO 8601 in Atom, and neither in the window's language.</summary>
    public static DateTimeOffset When(string? value)
    {
        if (value is null)
            return DateTimeOffset.MinValue;

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var text = value.Trim();

        // "GMT", "UT" and "Z" at the end are what RFC 822 allows and what the exact formats
        // below do not take, so they become the offset they mean.
        foreach (var zone in new[] { " GMT", " UT", " UTC", " Z" })
        {
            if (text.EndsWith(zone, StringComparison.OrdinalIgnoreCase))
                text = text[..^zone.Length] + " +0000";
        }

        string[] formats =
        [
            "ddd, d MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss zzz",
            "d MMM yyyy HH:mm:ss zzz", "dd MMM yyyy HH:mm:ss zzz",
            "ddd, d MMM yyyy HH:mm zzz", "ddd, dd MMM yyyy HH:mm zzz",
        ];
        if (DateTimeOffset.TryParseExact(text, formats, invariant, System.Globalization.DateTimeStyles.None, out var exact))
            return exact;

        return DateTimeOffset.TryParse(value, invariant, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\b(width|height)\s*=\s*[""']?1[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex OneByOne();
}
