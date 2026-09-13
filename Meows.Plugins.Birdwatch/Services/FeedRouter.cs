using System.Net.Http;

namespace Meows.Plugins.Birdwatch.Services;

/// <summary>
/// One source that is really four. Whatever was pasted is sent to the service that owns that
/// shape: a fediverse address to Mastodon, u/ or r/ or a reddit link to Reddit, a bare address
/// to the feed reader, and everything else, a handle or a bsky.app link, to Bluesky, which was
/// here first and takes the plain case. The handle that is kept is the tidy one, so it routes
/// the same way after a restart.
/// </summary>
public sealed class FeedRouter : IFeedSource
{
    private readonly BlueskyFeed _bluesky;
    private readonly MastodonFeed _mastodon;
    private readonly RedditFeed _reddit;
    private readonly RssFeed _rss;

    public FeedRouter(HttpClient http)
    {
        _bluesky = new BlueskyFeed(http);
        _mastodon = new MastodonFeed(http);
        _reddit = new RedditFeed(http);
        _rss = new RssFeed(http);
    }

    public string ServiceName => "Bluesky, Mastodon, Reddit, feeds";

    /// <summary>Which of the four a pasted address or a kept handle belongs to.</summary>
    public IFeedSource SourceFor(string handle)
    {
        var text = handle.Trim();
        if (RedditFeed.Owns(text))
            return _reddit;
        if (MastodonFeed.Owns(text))
            return _mastodon;
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("bsky.app/", StringComparison.OrdinalIgnoreCase))
            return _rss;
        return _bluesky;
    }

    public string ServiceOf(string handle) => SourceFor(handle).ServiceName;

    public string TidyHandle(string pasted) => SourceFor(pasted).TidyHandle(pasted);

    public Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token) =>
        SourceFor(handle).FetchAsync(handle, cursor, token);
}
