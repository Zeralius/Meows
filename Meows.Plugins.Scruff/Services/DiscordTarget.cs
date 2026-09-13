using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meows.Media;

namespace Meows.Plugins.Scruff.Services;

/// <summary>A webhook, which is a URL that is itself the credential, and what it said about itself.</summary>
public sealed record DiscordWebhook(string Url, string Name, string GuildId, string ChannelId);

/// <summary>
/// A Discord channel, through a webhook.
///
/// No app, no bot, no OAuth: whoever runs the server makes a webhook under the channel's
/// Integrations and hands over its URL, and one POST to that URL is a message in the channel.
/// The URL is the whole secret, so it is kept sealed like a password. Pictures are attachments,
/// with the alt text as their description, and a rated post has them marked as spoilers, which
/// is the only cover Discord gives outside an age-gated channel.
/// </summary>
public sealed class DiscordTarget : IApiTarget
{
    public const int MaxCharacters = 2000;

    private readonly HttpClient _http;

    public DiscordTarget(HttpClient http) => _http = http;

    public string Id => "discord";

    public string Name => "Discord";

    public MediaLimits Limits { get; } = new(10, 10_000_000, 0, [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Gif]);

    public bool PostsItself => true;

    public bool TakesTextOnly => true;

    public DiscordWebhook? Webhook { get; set; }

    public bool IsSignedIn => Webhook is { Url.Length: > 0 };

    public string? Account => Webhook?.Name;

    public Composed Compose(Draft draft, IReadOnlyList<Outgoing> images)
    {
        // Discord has no hashtags, so the tags stay off the message.
        var text = Tags.Body(draft, withHashtags: false);
        var length = TextLength.Graphemes(text);
        var (problems, notes) = Checks.Common(this, draft, images);

        if (length > MaxCharacters)
            problems.Add(new Problem("scruff.problem.toolong", MaxCharacters, length));
        if (draft.Rating != Rating.General && images.Count > 0)
            notes.Add(new Problem("scruff.note.spoiler"));

        return new Composed { Text = text, Length = length, Limit = MaxCharacters, Problems = problems, Notes = notes };
    }

    /// <summary>What the webhook says about itself, which proves the URL and names the card.</summary>
    public async Task<DiscordWebhook> VerifyAsync(string url, CancellationToken token)
    {
        url = url.Trim();
        if (!IsWebhookUrl(url))
            throw new InvalidOperationException("That is not a Discord webhook URL.");

        using var response = await _http.GetAsync(url, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response, body));

        return ParseWebhook(url, body);
    }

    public static bool IsWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" &&
        (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.Contains("/webhooks/", StringComparison.Ordinal);

    public static DiscordWebhook ParseWebhook(string url, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new DiscordWebhook(
            url,
            root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            root.TryGetProperty("guild_id", out var guild) ? guild.GetString() ?? "" : "",
            root.TryGetProperty("channel_id", out var channel) ? channel.GetString() ?? "" : "");
    }

    public async Task<PostResult> PostAsync(Draft draft, IReadOnlyList<Outgoing> images, CancellationToken token)
    {
        if (Webhook is null)
            return PostResult.Failed("Not signed in.");

        var composed = Compose(draft, images);
        if (!composed.CanGo)
            return PostResult.Failed("The draft does not fit here.");

        try
        {
            var names = FileNames(images, draft.Rating);
            using var content = new MultipartFormDataContent();

            var payload = new StringContent(BuildPayload(composed.Text, images, names).ToJsonString());
            payload.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Add(payload, "payload_json");

            for (var i = 0; i < images.Count; i++)
            {
                var file = new ByteArrayContent(images[i].File.Bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(images[i].File.MimeType);
                content.Add(file, $"files[{i}]", names[i]);
            }

            using var response = await _http.PostAsync(Webhook.Url + "?wait=true", content, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                return PostResult.Failed(Explain(response, body));

            using var document = JsonDocument.Parse(body);
            var id = document.RootElement.TryGetProperty("id", out var message) ? message.GetString() : null;
            return PostResult.Posted(MessageLink(Webhook, id));
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

    // ---- The message, kept pure so it can be checked without a network ------------------------

    /// <summary>A rated picture is a spoiler, which Discord spells as a prefix on the file name.</summary>
    public static List<string> FileNames(IReadOnlyList<Outgoing> images, Rating rating) =>
        images.Select(i => rating == Rating.General ? i.FileName : "SPOILER_" + i.FileName).ToList();

    public static JsonObject BuildPayload(string text, IReadOnlyList<Outgoing> images, IReadOnlyList<string> names)
    {
        var attachments = new JsonArray();
        for (var i = 0; i < images.Count; i++)
        {
            var attachment = new JsonObject { ["id"] = i, ["filename"] = names[i] };
            if (images[i].Alt.Length > 0)
                attachment["description"] = images[i].Alt.Length > 1024 ? images[i].Alt[..1024] : images[i].Alt;
            attachments.Add(attachment);
        }

        var payload = new JsonObject { ["attachments"] = attachments };
        if (text.Length > 0)
            payload["content"] = text;
        return payload;
    }

    public static string? MessageLink(DiscordWebhook webhook, string? messageId) =>
        messageId is { Length: > 0 } && webhook.GuildId.Length > 0 && webhook.ChannelId.Length > 0
            ? $"https://discord.com/channels/{webhook.GuildId}/{webhook.ChannelId}/{messageId}"
            : null;

    private static string Explain(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }
}
