namespace Meows.Plugins.Birdwatch.Services;

/// <summary>What a post carries that can be saved. Video is listed but not fetchable yet.</summary>
public enum MediaKind
{
    Image,

    /// <summary>
    /// Shown with its thumbnail and marked as not saveable. Bluesky serves video as an HLS
    /// playlist rather than a file, so pulling one down means reassembling segments, which is
    /// ffmpeg's job rather than this plugin's.
    /// </summary>
    Video,
}

/// <summary>One image or video on a post.</summary>
public sealed record FeedMedia
{
    public required MediaKind Kind { get; init; }

    /// <summary>Small, for the grid.</summary>
    public required string ThumbnailUrl { get; init; }

    /// <summary>The one worth saving. Null for anything that cannot be fetched as a file.</summary>
    public string? FullUrl { get; init; }

    /// <summary>The poster's own description, which often makes a better file name than the id.</summary>
    public string Alt { get; init; } = "";

    public bool CanSave => FullUrl is not null;
}

/// <summary>
/// One post, flattened to what this plugin cares about. Deliberately says nothing about which
/// service it came from, so a second source can be added without the grid learning about it.
/// </summary>
public sealed record FeedPost
{
    /// <summary>Stable and unique, and what "already saved" is remembered against.</summary>
    public required string Id { get; init; }

    public required string AuthorHandle { get; init; }

    public string AuthorName { get; init; } = "";

    public string Text { get; init; } = "";

    public DateTimeOffset PostedAt { get; init; }

    /// <summary>Where to open it in a browser, for when the grid is not enough.</summary>
    public string? WebUrl { get; init; }

    /// <summary>
    /// Somebody else's post that this account passed along. Worth knowing, because a feed of
    /// reposts is a feed of other people's material.
    /// </summary>
    public bool IsRepost { get; init; }

    /// <summary>
    /// Content labels the service put on it. Shown rather than acted on: what they mean differs
    /// between services and what to do about them is not this plugin's decision.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public IReadOnlyList<FeedMedia> Media { get; init; } = [];

    public bool HasMedia => Media.Count > 0;

    public bool HasSaveable => Media.Any(m => m.CanSave);
}

/// <summary>A page of posts, and where to carry on from.</summary>
public sealed record FeedPage(IReadOnlyList<FeedPost> Posts, string? Cursor)
{
    public static FeedPage Empty { get; } = new([], null);
}

/// <summary>
/// One place posts come from.
///
/// Bluesky is the only one so far. Mastodon fits the same shape and would be the second. X does
/// not, and the reason is worth writing down rather than rediscovering: reading a timeline needs
/// a paid tier, and working around that means fighting login walls that change without notice,
/// so the feature would break repeatedly and always at the worst moment.
/// </summary>
public interface IFeedSource
{
    /// <summary>The name shown next to a watched account.</summary>
    string ServiceName { get; }

    /// <summary>
    /// A page of an account's own posts. Cursor is null for the first page and comes back in
    /// the result for the next.
    /// </summary>
    Task<FeedPage> FetchAsync(string handle, string? cursor, CancellationToken token);
}
