using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Meows.Media;

namespace Meows.Plugins.Scruff.Services;

/// <summary>
/// DeviantArt, through Sta.sh.
///
/// Two calls per picture: the file goes up into Sta.sh with its title, comments and tags, and
/// the Sta.sh item is then published as a deviation with the rating on it. Each picture is its
/// own deviation, as on FurAffinity, so a post of three pictures is three deviations with the
/// same words and a count on the title.
///
/// The app is the person's own, registered under their account, since DeviantArt gives no
/// other way in. Tags are letters, digits and underscores, which is what the keyword spelling
/// already produces. The rating maps onto DeviantArt's two mature levels: Mature is moderate
/// nudity, Adult is strict sexual. Every deviation goes up with "noai" set, since that is a
/// choice worth making by default and easy to change on the site.
/// </summary>
public sealed class DeviantArtTarget : OAuthTarget
{
    public const int MaxTitleLength = 50;

    public const int MaxTags = 30;

    private const string Api = "https://www.deviantart.com/api/v1/oauth2";

    public DeviantArtTarget(HttpClient http) : base(http)
    {
    }

    public override string Id => "deviantart";

    public override string Name => "DeviantArt";

    public override TagSpelling Spelling => TagSpelling.AsciiKeywords;

    public override MediaLimits Limits { get; } = new(0, 30_000_000, 0, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Gif]);

    public override OAuthEndpoints Endpoints { get; } = new(
        "https://www.deviantart.com/oauth2/authorize",
        "https://www.deviantart.com/oauth2/token",
        "basic user stash publish");

    public override bool TakesTextOnly => false;

    public override Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var (problems, notes) = Checks.Common(this, draft, images);
        var title = draft.Title.Trim();

        if (title.Length == 0 && images.Count > 0)
            problems.Add(new Problem("scruff.problem.notitle"));
        else if (Titles(draft, images.Count).Any(t => t.Length > MaxTitleLength))
            problems.Add(new Problem("scruff.problem.titlelong", MaxTitleLength));

        var tags = CleanTags(draft.Tags);
        if (tags.Count > MaxTags)
            problems.Add(new Problem("scruff.problem.toomanytags", MaxTags, tags.Count));
        if (images.Count > 1)
            notes.Add(new Problem("scruff.note.onebyone", images.Count));

        return new Composed
        {
            Text = string.Join(" ", tags),
            Problems = problems,
            Notes = notes,
        };
    }

    protected override async Task<string> WhoAmIAsync(string accessToken, CancellationToken token)
    {
        using var response = await Http.SendAsync(Authorised(accessToken, HttpMethod.Get, $"{Api}/user/whoami"), token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body, "error_description", "error"));

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("username").GetString() ?? "";
    }

    public override async Task<PostResult> PostAsync(Draft draft, IReadOnlyList<Outgoing> images, CancellationToken token)
    {
        var composed = Compose(draft, images);
        if (!composed.CanGo)
            return PostResult.Failed("The draft does not fit here.");

        try
        {
            var access = await AccessTokenAsync(token);
            var titles = Titles(draft, images.Count);
            var tags = CleanTags(draft.Tags);
            string? firstUrl = null;

            for (var i = 0; i < images.Count; i++)
            {
                var itemId = await SubmitAsync(access, images[i], titles[i], draft.Text.Trim(), tags, token);
                var url = await PublishAsync(access, itemId, draft.Rating, tags, token);
                firstUrl ??= url;
            }

            return PostResult.Posted(firstUrl);
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

    private async Task<string> SubmitAsync(string access, Outgoing image, string title, string comments, IReadOnlyList<string> tags, CancellationToken token)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(title), "title");
        if (comments.Length > 0)
            content.Add(new StringContent(comments), "artist_comments");
        foreach (var tag in tags)
            content.Add(new StringContent(tag), "tags[]");

        var file = new ByteArrayContent(image.File.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.File.MimeType);
        content.Add(file, "file", image.FileName);

        var request = Authorised(access, HttpMethod.Post, $"{Api}/stash/submit");
        request.Content = content;

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body, "error_description", "error"));

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("itemid").ToString();
    }

    private async Task<string?> PublishAsync(string access, string itemId, Rating rating, IReadOnlyList<string> tags, CancellationToken token)
    {
        var request = Authorised(access, HttpMethod.Post, $"{Api}/stash/publish");
        request.Content = new FormUrlEncodedContent(PublishForm(itemId, rating, tags));

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body, "error_description", "error"));

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    // ---- The forms, kept pure so they can be checked without a network ------------------------

    /// <summary>One title per picture: as written for one, numbered when there are more.</summary>
    public static List<string> Titles(Draft draft, int count)
    {
        var title = draft.Title.Trim();
        if (count <= 1)
            return [title];
        return Enumerable.Range(1, count).Select(i => $"{title} ({i}/{count})").ToList();
    }

    /// <summary>Keyword spelling, then anything DeviantArt would refuse is dropped rather than replaced.</summary>
    public static List<string> CleanTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var tag in tags)
        {
            var keyword = new string(Tags.Keyword(tag).Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
            if (keyword.Length > 0 && seen.Add(keyword))
                clean.Add(keyword);
        }

        return clean;
    }

    public static List<KeyValuePair<string, string>> PublishForm(string itemId, Rating rating, IReadOnlyList<string> tags)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("itemid", itemId),
            new("agree_submission", "1"),
            new("agree_tos", "1"),
            new("feature", "1"),
            new("allow_comments", "1"),
            new("is_ai_generated", "0"),
            new("noai", "1"),
            new("is_mature", rating == Rating.General ? "0" : "1"),
        };

        switch (rating)
        {
            case Rating.Mature:
                form.Add(new("mature_level", "moderate"));
                form.Add(new("mature_classification[]", "nudity"));
                break;
            case Rating.Adult:
                form.Add(new("mature_level", "strict"));
                form.Add(new("mature_classification[]", "sexual"));
                break;
        }

        foreach (var tag in tags)
            form.Add(new("tags[]", tag));

        return form;
    }

    private static HttpRequestMessage Authorised(string access, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return request;
    }
}
