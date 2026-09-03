using System.Net.Http;
using System.Text.Json;

namespace Meows.Plugins.Birdwatch.Services;

/// <summary>
/// Bluesky, through the public AppView.
///
/// No account, no app password, no token to keep anywhere. getAuthorFeed answers unauthenticated
/// for any public account, and the images come back as ordinary CDN URLs that fetch the same way.
/// That is the whole reason this is the first source: the home timeline does need a login, and a
/// plugin holding its first real secret is a decision worth taking on its own rather than as a
/// side effect of wanting to see some pictures.
/// </summary>
public sealed class BlueskyFeed : IFeedSource
{
    /// <summary>The unauthenticated read-only view. The authenticated host is a different one.</summary>
    public const string PublicApi = "https://public.api.bsky.app";

    private readonly HttpClient _http;

    public BlueskyFeed(HttpClient http) => _http = http;

    public string ServiceName => "Bluesky";

    /// <summary>
    /// A handle as the API wants it: no leading at sign, lower case, trimmed. People copy them
    /// with the at sign because that is how they are written everywhere else.
    /// </summary>
    public static string TidyHandle(string handle) =>
        handle.Trim().TrimStart('@').Trim().ToLowerInvariant();

    public async Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token)
    {
        var actor = Uri.EscapeDataString(TidyHandle(handle));
        var url = $"{PublicApi}/xrpc/app.bsky.feed.getAuthorFeed?actor={actor}&limit=50";
        if (cursor is { Length: > 0 })
            url += "&cursor=" + Uri.EscapeDataString(cursor);

        using var response = await _http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        return Parse(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>
    /// Turns a getAuthorFeed response into posts.
    ///
    /// Separate from the fetching on purpose: this is where the shapes are, so this is the part
    /// worth testing, and it is tested against a real captured response rather than against
    /// something written to match the code.
    /// </summary>
    public static FeedPage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var posts = new List<FeedPost>();
        if (root.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in feed.EnumerateArray())
            {
                if (ReadItem(item) is { } post)
                    posts.Add(post);
            }
        }

        return new FeedPage(posts, Text(root, "cursor"));
    }

    private static FeedPost? ReadItem(JsonElement item)
    {
        if (!item.TryGetProperty("post", out var post))
            return null;

        var author = post.TryGetProperty("author", out var a) ? a : default;
        var record = post.TryGetProperty("record", out var r) ? r : default;
        var uri = Text(post, "uri") ?? "";
        var handle = Text(author, "handle") ?? "";

        return new FeedPost
        {
            // The at:// uri is unique per post and stable, which is what "already saved" has to
            // be remembered against.
            Id = uri,
            AuthorHandle = handle,
            AuthorName = Text(author, "displayName") ?? handle,
            Text = Text(record, "text") ?? "",
            PostedAt = When(Text(post, "indexedAt")),
            WebUrl = WebLink(handle, uri),
            IsRepost = item.TryGetProperty("reason", out var reason) &&
                       (Text(reason, "$type") ?? "").Contains("reasonRepost", StringComparison.Ordinal),
            Labels = ReadLabels(post),
            Media = ReadMedia(post),
        };
    }

    private static List<FeedMedia> ReadMedia(JsonElement post)
    {
        var media = new List<FeedMedia>();
        if (post.TryGetProperty("embed", out var embed))
            Collect(embed, media);
        return media;
    }

    /// <summary>
    /// Pulls the media out of an embed, following one level into a quoted post.
    ///
    /// Quoting an image post is how a good deal of art travels, and the pictures are just as
    /// fetchable from there. One level only: a quote of a quote is somebody else's thread.
    /// </summary>
    private static void Collect(JsonElement embed, List<FeedMedia> into, bool nested = false)
    {
        switch (Text(embed, "$type"))
        {
            case "app.bsky.embed.images#view":
                if (embed.TryGetProperty("images", out var images) &&
                    images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var image in images.EnumerateArray())
                    {
                        into.Add(new FeedMedia
                        {
                            Kind = MediaKind.Image,
                            ThumbnailUrl = Text(image, "thumb") ?? "",
                            FullUrl = Text(image, "fullsize"),
                            Alt = Text(image, "alt") ?? "",
                        });
                    }
                }
                break;

            case "app.bsky.embed.video#view":
                into.Add(new FeedMedia
                {
                    Kind = MediaKind.Video,
                    ThumbnailUrl = Text(embed, "thumbnail") ?? "",

                    // Deliberately null. What the API gives is an HLS manifest rather than a
                    // file, so saving one means reassembling segments, which is ffmpeg's job.
                    FullUrl = null,
                    Alt = Text(embed, "alt") ?? "",
                });
                break;

            case "app.bsky.embed.recordWithMedia#view":
                if (embed.TryGetProperty("media", out var withMedia))
                    Collect(withMedia, into, nested);
                break;

            case "app.bsky.embed.record#view" when !nested:
                if (embed.TryGetProperty("record", out var quoted) &&
                    quoted.TryGetProperty("embeds", out var embeds) &&
                    embeds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inner in embeds.EnumerateArray())
                        Collect(inner, into, nested: true);
                }
                break;

            // external#view is a link preview. Its thumbnail belongs to the site being linked
            // rather than to the person posting, so it is not theirs to hand out.
        }
    }

    private static List<string> ReadLabels(JsonElement post)
    {
        var labels = new List<string>();
        if (!post.TryGetProperty("labels", out var array) || array.ValueKind != JsonValueKind.Array)
            return labels;

        foreach (var label in array.EnumerateArray())
        {
            if (Text(label, "val") is { Length: > 0 } value && !labels.Contains(value))
                labels.Add(value);
        }

        return labels;
    }

    /// <summary>
    /// The web address for a post, built from the at:// uri. The last path segment is the record
    /// key, which is what bsky.app puts in its own links.
    /// </summary>
    public static string? WebLink(string? handle, string atUri)
    {
        if (handle is not { Length: > 0 } || atUri.Length == 0)
            return null;

        var key = atUri[(atUri.LastIndexOf('/') + 1)..];
        return key.Length == 0 ? null : $"https://bsky.app/profile/{handle}/post/{key}";
    }

    private static DateTimeOffset When(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
