using System.Net.Http;

namespace Meows.Plugins.Birdwatch.Services;

/// <summary>What happened to one file.</summary>
public enum SaveOutcome
{
    Saved,

    /// <summary>Already on disk under the name it would have been given.</summary>
    AlreadyThere,

    /// <summary>Video, or anything else with no file behind it.</summary>
    NotSaveable,

    Failed,
}

public sealed record SaveResult(SaveOutcome Outcome, string? Path, string? Detail)
{
    public bool Landed => Outcome is SaveOutcome.Saved or SaveOutcome.AlreadyThere;
}

/// <summary>
/// Pulls one picture down into the intake folder.
///
/// The folder is the same one Saucer drops clippings into, so anything saved here is sorted by
/// Kibble in the same pass as everything else. That is the whole point of ending here rather
/// than somewhere of its own: the rest of the pipeline already exists.
/// </summary>
public sealed class MediaSaver
{
    private readonly HttpClient _http;

    public MediaSaver(HttpClient http) => _http = http;

    /// <summary>
    /// Where the file type comes from.
    ///
    /// Not from the URL, which is the obvious thing to try and is wrong here: Bluesky's CDN
    /// serves an address ending in a bare content hash and hands back WebP. Guessing .jpg from
    /// the look of it would write a WebP called .jpg, and the bot would post a file whose name
    /// disagrees with its bytes.
    /// </summary>
    public static string ExtensionFor(string? contentType) => (contentType ?? "").ToLowerInvariant() switch
    {
        "image/webp" => ".webp",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",

        // Everything the bot posts as a photo is above. Anything else is saved as it arrives
        // and left for a person to look at, rather than given a name that claims otherwise.
        _ => ".bin",
    };

    /// <summary>
    /// A file name from the post rather than from the CDN's content hash.
    ///
    /// The handle and the date are what make a folder of these readable a month later, and the
    /// record key keeps two pictures from the same post apart. Everything except the extension,
    /// because whether this file is already here has to be answerable before fetching it, and
    /// the extension is only known once the response arrives.
    /// </summary>
    public static string BaseNameFor(FeedPost post, int index)
    {
        var key = post.Id[(post.Id.LastIndexOf('/') + 1)..];
        var handle = Safe(post.AuthorHandle);
        var when = post.PostedAt == DateTimeOffset.MinValue
            ? "undated"
            : post.PostedAt.ToLocalTime().ToString("yyyy-MM-dd");

        var suffix = index > 0 ? $"_{index + 1}" : "";
        return $"{handle}_{when}_{Safe(key)}{suffix}";
    }

    public static string NameFor(FeedPost post, int index, string extension) =>
        BaseNameFor(post, index) + extension;

    /// <summary>
    /// Whether this picture is already in the folder, whatever it was saved as.
    ///
    /// Asked before fetching. The name is worked out from the post rather than the response, so
    /// this can be answered without spending a download to discover we already had it.
    /// </summary>
    public static string? AlreadySaved(string intakeFolder, FeedPost post, int index)
    {
        if (!Directory.Exists(intakeFolder))
            return null;

        try
        {
            return Directory.EnumerateFiles(intakeFolder, BaseNameFor(post, index) + ".*")
                .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Unreadable folder. Treat it as not there and let the save report the real problem.
            return null;
        }
    }

    private static string Safe(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(text.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    public async Task<SaveResult> SaveAsync(
        FeedPost post, FeedMedia media, int index, string intakeFolder, CancellationToken token)
    {
        if (media.FullUrl is not { Length: > 0 } url)
            return new SaveResult(SaveOutcome.NotSaveable, null, null);

        try
        {
            // Before the network, not after. Naming from the post rather than from the response
            // is what makes that possible, and it means clicking save twice costs nothing.
            if (AlreadySaved(intakeFolder, post, index) is { } have)
                return new SaveResult(SaveOutcome.AlreadyThere, have, null);

            Directory.CreateDirectory(intakeFolder);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var extension = ExtensionFor(response.Content.Headers.ContentType?.MediaType);
            var target = Path.Combine(intakeFolder, NameFor(post, index, extension));

            if (File.Exists(target))
                return new SaveResult(SaveOutcome.AlreadyThere, target, null);

            // Written beside itself first. A half downloaded file with the final name is one
            // Kibble would happily pick up and queue.
            var partial = target + ".part";
            await using (var file = File.Create(partial))
            {
                await response.Content.CopyToAsync(file, token);
            }

            File.Move(partial, target, overwrite: false);
            return new SaveResult(SaveOutcome.Saved, target, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.Failed, null, ex.Message);
        }
    }
}
