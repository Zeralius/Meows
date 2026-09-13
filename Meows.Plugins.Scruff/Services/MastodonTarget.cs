using Meows.Media;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Meows.Plugins.Scruff.Services;

/// <summary>Which server, and the token it handed out under Preferences, Development.</summary>
public sealed record MastodonLogin(string Instance, string Token)
{
    /// <summary>The server as a base URL, whatever way it was typed.</summary>
    public string BaseUrl
    {
        get
        {
            var text = Instance.Trim().TrimEnd('/');
            if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return text;
            return "https://" + text.TrimStart('@');
        }
    }

    public string Host => new Uri(BaseUrl).Host;
}

/// <summary>
/// Mastodon, or anything speaking its API.
///
/// One media upload per picture, each of which may come back as "still working on it", then
/// one status once they are all ready. The token is a personal access token from the server's
/// own settings page, with write scope, and it is the only thing kept.
/// </summary>
public sealed class MastodonTarget : IApiTarget
{
    /// <summary>The default. A server can raise it, and the tab reads what the server says at sign in.</summary>
    public const int DefaultMaxCharacters = 500;

    private readonly HttpClient _http;

    public MastodonTarget(HttpClient http) => _http = http;

    public string Id => "mastodon";

    public string Name => "Mastodon";

    public MediaLimits Limits { get; } = new(4, 16_000_000, 4096, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Gif]);

    public bool PostsItself => true;

    public bool TakesTextOnly => true;

    public MastodonLogin? Login { get; set; }

    public int MaxCharacters { get; set; } = DefaultMaxCharacters;

    /// <summary>public, unlisted, private or direct. Unlisted is a reasonable default for art.</summary>
    public string Visibility { get; set; } = "public";

    public bool IsSignedIn => Login is { Instance.Length: > 0, Token.Length: > 0 };

    public string? Account { get; set; }

    public Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var text = Tags.Body(draft);
        var length = TextLength.Graphemes(text);
        var (problems, notes) = Checks.Common(this, draft, images);

        if (length > MaxCharacters)
            problems.Add(new Problem("scruff.problem.toolong", MaxCharacters, length));

        return new Composed { Text = text, Length = length, Limit = MaxCharacters, Problems = problems, Notes = notes };
    }

    /// <summary>Who the token belongs to, and how long a post may be on this server.</summary>
    public async Task<(string Account, int MaxCharacters)> VerifyAsync(MastodonLogin login, CancellationToken token)
    {
        using var response = await _http.SendAsync(
            Authorised(login, HttpMethod.Get, $"{login.BaseUrl}/api/v1/accounts/verify_credentials"), token);

        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body));

        string account;
        using (var document = JsonDocument.Parse(body))
        {
            account = "@" + (document.RootElement.GetProperty("acct").GetString() ?? "") + "@" + login.Host;
        }

        var max = DefaultMaxCharacters;
        try
        {
            using var instance = await _http.GetAsync($"{login.BaseUrl}/api/v2/instance", token);
            if (instance.IsSuccessStatusCode)
                max = ReadMaxCharacters(await instance.Content.ReadAsStringAsync(token));
        }
        catch (Exception)
        {
            // The default is what nearly every server uses anyway.
        }

        return (account, max);
    }

    public static int ReadMaxCharacters(string instanceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(instanceJson);
            if (document.RootElement.TryGetProperty("configuration", out var configuration) &&
                configuration.TryGetProperty("statuses", out var statuses) &&
                statuses.TryGetProperty("max_characters", out var max) &&
                max.TryGetInt32(out var value) && value > 0)
                return value;
        }
        catch (JsonException)
        {
        }

        return DefaultMaxCharacters;
    }

    public async Task<PostResult> PostAsync(Draft draft, IReadOnlyList<Outgoing> images, CancellationToken token)
    {
        if (Login is null)
            return PostResult.Failed("Not signed in.");

        var composed = Compose(draft, images);
        if (!composed.CanGo)
            return PostResult.Failed("The draft does not fit here.");

        try
        {
            var ids = new List<string>();
            foreach (var image in images)
                ids.Add(await UploadAsync(Login, image, token));

            var form = new List<KeyValuePair<string, string>>
            {
                new("status", composed.Text),
                new("visibility", Visibility),
                new("sensitive", draft.Rating == Rating.General ? "false" : "true"),
            };
            foreach (var id in ids)
                form.Add(new KeyValuePair<string, string>("media_ids[]", id));

            var request = Authorised(Login, HttpMethod.Post, $"{Login.BaseUrl}/api/v1/statuses");
            request.Content = new FormUrlEncodedContent(form);

            // The same key sent twice makes one post, not two. A retry after a timeout is the
            // usual way of double posting, and this is what stops it.
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

            using var response = await _http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                return PostResult.Failed(Explain(response, body));

            using var document = JsonDocument.Parse(body);
            return PostResult.Posted(document.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null);
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

    /// <summary>
    /// One picture up, and its id once the server has finished with it. A 202 means it is still
    /// being processed, and a status posted with an unprocessed attachment is refused, so this
    /// waits until the server says 200.
    /// </summary>
    private async Task<string> UploadAsync(MastodonLogin login, Outgoing image, CancellationToken token)
    {
        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(image.File.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.File.MimeType);
        content.Add(file, "file", image.FileName);

        if (image.Alt.Length > 0)
            content.Add(new StringContent(image.Alt), "description");

        var request = Authorised(login, HttpMethod.Post, $"{login.BaseUrl}/api/v2/media");
        request.Content = content;

        using var response = await _http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body));

        string id;
        using (var document = JsonDocument.Parse(body))
        {
            id = document.RootElement.GetProperty("id").GetString() ?? "";
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);

                using var check = await _http.SendAsync(
                    Authorised(login, HttpMethod.Get, $"{login.BaseUrl}/api/v1/media/{id}"), token);

                if (check.StatusCode == HttpStatusCode.OK)
                    return id;
                if (check.StatusCode != HttpStatusCode.PartialContent)
                    throw new InvalidOperationException(Explain(check, await check.Content.ReadAsStringAsync(token)));
            }

            throw new InvalidOperationException("The server did not finish processing the picture.");
        }

        return id;
    }

    private static HttpRequestMessage Authorised(MastodonLogin login, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token.Trim());
        return request;
    }

    private static string Explain(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }
}
