using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meows.Media;

namespace Meows.Plugins.Scruff.Services;

/// <summary>
/// Tumblr, in its Neue Post Format.
///
/// One request: a JSON part naming the blocks, then one part per picture keyed by the
/// identifier the JSON gave it. The pictures come first, then the words, which is how a photo
/// post reads on Tumblr. Tags go as their own field and may have spaces in them, so they go as
/// written. A rated post gets a community label; explicit work is refused, since Tumblr does
/// not allow it and the label does not cover it.
///
/// An account can have several blogs and the post goes to one of them, so the card has a
/// chooser once signed in, starting on the primary.
/// </summary>
public sealed class TumblrTarget : OAuthTarget
{
    public const int MaxTags = 30;

    private const string Api = "https://api.tumblr.com/v2";

    public TumblrTarget(HttpClient http) : base(http)
    {
    }

    public override string Id => "tumblr";

    public override string Name => "Tumblr";

    public override MediaLimits Limits { get; } = new(10, 20_000_000, 0, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Gif]);

    public override OAuthEndpoints Endpoints { get; } = new(
        "https://www.tumblr.com/oauth2/authorize",
        "https://api.tumblr.com/v2/oauth2/token",
        "basic write offline_access");

    public override bool TakesTextOnly => true;

    /// <summary>The blogs the account has, by name, primary first. Filled at sign in.</summary>
    public IReadOnlyList<string> Blogs { get; set; } = [];

    /// <summary>Which of them gets the post.</summary>
    public string Blog { get; set; } = "";

    public override Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var (problems, notes) = Checks.Common(this, draft, images);

        if (draft.Rating == Rating.Adult)
            problems.Add(new Problem("scruff.problem.noadult.tumblr"));

        var tags = CleanTags(draft.Tags);
        if (tags.Count > MaxTags)
            problems.Add(new Problem("scruff.problem.toomanytags", MaxTags, tags.Count));

        var text = Tags.Body(draft, withHashtags: false);
        return new Composed { Text = text, Problems = problems, Notes = notes };
    }

    protected override async Task<string> WhoAmIAsync(string accessToken, CancellationToken token)
    {
        using var response = await Http.SendAsync(Authorised(accessToken, HttpMethod.Get, $"{Api}/user/info"), token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body, "errors.0.detail", "meta.msg"));

        var (name, blogs) = ParseUser(body);
        Blogs = blogs;
        if (Blog.Length == 0 || !blogs.Contains(Blog))
            Blog = blogs.FirstOrDefault() ?? "";
        return name;
    }

    public override async Task<PostResult> PostAsync(Draft draft, IReadOnlyList<Outgoing> images, CancellationToken token)
    {
        var composed = Compose(draft, images);
        if (!composed.CanGo)
            return PostResult.Failed("The draft does not fit here.");
        if (Blog.Length == 0)
            return PostResult.Failed("No blog chosen.");

        try
        {
            var access = await AccessTokenAsync(token);

            using var content = new MultipartFormDataContent();
            var json = new StringContent(BuildPost(draft, images).ToJsonString());
            json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Add(json, "json");

            for (var i = 0; i < images.Count; i++)
            {
                var file = new ByteArrayContent(images[i].File.Bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(images[i].File.MimeType);
                content.Add(file, Identifier(i), images[i].FileName);
            }

            var request = Authorised(access, HttpMethod.Post, $"{Api}/blog/{Uri.EscapeDataString(Blog)}/posts");
            request.Content = content;

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                return PostResult.Failed(Explain(response, body, "errors.0.detail", "meta.msg"));

            return PostResult.Posted(PostLink(Blog, ParsePostId(body)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PostResult.Failed(ex.Message);
        }
    }

    // ---- The post, kept pure so it can be checked without a network ---------------------------

    public static string Identifier(int index) => $"picture{index}";

    /// <summary>As written, minus commas, which are what separates tags, and minus duplicates.</summary>
    public static List<string> CleanTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var tag in tags)
        {
            var text = tag.Replace(",", " ").Trim();
            while (text.Contains("  "))
                text = text.Replace("  ", " ");
            if (text.Length > 0 && seen.Add(text))
                clean.Add(text);
        }

        return clean;
    }

    public static JsonObject BuildPost(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var content = new JsonArray();
        for (var i = 0; i < images.Count; i++)
        {
            var block = new JsonObject
            {
                ["type"] = "image",
                ["media"] = new JsonArray(new JsonObject
                {
                    ["type"] = images[i].File.MimeType,
                    ["identifier"] = Identifier(i),
                }),
            };
            if (images[i].Alt.Length > 0)
                block["alt_text"] = images[i].Alt.Length > 4096 ? images[i].Alt[..4096] : images[i].Alt;
            content.Add(block);
        }

        var title = draft.Title.Trim();
        if (title.Length > 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = title, ["subtype"] = "heading2" });
        var text = draft.Text.Trim();
        if (text.Length > 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        var post = new JsonObject
        {
            ["content"] = content,
            ["state"] = "published",
            ["tags"] = string.Join(",", CleanTags(draft.Tags)),
        };

        if (draft.Rating != Rating.General)
        {
            post["has_community_label"] = true;
            post["community_label_categories"] = new JsonArray("sexual_themes");
        }

        return post;
    }

    public static (string Name, List<string> Blogs) ParseUser(string json)
    {
        using var document = JsonDocument.Parse(json);
        var user = document.RootElement.GetProperty("response").GetProperty("user");
        var name = user.GetProperty("name").GetString() ?? "";

        var blogs = new List<(string Name, bool Primary)>();
        if (user.TryGetProperty("blogs", out var array))
        {
            foreach (var blog in array.EnumerateArray())
            {
                var blogName = blog.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var primary = blog.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                if (blogName.Length > 0)
                    blogs.Add((blogName, primary));
            }
        }

        return (name, blogs.OrderByDescending(b => b.Primary).Select(b => b.Name).ToList());
    }

    public static string? ParsePostId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var response = document.RootElement.GetProperty("response");
            if (response.TryGetProperty("id_string", out var idString) && idString.GetString() is { Length: > 0 } s)
                return s;
            if (response.TryGetProperty("id", out var id))
                return id.ToString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return null;
    }

    public static string? PostLink(string blog, string? postId) =>
        postId is { Length: > 0 } ? $"https://{blog}.tumblr.com/post/{postId}" : null;

    private static HttpRequestMessage Authorised(string access, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return request;
    }
}
