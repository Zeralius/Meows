using Meows.Plugins;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// What the shell reads about a plugin's origin and its newer releases: the repository out of
/// a homepage, the zip out of GitHub's reply, and when one version counts as newer than another.
/// </summary>
public sealed class PluginUpdatesTests
{
    [Theory]
    [InlineData("https://github.com/Zeralius/Meows", "Zeralius", "Meows")]
    [InlineData("https://github.com/Zeralius/Meows.git", "Zeralius", "Meows")]
    [InlineData("http://www.github.com/someone/weather-watch/", "someone", "weather-watch")]
    public void A_github_homepage_names_its_repository(string url, string owner, string repo)
    {
        Assert.Equal((owner, repo), GitHubReleases.Parse(url));
    }

    [Theory]
    [InlineData("https://gitlab.com/someone/thing")]
    [InlineData("https://github.com/someone")]
    [InlineData("https://github.com/someone/thing/releases")]
    [InlineData("not a url")]
    public void Anything_else_is_not_asked(string url)
    {
        Assert.Null(GitHubReleases.Parse(url));
    }

    private const string Reply = """
        {
          "tag_name": "v1.3.0",
          "html_url": "https://github.com/someone/WeatherWatch/releases/tag/v1.3.0",
          "assets": [
            { "name": "WeatherWatch-1.3.0.nupkg", "browser_download_url": "https://example/n", "size": 10 },
            { "name": "WeatherWatch-1.3.0.zip", "browser_download_url": "https://example/z", "size": 17066 },
            { "name": "other.zip", "browser_download_url": "https://example/o", "size": 5 }
          ]
        }
        """;

    [Fact]
    public void The_first_zip_among_the_assets_is_the_plugin()
    {
        var update = GitHubReleases.Read(Reply);

        Assert.NotNull(update);
        Assert.Equal("v1.3.0", update.Version);
        Assert.Equal("WeatherWatch-1.3.0.zip", update.ZipName);
        Assert.Equal("https://example/z", update.ZipUrl);
        Assert.Equal(17066, update.Size);
        Assert.EndsWith("/tag/v1.3.0", update.ReleaseUrl);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v1.0", "html_url": "x", "assets": [ { "name": "a.7z", "browser_download_url": "u" } ] }""")]
    [InlineData("""{ "tag_name": "v1.0", "html_url": "x" }""")]
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void A_release_with_nothing_to_install_is_not_an_update(string json)
    {
        Assert.Null(GitHubReleases.Read(json));
    }

    [Theory]
    [InlineData("v1.3.0", "1.2.0", true)]
    [InlineData("1.3.0", "v1.3.0", false)]
    [InlineData("v1.2.0", "1.3.0", false)]
    [InlineData("v2.0.0-beta", "1.9.9+abc123", true)]
    [InlineData("v1.3.0", null, false)]
    [InlineData("latest", "1.0.0", false)]
    [InlineData("v1.3.0", "dev", false)]
    public void Newer_only_when_both_sides_say_what_they_are(string available, string? installed, bool newer)
    {
        Assert.Equal(newer, PluginUpdates.IsNewer(available, installed));
    }

    [Fact]
    public void Provenance_is_read_from_what_the_build_stamps_in()
    {
        // This test assembly has a version like every other; the point is that reading it
        // throws nothing and the name-as-company default is not shown as an author.
        var provenance = PluginProvenance.Read(typeof(PluginUpdatesTests).Assembly, typeof(PluginUpdatesTests).Assembly.Location);

        Assert.NotNull(provenance.Version);
        Assert.DoesNotContain('+', provenance.Version);
        Assert.NotEqual("Meows.Tests", provenance.Author);
        Assert.False(provenance.IsInstalled);
    }

    /// <summary>Answers each address from a table, and remembers what was asked.</summary>
    private sealed class Canned(Dictionary<string, byte[]> replies) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            return Task.FromResult(replies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static string Release(string zipUrl, string? digest = null) => $$"""
        {
          "tag_name": "v2.0.0",
          "html_url": "https://github.com/someone/Thing/releases/tag/v2.0.0",
          "assets": [ { "name": "Thing-2.0.0.zip", "browser_download_url": "{{zipUrl}}", "size": 3{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} } ]
        }
        """;

    [Fact]
    public async Task A_zip_from_anywhere_but_the_repository_s_own_releases_is_not_offered()
    {
        var http = new Canned(new()
        {
            [GitHubReleases.LatestReleaseApi("someone", "Thing")] =
                System.Text.Encoding.UTF8.GetBytes(Release("https://evil.example/Thing-2.0.0.zip")),
        });
        var updates = new PluginUpdates("0.0.0-test", new HttpClient(http));

        var (found, said) = await updates.CheckAsync([new UpdateCandidate("thing", "https://github.com/someone/Thing", "1.0.0")], CancellationToken.None);

        Assert.Empty(found);
        Assert.Contains(said, line => line.Contains("not one of its own release assets"));
    }

    [Fact]
    public async Task A_plugin_once_updated_is_asked_about_where_it_was_first_updated_from()
    {
        // The installed build now names another repository; the pin wins.
        var http = new Canned(new()
        {
            [GitHubReleases.LatestReleaseApi("someone", "Thing")] =
                System.Text.Encoding.UTF8.GetBytes(Release("https://github.com/someone/Thing/releases/download/v2.0.0/Thing-2.0.0.zip")),
        });
        var updates = new PluginUpdates("0.0.0-test", new HttpClient(http));

        var (found, _) = await updates.CheckAsync(
            [new UpdateCandidate("thing", "https://github.com/stranger/Elsewhere", "1.0.0", Pinned: "someone/Thing")],
            CancellationToken.None);

        Assert.Single(found);
        Assert.DoesNotContain(http.Asked, a => a.Contains("stranger"));
    }

    [Fact]
    public async Task A_download_that_is_not_what_was_released_is_refused_and_leaves_nothing()
    {
        const string url = "https://github.com/someone/Thing/releases/download/v2.0.0/Thing-2.0.0.zip";
        var http = new Canned(new() { [url] = [1, 2, 3] });
        var updates = new PluginUpdates("0.0.0-test", new HttpClient(http));
        var update = GitHubReleases.Read(Release(url, new string('0', 64)))!;
        var before = Directory.GetFiles(Path.GetTempPath(), "meows-update-*").Length;

        await Assert.ThrowsAsync<InvalidOperationException>(() => updates.DownloadAsync(update, null, CancellationToken.None));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "meows-update-*").Length);
    }

    [Fact]
    public void Nothing_to_read_is_nothing()
    {
        var provenance = PluginProvenance.Read(null, @"C:\nowhere\Thing\Thing.dll");
        Assert.Equal(PluginProvenance.None, provenance);
    }
}
