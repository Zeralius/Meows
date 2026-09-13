using Meows.Media;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Meows.Plugins.Scruff.Services;

/// <summary>The two halves of a Bluesky login. An app password, never the account's own.</summary>
public sealed record BlueskyLogin(string Identifier, string Password);

/// <summary>
/// Bluesky, through the AT Protocol.
///
/// Three calls: a session from the handle and an app password, one blob upload per picture,
/// and one record. Hashtags are not text on Bluesky, they are facets, which are byte ranges in
/// the UTF-8 of the post pointing at a tag, so the body is composed here and the ranges worked
/// out from it rather than typed. The limit is 300 graphemes and four pictures of at most a
/// megabyte each, and the pictures are fitted to that before they get here.
/// </summary>
public sealed class BlueskyTarget : IApiTarget
{
    public const string Entryway = "https://bsky.social";

    /// <summary>What the PDS refuses above. Not a mebibyte: the limit is written in decimal.</summary>
    public const int MaxBlobBytes = 1_000_000;

    public const int MaxGraphemes = 300;

    private static readonly Regex HashtagPattern = new(@"(?<=^|\s)#([\p{L}\p{N}_]+)", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"https?://[^\s<>""']+", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public BlueskyTarget(HttpClient http) => _http = http;

    public string Id => "bluesky";

    public string Name => "Bluesky";

    public MediaLimits Limits { get; } = new(4, MaxBlobBytes, 2000, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Gif]);

    public bool PostsItself => true;

    public bool TakesTextOnly => true;

    public BlueskyLogin? Login { get; set; }

    public bool IsSignedIn => Login is { Identifier.Length: > 0, Password.Length: > 0 };

    public string? Account => Login?.Identifier;

    public Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        var text = Tags.Body(draft);
        var length = TextLength.Graphemes(text);
        var (problems, notes) = Checks.Common(this, draft, images);

        if (length > MaxGraphemes)
            problems.Add(new Problem("scruff.problem.toolong", MaxGraphemes, length));

