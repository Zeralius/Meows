using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meows.Plugins.Birdwatch.Services;

/// <summary>
/// Reddit, through the .json view every public page has. A user's submissions or a subreddit's
/// newest posts, no login, no app registered anywhere. What it needs is a real User-Agent, which
/// the client already sends; without one Reddit answers 429 to everything.
///
/// A handle is <c>u/name</c> or <c>r/name</c>, which is how Reddit writes them itself.
/// </summary>
public sealed partial class RedditFeed : IFeedSource
{
    private readonly HttpClient _http;

    public RedditFeed(HttpClient http) => _http = http;

    public string ServiceName => "Reddit";

    public static bool Owns(string pasted)
    {
        var text = pasted.Trim();
        if (text.Contains("reddit.com/", StringComparison.OrdinalIgnoreCase))
            return true;
        return Prefixed().IsMatch(text);
    }

    /// <summary>u/name or r/name, out of a prefix, a link, or a link to one post.</summary>
    public string TidyHandle(string pasted)
    {
        var text = pasted.Trim();
        var link = Link().Match(text);
        if (link.Success)
        {
            var kind = link.Groups["kind"].Value.ToLowerInvariant();
            return $"{(kind == "user" ? "u" : kind)}/{link.Groups["name"].Value}";
        }

        var prefixed = Prefixed().Match(text);
        if (prefixed.Success)
            return $"{prefixed.Groups["kind"].Value.ToLowerInvariant()}/{prefixed.Groups["name"].Value}";

        return text;
    }

    public async Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token)
    {
        var slash = handle.IndexOf('/');
        if (slash <= 0)
            throw new InvalidOperationException($"{handle} is not u/name or r/name");
        var kind = handle[..slash];
        var name = handle[(slash + 1)..];

        var url = kind == "r"
            ? $"https://www.reddit.com/r/{Uri.EscapeDataString(name)}/new.json?limit=50&raw_json=1"
            : $"https://www.reddit.com/user/{Uri.EscapeDataString(name)}/submitted.json?limit=50&raw_json=1";
        if (cursor is { Length: > 0 })
            url += "&after=" + Uri.EscapeDataString(cursor);

        using var response = await _http.GetAsync(url, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Reddit is rate limiting; try again in a minute");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"{handle} is not there, or is private");
        response.EnsureSuccessStatusCode();

        return Parse(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>
    /// Turns a listing into posts. Only image posts and galleries carry anything saveable;
    /// links, text and video are listed with what they have so the grid is honest about them.
    /// </summary>
    public static FeedPage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data))
            return FeedPage.Empty;

        var posts = new List<FeedPost>();
        if (data.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (child.TryGetProperty("data", out var item) && ReadItem(item) is { } post)
                    posts.Add(post);
            }
        }

        return new FeedPage(posts, Text(data, "after"));
    }

    private static FeedPost? ReadItem(JsonElement item)
    {
        var author = Text(item, "author") ?? "";
        var labels = new List<string>();
        if (item.TryGetProperty("over_18", out var nsfw) && nsfw.ValueKind == JsonValueKind.True)
            labels.Add("nsfw");
        if (item.TryGetProperty("spoiler", out var spoiler) && spoiler.ValueKind == JsonValueKind.True)
            labels.Add("spoiler");

        var posted = item.TryGetProperty("created_utc", out var created) && created.TryGetDouble(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            : DateTimeOffset.MinValue;

        return new FeedPost
        {
            Id = "t3_" + (Text(item, "id") ?? ""),
            AuthorHandle = "u/" + author,
            AuthorName = author,
            Text = Text(item, "title") ?? "",
            PostedAt = posted,
            WebUrl = Text(item, "permalink") is { Length: > 0 } link ? "https://www.reddit.com" + link : null,
            IsRepost = Text(item, "crosspost_parent") is { Length: > 0 },
            Labels = labels,
            Media = ReadMedia(item),
        };
    }

    private static List<FeedMedia> ReadMedia(JsonElement item)
    {
        var media = new List<FeedMedia>();

        // A gallery: several pictures, each under media_metadata by id, in gallery_data's order.
        if (item.TryGetProperty("is_gallery", out var gallery) && gallery.ValueKind == JsonValueKind.True &&
            item.TryGetProperty("media_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in metadata.EnumerateObject())
            {
                var source = entry.Value.TryGetProperty("s", out var s) ? s : default;
                var full = Text(source, "u") ?? Text(source, "gif");
                if (full is null)
                    continue;
                var preview = full;
                if (entry.Value.TryGetProperty("p", out var previews) && previews.ValueKind == JsonValueKind.Array && previews.GetArrayLength() > 0)
                    preview = Text(previews[previews.GetArrayLength() - 1], "u") ?? full;
                media.Add(new FeedMedia { Kind = MediaKind.Image, ThumbnailUrl = preview, FullUrl = full });
            }
            return media;
        }

        var hint = Text(item, "post_hint");
        var url = Text(item, "url_overridden_by_dest") ?? Text(item, "url") ?? "";
        var thumb = PreviewOf(item) ?? Text(item, "thumbnail") ?? "";

        if (item.TryGetProperty("is_video", out var isVideo) && isVideo.ValueKind == JsonValueKind.True)
        {
            // Reddit video is a DASH stream without its audio track; not a file to save.
            if (thumb.Length > 0)
                media.Add(new FeedMedia { Kind = MediaKind.Video, ThumbnailUrl = thumb, FullUrl = null });
            return media;
        }

        if (hint == "image" || LooksLikeImage(url))
            media.Add(new FeedMedia { Kind = MediaKind.Image, ThumbnailUrl = thumb.Length > 0 ? thumb : url, FullUrl = url });

        return media;
    }

    private static string? PreviewOf(JsonElement item)
    {
        if (!item.TryGetProperty("preview", out var preview) ||
            !preview.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            return null;

        var first = images[0];
        if (first.TryGetProperty("resolutions", out var sizes) && sizes.ValueKind == JsonValueKind.Array && sizes.GetArrayLength() > 0)
            return Text(sizes[Math.Min(2, sizes.GetArrayLength() - 1)], "url");
        return first.TryGetProperty("source", out var source) ? Text(source, "url") : null;
    }

    private static bool LooksLikeImage(string url) =>
        url.Contains("i.redd.it/", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"reddit\.com/(?<kind>u|user|r)/(?<name>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"^/?(?<kind>u|r)/(?<name>[A-Za-z0-9_-]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Prefixed();
}
