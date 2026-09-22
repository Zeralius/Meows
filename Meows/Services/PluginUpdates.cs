using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meows.Services;

/// <summary>A newer release of an installed plugin: what it is called, and the zip to fetch.</summary>
public sealed record AvailableUpdate(string Version, string ZipName, string ZipUrl, long Size, string ReleaseUrl);

/// <summary>What the shell knows about one installed plugin when it goes asking.</summary>
public sealed record UpdateCandidate(string Id, string Homepage, string? Version);

/// <summary>
/// Reads GitHub's "latest release" for a repository: the tag is the version and the first zip
/// among the assets is the plugin. Separate from the fetching so it can be tested on a saved
/// reply, and so the shape of what GitHub sends is in one place when it changes.
/// </summary>
public static partial class GitHubReleases
{
    [GeneratedRegex(@"^https?://(www\.)?github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+?)(\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryUrl();

    /// <summary>Owner and repository out of a homepage, or null when the homepage is not a GitHub repository.</summary>
    public static (string Owner, string Repo)? Parse(string url)
    {
        var match = RepositoryUrl().Match(url.Trim());
        return match.Success ? (match.Groups["owner"].Value, match.Groups["repo"].Value) : null;
    }

    public static string LatestReleaseApi(string owner, string repo) =>
        $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

    /// <summary>The release in the reply, or null when it has no zip to offer or is not a release at all.</summary>
    public static AvailableUpdate? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || page is null)
                return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                return new AvailableUpdate(tag.Trim(), name, url, size, page);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Asks GitHub, once a day, whether any plugin installed through the Plugins tab has a newer
/// release than the one running, and fetches the zip when asked to. Only plugins the installer
/// put there and that name a GitHub repository are asked about: the built-in ones share the
/// shell's repository and would all answer with the shell's own release.
/// </summary>
public sealed class PluginUpdates
{
    private readonly HttpClient _http;

    public PluginUpdates(string appVersion, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Meows/{appVersion}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// One request per candidate, in turn rather than all at once: GitHub allows sixty an hour
    /// unauthenticated, and a plugin that cannot be asked about is skipped with a line for the
    /// log rather than failing the pass.
    /// </summary>
    public async Task<(IReadOnlyDictionary<string, AvailableUpdate> Updates, IReadOnlyList<string> Said)> CheckAsync(
        IEnumerable<UpdateCandidate> candidates, CancellationToken token)
    {
        var updates = new Dictionary<string, AvailableUpdate>(StringComparer.OrdinalIgnoreCase);
        var said = new List<string>();

        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();

            if (GitHubReleases.Parse(candidate.Homepage) is not { } repo)
                continue;

            try
            {
                var json = await _http.GetStringAsync(GitHubReleases.LatestReleaseApi(repo.Owner, repo.Repo), token);
                var latest = GitHubReleases.Read(json);
                if (latest is null)
                {
                    said.Add($"{candidate.Id}: the latest release at {repo.Owner}/{repo.Repo} has no zip attached.");
                    continue;
                }

                if (IsNewer(latest.Version, candidate.Version))
                    updates[candidate.Id] = latest;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                said.Add($"{candidate.Id}: could not ask {repo.Owner}/{repo.Repo}: {ex.Message}");
            }
        }

        return (updates, said);
    }

    /// <summary>
    /// Newer only when both sides say what they are. A plugin with no version, or one that is
    /// not a number, is never nagged: the shell cannot tell and does not pretend to.
    /// </summary>
    public static bool IsNewer(string available, string? installed)
    {
        var theirs = Plugins.PluginProvenance.Parse(available);
        var ours = Plugins.PluginProvenance.Parse(installed);
        return theirs is not null && ours is not null && theirs > ours;
    }

    /// <summary>The zip, into a temporary file the installer reads and the caller deletes.</summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double?>? progress, CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"meows-update-{Guid.NewGuid():N}-{update.ZipName}");
        using var response = await _http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? (update.Size > 0 ? update.Size : (long?)null);
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var file = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            progress?.Report(total is > 0 ? (double)done / total.Value : null);
        }

        return path;
    }
}