        return new Composed { Text = text, Length = length, Limit = MaxGraphemes, Problems = problems, Notes = notes };
    }

    /// <summary>
    /// Proves the login works and says who it belongs to. Called when the password is saved, so
    /// a typo is found then rather than at the first post.
    /// </summary>
    public async Task<string> VerifyAsync(BlueskyLogin login, CancellationToken token)
    {
        var session = await CreateSessionAsync(login, token);
        return session.Handle;
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
            var session = await CreateSessionAsync(Login, token);

            var blobs = new List<JsonNode>();
            foreach (var image in images)
            {
                var blob = await UploadAsync(session, image.File, token);
                blobs.Add(BuildImage(blob, image));
            }

            var record = BuildRecord(composed.Text, draft.Rating, blobs, DateTimeOffset.UtcNow);

            using var response = await _http.SendAsync(Authorised(session, HttpMethod.Post,
                $"{session.Pds}/xrpc/com.atproto.repo.createRecord",
                JsonContent.Create(new JsonObject
                {
                    ["repo"] = session.Did,
                    ["collection"] = "app.bsky.feed.post",
                    ["record"] = record,
                })), token);

            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                return PostResult.Failed(Explain(response, body));

            using var document = JsonDocument.Parse(body);
            var uri = document.RootElement.GetProperty("uri").GetString() ?? "";
            return PostResult.Posted(WebLink(session.Handle, uri));
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

    // ---- The record, kept pure so it can be checked without a network -------------------------

    /// <summary>
    /// The post record as the PDS wants it: the text, when, the facets found in the text, the
    /// pictures if any, and a self label when the rating asks for one.
    /// </summary>
    public static JsonObject BuildRecord(string text, Rating rating, IReadOnlyList<JsonNode> images, DateTimeOffset createdAt)
    {
        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = text,
            ["createdAt"] = createdAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        };

        var facets = Facets(text);
        if (facets.Count > 0)
            record["facets"] = new JsonArray(facets.ToArray());

        if (images.Count > 0)
        {
            record["embed"] = new JsonObject
            {
                ["$type"] = "app.bsky.embed.images",
                ["images"] = new JsonArray(images.ToArray()),
            };
        }

        var label = rating switch
        {
            Rating.Adult => "porn",
            Rating.Mature => "sexual",
            _ => null,
        };

        if (label is not null)
        {
            record["labels"] = new JsonObject
            {
                ["$type"] = "com.atproto.label.defs#selfLabels",
                ["values"] = new JsonArray(new JsonObject { ["val"] = label }),
            };
        }

        return record;
    }

    /// <summary>
    /// Hashtags and links as byte ranges. UTF-8 bytes, not characters: an umlaut before a tag
    /// shifts it by one and an emoji by four, and a facet that is off by one highlights the
    /// wrong letters and links the wrong tag.
    /// </summary>
    public static List<JsonNode> Facets(string text)
    {
        var facets = new List<JsonNode>();

        foreach (Match match in HashtagPattern.Matches(text))
        {
            facets.Add(Facet(text, match.Index, match.Length, new JsonObject
            {
                ["$type"] = "app.bsky.richtext.facet#tag",
                ["tag"] = match.Groups[1].Value,
            }));
        }

        foreach (Match match in LinkPattern.Matches(text))
        {
            // A trailing full stop or bracket belongs to the sentence, not the link.
            var link = match.Value.TrimEnd('.', ',', ';', ':', ')', ']');
            facets.Add(Facet(text, match.Index, link.Length, new JsonObject
            {
                ["$type"] = "app.bsky.richtext.facet#link",
                ["uri"] = link,
            }));
        }

        return facets;
    }

    private static JsonObject Facet(string text, int start, int length, JsonObject feature) => new()
    {
        ["index"] = new JsonObject
        {
            ["byteStart"] = Encoding.UTF8.GetByteCount(text.AsSpan(0, start)),
            ["byteEnd"] = Encoding.UTF8.GetByteCount(text.AsSpan(0, start + length)),
        },
        ["features"] = new JsonArray(feature),
    };

    private static JsonObject BuildImage(JsonNode blob, Outgoing image)
    {
        var node = new JsonObject
        {
            ["alt"] = image.Alt,
            ["image"] = blob,
        };

        if (image.File.Width > 0 && image.File.Height > 0)
        {
            node["aspectRatio"] = new JsonObject
            {
                ["width"] = image.File.Width,
                ["height"] = image.File.Height,
            };
        }

        return node;
    }

    // ---- The wire ------------------------------------------------------------------------------

    public sealed record Session(string AccessJwt, string Did, string Handle, string Pds);

    private async Task<Session> CreateSessionAsync(BlueskyLogin login, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync(
            $"{Entryway}/xrpc/com.atproto.server.createSession",
            new { identifier = login.Identifier.Trim().TrimStart('@'), password = login.Password },
            token);

        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body));

        return ParseSession(body);
    }

    /// <summary>
    /// The session, and the server the account actually lives on. The entryway answers for
    /// every account, but the record belongs on the account's own PDS, which is named in the
    /// DID document that comes back with the session.
    /// </summary>
    public static Session ParseSession(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var pds = Entryway;
        if (root.TryGetProperty("didDoc", out var doc) &&
            doc.TryGetProperty("service", out var services) &&
            services.ValueKind == JsonValueKind.Array)
        {
            foreach (var service in services.EnumerateArray())
            {
                if (service.TryGetProperty("id", out var id) && id.GetString() == "#atproto_pds" &&
                    service.TryGetProperty("serviceEndpoint", out var endpoint) &&
                    endpoint.GetString() is { Length: > 0 } url)
                {
                    pds = url.TrimEnd('/');
                    break;
                }
            }
        }

        return new Session(
            root.GetProperty("accessJwt").GetString() ?? "",
            root.GetProperty("did").GetString() ?? "",
            root.GetProperty("handle").GetString() ?? "",
            pds);
    }

    private async Task<JsonNode> UploadAsync(Session session, Prepared file, CancellationToken token)
    {
        var content = new ByteArrayContent(file.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.MimeType);

        using var response = await _http.SendAsync(
            Authorised(session, HttpMethod.Post, $"{session.Pds}/xrpc/com.atproto.repo.uploadBlob", content), token);

        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body));

        // The blob reference has to go back exactly as it came, so it is kept as a node rather
        // than read into a type and written out again.
        return JsonNode.Parse(body)?["blob"]?.DeepClone()
               ?? throw new InvalidOperationException("The upload answered without a blob.");
    }

    private static HttpRequestMessage Authorised(Session session, HttpMethod method, string url, HttpContent content)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
        return request;
    }

    private static string Explain(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
                return text;
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.GetString() is { Length: > 0 } code)
                return code;
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }

    public static string? WebLink(string handle, string atUri)
    {
        if (handle.Length == 0 || atUri.Length == 0)
            return null;

        var key = atUri[(atUri.LastIndexOf('/') + 1)..];
        return key.Length == 0 ? null : $"https://bsky.app/profile/{handle}/post/{key}";
    }
}
