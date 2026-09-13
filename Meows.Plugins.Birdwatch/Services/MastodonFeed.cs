using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meows.Plugins.Birdwatch.Services;

/// <summary>
/// Mastodon, and everything that speaks its API: Pixelfed, Akkoma, GoToSocial. The public
/// statuses of a public account answer without a token on most instances, and the attachments
/// are plain files on a CDN. An instance that has turned on authorized fetch answers 401, and
/// that is reported as what it is rather than as "nothing posted".
///
/// A handle is <c>user@instance</c>, which is how the fediverse writes them and what a profile
/// link boils down to.
/// </summary>
public sealed partial class MastodonFeed : IFeedSource
{
    private readonly HttpClient _http;

    public MastodonFeed(HttpClient http) => _http = http;

    public string ServiceName => "Mastodon";

    /// <summary>@user@instance, user@instance, https://instance/@user, https://instance/users/user, and any of those with a post id after.</summary>
    public static bool Owns(string pasted)
    {
        var text = pasted.Trim();
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return ProfileLink().IsMatch(text);
        return Acct().IsMatch(text.TrimStart('@'));
    }

    /// <summary>user@instance, lower case, out of whatever was pasted.</summary>
    public string TidyHandle(string pasted)
    {
        var text = pasted.Trim();
        var link = ProfileLink().Match(text);
        if (link.Success)
            return $"{link.Groups["user"].Value}@{link.Groups["host"].Value}".ToLowerInvariant();

        return text.TrimStart('@').Trim().ToLowerInvariant();
    }

    public async Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token)
    {
        var at = handle.IndexOf('@');
        if (at <= 0)
            throw new InvalidOperationException($"{handle} is not user@instance");
        var user = handle[..at];
        var host = handle[(at + 1)..];

        // Two calls: the account id, then its statuses. The lookup is cheap and the id is not
        // worth caching across a restart, since it is one request per refresh.
        var id = await LookupIdAsync(host, user, token);

        var url = $"https://{host}/api/v1/accounts/{id}/statuses?only_media=true&exclude_replies=true&limit=40";
        if (cursor is { Length: > 0 })
            url += "&max_id=" + Uri.EscapeDataString(cursor);

        using var response = await _http.GetAsync(url, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException($"{host} only shows this account to signed-in users");
        response.EnsureSuccessStatusCode();

        return Parse(await response.Content.ReadAsStringAsync(token), host);
    }

    private async Task<string> LookupIdAsync(string host, string user, CancellationToken token)
    {
        using var response = await _http.GetAsync(
            $"https://{host}/api/v1/accounts/lookup?acct={Uri.EscapeDataString(user)}", token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"no account called {user} on {host}");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException($"{host} only shows accounts to signed-in users");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return Text(document.RootElement, "id") ?? throw new InvalidOperationException($"{host} answered without an account id");
    }

    /// <summary>
    /// Turns a statuses response into posts. A boost carries the boosted status under
    /// <c>reblog</c>, and that is where its pictures are.
    /// </summary>
    public static FeedPage Parse(string json, string host)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return FeedPage.Empty;

        var posts = new List<FeedPost>();
        string? last = null;
        foreach (var status in root.EnumerateArray())
        {
            last = Text(status, "id") ?? last;
            if (ReadStatus(status, host) is { } post)
                posts.Add(post);
        }

        // The next page starts below the last id seen. No posts at all means no next page.
        return new FeedPage(posts, root.GetArrayLength() == 0 ? null : last);
    }

    private static FeedPost? ReadStatus(JsonElement status, string host)
    {
        var isBoost = status.TryGetProperty("reblog", out var reblog) && reblog.ValueKind == JsonValueKind.Object;
        var shown = isBoost ? reblog : status;

        var account = status.TryGetProperty("account", out var a) ? a : default;
        var handle = Text(account, "acct") ?? "";
        if (handle.Length > 0 && !handle.Contains('@'))
            handle = $"{handle}@{host}";

        var labels = new List<string>();
        if (shown.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True)
            labels.Add("sensitive");
        if (Text(shown, "spoiler_text") is { Length: > 0 } warning)
            labels.Add(warning);

        return new FeedPost
        {
            Id = Text(shown, "uri") ?? Text(shown, "url") ?? Text(status, "id") ?? "",
            AuthorHandle = handle,
            AuthorName = Text(account, "display_name") is { Length: > 0 } name ? name : handle,
            Text = StripHtml(Text(shown, "content") ?? ""),
            PostedAt = When(Text(status, "created_at")),
            WebUrl = Text(shown, "url"),
            IsRepost = isBoost,
            Labels = labels,
            Media = ReadMedia(shown),
        };
    }

    private static List<FeedMedia> ReadMedia(JsonElement status)
    {
        var media = new List<FeedMedia>();
        if (!status.TryGetProperty("media_attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
            return media;

        foreach (var attachment in attachments.EnumerateArray())
        {
            var type = Text(attachment, "type");
            var full = Text(attachment, "url") ?? Text(attachment, "remote_url");
            var preview = Text(attachment, "preview_url") ?? full ?? "";

            switch (type)
            {
                case "image":
                    media.Add(new FeedMedia { Kind = MediaKind.Image, ThumbnailUrl = preview, FullUrl = full, Alt = Text(attachment, "description") ?? "" });
                    break;
                case "video":
                case "gifv":
                    // A file rather than a stream, so it could be saved; it is not, to keep
                    // the intake folder to pictures, which is what Kibble sorts.
                    media.Add(new FeedMedia { Kind = MediaKind.Video, ThumbnailUrl = preview, FullUrl = null, Alt = Text(attachment, "description") ?? "" });
                    break;
            }
        }

        return media;
    }

    /// <summary>Mastodon hands out HTML. The words are wanted, the paragraphs are not.</summary>
    public static string StripHtml(string html)
    {
        var text = Tags().Replace(html.Replace("</p>", "\n").Replace("<br>", "\n").Replace("<br />", "\n"), "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static DateTimeOffset When(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"^https?://(?<host>[^/]+)/(?:@|users/)(?<user>[A-Za-z0-9_.-]+)(?:/|$|\?)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileLink();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")]
    private static partial Regex Acct();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
